using System.ComponentModel;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for knowledge graph operations: link, unlink, neighbors, traverse.
///
/// Every operation is scoped to the caller's tenant (<see cref="NamespaceAccess.TenantId"/>): edges
/// are created, queried, and traversed within that tenant only, so a tenant can never see or touch
/// another tenant's graph. The legacy tenant ("") behaves exactly as before.
/// </summary>
[McpServerToolType]
public sealed class GraphTools
{
    private readonly KnowledgeGraph _graph;
    private readonly AutoLinkScanner _autoLink;
    private readonly CognitiveIndex _index;
    private readonly NamespaceAccess _access;

    public GraphTools(KnowledgeGraph graph, AutoLinkScanner autoLink, CognitiveIndex index, NamespaceAccess access)
    {
        _graph = graph;
        _autoLink = autoLink;
        _index = index;
        _access = access;
    }

    /// <summary>
    /// Access check for an edge endpoint reached by id. Graph edges carry no namespace of
    /// their own, so the only way to know whether a caller may touch one is to resolve the
    /// entry (within the caller's tenant) and check its namespace. Same reply shape as a genuine
    /// miss - a distinct denial would confirm the id exists in a namespace the caller cannot see.
    /// </summary>
    private string? DenyIfCannotWrite(string id)
    {
        var entry = _access.TenantId.Length == 0 ? _index.Get(id) : _index.GetForTenant(id, _access.TenantId);
        if (entry is null || !_access.CanWrite(entry.Ns))
            return $"Error: Entry '{id}' not found.";
        return null;
    }

    [McpServerTool(Name = "link_memories", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
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
            var edge = new GraphEdge(sourceId, targetId, relation, weight, metadata, _access.TenantId);
            return DenyIfCannotWrite(sourceId) ?? DenyIfCannotWrite(targetId) ?? _graph.AddEdge(edge);
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "unlink_memories", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Remove edge(s) between two memory entries.")]
    public string UnlinkMemories(
        [Description("Edge origin entry ID.")] string sourceId,
        [Description("Edge destination entry ID.")] string targetId,
        [Description("Specific relation to remove (null = all).")] string? relation = null)
    {
        return DenyIfCannotWrite(sourceId) ?? DenyIfCannotWrite(targetId)
            ?? _graph.RemoveEdges(sourceId, targetId, relation, _access.TenantId);
    }

    [McpServerTool(Name = "get_neighbors", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Get entries directly connected to a node in the knowledge graph. Use to explore relationships around a specific memory.")]
    public GetNeighborsResult GetNeighbors(
        [Description("Entry ID to find neighbors for.")] string id,
        [Description("Filter by relation type.")] string? relation = null,
        [Description("Direction: 'outgoing', 'incoming', or 'both' (default).")] string direction = "both")
    {
        var result = _graph.GetNeighbors(id, relation, direction, _access.TenantId);

        // Edges are tenant-scoped but span namespaces, so a neighbor can live in a namespace this
        // caller may not read. Filter, don't deny: the id the caller passed in is already known to
        // them, so only the resolved neighbors need to be hidden.
        var visible = result.Neighbors.Where(n => _access.CanRead(n.Entry.Namespace)).ToList();
        return new GetNeighborsResult(result.Id, visible);
    }

    [McpServerTool(Name = "traverse_graph", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Multi-hop graph traversal from a starting entry. Use to discover transitive relationships and knowledge chains.")]
    public TraversalResult TraverseGraph(
        [Description("Starting entry ID.")] string startId,
        [Description("Maximum hops (default: 2, max: 5).")] int maxDepth = 2,
        [Description("Filter by edge type.")] string? relation = null,
        [Description("Minimum edge weight (default: 0.0).")] float minWeight = 0f,
        [Description("Result limit (default: 20).")] int maxResults = 20)
    {
        var result = _graph.Traverse(startId, maxDepth, relation, minWeight, maxResults, _access.TenantId);

        // The start entry itself is included in Entries, so it must be filtered too -
        // otherwise a caller could learn the text of an unreadable entry just by naming it
        // as the traversal root. Drop any edge whose endpoint fell out of the visible set.
        var visibleEntries = result.Entries.Where(e => _access.CanRead(e.Namespace)).ToList();
        var visibleIds = visibleEntries.Select(e => e.Id).ToHashSet();
        var visibleEdges = result.Edges
            .Where(e => visibleIds.Contains(e.SourceId) && visibleIds.Contains(e.TargetId))
            .ToList();

        return new TraversalResult(result.StartId, visibleEntries, visibleEdges);
    }

    public AutoLinkResult AutoLinkNamespace(
        [Description("Namespace to scan.")] string ns,
        [Description("Cosine-similarity threshold above which a pair gets a similar_to edge. Default 0.85 (clear semantic neighbors but not duplicates).")] float threshold = 0.85f,
        [Description("Per-scan safety cap on new edges. Default 1000.")] int maxNewEdges = 1000)
    {
        if (!_access.CanWrite(ns))
            return new AutoLinkResult(ns, 0, 0, 0, 0, false);
        return _autoLink.Scan(ns, threshold, maxNewEdges, tenantId: _access.TenantId);
    }
}
