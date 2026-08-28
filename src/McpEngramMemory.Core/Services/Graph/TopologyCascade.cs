using McpEngramMemory.Core.Services.Intelligence;

namespace McpEngramMemory.Core.Services.Graph;

/// <summary>
/// Result of one cascade sweep. <see cref="IdsSkippedAmbiguous"/> is the count of ids the sweep
/// refused to touch because the tenant holds that id in more than one namespace; it is reported
/// rather than logged so a caller can tell "nothing to remove" apart from "declined to guess".
/// </summary>
public readonly record struct CascadeOutcome(int EdgesRemoved, int IdsSkippedAmbiguous);

/// <summary>
/// Cascades entry deletion into graph edges and cluster memberships.
///
/// This exists because <see cref="CognitiveIndex.DeleteAllInNamespace(string, string)"/> is
/// deliberately non-cascading, so every caller open-codes the sweep — and each one re-decides
/// whether to guard it. Hoisting the sweep here makes the guarded behaviour the only reachable one.
///
/// The guard is the invariant that an entry's identity is (tenant, namespace, id). Graph adjacency
/// and cluster membership are keyed by (tenant, bare id), so an id that occurs in two of the
/// tenant's namespaces reaches BOTH entries' topology. Deleting one entry must not strip the other
/// one's edges, and nothing at this level can tell the two apart — so an ambiguous id is skipped.
/// </summary>
public static class TopologyCascade
{
    /// <summary>
    /// Remove (or, with <paramref name="apply"/> false, merely count) the graph edges and cluster
    /// memberships belonging to <paramref name="ids"/> within one tenant.
    ///
    /// Both branches run the same resolution and the same guard, so a dry run can no longer report
    /// a different figure from the purge it is previewing.
    /// </summary>
    public static CascadeOutcome CascadeAll(
        CognitiveIndex index, KnowledgeGraph graph, ClusterManager clusters,
        IEnumerable<string> ids, string tenantId, bool apply)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(clusters);
        ArgumentNullException.ThrowIfNull(ids);

        // One namespace listing for the whole sweep. Listing a tenant's namespaces loads every
        // persisted namespace, so re-listing per id would cost a full store reload per entry on an
        // operation that routinely spans hundreds of namespaces.
        var namespaces = index.GetNamespaces(tenantId: tenantId);

        int edgesRemoved = 0;
        int skippedAmbiguous = 0;

        // The dry run counts DISTINCT edges. Summing per-entry edge lists double-counts every edge
        // whose two endpoints are both in the swept set — which is most of them for an internally
        // linked namespace — and would report roughly twice what the real purge removes. The apply
        // branch needs no such set: removing an edge unlinks both directions, so the second
        // endpoint's removal no longer sees it.
        HashSet<(string Source, string Target, string Relation)>? distinctEdges =
            apply ? null : new HashSet<(string, string, string)>();

        foreach (var id in ids)
        {
            int namespacesHoldingId = index.CountNamespacesContaining(id, tenantId: tenantId, namespaces);

            // Nothing of this tenant's answers to this id, so there is no topology we can attribute
            // to it. Fail closed rather than removing edges we cannot tie to an entry.
            if (namespacesHoldingId == 0)
                continue;

            if (namespacesHoldingId > 1)
            {
                skippedAmbiguous++;
                continue;
            }

            if (apply)
            {
                edgesRemoved += graph.RemoveAllEdgesForEntry(id, tenantId: tenantId);
                clusters.RemoveEntryFromAllClusters(id, tenantId: tenantId);
            }
            else
            {
                foreach (var edge in graph.GetEdgesForEntry(id, tenantId: tenantId))
                    distinctEdges!.Add((edge.SourceId, edge.TargetId, edge.Relation));
            }
        }

        return new CascadeOutcome(apply ? edgesRemoved : distinctEdges!.Count, skippedAmbiguous);
    }
}
