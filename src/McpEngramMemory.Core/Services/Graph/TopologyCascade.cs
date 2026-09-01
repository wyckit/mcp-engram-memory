using McpEngramMemory.Core.Services.Intelligence;

namespace McpEngramMemory.Core.Services.Graph;

/// <summary>
/// Result of one cascade sweep. <see cref="IdsSkippedAmbiguous"/> is the count of ids the sweep
/// refused to touch because the tenant holds that id in more than one namespace; it is reported
/// rather than logged so a caller can tell "nothing to remove" apart from "declined to guess".
///
/// <see cref="UnsettledIds"/> carries a DIFFERENT verdict and callers must treat it differently.
/// A skipped-ambiguous id was deliberately left alone — its topology belongs to the surviving
/// twin, so deleting the entry afterwards is safe by design. An unsettled id's cleanup was
/// ATTEMPTED and cannot be proven complete: the tenant's attribution revision kept moving across
/// the primitives, so one may have succeeded while the other silently refused. Deleting the entry
/// on that verdict strands whatever the refused primitive left behind, attributed to an entry
/// that no longer exists — the caller must NOT delete unsettled ids' entries, and should retry.
/// The ids themselves are named, not merely counted, so a multi-id caller can finish the job for
/// every other id instead of deferring work whose topology the cascade already tore down.
/// </summary>
public readonly record struct CascadeOutcome(int EdgesRemoved, int IdsSkippedAmbiguous, IReadOnlyList<string>? UnsettledIds)
{
    private readonly IReadOnlyList<string>? _unsettledIds = UnsettledIds;

    /// <summary>
    /// The unsettled ids — never null. A <c>default(CascadeOutcome)</c> is reachable through any
    /// defaulted struct field or array slot and bypasses the constructor, so the backing field is
    /// coalesced on every read: a defaulted outcome reads as "nothing unsettled" rather than
    /// throwing on enumeration.
    /// </summary>
    public IReadOnlyList<string> UnsettledIds => _unsettledIds ?? Array.Empty<string>();

    /// <summary>Count convenience over <see cref="UnsettledIds"/>.</summary>
    public int IdsUnsettled => UnsettledIds.Count;
}

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
    // How many revision-bracketed attempts one id's apply pair gets before the sweep stops
    // chasing a churning tenant and reports the id as skipped. Two, matching the optimistic
    // read paths' AttributionReadAttempts: one honest retry distinguishes a transient crossing
    // from sustained churn, and more would let churn stall a purge.
    private const int CascadeApplyAttempts = 2;

    /// <summary>
    /// Remove (or, with <paramref name="apply"/> false, merely count) the graph edges and cluster
    /// memberships belonging to <paramref name="ids"/> within one tenant.
    ///
    /// Both branches run the same tenant-wide ambiguity admission rule, so a dry run can no longer
    /// report a different figure from the purge it is previewing.
    ///
    /// ONE FRESH SWEEP PER PRIMITIVE — never one sweep for the whole purge and never one sweep
    /// reused after a primitive has released its attribution fence.
    ///
    /// The graph primitive releases its fence before scheduling persistence. An unrelated crossing
    /// can land before the cluster primitive starts, so handing the latter the graph's sweep turns a
    /// safe cluster eviction into a silent stale-revision refusal. Building the second sweep after
    /// graph removal is the optimistic transaction boundary: each primitive acts on a view captured
    /// immediately before its own fenced publish. Store loading is cached, so the second sweep does
    /// not repeat provider enumeration on the warm path.
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
    /// <param name="index">Index the ambiguity rule and revision brackets resolve against.</param>
    /// <param name="graph">Graph whose edges are swept.</param>
    /// <param name="clusters">Cluster manager whose memberships are swept.</param>
    /// <param name="ids">Entry ids to sweep, all from one tenant.</param>
    /// <param name="tenantId">The tenant partition; pass "" for the legacy partition.</param>
    /// <param name="apply">False counts what a purge would remove; true removes it.</param>
    /// <param name="watchNs">
    /// The namespace the swept entries were STAGED from, when the caller judged them against a
    /// snapshot (staleness, version filters) before calling. The apply bracket then also watches
    /// that partition's OCCUPANCY revision: an entry written or removed there mid-sweep — a
    /// same-slot replacement in particular, which crosses no ambiguity boundary and so moves no
    /// attribution revision — invalidates the staged judgment itself, and the raced id is
    /// reported unsettled WITHOUT retry. A retry against attribution churn re-derives the same
    /// decision; a retry against occupancy churn would apply a stale decision to occupations
    /// nobody examined, so the id goes back to the caller to be re-staged.
    /// </param>
    /// <param name="dryRunEdgeDedup">
    /// A PASS-scoped dedup set for previews spanning several CascadeAll calls. A dry run counts
    /// distinct edges, but distinctness is only as wide as the set that judges it: an edge
    /// between entries in two namespaces previewed by two calls is seen by both and — with each
    /// call holding its own set — counted twice, while the real purge removes it once. A caller
    /// previewing one pass over many namespaces passes one set for the whole pass. Ignored when
    /// <paramref name="apply"/> is true.
    /// </param>
    /// <param name="watchOccupancyBaseline">
    /// The watched partition's occupancy revision AS OF THE CALLER'S STAGING — captured before
    /// the caller listed the entries it judged. The bracket and the fenced primitives compare
    /// against this baseline rather than values captured when the sweep happens to start, so a
    /// replacement landing anywhere between the staging and the mutation is seen. Ignored when
    /// <paramref name="watchNs"/> is null; when watching without a baseline, staging is taken
    /// to be this call itself.
    /// </param>
    public static CascadeOutcome CascadeAll(
        CognitiveIndex index, KnowledgeGraph graph, ClusterManager clusters,
        IEnumerable<string> ids, string tenantId, bool apply, string? watchNs = null,
        HashSet<(string Source, string Target, string Relation)>? dryRunEdgeDedup = null,
        long? watchOccupancyBaseline = null)
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
        List<string>? unsettledIds = null;

        // The dry run counts DISTINCT edges. Summing per-entry edge lists double-counts every edge
        // whose two endpoints are both in the swept set — which is most of them for an internally
        // linked namespace — and would report roughly twice what the real purge removes. The apply
        // branch needs no such set: removing an edge unlinks both directions, so the second
        // endpoint's removal no longer sees it. A pass-scoped set from the caller widens the same
        // dedup across calls (see dryRunEdgeDedup); this call reports only the edges IT newly
        // discovered, so a pass's totals sum to what one purge removes.
        HashSet<(string Source, string Target, string Relation)>? distinctEdges =
            apply ? null : dryRunEdgeDedup ?? new HashSet<(string, string, string)>();
        int dryRunNewEdges = 0;

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
                // Each primitive gets a sweep captured immediately before its own fenced publish.
                // The graph releases its attribution fence before it schedules persistence and
                // returns; an unrelated 1<->2 crossing can land in that gap. Reusing the graph's
                // sweep for cluster eviction would then make the second primitive fail closed on a
                // stale tenant-wide revision, leaving a dangling membership while the caller goes
                // on to delete the entry. A fresh sweep preserves the one-id cost bound and judges
                // the cluster phase against the state that actually precedes it.
                // The primitives fail closed SILENTLY when ANY id in the tenant crosses the
                // ambiguity boundary between their sweep capture and their fenced compare:
                // RemoveAllEdgesForEntry returns 0 and the cluster primitive is void, so
                // "refused" is indistinguishable from "nothing to remove" out here — and the
                // refusing crossing need not involve this id at all. So each attempt runs under
                // a revision BRACKET: if the tenant's attribution revision is unchanged across
                // both primitives, no refusal was possible and their outcome is exact; if it
                // moved, a refusal is possible and the pair is retried against fresh sweeps
                // (both primitives are idempotent, so a re-run after success removes nothing
                // twice). A bracket that never stabilizes — or an id that ends the sweep
                // ambiguous, which stable brackets cannot excuse — is reported through the
                // outcome rather than letting it claim a clean zero. The two conditions carry
                // DIFFERENT verdicts (see CascadeOutcome): an ambiguous id was deliberately
                // left alone and its entry may still be deleted; an unsettled id's cleanup is
                // unproven and its entry must not be.
                // The occupancy verdict is judged against the CALLER'S STAGED baseline for the
                // whole bracket: a replacement anywhere after the staging — not merely after
                // this attempt started — invalidates the judgment being applied.
                long? occupancyBaseline = watchNs is null
                    ? null
                    : watchOccupancyBaseline ?? index.OccupancyRevisionFor(watchNs, tenantId);

                bool settled = false;
                bool everUnstable = false;
                for (int attempt = 0; attempt < CascadeApplyAttempts && !settled; attempt++)
                {
                    long revision = index.AttributionRevisionFor(tenantId);

                    // Watched sweeps make the primitives PIN the watched partition at the same
                    // staged baseline for the whole mutation — a replacement either refuses the
                    // primitive before anything is removed or waits until it is done. The
                    // bracket's own compare below is the second line for a replacement landing
                    // BETWEEN the primitives (after the graph pin releases, before the cluster
                    // pin is taken — the cluster primitive refuses it too, and the bracket is
                    // what reports the id unsettled).
                    var graphGuard = watchNs is null
                        ? TopologyGuard.ForSweep(index, tenantId)
                        : TopologyGuard.ForSweep(index, tenantId, watchNs, occupancyBaseline!.Value);
                    edgesRemoved += graph.RemoveAllEdgesForEntry(id, tenantId: tenantId, graphGuard);

                    var clusterGuard = watchNs is null
                        ? TopologyGuard.ForSweep(index, tenantId)
                        : TopologyGuard.ForSweep(index, tenantId, watchNs, occupancyBaseline!.Value);
                    clusters.RemoveEntryFromAllClusters(id, tenantId: tenantId, clusterGuard);

                    bool occupancyMoved = occupancyBaseline is long baseline
                        && index.OccupancyRevisionFor(watchNs!, tenantId) != baseline;
                    settled = !occupancyMoved && index.AttributionRevisionFor(tenantId) == revision;
                    if (!settled) everUnstable = true;

                    // Occupancy movement ends the id's sweep UNSETTLED with no retry — see the
                    // watchNs remarks: attribution churn invalidates an attempt, occupancy churn
                    // invalidates the STAGING, and re-running the primitives against a partition
                    // whose entries changed would sweep occupations this pass never judged.
                    if (occupancyMoved) break;
                }

                if (!settled)
                {
                    (unsettledIds ??= new List<string>()).Add(id);
                }
                // The single-id overload probes the candidate index; it does not re-list.
                else if (index.CountNamespacesContaining(id, tenantId: tenantId) > 1)
                {
                    // A clean skip requires that NOTHING was mutated for this id, and that is
                    // provable only when every attempt ran under a stable bracket — there an
                    // ambiguous id's guards refused everything deterministically. An earlier
                    // UNSTABLE attempt may have torn down part of the shared node's topology
                    // before the ambiguity landed, and a later stable-but-refused retry must
                    // not launder that into "deliberately left alone": the id stays unsettled,
                    // so its entry is not deleted on a half-dismantled node.
                    if (everUnstable)
                        (unsettledIds ??= new List<string>()).Add(id);
                    else
                        skippedAmbiguous++;
                }
            }
            else
            {
                foreach (var edge in graph.GetEdgesForEntry(id, tenantId: tenantId))
                {
                    if (distinctEdges!.Add((edge.SourceId, edge.TargetId, edge.Relation)))
                        dryRunNewEdges++;
                }
            }
        }

        return new CascadeOutcome(apply ? edgesRemoved : dryRunNewEdges, skippedAmbiguous, unsettledIds);
    }
}
