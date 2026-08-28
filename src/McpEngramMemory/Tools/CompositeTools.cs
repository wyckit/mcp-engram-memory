using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Graph;
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
    private readonly LifecycleEngine _lifecycle;
    private readonly ExpertDispatcher _dispatcher;
    private readonly MetricsCollector _metrics;
    private readonly SpectralRetrievalReranker _spectral;
    private readonly NamespaceAccess _access;

    [ActivatorUtilitiesConstructor]
    public CompositeTools(
        CognitiveIndex index, IEmbeddingService embedding, KnowledgeGraph graph,
        LifecycleEngine lifecycle, ExpertDispatcher dispatcher, MetricsCollector metrics,
        SpectralRetrievalReranker spectral,
        NamespaceAccess access)
    {
        _index = index;
        _embedding = embedding;
        _graph = graph;
        _lifecycle = lifecycle;
        _dispatcher = dispatcher;
        _metrics = metrics;
        _spectral = spectral;
        _access = access;
    }

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
        var topology = BareIdTopology.ForSweep(_index, _access.TenantId);
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

    [McpServerTool(Name = "recall", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Find memories in one namespace (or auto-route across all) — the default search for any retrieval task. Don't use it to search across multiple specific namespaces simultaneously; use `cross_search` instead.")]
    public object Recall(
        [Description("What to search for.")] string query,
        [Description("Namespace to search (omit to auto-route via expert dispatcher).")] string? ns = null,
        [Description("Maximum results (default: 5).")] int k = 5,
        [Description("Minimum similarity score (default: 0.3).")] float minScore = 0.3f,
        [Description("Use hybrid BM25+vector search (default: true).")] bool hybrid = true,
        [Description("Apply token-level reranking (default: true).")] bool rerank = true,
        [Description("Prioritize cluster summaries in results (default: false).")] bool summaryFirst = false,
        [Description("Include graph-connected neighbors in results (default: true).")] bool expandGraph = true,
        [Description("Spectral re-ranking mode applied to the candidate set after standard retrieval+graph expansion. 'auto' (default): infer from query — short conceptual queries get broad, longer specific queries get specific; runs entirely from a local word-count heuristic, no extra LLM/embedding calls. 'broad': low-pass filter, boosts cluster-supported memories. 'specific': high-pass filter, boosts entries that score high relative to their cluster. 'none': skip spectral re-ranking. Only applied when a namespace is provided; gracefully degrades to passthrough on namespaces without a qualifying diffusion basis.")] string spectralMode = "auto")
    {
        if (string.IsNullOrWhiteSpace(query)) return "Error: query must not be empty.";
        if (k <= 0) return "Error: k must be positive.";

        using var timer = _metrics.StartTimer("recall");
        float[] vector;
        try { vector = _embedding.Embed(query); }
        catch (ArgumentException ex) { return $"Error: {ex.Message}"; }
        catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
        var strategy = "direct";

        try
        {

        // Strategy 1: If namespace provided, search directly with optional hybrid + graph expansion
        if (ns is not null && !_access.CanRead(ns))
            return new RecallResult("direct", ns, new List<CognitiveSearchResult>(),
                $"No accessible memories in namespace '{ns}'.");

        if (ns is not null)
        {
            var states = new HashSet<string> { "stm", "ltm" };
            IReadOnlyList<CognitiveSearchResult> results = hybrid
                ? _index.HybridSearch(vector, query, ns, tenantId: _access.TenantId,
                    k: k, minScore: minScore, rerank: rerank)
                : (rerank
                    ? _index.Rerank(query, _index.Search(vector, ns, tenantId: _access.TenantId,
                        k: k * 2, minScore: minScore, summaryFirst: summaryFirst)).Take(k).ToList()
                    : _index.Search(vector, ns, tenantId: _access.TenantId,
                        k: k, minScore: minScore, summaryFirst: summaryFirst));

            // Expand with graph neighbors
            var expanded = expandGraph
                ? ExpandWithGraph(results, states)
                : results;

            // Fallback FIRST: if hybrid produced poor scores, swap in deep_recall
            // before spectral re-ranking. Otherwise spectral runs on the low-score
            // expansion and gets discarded when fallback overrides it.
            if (results.Count == 0 || (results.Count > 0 && results[0].Score < 0.5f))
            {
                // DeepRecall resurrects high-scoring archived entries to STM as a side effect of
                // reading them, so the fallback carries a write on a path the caller only ever
                // asked to read on. CanRead legitimately authorizes seeing archived text, so the
                // gate goes on the mutation and not on the call: a read-only grantee gets the
                // same rows, scores and order, and only the reported LifecycleState changes —
                // to the truth, since nothing was promoted.
                var deepResults = _lifecycle.DeepRecall(vector, ns, tenantId: _access.TenantId,
                    k: k, minScore: 0.3f, resurrectionThreshold: 0.7f, resurrect: _access.CanWrite(ns));
                if (deepResults.Count > results.Count ||
                    (deepResults.Count > 0 && (results.Count == 0 || deepResults[0].Score > results[0].Score)))
                {
                    strategy = "deep_recall";
                    expanded = deepResults;
                }
            }

            // Optional spectral re-ranking on whatever candidate set we ended up
            // with (post-graph-expansion or post-deep_recall fallback). Restricted
            // to entries already in the candidate pool for Specific mode; Broad
            // mode applies a cluster-dominance-gated max-neighbor boost.
            expanded = ApplySpectralRerankRestricted(ns, expanded, spectralMode, query, k);

            // Record access for actually-returned entries (after spectral
            // re-ranking, since that may have reshaped the top-K).
            var finalResults = expanded.Take(k).ToList();
            foreach (var r in finalResults)
                _index.RecordAccess(r.Id, ns, _access.TenantId);

            return new RecallResult(strategy, ns, finalResults);
        }

        // Strategy 2: No namespace — auto-route via expert dispatcher
        var (status, experts) = _access.IsLegacyUnisolated
            ? _dispatcher.Route(vector, topK: 3, threshold: 0.7f)
            : ("needs_expert", (IReadOnlyList<ExpertMatch>)Array.Empty<ExpertMatch>());

        if (status == "routed" && experts.Count > 0)
        {
            // Routing metadata is global, but expert evidence is not. Select the best
            // candidate the current principal may read; if none is accessible, fall
            // through to the already-filtered broadcast path without revealing that a
            // private expert namespace exists.
            var bestExpert = experts.FirstOrDefault(e => _access.CanRead(e.TargetNamespace));
            if (bestExpert is not null)
            {
                _dispatcher.RecordDispatch(bestExpert.ExpertId);

                IReadOnlyList<CognitiveSearchResult> expertResults = hybrid
                    ? _index.HybridSearch(vector, query, bestExpert.TargetNamespace,
                        tenantId: _access.TenantId, k: k, minScore: minScore, rerank: rerank)
                    : _index.Search(vector, bestExpert.TargetNamespace, tenantId: _access.TenantId,
                        k: k, minScore: minScore, summaryFirst: summaryFirst);

                foreach (var r in expertResults)
                    _index.RecordAccess(r.Id, bestExpert.TargetNamespace, _access.TenantId);

                return new RecallResult("expert_routed", bestExpert.TargetNamespace, expertResults.ToList(),
                    $"Routed to expert '{bestExpert.ExpertId}' ({bestExpert.TargetNamespace})");
            }
        }

        // Strategy 3: No expert match — search all known namespaces
        var allResults = new List<CognitiveSearchResult>();
        // The broadcast path searches the entire store, so without a filter it is the widest
        // possible read: one call returns hits from every namespace regardless of ownership.
        var namespaces = _index.GetNamespaces(_access.TenantId).Where(_access.CanRead).ToList();
        foreach (var searchNs in namespaces)
        {
            if (searchNs.StartsWith("_system") || searchNs.StartsWith("active-debate")) continue;
            var nsResults = _index.Search(vector, searchNs, k: 3, minScore: minScore,
                tenantId: _access.TenantId);
            allResults.AddRange(nsResults);
        }

        var sorted = allResults.OrderByDescending(r => r.Score).Take(k).ToList();
        return new RecallResult("broadcast", null, sorted,
            $"Searched {namespaces.Count} namespace(s), no expert match");
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
        [Description("Include namespace statistics header (default: true).")] bool includeHeader = true)
    {
        using var timer = _metrics.StartTimer("get_context_block");

        if (!_access.CanRead(ns))
            return NamespaceAccess.ReadDenied(ns);

        // Get all LTM entries (these are the most stable memories worth caching)
        var allEntries = _index.GetAllInNamespace(ns, _access.TenantId);
        var stableEntries = allEntries
            .Where(e => e.LifecycleState == "ltm" && e.AccessCount >= minAccessCount && !e.IsSummaryNode)
            .OrderBy(e => e.Id) // Deterministic ordering by ID — critical for cache stability
            .Take(maxEntries)
            .ToList();

        // Build the stable block — exclude volatile fields (scores, timestamps, access counts)
        // that would change between calls and invalidate the cache
        var stableBlock = stableEntries.Select(e => new StableMemoryEntry(
            e.Id, e.Text, e.Category,
            e.Metadata.Count > 0 ? e.Metadata : null)).ToList();

        // Build header with namespace metadata (also stable)
        var (stm, ltm, archived) = _index.GetStateCounts(ns, _access.TenantId);

        return new ContextBlockResult(
            ns,
            $"{ns}:{ltm}:{stableEntries.Count}", // Changes only when LTM count changes
            stableBlock,
            includeHeader ? new NamespaceHeader(stm, ltm, archived, stableEntries.Count) : null,
            "Place this block as a stable prefix in your system prompt. " +
                "Append dynamic query results after this block. " +
                "The version field changes when the stable block content changes — " +
                "cache is valid while version is unchanged.");
    }

    private IReadOnlyList<CognitiveSearchResult> ExpandWithGraph(
        IReadOnlyList<CognitiveSearchResult> results, HashSet<string> states)
    {
        if (results.Count == 0) return results;

        var existingIds = results.Select(r => r.Id).ToHashSet();
        var expanded = new List<CognitiveSearchResult>(results);
        float lowestScore = results.Min(r => r.Score);

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

                existingIds.Add(neighbor.Entry.Id);
                expanded.Add(new CognitiveSearchResult(
                    neighbor.Entry.Id, neighbor.Entry.Text, lowestScore * 0.8f,
                    neighbor.Entry.LifecycleState, 0f,
                    neighbor.Entry.Category, null, false, null, 0));
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
        int k)
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
        // - Specific: spectral high-pass via the diffusion kernel — suppresses
        //   cluster-mate noise from graph expansion when the query is precise.
        // - Broad: graph-adjacency-based cluster boost, gated by dominance —
        //   only fires when the candidate set is dominated by one cluster
        //   (i.e., the query is unambiguously about that topic). The spectral
        //   low-pass filter doesn't work here: it converges scores to cluster
        //   mean, which is structurally below ExpandWithGraph's pre-assigned
        //   scores, so the lift never beats lexical false positives.
        if (resolved == SpectralRetrievalMode.Broad)
            return ApplyBroadModeClusterBoost(ns, candidates, k);

        // Specific mode: spectral high-pass via the kernel.
        var byId = new Dictionary<string, CognitiveSearchResult>(candidates.Count);
        foreach (var c in candidates) byId[c.Id] = c;

        var scoreList = new List<(string Id, float Score)>(candidates.Count);
        foreach (var c in candidates) scoreList.Add((c.Id, c.Score));

        var reranked = _spectral.Rerank(ns, scoreList, resolved, tenantId: _access.TenantId, topK: k * 3);

        var output = new List<CognitiveSearchResult>(k);
        foreach (var (id, score) in reranked)
        {
            if (output.Count >= k) break;
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
        string ns, IReadOnlyList<CognitiveSearchResult> candidates, int k)
    {
        if (candidates.Count == 0) return candidates;

        // Assign each candidate to its connected component within the
        // candidate set itself — we only consider edges between candidates,
        // so an entry whose graph neighbors aren't in the pool is in a
        // singleton component.
        var componentOf = AssignComponentsWithinCandidates(candidates);
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
        if (clusterMember is not null)
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
                    queue.Enqueue(n.Entry.Id);

                    if (seen.Contains(n.Entry.Id)) continue;
                    var entry = _index.Get(n.Entry.Id, ns, tenantId: _access.TenantId);
                    if (entry is null) continue;
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
        if (output.Count > k) output = output.GetRange(0, k);
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
    [property: JsonPropertyName("routingInfo")] string? RoutingInfo = null);

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
