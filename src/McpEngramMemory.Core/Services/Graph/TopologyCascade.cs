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
    ///
    /// ONE SWEEP PER ID, SHARED BY THAT ID'S TWO PRIMITIVES — never one sweep for the whole purge,
    /// and the two halves of that sentence are load-bearing in opposite directions.
    ///
    /// Shared between the primitives, because building two identical sweeps for one id cost two full
    /// namespace listings — a LINQ pass over every (tenant, ns) partition in the process, plus a
    /// list, plus two attribution-fence acquire/release cycles — on a path whose stated design goal
    /// is that the dry run and the purge cost the same. Both mutators expose a guard overload for
    /// exactly this, and both assert that the sweep's tenant matches theirs.
    ///
    /// NOT shared across ids, even though the same overloads would allow it and their older
    /// documentation invited it. A <see cref="TopologyGuard.Sweep"/> carries ONE attribution
    /// revision, captured when it was built, and every mutator holding it fails closed the instant
    /// that value goes stale. A batch-wide sweep therefore converts a single unrelated crossing
    /// anywhere in the tenant — an ordinary <c>remember</c> of an id that already exists in another
    /// namespace — into a silent no-op for every REMAINING id of the purge, while
    /// <c>DeleteAllInNamespace</c> still removes those entries: edges and memberships left dangling
    /// against entries that no longer exist, <see cref="CascadeOutcome.EdgesRemoved"/> undercounting,
    /// <see cref="CascadeOutcome.IdsSkippedAmbiguous"/> not moving, and no error raised. Per id, one
    /// crossing costs at most the id it raced.
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
                // One sweep for this id, handed to both primitives — see the remarks above for why
                // the unit is the id and not the batch.
                var guard = TopologyGuard.ForSweep(index, tenantId);
                edgesRemoved += graph.RemoveAllEdgesForEntry(id, tenantId: tenantId, guard);
                clusters.RemoveEntryFromAllClusters(id, tenantId: tenantId, guard);
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
