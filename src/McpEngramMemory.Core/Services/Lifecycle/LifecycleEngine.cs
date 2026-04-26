using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Core.Services.Lifecycle;

/// <summary>
/// Manages activation energy computation, decay cycles, and lifecycle state transitions.
/// </summary>
public sealed class LifecycleEngine
{
    private readonly CognitiveIndex _index;
    private readonly IStorageProvider? _persistence;
    private readonly MemoryDiffusionKernel? _diffusion;
    private readonly Dictionary<string, DecayConfig> _decayConfigs = new();
    private readonly object _configLock = new();
    private bool _configsLoaded;

    public LifecycleEngine(
        CognitiveIndex index,
        IStorageProvider? persistence = null,
        MemoryDiffusionKernel? diffusion = null)
    {
        _index = index;
        _persistence = persistence;
        _diffusion = diffusion;
    }

    /// <summary>Set or update a per-namespace decay configuration.</summary>
    public DecayConfig SetDecayConfig(string ns, float? decayRate = null, float? reinforcementWeight = null,
        float? stmThreshold = null, float? archiveThreshold = null,
        bool? useSpectralDecay = null, float? subdiffusiveExponent = null)
    {
        lock (_configLock)
        {
            EnsureConfigsLoaded();
            if (!_decayConfigs.TryGetValue(ns, out var config))
            {
                config = new DecayConfig(ns);
                _decayConfigs[ns] = config;
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

    /// <summary>Get the decay config for a namespace, or null if using defaults.</summary>
    public DecayConfig? GetDecayConfig(string ns)
    {
        lock (_configLock)
        {
            EnsureConfigsLoaded();
            return _decayConfigs.TryGetValue(ns, out var config) ? config : null;
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
        float decayRate = 0.1f,
        float reinforcementWeight = 1.0f,
        float stmThreshold = 2.0f,
        float archiveThreshold = -5.0f,
        bool useStoredConfig = false)
    {
        var allNamespaces = ns == "*" ? _index.GetNamespaces() : new[] { ns };

        var stmToLtmIds = new List<string>();
        var ltmToArchivedIds = new List<string>();
        int processedCount = 0;

        foreach (var currentNs in allNamespaces)
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
                var config = GetDecayConfig(currentNs);
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
            var entries = _index.GetAllInNamespace(currentNs);
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
            // debt if the kernel declines (namespace too small, no qualifying edges).
            IReadOnlyDictionary<string, float> appliedDebt = debt;
            if (useSpectral)
            {
                appliedDebt = _diffusion!.ApplySpectralFilter(currentNs, debt,
                    lambda => MathF.Exp(-MathF.Pow(lambda, subdiffusiveExponent)));
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

                _index.SetActivationEnergyAndState(entry.Id, newActivationEnergy, newState);
            }
        }

        return new DecayCycleResult(
            processedCount,
            stmToLtmIds.Count,
            ltmToArchivedIds.Count,
            stmToLtmIds,
            ltmToArchivedIds);
    }

    /// <summary>Promote (or demote) an entry to a specific lifecycle state.</summary>
    public string PromoteMemory(string id, string targetState)
    {
        if (targetState is not ("stm" or "ltm" or "archived"))
            return $"Error: Invalid target state '{targetState}'. Use 'stm', 'ltm', or 'archived'.";

        var entry = _index.Get(id);
        if (entry is null)
            return $"Error: Entry '{id}' not found.";

        var previousState = entry.LifecycleState;
        if (!_index.SetLifecycleState(id, targetState))
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
    /// <param name="ns">Optional namespace hint for config lookup.</param>
    public FeedbackResult? ApplyFeedback(string id, float delta, string? ns = null)
    {
        delta = Math.Clamp(delta, -10f, 10f);

        var entry = _index.Get(id);
        if (entry is null)
            return null;

        float previousEnergy = entry.ActivationEnergy;
        string previousState = entry.LifecycleState;
        float newEnergy = previousEnergy + delta;

        // Positive feedback also records an access (boosts decay resistance)
        if (delta > 0)
            _index.RecordAccess(id);

        // Resolve thresholds from stored config or defaults
        float stmThreshold = 2.0f;
        float archiveThreshold = -5.0f;
        if (ns is not null)
        {
            var config = GetDecayConfig(ns);
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

        _index.SetActivationEnergyAndState(id, newEnergy, newState);

        string finalState = newState ?? previousState;
        return new FeedbackResult(id, previousEnergy, newEnergy, previousState, finalState, newState is not null);
    }

    /// <summary>Deep recall: search all states and auto-resurrect high-scoring archived entries.</summary>
    public IReadOnlyList<CognitiveSearchResult> DeepRecall(
        float[] vector, string ns, int k = 10, float minScore = 0.3f,
        float resurrectionThreshold = 0.7f,
        string? queryText = null, bool hybrid = false, bool rerank = false)
    {
        var results = _index.SearchAllStates(vector, ns, k, minScore,
            queryText: queryText, hybrid: hybrid, rerank: rerank);

        // Auto-resurrect high-scoring archived entries and return updated results
        var updatedResults = new List<CognitiveSearchResult>(results.Count);
        foreach (var result in results)
        {
            if (result.LifecycleState == "archived" && result.Score >= resurrectionThreshold)
            {
                _index.SetLifecycleState(result.Id, "stm");
                _index.RecordAccess(result.Id, ns);
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
        foreach (var (ns, config) in configs)
            _decayConfigs[ns] = config;
        _configsLoaded = true;
    }

    private void ScheduleSaveConfigs()
    {
        if (_persistence is null) return;
        var snapshot = _decayConfigs.ToDictionary(kv => kv.Key, kv => kv.Value);
        _persistence.ScheduleSaveDecayConfigs(() => snapshot);
    }
}
