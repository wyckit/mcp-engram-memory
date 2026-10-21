using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Governance;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Sharing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// Tier-1 composite MCP tools: high-level operations that orchestrate multiple
/// subsystems internally. Designed for models that don't need (or can't handle)
/// the full 49-tool surface.
///
/// remember — intelligent store with auto-dedup and auto-linking
/// recall   — intelligent search with auto-routing and fallback
/// reflect  — store a lesson/retrospective with auto-linking
/// </summary>
[McpServerToolType]
public sealed class CompositeTools
{
    private readonly CognitiveIndex _index;
    private readonly IEmbeddingService _embedding;
    private readonly KnowledgeGraph _graph;
    private readonly ExpertDispatcher _dispatcher;
    private readonly MetricsCollector _metrics;
    private readonly SpectralRetrievalReranker _spectral;
    private readonly NamespaceAccess _access;
    private readonly IGovernedKnowledgeStore? _knowledgeStore;

    [ActivatorUtilitiesConstructor]
    public CompositeTools(
        CognitiveIndex index, IEmbeddingService embedding, KnowledgeGraph graph,
        LifecycleEngine lifecycle, ExpertDispatcher dispatcher, MetricsCollector metrics,
        SpectralRetrievalReranker spectral,
        NamespaceAccess access, IGovernedKnowledgeStore? knowledgeStore = null)
    {
        _index = index;
        _embedding = embedding;
        _graph = graph;
        // Keep the lifecycle constructor parameter for compatibility; recall defers
        // lifecycle updates until after final selection instead of using DeepRecall.
        _dispatcher = dispatcher;
        _metrics = metrics;
        _spectral = spectral;
        _access = access;
        _knowledgeStore = knowledgeStore;
    }

    /// <summary>Preserve the existing tenant-aware in-process constructor.</summary>
    public CompositeTools(
        CognitiveIndex index, IEmbeddingService embedding, KnowledgeGraph graph,
        LifecycleEngine lifecycle, ExpertDispatcher dispatcher, MetricsCollector metrics,
        SpectralRetrievalReranker spectral, NamespaceAccess access)
        : this(index, embedding, graph, lifecycle, dispatcher, metrics, spectral, access, null) { }

    /// <summary>Compatibility constructor for in-process hosts compiled against the v1 API.</summary>
    public CompositeTools(
        CognitiveIndex index, IEmbeddingService embedding, KnowledgeGraph graph,
        LifecycleEngine lifecycle, ExpertDispatcher dispatcher, MetricsCollector metrics,
        SpectralRetrievalReranker spectral,
        NamespaceRegistry registry, AgentIdentity agent)
        : this(index, embedding, graph, lifecycle, dispatcher, metrics, spectral,
            new NamespaceAccess(registry, agent)) { }

    /// <summary>
    /// Resolve a link target named by the caller, then authorize it at the verb we are about
    /// to perform on it. An id reaching us in <c>relatedIds</c> carries no namespace, so it has
    /// to be resolved before it can be checked, and the resolution itself only considers
    /// namespaces this principal may write — an invisible namespace must neither win the
    /// resolution nor make it ambiguous, or "your link silently did nothing" becomes a signal
    /// that a private twin of that id exists. <paramref name="preferredNs"/> is the namespace
    /// the caller is already authorized for, so a same-id entry elsewhere in the tenant can
    /// neither hijack the link nor blank it. Write access, not read, matches
    /// <c>GraphTools.LinkMemories</c>, which requires it on both endpoints.
    /// </summary>
    private CognitiveEntry? ResolveLinkTarget(string targetId, string preferredNs)
    {
        // Resolution authorizes the ENTRY. The edge, though, is written onto the bare-id graph
        // node, which a same-id twin in a namespace this caller cannot see shares byte for byte —
        // so authorizing through the twin we can resolve would still hang topology off the one we
        // cannot. Topology therefore takes the ACL-blind tenant-wide test as well, the same
        // posture GraphTools.LinkMemories and TopologyCascade already take. Both failures return
        // null, so "no such id" and "that id has a twin somewhere" stay indistinguishable.
        if (!BareIdTopology.IsTopologySafe(_index, targetId, _access.TenantId))
            return null;

        return EntryAccessResolver.Resolve(_index, targetId, _access.TenantId, _access.CanWrite, preferredNs);
    }

    [McpServerTool(Name = "remember", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Save a new memory with automatic duplicate detection and graph linking — the default way to store anything. Don't use `store_memory` directly unless you need to supply a raw embedding vector or skip duplicate checking.")]
    public object Remember(
        [Description("Unique identifier for this memory (kebab-case recommended).")] string id,
        [Description("Namespace (e.g. project directory name, 'work', 'synthesis').")] string ns,
        [Description("The memory text to store and embed.")] string text,
        [Description("Category: 'decision', 'pattern', 'bug-fix', 'architecture', 'preference', 'lesson', 'reference', 'retrospective'.")] string? category = null,
        [Description("Optional metadata as a JSON object. Values may be any JSON type — strings are stored verbatim, numbers/booleans become their literal text, and arrays/objects are serialized to compact JSON so the dictionary storage stays flat.")] Dictionary<string, JsonElement>? metadata = null,
        [Description("Lifecycle state: 'stm' (default) or 'ltm' for stable knowledge.")] string? lifecycleState = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return "Error: id must not be empty.";
        if (string.IsNullOrWhiteSpace(ns)) return "Error: ns must not be empty.";
        if (string.IsNullOrWhiteSpace(text)) return "Error: text must not be empty.";
        if (!_access.CanWrite(ns)) return NamespaceAccess.WriteDenied(ns);

        using var timer = _metrics.StartTimer("remember");
        var state = lifecycleState ?? "stm";
        var actions = new List<string>();

        try
        {
        // 1. Embed with contextual prefix
        var prefix = BenchmarkRunner.BuildContextualPrefix(ns, category);
        var vector = _embedding.Embed(prefix + text);

        // 2. Check for near-duplicates BEFORE storing (search by vector similarity)
        var existing = _index.Search(vector, ns, k: 3, minScore: 0.90f,
            tenantId: _access.TenantId);
        var highDup = existing.FirstOrDefault(r => r.Score >= 0.95f && r.Id != id && !r.IsSummaryNode);
        if (highDup is not null)
        {
            return new RememberResult("duplicate_blocked", id, ns,
                $"Very similar memory already exists: '{highDup.Id}' (similarity: {highDup.Score:F3}). " +
                "Consider updating the existing memory instead.",
                actions,
                new[] { new DuplicateWarning(highDup.Id, highDup.Text, highDup.Score) });
        }

        // 3. Store the entry
        var entry = new CognitiveEntry(id, vector, ns, text, category,
            MetadataNormalizer.Normalize(metadata), state, tenantId: _access.TenantId);
        _index.Upsert(entry);
        _access.ClaimOnWrite(ns);
        actions.Add("stored");

        // 4. Find related memories and auto-link (use pre-store search results + fresh search)
        var related = existing.Count > 0 ? existing : _index.Search(
            vector, ns, k: 5, minScore: 0.65f, tenantId: _access.TenantId);
        var links = new List<string>();
        // One sweep for the whole loop: the per-id overload re-lists the tenant's namespaces and
        // that listing reloads the store, so guarding a result set id-by-id would cost one full
        // reload per candidate. Both endpoints are tested — an edge lands on two bare nodes, and
        // either of them may be shared with a twin this caller cannot see.
        //
        // The entry above is already durably stored, so a failed provider listing here must
        // degrade to "no auto-linking" rather than erroring a write that happened — an error
        // reply for a committed upsert makes the caller retry and duplicate the entry.
        TopologyGuard.Sweep? topology = null;
        try { topology = BareIdTopology.ForSweep(_index, _access.TenantId); }
        catch (NamespaceEnumerationException) { actions.Add("auto-linking skipped (namespace listing unavailable)"); }
        if (topology is not null)
        {
        bool selfLinkable = topology.IsTopologySafe(id);
        foreach (var result in related)
        {
            if (result.Id == id) continue;
            if (result.IsSummaryNode) continue;
            if (result.Score < 0.65f) continue;
            if (!selfLinkable || !topology.IsTopologySafe(result.Id)) continue;

            var relation = result.Score >= 0.85f ? "similar_to" : "cross_reference";
            _graph.AddEdge(new GraphEdge(id, result.Id, relation, tenantId: _access.TenantId));
            links.Add($"{result.Id} ({relation}, {result.Score:F3})");
        }
        }

        if (links.Count > 0)
            actions.Add($"linked to {links.Count} related memor{(links.Count == 1 ? "y" : "ies")}");

        // 5. Duplicate warnings (entries between 0.90 and 0.95 similarity)
        var warnings = existing
            .Where(r => r.Score >= 0.90f && r.Score < 0.95f && r.Id != id && !r.IsSummaryNode)
            .Select(r => new DuplicateWarning(r.Id, r.Text, r.Score))
            .ToArray();

        if (warnings.Length > 0)
            actions.Add($"{warnings.Length} near-duplicate warning(s)");

        return new RememberResult("stored", id, ns,
            $"Remembered '{id}' in '{ns}'. Actions: {string.Join(", ", actions)}.",
            actions, warnings.Length > 0 ? warnings : null);
        }
        catch (ArgumentException ex) { return $"Error: {ex.Message}"; }
        catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>Compatibility API for synchronous in-process hosts. Prefer RecallAsync for concurrent serving.</summary>
    public object Recall(string query, string? ns = null, int k = 5, float minScore = 0.3f,
        bool hybrid = true, bool rerank = true, bool summaryFirst = false, bool expandGraph = true,
        string spectralMode = "auto") =>
        RecallAsync(query, ns, k, minScore, hybrid, rerank, summaryFirst, expandGraph, spectralMode).GetAwaiter().GetResult();

    [McpServerTool(Name = "recall", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Find memories in one namespace (or auto-route across all) — the default search for any retrieval task. Don't use it to search across multiple specific namespaces simultaneously; use `cross_search` instead.")]
    public async Task<object> RecallAsync(
        [Description("What to search for.")] string query,
        [Description("Namespace to search (omit to auto-route via expert dispatcher).")] string? ns = null,
        [Description("Maximum results (default: 5).")] int k = 5,
        [Description("Minimum similarity score (default: 0.3).")] float minScore = 0.3f,
        [Description("Use hybrid BM25+vector search (default: true).")] bool hybrid = true,
        [Description("Apply token-level reranking (default: true).")] bool rerank = true,
        [Description("Prioritize cluster summaries in results (default: false).")] bool summaryFirst = false,
        [Description("Include graph-connected neighbors in results (default: true).")] bool expandGraph = true,
        [Description("Graph-aware re-ranking after standard retrieval and graph expansion. 'auto' (default): short conceptual queries get broad, longer specific queries get specific; uses a local heuristic, no extra LLM/embedding calls. 'broad': boosts a dominant cluster. 'specific': preserves relevance and adds a bounded specificity bonus (at most 25%) to retrieved candidates. 'none': skip graph re-ranking. Applied within each searched namespace on every route; specific mode passes through without a qualifying diffusion basis.")] string spectralMode = "auto")
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query must not be empty.";
        if (k <= 0) return "Error: k must be positive.";

        using var timer = _metrics.StartTimer("recall");
        float[] vector;
        try { vector = _embedding.Embed(query); }
        catch (ArgumentException ex) { return $"Error: {ex.Message}"; }
        catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
        try
        {
        var queryEvidence = rerank && !summaryFirst && RecallFreshness.PrefersCurrent(query) ? RecallQueryEvidence.Parse(query) : null;
        if (hybrid && rerank && !summaryFirst && RecallFreshness.PrefersCurrent(query) &&
            (spectralMode?.Trim().ToLowerInvariant() is "auto" or "broad"))
            queryEvidence = RecallQueryEvidence.ParseCompound(query) ?? queryEvidence;
        var broadIntent = queryEvidence is null && hybrid && rerank && !summaryFirst && RecallFreshness.PrefersCurrent(query) &&
            (spectralMode?.Trim().ToLowerInvariant() is "auto" or "broad") ? BroadRecallIntent.Parse(query) : null;
        var options = new RecallOptions(k, minScore, hybrid, rerank, summaryFirst, expandGraph, spectralMode!,
            rerank && RecallFreshness.PrefersCurrent(query),
            queryEvidence is null && hybrid && rerank && !summaryFirst && RecallFreshness.PrefersCurrent(query) &&
                (spectralMode?.Trim().ToLowerInvariant() is "auto" or "broad") &&
                (broadIntent is not null || InferSpectralMode(query) == SpectralRetrievalMode.Broad) && BroadRecallRanking.IsTopicQuery(query),
            queryEvidence, BroadIntent: broadIntent);
        // Scope is independent of the short-query heuristic used for broad ranking.
        // A longer deployment/recovery topic still names the same project/environment.
        if (rerank && !summaryFirst && queryEvidence is null &&
            (BroadRecallRanking.IsTopicQuery(query) || !options.PreferCurrent))
            options = options with { Scope = RecallQueryEvidence.ParseScope(query) };
        if (rerank && !summaryFirst && !options.PreferCurrent)
            options = options with { History = RecallHistorySelection.Parse(query) };
        if (ns is not null && !_access.CanRead(ns))
            return await CompleteRecallAsync("direct", ns, [], options,
                $"No accessible memories in namespace '{ns}'.").ConfigureAwait(false);
        if (ns is not null)
        {
            var (candidates, deep, broad) = RecallNamespace(query, vector, ns, options);
            return await CompleteRecallAsync(deep ? "deep_recall" : "direct", ns, candidates,
                options with { BroadCoverage = broad }).ConfigureAwait(false);
        }

        // Strategy 2: No namespace — auto-route via expert dispatcher
        var (status, experts) = _access.IsLegacyUnisolated
            ? _dispatcher.Route(vector, topK: 3, threshold: 0.7f)
            : ("needs_expert", (IReadOnlyList<ExpertMatch>)Array.Empty<ExpertMatch>());

        var attemptedExpertNamespaces = new HashSet<string>(StringComparer.Ordinal);
        var allResults = new List<RecallCandidate>();
        bool broadMerge = false;
        if (status == "routed" && experts.Count > 0)
        {
            // A matching persona is not evidence. Try the existing (at most three)
            // routing candidates in order, once per readable target namespace. The
            // namespace pipeline applies scope, source and score requirements before
            // deciding whether any eligible evidence remains, without recording access.
            foreach (var expert in experts)
            {
                if (!_access.CanRead(expert.TargetNamespace) || !attemptedExpertNamespaces.Add(expert.TargetNamespace))
                    continue;
                var (candidates, _, broad) = RecallNamespace(query, vector, expert.TargetNamespace, options);
                if (candidates.Count == 0 || (options.Scope is not null &&
                    !candidates.Any(c => options.Scope.Matches(c.Result.Text, c.Namespace)))) continue;

                // One populated expert can answer only one part of a compound
                // request. Retain its candidates and complete the authorized sweep;
                // do not record access until the final combined selection.
                bool missingFacet = options.QueryEvidence is { IsCompound: true } compound &&
                    compound.Facets.Any(f => !candidates.Any(c => !c.Result.IsSummaryNode &&
                        !RecallFreshness.IsSuperseded(c.Result.Text) && f.Matches(c.Result.Text, c.Namespace)));
                if (options.BroadIntent?.Compound == true || missingFacet)
                {
                    allResults.AddRange(candidates);
                    broadMerge |= broad;
                    continue;
                }

                _dispatcher.RecordDispatch(expert.ExpertId);
                return await CompleteRecallAsync("expert_routed", expert.TargetNamespace, candidates, options with { BroadCoverage = broad },
                    $"Routed to expert '{expert.ExpertId}' ({expert.TargetNamespace})").ConfigureAwait(false);
            }
        }

        // Strategy 3: No expert with eligible evidence, or complementary compound
        // coverage is required — authorized broadcast with expert probes reused.
        var namespaces = _index.GetNamespaces(_access.TenantId)
            .Where(_access.CanRead)
            .Where(n => !n.StartsWith("_system", StringComparison.Ordinal) &&
                        !n.StartsWith("active-debate", StringComparison.Ordinal)).ToList();
        foreach (var searchNs in namespaces)
        {
            // Expert probes already searched this namespace with these options.
            // Reuse retained compound candidates or the empty outcome for this call.
            if (attemptedExpertNamespaces.Contains(searchNs)) continue;
            var (candidates, _, broad) = RecallNamespace(query, vector, searchNs, options);
            broadMerge |= broad;
            allResults.AddRange(candidates);
        }

        options = options with { BroadCoverage = broadMerge };
        var merged = broadMerge
            ? CalibrateBroadScores(query, vector, allResults, options.BroadIntent?.Compound == true)
            : options.Hybrid && options.PreferCurrent &&
            allResults.Select(c => c.Namespace).Distinct().Skip(1).Any()
            ? CalibrateBroadcastScores(query, vector, allResults)
            : allResults;
        return await CompleteRecallAsync("broadcast", null, merged, options,
            attemptedExpertNamespaces.Count == 0 ? $"Searched {namespaces.Count} namespace(s), no expert match" :
                options.BroadIntent?.Compound == true || options.QueryEvidence?.IsCompound == true ?
                    $"Combined compound recall across {attemptedExpertNamespaces.Union(namespaces).Count()} namespace(s)." :
                    $"No eligible evidence in routed experts; searched {attemptedExpertNamespaces.Union(namespaces).Count()} namespace(s).")
            .ConfigureAwait(false);
        }
        catch (ArgumentException ex) { return $"Error: {ex.Message}"; }
        catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "reflect", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Save an end-of-session lesson or retrospective — always stored as long-term memory with automatic cross-linking to related work. Don't use it for general notes or decisions; use `remember` for those.")]
    public object Reflect(
        [Description("The lesson or reflection text. Be specific about what happened and what was learned.")] string text,
        [Description("Namespace (project directory name).")] string ns,
        [Description("Brief topic identifier for the reflection (e.g. 'architecture-decomposition', 'dll-lock-debugging').")] string topic,
        [Description("IDs of specific memories this reflection relates to (auto-linked). Accepts a JSON array of ids, a comma-separated string, or a single id.")] JsonElement? relatedIds = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Error: text must not be empty.";
        if (string.IsNullOrWhiteSpace(ns)) return "Error: ns must not be empty.";
        if (string.IsNullOrWhiteSpace(topic)) return "Error: topic must not be empty.";
        if (!StringListNormalizer.TryNormalize(relatedIds, nameof(relatedIds), out var relatedIdList, out var relatedIdsError))
            return $"Error: {relatedIdsError}";
        if (!_access.CanWrite(ns)) return NamespaceAccess.WriteDenied(ns);

        using var timer = _metrics.StartTimer("reflect");
        var actions = new List<string>();

        try
        {
        // 1. Generate ID
        var id = $"retro-{DateTimeOffset.UtcNow:yyyy-MM-dd}-{topic}";

        // 2. Check for existing reflections on same topic to avoid duplicates
        var prefix = BenchmarkRunner.BuildContextualPrefix(ns, "lesson");
        var vector = _embedding.Embed(prefix + text);

        var existing = _index.Search(vector, ns, k: 3, minScore: 0.85f,
            category: "lesson", tenantId: _access.TenantId);
        if (existing.Count > 0 && existing[0].Score >= 0.92f)
        {
            return new ReflectResult("duplicate_warning", id, ns,
                $"Very similar reflection already exists: '{existing[0].Id}' (score: {existing[0].Score:F3}). " +
                "Consider updating the existing reflection instead.",
                actions);
        }

        // 3. Store as LTM lesson
        var entry = new CognitiveEntry(id, vector, ns, text, "lesson",
            new Dictionary<string, string> { ["topic"] = topic },
            lifecycleState: "ltm", tenantId: _access.TenantId);
        _index.Upsert(entry);
        _access.ClaimOnWrite(ns);
        actions.Add("stored as ltm lesson");

        // Steps 4 and 5 resolve bare ids and build a topology sweep, either of which can throw
        // NamespaceEnumerationException when the provider's namespace listing fails. The
        // reflection above is already durably stored, so linking must degrade rather than error
        // the write that happened — an error reply here makes the caller retry and duplicate it.
        try
        {
        // 4. Auto-link to explicitly referenced memories. These ids come from the caller, not
        // from a search inside an already-authorized namespace, so each one is resolved and
        // authorized before an edge is drawn to it (see ResolveLinkTarget).
        if (relatedIdList is { Length: > 0 })
        {
            int skipped = 0;
            // The reflection's own id is the source endpoint of every edge below, and it is a
            // bare node too: if a twin of it exists elsewhere in the tenant, links drawn from it
            // would attach to that twin's topology. Fail the whole block closed rather than
            // per-target, and report it through the same aggregate count.
            bool selfLinkable = BareIdTopology.IsTopologySafe(_index, id, _access.TenantId);
            foreach (var relatedId in relatedIdList)
            {
                if (relatedId == id) continue;

                if (!selfLinkable || ResolveLinkTarget(relatedId, ns) is null)
                {
                    skipped++;
                    continue;
                }

                _graph.AddEdge(new GraphEdge(id, relatedId, "elaborates", tenantId: _access.TenantId));
                actions.Add($"linked to {relatedId}");
            }

            // Reported as one count, never per id and never with a reason. Saying which id was
            // skipped — or distinguishing "not found" from "you may not link that" — would let a
            // caller probe for ids in namespaces it cannot see, which is the same existence
            // oracle a distinct denial reply creates for a namespace.
            if (skipped > 0)
                actions.Add($"{skipped} related id(s) skipped (not found or not linkable)");
        }

        // 5. Auto-link to semantically related memories
        var related = _index.Search(vector, ns, k: 5, minScore: 0.7f,
            tenantId: _access.TenantId);
        int autoLinked = 0;
        // Same guard as the explicit relatedIds above, and for the same reason — these targets
        // came from a search this caller is authorized for, but the edge still lands on a bare
        // node that a twin may share. One sweep for the loop; see BareIdTopology.
        var topology = BareIdTopology.ForSweep(_index, _access.TenantId);
        bool selfAutoLinkable = topology.IsTopologySafe(id);
        foreach (var r in related)
        {
            if (r.Id == id) continue;
            if (r.IsSummaryNode) continue;
            if (relatedIdList is not null && relatedIdList.Contains(r.Id)) continue;
            if (!selfAutoLinkable || !topology.IsTopologySafe(r.Id)) continue;
            if (r.Score < 0.7f) continue;

            _graph.AddEdge(new GraphEdge(id, r.Id, "cross_reference", tenantId: _access.TenantId));
            autoLinked++;
        }
        if (autoLinked > 0)
            actions.Add($"auto-linked to {autoLinked} related memor{(autoLinked == 1 ? "y" : "ies")}");
        }
        catch (NamespaceEnumerationException)
        {
            actions.Add("auto-linking skipped (namespace listing unavailable)");
        }

        // 6. Search for past reflections to surface patterns
        var pastReflections = _index.Search(vector, ns, k: 3, minScore: 0.6f,
            category: "lesson", tenantId: _access.TenantId)
            .Where(r => r.Id != id)
            .ToList();

        return new ReflectResult("stored", id, ns,
            $"Reflected on '{topic}'. Actions: {string.Join(", ", actions)}.",
            actions, pastReflections.Count > 0 ? pastReflections : null);
        }
        catch (ArgumentException ex) { return $"Error: {ex.Message}"; }
        catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "get_context_block", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns a cache-optimized memory context block for a namespace. Stable LTM memories sorted by ID (deterministic ordering for prompt caching). Place this block as a stable prefix in your context to benefit from prompt caching across turns.")]
    public object GetContextBlock(
        [Description("Namespace to build context block from.")] string ns,
        [Description("Maximum number of LTM memories to include (default: 20).")] int maxEntries = 20,
        [Description("Minimum access count to qualify as 'stable' (default: 2).")] int minAccessCount = 2,
        [Description("Include namespace statistics header (default: true). Set false to keep statistics changes from invalidating the cache.")] bool includeHeader = true)
    {
        using var timer = _metrics.StartTimer("get_context_block");

        if (!_access.CanRead(ns))
            return NamespaceAccess.ReadDenied(ns);

        // Get all LTM entries (these are the most stable memories worth caching)
        var allEntries = _index.GetAllInNamespace(ns, _access.TenantId);
        var stableEntries = allEntries
            .Where(e => e.LifecycleState == "ltm" && e.AccessCount >= minAccessCount && !e.IsSummaryNode)
            .OrderBy(e => e.Id, StringComparer.Ordinal) // Stable across host cultures
            .Take(maxEntries)
            .ToList();

        // Build the stable block — exclude volatile fields (scores, timestamps, access counts)
        // that would change between calls and invalidate the cache
        var stableBlock = stableEntries.Select(e => new StableMemoryEntry(
            e.Id, e.Text, e.Category,
            e.Metadata.Count > 0
                ? e.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                : null)).ToList();

        // Snapshot the returned data before hashing it. In particular, metadata must not
        // alias the live entry, and its insertion order must not affect the cache token.
        var (stm, ltm, archived) = _index.GetStateCounts(ns, _access.TenantId);
        var header = includeHeader ? new NamespaceHeader(stm, ltm, archived, stableEntries.Count) : null;
        var content = JsonSerializer.SerializeToUtf8Bytes(new { ns, stableMemories = stableBlock, header });
        var version = $"sha256:{Convert.ToHexString(SHA256.HashData(content))}";

        return new ContextBlockResult(
            ns,
            version,
            stableBlock,
            header,
            "Place this block as a stable prefix in your system prompt. " +
                "Append dynamic query results after this block. " +
                "The version is an opaque content fingerprint of the namespace, stable memories, " +
                "and included header; cache is valid while version is unchanged. " +
                "Set includeHeader to false to avoid invalidation from namespace statistics changes.");
    }

    private sealed record RecallOptions(int K, float MinScore, bool Hybrid, bool Rerank,
        bool SummaryFirst, bool ExpandGraph, string SpectralMode, bool PreferCurrent, bool BroadCoverage,
        RecallQueryEvidence? QueryEvidence, RecallQueryEvidence? Scope = null, RecallHistorySelection? History = null,
        BroadRecallIntent? BroadIntent = null);

    // Keep namespace provenance for selection and conflict evidence. Graph neighbors
    // may belong to another readable namespace; bare IDs are not storage keys.
    private sealed record RecallCandidate(string Namespace, CognitiveSearchResult Result, bool Resurrect,
        IReadOnlyList<CognitiveSearchResult>? ConflictEvidence = null, bool CoverageRecovery = false);

    // With the default five-result budget, leave room for an overview, two sources,
    // and other independent hits. Smaller budgets retain the strongest hit first.
    private const int SummarySourceLimit = 2;

    private (IReadOnlyList<RecallCandidate> Candidates, bool Deep, bool Broad) RecallNamespace(
        string query, float[] vector, string ns, RecallOptions options)
    {
        var states = new HashSet<string> { "stm", "ltm" };
        (IReadOnlyList<CognitiveSearchResult> Hits, IReadOnlyList<CognitiveSearchResult> Evidence) Search(HashSet<string> includeStates, int? limit = null)
        {
            int take = limit ?? options.K;
            IReadOnlyList<CognitiveSearchResult> Retrieve(int count)
            {
                var found = _index.Search(new SearchRequest
                {
                    Query = vector, QueryText = options.Hybrid ? query : null,
                    Namespace = ns, TenantId = _access.TenantId,
                    K = !options.Hybrid && options.Rerank ? count * 2 : count,
                    MinScore = options.MinScore, IncludeStates = includeStates,
                    Hybrid = options.Hybrid, Rerank = options.Rerank, SummaryFirst = options.SummaryFirst
                });
                return !options.Hybrid && options.Rerank ? _index.Rerank(query, found) : found;
            }
            var results = Retrieve(take);
            var evidence = results;
            // A superseded top hit must not hide its replacement just outside k.
            // Retry once only if status demotion would leave fewer than k unmarked
            // hits. Vector reranking may already have retrieved enough alternatives.
            if (options.PreferCurrent)
            {
                int superseded = results.Count(r => RecallFreshness.IsSuperseded(r.Text));
                if (limit is null && superseded > 0 && results.Count - superseded < take)
                {
                    results = evidence = Retrieve(take > int.MaxValue / 2 ? int.MaxValue : take * 2);
                }
                // A conflicting value can sit just outside k even without graph links.
                // Leave a small evidence floor for repeated copies even when k=1.
                // Probe only for supported factual claims, with at most 32 extra hits.
                // Diagnostic evidence never changes the primary relevance selection.
                int evidenceCount = options.K + Math.Min(Math.Max(options.K, 8), Math.Min(32, int.MaxValue - options.K));
                if (evidence.Count < evidenceCount &&
                    results.Any(r => !r.IsSummaryNode && RecallConflictDetection.Parse(r.Text) is not null))
                    evidence = Retrieve(evidenceCount);
            }
            return (OrderRecall(results, options, ns).Take(take).ToList(), evidence.Take(64).ToList());
        }

        IReadOnlyList<CognitiveSearchResult> results = [], evidence = [];
        bool deep = false;
        int candidateCount = options.K;
        bool useBroad = false;
        bool recoverCoverage = false;
        if (options.BroadCoverage)
        {
            // A broad answer needs room to choose complementary evidence. Keep the
            // public result budget, but inspect at most 32 additional candidates.
            // Relevant archived lessons may complement a confident live overview.
            candidateCount = BroadRecallRanking.CandidateLimit(options.K);
            HashSet<string> broadStates = ["stm", "ltm", "archived"];
            var (broader, broaderEvidence) = Search(broadStates, candidateCount);
            recoverCoverage = options.BroadIntent is not null && (options.BroadIntent.Compound ||
                broader.Any(r => !r.IsSummaryNode && !BroadRecallIntent.HasSourceEvidence(r.Text)) ||
                (options.Scope is not null && !broader.Any(r => !r.IsSummaryNode && options.Scope.Matches(r.Text, ns))));
            useBroad = recoverCoverage || BroadRecallRanking.HasTopicEvidence(query, broader);
            if (useBroad)
            {
                states = broadStates;
                if (recoverCoverage)
                {
                    // An overview can have a partial answer yet still lose whole
                    // topics behind repeated index/planning notes. Recover source
                    // evidence before truncation, within this authorized partition
                    // and the original cosine threshold. No new embedding or scope.
                    var recovered = _index.SearchEligible(new SearchRequest
                    {
                        Query = vector, Namespace = ns, TenantId = _access.TenantId,
                        K = candidateCount, MinScore = options.MinScore, IncludeStates = states
                    }, e => !e.IsSummaryNode && !RecallFreshness.IsSuperseded(e.Text) &&
                        (options.Scope is null || options.Scope.Matches(e.Text, ns)) &&
                        BroadRecallIntent.HasSourceEvidence(e.Text));
                    broader = broader.Concat(recovered).DistinctBy(r => r.Id).ToList();
                    broaderEvidence = broader.Take(64).ToList();
                }
                evidence = broaderEvidence;
                deep = broader.Any(r => r.LifecycleState == "archived");
                results = BroadRecallRanking.Rank(query, vector, broader, r => r,
                    r => _index.Get(r.Id, ns, _access.TenantId)?.Vector,
                    options.BroadIntent?.Compound == true ? _ => ns : null)
                    .Select(r => r.Candidate with { Score = r.Score }).ToList();
            }
        }

        if (options.History is not null)
        {
            // A confident current hit says nothing about archive relevance to the
            // requested period. Search all states, retaining the public result budget.
            states = ["stm", "ltm", "archived"];
            int historyCount = BroadRecallRanking.CandidateLimit(options.K);
            (results, evidence) = Search(states, historyCount);
            bool HistoricalSource(string? text) =>
                (options.Scope is null || options.Scope.Matches(text, ns)) && options.History.Prefers(text, ns);
            if (!results.Any(r => !r.IsSummaryNode && HistoricalSource(r.Text)))
            {
                // Noise can occupy the entire ranked window. The existing exact
                // recovery path filters this authorized partition before truncation.
                var recovered = _index.Rerank(query, _index.SearchEligible(new SearchRequest
                {
                    Query = vector, Namespace = ns, TenantId = _access.TenantId,
                    K = historyCount, MinScore = options.MinScore, IncludeStates = states
                }, e => !e.IsSummaryNode && HistoricalSource(e.Text)));
                results = results.Concat(recovered).DistinctBy(r => r.Id).ToList();
                evidence = results.Take(64).ToList();
            }
            deep = results.Any(r => r.LifecycleState == "archived");
        }
        else if (!useBroad)
        {
            // Preserve the existing confidence-triggered fallback for specific
            // queries, opt-outs, and overviews with no discriminating source topic.
            (results, evidence) = Search(states);
            if (results.Count == 0 || results[0].Score < 0.5f)
            {
                HashSet<string> allStates = ["stm", "ltm", "archived"];
                var (recovered, recoveredEvidence) = Search(allStates);
                if (recovered.Count > results.Count ||
                    (recovered.Count > 0 && (results.Count == 0 || recovered[0].Score > results[0].Score)))
                {
                    results = recovered;
                    evidence = recoveredEvidence;
                    states = allStates;
                    deep = true;
                }
            }
        }

        if (options.QueryEvidence is { IsCompound: true } compound)
        {
            // A partial source must not prevent recovery of the other answer.
            // At most two bounded exact probes, filtered before truncation within
            // this authorized partition and the ORIGINAL query's cosine floor.
            states = ["stm", "ltm", "archived"];
            foreach (var facet in compound.Facets)
            {
                bool Source(string? text) => !RecallFreshness.IsSuperseded(text) &&
                    compound.Matches(text, ns) && facet.Matches(text, ns);
                if (results.Any(r => !r.IsSummaryNode && Source(r.Text))) continue;
                var recovered = _index.Rerank(query, _index.SearchEligible(new SearchRequest
                {
                    Query = vector, Namespace = ns, TenantId = _access.TenantId,
                    K = BroadRecallRanking.CandidateLimit(options.K), MinScore = options.MinScore, IncludeStates = states
                }, e => !e.IsSummaryNode && Source(e.Text)));
                results = results.Concat(recovered).DistinctBy(r => r.Id).ToList();
                evidence = evidence.Concat(recovered).DistinctBy(r => r.Id).Take(64).ToList();
            }
            deep |= results.Any(r => r.LifecycleState == "archived");
        }

        // Broad scores depend on the retrieved pool's topic frequencies. Keep that
        // pool intact through calibration, then enforce scope at final selection.
        // Otherwise removing unrelated contexts can demote an already-found source.
        if (options.QueryEvidence is not null || options.History is not null || (options.Scope is not null && !useBroad))
        {
            bool EligibleText(string? text) => (options.QueryEvidence is null || options.QueryEvidence.Matches(text, ns)) &&
                (options.History is null || options.History.MatchesTopic(text, ns)) &&
                (options.Scope is null || options.Scope.Matches(text, ns));
            bool Eligible(CognitiveSearchResult r) => EligibleText(r.Text);
            // Apply evidence requirements after the established confidence fallback:
            // filtering must not change archive ranking when live evidence already fits.
            // If only mismatches/summaries occupy k, retry a bounded larger window
            // for factual/history requests. Broad retrieval already probes its
            // wider archive pool; preserve its existing topic/coverage decisions.
            if ((options.QueryEvidence is not null || !options.PreferCurrent) &&
                !results.Any(r => !r.IsSummaryNode && Eligible(r)))
            {
                HashSet<string> allStates = ["stm", "ltm", "archived"];
                var (recovered, recoveredEvidence) = Search(allStates, BroadRecallRanking.CandidateLimit(options.K));
                results = recovered;
                evidence = recoveredEvidence;
                states = allStates;
                deep = recovered.Any(r => r.LifecycleState == "archived" && Eligible(r));
            }
            if ((options.QueryEvidence is not null || options.History is not null) &&
                !results.Any(r => !r.IsSummaryNode && Eligible(r)))
            {
                // Both ranked windows can be full of reference-only notes or wrong
                // scopes. Filter within this authorized partition BEFORE truncation.
                // Reuse the query embedding, exact cosine threshold, and public k.
                // Probe sources only: summaries cannot manufacture a factual answer.
                // Historical context must match the topic even if no source can
                // establish the requested period; unrelated noise must not fill k.
                int recoveryCount = BroadRecallRanking.CandidateLimit(options.K);
                var recovered = _index.Rerank(query, _index.SearchEligible(new SearchRequest
                {
                    Query = vector, Namespace = ns, TenantId = _access.TenantId,
                    K = recoveryCount, MinScore = options.MinScore, IncludeStates = states
                }, e => !e.IsSummaryNode && EligibleText(e.Text)));
                // Retain eligible summaries as graph anchors. A linked source may
                // keep this expert route even when no local source was recovered.
                results = results.Where(r => r.IsSummaryNode && Eligible(r)).Concat(recovered).ToList();
                evidence = results.Take(64).ToList();
                deep = results.Any(r => r.LifecycleState == "archived");
            }
            results = results.Where(Eligible).ToList();
        }

        var sources = results.ToDictionary(r => r.Id, _ => ns);
        // Recovery eligibility uses semantic similarity, not RRF or graph-boosted scores.
        var recoverable = results.Where(r => options.History is null && deep && r.LifecycleState == "archived")
            .Where(r => _index.Get(r.Id, ns, _access.TenantId) is { } entry &&
                VectorMath.Dot(vector, entry.Vector) /
                    (VectorMath.Norm(vector) * VectorMath.Norm(entry.Vector) + 1e-9f) >= 0.7f)
            .Select(r => r.Id).ToHashSet();
        var expanded = options.ExpandGraph
            ? ExpandWithGraph(results, states, sources, vector, options.MinScore)
            : results;
        if (options.Scope is not null)
        {
            if (!useBroad)
                expanded = expanded.Where(r => options.Scope.Matches(r.Text, sources.GetValueOrDefault(r.Id, ns))).ToList();
            evidence = evidence.Where(r => options.Scope.Matches(r.Text, ns)).ToList();
        }
        expanded = ApplySpectralRerankRestricted(ns, expanded, options.SpectralMode, query, options.K,
            options.ExpandGraph, states);
        if (recoverCoverage)
        {
            // Calibrate against the original pool first, but do not let rejected
            // scopes consume the recovered budget. Graph context must satisfy the
            // same original cosine floor before it can supply coverage or support.
            float queryNorm = VectorMath.Norm(vector);
            expanded = expanded.Where(r =>
            {
                string source = sources.GetValueOrDefault(r.Id, ns);
                if (options.Scope is not null && !options.Scope.Matches(r.Text, source)) return false;
                var entry = _index.Get(r.Id, source, _access.TenantId);
                return entry is not null && entry.Vector.Length == vector.Length &&
                    VectorMath.Dot(vector, entry.Vector) / (queryNorm * VectorMath.Norm(entry.Vector) + 1e-9f) >= options.MinScore;
            }).ToList();
        }
        var candidates = expanded.Select(r =>
            new RecallCandidate(sources.GetValueOrDefault(r.Id, ns), r, recoverable.Contains(r.Id),
                sources.GetValueOrDefault(r.Id, ns) == ns ? evidence : null, recoverCoverage));
        return (useBroad
            ? candidates.OrderBy(c => RecallFreshness.IsSuperseded(c.Result.Text))
                .ThenByDescending(c => options.BroadIntent is not null && IsBroadSource(c))
                .ThenByDescending(c => c.Result.Score).Take(candidateCount).ToList()
            : SelectRecallCandidates(candidates, options with { BroadCoverage = false }), deep, useBroad);
    }

    private static IOrderedEnumerable<CognitiveSearchResult> OrderRecall(
        IEnumerable<CognitiveSearchResult> results, RecallOptions options, string ns) =>
        results.OrderByDescending(r => options.SummaryFirst && r.IsSummaryNode)
            .ThenBy(r => options.PreferCurrent && RecallFreshness.IsSuperseded(r.Text))
            .ThenByDescending(r => !r.IsSummaryNode && options.History?.Prefers(r.Text, ns) == true)
            .ThenByDescending(r => r.Score);

    private IReadOnlyList<RecallCandidate> CalibrateBroadcastScores(
        string query, float[] vector, IReadOnlyList<RecallCandidate> candidates)
    {
        var output = new List<RecallCandidate>();
        float queryNorm = VectorMath.Norm(vector);
        // Namespace-local hybrid paths return cosine, boosted cosine, or RRF.
        // Compare final authorized candidates on the same relevance scale. Local
        // retrieval, archive fallback and raw search retain their existing scores.
        foreach (var group in candidates.GroupBy(c => c.Namespace))
        {
            // The same memory may also arrive as another namespace's graph neighbor.
            // Retain its own search provenance for diagnosis and recovery eligibility.
            var byId = group.GroupBy(c => c.Result.Id).Select(g => g
                .OrderByDescending(c => c.ConflictEvidence is not null)
                .ThenByDescending(c => c.Result.Score).First()).ToDictionary(c => c.Result.Id);
            var cosines = new Dictionary<string, float>();
            foreach (var candidate in byId.Values)
                if (_index.Get(candidate.Result.Id, group.Key, _access.TenantId) is { } entry &&
                    entry.Vector.Length == vector.Length)
                    cosines[candidate.Result.Id] = VectorMath.Dot(vector, entry.Vector) /
                        (queryNorm * VectorMath.Norm(entry.Vector) + 1e-9f);
            var reranker = new TokenReranker(r => cosines[r.Id]);
            foreach (var result in reranker.Rerank(query,
                byId.Values.Where(c => cosines.ContainsKey(c.Result.Id)).Select(c => c.Result).ToList()))
                output.Add(byId[result.Id] with { Result = result });
        }
        return output;
    }

    private IReadOnlyList<RecallCandidate> CalibrateBroadScores(string query, float[] vector,
        IReadOnlyList<RecallCandidate> candidates, bool compound)
    {
        var unique = candidates.GroupBy(c => (c.Namespace, c.Result.Id)).Select(g => g
            .OrderByDescending(c => c.ConflictEvidence is not null).ThenByDescending(c => c.Result.Score).First()).ToList();
        var terms = BroadRecallRanking.Tokens(query);
        var matches = unique.Select(c => c.Namespace).Distinct()
            .ToDictionary(ns => ns, ns => BroadRecallRanking.Tokens(ns).Count(terms.Contains));
        int strongest = matches.Values.DefaultIfEmpty().Max();
        // A named topic can distinguish expert_project_recovery from project_ops.
        // Compound requests must not favor one named component over the others.
        bool preferTopicNamespace = !compound && unique.Any(c => c.CoverageRecovery);
        var named = matches.Where(p => p.Value > 0 && (!preferTopicNamespace || p.Value == strongest))
            .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        return BroadRecallRanking.Rank(query, vector, unique, c => c.Result,
            c => _index.Get(c.Result.Id, c.Namespace, _access.TenantId)?.Vector,
            compound ? c => c.Namespace : null)
            .Select(c => c.Candidate with { Result = c.Candidate.Result with
                { Score = c.Score * (named.Count > 0 && !named.Contains(c.Candidate.Namespace) ? .75f : 1f) } }).ToList();
    }

    private IReadOnlyList<RecallCandidate> SelectRecallCandidates(
        IEnumerable<RecallCandidate> candidates, RecallOptions options)
    {
        var ranked = candidates.Where(c => (options.QueryEvidence is null || options.QueryEvidence.Matches(c.Result.Text, c.Namespace)) &&
                (options.History is null || options.History.MatchesTopic(c.Result.Text, c.Namespace)) &&
                (options.Scope is null || options.Scope.Matches(c.Result.Text, c.Namespace)))
            .OrderByDescending(c => options.SummaryFirst && c.Result.IsSummaryNode)
            .ThenBy(c => options.PreferCurrent && RecallFreshness.IsSuperseded(c.Result.Text))
            .ThenByDescending(c => !c.Result.IsSummaryNode && options.History?.Prefers(c.Result.Text, c.Namespace) == true)
            .ThenByDescending(c => c.Result.Score)
            .ThenBy(c => c.Namespace, StringComparer.Ordinal).ThenBy(c => c.Result.Id, StringComparer.Ordinal)
            .DistinctBy(c => (c.Namespace, c.Result.Id)).ToList();
        if (options.QueryEvidence is not null)
        {
            // Overviews remain useful alongside sources but cannot establish that a
            // factual answer was retrieved. Small budgets keep actual source evidence.
            if (!ranked.Any(c => !c.Result.IsSummaryNode)) return [];
            if (options.QueryEvidence.IsCompound)
                return SelectCompoundCandidates(ranked, options.K, options.QueryEvidence);
            if (options.K == 1) return ranked.Where(c => !c.Result.IsSummaryNode).Take(1).ToList();
        }
        if (options.BroadCoverage) return SelectBroadCandidates(ranked, options.K,
            options.BroadIntent is not null, options.ExpandGraph && options.BroadIntent?.Compound == true);
        var selected = ranked.Take(options.K).ToList();
        if (options.QueryEvidence is not null && selected.All(c => c.Result.IsSummaryNode))
            selected[^1] = ranked.First(c => !c.Result.IsSummaryNode);
        if (options.SummaryFirst || !options.ExpandGraph || selected.Count < 2 ||
            !selected.Any(c => c.Result.IsSummaryNode)) return selected;

        // Keep up to two query-ranked sources with each retained summary when possible.
        // This is selection, not a score boost: keep the strongest hit and original
        // scores, and place supporting context next to the overview it explains.
        // Operate only on already authorized candidates, including during broadcast.
        var protectedKeys = new HashSet<(string Namespace, string Id)>
            { (selected[0].Namespace, selected[0].Result.Id) };
        var topology = BareIdTopology.ForSweep(_index, tenantId: _access.TenantId);
        var pairs = new Dictionary<(string Namespace, string Id), List<RecallCandidate>>();
        foreach (var summary in selected.Where(c => c.Result.IsSummaryNode &&
            !(options.PreferCurrent && RecallFreshness.IsSuperseded(c.Result.Text))).ToList())
        {
            if (!selected.Contains(summary) || !topology.IsTopologySafe(summary.Result.Id)) continue;
            var linkedKeys = _graph.GetNeighbors(summary.Result.Id, relation: null, direction: "both",
                    tenantId: _access.TenantId).Neighbors
                .Where(n => n.Edge.Relation != "contradicts")
                .Select(n => (n.Entry.Namespace, n.Entry.Id)).ToHashSet();
            var sources = ranked.Where(c => !c.Result.IsSummaryNode &&
                !(options.PreferCurrent && RecallFreshness.IsSuperseded(c.Result.Text)) &&
                linkedKeys.Contains((c.Namespace, c.Result.Id))).Take(Math.Min(SummarySourceLimit, options.K - 1));
            foreach (var source in sources)
            {
                if (!selected.Contains(source))
                {
                    var replace = selected.FindLastIndex(c => c != summary &&
                        !protectedKeys.Contains((c.Namespace, c.Result.Id)));
                    if (replace < 0) continue;
                    selected[replace] = source;
                }
                var key = (summary.Namespace, summary.Result.Id);
                if (!pairs.TryGetValue(key, out var linked)) pairs[key] = linked = new List<RecallCandidate>();
                linked.Add(source);
                protectedKeys.Add(key);
                protectedKeys.Add((source.Namespace, source.Result.Id));
            }
        }
        var keys = selected.Select(c => (c.Namespace, c.Result.Id)).ToHashSet();
        var output = new List<RecallCandidate>();
        var emitted = new HashSet<(string Namespace, string Id)>();
        foreach (var candidate in ranked.Where(c => keys.Contains((c.Namespace, c.Result.Id))))
        {
            if (!emitted.Add((candidate.Namespace, candidate.Result.Id))) continue;
            output.Add(candidate);
            if (pairs.TryGetValue((candidate.Namespace, candidate.Result.Id), out var sources))
                foreach (var source in sources)
                    if (keys.Contains((source.Namespace, source.Result.Id)) && emitted.Add((source.Namespace, source.Result.Id)))
                        output.Add(source);
        }
        return output;
    }

    private static IReadOnlyList<RecallCandidate> SelectCompoundCandidates(
        IReadOnlyList<RecallCandidate> ranked, int k, RecallQueryEvidence compound)
    {
        var selected = new List<RecallCandidate>();
        var uncovered = compound.Facets.ToHashSet();
        // Reserve source slots for distinct requested facts before filling with
        // repeated evidence or summaries. Ranking breaks ties; scores are unchanged.
        while (selected.Count < k && uncovered.Count > 0)
        {
            var next = ranked.Where(c => !c.Result.IsSummaryNode && !selected.Contains(c) &&
                    !RecallFreshness.IsSuperseded(c.Result.Text))
                .Select(c => (Candidate: c, Covered: uncovered.Where(f => f.Matches(c.Result.Text, c.Namespace)).ToArray()))
                .Where(c => c.Covered.Length > 0).OrderByDescending(c => c.Covered.Length).FirstOrDefault();
            if (next.Candidate is null) break;
            selected.Add(next.Candidate);
            uncovered.ExceptWith(next.Covered);
        }
        // Preserve the factual-source invariant even when only superseded
        // sources remain and a current summary ranks ahead of all of them.
        if (selected.Count == 0) selected.Add(ranked.First(c => !c.Result.IsSummaryNode));
        selected.AddRange(ranked.Where(c => !selected.Contains(c)).Take(k - selected.Count));
        return selected;
    }

    private static bool IsBroadSource(RecallCandidate candidate) =>
        BroadRecallIntent.HasSourceEvidence(candidate.Result.Text);

    private IReadOnlyList<RecallCandidate> SelectBroadCandidates(IReadOnlyList<RecallCandidate> ranked, int k,
        bool preferSources, bool expandGraph)
    {
        if (ranked.Count <= 1) return ranked;
        var remaining = ranked.ToList();
        var selected = new List<RecallCandidate>();
        var vectors = ranked.ToDictionary(c => (c.Namespace, c.Result.Id),
            c =>
            {
                var vector = _index.Get(c.Result.Id, c.Namespace, _access.TenantId)?.Vector;
                return (Vector: vector, Norm: vector is null ? 0 : VectorMath.Norm(vector));
            });
        var redundancy = ranked.ToDictionary(c => (c.Namespace, c.Result.Id), _ => 0f);
        var substantive = ranked.ToDictionary(c => (c.Namespace, c.Result.Id), c => !preferSources || IsBroadSource(c));
        bool recovered = ranked.Any(c => c.CoverageRecovery);
        float diversityWeight = recovered ? .4f : .2f;
        var supported = new HashSet<(string Namespace, string Id)>();
        if (recovered && expandGraph)
        {
            // Linked evidence can be complementary even when its vectors are very
            // similar (for example a procedure and its observed result). Give one
            // bounded support bonus, never a degree/count bonus. Both endpoints
            // must already be eligible, unambiguous, current source candidates.
            var topology = BareIdTopology.ForSweep(_index, tenantId: _access.TenantId);
            var sources = ranked.Where(c => !c.Result.IsSummaryNode && !RecallFreshness.IsSuperseded(c.Result.Text) &&
                substantive[(c.Namespace, c.Result.Id)] && topology.IsTopologySafe(c.Result.Id))
                .ToDictionary(c => (c.Namespace, c.Result.Id));
            foreach (var candidate in sources.Values)
                foreach (var neighbor in _graph.GetNeighbors(candidate.Result.Id, relation: null,
                    direction: "both", tenantId: _access.TenantId).Neighbors)
                    if (neighbor.Edge.Relation is "related_to" or "elaborates" &&
                        sources.TryGetValue((neighbor.Entry.Namespace, neighbor.Entry.Id), out var other) &&
                        other.Result.Text != candidate.Result.Text)
                    {
                        supported.Add((candidate.Namespace, candidate.Result.Id));
                        supported.Add((other.Namespace, other.Result.Id));
                    }
        }
        while (selected.Count < k && remaining.Count > 0)
        {
            // Preserve status priority; explicit coverage recovery prefers evidence
            // and bounded graph support. Later hits trade relevance for diversity.
            var next = remaining.OrderBy(c => RecallFreshness.IsSuperseded(c.Result.Text))
                .ThenByDescending(c => preferSources && substantive[(c.Namespace, c.Result.Id)])
                .ThenByDescending(c => (1 - diversityWeight) *
                    (c.Result.Score + (supported.Contains((c.Namespace, c.Result.Id)) ? .1f : 0)) -
                    diversityWeight * redundancy[(c.Namespace, c.Result.Id)]).First();
            selected.Add(next);
            remaining.Remove(next);
            var chosen = vectors[(next.Namespace, next.Result.Id)];
            if (chosen.Vector is null || selected.Count == k) continue;
            // Update each remaining candidate once per selection instead of
            // recomputing every previously selected pair for larger result budgets.
            foreach (var candidate in remaining)
            {
                // An overview overlaps its sources by design; it is not a
                // replacement for evidence and must not suppress those sources.
                if (next.Result.IsSummaryNode != candidate.Result.IsSummaryNode) continue;
                var key = (candidate.Namespace, candidate.Result.Id);
                var other = vectors[key];
                if (other.Vector is null || other.Vector.Length != chosen.Vector.Length) continue;
                redundancy[key] = Math.Max(redundancy[key], VectorMath.Dot(other.Vector, chosen.Vector) /
                    (other.Norm * chosen.Norm + 1e-9f));
            }
        }
        return selected;
    }

    private async Task<RecallResult> CompleteRecallAsync(string strategy, string? ns, IEnumerable<RecallCandidate> candidates,
        RecallOptions options, string? routingInfo = null)
    {
        var selected = SelectRecallCandidates(candidates, options);
        var conflicts = FindRecallConflicts(selected, options);
        if (_knowledgeStore is not null && conflicts.Count is > 0 and < 8)
            conflicts = await new RecallEvidenceResolution(_index, _access, _knowledgeStore).ResolveAsync(conflicts).ConfigureAwait(false);
        var disputed = conflicts.SelectMany(c => c.Evidence).Select(e => (e.Namespace, e.Id)).ToHashSet();
        var results = new List<CognitiveSearchResult>();
        foreach (var candidate in selected)
        {
            var result = candidate.Result;
            // Historical usefulness does not make a superseded fact current again.
            if (candidate.Resurrect && !RecallFreshness.IsSuperseded(result.Text) &&
                !disputed.Contains((candidate.Namespace, result.Id)) && _access.CanWrite(candidate.Namespace) &&
                _index.SetLifecycleState(result.Id, "stm", candidate.Namespace, _access.TenantId))
                result = result with { LifecycleState = "stm" };
            _index.RecordAccess(result.Id, candidate.Namespace, _access.TenantId);
            results.Add(result);
        }
        return new RecallResult(strategy, ns, results, routingInfo)
        {
            Conflicts = conflicts.Count > 0 ? conflicts : null,
            QueryEvidence = options.QueryEvidence is not null && results.Count == 0
                ? new RecallQueryEvidenceResult("insufficient_evidence", "No source in the eligible recall window matched the question's subject, environment and topic. This does not establish that an answer is absent from all stored memories.")
                : null
        };
    }

    private static IReadOnlyList<RecallConflict> FindRecallConflicts(
        IReadOnlyList<RecallCandidate> selected, RecallOptions options)
    {
        var conflicts = new List<RecallConflict>();
        if (!options.PreferCurrent) return conflicts;
        var seen = new HashSet<(string Namespace, string First, string Second)>();
        var parsed = new Dictionary<(string Namespace, string Id), IReadOnlyList<RecallConflictDetection.Claim>>();
        IReadOnlyList<RecallConflictDetection.Claim> Claims(string sourceNs, CognitiveSearchResult result)
        {
            var key = (sourceNs, result.Id);
            if (!parsed.TryGetValue(key, out var claims))
                parsed[key] = claims = RecallConflictDetection.ParseAll(result.Text);
            return claims;
        }
        // Evidence was retrieved within the same authorized namespace/tenant, with
        // the original minScore and lifecycle scope. Never consult bare graph IDs,
        // hidden namespaces, timestamps, or user-supplied metadata for authority.
        foreach (var candidate in selected.Take(32))
        {
            if (candidate.Result.IsSummaryNode || candidate.ConflictEvidence is null) continue;
            var claims = Claims(candidate.Namespace, candidate.Result);
            if (claims.Count == 0) continue;
            // A selected copy can hide the original cited record. Include records
            // expressing the same normalized claim as diagnostic anchors, preserving
            // their actual IDs. Copies are not votes and receive no access updates.
            var anchors = candidate.ConflictEvidence.Where(r => !r.IsSummaryNode &&
                    Claims(candidate.Namespace, r).Any(claims.Contains))
                .Prepend(candidate.Result).DistinctBy(r => r.Id);
            foreach (var anchor in anchors)
            foreach (var alternative in candidate.ConflictEvidence)
            {
                if (alternative.Id == anchor.Id || alternative.IsSummaryNode) continue;
                var anchorClaims = Claims(candidate.Namespace, anchor).Where(claims.Contains);
                var others = Claims(candidate.Namespace, alternative);
                var disagreement = (from claim in anchorClaims from other in others
                    where claim.ConflictsWith(other) select (Claim: claim, Other: other)).FirstOrDefault();
                if (disagreement.Claim is not { } match) continue;
                var first = anchor.Id;
                var second = alternative.Id;
                if (StringComparer.Ordinal.Compare(first, second) > 0) (first, second) = (second, first);
                if (!seen.Add((candidate.Namespace, first, second))) continue;
                string basis = match.Negated != disagreement.Other.Negated || match.Kind == "proposition"
                    ? "explicit_negation" : "same_subject_different_value";
                conflicts.Add(new RecallConflict("unresolved", basis, match.Subject,
                    [new(anchor.Id, candidate.Namespace, anchor.Text!),
                     new(alternative.Id, candidate.Namespace, alternative.Text!)]));
                if (conflicts.Count == 8) return conflicts;
            }
        }
        return conflicts;
    }

    private IReadOnlyList<CognitiveSearchResult> ExpandWithGraph(
        IReadOnlyList<CognitiveSearchResult> results, HashSet<string> states, Dictionary<string, string> sources,
        float[] queryVector, float minScore)
    {
        if (results.Count == 0) return results;

        var existingIds = results.Select(r => r.Id).ToHashSet();
        var expanded = new List<CognitiveSearchResult>(results);
        float lowestScore = results.Min(r => r.Score);
        float queryNorm = VectorMath.Norm(queryVector);

        // The seeds are entries the caller may read, but graph adjacency is keyed (tenant, id)
        // with no namespace: an id the tenant holds in two namespaces names ONE node, shared with
        // a twin the caller cannot see. Expanding through it would attach that twin's topology to
        // this caller's hit, so an ambiguous seed contributes no neighbors. One sweep for the
        // whole expansion — see BareIdTopology for why the test is ACL-blind and what it costs.
        var topology = BareIdTopology.ForSweep(_index, tenantId: _access.TenantId);

        foreach (var result in results)
        {
            if (!topology.IsTopologySafe(result.Id)) continue;

            var neighbors = _graph.GetNeighbors(result.Id, relation: null, direction: "both",
                tenantId: _access.TenantId);
            foreach (var neighbor in neighbors.Neighbors)
            {
                if (existingIds.Contains(neighbor.Entry.Id)) continue;
                if (!states.Contains(neighbor.Entry.LifecycleState)) continue;
                if (!_access.CanRead(neighbor.Entry.Namespace)) continue;

                var entry = _index.Get(neighbor.Entry.Id, neighbor.Entry.Namespace, _access.TenantId);
                if (entry is null) continue;
                float score = lowestScore * 0.8f;
                if (result.IsSummaryNode)
                {
                    // Drill into an overview using the query, not equal scores for every
                    // linked source. Scale by the parent's retrieval score so cosine and
                    // hybrid/RRF scores are never directly compared. A source cannot
                    // inherit more confidence than the summary that led us to it.
                    if (entry.Vector.Length != queryVector.Length) continue;
                    float similarity = VectorMath.Dot(queryVector, entry.Vector) /
                        (queryNorm * VectorMath.Norm(entry.Vector) + 1e-9f);
                    if (similarity < minScore) continue;
                    score = result.Score * Math.Clamp(similarity, 0f, 1f);
                }
                existingIds.Add(entry.Id);
                sources[entry.Id] = neighbor.Entry.Namespace;
                expanded.Add(new CognitiveSearchResult(
                    entry.Id, entry.Text, score, entry.LifecycleState, entry.ActivationEnergy,
                    entry.Category, new Dictionary<string, string>(entry.Metadata), entry.IsSummaryNode,
                    entry.SourceClusterId, entry.AccessCount));
            }
        }

        return expanded;
    }

    /// <summary>
    /// Apply the spectral retrieval reranker to <paramref name="candidates"/>,
    /// restricting the output to entries already in the candidate set so the
    /// existing recall API contract (results were retrieved by query relevance)
    /// is preserved. Mode 'none' or unrecognized falls through unchanged. Mode
    /// 'auto' is resolved by <see cref="InferSpectralMode"/>.
    /// </summary>
    private IReadOnlyList<CognitiveSearchResult> ApplySpectralRerankRestricted(
        string ns,
        IReadOnlyList<CognitiveSearchResult> candidates,
        string spectralMode,
        string query,
        int k, bool expandGraph, HashSet<string> states)
    {
        if (candidates.Count == 0) return candidates;
        if (string.IsNullOrWhiteSpace(spectralMode)) return candidates;

        var resolved = spectralMode.Trim().ToLowerInvariant() switch
        {
            "broad" => SpectralRetrievalMode.Broad,
            "specific" => SpectralRetrievalMode.Specific,
            "auto" => InferSpectralMode(query),
            "none" => SpectralRetrievalMode.None,
            _ => SpectralRetrievalMode.None,
        };
        if (resolved == SpectralRetrievalMode.None) return candidates;

        // Different modes use different mechanisms:
        // - Specific: a bounded specificity bonus on upstream relevance — favors
        //   outliers without shrinking linked scores relative to isolated candidates.
        // - Broad: graph-adjacency-based cluster boost, gated by dominance —
        //   only fires when the candidate set is dominated by one cluster
        //   (i.e., the query is unambiguously about that topic). The spectral
        //   low-pass filter doesn't work here: it converges scores to cluster
        //   mean, which is structurally below ExpandWithGraph's pre-assigned
        //   scores, so the lift never beats lexical false positives.
        if (resolved == SpectralRetrievalMode.Broad)
            return ApplyBroadModeClusterBoost(ns, candidates, k, expandGraph, states);

        // Specific mode retains the original candidates and relevance score scale.
        var byId = new Dictionary<string, CognitiveSearchResult>(candidates.Count);
        foreach (var c in candidates) byId[c.Id] = c;

        var scoreList = new List<(string Id, float Score)>(candidates.Count);
        foreach (var c in candidates) scoreList.Add((c.Id, c.Score));

        var reranked = _spectral.Rerank(ns, scoreList, resolved, tenantId: _access.TenantId, topK: candidates.Count);

        var output = new List<CognitiveSearchResult>(k);
        foreach (var (id, score) in reranked)
        {
            if (byId.TryGetValue(id, out var orig))
                output.Add(orig with { Score = score });
        }
        return output.Count > 0 ? output : candidates;
    }

    /// <summary>
    /// Broad-mode re-rank: detect whether the candidate top-K is dominated by
    /// one connected component of the graph. If yes (the query is unambiguously
    /// about that topic), boost every candidate in that component to at least
    /// <c>α * max-neighbor-score</c> so cluster mates can outrank lexical false
    /// positives from other topics. If no (top-K is split across clusters, the
    /// query is ambiguous), pass through unchanged — better to give the user
    /// the original distinct-topic ordering than to arbitrarily pick one.
    ///
    /// This combines two ideas from the panel synthesis: max-neighbor boost
    /// (compares cluster members to non-cluster competitors directly, not to
    /// cluster mean) and cluster-dominance detection (only boost when the
    /// query's intent is clear).
    /// </summary>
    private IReadOnlyList<CognitiveSearchResult> ApplyBroadModeClusterBoost(
        string ns, IReadOnlyList<CognitiveSearchResult> candidates, int k, bool expandGraph, HashSet<string> states)
    {
        if (candidates.Count == 0) return candidates;

        // Assign each candidate to its connected component within the
        // candidate set itself — we only consider edges between candidates,
        // so an entry whose graph neighbors aren't in the pool is in a
        // singleton component.
        // A summary is a navigation hub, not independent evidence that all of its
        // sources describe one query-relevant topic. Keep summaries selectable at
        // their retrieval scores, but infer dominance from source-to-source links.
        var componentOf = AssignComponentsWithinCandidates(candidates.Where(c => !c.IsSummaryNode).ToList());
        if (componentOf.Count == 0) return candidates;

        // Find the dominant component among the top-K candidates by score.
        var topK = candidates.OrderByDescending(c => c.Score).Take(Math.Min(k, candidates.Count));
        var counts = new Dictionary<int, int>();
        foreach (var c in topK)
            if (componentOf.TryGetValue(c.Id, out var comp))
                counts[comp] = counts.TryGetValue(comp, out var existing) ? existing + 1 : 1;

        if (counts.Count == 0) return candidates;

        int dominantComponent = -1;
        int dominantCount = 0;
        foreach (var kv in counts)
        {
            if (kv.Value > dominantCount)
            {
                dominantComponent = kv.Key;
                dominantCount = kv.Value;
            }
        }

        // Require strict majority of top-K (not just plurality) to call a
        // cluster dominant. With k=5 that's >= 3 entries from the same
        // component. Below this threshold the query is ambiguous and we
        // pass through.
        int threshold = (k / 2) + 1;
        if (dominantCount < threshold) return candidates;

        // Apply max-neighbor boost to every candidate in the dominant
        // component. The boost uses graph neighbors that are also in the
        // candidate pool, taking the highest score among them × discount.
        // The discount keeps original top hits stably ranked.
        var byId = new Dictionary<string, CognitiveSearchResult>(candidates.Count);
        foreach (var c in candidates) byId[c.Id] = c;

        // Find the maximum score within the dominant cluster — used both for
        // boosting candidates in the cluster and for setting the score of
        // newly-surfaced cluster members not yet in the candidate pool.
        float clusterMaxScore = 0f;
        foreach (var c in candidates)
        {
            if (!componentOf.TryGetValue(c.Id, out var comp) || comp != dominantComponent) continue;
            if (c.Score > clusterMaxScore) clusterMaxScore = c.Score;
        }
        float clusterBoostedScore = clusterMaxScore * BroadMaxNeighborDiscount;

        var output = new List<CognitiveSearchResult>(candidates.Count);
        var seen = new HashSet<string>(candidates.Count);
        foreach (var c in candidates)
        {
            if (!componentOf.TryGetValue(c.Id, out var comp) || comp != dominantComponent)
            {
                output.Add(c);
                seen.Add(c.Id);
                continue;
            }

            float final = Math.Max(c.Score, clusterBoostedScore);
            output.Add(c with { Score = final });
            seen.Add(c.Id);
        }

        // Surface dominant-cluster members that weren't in the candidate pool.
        // BFS the full graph (not restricted to candidates) starting from any
        // candidate in the dominant component; every reachable id we haven't
        // already seen is a cluster member that BM25/ANN missed and that
        // graph expansion didn't reach. Surface them at the cluster's
        // boosted score so they can compete for the top-K.
        var clusterMember = candidates.FirstOrDefault(c =>
            componentOf.TryGetValue(c.Id, out var comp) && comp == dominantComponent);
        if (expandGraph && clusterMember is not null)
        {
            // This BFS reads bare-id adjacency at every hop, so the guard belongs on each node
            // whose neighbors we are about to read, not only on the root: a walk that starts
            // unambiguous can still reach a node shared with an invisible twin and pull that
            // twin's component in behind it. One sweep for the walk.
            var topology = BareIdTopology.ForSweep(_index, tenantId: _access.TenantId);
            var queue = new Queue<string>();
            var fullClusterSeen = new HashSet<string> { clusterMember.Id };
            queue.Enqueue(clusterMember.Id);
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!topology.IsTopologySafe(id)) continue;
                var neighbors = _graph.GetNeighbors(id, relation: null, direction: "both", tenantId: _access.TenantId);
                foreach (var n in neighbors.Neighbors)
                {
                    if (fullClusterSeen.Contains(n.Entry.Id)) continue;
                    fullClusterSeen.Add(n.Entry.Id);
                    if (!_access.CanRead(n.Entry.Namespace)) continue;
                    var entry = _index.Get(n.Entry.Id, n.Entry.Namespace, tenantId: _access.TenantId);
                    // This also covers summaries outside the original candidate pool:
                    // a source cluster must not spread through an overview into another.
                    if (entry is null || entry.IsSummaryNode) continue;
                    queue.Enqueue(n.Entry.Id);

                    if (seen.Contains(n.Entry.Id)) continue;
                    if (entry.Ns != ns || !states.Contains(entry.LifecycleState)) continue;
                    output.Add(new CognitiveSearchResult(
                        entry.Id, entry.Text, clusterBoostedScore, entry.LifecycleState,
                        entry.ActivationEnergy, entry.Category,
                        entry.Metadata.Count > 0 ? new Dictionary<string, string>(entry.Metadata) : null,
                        entry.IsSummaryNode, entry.SourceClusterId, entry.AccessCount));
                    seen.Add(n.Entry.Id);
                }
            }
        }

        output.Sort((a, b) => b.Score.CompareTo(a.Score));
        return output;
    }

    /// <summary>
    /// Group candidates into connected components considering only graph edges
    /// where both endpoints are in the candidate set. Returns id -&gt; component-index.
    /// </summary>
    private Dictionary<string, int> AssignComponentsWithinCandidates(
        IReadOnlyList<CognitiveSearchResult> candidates)
    {
        var candidateIds = new HashSet<string>(candidates.Count);
        foreach (var c in candidates) candidateIds.Add(c.Id);

        var componentOf = new Dictionary<string, int>(candidates.Count);
        int nextComponent = 0;

        // Components are built from bare-id adjacency, so a node the tenant holds in two
        // namespaces would merge two candidates through an edge that belongs to an invisible
        // twin — and the dominant component that comes out of this drives which entries get
        // boosted and which extra ones get surfaced. An unattributable node stays a singleton.
        var topology = BareIdTopology.ForSweep(_index, tenantId: _access.TenantId);

        foreach (var c in candidates)
        {
            if (componentOf.ContainsKey(c.Id)) continue;

            // BFS from this candidate, following edges that stay inside the pool.
            var queue = new Queue<string>();
            queue.Enqueue(c.Id);
            componentOf[c.Id] = nextComponent;

            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!topology.IsTopologySafe(id)) continue;
                var neighbors = _graph.GetNeighbors(id, relation: null, direction: "both", tenantId: _access.TenantId);
                foreach (var n in neighbors.Neighbors)
                {
                    if (!candidateIds.Contains(n.Entry.Id)) continue;
                    if (componentOf.ContainsKey(n.Entry.Id)) continue;
                    componentOf[n.Entry.Id] = nextComponent;
                    queue.Enqueue(n.Entry.Id);
                }
            }
            nextComponent++;
        }

        return componentOf;
    }

    /// <summary>
    /// Discount factor on max-neighbor score for the broad-mode cluster boost.
    /// Slightly below 1.0 so the original top hit in a cluster stays ahead of
    /// its boosted peers — preserves the existing best-match-first ranking
    /// within a cluster while lifting cluster mates above non-cluster competitors.
    /// </summary>
    private const float BroadMaxNeighborDiscount = 0.95f;

    /// <summary>
    /// Local heuristic to pick a spectral mode from a query string. No external
    /// LLM or embedding calls — runs in microseconds inline. The rule:
    ///
    /// - Queries with explicit precision markers (digits, quoted phrases) lean
    ///   <see cref="SpectralRetrievalMode.Specific"/>: the user is asking about a
    ///   particular value or exact phrase, surface outliers within the cluster.
    /// - Queries with 5 or more words also lean Specific: longer queries usually
    ///   carry enough disambiguating context that the user wants the precise entry.
    /// - Otherwise <see cref="SpectralRetrievalMode.Broad"/>: short queries are
    ///   typically conceptual ("memory consolidation", "auth flow"), surface
    ///   the cluster they belong to.
    /// </summary>
    public static SpectralRetrievalMode InferSpectralMode(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return SpectralRetrievalMode.Broad;

        // Precision markers: digits or quoted phrases push toward Specific.
        bool hasDigit = false;
        bool hasQuote = false;
        foreach (var ch in query)
        {
            if (char.IsDigit(ch)) { hasDigit = true; break; }
        }
        if (!hasDigit)
        {
            for (int i = 0; i < query.Length; i++)
            {
                if (query[i] == '"' || query[i] == '\'')
                {
                    // Need a matching closer to count as a quoted phrase.
                    if (query.IndexOf(query[i], i + 1) > i) { hasQuote = true; break; }
                }
            }
        }
        if (hasDigit || hasQuote) return SpectralRetrievalMode.Specific;

        var words = query.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 5 ? SpectralRetrievalMode.Specific : SpectralRetrievalMode.Broad;
    }
}

// ── Composite tool result models ──

public sealed record RememberResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ns")] string Namespace,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions,
    [property: JsonPropertyName("duplicateWarnings")] IReadOnlyList<DuplicateWarning>? DuplicateWarnings = null);

public sealed record DuplicateWarning(
    [property: JsonPropertyName("existingId")] string ExistingId,
    [property: JsonPropertyName("existingText")] string? ExistingText,
    [property: JsonPropertyName("similarity")] float Similarity);

public sealed record RecallResult(
    [property: JsonPropertyName("strategy")] string Strategy,
    [property: JsonPropertyName("ns")] string? Namespace,
    [property: JsonPropertyName("results")] IReadOnlyList<CognitiveSearchResult> Results,
    [property: JsonPropertyName("routingInfo")] string? RoutingInfo = null)
{
    [JsonPropertyName("queryEvidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RecallQueryEvidenceResult? QueryEvidence { get; init; }
    // Additive diagnostic; preserve the existing constructor/deconstruction and omit
    // it entirely for ordinary results. A missing diagnostic does not certify truth.
    [JsonPropertyName("conflicts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RecallConflict>? Conflicts { get; init; }
}

public sealed record RecallQueryEvidenceResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message);

public sealed record RecallConflict(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("basis")] string Basis,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("evidence")] IReadOnlyList<RecallConflictEvidence> Evidence)
{
    [JsonPropertyName("resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RecallConflictResolution? Resolution { get; init; }
}

public sealed record RecallConflictResolution(
    [property: JsonPropertyName("basis")] string Basis,
    [property: JsonPropertyName("preferredId")] string PreferredId,
    [property: JsonPropertyName("ns")] string Namespace,
    [property: JsonPropertyName("knowledgeId")] string KnowledgeId,
    [property: JsonPropertyName("knowledgeVersion")] string KnowledgeVersion,
    [property: JsonPropertyName("knowledgeContentHash")] string KnowledgeContentHash,
    [property: JsonPropertyName("provenanceId")] string ProvenanceId,
    [property: JsonPropertyName("sources")] IReadOnlyList<RecallResolutionSource> Sources);

public sealed record RecallResolutionSource(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ns")] string Namespace,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("contentHash")] string ContentHash);

public sealed record RecallConflictEvidence(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ns")] string Namespace,
    [property: JsonPropertyName("text")] string Text);

public sealed record ReflectResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ns")] string Namespace,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions,
    [property: JsonPropertyName("relatedReflections")] IReadOnlyList<CognitiveSearchResult>? RelatedReflections = null);

public sealed record ContextBlockResult(
    [property: JsonPropertyName("ns")] string Namespace,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("stableMemories")] IReadOnlyList<StableMemoryEntry> StableMemories,
    [property: JsonPropertyName("header")] NamespaceHeader? Header = null,
    [property: JsonPropertyName("cacheGuidance")] string? CacheGuidance = null);

public sealed record StableMemoryEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("metadata")] Dictionary<string, string>? Metadata = null);

public sealed record NamespaceHeader(
    [property: JsonPropertyName("stmCount")] int StmCount,
    [property: JsonPropertyName("ltmCount")] int LtmCount,
    [property: JsonPropertyName("archivedCount")] int ArchivedCount,
    [property: JsonPropertyName("stableBlockSize")] int StableBlockSize);
