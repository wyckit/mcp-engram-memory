using System.ComponentModel;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for knowledge graph operations: link, unlink, neighbors, traverse.
/// </summary>
[McpServerToolType]
public sealed class GraphTools
{
    private readonly KnowledgeGraph _graph;

    public GraphTools(KnowledgeGraph graph)
    {
        _graph = graph;
    }

    [McpServerTool(Name = "link_memories")]
    [Description("Create a directed graph edge between two entries. Use 'cross_reference' for bidirectional links (auto-creates reverse edge).")]
    public string LinkMemories(
        [Description("Edge origin entry ID.")] string sourceId,
        [Description("Edge destination entry ID.")] string targetId,
        [Description("Relation type: 'parent_child', 'cross_reference', 'similar_to', 'contradicts', 'elaborates', 'depends_on', or custom.")] string relation,
        [Description("Edge weight 0.0-1.0 (default: 1.0).")] float weight = 1.0f,
        [Description("Optional edge metadata.")] Dictionary<string, string>? metadata = null)
    {
        try
        {
            var edge = new GraphEdge(sourceId, targetId, relation, weight, metadata);
            return _graph.AddEdge(edge);
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "unlink_memories")]
    [Description("Remove edge(s) between two memory entries.")]
    public string UnlinkMemories(
        [Description("Edge origin entry ID.")] string sourceId,
        [Description("Edge destination entry ID.")] string targetId,
        [Description("Specific relation to remove (null = all).")] string? relation = null)
    {
        return _graph.RemoveEdges(sourceId, targetId, relation);
    }

    [McpServerTool(Name = "get_neighbors")]
    [Description("Get entries directly connected to a node in the knowledge graph. Use to explore relationships around a specific memory.")]
    public GetNeighborsResult GetNeighbors(
        [Description("Entry ID to find neighbors for.")] string id,
        [Description("Filter by relation type.")] string? relation = null,
        [Description("Direction: 'outgoing', 'incoming', or 'both' (default).")] string direction = "both")
    {
        return _graph.GetNeighbors(id, relation, direction);
    }

    [McpServerTool(Name = "traverse_graph")]
    [Description("Multi-hop graph traversal from a starting entry. Use to discover transitive relationships and knowledge chains.")]
    public TraversalResult TraverseGraph(
        [Description("Starting entry ID.")] string startId,
        [Description("Maximum hops (default: 2, max: 5).")] int maxDepth = 2,
        [Description("Filter by edge type.")] string? relation = null,
        [Description("Minimum edge weight (default: 0.0).")] float minWeight = 0f,
        [Description("Result limit (default: 20).")] int maxResults = 20)
    {
        return _graph.Traverse(startId, maxDepth, relation, minWeight, maxResults);
    }
}
