using System.ComponentModel;
using System.Diagnostics;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Retrieval;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

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
    private readonly KnowledgeGraph _graph;
    private readonly QueryExpander _queryExpander;
    private readonly SpreadingActivationService _spreading;
    private readonly ClusterManager _clusters;

    public CoreMemoryTools(CognitiveIndex index, PhysicsEngine physics, IEmbeddingService embedding,
        MetricsCollector metrics, KnowledgeGraph graph, QueryExpander queryExpander,
        SpreadingActivationService spreading, ClusterManager clusters)
    {
        _index = index;
        _physics = physics;
        _embedding = embedding;
        _metrics = metrics;
        _graph = graph;
        _queryExpander = queryExpander;
        _spreading = spreading;
        _clusters = clusters;
    }

    [McpServerTool(Name = "store_memory")]
    [Description("Low-level store with explicit vector/text control. Use remember instead for auto-dedup and auto-linking. Supports namespace isolation, categorical metadata, and lifecycle tracking.")]
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

    [McpServerTool(Name = "store_batch")]
    [Description("Bulk-store multiple entries in one write-lock. Faster than repeated store_memory calls. Each entry gets contextual prefix embedding. Returns stored count and duplicate warnings.")]
    public object StoreBatch(
        [Description("Namespace for all entries.")] string ns,
        [Description("Array of entries to store. Each must have 'id' and 'text' fields. Optional: 'category', 'metadata', 'lifecycleState'.")] BatchEntry[] entries,
        [Description("Check for near-duplicates within the batch (default: true).")] bool checkDuplicates = true)
    {
        if (entries is null || entries.Length == 0)
            return new { status = "error", message = "No entries provided." };

        using var timer = _metrics.StartTimer("store_batch");

        // Embed all entries
        var cognitiveEntries = new List<CognitiveEntry>(entries.Length);
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Text))
                continue;

            var prefix = BenchmarkRunner.BuildContextualPrefix(ns, e.Category);
            var vector = _embedding.Embed(prefix + e.Text);
            var entry = new CognitiveEntry(e.Id, vector, ns, e.Text, e.Category, e.Metadata,
                e.LifecycleState ?? "stm");
            cognitiveEntries.Add(entry);
        }

        if (cognitiveEntries.Count == 0)
            return new { status = "error", message = "No valid entries (each needs 'id' and 'text')." };

        // Batch upsert with single lock
        int stored = _index.UpsertBatch(cognitiveEntries);

        // Optional duplicate check
        var warnings = new List<string>();
        if (checkDuplicates && stored > 0)
        {
            foreach (var entry in cognitiveEntries)
            {
                var dups = _index.FindDuplicatesForEntry(ns, entry.Id, threshold: 0.95f);
                if (dups.Count > 0)
                {
                    var dupIds = dups.Select(d => d.IdA == entry.Id ? d.IdB : d.IdA);
                    warnings.Add($"{entry.Id}: near-duplicate(s) [{string.Join(", ", dupIds)}]");
                }
            }
        }

        return new
        {
            status = "stored",
            ns,
            entriesStored = stored,
            entriesSkipped = entries.Length - stored,
            duplicateWarnings = warnings.Count > 0 ? warnings : null
        };
    }

    [McpServerTool(Name = "search_memory")]
    [Description("Low-level namespace search with full parameter control. Use recall instead for auto-routing and fallback. Supports hybrid BM25+vector, reranking, query expansion, graph expansion, physics re-ranking, and explain mode.")]
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
        [Description("When true, apply token-level reranking to improve precision on the top results.")] bool rerank = false,
        [Description("When true, use pseudo-relevance feedback to expand the query with terms from top results, improving recall.")] bool expandQuery = false,
        [Description("When true, include graph-connected neighbors of search results, boosting recall for related memories.")] bool expandGraph = false,
        [Description("Cognitive temperature [0.0-1.0]. T=0: pure semantic (factual lookup). T=0.3: balanced (default when usePhysics=true). T=0.7: associative brainstorming. T=1.0: heavily physics-weighted (favors frequently accessed memories).")] float temperature = 0f,
        [Description("When true, apply cluster-aware MMR diversity reranking to spread results across different sub-topics instead of clustering around one.")] bool diversity = false,
        [Description("Diversity trade-off [0.0-1.0]. 1.0=pure relevance, 0.0=pure diversity (default: 0.5).")] float diversityLambda = 0.5f)
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
        if (diversity)
        {
            // Use SearchRequest path which supports diversity reranking
            results = _index.Search(new SearchRequest
            {
                Query = resolved, Namespace = ns, QueryText = text, K = k,
                MinScore = minScore, Category = category, IncludeStates = states,
                Hybrid = hybrid, Rerank = rerank, SummaryFirst = summaryFirst,
                Diversity = true, DiversityLambda = diversityLambda
            });
        }
        else if (hybrid && text is not null)
        {
            results = _index.HybridSearch(resolved, text, ns, k, minScore, category, states, rerank);
        }
        else
        {
            results = _index.Search(resolved, ns, k, minScore, category, states, summaryFirst);
            if (rerank && text is not null && results.Count > 1)
                results = _index.Rerank(text, results);
        }

        // Query expansion via pseudo-relevance feedback: use top results to expand query, then re-search
        if (expandQuery && text is not null && results.Count >= 2)
        {
            var expandedText = _queryExpander.Expand(text, results);
            if (expandedText != text)
            {
                var expandedVector = _embedding.Embed(expandedText);
                IReadOnlyList<CognitiveSearchResult> expandedResults;
                if (hybrid)
                {
                    expandedResults = _index.HybridSearch(expandedVector, expandedText, ns, k, minScore, category, states, rerank);
                }
                else
                {
                    expandedResults = _index.Search(expandedVector, ns, k, minScore, category, states, summaryFirst);
                    if (rerank)
                        expandedResults = _index.Rerank(expandedText, expandedResults);
                }

                // Merge: prefer expanded results but keep unique original results
                var mergedIds = new HashSet<string>();
                var merged = new List<CognitiveSearchResult>();
                foreach (var r in expandedResults.Concat(results))
                {
                    if (mergedIds.Add(r.Id))
                        merged.Add(r);
                }
                results = merged.Take(k).ToList();
            }
        }

        searchSw.Stop();

        // Side effect: record access and trigger spreading activation for returned entries
        foreach (var result in results)
        {
            _index.RecordAccess(result.Id, ns);
            // Asynchronous spreading activation: propagate energy to graph neighbors and cluster peers
            if (expandGraph)
                _spreading.PropagateAccess(result.Id, ns, baseEnergy: 0.5f);
        }

        // Graph expansion: pull in neighbors of top results with edge-type-weighted scoring
        if (expandGraph && results.Count > 0)
        {
            var existingIds = results.Select(r => r.Id).ToHashSet();
            var graphExpanded = new List<CognitiveSearchResult>(results);
            float lowestScore = results.Min(r => r.Score);

            foreach (var result in results)
            {
                // Graph neighbor expansion with edge-type-weighted scoring
                var neighbors = _graph.GetNeighbors(result.Id);
                foreach (var neighbor in neighbors.Neighbors)
                {
                    if (existingIds.Contains(neighbor.Entry.Id)) continue;
                    if (!states.Contains(neighbor.Entry.LifecycleState)) continue;
                    if (category is not null && neighbor.Entry.Category != category) continue;

                    existingIds.Add(neighbor.Entry.Id);

                    // Edge-type-weighted score: stronger edge types get higher scores
                    float edgeWeight = PhysicsEngine.GetEdgeTypeWeight(neighbor.Edge.Relation);
                    float expandedScore = lowestScore * edgeWeight;

                    graphExpanded.Add(new CognitiveSearchResult(
                        neighbor.Entry.Id, neighbor.Entry.Text, expandedScore,
                        neighbor.Entry.LifecycleState, 0f,
                        neighbor.Entry.Category, null,
                        false, null, 0));
                }

                // Cluster expansion: include cluster peers of top results
                var clusterIds = _clusters.GetClustersForEntry(result.Id);
                foreach (var clusterId in clusterIds)
                {
                    var clusterInfo = _clusters.GetCluster(clusterId);
                    if (clusterInfo is null) continue;

                    // Include cluster summary node at high priority
                    if (clusterInfo.SummaryEntry is not null && existingIds.Add(clusterInfo.SummaryEntry.Id))
                    {
                        graphExpanded.Add(new CognitiveSearchResult(
                            clusterInfo.SummaryEntry.Id, clusterInfo.SummaryEntry.Text,
                            lowestScore * 0.9f,
                            clusterInfo.SummaryEntry.LifecycleState, 0f,
                            clusterInfo.SummaryEntry.Category, null,
                            true, clusterId, 0));
                    }

                    // Include top cluster members
                    foreach (var member in clusterInfo.Members.Take(3))
                    {
                        if (!existingIds.Add(member.Id)) continue;
                        if (!states.Contains(member.LifecycleState)) continue;

                        graphExpanded.Add(new CognitiveSearchResult(
                            member.Id, member.Text, lowestScore * 0.6f,
                            member.LifecycleState, 0f,
                            member.Category, null,
                            false, null, 0));
                    }
                }
            }

            results = graphExpanded;
        }

        // When usePhysics, apply gravity re-ranking before explain or return
        IReadOnlyList<CognitiveSearchResult> orderedResults = results;
        SlingshotResult? slingshot = null;
        if (usePhysics && results.Count > 0)
        {
            slingshot = _physics.Slingshot(results, temperature);
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
    [Description("Delete a memory entry by ID. Cascades to remove graph edges and cluster memberships.")]
    public string DeleteMemory(
        [Description("The identifier of the entry to delete.")] string id)
    {
        // Cascade: remove graph edges (safe even if entry doesn't exist)
        int edgesRemoved = _graph.RemoveAllEdgesForEntry(id);

        // Cascade: remove from clusters
        _clusters.RemoveEntryFromAllClusters(id);

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

/// <summary>A single entry in a store_batch call.</summary>
public sealed class BatchEntry
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("text")]
    public string Text { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("category")]
    public string? Category { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("lifecycleState")]
    public string? LifecycleState { get; set; }
}
