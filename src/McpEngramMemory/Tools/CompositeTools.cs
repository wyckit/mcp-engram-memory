using System.ComponentModel;
using System.Text.Json.Serialization;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Lifecycle;
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

    public CompositeTools(
        CognitiveIndex index, IEmbeddingService embedding, KnowledgeGraph graph,
        LifecycleEngine lifecycle, ExpertDispatcher dispatcher, MetricsCollector metrics)
    {
        _index = index;
        _embedding = embedding;
        _graph = graph;
        _lifecycle = lifecycle;
        _dispatcher = dispatcher;
        _metrics = metrics;
    }

    [McpServerTool(Name = "remember")]
    [Description("Intelligent store: saves a memory with auto-generated embedding, duplicate detection, and auto-linking to related existing memories. Use this instead of store_memory + detect_duplicates + link_memories.")]
    public object Remember(
        [Description("Unique identifier for this memory (kebab-case recommended).")] string id,
        [Description("Namespace (e.g. project directory name, 'work', 'synthesis').")] string ns,
        [Description("The memory text to store and embed.")] string text,
        [Description("Category: 'decision', 'pattern', 'bug-fix', 'architecture', 'preference', 'lesson', 'reference', 'retrospective'.")] string? category = null,
        [Description("Optional metadata as key-value pairs.")] Dictionary<string, string>? metadata = null,
        [Description("Lifecycle state: 'stm' (default) or 'ltm' for stable knowledge.")] string? lifecycleState = null)
    {
        using var timer = _metrics.StartTimer("remember");
        var state = lifecycleState ?? "stm";
        var actions = new List<string>();

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

    [McpServerTool(Name = "recall")]
    [Description("Intelligent search: auto-routes to the best retrieval strategy. Searches the given namespace with hybrid+graph expansion, falls back to deep_recall for archived memories, and routes to expert namespaces when no namespace is specified. Use this instead of search_memory / deep_recall / dispatch_task.")]
    public object Recall(
        [Description("What to search for.")] string query,
        [Description("Namespace to search (omit to auto-route via expert dispatcher).")] string? ns = null,
        [Description("Maximum results (default: 5).")] int k = 5,
        [Description("Minimum similarity score (default: 0.3).")] float minScore = 0.3f)
    {
        using var timer = _metrics.StartTimer("recall");
        var vector = _embedding.Embed(query);
        var strategy = "direct";

        // Strategy 1: If namespace provided, search directly with hybrid + graph expansion
        if (ns is not null)
        {
            var states = new HashSet<string> { "stm", "ltm" };
            var results = _index.HybridSearch(vector, query, ns, k, minScore, rerank: true);

            // Record access for returned entries
            foreach (var r in results)
                _index.RecordAccess(r.Id, ns);

            // Expand with graph neighbors
            var expanded = ExpandWithGraph(results, states);

            // Fallback: if poor results, try deep_recall
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

            return new RecallResult(strategy, ns, expanded.Take(k).ToList());
        }

        // Strategy 2: No namespace — auto-route via expert dispatcher
        var (status, experts) = _dispatcher.Route(vector, topK: 3, threshold: 0.7f);

        if (status == "routed" && experts.Count > 0)
        {
            var bestExpert = experts[0];
            _dispatcher.RecordDispatch(bestExpert.ExpertId);

            var expertResults = _index.HybridSearch(
                vector, query, bestExpert.TargetNamespace, k, minScore, rerank: true);

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

    [McpServerTool(Name = "reflect")]
    [Description("Store a lesson learned or retrospective with auto-linking to related memories. Use this at the end of work sessions to capture what went well, what went wrong, and key decisions. Auto-links to referenced memories and promotes stable knowledge.")]
    public object Reflect(
        [Description("The lesson or reflection text. Be specific about what happened and what was learned.")] string text,
        [Description("Namespace (project directory name).")] string ns,
        [Description("Brief topic identifier for the reflection (e.g. 'architecture-decomposition', 'dll-lock-debugging').")] string topic,
        [Description("IDs of specific memories this reflection relates to (auto-linked).")] string[]? relatedIds = null)
    {
        using var timer = _metrics.StartTimer("reflect");
        var actions = new List<string>();

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
