using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Lifecycle;

/// <summary>
/// Manages activation energy computation, decay cycles, and lifecycle state transitions.
/// </summary>
public sealed class LifecycleEngine
{
    private readonly CognitiveIndex _index;
    private readonly IStorageProvider? _persistence;
    private readonly MemoryDiffusionKernel? _diffusion;
    private readonly ILogger<LifecycleEngine>? _logger;
    private readonly Dictionary<string, DecayConfig> _decayConfigs = new();
    private readonly object _configLock = new();
    private bool _configsLoaded;

    public LifecycleEngine(
        CognitiveIndex index,
        IStorageProvider? persistence = null,
        MemoryDiffusionKernel? diffusion = null,
        ILogger<LifecycleEngine>? logger = null)
    {
        _index = index;
        _persistence = persistence;
        _diffusion = diffusion;
        _logger = logger;
    }

    /// <summary>All distinct tenant ids in the store — used by background maintenance to cover every tenant.</summary>
    public IReadOnlyList<string> GetAllTenants() => _index.GetAllTenants();

    /// <summary>
    /// Set or update a per-namespace decay configuration. Every knob is required so that a
    /// forgotten argument is a compile error, not a silent fall-through to defaults — pass
    /// <c>null</c> to leave a knob unchanged, and pass <c>""</c> as <paramref name="tenantId"/>
    /// to target the legacy partition.
    ///
    /// NORMALIZES THE TENANT FIRST, and both entry points here must, because the map is keyed one
    /// way and read another. <see cref="NamespaceStore.PartitionKey(string, string)"/> validates but
    /// deliberately does not normalize, while <see cref="DecayConfig"/>'s constructor DOES —
    /// and <see cref="EnsureConfigsLoaded"/> re-keys the whole map from that normalized property on
    /// every reload. So a padded tenant wrote one key and every reader used another:
    /// <c>AutoLinkBackgroundService</c> reads through <see cref="CognitiveIndex.GetAllTenants"/>,
    /// which returns store tenants and is therefore normalized, so an operator's
    /// <c>EnableAutoLink: false</c> written under <c>" acme "</c> simply never reached the sweep
    /// that was supposed to obey it — no error, no restart needed, the opt-out just did nothing.
    /// After a restart the padded spelling then missed its own row too, and the next write created a
    /// SECOND row that composed to the same key on the following boot. <c>IPrincipalContext</c> is
    /// an extension point with no normalization of its own, so a padded claim value reaches here.
    /// </summary>
    public DecayConfig SetDecayConfig(string ns, float? decayRate, float? reinforcementWeight,
        float? stmThreshold, float? archiveThreshold,
        bool? useSpectralDecay, float? subdiffusiveExponent, string tenantId)
    {
        string tenant = Tenancy.Normalize(tenantId);
        string pk = NamespaceStore.PartitionKey(tenant, ns);
        lock (_configLock)
        {
            EnsureConfigsLoaded();
            if (!_decayConfigs.TryGetValue(pk, out var config))
            {
                config = new DecayConfig(ns, tenantId: tenant);
                _decayConfigs[pk] = config;
            }

            if (decayRate.HasValue) config.DecayRate = decayRate.Value;
            if (reinforcementWeight.HasValue) config.ReinforcementWeight = reinforcementWeight.Value;
            if (stmThreshold.HasValue) config.StmThreshold = stmThreshold.Value;
            if (archiveThreshold.HasValue) config.ArchiveThreshold = archiveThreshold.Value;
            if (useSpectralDecay.HasValue) config.UseSpectralDecay = useSpectralDecay.Value;
            if (subdiffusiveExponent.HasValue) config.SubdiffusiveExponent = subdiffusiveExponent.Value;

            ScheduleSaveConfigs();
            return config;
        }
    }

    /// <summary>
    /// Get the decay config for a namespace, or null if using defaults. Pass "" as
    /// <paramref name="tenantId"/> for the legacy partition.
    ///
    /// Normalizes first, for the reason spelled out on <see cref="SetDecayConfig"/>: the map is
    /// re-keyed from the normalized <see cref="DecayConfig.TenantId"/> on every reload, so a raw
    /// spelling reads a key that only ever existed until the next load.
    /// </summary>
    public DecayConfig? GetDecayConfig(string ns, string tenantId)
    {
        string pk = NamespaceStore.PartitionKey(Tenancy.Normalize(tenantId), ns);
        lock (_configLock)
        {
            EnsureConfigsLoaded();
            return _decayConfigs.TryGetValue(pk, out var config) ? config : null;
        }
    }

    /// <summary>Get all configured decay configs.</summary>
    public IReadOnlyList<DecayConfig> GetAllDecayConfigs()
    {
        lock (_configLock)
        {
            EnsureConfigsLoaded();
            return _decayConfigs.Values.ToList();
        }
    }

    /// <summary>
    /// Trigger activation energy recomputation and state transitions.
    /// If useStoredConfig is true and a per-namespace config exists, its values are used
    /// instead of the method parameters.
    /// Formula: ActivationEnergy = (accessCount * reinforcementWeight) - (hoursSinceLastAccess * decayRate)
    /// </summary>
    public DecayCycleResult RunDecayCycle(
        string ns,
        string tenantId,
        float decayRate = 0.1f,
        float reinforcementWeight = 1.0f,
        float stmThreshold = 2.0f,
        float archiveThreshold = -5.0f,
        bool useStoredConfig = false)
    {
        var allNamespaces = ns == "*" ? _index.GetNamespaces(tenantId) : new[] { ns };

        var stmToLtmIds = new List<string>();
        var ltmToArchivedIds = new List<string>();
        var spectralFallbackNamespaces = new List<string>();
        var failedNamespaces = new List<string>();
        int processedCount = 0;

        foreach (var currentNs in allNamespaces)
        {
            try
            {
                // Resolve effective parameters: stored config if requested, else method params
                float effectiveDecayRate = decayRate;
                float effectiveReinforcement = reinforcementWeight;
                float effectiveStmThreshold = stmThreshold;
                float effectiveArchiveThreshold = archiveThreshold;
                float stmMultiplier = 3.0f;
                float ltmMultiplier = 1.0f;
                float archivedMultiplier = 0.1f;
                bool useSpectral = false;
                float subdiffusiveExponent = 1.0f;

                if (useStoredConfig)
                {
                    var config = GetDecayConfig(currentNs, tenantId: tenantId);
                    if (config is not null)
                    {
                        effectiveDecayRate = config.DecayRate;
                        effectiveReinforcement = config.ReinforcementWeight;
                        effectiveStmThreshold = config.StmThreshold;
                        effectiveArchiveThreshold = config.ArchiveThreshold;
                        stmMultiplier = config.StmDecayMultiplier;
                        ltmMultiplier = config.LtmDecayMultiplier;
                        archivedMultiplier = config.ArchivedDecayMultiplier;
                        useSpectral = config.UseSpectralDecay && _diffusion is not null;
                        subdiffusiveExponent = config.SubdiffusiveExponent;
                    }
                    else
                    {
                        // No stored config — apply defaults. Spectral diffusion is on
                        // by default whenever a kernel is available; the kernel itself
                        // self-bypasses for namespaces that don't qualify, so this is
                        // safe even on tiny / sparsely-linked namespaces.
                        useSpectral = _diffusion is not null;
                    }
                }

                // GetAllInNamespace returns a snapshot list — safe to iterate
                var entries = _index.GetAllInNamespace(currentNs, tenantId: tenantId);
                var nonSummary = new List<CognitiveEntry>(entries.Count);
                foreach (var e in entries)
                    if (!e.IsSummaryNode) nonSummary.Add(e);

                // Pass 1: compute per-entry "decay debt" — the amount the entry would
                // lose pointwise. We diffuse this debt (not the activation itself, which
                // is the input/source field, not the dissipative field) when spectral
                // mode is on.
                var debt = new Dictionary<string, float>(nonSummary.Count);
                var now = DateTimeOffset.UtcNow;
                foreach (var entry in nonSummary)
                {
                    var hoursSinceAccess = (float)(now - entry.LastAccessedAt).TotalHours;
                    float stateMultiplier = entry.LifecycleState switch
                    {
                        "stm" => stmMultiplier,
                        "ltm" => ltmMultiplier,
                        "archived" => archivedMultiplier,
                        _ => 1.0f
                    };
                    debt[entry.Id] = hoursSinceAccess * effectiveDecayRate * stateMultiplier;
                }

                // Optional pass 1.5: diffuse debt through the graph heat kernel. The
                // filter exp(-lambda^alpha) with t=1 means "one unit of diffusion per
                // decay cycle"; the magnitude of debt is already scaled by decayRate
                // and hours-since-access on the way in, so the spectral step here
                // controls only the *shape* (which entries share their forgetting
                // pressure with their neighbors). Falls back silently to pointwise
                // debt if the kernel declines (namespace too small, no qualifying edges),
                // and falls back *loudly* (recorded in SpectralFallbackNamespaces) if
                // the kernel throws — the failing namespace still gets full
                // non-spectral pointwise decay below.
                IReadOnlyDictionary<string, float> appliedDebt = debt;
                if (useSpectral)
                {
                    try
                    {
                        appliedDebt = _diffusion!.ApplySpectralFilter(currentNs, debt,
                            lambda => MathF.Exp(-MathF.Pow(lambda, subdiffusiveExponent)), tenantId: tenantId);
                    }
                    catch (Exception ex)
                    {
                        spectralFallbackNamespaces.Add(currentNs);
                        _logger?.LogWarning(ex,
                            "Spectral decay filter failed for ns={Namespace}; applying non-spectral pointwise decay.",
                            currentNs);
                    }
                }

                // Pass 2: apply debt and resolve state transitions.
                foreach (var entry in nonSummary)
                {
                    processedCount++;
                    float entryDebt = appliedDebt.TryGetValue(entry.Id, out var d) ? d : 0f;
                    float newActivationEnergy = (entry.AccessCount * effectiveReinforcement) - entryDebt;

                    string? newState = null;
                    switch (entry.LifecycleState)
                    {
                        case "stm" when newActivationEnergy < effectiveStmThreshold:
                            newState = "ltm";
                            stmToLtmIds.Add(entry.Id);
                            break;
                        case "ltm" when newActivationEnergy < effectiveArchiveThreshold:
                            newState = "archived";
                            ltmToArchivedIds.Add(entry.Id);
                            break;
                    }

                    _index.SetActivationEnergyAndState(entry.Id, newActivationEnergy, newState, currentNs, tenantId: tenantId);
                }
            }
            catch (Exception ex)
            {
                // Per-namespace fault isolation: one failing namespace must not
                // abort the decay cycle for every other namespace.
                failedNamespaces.Add(currentNs);
                _logger?.LogWarning(ex, "Decay cycle failed for ns={Namespace}; skipping and continuing.", currentNs);
            }
        }

        return new DecayCycleResult(
            processedCount,
            stmToLtmIds.Count,
            ltmToArchivedIds.Count,
            stmToLtmIds,
            ltmToArchivedIds,
            allNamespaces.Count,
            spectralFallbackNamespaces,
            failedNamespaces);
    }

    /// <summary>
    /// Sleep-consolidation pass: smooth the activation field through a long-time
    /// graph heat kernel and drive lifecycle transitions based on the smoothed
    /// (cluster-aware) values rather than the raw per-entry energy.
    ///
    /// Semantics. The smoothed activation <c>A_smooth = exp(-tL) A</c> at large t
    /// converges to the mean activation within each connected component of the
    /// memory graph. So <c>A_smooth[i]</c> reads as "how much support does memory
    /// i's cluster collectively give it." Transitions:
    ///
    /// - STM -&gt; LTM when <c>A_smooth[i] >= ConsolidationPromotionThreshold</c>:
    ///   the memory's cluster is collectively warm enough to anchor it as a
    ///   stable long-term memory, even if its own access count is modest.
    /// - LTM -&gt; archived when <c>A_smooth[i] &lt; ConsolidationArchiveThreshold</c>:
    ///   the memory's cluster has cooled below the archive floor; even if this
    ///   particular entry was recently accessed, its surrounding context is gone.
    ///
    /// Complements <see cref="RunDecayCycle"/>, which drives transitions by
    /// per-entry decay debt. Topology-driven transitions here can rescue memories
    /// whose own activation is low but whose cluster is hot, and conversely
    /// archive memories whose cluster has dispersed.
    ///
    /// Skips namespaces that do not qualify for the diffusion kernel (too small
    /// or too sparsely linked) — without a graph to diffuse on, this pass has
    /// nothing to add over the regular decay cycle.
    /// </summary>
    /// <param name="ns">Namespace to consolidate, or "*" for every non-system namespace.</param>
    /// <param name="tenantId">Tenant partition to consolidate; pass "" for the legacy partition.</param>
    public ConsolidationResult RunConsolidationPass(string ns, string tenantId)
    {
        var stmToLtmIds = new List<string>();
        var ltmToArchivedIds = new List<string>();
        var failedNamespaces = new List<string>();
        int processedNamespaces = 0;
        int skippedNamespaces = 0;
        int processedEntries = 0;

        if (_diffusion is null)
        {
            // Without a kernel injected, consolidation has no graph diffusion to
            // run; report a single skip so callers can distinguish "no kernel" from
            // "no qualifying namespaces."
            return new ConsolidationResult(0, 1, 0, 0, 0, stmToLtmIds, ltmToArchivedIds, Array.Empty<string>());
        }

        var allNamespaces = ns == "*" ? _index.GetNamespaces(tenantId) : new[] { ns };

        foreach (var currentNs in allNamespaces)
        {
            if (currentNs.StartsWith('_'))
            {
                // Skip system namespaces (sharing registry, etc.).
                skippedNamespaces++;
                continue;
            }

            try
            {
                var config = GetDecayConfig(currentNs, tenantId: tenantId) ?? new DecayConfig(currentNs, tenantId: tenantId);
                if (!config.EnableConsolidation)
                {
                    skippedNamespaces++;
                    continue;
                }

                var entries = _index.GetAllInNamespace(currentNs, tenantId: tenantId);
                var nonSummary = new List<CognitiveEntry>(entries.Count);
                foreach (var e in entries)
                    if (!e.IsSummaryNode) nonSummary.Add(e);
                if (nonSummary.Count == 0)
                {
                    skippedNamespaces++;
                    continue;
                }

                // Snapshot current activation field. The kernel handles namespaces
                // that don't qualify by returning the signal unchanged; we detect
                // that case explicitly to skip rather than make essentially-no-op
                // threshold decisions on raw activation (which would duplicate the
                // existing decay cycle's role).
                var basis = _diffusion.GetBasis(currentNs, tenantId: tenantId);
                if (basis is null)
                {
                    skippedNamespaces++;
                    continue;
                }

                var activation = new Dictionary<string, float>(nonSummary.Count);
                foreach (var entry in nonSummary)
                    activation[entry.Id] = entry.ActivationEnergy;

                // Long-time heat kernel diffusion: exp(-t * lambda).
                float t = config.ConsolidationDiffusionTime;
                var smoothed = _diffusion.ApplySpectralFilter(currentNs, activation,
                    lambda => MathF.Exp(-lambda * t), tenantId: tenantId);

                // Apply topology-driven transitions.
                foreach (var entry in nonSummary)
                {
                    if (!smoothed.TryGetValue(entry.Id, out var smoothAE)) continue;
                    processedEntries++;

                    switch (entry.LifecycleState)
                    {
                        case "stm" when smoothAE >= config.ConsolidationPromotionThreshold:
                            if (_index.SetLifecycleState(entry.Id, "ltm", currentNs, tenantId: tenantId))
                                stmToLtmIds.Add(entry.Id);
                            break;
                        case "ltm" when smoothAE < config.ConsolidationArchiveThreshold:
                            if (_index.SetLifecycleState(entry.Id, "archived", currentNs, tenantId: tenantId))
                                ltmToArchivedIds.Add(entry.Id);
                            break;
                    }
                }

                processedNamespaces++;
            }
            catch (Exception ex)
            {
                // Per-namespace fault isolation: skip the failing namespace whole
                // and keep consolidating the rest. There is deliberately NO
                // non-spectral fallback here — consolidation's entire mechanism IS
                // the spectral smoothing (A_smooth = exp(-tL) A); thresholding on
                // raw activation instead would just duplicate the decay cycle's
                // role (see the skip rationale above the GetBasis call).
                failedNamespaces.Add(currentNs);
                _logger?.LogWarning(ex, "Consolidation failed for ns={Namespace}; skipping and continuing.", currentNs);
            }
        }

        return new ConsolidationResult(
            processedNamespaces,
            skippedNamespaces,
            processedEntries,
            stmToLtmIds.Count,
            ltmToArchivedIds.Count,
            stmToLtmIds,
            ltmToArchivedIds,
            failedNamespaces);
    }

    /// <summary>Promote (or demote) an entry to a specific lifecycle state.</summary>
    /// <param name="id">Entry ID.</param>
    /// <param name="targetState">Target lifecycle state: "stm", "ltm", or "archived".</param>
    /// <param name="ns">Namespace for tenant-scoped resolution; pass "" to use the legacy global bare-id locator.</param>
    /// <param name="tenantId">Tenant partition; pass "" for the legacy partition. Only used when <paramref name="ns"/> is non-empty.</param>
    public string PromoteMemory(string id, string targetState, string ns, string tenantId)
    {
        if (targetState is not ("stm" or "ltm" or "archived"))
            return $"Error: Invalid target state '{targetState}'. Use 'stm', 'ltm', or 'archived'.";

        // When a namespace is supplied the lookup is tenant-scoped; an explicit ns == "" selects
        // the legacy global bare-id locator. Neither ns nor tenantId defaults — a forgotten
        // argument must be a compile error, not a silent fall-through to cross-tenant scope.
        bool scoped = ns.Length != 0;
        var entry = scoped ? _index.Get(id, ns, tenantId: tenantId) : _index.Get(id);
        if (entry is null)
            return $"Error: Entry '{id}' not found.";

        var previousState = entry.LifecycleState;
        bool updated = scoped
            ? _index.SetLifecycleState(id, targetState, ns, tenantId: tenantId)
            : _index.SetLifecycleState(id, targetState);
        if (!updated)
            return $"Error: Failed to update state for '{id}'.";

        return $"Entry '{id}' transitioned: {previousState} -> {targetState}.";
    }

    /// <summary>
    /// Apply agent feedback to a memory entry. Positive feedback boosts activation energy
    /// and records an access; negative feedback reduces activation energy. State transitions
    /// are applied if the new energy crosses thresholds.
    /// </summary>
    /// <param name="id">Entry ID.</param>
    /// <param name="delta">Feedback delta: positive values reinforce, negative values suppress. Clamped to [-10, 10].</param>
    /// <param name="ns">Namespace for config lookup and tenant-scoped resolution; pass null to use the legacy global bare-id locator.</param>
    /// <param name="tenantId">Tenant partition; pass "" for the legacy partition. Only used when <paramref name="ns"/> is supplied.</param>
    public FeedbackResult? ApplyFeedback(string id, float delta, string? ns, string tenantId)
    {
        delta = Math.Clamp(delta, -10f, 10f);

        // When a namespace is supplied the entry is resolved tenant-scoped; an explicit null ns
        // selects the legacy bare-id path. Neither ns nor tenantId defaults — a forgotten
        // argument must be a compile error, not a silent fall-through to cross-tenant scope.
        var entry = ns is not null ? _index.Get(id, ns, tenantId: tenantId) : _index.Get(id);
        if (entry is null)
            return null;

        float previousEnergy = entry.ActivationEnergy;
        string previousState = entry.LifecycleState;
        float newEnergy = previousEnergy + delta;

        // Positive feedback also records an access (boosts decay resistance)
        if (delta > 0)
        {
            if (ns is not null) _index.RecordAccess(id, ns, tenantId: tenantId);
            else _index.RecordAccess(id);
        }

        // Resolve thresholds from stored config or defaults
        float stmThreshold = 2.0f;
        float archiveThreshold = -5.0f;
        if (ns is not null)
        {
            var config = GetDecayConfig(ns, tenantId: tenantId);
            if (config is not null)
            {
                stmThreshold = config.StmThreshold;
                archiveThreshold = config.ArchiveThreshold;
            }
        }

        // Determine state transition
        string? newState = null;
        switch (previousState)
        {
            case "stm" when newEnergy < stmThreshold && delta < 0:
                newState = "ltm";
                break;
            case "ltm" when newEnergy < archiveThreshold:
                newState = "archived";
                break;
            case "archived" when delta > 0 && newEnergy >= stmThreshold:
                newState = "stm";
                break;
            case "archived" when delta > 0:
                newState = "ltm";
                break;
        }

        if (ns is not null) _index.SetActivationEnergyAndState(id, newEnergy, newState, ns, tenantId: tenantId);
        else _index.SetActivationEnergyAndState(id, newEnergy, newState);

        string finalState = newState ?? previousState;
        return new FeedbackResult(id, previousEnergy, newEnergy, previousState, finalState, newState is not null);
    }

    /// <summary>Deep recall: search all states and auto-resurrect high-scoring archived entries.</summary>
    /// <remarks>
    /// <c>resurrect</c> controls whether this pass may mutate. Resurrection promotes an archived
    /// entry back to "stm" and records an access, so a read-shaped call performs a write on the
    /// caller's behalf; a caller that holds only read permission on the namespace must pass false
    /// and gets the same rows without the side effect. The parameter is trailing and defaults to
    /// true so every existing caller — including the benchmark runners that drive the UseLifecycle
    /// ablation and whose IR baselines would otherwise shift — keeps its current behaviour with no
    /// argument change. <c>tenantId</c> is required and sits directly after <c>ns</c> (tenant-qualified
    /// identity first, tuning knobs after); it does not disturb the trailing <c>resurrect</c> default.
    /// </remarks>
    public IReadOnlyList<CognitiveSearchResult> DeepRecall(
        float[] vector, string ns, string tenantId, int k = 10, float minScore = 0.3f,
        float resurrectionThreshold = 0.7f,
        string? queryText = null, bool hybrid = false, bool rerank = false,
        bool resurrect = true)
    {
        var results = _index.SearchAllStates(vector, ns, tenantId: tenantId, k: k, minScore: minScore,
            queryText: queryText, hybrid: hybrid, rerank: rerank);

        // Read-only path. Only the write is withheld, never the rows: the archived entries are
        // returned exactly as found, so an unprivileged caller's result set is indistinguishable
        // in content and count from a privileged one and reveals nothing about the denial.
        if (!resurrect) return results;

        // Auto-resurrect high-scoring archived entries and return updated results
        var updatedResults = new List<CognitiveSearchResult>(results.Count);
        foreach (var result in results)
        {
            if (result.LifecycleState == "archived" && result.Score >= resurrectionThreshold)
            {
                _index.SetLifecycleState(result.Id, "stm", ns, tenantId: tenantId);
                _index.RecordAccess(result.Id, ns, tenantId: tenantId);
                // Return with updated lifecycle state
                updatedResults.Add(result with { LifecycleState = "stm" });
            }
            else
            {
                updatedResults.Add(result);
            }
        }

        return updatedResults;
    }

    private void EnsureConfigsLoaded()
    {
        if (_configsLoaded || _persistence is null) return;
        var configs = _persistence.LoadDecayConfigs();
        foreach (var config in configs.Values)
            _decayConfigs[NamespaceStore.PartitionKey(config.TenantId, config.Ns)] = config;
        _configsLoaded = true;
    }

    private void ScheduleSaveConfigs()
    {
        if (_persistence is null) return;
        var snapshot = _decayConfigs.ToDictionary(kv => kv.Key, kv => kv.Value);
        _persistence.ScheduleSaveDecayConfigs(() => snapshot);
    }
}
