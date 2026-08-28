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
        // Namespace is authoritative and CanRead on it means what it says. The filter is not implied
        // by the read check on the cluster above: members added by id can live outside the cluster's
        // own namespace.
        var visibleMembers = result.Members.Where(m => _access.CanRead(m.Namespace)).ToList();
        return result with { Members = visibleMembers };
    }

    [McpServerTool(Name = "list_clusters", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List all clusters in a namespace with summary status.")]
    public IReadOnlyList<ClusterSummaryInfo> ListClusters(
        [Description("Namespace.")] string ns)
    {
        if (!_access.CanRead(ns)) return Array.Empty<ClusterSummaryInfo>();
        return _clusters.ListClusters(ns, tenantId: _access.TenantId);
    }
}
