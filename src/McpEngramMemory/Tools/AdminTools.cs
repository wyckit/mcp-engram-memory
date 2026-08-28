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

        // Graph topology is global and edges carry bare endpoint IDs. Returning an edge
        // to a private endpoint discloses that endpoint's ID, relationship, and metadata
        // even when its entry body is protected. Project the edge set through the same
        // entry-level read policy as the primary object.
        var edges = _graph.GetEdgesForEntry(id, tenantId: _principal.TenantId)
            .Where(edge => CanReadEndpoint(edge.SourceId) && CanReadEndpoint(edge.TargetId))
            .ToList();
        // Cluster membership is the same kind of disclosure as an edge: a cluster id names a
        // grouping that lives in some namespace, and co-membership tells the caller that this
        // entry was grouped with content they cannot read. Membership is deliberately allowed
        // to span namespaces, so the gate is the cluster's OWN namespace — CanRead(m.Ns), the
        // same predicate ClusterTools.GetCluster applies — not equality with entry.Ns.
        var clusterIds = _clusters.GetClusterMembershipsForEntry(id, tenantId: _principal.TenantId)
            .Where(m => CanRead(m.Ns))
            .Select(m => m.ClusterId)
            .ToList();

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
            edgeCount = _graph.GetAllEdges(_principal.TenantId).Count(edge =>
                visibleIds.Contains(edge.SourceId) && visibleIds.Contains(edge.TargetId));
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

        foreach (var debateNs in debateNamespaces)
        {
            var entries = _index.GetAllInNamespace(debateNs, _principal.TenantId);
            if (entries.Count == 0)
            {
                // Empty namespace — always purge
                if (!dryRun)
                {
                    _index.DeleteAllInNamespace(debateNs, _principal.TenantId);
                }
                purged.Add(new PurgedNamespaceInfo(debateNs, 0, 0, 0, null));
                continue;
            }

            // Check age using the most recent entry's CreatedAt timestamp
            var newestEntry = entries.MaxBy(e => e.CreatedAt);
            if (newestEntry is null || newestEntry.CreatedAt >= cutoff)
                continue; // Not stale yet

            int entryCount = entries.Count;

            // Graph and cluster topology is keyed by bare id, but an id is only unique per
            // (tenant, namespace) — so cascading on a debate entry's id can tear out edges and
            // cluster memberships belonging to a same-named entry in a live namespace. The
            // cascade therefore re-resolves each id and skips the ambiguous ones.
            //
            // Both branches go through the SAME call with only `apply` differing, so the dry
            // run cannot report a different edge count from the purge it is previewing — that
            // exact divergence was already found and fixed once (CHANGELOG "dry-run count").
            var cascade = TopologyCascade.CascadeAll(
                _index, _graph, _clusters,
                entries.Select(e => e.Id),
                _principal.TenantId,
                apply: !dryRun);

            if (!dryRun)
            {
                // Residual, deliberately accepted: for a skipped ambiguous id the entry itself
                // still goes (DeleteAllInNamespace is namespace-scoped, so it can only touch
                // this debate namespace) while its edges are left dangling. Dangling edges are
                // an already-tolerated graph state — GetNeighbors and Traverse both skip
                // endpoints that no longer resolve — and are strictly preferable to silently
                // destroying another namespace's live topology.
                _index.DeleteAllInNamespace(debateNs, _principal.TenantId);
            }

            totalEntriesRemoved += entryCount;
            totalEdgesRemoved += cascade.EdgesRemoved;
            totalIdsSkippedAmbiguous += cascade.IdsSkippedAmbiguous;
            purged.Add(new PurgedNamespaceInfo(
                debateNs, entryCount, cascade.EdgesRemoved, cascade.IdsSkippedAmbiguous, newestEntry.CreatedAt));
        }

        // A real purge can span hundreds of namespaces; returning every one in full detail
        // buries the totals that actually drive the go/no-go decision. Cap the detail,
        // never the totals.
        var detail = detailLimit > 0 && purged.Count > detailLimit
            ? purged.Take(detailLimit).ToList()
            : purged;

        return new PurgeDebatesResult(
            purged.Count, totalEntriesRemoved, totalEdgesRemoved, totalIdsSkippedAmbiguous, dryRun, detail);
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

public sealed record PurgeDebatesResult(
    [property: JsonPropertyName("namespacesAffected")] int NamespacesAffected,
    [property: JsonPropertyName("totalEntriesRemoved")] int TotalEntriesRemoved,
    [property: JsonPropertyName("totalEdgesRemoved")] int TotalEdgesRemoved,
    [property: JsonPropertyName("totalIdsSkippedAmbiguous")] int TotalIdsSkippedAmbiguous,
    [property: JsonPropertyName("dryRun")] bool DryRun,
    [property: JsonPropertyName("namespaces")] IReadOnlyList<PurgedNamespaceInfo> Namespaces);
