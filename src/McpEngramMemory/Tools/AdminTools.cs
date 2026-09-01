using System.ComponentModel;
using System.Text.Json.Serialization;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for inspection: get_memory, cognitive_stats, purge_debates, and engram_status.
/// </summary>
[McpServerToolType]
public sealed class AdminTools
{
    /// <summary>Default cap on the namespace list returned by cognitive_stats.</summary>
    private const int DefaultNamespaceLimit = 100;

    /// <summary>Default cap on the per-namespace detail returned by purge_debates.</summary>
    private const int DefaultDetailLimit = 25;

    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly IBackgroundWorkerStatusTracker? _statusTracker;
    private readonly NamespaceRegistry _registry;
    private readonly IPrincipalContext _principal;

    [ActivatorUtilitiesConstructor]
    public AdminTools(CognitiveIndex index, KnowledgeGraph graph, ClusterManager clusters, IStorageProvider storage,
        NamespaceRegistry registry, IPrincipalContext principal,
        IBackgroundWorkerStatusTracker? statusTracker = null)
    {
        _index = index;
        _graph = graph;
        _clusters = clusters;
        _ = storage; // Retained in the constructor for binary/source compatibility.
        _registry = registry;
        _principal = principal;
        _statusTracker = statusTracker;
    }

    public AdminTools(CognitiveIndex index, KnowledgeGraph graph, ClusterManager clusters, IStorageProvider storage,
        NamespaceRegistry registry, AgentIdentity agent,
        IBackgroundWorkerStatusTracker? statusTracker = null)
        : this(index, graph, clusters, storage, registry,
            new PrincipalContext(string.Empty, agent.AgentId), statusTracker) { }

    private bool CanRead(string ns) => _principal.IsSystem ||
        _registry.HasAccess(_principal.AgentId, ns, requiredLevel: "read", tenantId: _principal.TenantId);
    private bool CanWrite(string ns) => _principal.IsSystem ||
        _registry.HasAccess(_principal.AgentId, ns, "write", tenantId: _principal.TenantId);

    [McpServerTool(Name = "get_memory", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Look up one memory's full metadata — lifecycle state, graph edges, cluster memberships, access count — without triggering an access-count increment. Don't use it to search by topic; use `recall` or `search_memory` for that.")]
    public object GetMemory(
        [Description("Entry ID.")] string id)
    {
        // get_memory resolves by id across every namespace, so unguarded it hands back the
        // full text and metadata of any entry whose id a caller can guess or has seen in a
        // graph edge. EntryAccessResolver applies the read predicate before matching, and
        // not-found, not-permitted, and ambiguous all share the reply of a genuine miss -
        // a distinct denial would confirm the id. Same semantics as the edge filter below.
        var entry = EntryAccessResolver.Resolve(_index, id, _principal.TenantId, CanRead);
        if (entry is null)
            return $"Entry '{id}' not found.";

        // The resolution above authorized an ENTRY, and it was right to do so ACL-filtered: the
        // text and metadata returned belong to the qualified (tenant, namespace, id) entry this
        // caller can see. The two collections below are not that object. Graph adjacency and
        // cluster membership are keyed (tenant, id) with no namespace, so they describe a node
        // SHARED with every same-id entry in the tenant — including the invisible twin whose
        // existence the ACL-filtered resolution is deliberately blind to. Attaching that shared
        // node's topology to the twin the caller happens to see is how a private edge is served
        // up as if it belonged to somebody else's entry, so the topology gate is the ACL-blind
        // tenant-wide test and the entry gate is not reused for it. See BareIdTopology for the
        // asymmetry and for the one bit this suppression costs.
        bool topologySafe = BareIdTopology.IsTopologySafe(_index, id, tenantId: _principal.TenantId);

        // Graph topology is global and edges carry bare endpoint IDs. Returning an edge
        // to a private endpoint discloses that endpoint's ID, relationship, and metadata
        // even when its entry body is protected. Project the edge set through the same
        // entry-level read policy as the primary object.
        var edges = topologySafe
            ? _graph.GetEdgesForEntry(id, tenantId: _principal.TenantId)
                .Where(edge => CanReadEndpoint(edge.SourceId) && CanReadEndpoint(edge.TargetId))
                .ToList()
            : (IReadOnlyList<GraphEdge>)Array.Empty<GraphEdge>();
        // Cluster membership is the same kind of disclosure as an edge: a cluster id names a
        // grouping that lives in some namespace, and co-membership tells the caller that this
        // entry was grouped with content they cannot read. Membership is deliberately allowed
        // to span namespaces, so the gate is the cluster's OWN namespace — CanRead(m.Ns), the
        // same predicate ClusterTools.GetCluster applies — not equality with entry.Ns.
        var clusterIds = topologySafe
            ? _clusters.GetClusterMembershipsForEntry(id, tenantId: _principal.TenantId)
                .Where(m => CanRead(m.Ns))
                .Select(m => m.ClusterId)
                .ToList()
            : (IReadOnlyList<string>)Array.Empty<string>();

        return new GetMemoryResult(
            new CognitiveEntryInfo(entry.Id, entry.Text, entry.Ns, entry.Category, entry.LifecycleState),
            entry.Text,
            entry.Metadata,
            entry.LifecycleState,
            entry.ActivationEnergy,
            entry.AccessCount,
            entry.CreatedAt,
            entry.LastAccessedAt,
            edges,
            clusterIds);
    }

    private bool CanReadEndpoint(string entryId) =>
        EntryAccessResolver.Resolve(_index, entryId, _principal.TenantId, CanRead) is not null;

    [McpServerTool(Name = "cognitive_stats", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Check how many memories exist across lifecycle states (STM/LTM/archived), plus cluster and edge counts and the namespace list. The namespace list is capped — raise namespaceLimit or pass 0 for all. Don't use it to check background worker health; use `engram_status` for that.")]
    public LifecycleStats CognitiveStats(
        [Description("Namespace ('*' for all, default).")] string ns = "*",
        [Description("Maximum namespaces to list (default: 100). Use 0 for no cap. Counts are always exact regardless of this limit.")] int namespaceLimit = DefaultNamespaceLimit)
    {
        // Counts, topology totals, and cluster totals are all existence signals, so every
        // aggregate is derived from the same principal-visible namespace set — a caller cannot
        // probe a private namespace by name or infer its size.
        var namespaces = _index.GetNamespaces(_principal.TenantId).Where(CanRead).ToList();
        var scopedNamespaces = ns == "*"
            ? namespaces
            : namespaces.Where(candidate => candidate == ns).ToList();

        // Counts come from the index's per-partition tally rather than a materialized entry
        // list. This tool is asked for the summary, not the contents, so pulling every visible
        // entry into a list just to run three predicates over it is pure overhead.
        int stm = 0, ltm = 0, archived = 0;
        foreach (var scope in scopedNamespaces)
        {
            var (scopeStm, scopeLtm, scopeArchived) = _index.GetStateCounts(scope, _principal.TenantId);
            stm += scopeStm;
            ltm += scopeLtm;
            archived += scopeArchived;
        }

        // Graph and cluster keys are bare ids, so topology totals are meaningful only in the
        // legacy partition and are fixed at zero elsewhere. Build the visible-id set — which
        // costs a pass over every visible entry — only when a count actually depends on it.
        int edgeCount, clusterCount;
        {
            var visibleIds = scopedNamespaces
                .SelectMany(scope => _index.GetAllInNamespace(scope, _principal.TenantId))
                .Select(entry => entry.Id)
                .ToHashSet();
            // visibleIds holds BARE ids, so membership in it says the caller can see AN entry with
            // that id, not that this edge belongs to it. A caller who creates twins of two ids that
            // another principal privately linked would otherwise see that private edge counted
            // here — a tally is a smaller disclosure than the edge itself but still an oracle,
            // answerable one probe at a time. Count only edges attributable at both ends. Guarded
            // by a sweep because a store's edge list revisits the same ids many times over.
            var topology = BareIdTopology.ForSweep(_index, tenantId: _principal.TenantId);
            edgeCount = _graph.GetAllEdges(_principal.TenantId).Count(edge =>
                visibleIds.Contains(edge.SourceId) && visibleIds.Contains(edge.TargetId)
                && topology.IsTopologySafe(edge.SourceId) && topology.IsTopologySafe(edge.TargetId));
            // Clusters need no such guard: ListClusters is namespace-scoped and a cluster carries
            // its own Ns, so the count is already qualified rather than reached by bare id.
            clusterCount = scopedNamespaces.Sum(scope => _clusters.ListClusters(scope, tenantId: _principal.TenantId).Count);
        }

        // A store with hundreds of namespaces returns a list that dominates the caller's
        // context window for no benefit — the counts are what the tool is usually asked for.
        // Cap the list, but never the counts, and say plainly that it was capped.
        var totalNamespaces = namespaces.Count;
        var listed = namespaceLimit > 0 && totalNamespaces > namespaceLimit
            ? namespaces.Take(namespaceLimit).Append($"… +{totalNamespaces - namespaceLimit} more (pass namespaceLimit=0 to list all)").ToList()
            : namespaces;

        return new LifecycleStats(
            stm + ltm + archived,
            stm, ltm, archived,
            clusterCount, edgeCount,
            listed);
    }

    [McpServerTool(Name = "engram_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Check the last-run timestamps, cycle counts, and error counts for every background worker (decay, consolidation, diffusion, accretion). Don't use it to see memory counts or namespace lists; use `cognitive_stats` for that.")]
    public EngramStatusOutput EngramStatus()
    {
        return _statusTracker?.GetSnapshot()
            ?? new EngramStatusOutput(
                new EngramWorkerStatus("decay",         null, 0, 0, 0, null),
                new EngramWorkerStatus("consolidation", null, 0, 0, 0, null),
                new EngramWorkerStatus("auto_link",     null, 0, 0, 0, null),
                new EngramWorkerStatus("accretion",     null, 0, 0, 0, null));
    }

    [McpServerTool(Name = "purge_debates", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Clean up stale debate namespaces older than maxAgeHours. Deletes entries, edges, and cluster memberships. Defaults to dry-run mode.")]
    public async Task<object> PurgeDebates(
        [Description("Maximum age in hours before a debate namespace is considered stale (default: 24).")] int maxAgeHours = 24,
        [Description("If true, only list what would be purged without deleting (default: true).")] bool dryRun = true,
        [Description("Maximum namespaces to detail in the response (default: 25). Use 0 for all. Totals are always exact regardless of this limit.")] int detailLimit = DefaultDetailLimit)
    {
        await Task.CompletedTask;
        var namespaces = _index.GetNamespaces(_principal.TenantId);
        var debateNamespaces = namespaces
            .Where(n => n.StartsWith("active-debate-"))
            // Dry-run details are sensitive too, so use the execution permission for both
            // modes. Once a debate session is claimed by its creator, other principals can
            // neither enumerate nor delete it through this maintenance path.
            .Where(CanWrite)
            .ToList();

        if (debateNamespaces.Count == 0)
            return new PurgeDebatesResult(0, 0, 0, 0, dryRun, Array.Empty<PurgedNamespaceInfo>());

        var cutoff = DateTimeOffset.UtcNow.AddHours(-maxAgeHours);
        var purged = new List<PurgedNamespaceInfo>();
        int totalEntriesRemoved = 0;
        int totalEdgesRemoved = 0;
        int totalIdsSkippedAmbiguous = 0;
        int totalIdsDeferredUnsettled = 0;

        // PHASE 1 — every cascade runs before ANY entry is deleted. Ambiguity is judged against
        // the store as this pass found it: deleting namespace A's entries before cascading
        // namespace B would UN-ambiguATE an id the two stale namespaces share, so the real purge
        // would remove edges the dry run reported as skipped — the exact preview divergence the
        // shared CascadeAll call exists to prevent, reintroduced one level up by interleaving
        // deletions with cascades.
        var staged = new List<(string Ns, IReadOnlyList<CognitiveEntry> Entries, Dictionary<string, long> StagedRevisions, CascadeOutcome Cascade, DateTimeOffset? NewestAt)>();
        // One dedup set for the WHOLE preview: an edge between entries in two stale namespaces
        // is incident to both cascades, and per-call dedup would count it once per side while
        // the real purge removes it once.
        var previewEdgeDedup = dryRun ? new HashSet<(string Source, string Target, string Relation)>() : null;
        foreach (var debateNs in debateNamespaces)
        {
            // The occupancy baseline is captured BEFORE the entry listing: everything below —
            // the snapshot, the staleness judgment, the revision witnesses, the cascade — is
            // staged work, and the watch can only vouch for replacements that land after its
            // own capture.
            long occupancyBaseline = _index.OccupancyRevisionFor(debateNs, _principal.TenantId);
            var entries = _index.GetAllInNamespace(debateNs, _principal.TenantId);
            if (entries.Count == 0)
            {
                // Empty namespace — staged for deletion in phase 2 like everything else. Phase 1
                // performs no deletions at all: even an "empty" namespace's wholesale delete
                // here would race an entry created mid-pass, and the phase separation is the
                // whole preview-parity argument. default(CascadeOutcome) is inert by design.
                staged.Add((debateNs, entries, new Dictionary<string, long>(StringComparer.Ordinal), default, null));
                continue;
            }

            // Check age using the most recent entry's CreatedAt timestamp
            var newestEntry = entries.MaxBy(e => e.CreatedAt);
            if (newestEntry is null || newestEntry.CreatedAt >= cutoff)
                continue; // Not stale yet

            // Graph and cluster topology is keyed by bare id, but an id is only unique per
            // (tenant, namespace) — so cascading on a debate entry's id can tear out edges and
            // cluster memberships belonging to a same-named entry in a live namespace. The
            // cascade therefore re-resolves each id and skips the ambiguous ones.
            //
            // Both branches go through the SAME call with only `apply` differing, so the dry
            // run cannot report a different edge count from the purge it is previewing — that
            // exact divergence was already found and fixed once (CHANGELOG "dry-run count").
            //
            // Swept ids are VERSION-CHECKED against the staging snapshot first: a bare id whose
            // entry was replaced since staging now names the FRESH entry's topology, and
            // cascading it would tear down what belongs to work this pass never judged — the
            // stale version's own topology was already cascaded by whatever deleted it, or is
            // the tolerated dangling residual. Phase 2's conditional delete skips the same ids,
            // so a replaced entry keeps both its topology and itself. Revision is the witness —
            // a same-tick replacement can repeat CreatedAt, but never a Revision. And a
            // replacement landing AFTER this filter is caught by the cascade itself: watchNs
            // puts the staged partition's occupancy revision inside the apply bracket, so an
            // entry written or removed there mid-sweep sends its id to UnsettledIds instead of
            // being swept on a judgment the replacement invalidated.
            // The witness is frozen HERE, as numbers: the objects the listing returned are the
            // live map occupants, and a re-stamp through them would silently retarget any check
            // that read Revision later. Every judgment below — the sweep filter, the cascade's
            // occupancy watch by extension, and phase 2's conditional delete — uses these staged
            // values, never the live property.
            var stagedRevisions = new Dictionary<string, long>(entries.Count, StringComparer.Ordinal);
            foreach (var entry in entries)
                stagedRevisions[entry.Id] = entry.Revision;

            var sweepIds = new List<string>(entries.Count);
            foreach (var entry in entries)
            {
                var current = _index.Get(entry.Id, debateNs, _principal.TenantId);
                if (current is not null && current.Revision == stagedRevisions[entry.Id])
                    sweepIds.Add(entry.Id);
            }

            var cascade = TopologyCascade.CascadeAll(
                _index, _graph, _clusters,
                sweepIds,
                _principal.TenantId,
                apply: !dryRun,
                watchNs: debateNs,
                dryRunEdgeDedup: previewEdgeDedup,
                watchOccupancyBaseline: occupancyBaseline);

            staged.Add((debateNs, entries, stagedRevisions, cascade, newestEntry.CreatedAt));
        }

        // PHASE 2 — deletions, after every cascade has judged the untouched store.
        foreach (var (debateNs, entries, stagedRevisions, cascade, newestAt) in staged)
        {
            if (entries.Count == 0)
            {
                if (!dryRun)
                {
                    // Deferred from phase 1, and conditional IN CORE: the emptiness check and
                    // the removal run under one partition write lock, so an entry created at
                    // any point since staging keeps its namespace — it is left for a future
                    // pass to age-check rather than wholesale-deleted underneath it.
                    _index.DeleteAllInNamespaceIfEmpty(debateNs, _principal.TenantId);
                }
                purged.Add(new PurgedNamespaceInfo(debateNs, 0, 0, 0, null));
                continue;
            }

            int entryCount = entries.Count;
            int entriesRemoved = entryCount;
            if (!dryRun)
            {
                // Residual, deliberately accepted: for a skipped ambiguous id the entry itself
                // still goes (deletion is namespace-scoped, so it can only touch this debate
                // namespace) while its edges are left dangling. Dangling edges are an
                // already-tolerated graph state — GetNeighbors and Traverse both skip
                // endpoints that no longer resolve — and are strictly preferable to silently
                // destroying another namespace's live topology.
                //
                // Deletion is per STAGED entry, never wholesale: phase 2 runs after every
                // cascade of the pass, and an entry created (or re-created) in that gap was
                // neither age-checked nor cascaded — a wholesale namespace delete would take it
                // down anyway while the totals described the old snapshot. CreatedAt is the
                // version witness available here; a mismatch means the id names newer work and
                // the entry is left for a future pass to judge. The unsettled ids are also
                // kept: their cleanup was attempted and cannot be proven complete, and an entry
                // whose topology may be half-removed must outlive its topology.
                var unsettled = cascade.IdsUnsettled > 0
                    ? cascade.UnsettledIds.ToHashSet(StringComparer.Ordinal)
                    : null;
                int deleted = 0;
                foreach (var entry in entries)
                {
                    if (unsettled is not null && unsettled.Contains(entry.Id)) continue;

                    // Conditional on the staged OCCUPATION, atomically: the revision check and
                    // the removal run under one partition write lock inside Core, so an entry
                    // replaced at ANY point since staging survives — a separate check-then-
                    // delete here would still race the replacement in the gap between the two,
                    // and CreatedAt could not even witness a same-tick replacement. The staged
                    // NUMBER, never the live property, is what is compared.
                    if (_index.Delete(entry.Id, debateNs, _principal.TenantId, onlyIfRevision: stagedRevisions[entry.Id]))
                        deleted++;
                }
                entriesRemoved = deleted;
                if (unsettled is not null)
                    totalIdsDeferredUnsettled += unsettled.Count;
            }

            totalEntriesRemoved += entriesRemoved;
            totalEdgesRemoved += cascade.EdgesRemoved;
            totalIdsSkippedAmbiguous += cascade.IdsSkippedAmbiguous;
            purged.Add(new PurgedNamespaceInfo(
                debateNs, entriesRemoved, cascade.EdgesRemoved, cascade.IdsSkippedAmbiguous, newestAt));
        }

        // A real purge can span hundreds of namespaces; returning every one in full detail
        // buries the totals that actually drive the go/no-go decision. Cap the detail,
        // never the totals.
        var detail = detailLimit > 0 && purged.Count > detailLimit
            ? purged.Take(detailLimit).ToList()
            : purged;

        return new PurgeDebatesResult(
            purged.Count, totalEntriesRemoved, totalEdgesRemoved, totalIdsSkippedAmbiguous, dryRun, detail,
            totalIdsDeferredUnsettled);
    }
}

/// <param name="IdsSkippedAmbiguous">
/// Entry ids whose namespace could not be resolved unambiguously within the tenant, so their
/// edges and cluster memberships were left in place. Reported rather than swallowed: a purge
/// that quietly leaves topology behind is harder to reason about than one that says so.
/// </param>
public sealed record PurgedNamespaceInfo(
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("entryCount")] int EntryCount,
    [property: JsonPropertyName("edgeCount")] int EdgeCount,
    [property: JsonPropertyName("idsSkippedAmbiguous")] int IdsSkippedAmbiguous,
    [property: JsonPropertyName("newestEntryAt")] DateTimeOffset? NewestEntryAt);

/// <param name="TotalIdsDeferredUnsettled">
/// Entry ids left in place this pass because their topology cascade could not be proven complete
/// (the tenant's attribution kept moving). An entry whose cleanup is unproven must outlive its
/// topology, so it is kept while every other entry of its stale namespace goes; the next purge
/// pass retries exactly these. Trailing so existing positional construction and serialized
/// shapes stay valid.
/// </param>
public sealed record PurgeDebatesResult(
    [property: JsonPropertyName("namespacesAffected")] int NamespacesAffected,
    [property: JsonPropertyName("totalEntriesRemoved")] int TotalEntriesRemoved,
    [property: JsonPropertyName("totalEdgesRemoved")] int TotalEdgesRemoved,
    [property: JsonPropertyName("totalIdsSkippedAmbiguous")] int TotalIdsSkippedAmbiguous,
    [property: JsonPropertyName("dryRun")] bool DryRun,
    [property: JsonPropertyName("namespaces")] IReadOnlyList<PurgedNamespaceInfo> Namespaces,
    [property: JsonPropertyName("totalIdsDeferredUnsettled")] int TotalIdsDeferredUnsettled = 0);
