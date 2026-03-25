using System.ComponentModel;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for intelligence features: duplicate detection, contradiction surfacing, reversible collapse.
/// </summary>
[McpServerToolType]
public sealed class IntelligenceTools
{
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly IEmbeddingService _embedding;
    private readonly AccretionScanner _scanner;
    private readonly ClusterManager _clusters;
    private readonly LifecycleEngine _lifecycle;

    public IntelligenceTools(
        CognitiveIndex index, KnowledgeGraph graph, IEmbeddingService embedding,
        AccretionScanner scanner, ClusterManager clusters, LifecycleEngine lifecycle)
    {
        _index = index;
        _graph = graph;
        _embedding = embedding;
        _scanner = scanner;
        _clusters = clusters;
        _lifecycle = lifecycle;
    }

    [McpServerTool(Name = "detect_duplicates")]
    [Description("Scan a namespace for near-duplicate entries by pairwise cosine similarity. Use before bulk imports or periodic cleanup to find redundant memories.")]
    public object DetectDuplicates(
        [Description("Namespace to scan.")] string ns,
        [Description("Cosine similarity threshold (default: 0.95). Entries above this are flagged as duplicates.")] float threshold = 0.95f,
        [Description("Filter by category.")] string? category = null,
        [Description("Comma-separated lifecycle states to include (default: 'stm,ltm').")] string? includeStates = null)
    {
        if (threshold < 0f || threshold > 1f)
            return "Error: Threshold must be between 0 and 1.";

        var states = includeStates is not null
            ? new HashSet<string>(includeStates.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            : new HashSet<string> { "stm", "ltm" };

        var raw = _index.FindDuplicates(ns, threshold, category, states);

        var pairs = new List<DuplicatePair>(raw.Count);
        foreach (var (idA, idB, sim) in raw)
        {
            var a = _index.Get(idA, ns);
            var b = _index.Get(idB, ns);
            if (a is null || b is null) continue;

            pairs.Add(new DuplicatePair(
                new CognitiveEntryInfo(a.Id, a.Text, a.Ns, a.Category, a.LifecycleState),
                new CognitiveEntryInfo(b.Id, b.Text, b.Ns, b.Category, b.LifecycleState),
                sim));
        }

        var scannedCount = _index.CountInNamespace(ns);
        return new DuplicateDetectionResult(scannedCount, pairs, threshold);
    }

    [McpServerTool(Name = "find_contradictions")]
    [Description("Surface conflicting memories: explicit 'contradicts' graph edges plus high-similarity pairs that may disagree. Use when information seems inconsistent.")]
    public object FindContradictions(
        [Description("Namespace to search.")] string ns,
        [Description("Optional topic text to focus contradiction search.")] string? topic = null,
        [Description("Cosine similarity threshold for potential contradiction detection (default: 0.8).")] float similarityThreshold = 0.8f)
    {
        // Part 1: Get explicit contradiction edges from the knowledge graph
        var graphContradictions = _graph.GetContradictions(ns);
        var contradictions = new List<ContradictionInfo>();
        var knownPairs = new HashSet<(string, string)>();

        foreach (var (edge, source, target) in graphContradictions)
        {
            if (source is null || target is null) continue;

            // Compute similarity between the two entries
            float sim = 0f;
            if (source.Vector.Length == target.Vector.Length)
            {
                float sourceNorm = VectorMath.Norm(source.Vector);
                float targetNorm = VectorMath.Norm(target.Vector);
                if (sourceNorm > 0f && targetNorm > 0f)
                    sim = VectorMath.Dot(source.Vector, target.Vector) / (sourceNorm * targetNorm);
            }

            contradictions.Add(new ContradictionInfo(
                new CognitiveEntryInfo(source.Id, source.Text, source.Ns, source.Category, source.LifecycleState),
                new CognitiveEntryInfo(target.Id, target.Text, target.Ns, target.Category, target.LifecycleState),
                sim, "graph_edge"));

            // Track both orderings for O(1) dedup
            knownPairs.Add((source.Id, target.Id));
            knownPairs.Add((target.Id, source.Id));
        }
        int graphCount = contradictions.Count;

        // Part 2: If a topic is provided, find high-similarity entries that might contradict
        int highSimCount = 0;
        if (topic is not null)
        {
            var vector = _embedding.Embed(topic);
            var results = _index.Search(vector, ns, k: 20, minScore: similarityThreshold);

            // Pre-resolve all entries and their norms in a single pass (O(N) locks instead of O(N²))
            var resolved = new (CognitiveEntry? Entry, float Norm)[results.Count];
            for (int i = 0; i < results.Count; i++)
            {
                var entry = _index.Get(results[i].Id, ns);
                resolved[i] = (entry, entry is not null ? VectorMath.Norm(entry.Vector) : 0f);
            }

            // Check for pairs among the results that are very similar to each other
            for (int i = 0; i < results.Count; i++)
            {
                var (a, aNorm) = resolved[i];
                if (a is null || aNorm == 0f) continue;

                for (int j = i + 1; j < results.Count; j++)
                {
                    var (b, bNorm) = resolved[j];
                    if (b is null || bNorm == 0f) continue;
                    if (a.Vector.Length != b.Vector.Length) continue;

                    float pairSim = VectorMath.Dot(a.Vector, b.Vector) / (aNorm * bNorm);
                    if (pairSim < similarityThreshold) continue;

                    // Skip if this pair is already in the graph contradictions
                    if (knownPairs.Contains((a.Id, b.Id))) continue;

                    knownPairs.Add((a.Id, b.Id));
                    knownPairs.Add((b.Id, a.Id));

                    contradictions.Add(new ContradictionInfo(
                        new CognitiveEntryInfo(a.Id, a.Text, a.Ns, a.Category, a.LifecycleState),
                        new CognitiveEntryInfo(b.Id, b.Text, b.Ns, b.Category, b.LifecycleState),
                        pairSim, "high_similarity"));
                    highSimCount++;
                }
            }
        }

        return new ContradictionResult(contradictions, graphCount, highSimCount);
    }

    [McpServerTool(Name = "uncollapse_cluster")]
    [Description("Reverse an accretion collapse: restore archived members, delete the summary entry, and clean up the cluster.")]
    public string UncollapseCluster(
        [Description("The collapse ID to reverse.")] string collapseId)
    {
        return _scanner.UndoCollapse(collapseId, _lifecycle, _clusters);
    }

    [McpServerTool(Name = "list_collapse_history")]
    [Description("List all reversible collapse records for a namespace.")]
    public IReadOnlyList<CollapseRecord> ListCollapseHistory(
        [Description("Namespace to list collapse history for.")] string ns)
    {
        return _scanner.GetCollapseHistory(ns);
    }

    [McpServerTool(Name = "merge_memories")]
    [Description("Merge two duplicate entries into one. Keeps first entry's vector, combines metadata/access counts, transfers edges and clusters, archives the second. Use after detect_duplicates.")]
    public string MergeMemories(
        [Description("ID of the entry to keep.")] string keepId,
        [Description("ID of the duplicate entry to archive.")] string archiveId,
        [Description("Namespace containing both entries.")] string ns)
    {
        var keepEntry = _index.Get(keepId, ns);
        if (keepEntry is null)
            return $"Error: Entry '{keepId}' not found in namespace '{ns}'.";

        var archiveEntry = _index.Get(archiveId, ns);
        if (archiveEntry is null)
            return $"Error: Entry '{archiveId}' not found in namespace '{ns}'.";

        // Merge metadata: union of keys, keep entry's value wins on conflict
        var mergedMeta = new Dictionary<string, string>(keepEntry.Metadata ?? new());
        int metaKeysMerged = 0;
        if (archiveEntry.Metadata is { Count: > 0 })
        {
            foreach (var (key, value) in archiveEntry.Metadata)
            {
                if (mergedMeta.TryAdd(key, value))
                    metaKeysMerged++;
            }
        }

        var updated = new CognitiveEntry(
            keepEntry.Id, keepEntry.Vector, keepEntry.Ns, keepEntry.Text,
            keepEntry.Category, mergedMeta, keepEntry.LifecycleState,
            keepEntry.CreatedAt, keepEntry.LastAccessedAt,
            keepEntry.AccessCount + archiveEntry.AccessCount,
            keepEntry.ActivationEnergy, keepEntry.IsSummaryNode, keepEntry.SourceClusterId);
        _index.Upsert(updated);

        // Transfer graph edges from archived entry to kept entry
        int edgesTransferred = _graph.TransferEdges(archiveId, keepId);

        // Transfer cluster memberships
        int clustersTransferred = _clusters.TransferMembership(archiveId, keepId);

        // Archive the duplicate via lifecycle engine
        _lifecycle.PromoteMemory(archiveId, "archived");

        // Add traceability edge
        _graph.AddEdge(new GraphEdge(keepId, archiveId, "similar_to"));

        return $"Merged '{archiveId}' into '{keepId}'. " +
               $"Transferred {edgesTransferred} edge(s), {clustersTransferred} cluster(s), " +
               $"{metaKeysMerged} metadata key(s). Archived '{archiveId}'.";
    }
}
