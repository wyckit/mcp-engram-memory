using System.ComponentModel;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for exporting the memory graph as a visualization-ready snapshot.
/// </summary>
[McpServerToolType]
public sealed class VisualizationTools
{
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly NamespaceAccess _access;

    public VisualizationTools(CognitiveIndex index, KnowledgeGraph graph, ClusterManager clusters, NamespaceAccess access)
    {
        _index = index;
        _graph = graph;
        _clusters = clusters;
        _access = access;
    }

    [McpServerTool(Name = "get_graph_snapshot", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Export the memory graph as a JSON snapshot for visualization. " +
        "Returns all nodes (cognitive entries), typed edges (knowledge graph relationships), " +
        "and cluster groupings. " +
        "To visualize: save the JSON to a file, then open visualization/memory-graph.html in a browser and load the file. " +
        "Node color encodes lifecycle state (STM=amber, LTM=blue, archived=gray). " +
        "Edge color encodes relation type. Clusters appear as labeled convex hulls.")]
    public GraphSnapshot GetGraphSnapshot(
        [Description("Namespace to snapshot, or '*' for all namespaces (default: '*').")]
        string ns = "*",
        [Description("Include archived entries in the snapshot (default: false).")]
        bool includeArchived = false)
    {
        // ── Nodes ────────────────────────────────────────────────────────────
        // This exports the whole graph, so an unreadable single ns or a namespace this
        // caller can't see inside "*" must vanish entirely - same shape as an empty store,
        // not a distinct denial that would confirm the namespace exists.
        // System namespaces are excluded outright, not merely access-checked. The
        // NamespaceRegistry stores one permission record per namespace in _system_sharing
        // with the namespace name embedded in the record's id ("perm_<ns>"). Exporting them
        // therefore hands back the name of every private namespace in the store - the exact
        // disclosure the namespace filtering below is meant to prevent. They are internal
        // bookkeeping and have no place in a memory-graph visualisation regardless.
        var allEntries = (ns == "*"
                ? _index.GetAllForTenant(tenantId: _access.TenantId)
                : _index.GetAllInNamespace(ns, tenantId: _access.TenantId))
            .Where(e => !e.Ns.StartsWith('_'))
            .Where(e => _access.CanRead(e.Ns));

        var nodes = allEntries
            .Where(e => includeArchived || e.LifecycleState != "archived")
            .Select(e => new GraphSnapshotNode(
                e.Id,
                e.Text,
                e.Ns,
                e.LifecycleState,
                e.ActivationEnergy,
                e.Category,
                e.AccessCount,
                e.IsSummaryNode,
                e.SourceClusterId,
                e.Keywords))
            .ToList();

        var nodeIds = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        // Nodes above are namespace-qualified — each carries its own Ns and was admitted by
        // CanRead(e.Ns) — so they need no further guard. Edges and cluster memberships are not:
        // both are keyed (tenant, id) with no namespace, and nodeIds holds BARE ids. Membership in
        // that set says only "this caller can see AN entry with that id", so a principal who
        // creates twins of two ids another principal privately linked would see that private edge
        // drawn between their own two nodes. Test attribution ACL-blind and tenant-wide, once per
        // distinct id — a whole-store export revisits the same id for every edge it touches.
        // See BareIdTopology for the asymmetry and the one bit this suppression costs.
        var topology = BareIdTopology.ForSweep(_index, tenantId: _access.TenantId);

        // ── Edges ────────────────────────────────────────────────────────────
        // Only include edges where both endpoints are in the visible node set and both are
        // attributable to a single entry.
        var edges = _graph.GetAllEdges(tenantId: _access.TenantId)
            .Where(e => nodeIds.Contains(e.SourceId) && nodeIds.Contains(e.TargetId))
            .Where(e => topology.IsTopologySafe(e.SourceId) && topology.IsTopologySafe(e.TargetId))
            .Select(e => new GraphSnapshotEdge(e.SourceId, e.TargetId, e.Relation, e.Weight))
            .ToList();

        // ── Clusters ─────────────────────────────────────────────────────────
        // Also drives Stats.Namespaces below - filtering here keeps namespace names
        // themselves from leaking through either surface.
        IEnumerable<string> candidateNamespaces = ns == "*" ? _index.GetNamespaces(tenantId: _access.TenantId) : new[] { ns };
        var namespaces = candidateNamespaces.Where(n => !n.StartsWith('_')).Where(_access.CanRead).ToList();

        var clusters = new List<GraphSnapshotCluster>();
        foreach (var nsName in namespaces)
        {
            foreach (var info in _clusters.ListClusters(nsName, tenantId: _access.TenantId))
            {
                var detail = _clusters.GetCluster(info.ClusterId, tenantId: _access.TenantId);
                if (detail is null) continue;

                // Same rule as the edges: a membership row names a bare id, so an ambiguous one
                // would draw a hull around this caller's node on the strength of an invisible
                // twin's membership.
                var memberIds = detail.Members
                    .Select(m => m.Id)
                    .Where(id => nodeIds.Contains(id) && topology.IsTopologySafe(id))
                    .ToList();

                if (memberIds.Count == 0) continue;

                clusters.Add(new GraphSnapshotCluster(
                    info.ClusterId,
                    info.Label,
                    nsName,
                    memberIds,
                    info.HasSummary));
            }
        }

        // ── Stats ─────────────────────────────────────────────────────────────
        var stm = nodes.Count(n => n.LifecycleState == "stm");
        var ltm = nodes.Count(n => n.LifecycleState == "ltm");
        var archived = nodes.Count(n => n.LifecycleState == "archived");

        var stats = new GraphSnapshotStats(
            nodes.Count,
            edges.Count,
            clusters.Count,
            stm, ltm, archived,
            namespaces.ToList());

        return new GraphSnapshot(
            ns,
            DateTimeOffset.UtcNow,
            nodes,
            edges,
            clusters,
            stats);
    }
}
