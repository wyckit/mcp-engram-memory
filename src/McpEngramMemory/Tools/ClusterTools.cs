using System.ComponentModel;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using ModelContextProtocol.Server;
using static McpEngramMemory.Core.Models.ToolError;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for semantic clustering operations. Every operation is scoped to the caller's tenant
/// (<see cref="NamespaceAccess.TenantId"/>); the legacy tenant ("") behaves exactly as before.
/// </summary>
[McpServerToolType]
public sealed class ClusterTools
{
    private readonly ClusterManager _clusters;
    private readonly IEmbeddingService _embedding;
    private readonly NamespaceAccess _access;

    public ClusterTools(ClusterManager clusters, IEmbeddingService embedding, NamespaceAccess access)
    {
        _clusters = clusters;
        _embedding = embedding;
        _access = access;
    }

    [McpServerTool(Name = "create_cluster", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Group entries into a semantic cluster with auto-computed centroid. Use for manual clustering when accretion scan isn't suitable.")]
    public object CreateCluster(
        [Description("Cluster identifier.")] string clusterId,
        [Description("Namespace.")] string ns,
        [Description("Comma-separated initial member entry IDs.")] string memberIds,
        [Description("Human-readable cluster name.")] string? label = null)
    {
        if (!_access.CanWrite(ns)) return NamespaceAccess.WriteDenied(ns);

        try
        {
            var ids = memberIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            return _clusters.CreateCluster(clusterId, ns, ids, label, tenantId: _access.TenantId);
        }
        catch (Exception ex)
        {
            return FromException(ex);
        }
    }

    [McpServerTool(Name = "update_cluster", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Add or remove cluster members and update label. Centroid recomputes automatically.")]
    public string UpdateCluster(
        [Description("Cluster to modify.")] string clusterId,
        [Description("Comma-separated entry IDs to add.")] string? addMemberIds = null,
        [Description("Comma-separated entry IDs to remove.")] string? removeMemberIds = null,
        [Description("New label.")] string? label = null)
    {
        // Cluster ownership isn't known until the cluster itself is resolved (within this tenant).
        // Same reply shape as a genuine miss - a distinct denial would confirm the cluster exists in
        // a namespace this caller cannot see.
        var clusterNs = _clusters.GetCluster(clusterId, tenantId: _access.TenantId)?.Namespace;
        if (clusterNs is null || !_access.CanWrite(clusterNs))
            return $"Error: Cluster '{clusterId}' not found.";

        var addIds = addMemberIds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var removeIds = removeMemberIds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return _clusters.UpdateCluster(clusterId, addIds, removeIds, label, tenantId: _access.TenantId);
    }

    [McpServerTool(Name = "store_cluster_summary", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Store an LLM-generated summary as a searchable entry tied to a cluster. Enables summaryFirst search mode for the cluster.")]
    public string StoreClusterSummary(
        [Description("Cluster to summarize.")] string clusterId,
        [Description("Generated summary text.")] string summaryText,
        [Description("Embedding of the summary.")] float[]? summaryVector = null)
    {
        var clusterNs = _clusters.GetCluster(clusterId, tenantId: _access.TenantId)?.Namespace;
        if (clusterNs is null || !_access.CanWrite(clusterNs))
            return $"Error: Cluster '{clusterId}' not found.";

        var resolved = summaryVector is not null && summaryVector.Length > 0
            ? summaryVector
            : _embedding.Embed(summaryText);

        var result = _clusters.StoreSummary(clusterId, summaryText, resolved, tenantId: _access.TenantId);
        if (result.StartsWith("Error:")) return result;

        _access.ClaimOnWrite(clusterNs);
        return $"Stored summary entry '{result}'.";
    }

    [McpServerTool(Name = "get_cluster", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Get cluster details: members, centroid, summary, and label.")]
    public object GetCluster(
        [Description("Cluster ID.")] string clusterId)
    {
        var result = _clusters.GetCluster(clusterId, tenantId: _access.TenantId);
        if (result is null || !_access.CanRead(result.Namespace))
            return $"Cluster '{clusterId}' not found.";

        // Two independent tests stand between a stored membership and this reply, and only one of
        // them belongs at the tool.
        //
        // ATTRIBUTION is settled in Core. Membership is keyed (tenant, id) with no namespace, so an
        // id the tenant holds in two namespaces names ONE bucket shared by two entries;
        // ClusterManager's projection therefore withholds such a member before it ever reaches here.
        // That test is ACL-blind and could not be made at this layer even if it were repeated: the
        // twin that makes the bucket shared is exactly the one this caller cannot see, and the bare
        // id resolves to whichever twin the locator picks — quite possibly the CALLER'S OWN readable
        // one, which would then be presented as a member of somebody else's cluster and pass every
        // check below.
        //
        // ACCESS is genuinely this tool's, because it is the only layer that has a principal. A
        // member that survived Core's projection is attributable to exactly one entry, so its
        // Namespace is authoritative and CanRead on it means what it says. It is not implied by the
        // read check on the cluster above: members added by id can live outside the cluster's own
        // namespace. ProjectForPrincipal applies it, and carries the member count and the staleness
        // bit along with it so that nothing describing the member set outlives the filter.
        return ProjectForPrincipal(result);
    }

    [McpServerTool(Name = "list_clusters", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List all clusters in a namespace with summary status.")]
    public IReadOnlyList<ClusterSummaryInfo> ListClusters(
        [Description("Namespace.")] string ns)
    {
        if (!_access.CanRead(ns)) return Array.Empty<ClusterSummaryInfo>();

        var listed = _clusters.ListClusters(ns, tenantId: _access.TenantId);
        if (listed.Count == 0) return Array.Empty<ClusterSummaryInfo>();

        // The listing carries the same two facts get_cluster does — how many members, and whether a
        // summary is there to be had — so it has to be narrowed by the same principal, through the
        // same projection. Reporting Core's ACL-blind figures here would re-open the disclosure one
        // tool over: a caller who cannot see a member in get_cluster would simply read its existence
        // off list_clusters instead, which is the cheaper call of the two.
        //
        // A member carries its namespace only once it has been RESOLVED, so an honest count costs
        // the per-member index lookups get_cluster already pays, once per cluster. That cost is
        // taken deliberately. There is no cheaper exact shape available at this layer: Core cannot
        // pre-filter (it has no principal), and a security-relevant count must not be approximated —
        // any figure larger than the visible list is the oracle, and any figure smaller is a lie.
        var projected = new List<ClusterSummaryInfo>(listed.Count);
        foreach (var info in listed)
        {
            // A cluster the listing named but whose projection cannot be produced is dropped, not
            // reported with a fabricated zero: an unavailable projection is not an empty one, and a
            // zero count would describe a member set nobody enumerated.
            var detail = _clusters.GetCluster(info.ClusterId, tenantId: _access.TenantId);
            if (detail is null) continue;

            var visible = ProjectForPrincipal(detail);
            projected.Add(info with
            {
                MemberCount = visible.MemberCount,
                // HasSummary advertises a summary this caller can actually obtain from get_cluster.
                // Left as "a summary id is stored", it would report true for one get_cluster
                // withholds — the "an entry you cannot see answers to this id" bit, restated as a
                // flag instead of as a count.
                HasSummary = visible.SummaryEntry is not null,
            });
        }

        return projected;
    }

    /// <summary>
    /// Narrow a cluster to what this principal may read, and make every field that DESCRIBES the
    /// member set agree with the members actually handed back. One projection serves both
    /// get_cluster and list_clusters so the two can never disagree about a cluster's size.
    ///
    /// <c>MemberCount</c> is recomputed here rather than carried through, and it is the same defect
    /// the find_contradictions count already cost once: a count taken before a filter and returned
    /// beside the filtered payload states precisely what the filter withheld. Core's figure is
    /// correct at Core's layer — it counts topology-attributable memberships and has no principal to
    /// filter by — so the recomputation belongs at the one layer that has one.
    ///
    /// Counting the list the caller receives is also what keeps the three outcomes indistinguishable
    /// that must stay so: a member that resolves to nothing, one in a namespace this caller cannot
    /// read, and one Core withheld as unattributable are now all simply absent — from the list and
    /// from the count alike. Core keeps its own divergence between MemberCount and Members for a
    /// dangling member, which is a statement about storage rather than about a principal; the
    /// divergence stops here, where a principal could read a suppression off it.
    /// </summary>
    private GetClusterResult ProjectForPrincipal(GetClusterResult result)
    {
        var visibleMembers = result.Members.Where(m => _access.CanRead(m.Namespace)).ToList();

        // Staleness is a one-bit claim about the members' timestamps, and Core computed it over the
        // members it resolved — including the ones just withheld. Reported as-is it says "something
        // you may not read is newer than this summary", which is the same disclosure as the count.
        //
        // It is therefore reported only when the visible projection IS the whole projection, in
        // which case Core's bit already describes exactly these members and needs no recomputation.
        // Otherwise it is withheld as false, and false is the only available answer rather than a
        // chosen one: staleness needs each member's CreatedAt, and withholding a member withholds
        // its timestamp with it. No oracle survives the choice — the withholding branch is a
        // constant, and false is equally reachable when nothing was withheld at all.
        var isStale = visibleMembers.Count == result.Members.Count && result.IsStale;

        return result with
        {
            MemberCount = visibleMembers.Count,
            Members = visibleMembers,
            IsStale = isStale,
        };
    }
}
