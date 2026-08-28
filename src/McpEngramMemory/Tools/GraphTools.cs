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

    /// <summary>Reply for every refusal on this path. One string - and it is Core's string, so the
    /// three refusal reasons (no such entry, not writable, id shared with an invisible twin) cannot
    /// drift apart into an existence oracle even across the layer boundary.</summary>
    private static string NotFound(string id) => TopologyGuard.Unattributable(id);

    /// <summary>
    /// Access check for an edge endpoint reached by id. Two conditions, and they authorize
    /// different objects, so both are required.
    ///
    /// Resolution authorizes the ENTRY: graph edges carry no namespace of their own, so the only
    /// way to know whether a caller may touch one is to resolve the entry (within the caller's
    /// tenant) and check its namespace. Core cannot do this one - it has no principal.
    ///
    /// The ambiguity test authorizes the NODE, which is not the same object. Adjacency is keyed
    /// (tenant, id) with no namespace, so a caller who resolves to a twin they may write is still
    /// about to mutate the node their twin shares with every other same-id entry in the tenant -
    /// including ones in namespaces they cannot see. Resolution is ACL-filtered and cannot see
    /// that twin, which is exactly why the second test has to be ACL-blind.
    ///
    /// <see cref="KnowledgeGraph"/> now enforces that second test itself, so it is kept here only
    /// to SHAPE THE REPLY: without it the tool would hand back Core's refusal, and a caller
    /// deserves the same not-found answer for all three reasons rather than a message that
    /// happens to match today. Both tests refuse with that one reply - a distinct denial would
    /// confirm the id exists in a namespace the caller cannot see.
    /// </summary>
    private string? DenyIfCannotWrite(string id, TopologyGuard.Sweep topology)
    {
        if (!topology.IsTopologySafe(id))
            return NotFound(id);

        return _access.ResolveWritableEntry(_index, id) is null ? NotFound(id) : null;
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
            var edge = new GraphEdge(sourceId, targetId, relation, weight, metadata, tenantId: _access.TenantId);
            // One sweep for both endpoints: the per-id overload re-lists the tenant's namespaces
            // and that listing reloads the store.
            var topology = BareIdTopology.ForSweep(_index, tenantId: _access.TenantId);
            return DenyIfCannotWrite(sourceId, topology)
                ?? DenyIfCannotWrite(targetId, topology)
                ?? _graph.AddEdge(edge);
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
        var topology = BareIdTopology.ForSweep(_index, tenantId: _access.TenantId);
        return DenyIfCannotWrite(sourceId, topology) ?? DenyIfCannotWrite(targetId, topology)
            ?? _graph.RemoveEdges(sourceId, targetId, relation, tenantId: _access.TenantId);
    }

    [McpServerTool(Name = "get_neighbors", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Get entries directly connected to a node in the knowledge graph. Use to explore relationships around a specific memory.")]
    public GetNeighborsResult GetNeighbors(
        [Description("Entry ID to find neighbors for.")] string id,
        [Description("Filter by relation type.")] string? relation = null,
        [Description("Direction: 'outgoing', 'incoming', or 'both' (default).")] string direction = "both")
    {
        // One sweep for the seed and every neighbor it returns.
        var topology = BareIdTopology.ForSweep(_index, tenantId: _access.TenantId);

        // The seed names a node, not an entry. When the tenant holds this id in two namespaces the
        // node is shared with a twin the caller may not see, so its adjacency is not attributable
        // to the entry the caller means - answer as a node with no neighbors, which is what a
        // caller with no readable neighbors already sees.
        if (!topology.IsTopologySafe(id))
            return new GetNeighborsResult(id, Array.Empty<NeighborResult>());

        var result = _graph.GetNeighbors(id, relation, direction, tenantId: _access.TenantId);

        // Edges are tenant-scoped but span namespaces, so a neighbor can live in a namespace this
        // caller may not read. Filter, don't deny: the id the caller passed in is already known to
        // them, so only the resolved neighbors need to be hidden.
        //
        // The second test is the one a safe seed does not buy. A neighbor id the tenant holds
        // twice resolves through the legacy id locator to whichever twin was written last - so a
        // caller who creates a twin of an id that appears in someone else's edge gets that edge
        // handed back attached to their own readable entry, relation, weight and metadata
        // included. Filtering the neighbor rather than denying the seed keeps a caller's own
        // adjacency intact while withholding the edge that is not attributable.
        var visible = result.Neighbors
            .Where(n => _access.CanRead(n.Entry.Namespace))
            .Where(n => topology.IsTopologySafe(n.Entry.Id))
            .ToList();
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
        // No topology guard here, and that is the fix rather than an omission. This tool used to
        // guard the seed and then strip unsafe edges from the finished result, which is too late:
        // by then the BFS had already crossed the shared node and discovered its descendants, so
        // the twin and everything downstream of it stayed in Entries with only the edges removed.
        // The predicate now runs INSIDE KnowledgeGraph.Traverse, before a target is resolved or
        // enqueued, so a walk that reaches an unattributable node simply stops there and an
        // ambiguous root returns this same empty result. Re-testing here would be a second copy of
        // a predicate that could only ever agree or be wrong.
        var result = _graph.Traverse(startId, tenantId: _access.TenantId, maxDepth: maxDepth, relation: relation, minWeight: minWeight, maxResults: maxResults);

        // What remains is the ACL filter, which Core cannot do because it has no principal. The
        // start entry itself is included in Entries, so it must be filtered too - otherwise a
        // caller could learn the text of an unreadable entry just by naming it as the traversal
        // root. Drop any edge whose endpoint fell out of the visible set.
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
