using System.ComponentModel;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using ModelContextProtocol.Server;
using static McpEngramMemory.Core.Models.ToolError;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for semantic clustering operations.
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
        if (_access.RequiresTenantQualifiedStructures)
            return NamespaceAccess.TenantStructureUnavailable;
        if (!_access.CanWrite(ns)) return NamespaceAccess.WriteDenied(ns);

        try
        {
            var ids = memberIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            return _clusters.CreateCluster(clusterId, ns, ids, label);
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
        if (_access.RequiresTenantQualifiedStructures)
            return NamespaceAccess.TenantStructureUnavailable;
        // Cluster ownership isn't known until the cluster itself is resolved. Same reply
        // shape as a genuine miss - a distinct denial would confirm the cluster exists in a
        // namespace this caller cannot see.
        var clusterNs = _clusters.GetCluster(clusterId)?.Namespace;
        if (clusterNs is null || !_access.CanWrite(clusterNs))
            return $"Error: Cluster '{clusterId}' not found.";

        var addIds = addMemberIds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var removeIds = removeMemberIds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return _clusters.UpdateCluster(clusterId, addIds, removeIds, label);
    }

    [McpServerTool(Name = "store_cluster_summary", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Store an LLM-generated summary as a searchable entry tied to a cluster. Enables summaryFirst search mode for the cluster.")]
    public string StoreClusterSummary(
        [Description("Cluster to summarize.")] string clusterId,
        [Description("Generated summary text.")] string summaryText,
        [Description("Embedding of the summary.")] float[]? summaryVector = null)
    {
        if (_access.RequiresTenantQualifiedStructures)
            return NamespaceAccess.TenantStructureUnavailable;
        var clusterNs = _clusters.GetCluster(clusterId)?.Namespace;
        if (clusterNs is null || !_access.CanWrite(clusterNs))
            return $"Error: Cluster '{clusterId}' not found.";

        var resolved = summaryVector is not null && summaryVector.Length > 0
            ? summaryVector
            : _embedding.Embed(summaryText);

        var result = _clusters.StoreSummary(clusterId, summaryText, resolved);
        if (result.StartsWith("Error:")) return result;

        _access.ClaimOnWrite(clusterNs);
        return $"Stored summary entry '{result}'.";
    }

    [McpServerTool(Name = "get_cluster", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Get cluster details: members, centroid, summary, and label.")]
    public object GetCluster(
        [Description("Cluster ID.")] string clusterId)
    {
        if (_access.RequiresTenantQualifiedStructures)
            return $"Cluster '{clusterId}' not found.";
        var result = _clusters.GetCluster(clusterId);
        if (result is null || !_access.CanRead(result.Namespace))
            return $"Cluster '{clusterId}' not found.";

        // Clusters are global structures; individual members can live outside the
        // cluster's own namespace if they were added by id, so filter them independently.
        var visibleMembers = result.Members.Where(m => _access.CanRead(m.Namespace)).ToList();
        return result with { Members = visibleMembers };
    }

    [McpServerTool(Name = "list_clusters", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List all clusters in a namespace with summary status.")]
    public IReadOnlyList<ClusterSummaryInfo> ListClusters(
        [Description("Namespace.")] string ns)
    {
        if (_access.RequiresTenantQualifiedStructures)
            return Array.Empty<ClusterSummaryInfo>();
        if (!_access.CanRead(ns)) return Array.Empty<ClusterSummaryInfo>();
        return _clusters.ListClusters(ns);
    }
}
