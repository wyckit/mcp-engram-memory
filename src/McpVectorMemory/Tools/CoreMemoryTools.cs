using System.ComponentModel;
using System.Diagnostics;
using McpVectorMemory.Core.Models;
using McpVectorMemory.Core.Services;
using ModelContextProtocol.Server;

namespace McpVectorMemory.Tools;

/// <summary>
/// MCP tools for core memory operations: store, search, delete (enhanced).
/// </summary>
[McpServerToolType]
public sealed class CoreMemoryTools
{
    private readonly CognitiveIndex _index;
    private readonly PhysicsEngine _physics;
    private readonly IEmbeddingService _embedding;
    private readonly MetricsCollector _metrics;

    public CoreMemoryTools(CognitiveIndex index, PhysicsEngine physics, IEmbeddingService embedding, MetricsCollector metrics)
    {
        _index = index;
        _physics = physics;
        _embedding = embedding;
        _metrics = metrics;
    }

    [McpServerTool(Name = "store_memory")]
    [Description("Store a cognitive memory entry with namespace isolation, categorical metadata, and lifecycle tracking. Entry starts in STM by default. Uses contextual prefix embedding by default to improve retrieval quality.")]
    public string StoreMemory(
        [Description("Unique identifier for this memory entry.")] string id,
        [Description("Namespace (e.g. 'work', 'personal').")] string ns,
        [Description("The original text the vector was derived from.")] string? text = null,
        [Description("The float vector embedding as an array of numbers.")] float[]? vector = null,
        [Description("Category within namespace (e.g. 'meeting-notes').")] string? category = null,
        [Description("Optional metadata as a JSON object with string keys and values.")] Dictionary<string, string>? metadata = null,
        [Description("Initial lifecycle state: 'stm' (default), 'ltm', or 'archived'.")] string? lifecycleState = null)
    {
        try
        {
            using var _ = _metrics.StartTimer("store");
            // Apply contextual prefix when embedding from text (not when vector is provided directly)
            var textToEmbed = text;
            if (vector is null && !string.IsNullOrWhiteSpace(text))
            {
                var prefix = BenchmarkRunner.BuildContextualPrefix(ns, category);
                textToEmbed = prefix + text;
            }
            var resolved = ResolveVector(vector, textToEmbed);
            var entry = new CognitiveEntry(id, resolved, ns, text, category, metadata,
                lifecycleState ?? "stm");
            _index.Upsert(entry);

            // Check for near-duplicates against just this entry (O(N) instead of O(N²))
            var duplicates = _index.FindDuplicatesForEntry(ns, id, threshold: 0.95f);
            if (duplicates.Count > 0)
            {
                var dupIds = duplicates.Select(d => d.IdA == id ? d.IdB : d.IdA);
                return $"Stored entry '{id}' ({resolved.Length}-dim vector) in namespace '{ns}'. WARNING: Near-duplicate(s) detected: [{string.Join(", ", dupIds)}]. Use detect_duplicates for details.";
            }

            return $"Stored entry '{id}' ({resolved.Length}-dim vector) in namespace '{ns}'.";
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "search_memory")]
    [Description("Namespace-scoped vector similarity search with lifecycle awareness, summary-first mode, and optional physics-based gravity re-ranking (slingshot output). Supports hybrid search (BM25+vector fusion) and token-level reranking. Set explain=true for full retrieval diagnostics.")]
    public object SearchMemory(
        [Description("Namespace to search.")] string ns,
        [Description("The original text to search for.")] string? text = null,
        [Description("The query vector embedding as an array of numbers.")] float[]? vector = null,
        [Description("Maximum number of results to return (default: 5).")] int k = 5,
        [Description("Minimum cosine-similarity score threshold (default: 0).")] float minScore = 0f,
        [Description("Filter by category within namespace.")] string? category = null,
        [Description("Comma-separated lifecycle states to include (default: 'stm,ltm').")] string? includeStates = null,
        [Description("Prioritize cluster summaries in results (default: false).")] bool summaryFirst = false,
        [Description("When true, return physics-based slingshot output (Asteroid + Sun) instead of flat list.")] bool usePhysics = false,
        [Description("When true, return detailed retrieval explanation with each result (cosine, physics, lifecycle breakdown).")] bool explain = false,
        [Description("When true, use hybrid search combining BM25 keyword matching with vector similarity via Reciprocal Rank Fusion.")] bool hybrid = false,
        [Description("When true, apply token-level reranking to improve precision on the top results.")] bool rerank = false)
    {
        using var timer = _metrics.StartTimer("search");

        float[] resolved;
        double embeddingMs;
        try
        {
            var embedSw = Stopwatch.StartNew();
            resolved = ResolveVector(vector, text);
            embedSw.Stop();
            embeddingMs = embedSw.Elapsed.TotalMilliseconds;
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }

        var states = includeStates is not null
            ? new HashSet<string>(includeStates.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            : new HashSet<string> { "stm", "ltm" };

        var searchSw = Stopwatch.StartNew();
        IReadOnlyList<CognitiveSearchResult> results;
        if (hybrid && text is not null)
        {
            results = _index.HybridSearch(resolved, text, ns, k, minScore, category, states, rerank);
        }
        else
        {
            results = _index.Search(resolved, ns, k, minScore, category, states, summaryFirst);
            if (rerank && text is not null && results.Count > 1)
                results = _index.Rerank(text, results);
        }
        searchSw.Stop();

        // Side effect: record access for returned entries (namespace-scoped for efficiency)
        foreach (var result in results)
            _index.RecordAccess(result.Id, ns);

        // When usePhysics, apply gravity re-ranking before explain or return
        IReadOnlyList<CognitiveSearchResult> orderedResults = results;
        SlingshotResult? slingshot = null;
        if (usePhysics && results.Count > 0)
        {
            slingshot = _physics.Slingshot(results);
            // Re-map to CognitiveSearchResult in gravity order for explain path
            orderedResults = slingshot.AllResults.Select(r =>
                new CognitiveSearchResult(r.Id, r.Text, r.CosineScore, r.LifecycleState,
                    r.ActivationEnergy, r.Category, null, r.IsSummaryNode,
                    r.SourceClusterId, r.AccessCount)).ToArray();
        }

        if (explain)
            return BuildExplainedResponse(orderedResults, ns, searchSw.Elapsed.TotalMilliseconds,
                embeddingMs, category, states, usePhysics, summaryFirst);

        if (slingshot is not null)
            return slingshot;

        return results;
    }

    private ExplainedSearchResponse BuildExplainedResponse(
        IReadOnlyList<CognitiveSearchResult> results, string ns,
        double searchMs, double embeddingMs, string? category,
        HashSet<string> states, bool usePhysics, bool summaryFirst)
    {
        var explained = new List<ExplainedSearchResult>(results.Count);
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            float mass = PhysicsEngine.ComputeMass(r.AccessCount, r.LifecycleState);
            float gravity = PhysicsEngine.ComputeGravity(mass, r.Score);
            float tierWeight = PhysicsEngine.GetTierWeight(r.LifecycleState);

            var explanation = new RetrievalExplanation(
                Rank: i + 1,
                CosineScore: r.Score,
                PhysicsMass: usePhysics ? mass : null,
                GravityForce: usePhysics ? gravity : null,
                LifecycleState: r.LifecycleState,
                LifecycleTierWeight: tierWeight,
                ActivationEnergy: r.ActivationEnergy,
                AccessCount: r.AccessCount,
                IsSummaryNode: r.IsSummaryNode,
                SummaryBoosted: summaryFirst && r.IsSummaryNode);

            explained.Add(new ExplainedSearchResult(r, explanation));
        }

        int totalInNamespace = _index.CountInNamespace(ns);

        return new ExplainedSearchResponse(
            explained, totalInNamespace, searchMs, embeddingMs,
            category, states.ToList(), usePhysics, summaryFirst);
    }

    [McpServerTool(Name = "delete_memory")]
    [Description("Delete a memory entry by ID, with cascading removal of graph edges and cluster memberships.")]
    public string DeleteMemory(
        [Description("The identifier of the entry to delete.")] string id,
        KnowledgeGraph graph,
        ClusterManager clusters)
    {
        // Cascade: remove graph edges (safe even if entry doesn't exist)
        int edgesRemoved = graph.RemoveAllEdgesForEntry(id);

        // Cascade: remove from clusters
        clusters.RemoveEntryFromAllClusters(id);

        // Remove the entry itself — check return value to avoid TOCTOU
        if (!_index.Delete(id))
            return $"Entry '{id}' not found.";

        return $"Deleted entry '{id}'. Removed {edgesRemoved} edge(s) and cleaned cluster memberships.";
    }

    private float[] ResolveVector(float[]? vector, string? text)
    {
        if (vector is not null && vector.Length > 0)
            return vector;

        if (!string.IsNullOrWhiteSpace(text))
            return _embedding.Embed(text);

        throw new ArgumentException("Either 'vector' or 'text' must be provided.");
    }
}
