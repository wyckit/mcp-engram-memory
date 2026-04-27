using System.ComponentModel;
using System.Text.Json.Serialization;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
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

    public CompositeTools(
        CognitiveIndex index, IEmbeddingService embedding, KnowledgeGraph graph,
        LifecycleEngine lifecycle, ExpertDispatcher dispatcher, MetricsCollector metrics,
        SpectralRetrievalReranker spectral)
    {
        _index = index;
        _embedding = embedding;
        _graph = graph;
        _lifecycle = lifecycle;
        _dispatcher = dispatcher;
        _metrics = metrics;
        _spectral = spectral;
    }

    [McpServerTool(Name = "remember")]
    [Description("Default way to store memories. Auto-embeds text, blocks near-duplicates, and links to related entries. Use this instead of store_memory unless you need raw vector control.")]
    public object Remember(
        [Description("Unique identifier for this memory (kebab-case recommended).")] string id,
        [Description("Namespace (e.g. project directory name, 'work', 'synthesis').")] string ns,
        [Description("The memory text to store and embed.")] string text,
        [Description("Category: 'decision', 'pattern', 'bug-fix', 'architecture', 'preference', 'lesson', 'reference', 'retrospective'.")] string? category = null,
        [Description("Optional metadata as key-value pairs.")] Dictionary<string, string>? metadata = null,
        [Description("Lifecycle state: 'stm' (default) or 'ltm' for stable knowledge.")] string? lifecycleState = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return "Error: id must not be empty.";
        if (string.IsNullOrWhiteSpace(ns)) return "Error: ns must not be empty.";
        if (string.IsNullOrWhiteSpace(text)) return "Error: text must not be empty.";

        using var timer = _metrics.StartTimer("remember");
        var state = lifecycleState ?? "stm";
        var actions = new List<string>();

        try
        {
        // 1. Embed with contextual prefix
        var prefix = BenchmarkRunner.BuildContextualPrefix(ns, category);
        var vector = _embedding.Embed(prefix + text);

        // 2. Check for near-duplicates BEFORE storing (search by vector similarity)
        var existing = _index.Search(vector, ns, k: 3, minScore: 0.90f);
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
        var entry = new CognitiveEntry(id, vector, ns, text, category, metadata, state);
        _index.Upsert(entry);
        actions.Add("stored");

        // 4. Find related memories and auto-link (use pre-store search results + fresh search)
        var related = existing.Count > 0 ? existing : _index.Search(vector, ns, k: 5, minScore: 0.65f);
        var links = new List<string>();
        foreach (var result in related)
        {
            if (result.Id == id) continue;
            if (result.IsSummaryNode) continue;
            if (result.Score < 0.65f) continue;

            var relation = result.Score >= 0.85f ? "similar_to" : "cross_reference";
            _graph.AddEdge(new GraphEdge(id, result.Id, relation));
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

    [McpServerTool(Name = "recall")]
    [Description("Default search tool. Searches a namespace with hybrid+graph expansion and falls back to deep_recall for archived entries. Omit namespace to auto-route via expert dispatcher. Use search_memory only for low-level queries without fallback.")]
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
        if (ns is not null)
        {
            var states = new HashSet<string> { "stm", "ltm" };
            IReadOnlyList<CognitiveSearchResult> results = hybrid
                ? _index.HybridSearch(vector, query, ns, k, minScore, rerank: rerank)
                : (rerank
                    ? _index.Rerank(query, _index.Search(vector, ns, k * 2, minScore, summaryFirst: summaryFirst)).Take(k).ToList()
                    : _index.Search(vector, ns, k, minScore, summaryFirst: summaryFirst));

            // Expand with graph neighbors
            var expanded = expandGraph ? ExpandWithGraph(results, states) : results;

            // Fallback FIRST: if hybrid produced poor scores, swap in deep_recall
            // before spectral re-ranking. Otherwise spectral runs on the low-score
            // expansion and gets discarded when fallback overrides it.
            if (results.Count == 0 || (results.Count > 0 && results[0].Score < 0.5f))
            {
                var deepResults = _lifecycle.DeepRecall(vector, ns, k, minScore: 0.3f, resurrectionThreshold: 0.7f);
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
                _index.RecordAccess(r.Id, ns);

            return new RecallResult(strategy, ns, finalResults);
        }

        // Strategy 2: No namespace — auto-route via expert dispatcher
        var (status, experts) = _dispatcher.Route(vector, topK: 3, threshold: 0.7f);

        if (status == "routed" && experts.Count > 0)
        {
            var bestExpert = experts[0];
            _dispatcher.RecordDispatch(bestExpert.ExpertId);

            IReadOnlyList<CognitiveSearchResult> expertResults = hybrid
                ? _index.HybridSearch(vector, query, bestExpert.TargetNamespace, k, minScore, rerank: rerank)
                : _index.Search(vector, bestExpert.TargetNamespace, k, minScore, summaryFirst: summaryFirst);

            foreach (var r in expertResults)
                _index.RecordAccess(r.Id, bestExpert.TargetNamespace);

            return new RecallResult("expert_routed", bestExpert.TargetNamespace, expertResults.ToList(),
                $"Routed to expert '{bestExpert.ExpertId}' ({bestExpert.TargetNamespace})");
        }

        // Strategy 3: No expert match — search all known namespaces
        var allResults = new List<CognitiveSearchResult>();
        var namespaces = _index.GetNamespaces();
        foreach (var searchNs in namespaces)
        {
            if (searchNs.StartsWith("_system") || searchNs.StartsWith("active-debate")) continue;
            var nsResults = _index.Search(vector, searchNs, k: 3, minScore: minScore);
            allResults.AddRange(nsResults);
        }

        var sorted = allResults.OrderByDescending(r => r.Score).Take(k).ToList();
        return new RecallResult("broadcast", null, sorted,
            $"Searched {namespaces.Count} namespace(s), no expert match");
        }
        catch (ArgumentException ex) { return $"Error: {ex.Message}"; }
        catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "reflect")]
    [Description("Store a lesson or retrospective as LTM with auto-linking. Use at the end of work sessions to capture what went well, what went wrong, and key decisions.")]
    public object Reflect(
        [Description("The lesson or reflection text. Be specific about what happened and what was learned.")] string text,
        [Description("Namespace (project directory name).")] string ns,
        [Description("Brief topic identifier for the reflection (e.g. 'architecture-decomposition', 'dll-lock-debugging').")] string topic,
        [Description("IDs of specific memories this reflection relates to (auto-linked).")] string[]? relatedIds = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Error: text must not be empty.";
        if (string.IsNullOrWhiteSpace(ns)) return "Error: ns must not be empty.";
        if (string.IsNullOrWhiteSpace(topic)) return "Error: topic must not be empty.";

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
            category: "lesson");
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
            lifecycleState: "ltm");
        _index.Upsert(entry);
        actions.Add("stored as ltm lesson");

        // 4. Auto-link to explicitly referenced memories
        if (relatedIds is { Length: > 0 })
        {
            foreach (var relatedId in relatedIds)
            {
                if (_index.Get(relatedId) is not null)
                {
                    _graph.AddEdge(new GraphEdge(id, relatedId, "elaborates"));
                    actions.Add($"linked to {relatedId}");
                }
            }
        }

        // 5. Auto-link to semantically related memories
        var related = _index.Search(vector, ns, k: 5, minScore: 0.7f);
        int autoLinked = 0;
        foreach (var r in related)
        {
            if (r.Id == id) continue;
            if (r.IsSummaryNode) continue;
            if (relatedIds is not null && relatedIds.Contains(r.Id)) continue;
            if (r.Score < 0.7f) continue;

            _graph.AddEdge(new GraphEdge(id, r.Id, "cross_reference"));
            autoLinked++;
        }
        if (autoLinked > 0)
            actions.Add($"auto-linked to {autoLinked} related memor{(autoLinked == 1 ? "y" : "ies")}");

        // 6. Search for past reflections to surface patterns
        var pastReflections = _index.Search(vector, ns, k: 3, minScore: 0.6f,
            category: "lesson")
            .Where(r => r.Id != id)
            .ToList();

        return new ReflectResult("stored", id, ns,
            $"Reflected on '{topic}'. Actions: {string.Join(", ", actions)}.",
            actions, pastReflections.Count > 0 ? pastReflections : null);
        }
        catch (ArgumentException ex) { return $"Error: {ex.Message}"; }
        catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
    }

    [McpServerTool(Name = "get_context_block")]
    [Description("Returns a cache-optimized memory context block for a namespace. Stable LTM memories sorted by ID (deterministic ordering for prompt caching). Place this block as a stable prefix in your context to benefit from prompt caching across turns.")]
    public object GetContextBlock(
        [Description("Namespace to build context block from.")] string ns,
        [Description("Maximum number of LTM memories to include (default: 20).")] int maxEntries = 20,
        [Description("Minimum access count to qualify as 'stable' (default: 2).")] int minAccessCount = 2,
        [Description("Include namespace statistics header (default: true).")] bool includeHeader = true)
    {
        using var timer = _metrics.StartTimer("get_context_block");

        // Get all LTM entries (these are the most stable memories worth caching)
        var allEntries = _index.GetAllInNamespace(ns);
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
        var (stm, ltm, archived) = _index.GetStateCounts(ns);

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

        foreach (var result in results)
        {
            var neighbors = _graph.GetNeighbors(result.Id);
            foreach (var neighbor in neighbors.Neighbors)
            {
                if (existingIds.Contains(neighbor.Entry.Id)) continue;
                if (!states.Contains(neighbor.Entry.LifecycleState)) continue;

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

        var reranked = _spectral.Rerank(ns, scoreList, resolved, k * 3);

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
            var queue = new Queue<string>();
            var fullClusterSeen = new HashSet<string> { clusterMember.Id };
            queue.Enqueue(clusterMember.Id);
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                var neighbors = _graph.GetNeighbors(id, direction: "both");
                foreach (var n in neighbors.Neighbors)
                {
                    if (fullClusterSeen.Contains(n.Entry.Id)) continue;
                    fullClusterSeen.Add(n.Entry.Id);
                    queue.Enqueue(n.Entry.Id);

                    if (seen.Contains(n.Entry.Id)) continue;
                    var entry = _index.Get(n.Entry.Id, ns);
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
                var neighbors = _graph.GetNeighbors(id, direction: "both");
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
