using System.ComponentModel;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for inspection: get_memory and cognitive_stats.
/// </summary>
[McpServerToolType]
public sealed class AdminTools
{
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;

    public AdminTools(CognitiveIndex index, KnowledgeGraph graph, ClusterManager clusters)
    {
        _index = index;
        _graph = graph;
        _clusters = clusters;
    }

    [McpServerTool(Name = "get_memory")]
    [Description("Retrieve full cognitive context of a single entry (lifecycle, edges, clusters). Does NOT count as an access.")]
    public object GetMemory(
        [Description("Entry ID.")] string id)
    {
        var entry = _index.Get(id);
        if (entry is null)
            return $"Entry '{id}' not found.";

        var edges = _graph.GetEdgesForEntry(id);
        var clusterIds = _clusters.GetClustersForEntry(id);

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

    [McpServerTool(Name = "cognitive_stats")]
    [Description("System overview: entry counts by state, cluster count, edge count, namespaces.")]
    public LifecycleStats CognitiveStats(
        [Description("Namespace ('*' for all, default).")] string ns = "*")
    {
        var (stm, ltm, archived) = _index.GetStateCounts(ns);
        var namespaces = _index.GetNamespaces();
        var edgeCount = _graph.EdgeCount;
        var clusterCount = _clusters.ClusterCount;

        return new LifecycleStats(
            stm + ltm + archived,
            stm, ltm, archived,
            clusterCount, edgeCount,
            namespaces);
    }
}
