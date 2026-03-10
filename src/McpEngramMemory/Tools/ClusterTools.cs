using System.ComponentModel;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for semantic clustering operations.
/// </summary>
[McpServerToolType]
public sealed class ClusterTools
{
    private readonly ClusterManager _clusters;
    private readonly IEmbeddingService _embedding;

    public ClusterTools(ClusterManager clusters, IEmbeddingService embedding)
    {
        _clusters = clusters;
        _embedding = embedding;
    }

    [McpServerTool(Name = "create_cluster")]
    [Description("Group entries into a semantic cluster with computed centroid.")]
    public string CreateCluster(
        [Description("Cluster identifier.")] string clusterId,
        [Description("Namespace.")] string ns,
        [Description("Comma-separated initial member entry IDs.")] string memberIds,
        [Description("Human-readable cluster name.")] string? label = null)
    {
        try
        {
            var ids = memberIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            return _clusters.CreateCluster(clusterId, ns, ids, label);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
        }
    }

    [McpServerTool(Name = "update_cluster")]
    [Description("Add/remove members or update label. Centroid recomputed automatically.")]
    public string UpdateCluster(
        [Description("Cluster to modify.")] string clusterId,
        [Description("Comma-separated entry IDs to add.")] string? addMemberIds = null,
        [Description("Comma-separated entry IDs to remove.")] string? removeMemberIds = null,
        [Description("New label.")] string? label = null)
    {
        var addIds = addMemberIds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var removeIds = removeMemberIds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return _clusters.UpdateCluster(clusterId, addIds, removeIds, label);
    }

    [McpServerTool(Name = "store_cluster_summary")]
    [Description("Store an LLM-generated summary as a searchable entry tied to a cluster.")]
    public string StoreClusterSummary(
        [Description("Cluster to summarize.")] string clusterId,
        [Description("Generated summary text.")] string summaryText,
        [Description("Embedding of the summary.")] float[]? summaryVector = null)
    {
        var resolved = summaryVector is not null && summaryVector.Length > 0
            ? summaryVector
            : _embedding.Embed(summaryText);

        var result = _clusters.StoreSummary(clusterId, summaryText, resolved);
        return result.StartsWith("Error:") ? result : $"Stored summary entry '{result}'.";
    }

    [McpServerTool(Name = "get_cluster")]
    [Description("Retrieve cluster details, members, and summary.")]
    public object GetCluster(
        [Description("Cluster ID.")] string clusterId)
    {
        var result = _clusters.GetCluster(clusterId);
        return result is not null ? result : $"Cluster '{clusterId}' not found.";
    }

    [McpServerTool(Name = "list_clusters")]
    [Description("List all clusters in a namespace with summary status.")]
    public IReadOnlyList<ClusterSummaryInfo> ListClusters(
        [Description("Namespace.")] string ns)
    {
        return _clusters.ListClusters(ns);
    }
}
