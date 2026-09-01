using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// WHAT THE TOPOLOGY MUTATORS COUNT, WHAT THEY REFUSE, AND WHAT A SHARED SWEEP MEANS.
///
/// Four defects with one thing in common: every existing assertion on these paths checks a PREFIX
/// (<c>Assert.Contains("Removed", reply)</c>) or an outcome that happens to be right for the wrong
/// reason, so the numbers and the self-referential shapes had never been pinned in either direction.
///
///  - <c>RemoveEdges</c> summed BOTH adjacency halves of one edge into a caller-visible count, so
///    <c>unlink_memories</c> answered "Removed 2 edge(s)" where <c>delete_memory</c>, through
///    <c>RemoveAllEdgesForEntry</c>, answered "Removed 1" for the identical structural change.
///  - <c>TransferEdges</c> counted an edge that was ALREADY self-referential as transferred, then
///    destroyed the edge it had just built, later in the same critical section.
///  - <c>TransferEdges</c> had no <c>fromId == toId</c> guard, so a self-transfer rewrote a node's
///    adjacency into itself and then dropped the list it had just written into — losing one half of
///    every incident edge and persisting the loss.
///  - The <c>guard</c> overloads accepted a <c>Sweep</c> built for ANOTHER tenant, which judges this
///    tenant's ids against the wrong namespace listing, finds zero, and admits everything.
/// </summary>
public sealed class TopologyAccountingTests : IDisposable
{
    private const string Tenant = "acme";
    private const string OtherTenant = "globex";
    private const string MainNs = "main";
    private const string ShadowNs = "shadow";

    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;

    public TopologyAccountingTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"topology_accounting_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 600_000);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    private void Seed(string id, string ns, string tenantId = Tenant)
        => _index.Upsert(new CognitiveEntry(id, [0.5f, 0.5f], ns, $"entry '{id}' in {ns}", tenantId: tenantId));

    private static GraphEdge Edge(string src, string dst, string relation = "supports", string tenantId = Tenant)
        => new(src, dst, relation, 0.9f, null, tenantId);

    private void Link(string src, string dst, string relation = "supports")
        => Assert.True(_graph.TryAddEdge(Edge(src, dst, relation), out _),
            $"fixture edge '{src}' -> '{dst}' ({relation}) was refused");

    private void AssertHalvesAgree()
        => Assert.True(_graph.FindAdjacencyMirrorViolations().Count == 0,
            string.Join(Environment.NewLine, _graph.FindAdjacencyMirrorViolations()));

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 1. RemoveEdges COUNTS EDGES, NOT ADJACENCY ENTRIES
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One edge removed reports ONE. It reported two, because <c>AddEdgeInternal</c> publishes every
    /// edge into both adjacency dictionaries and the removal summed both halves. The figure goes
    /// straight to the MCP client through <c>unlink_memories</c>.
    /// </summary>
    [Fact]
    public void RemoveEdges_RemovingOneEdge_ReportsOne()
    {
        Seed("a", MainNs);
        Seed("b", MainNs);
        Link("a", "b");

        Assert.Equal("Removed 1 edge(s) between 'a' and 'b'.",
            _graph.RemoveEdges("a", "b", "supports", Tenant));

        Assert.Empty(_graph.GetStoredEdges(Tenant));
        AssertHalvesAgree();
    }

    /// <summary>
    /// Three relations between one pair, removed by <c>relation: null</c>, report THREE — not six.
    /// The multiplier was exactly two, so a single-edge assertion alone could be satisfied by an
    /// off-by-one fix; this pins the shape rather than one value.
    /// </summary>
    [Fact]
    public void RemoveEdges_RemovingEveryRelationBetweenAPair_ReportsTheEdgeCount()
    {
        Seed("a", MainNs);
        Seed("b", MainNs);
        Link("a", "b", "supports");
        Link("a", "b", "elaborates");
        Link("a", "b", "depends_on");
        Assert.Equal(3, _graph.GetStoredEdges(Tenant).Count);

        Assert.Equal("Removed 3 edge(s) between 'a' and 'b'.",
            _graph.RemoveEdges("a", "b", null, Tenant));

        Assert.Empty(_graph.GetStoredEdges(Tenant));
        AssertHalvesAgree();
    }

    /// <summary>
    /// THE TWO REPLIES FOR THE SAME STRUCTURAL CHANGE MUST AGREE. This is the assertion that makes
    /// the count objective rather than a matter of taste: the same graph, the same one edge, removed
    /// two different ways, has to produce the same number.
    /// </summary>
    [Fact]
    public void RemoveEdges_AndRemoveAllEdgesForEntry_ReportTheSameFigureForTheSameEdge()
    {
        Seed("a", MainNs);
        Seed("b", MainNs);
        Link("a", "b");
        string viaUnlink = _graph.RemoveEdges("a", "b", "supports", Tenant);

        Link("a", "b");
        int viaCascade = _graph.RemoveAllEdgesForEntry("a", Tenant);

        Assert.Equal($"Removed {viaCascade} edge(s) between 'a' and 'b'.", viaUnlink);
        Assert.Equal(1, viaCascade);
    }

    /// <summary>
    /// The control: a removal that matches nothing still says so, and does not schedule a save. The
    /// fix separates the reported count from the structural flag, and both halves have to stay
    /// honest.
    /// </summary>
    [Fact]
    public void RemoveEdges_WithNoMatchingEdge_ReportsTheMiss()
    {
        Seed("a", MainNs);
        Seed("b", MainNs);

        Assert.Equal("No edges found between 'a' and 'b'.",
            _graph.RemoveEdges("a", "b", "supports", Tenant));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 2. TransferEdges AND SELF-REFERENCE
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// An edge that is ALREADY self-referential — <c>from -&gt; from</c> — is refused, not counted.
    ///
    /// It is the only shape that traverses BOTH branches of the transfer. The outgoing branch dropped
    /// its mirror, fell through the self-edge skip (which only tested <c>TargetId == toId</c>), built
    /// <c>to -&gt; from</c>, counted a transfer, and republished it into <c>_incoming[(t, from)]</c>;
    /// the incoming branch then read that same list, deleted the edge again, and continued. Both
    /// adjacency halves end consistent and the semantics are right, so no invariant seam fires — the
    /// only residue is a count claiming an edge landed on <c>toId</c> when none did, reported
    /// verbatim in the merge reply.
    ///
    /// Self-loops are creatable: <c>GraphEdge</c> rejects only empty ids, and <c>link_memories</c>
    /// never compares source to target.
    /// </summary>
    [Fact]
    public void TransferEdges_WithAnAlreadySelfReferentialEdge_TransfersNothingAndCountsNothing()
    {
        Seed("from", MainNs);
        Seed("to", MainNs);
        Link("from", "from", "similar_to");
        Assert.Single(_graph.GetStoredEdges(Tenant));

        Assert.Equal(0, _graph.TransferEdges("from", "to", Tenant));

        Assert.Empty(_graph.GetStoredEdgesForEntry("to", Tenant));
        Assert.Empty(_graph.GetStoredEdgesForEntry("from", Tenant));
        Assert.Empty(_graph.GetStoredEdges(Tenant));
        AssertHalvesAgree();
    }

    /// <summary>
    /// The mixed case, which is where the miscount is visible without also being a whole-graph
    /// no-op: one self-loop plus one ordinary edge must report ONE transfer, not two.
    /// </summary>
    [Fact]
    public void TransferEdges_WithASelfLoopAndAnOrdinaryEdge_CountsOnlyTheOrdinaryOne()
    {
        Seed("from", MainNs);
        Seed("to", MainNs);
        Seed("far", MainNs);
        Link("from", "from", "similar_to");
        Link("from", "far", "elaborates");

        Assert.Equal(1, _graph.TransferEdges("from", "to", Tenant));

        var onTo = _graph.GetStoredEdgesForEntry("to", Tenant);
        Assert.Single(onTo);
        Assert.Equal("far", onTo[0].TargetId);
        Assert.Empty(_graph.GetStoredEdgesForEntry("from", Tenant));
        AssertHalvesAgree();
    }

    /// <summary>
    /// A SELF-TRANSFER MOVES NOTHING AND MUST TOUCH NOTHING.
    ///
    /// With <c>fromId == toId</c> every rewrite produces the edge it already had and publishes it
    /// back into the very list the branch then drops wholesale, so the outgoing half of every
    /// incident edge disappears while the incoming half survives with no counterpart — and
    /// symmetrically. <c>mutated</c> is true, so a save IS scheduled and the loss is what gets
    /// persisted; <c>SnapshotEdgesForSave</c> walks <c>_outgoing</c> only. The reply meanwhile claims
    /// the edges were transferred.
    ///
    /// Reachable through <c>merge_memories(keepId: x, archiveId: x)</c>: both ids resolve to the one
    /// entry, so nothing upstream notices.
    /// </summary>
    [Fact]
    public void TransferEdges_OntoItself_ChangesNothing()
    {
        Seed("x", MainNs);
        Seed("a", MainNs);
        Seed("c", MainNs);
        Link("x", "a");
        Link("c", "x");

        long revisionBefore = _graph.RevisionFor(Tenant);

        Assert.Equal(0, _graph.TransferEdges("x", "x", Tenant));

        // Every edge is exactly where it was, in BOTH halves.
        Assert.Equal(2, _graph.GetStoredEdges(Tenant).Count);
        Assert.Single(_graph.GetStoredEdgesForEntry("a", Tenant));
        Assert.Single(_graph.GetStoredEdgesForEntry("c", Tenant));
        Assert.Equal(2, _graph.GetStoredEdgesForEntry("x", Tenant).Count);
        Assert.Contains(_graph.GetNeighbors("x", null, "outgoing", Tenant).Neighbors,
            n => n.Edge.TargetId == "a");
        Assert.Contains(_graph.GetNeighbors("x", null, "incoming", Tenant).Neighbors,
            n => n.Edge.SourceId == "c");
        AssertHalvesAgree();

        // Nothing changed, so nothing may claim a change.
        Assert.Equal(revisionBefore, _graph.RevisionFor(Tenant));
    }

    /// <summary>
    /// The same self-argument shape in the cluster half, swept for rather than waited for.
    ///
    /// <c>TransferMembership</c> screens its two arguments SEPARATELY, so both pass when they are
    /// the same id. Running it removes the member and immediately re-adds it: the membership SET is
    /// unchanged, but every cluster that held it is republished with the member moved to the end of
    /// the list, a persist is scheduled, the centroids are recomputed, and the returned count claims
    /// those clusters were re-homed — the figure <c>merge_memories</c> reports.
    /// </summary>
    [Fact]
    public void TransferMembership_OntoItself_ChangesNothing()
    {
        Seed("m1", MainNs);
        Seed("m2", MainNs);
        Assert.Contains("Created", _clusters.CreateCluster("k", MainNs, new[] { "m1", "m2" }, "l", Tenant));

        // Member ORDER, not just membership: the self-transfer removes and re-appends, so a list
        // that still holds the same ids in a different sequence is the residue it leaves behind.
        var membersBefore = _clusters.GetCluster("k", Tenant)!.Members.Select(m => m.Id).ToList();
        Assert.Equal(new[] { "m1", "m2" }, membersBefore);

        Assert.Equal(0, _clusters.TransferMembership("m1", "m1", Tenant));

        var membersAfter = _clusters.GetCluster("k", Tenant)!.Members.Select(m => m.Id).ToList();
        Assert.Equal(membersBefore, membersAfter);
        Assert.Contains("k", _clusters.GetClustersForEntry("m1", Tenant));
        Assert.Contains("k", _clusters.GetClustersForEntry("m2", Tenant));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 3. A SWEEP BELONGS TO ONE TENANT AND TO ONE UNIT OF WORK
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A sweep built for another tenant is REFUSED, not quietly trusted.
    ///
    /// It fails OPEN, which is why this has to throw rather than degrade: the sweep judges ids
    /// against ITS tenant's namespace listing, so asked about this tenant's id it counts zero
    /// namespaces — and zero is treated as attributable, deliberately, because a dangling node is
    /// unambiguous. Every id would therefore be admitted without ever having been judged, on the one
    /// path whose entire purpose is to judge it.
    /// </summary>
    [Fact]
    public void TheGuardOverloads_RefuseASweepBuiltForAnotherTenant()
    {
        Seed("a", MainNs);
        Seed("b", MainNs);
        Seed("x", MainNs, OtherTenant);
        Link("a", "b");

        var foreignSweep = TopologyGuard.ForSweep(_index, OtherTenant);

        Assert.Throws<ArgumentException>(
            () => _graph.RemoveAllEdgesForEntry("a", Tenant, foreignSweep));
        Assert.Throws<ArgumentException>(
            () => _clusters.RemoveEntryFromAllClusters("a", Tenant, foreignSweep));

        // The refusal is total: nothing was removed on the way to throwing.
        Assert.Single(_graph.GetStoredEdges(Tenant));
    }

    /// <summary>
    /// A sweep shared across MANY ids fails closed for all of them once one unrelated crossing lands.
    ///
    /// This is the behaviour that made the guard overloads' old "call this from a sweep over many
    /// entries" guidance a correctness hazard, and it is pinned here rather than left as prose: a
    /// <c>Sweep</c> carries ONE attribution revision, so a crossing anywhere in the tenant voids
    /// every remaining call made with it. <c>TopologyCascade</c> therefore scopes a sweep to one id
    /// and shares it only between that id's two primitives.
    /// </summary>
    [Fact]
    public void ASweepSharedAcrossIds_FailsClosedForAllOfThemAfterOneUnrelatedCrossing()
    {
        Seed("a", MainNs);
        Seed("b", MainNs);
        Seed("c", MainNs);
        Seed("unrelated", MainNs);
        Link("a", "b");
        Link("b", "c");

        var shared = TopologyGuard.ForSweep(_index, Tenant);

        // One crossing, naming none of the ids the sweep is about to be used for.
        Seed("unrelated", ShadowNs);

        Assert.Equal(0, _graph.RemoveAllEdgesForEntry("a", Tenant, shared));
        Assert.Equal(0, _graph.RemoveAllEdgesForEntry("b", Tenant, shared));
        Assert.Equal(2, _graph.GetStoredEdges(Tenant).Count);

        // The control: a sweep built AFTER the crossing removes normally, so the refusals above are
        // the freshness rule and not a broken fixture.
        Assert.Equal(1, _graph.RemoveAllEdgesForEntry("a", Tenant, TopologyGuard.ForSweep(_index, Tenant)));
    }

    /// <summary>
    /// The cascade shares one sweep between an id's two primitives and still removes both halves.
    /// The sharing is a cost fix; it must not become a behaviour change.
    /// </summary>
    [Fact]
    public void CascadeAll_RemovesEdgesAndMembershipsForEachSweptId()
    {
        Seed("a", MainNs);
        Seed("b", MainNs);
        Seed("c", MainNs);
        Link("a", "b");
        Link("b", "c");
        Assert.Contains("Created", _clusters.CreateCluster("k", MainNs, new[] { "a", "b", "c" }, "l", Tenant));

        var outcome = TopologyCascade.CascadeAll(
            _index, _graph, _clusters, new[] { "a" }, Tenant, apply: true);

        Assert.Equal(1, outcome.EdgesRemoved);
        Assert.Equal(0, outcome.IdsSkippedAmbiguous);
        Assert.Empty(_graph.GetStoredEdgesForEntry("a", Tenant));
        Assert.Empty(_clusters.GetClustersForEntry("a", Tenant));
        Assert.Contains("k", _clusters.GetClustersForEntry("b", Tenant));
        AssertHalvesAgree();
    }

    /// <summary>
    /// And an ambiguous id in the same batch is still skipped rather than swept — the property the
    /// cascade exists for, re-pinned now that the sweep's scope has changed.
    /// </summary>
    [Fact]
    public void CascadeAll_SkipsAnAmbiguousIdAndStillSweepsTheRest()
    {
        Seed("clean", MainNs);
        Seed("other", MainNs);
        Seed("twin", MainNs);
        Link("clean", "other");

        // Linked while "twin" is still attributable, THEN twinned. An edge cannot be created onto an
        // already-ambiguous id, so the twin has to arrive after the fixture edge — which is also the
        // real sequence this guard exists for: topology written under a unique id, and a second
        // entry claiming that id afterwards.
        Link("twin", "other");
        Seed("twin", ShadowNs);

        var outcome = TopologyCascade.CascadeAll(
            _index, _graph, _clusters, new[] { "clean", "twin" }, Tenant, apply: true);

        Assert.Equal(1, outcome.IdsSkippedAmbiguous);
        Assert.Equal(1, outcome.EdgesRemoved);
        Assert.Empty(_graph.GetStoredEdgesForEntry("clean", Tenant));
        Assert.Single(_graph.GetStoredEdgesForEntry("twin", Tenant));
        AssertHalvesAgree();
    }

    /// <summary>
    /// A same-slot replacement DURING the sweep moves no attribution revision (no ambiguity
    /// boundary is crossed), so only the occupancy watch can see it — and seeing it must ABORT
    /// the id as unsettled rather than retry: a retry would re-run the primitives against the
    /// replacement, sweeping topology this pass never staged or judged. The seam fires
    /// immediately before the graph primitive pins the watched partition — the LAST instant a
    /// replacement can land at all: once the pin holds the partition's read lock, a
    /// replacement blocks until the sweep is over, so the check and the mutation are one atom.
    /// The decisive assertion is the last one: the replacement's inherited topology SURVIVES,
    /// where a compare-then-mutate design had already removed the edge by the time the
    /// post-cascade bracket said "unsettled".
    /// </summary>
    [Fact]
    public void CascadeAll_ReplacementDuringSweep_ReportsUnsettledAndDoesNotRetry()
    {
        Seed("swept", MainNs);
        Seed("anchor", MainNs);
        Link("swept", "anchor");

        int seamFired = 0;
        _graph.OnBeforeOccupancyPin = () =>
        {
            if (Interlocked.Increment(ref seamFired) == 1)
            {
                _graph.OnBeforeOccupancyPin = null;
                _index.Upsert(new CognitiveEntry("swept", [0.5f, 0.5f], MainNs,
                    "replacement occupation", tenantId: Tenant));
            }
        };

        var outcome = TopologyCascade.CascadeAll(
            _index, _graph, _clusters, new[] { "swept" }, Tenant, apply: true,
            watchNs: MainNs);

        Assert.True(seamFired >= 1, "the pre-pin seam never fired; the sweep removed nothing");
        Assert.Equal(1, outcome.IdsUnsettled);
        Assert.Equal(0, outcome.IdsSkippedAmbiguous);
        Assert.Equal(0, outcome.EdgesRemoved);
        // The replacement itself survives the aborted sweep...
        Assert.Equal("replacement occupation",
            _index.Get("swept", MainNs, tenantId: Tenant)!.Text);
        // ...and so does the topology it inherited under the same id: the pin refused BEFORE
        // anything came off, rather than reporting a loss that had already happened.
        Assert.Single(_graph.GetStoredEdgesForEntry("swept", Tenant));
        AssertHalvesAgree();
    }
}
