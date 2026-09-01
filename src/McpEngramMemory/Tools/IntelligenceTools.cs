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

    /// <summary>Caller-facing projection of a collapse record. Recovery internals — the
    /// incarnation stamp the destructive cleanup compares, the applied/expected witness maps,
    /// the generation — are deliberately OMITTED: they are ownership tokens, not data, and the
    /// stamp in particular must never be handed to a caller who could replay it into a forged
    /// cluster incarnation.</summary>
    public sealed record CollapseRecordInfo(
        string CollapseId, string ClusterId, string SummaryEntryId, string Ns,
        IReadOnlyList<string> MemberIds, IReadOnlyDictionary<string, string> PreviousStates,
        DateTimeOffset CollapsedAt);

    [McpServerTool(Name = "list_collapse_history", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List all reversible collapse records for a namespace.")]
    public IReadOnlyList<CollapseRecordInfo> ListCollapseHistory(
        [Description("Namespace to list collapse history for.")] string ns)
    {
        if (!_access.CanRead(ns)) return Array.Empty<CollapseRecordInfo>();
        return _scanner.GetCollapseHistory(ns, tenantId: _access.TenantId)
            .Select(r => new CollapseRecordInfo(
                r.CollapseId, r.ClusterId, r.SummaryEntryId, r.Ns,
                r.MemberIds, r.PreviousStates, r.CollapsedAt))
            .ToList();
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

        // MERGING AN ENTRY WITH ITSELF IS NOT A MERGE, it is an archive of the entry the reply
        // claims to be keeping. Both lookups below resolve to the one entry, so every step
        // afterwards runs against a single object: the metadata union is a no-op, the access count
        // doubles, the topology calls are asked to move a node onto itself, and PromoteMemory then
        // archives the id the caller named as keepId. Refused here rather than left to Core —
        // TransferEdges and TransferMembership each decline a self-transfer, but nothing there can
        // stop the archival, and no caller means "archive this" when they write keepId.
        //
        // Ordinal, matching the id comparisons in Core: two ids that differ only by
        // culture-sensitive equality are two entries everywhere else in this server.
        if (string.Equals(keepId, archiveId, StringComparison.Ordinal))
            return $"Error: Cannot merge entry '{keepId}' with itself.";

        if (_index.Get(keepId, ns, tenantId: _access.TenantId) is null)
            return $"Error: Entry '{keepId}' not found in namespace '{ns}'.";
        if (_index.Get(archiveId, ns, tenantId: _access.TenantId) is null)
            return $"Error: Entry '{archiveId}' not found in namespace '{ns}'.";

        // Capture detached copies and explicit witnesses under one partition read lock. Revision
        // identifies the occupation, while lifecycle/access/energy fields witness the in-place
        // changes Revision deliberately does not move.
        if (!_index.TryCaptureMergeEntries(
                keepId, archiveId, ns, _access.TenantId,
                out var keepSnapshot, out var archiveSnapshot)
            || keepSnapshot is null || archiveSnapshot is null)
            return "Error: One of the entries changed while the merge was being prepared; nothing was merged. Re-read and retry.";

        var keepEntry = keepSnapshot.Entry;
        var archiveEntry = archiveSnapshot.Entry;

        // MACHINERY-OWNED and RECEIPT-HELD entries refuse the merge outright. A cluster
        // summary's slot is written only through the incarnation-conditioned store — a merge
        // upserting over it (or archiving it) would bypass that machinery entirely. An
        // ARCHIVED entry is potentially held by a collapse receipt whose restore CAS expects
        // the entry's CURRENT LifecycleRevision; the merge's upsert mints a fresh one, after
        // which the undo's restore refuses and the member is stranded archived with its
        // receipt consumed. Undo the collapse (or promote the entry) first.
        if (keepEntry.IsSummaryNode || archiveEntry.IsSummaryNode)
            return "Error: Cluster summaries are machinery-owned and cannot be merged; re-summarize the cluster instead.";
        if (keepEntry.LifecycleState == "archived" || archiveEntry.LifecycleState == "archived")
            return "Error: Archived entries can be held by collapse receipts and cannot be merged; undo the collapse or promote the entry first.";

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
            keywords: keepEntry.Keywords, tenantId: keepEntry.TenantId)
        {
            // Summary OWNERSHIP survives the merge: a kept entry that happens to be a
            // cluster summary must keep its stamp and instance, or the ownership read
            // screens stop serving it and its record's conditioned cleanup stops matching.
            SourceClusterStamp = keepEntry.SourceClusterStamp,
            SourceClusterInstance = keepEntry.SourceClusterInstance
        };
        // Preflight topology without mutation. The coherent commit then holds the namespace
        // partition write lock through both entry updates and both topology publications, nested
        // in the established partition -> attribution fence -> graph -> cluster order. There is no
        // keep-first or topology-first failure image: every validation that can decline precedes
        // the first write. The old similar_to trace is deliberately omitted because an edge added
        // after the critical section would reopen the exact unbound topology window being closed.
        var topology = _graph.PrepareMergeTopology(
            archiveId, keepId, _access.TenantId, ns, _clusters);
        if (topology is null
            || !_index.TryCommitMerge(
                keepSnapshot, archiveSnapshot, updated, topology, _graph, _clusters,
                out var committed)
            || committed is null)
            return "Error: An entry or its topology changed while the merge was being prepared; nothing was merged. Re-read and retry.";

        _access.ClaimOnWrite(ns);

        return $"Merged '{archiveId}' into '{keepId}'. " +
               $"Transferred {committed.EdgesTransferred} edge(s), {committed.ClustersTransferred} cluster(s), " +
               $"{metaKeysMerged} metadata key(s). Archived '{archiveId}'.";
    }
}
