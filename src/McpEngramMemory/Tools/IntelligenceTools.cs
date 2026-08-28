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
    private readonly NamespaceAccess _access;

    public IntelligenceTools(
        CognitiveIndex index, KnowledgeGraph graph, IEmbeddingService embedding,
        AccretionScanner scanner, ClusterManager clusters, LifecycleEngine lifecycle,
        NamespaceAccess access)
    {
        _index = index;
        _graph = graph;
        _embedding = embedding;
        _scanner = scanner;
        _clusters = clusters;
        _lifecycle = lifecycle;
        _access = access;
    }

    [McpServerTool(Name = "detect_duplicates", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Scan a namespace for near-duplicate entries by pairwise cosine similarity. Use before bulk imports or periodic cleanup to find redundant memories.")]
    public object DetectDuplicates(
        [Description("Namespace to scan.")] string ns,
        [Description("Cosine similarity threshold (default: 0.95). Entries above this are flagged as duplicates.")] float threshold = 0.95f,
        [Description("Filter by category.")] string? category = null,
        [Description("Comma-separated lifecycle states to include (default: 'stm,ltm').")] string? includeStates = null)
    {
        if (threshold < 0f || threshold > 1f)
            return "Error: Threshold must be between 0 and 1.";
        if (!_access.CanRead(ns))
            return new DuplicateDetectionResult(0, Array.Empty<DuplicatePair>(), threshold);

        var states = includeStates is not null
            ? new HashSet<string>(includeStates.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            : new HashSet<string> { "stm", "ltm" };

        var raw = _index.FindDuplicates(ns, tenantId: _access.TenantId, threshold: threshold, category: category, includeStates: states);

        var pairs = new List<DuplicatePair>(raw.Count);
        foreach (var (idA, idB, sim) in raw)
        {
            var a = _index.Get(idA, ns, tenantId: _access.TenantId);
            var b = _index.Get(idB, ns, tenantId: _access.TenantId);
            if (a is null || b is null) continue;

            pairs.Add(new DuplicatePair(
                new CognitiveEntryInfo(a.Id, a.Text, a.Ns, a.Category, a.LifecycleState),
                new CognitiveEntryInfo(b.Id, b.Text, b.Ns, b.Category, b.LifecycleState),
                sim));
        }

        var scannedCount = _index.CountInNamespace(ns, tenantId: _access.TenantId);
        return new DuplicateDetectionResult(scannedCount, pairs, threshold);
    }

    [McpServerTool(Name = "find_contradictions", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Surface conflicting memories: explicit 'contradicts' graph edges plus high-similarity pairs that may disagree. Use when information seems inconsistent.")]
    public object FindContradictions(
        [Description("Namespace to search.")] string ns,
        [Description("Optional topic text to focus contradiction search.")] string? topic = null,
        [Description("Cosine similarity threshold for potential contradiction detection (default: 0.8).")] float similarityThreshold = 0.8f)
    {
        if (!_access.CanRead(ns))
            return new ContradictionResult(Array.Empty<ContradictionInfo>(), 0, 0);

        // Part 1: Get explicit contradiction edges from the knowledge graph (this tenant)
        var graphContradictions = _graph.GetContradictions(ns, tenantId: _access.TenantId);
        var contradictions = new List<ContradictionInfo>();
        var knownPairs = new HashSet<(string, string)>();

        foreach (var (edge, source, target) in graphContradictions)
        {
            // A contradiction edge is only half-anchored in `ns`: the graph query matches an edge
            // when EITHER endpoint lives here, so the opposite endpoint can be any namespace in
            // the tenant, including one this caller was never granted. Authorize the entry we are
            // about to disclose, not the namespace that was asked for. CanReadEntry null-guards,
            // so an unresolvable endpoint is skipped by the same test — fail closed.
            if (!_access.CanReadEntry(source) || !_access.CanReadEntry(target)) continue;

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
        // Counted after the loop, never from graphContradictions.Count: the tally has to be
        // post-filter by construction, or it reports how many pairs were withheld and the count
        // becomes an existence oracle for the namespaces the caller cannot read.
        int graphCount = contradictions.Count;

        // Part 2: If a topic is provided, find high-similarity entries that might contradict
        int highSimCount = 0;
        if (topic is not null)
        {
            var vector = _embedding.Embed(topic);
            var results = _index.Search(vector, ns, k: 20, minScore: similarityThreshold, tenantId: _access.TenantId);

            // Pre-resolve all entries and their norms in a single pass (O(N) locks instead of O(N²))
            var resolved = new (CognitiveEntry? Entry, float Norm)[results.Count];
            for (int i = 0; i < results.Count; i++)
            {
                var entry = _index.Get(results[i].Id, ns, tenantId: _access.TenantId);
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

    [McpServerTool(Name = "uncollapse_cluster", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Reverse an accretion collapse: restore archived members, delete the summary entry, and clean up the cluster.")]
    public string UncollapseCluster(
        [Description("The collapse ID to reverse.")] string collapseId)
    {
        // Resolve the collapse's namespace before touching anything (within this tenant) - same
        // reply shape as a genuine miss for both "doesn't exist" and "exists but you can't touch it".
        var ns = _scanner.GetCollapseRecordNs(collapseId, tenantId: _access.TenantId);
        if (ns is null || !_access.CanWrite(ns))
            return $"Error: No collapse record found for '{collapseId}'.";

        var result = _scanner.UndoCollapse(collapseId, _lifecycle, _clusters, tenantId: _access.TenantId);
        if (!result.StartsWith("Error:"))
            _access.ClaimOnWrite(ns);
        return result;
    }

    [McpServerTool(Name = "list_collapse_history", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List all reversible collapse records for a namespace.")]
    public IReadOnlyList<CollapseRecord> ListCollapseHistory(
        [Description("Namespace to list collapse history for.")] string ns)
    {
        if (!_access.CanRead(ns)) return Array.Empty<CollapseRecord>();
        return _scanner.GetCollapseHistory(ns, tenantId: _access.TenantId);
    }

    /// <summary>
    /// Merge two entries the caller can write in one namespace.
    ///
    /// Two different objects are touched here and they are authorized differently. The ENTRY work
    /// - metadata union, access-count roll-up, archival - is namespace-qualified through
    /// <c>_index.Get(id, ns, tenant)</c> and the <c>CanWrite(ns)</c> gate above it, so it can only
    /// ever land on the two entries the caller named in the namespace they hold.
    ///
    /// The TOPOLOGY work is not namespace-qualified and cannot be: graph adjacency and cluster
    /// membership are keyed (tenant, bare id), so <c>TransferEdges</c>/<c>TransferMembership</c>
    /// reach every same-id entry in the tenant. That is what let a caller who created writable
    /// twins of two ids rewire another principal's private graph through this tool. The fix is not
    /// a gate here: <see cref="KnowledgeGraph"/> and <see cref="ClusterManager"/> now decline an
    /// ambiguous bare id themselves, so this path is covered along with every other writer.
    ///
    /// Deliberately no tool-level refusal on top of that. Refusing the whole merge when an id is
    /// ambiguous would deny a caller a legitimate, correctly-authorized operation on their OWN two
    /// entries merely because someone else in the tenant happens to hold the same id - and it
    /// would announce that fact. The reply stays honest instead by reporting the counts Core
    /// actually returned: a merge that moved no topology says it moved none, which is exactly what
    /// merging two unlinked entries has always said.
    /// </summary>
    [McpServerTool(Name = "merge_memories", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Merge two duplicate entries into one. Keeps first entry's vector, combines metadata/access counts, transfers edges and clusters, archives the second. Use after detect_duplicates.")]
    public string MergeMemories(
        [Description("ID of the entry to keep.")] string keepId,
        [Description("ID of the duplicate entry to archive.")] string archiveId,
        [Description("Namespace containing both entries.")] string ns)
    {
        if (!_access.CanWrite(ns)) return NamespaceAccess.WriteDenied(ns);

        var keepEntry = _index.Get(keepId, ns, tenantId: _access.TenantId);
        if (keepEntry is null)
            return $"Error: Entry '{keepId}' not found in namespace '{ns}'.";

        var archiveEntry = _index.Get(archiveId, ns, tenantId: _access.TenantId);
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
            keepEntry.ActivationEnergy, keepEntry.IsSummaryNode, keepEntry.SourceClusterId,
            keywords: keepEntry.Keywords, tenantId: keepEntry.TenantId);
        _index.Upsert(updated);
        _access.ClaimOnWrite(ns);

        // Transfer graph edges from archived entry to kept entry (within this tenant). Both counts
        // below are whatever Core actually did, never what was attempted - that is what keeps the
        // reply truthful when an ambiguous id made the transfer a no-op.
        int edgesTransferred = _graph.TransferEdges(archiveId, keepId, tenantId: _access.TenantId);

        // Transfer cluster memberships
        int clustersTransferred = _clusters.TransferMembership(archiveId, keepId, tenantId: _access.TenantId);

        // Archive the duplicate via lifecycle engine. Namespace-qualified, so it reaches only the
        // caller's own entry even when the id is held elsewhere in the tenant.
        _lifecycle.PromoteMemory(archiveId, "archived", ns, tenantId: _access.TenantId);

        // Add traceability edge. Not reported either way: the reply never claims this edge, so a
        // refusal here stays invisible rather than becoming a "a twin exists" signal.
        _graph.AddEdge(new GraphEdge(keepId, archiveId, "similar_to", 1.0f, null, tenantId: _access.TenantId));

        return $"Merged '{archiveId}' into '{keepId}'. " +
               $"Transferred {edgesTransferred} edge(s), {clustersTransferred} cluster(s), " +
               $"{metaKeysMerged} metadata key(s). Archived '{archiveId}'.";
    }
}
