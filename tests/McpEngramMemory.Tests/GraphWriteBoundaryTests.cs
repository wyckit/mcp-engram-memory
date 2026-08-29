using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// THE WRITE BOUNDARY OF <see cref="KnowledgeGraph"/> — the four properties that must hold of EVERY
/// mutator on the class, tested against the mutators the previous rounds' fixes did not reach.
///
/// 1. ADMISSION IS RE-CHECKED AT THE MUTATION. Attribution is resolved BEFORE the graph write lock,
///    deliberately, because the guard resolves through <see cref="CognitiveIndex"/> and index work
///    under the graph lock is a lock-order inversion. That leaves a writable gap: inserting a
///    same-id twin is an ordinary entry write that takes none of the graph's locks, creates no edge
///    and moves no graph revision, so nothing in the graph observes it. The counter compare added
///    last round went into the two ADD paths only; <c>RemoveEdges</c>, <c>RemoveAllEdgesForEntry</c>
///    and <c>TransferEdges</c> build their sweep the same way, hold strictly WIDER windows, and
///    consulted nothing. <c>TransferEdges</c> looked covered and was not: its under-lock
///    <c>IsEdgeKnownUsable</c> walk catches an edge that ARRIVED after the snapshot, and an id the
///    memo already judged safe is returned safe forever — so the one thing it cannot see is
///    attribution MOVING under an endpoint, which is exactly what the counter exists for.
///
/// 2. THE TWO ADJACENCY INDEXES MIRROR EACH OTHER. <c>_outgoing</c> and <c>_incoming</c> are two
///    halves of one structure; a read-modify-write that updates one half leaves a phantom in the
///    other. Every assertion here that could be satisfied by a phantom is paired with
///    <c>FindAdjacencyMirrorViolations</c>, because the suite's existing "consistency" assertion
///    (<c>GetStoredEdges</c> against <c>GetAllEdges</c>) compares two views that BOTH read
///    <c>_outgoing</c> and is structurally incapable of witnessing an <c>_incoming</c>-only leak.
///
/// 3. THE EDGE SAVE TAKES NO SNAPSHOT INSIDE THE WRITE LOCK. It hands persistence a method group
///    that snapshots under the read lock on the debounce thread. Observable from outside because
///    the deferred provider reads LIVE state: a provider handed over by the first write must, when
///    invoked later, report the second write too.
///
/// 4. TENANT KEYS ARE NORMALIZED ON BOTH SIDES. <see cref="GraphEdge"/> normalizes its own
///    TenantId, so every adjacency key ever written is normalized, while a tenant id arriving as a
///    method argument is whatever the principal supplied. Every tenant-scoped test in this suite
///    passes an already-canonical literal, so the split was invisible to all of them; the theories
///    below write under one spelling and read under another.
///
/// SEAMS, and why none of them is a delay. Two shapes are used, both deterministic:
///   - The <c>TopologyGuard.Sweep</c> overload of <c>RemoveAllEdgesForEntry</c> is a seam with no
///     concurrency at all — build the sweep, plant the twin, call the overload with that now-stale
///     sweep.
///   - <see cref="EdgeSeedingLoadHook"/> runs the interfering entry write from inside
///     <c>LoadGlobalEdges</c>, which the graph calls on its FIRST operation, after admission and
///     before the first mutation. That is the window, made to happen on every run.
/// The twin is always planted into a namespace that did not exist when the sweep listed the
/// tenant's namespaces. That is not incidental: the sweep's namespace snapshot bounds every
/// judgement it makes, so a twin in a namespace it already listed would be caught by the guard
/// itself and would prove nothing about the counter.
/// </summary>
public class GraphWriteBoundaryTests : IDisposable
{
    private const string Tenant = "acme";
    private const string MainNs = "main";

    /// <summary>Exists only once a twin is planted in it — see the class note on why that matters.</summary>
    private const string ShadowNs = "shadow";

    private const string LegacyTenant = "";

    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;

    public GraphWriteBoundaryTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"graph_write_boundary_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    // ── fixtures ──

    private static string Json(object? o) => JsonSerializer.Serialize(o);

    /// <summary>Seed straight into the index — no principal, no ownership, no tool.</summary>
    private void Seed(string id, string ns, string tenantId = Tenant)
        => _index.Upsert(new CognitiveEntry(id, [0.5f, 0.5f], ns, $"entry '{id}' in {ns}", tenantId: tenantId));

    /// <summary>
    /// Build a fixture edge, asserting it was actually written. A fixture edge that was silently
    /// refused would make every "nothing moved" assertion below vacuous.
    /// </summary>
    private void Link(string src, string dst, string relation, string tenantId = Tenant)
        => Assert.True(
            _graph.TryAddEdge(new GraphEdge(src, dst, relation, 0.9f, null, tenantId), out _),
            $"fixture edge '{src}' -> '{dst}' ({relation}) was refused");

    /// <summary>
    /// The two adjacency halves agree — every edge in one is present in the other. Reported as a
    /// named list rather than as a bare count so a failure says WHICH phantom survived: the whole
    /// reason this defect kept coming back is that the suite could see a wrong total and never a
    /// wrong edge.
    /// </summary>
    private static void AssertHalvesAgree(KnowledgeGraph graph)
    {
        var violations = graph.FindAdjacencyMirrorViolations();
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 1. ATTRIBUTION MOVES BETWEEN ADMISSION AND THE WRITE — the three mutators that had no check
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>RemoveAllEdgesForEntry</c>'s sweep overload is a deterministic seam with no concurrency in
    /// it at all: the caller supplies the sweep, so the test can build one, let the world move, and
    /// then hand the stale sweep to the write.
    ///
    /// This is the WIDEST window in the class in production too. <c>removableOut</c> and
    /// <c>removableIn</c> are judged after the read lock is released and before the write lock is
    /// taken, and every incident edge is resolved through CognitiveIndex in that gap — including
    /// the far endpoints, which no argument names.
    /// </summary>
    [Fact]
    public void RemoveAllEdgesForEntry_WithASweepThatWentStaleBeforeTheWrite_RemovesNothing()
    {
        Seed("anchor", MainNs);
        Seed("far", MainNs);
        Link("anchor", "far", "elaborates");

        // Admission, exactly as a caller sweeping many entries performs it: one listing, one memo,
        // built while every id is still unique.
        var sweep = TopologyGuard.ForSweep(_index, Tenant);
        Assert.True(sweep.IsEdgeUsable("anchor", "far"));

        long graphRevisionBefore = _graph.RevisionFor(Tenant);

        // The interfering write. An ordinary entry upsert into a namespace the sweep never listed:
        // it takes none of the graph's locks and moves no graph revision, so nothing the graph can
        // see has changed — which is the whole reason a graph-side revision cannot substitute here.
        Seed("far", ShadowNs);
        Assert.Equal(graphRevisionBefore, _graph.RevisionFor(Tenant));

        // And the sweep cannot notice either: its namespace snapshot predates ShadowNs, and its
        // memo already judged 'far'. This is the state the old code deleted against.
        Assert.True(sweep.IsEdgeUsable("anchor", "far"));

        Assert.Equal(0, _graph.RemoveAllEdgesForEntry("anchor", Tenant, sweep));

        // The edge is byte-for-byte what it was, on both endpoints.
        Assert.Single(_graph.GetStoredEdgesForEntry("anchor", tenantId: Tenant));
        Assert.Single(_graph.GetStoredEdgesForEntry("far", tenantId: Tenant));
        AssertHalvesAgree(_graph);

        // The fixture really did make the endpoint ambiguous, so this passes for the right reason.
        Assert.Equal(2, _index.CountNamespacesContaining("far", tenantId: Tenant));
    }

    /// <summary>
    /// OVER-CORRECTION CONTROL. The identical call with nothing racing it must still delete: the
    /// check must refuse a stale admission, not every caller that supplies its own sweep.
    /// </summary>
    [Fact]
    public void RemoveAllEdgesForEntry_WithASweepThatIsStillFresh_RemovesTheEdge()
    {
        Seed("anchor", MainNs);
        Seed("far", MainNs);
        Link("anchor", "far", "elaborates");

        var sweep = TopologyGuard.ForSweep(_index, Tenant);

        Assert.Equal(1, _graph.RemoveAllEdgesForEntry("anchor", Tenant, sweep));
        Assert.Empty(_graph.GetStoredEdgesForEntry("anchor", tenantId: Tenant));
        Assert.Empty(_graph.GetStoredEdgesForEntry("far", tenantId: Tenant));
        AssertHalvesAgree(_graph);
    }

    /// <summary>
    /// <c>RemoveEdges</c> takes no caller-supplied enumerable and no caller-supplied sweep, so the
    /// seam is the graph's own first-write edge load: it runs INSIDE the write lock, after the
    /// pre-lock screen admitted both endpoints and before the first mutation.
    ///
    /// The refusal must be byte-identical to a genuine miss — asserted against a genuine miss
    /// produced by the same method rather than against a literal, because a caller that could tell
    /// "attribution moved under you" apart from "no such edge" would have an oracle for twins it
    /// was never shown.
    /// </summary>
    [Fact]
    public void RemoveEdges_WithATwinPlantedBetweenAdmissionAndTheWrite_RefusesAsAnOrdinaryMiss()
    {
        Seed("rm-a", MainNs);
        Seed("rm-b", MainNs);

        // A genuine miss on the same pair, from a graph that holds no such edge.
        var empty = new KnowledgeGraph(new EdgeSeedingLoadHook(_persistence, [], null), _index);
        string genuineMiss = empty.RemoveEdges("rm-a", "rm-b", relation: null, tenantId: Tenant);

        var edge = new GraphEdge("rm-a", "rm-b", "supports", 0.9f, null, Tenant);
        var graph = new KnowledgeGraph(
            new EdgeSeedingLoadHook(_persistence, [edge], () => Seed("rm-b", ShadowNs)), _index);

        string reply = graph.RemoveEdges("rm-a", "rm-b", relation: null, tenantId: Tenant);

        Assert.Equal(genuineMiss, reply);
        Assert.Single(graph.GetStoredEdgesForEntry("rm-a", tenantId: Tenant));
        Assert.Single(graph.GetStoredEdgesForEntry("rm-b", tenantId: Tenant));
        AssertHalvesAgree(graph);
        Assert.Equal(2, _index.CountNamespacesContaining("rm-b", tenantId: Tenant));
    }

    /// <summary>OVER-CORRECTION CONTROL for <c>RemoveEdges</c>: the identical seam planting nothing
    /// must still remove both directions of the edge.</summary>
    [Fact]
    public void RemoveEdges_WithNothingRacingIt_StillRemovesTheEdge()
    {
        Seed("rm-a", MainNs);
        Seed("rm-b", MainNs);

        var edge = new GraphEdge("rm-a", "rm-b", "supports", 0.9f, null, Tenant);
        var graph = new KnowledgeGraph(new EdgeSeedingLoadHook(_persistence, [edge], null), _index);

        Assert.StartsWith("Removed", graph.RemoveEdges("rm-a", "rm-b", relation: null, tenantId: Tenant));
        Assert.Empty(graph.GetStoredEdgesForEntry("rm-a", tenantId: Tenant));
        Assert.Empty(graph.GetStoredEdgesForEntry("rm-b", tenantId: Tenant));
        AssertHalvesAgree(graph);
    }

    /// <summary>
    /// <c>TransferEdges</c> is the case that LOOKS covered. Its under-lock re-check consults
    /// <c>IsEdgeKnownUsable</c>, which detects an edge that ARRIVED after the snapshot — an id the
    /// memo has never judged. An id the memo already judged safe is returned safe forever, so the
    /// one thing that check cannot see is an endpoint that has since crossed the ambiguity
    /// boundary, which is precisely what the counter exists for.
    ///
    /// The twin lands on <c>tx-far</c>: the THIRD endpoint, named by no argument, whose adjacency
    /// list the rewrite would edit.
    /// </summary>
    [Fact]
    public void TransferEdges_WithATwinPlantedBetweenAdmissionAndTheWrite_MovesNothing()
    {
        Seed("tx-from", MainNs);
        Seed("tx-to", MainNs);
        Seed("tx-far", MainNs);

        var edge = new GraphEdge("tx-from", "tx-far", "elaborates", 0.9f, null, Tenant);
        var graph = new KnowledgeGraph(
            new EdgeSeedingLoadHook(_persistence, [edge], () => Seed("tx-far", ShadowNs)), _index);

        // NOTHING may read this graph before the call under test. The seam is the graph's FIRST
        // edge load, so any earlier read would fire the hook, plant the twin before the sweep is
        // built, and let the ordinary pre-lock screen refuse — the test would pass having exercised
        // the old code path instead of the new one.

        // Zero is what a merge of two edgeless entries already reports, so it is truthful without
        // being a signal.
        Assert.Equal(0, graph.TransferEdges("tx-from", "tx-to", tenantId: Tenant));

        // The third endpoint — named by no argument — still carries the original edge, unrewritten.
        Assert.Equal("tx-from", Assert.Single(graph.GetStoredEdgesForEntry("tx-far", tenantId: Tenant)).SourceId);
        Assert.Single(graph.GetStoredEdgesForEntry("tx-from", tenantId: Tenant));
        Assert.Empty(graph.GetStoredEdgesForEntry("tx-to", tenantId: Tenant));
        AssertHalvesAgree(graph);
        Assert.Equal(2, _index.CountNamespacesContaining("tx-far", tenantId: Tenant));
    }

    /// <summary>OVER-CORRECTION CONTROL for <c>TransferEdges</c>: the identical seam planting
    /// nothing must still move the edge, all of it.</summary>
    [Fact]
    public void TransferEdges_WithNothingRacingIt_StillMovesTheEdge()
    {
        Seed("tx-from", MainNs);
        Seed("tx-to", MainNs);
        Seed("tx-far", MainNs);

        var edge = new GraphEdge("tx-from", "tx-far", "elaborates", 0.9f, null, Tenant);
        var graph = new KnowledgeGraph(new EdgeSeedingLoadHook(_persistence, [edge], null), _index);

        Assert.Equal(1, graph.TransferEdges("tx-from", "tx-to", tenantId: Tenant));
        Assert.Empty(graph.GetStoredEdgesForEntry("tx-from", tenantId: Tenant));
        Assert.Equal("tx-to", Assert.Single(graph.GetStoredEdgesForEntry("tx-far", tenantId: Tenant)).SourceId);
        AssertHalvesAgree(graph);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 2. THE TWO ADJACENCY INDEXES MIRROR EACH OTHER
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The reviewer's phantom, from one <c>merge_memories</c> and no concurrency at all.
    ///
    /// <c>TransferEdges</c>'s outgoing branch added <c>to -&gt; X</c> and dropped
    /// <c>_outgoing[(t, from)]</c> wholesale, but <c>AddEdgeInternal</c>'s dedup only matches
    /// <c>SourceId == to</c>, so the old <c>from -&gt; X</c> survived in <c>_incoming[(t, X)]</c>
    /// with no counterpart anywhere. <c>X</c> is named by no argument of the call, and no existing
    /// test looks at a third endpoint's adjacency after a transfer.
    ///
    /// The three observable outcomes are asserted, not just the count: get_neighbors reporting a
    /// predecessor that no longer exists, the phantom being invisible to everything that reads
    /// <c>_outgoing</c> (so a restart answers differently from the live process), and the pair
    /// counting as already-linked to the auto-link scan forever.
    /// </summary>
    [Fact]
    public void TransferEdges_LeavesNoPhantomPredecessorOnAThirdEndpoint()
    {
        Seed("alpha", MainNs);
        Seed("zeta", MainNs);
        Seed("keep", MainNs);
        Link("zeta", "alpha", "depends_on");

        Assert.Equal(1, _graph.TransferEdges("zeta", "keep", tenantId: Tenant));

        // THE ASSERTION NO EXISTING TEST MAKES: the third endpoint's own adjacency.
        Assert.Equal("keep", Assert.Single(_graph.GetStoredEdgesForEntry("alpha", tenantId: Tenant)).SourceId);

        // (1) get_neighbors must not report 'zeta' as a live predecessor of an edge that moved.
        var incoming = _graph.GetNeighbors("alpha", relation: null, direction: "incoming", tenantId: Tenant);
        Assert.Equal("keep", Assert.Single(incoming.Neighbors).Edge.SourceId);

        // (2) the _outgoing-only views and the endpoint views agree, so the persisted graph and the
        //     live one answer the same question the same way.
        Assert.Equal(1, _graph.EdgeCount);
        AssertHalvesAgree(_graph);

        // (3) the scan's already-linked test unions _incoming, so a phantom starves the pair
        //     (alpha, zeta) permanently if 'zeta' is ever re-created under the same id.
        Assert.DoesNotContain(
            _graph.GetStoredEdgesForEntry("alpha", tenantId: Tenant),
            e => e.SourceId == "zeta" || e.TargetId == "zeta");
    }

    /// <summary>
    /// The self-referential skip leaks the same way. <c>from -&gt; to</c> would become
    /// <c>to -&gt; to</c>, so it is not transferred — but it must VANISH, not survive in
    /// <c>_incoming[(t, to)]</c> as a predecessor <c>from</c> that <c>_outgoing</c> no longer knows
    /// about. <c>merge_memories</c> ARCHIVES the source rather than deleting it, so that phantom
    /// resolves to a real entry and is shown to the caller.
    /// </summary>
    [Fact]
    public void TransferEdges_SelfReferentialOutgoingEdge_IsDroppedFromBothHalves()
    {
        Seed("from", MainNs);
        Seed("to", MainNs);
        Seed("far", MainNs);
        Link("from", "to", "similar_to");
        Link("from", "far", "depends_on");

        Assert.Equal(1, _graph.TransferEdges("from", "to", tenantId: Tenant));

        var toStored = _graph.GetStoredEdgesForEntry("to", tenantId: Tenant);
        var moved = Assert.Single(toStored);
        Assert.Equal("to", moved.SourceId);
        Assert.Equal("far", moved.TargetId);

        Assert.Empty(_graph.GetStoredEdgesForEntry("from", tenantId: Tenant));
        AssertHalvesAgree(_graph);
    }

    /// <summary>
    /// THE SYMMETRIC CASE, found by sweeping the method rather than by fixing the branch that was
    /// reported. The incoming branch cleans its mirror for every edge it transfers, but its own
    /// self-referential skip (<c>to -&gt; from</c>, which would become <c>to -&gt; to</c>) jumped
    /// the cleanup — so <c>_incoming[(t, from)]</c> was dropped wholesale while <c>to -&gt; from</c>
    /// stayed alive in <c>_outgoing[(t, to)]</c>. Same phantom, opposite half, reachable from the
    /// same single merge.
    /// </summary>
    [Fact]
    public void TransferEdges_SelfReferentialIncomingEdge_IsDroppedFromBothHalves()
    {
        Seed("from", MainNs);
        Seed("to", MainNs);
        Seed("origin", MainNs);
        Link("to", "from", "supports");
        Link("origin", "from", "depends_on");

        Assert.Equal(1, _graph.TransferEdges("from", "to", tenantId: Tenant));

        var toStored = _graph.GetStoredEdgesForEntry("to", tenantId: Tenant);
        var moved = Assert.Single(toStored);
        Assert.Equal("origin", moved.SourceId);
        Assert.Equal("to", moved.TargetId);

        Assert.Empty(_graph.GetStoredEdgesForEntry("from", tenantId: Tenant));
        AssertHalvesAgree(_graph);
    }

    /// <summary>
    /// OVER-CORRECTION CONTROL for the mirror cleanup: the ordinary transfer must still move
    /// everything, in both directions, and leave the two halves agreeing.
    /// </summary>
    [Fact]
    public void TransferEdges_WithNoSelfReference_MovesBothDirectionsAndLeavesTheHalvesAgreeing()
    {
        Seed("from", MainNs);
        Seed("to", MainNs);
        Seed("far", MainNs);
        Seed("origin", MainNs);
        Link("from", "far", "depends_on");
        Link("origin", "from", "elaborates");

        Assert.Equal(2, _graph.TransferEdges("from", "to", tenantId: Tenant));

        Assert.Empty(_graph.GetStoredEdgesForEntry("from", tenantId: Tenant));
        Assert.Equal(2, _graph.GetStoredEdgesForEntry("to", tenantId: Tenant).Count);
        Assert.Equal("to", Assert.Single(_graph.GetStoredEdgesForEntry("far", tenantId: Tenant)).SourceId);
        Assert.Equal("to", Assert.Single(_graph.GetStoredEdgesForEntry("origin", tenantId: Tenant)).TargetId);
        Assert.Equal(2, _graph.EdgeCount);
        AssertHalvesAgree(_graph);
    }

    /// <summary>
    /// The mirror must also survive the paths that rewrite adjacency without transferring: the
    /// batch write boundary, the replace-same-relation dedup, and both removals. This is the seam
    /// the class's <c>OnlyIfUnlinked</c> dedup-skip now leans on — it is sound only while the two
    /// halves agree, so the invariant is pinned here rather than assumed.
    /// </summary>
    [Fact]
    public void EveryMutator_LeavesTheTwoAdjacencyHalvesAgreeing()
    {
        Seed("m1", MainNs);
        Seed("m2", MainNs);
        Seed("m3", MainNs);

        Link("m1", "m2", "supports");
        // Same (source, target, relation): exercises AddEdgeInternal's dedup, which rewrites both
        // halves in place.
        Link("m1", "m2", "supports");
        AssertHalvesAgree(_graph);

        // cross_reference materializes the reverse edge as well.
        Link("m2", "m3", "cross_reference");
        AssertHalvesAgree(_graph);

        Assert.Equal(1, _graph.AddEdges(
            [new GraphEdge("m1", "m3", "similar_to", 0.9f, null, Tenant)], EdgeAddMode.OnlyIfUnlinked));
        AssertHalvesAgree(_graph);

        // ...and the already-related pair is declined without disturbing either half.
        Assert.Equal(0, _graph.AddEdges(
            [new GraphEdge("m1", "m2", "similar_to", 0.9f, null, Tenant)], EdgeAddMode.OnlyIfUnlinked));
        AssertHalvesAgree(_graph);

        Assert.StartsWith("Removed", _graph.RemoveEdges("m1", "m2", relation: null, tenantId: Tenant));
        AssertHalvesAgree(_graph);

        Assert.True(_graph.RemoveAllEdgesForEntry("m3", Tenant) > 0);
        AssertHalvesAgree(_graph);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 3. THE EDGE SAVE TAKES NO SNAPSHOT INSIDE THE WRITE LOCK
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The eager form materialized the entire cross-tenant edge set inside the exclusive write
    /// lock, on every mutation, and the debounce then threw almost all of it away — it disposes the
    /// pending timer and OVERWRITES the pending provider, so only the last proposal in a window is
    /// ever serialized.
    ///
    /// Observable from outside without measuring anything: a deferred provider reads LIVE state, so
    /// the provider handed over by the FIRST write must report the second write too. The eager form
    /// returns the graph as of its own write and fails this.
    /// </summary>
    [Fact]
    public void TheEdgeSaveSnapshotIsTakenWhenTheProviderRuns_NotInsideTheWriteLock()
    {
        Seed("s1", MainNs);
        Seed("s2", MainNs);
        Seed("s3", MainNs);

        var recorder = new EdgeSaveRecorder(_persistence, forward: false);
        var graph = new KnowledgeGraph(recorder, _index);

        Assert.True(graph.TryAddEdge(new GraphEdge("s1", "s2", "supports", 0.9f, null, Tenant), out _));
        var firstProvider = Assert.Single(recorder.Providers);

        Assert.True(graph.TryAddEdge(new GraphEdge("s1", "s3", "supports", 0.9f, null, Tenant), out _));
        Assert.Equal(2, recorder.Providers.Count);

        // THE ASSERTION THE EAGER FORM FAILED: 1, the graph as of the write that scheduled it.
        Assert.Equal(2, firstProvider().Count);
        Assert.Equal(2, recorder.Providers[^1]().Count);
    }

    /// <summary>
    /// Deferring must not change WHAT is saved. The snapshot is the whole cross-tenant edge set, in
    /// the same order the raw accessor reports, so the hand-rolled preallocated walk that replaced
    /// the LINQ chain is byte-identical to it.
    /// </summary>
    [Fact]
    public void TheEdgeSaveSnapshotCarriesEveryTenantsEdges()
    {
        Seed("a", MainNs);
        Seed("b", MainNs);
        Seed("l1", MainNs, LegacyTenant);
        Seed("l2", MainNs, LegacyTenant);

        var recorder = new EdgeSaveRecorder(_persistence, forward: false);
        var graph = new KnowledgeGraph(recorder, _index);

        Assert.True(graph.TryAddEdge(new GraphEdge("a", "b", "supports", 0.9f, null, Tenant), out _));
        Assert.True(graph.TryAddEdge(new GraphEdge("l1", "l2", "supports", 0.9f, null, LegacyTenant), out _));

        var saved = recorder.Providers[^1]();
        Assert.Equal(2, saved.Count);
        Assert.Equal(Json(graph.GetAllEdges()), Json(saved));
    }

    /// <summary>
    /// THE PERSISTENCE CONTRACT, end to end. <c>Flush</c> invokes the provider synchronously on the
    /// caller's thread, AFTER releasing the storage layer's own timer lock — which is what makes it
    /// safe for the provider to take the graph's read lock. If it took the write lock, or if the
    /// storage layer still held its timer lock while calling back, this is where it would throw
    /// <c>LockRecursionException</c> or hang.
    ///
    /// A long debounce so the timer never fires: <c>Flush</c> is then the only thing that runs the
    /// provider, and the round-trip is deterministic rather than a race with a background timer.
    /// </summary>
    [Fact]
    public void TheDeferredSnapshotRoundTripsThroughTheRealFlushPath()
    {
        Seed("f1", MainNs);
        Seed("f2", MainNs);

        var path = Path.Combine(Path.GetTempPath(), $"graph_write_flush_{Guid.NewGuid():N}");
        var persistence = new PersistenceManager(path, debounceMs: 600_000);
        try
        {
            var graph = new KnowledgeGraph(persistence, _index);
            Assert.True(graph.TryAddEdge(new GraphEdge("f1", "f2", "supports", 0.9f, null, Tenant), out _));

            persistence.Flush();

            var persisted = persistence.LoadGlobalEdges();
            var edge = Assert.Single(persisted);
            Assert.Equal("f1", edge.SourceId);
            Assert.Equal("f2", edge.TargetId);
        }
        finally
        {
            persistence.Dispose();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 4. TENANT KEYS ARE NORMALIZED ON BOTH SIDES
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every tenant-scoped test in this suite passes an already-canonical literal, so the raw-vs-
    /// normalized split was invisible to all of them. The shipped stdio host normalizes at
    /// PrincipalContext — but <c>IPrincipalContext</c> is a documented extension point with no
    /// normalization of its own, and a host returning a padded claim value writes edges under
    /// <c>"acme"</c> (GraphEdge normalizes) while every read passes <c>"acme "</c> straight through.
    ///
    /// The symptom is total and silent: unlink reports no such edge for edges that exist, traverse
    /// returns an empty walk, and the diffusion kernel is handed an empty basis.
    /// </summary>
    [Theory]
    [InlineData("acme")]
    [InlineData(" acme")]
    [InlineData("acme ")]
    [InlineData("  acme  ")]
    public void TopologyWrittenUnderOneSpellingOfATenant_IsVisibleToEveryOtherSpelling(string spelling)
    {
        Seed("n1", MainNs);
        Seed("n2", MainNs);

        Assert.True(_graph.TryAddEdge(new GraphEdge("n1", "n2", "supports", 0.9f, null, spelling), out _));

        Assert.Single(_graph.GetStoredEdgesForEntry("n1", tenantId: spelling));
        Assert.Single(_graph.GetEdgesForEntry("n1", tenantId: spelling));
        Assert.Single(_graph.GetStoredEdges(spelling));
        Assert.Single(_graph.GetAllEdges(spelling));
        Assert.Single(_graph.GetNeighbors("n1", relation: null, direction: "both", tenantId: spelling).Neighbors);
        Assert.Equal(2, _graph.Traverse("n1", spelling).Entries.Count);
        Assert.True(_graph.RevisionFor(spelling) > 0);

        // The mutators are where a missed key is worst: it is a silent no-op reported as success.
        Assert.StartsWith("Removed", _graph.RemoveEdges("n1", "n2", relation: null, tenantId: spelling));
        Assert.Empty(_graph.GetStoredEdgesForEntry("n1", tenantId: Tenant));
    }

    /// <summary>
    /// The same split against the MUTATORS, which is where it is worst: a dictionary probe that
    /// misses is a silent no-op, so a merge reports a truthful-looking 0 and a cascade delete
    /// reports nothing to remove, both while the edges sit there under the normalized key.
    /// </summary>
    [Theory]
    [InlineData("acme")]
    [InlineData(" acme")]
    [InlineData("acme ")]
    public void TransferAndCascadeKeyedByAPaddedTenantSpelling_StillReachTheEdges(string spelling)
    {
        Seed("t-from", MainNs);
        Seed("t-to", MainNs);
        Seed("t-far", MainNs);
        Link("t-from", "t-far", "elaborates");

        Assert.Equal(1, _graph.TransferEdges("t-from", "t-to", tenantId: spelling));
        Assert.Equal("t-to", Assert.Single(_graph.GetStoredEdgesForEntry("t-far", tenantId: Tenant)).SourceId);

        Assert.Equal(1, _graph.RemoveAllEdgesForEntry("t-to", spelling));
        Assert.Empty(_graph.GetStoredEdgesForEntry("t-far", tenantId: Tenant));
        AssertHalvesAgree(_graph);
    }

    /// <summary>
    /// THE LEGACY MIRROR. Every blank spelling collapses to the pre-tenancy partition <c>""</c>,
    /// so a host handing back a whitespace-only tenant claim must reach exactly the same edges as
    /// one handing back the empty string.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void InTheLegacyPartition_EveryBlankSpellingNamesTheSameTenant(string spelling)
    {
        Seed("leg-1", MainNs, LegacyTenant);
        Seed("leg-2", MainNs, LegacyTenant);
        Link("leg-1", "leg-2", "supports", LegacyTenant);

        Assert.Single(_graph.GetStoredEdgesForEntry("leg-1", tenantId: spelling));
        Assert.Single(_graph.GetAllEdges(spelling));
        Assert.Single(_graph.GetNeighbors("leg-1", relation: null, direction: "both", tenantId: spelling).Neighbors);
        Assert.Equal(2, _graph.Traverse("leg-1", spelling).Entries.Count);
        Assert.StartsWith("Removed", _graph.RemoveEdges("leg-1", "leg-2", relation: null, tenantId: spelling));
        AssertHalvesAgree(_graph);
    }
}

/// <summary>
/// A storage provider that delegates everything, hands the graph a fixed edge set on load, and runs
/// one action the first time that load happens.
///
/// THE DETERMINISTIC SEAM FOR A WRITE PATH WITH NO CALLER-SUPPLIED ENUMERABLE AND NO CALLER-SUPPLIED
/// SWEEP. <c>RemoveEdges</c> and <c>TransferEdges</c> take neither, so there is nothing lazy to
/// suspend them inside — but they do load persisted edges on their first operation, after the guard
/// has admitted their arguments and before the first mutation, which is exactly the window the
/// attribution race lives in. Running the interfering entry write from there makes the interleaving
/// happen on every run, with no delay, no polling and no pair of threads that could miss each other.
///
/// The seeded edge list is what lets the graph be non-empty at that instant: a fixture that added
/// the edge through the same graph would have consumed the one-shot load before the operation under
/// test began.
///
/// Fires once. The graph loads its edges once per instance, and a hook that could fire again would
/// make the test depend on how many times an implementation detail happens to call back.
/// </summary>
file sealed class EdgeSeedingLoadHook : IStorageProvider
{
    private readonly IStorageProvider _inner;
    private readonly List<GraphEdge> _seed;
    private Action? _onFirstLoad;

    public EdgeSeedingLoadHook(IStorageProvider inner, IEnumerable<GraphEdge> seed, Action? onFirstLoad)
    {
        _inner = inner;
        _seed = seed.ToList();
        _onFirstLoad = onFirstLoad;
    }

    public List<GraphEdge> LoadGlobalEdges()
    {
        // A fresh list each call: the graph takes ownership of what it is handed.
        var edges = new List<GraphEdge>(_seed);
        var hook = _onFirstLoad;
        _onFirstLoad = null;
        hook?.Invoke();
        return edges;
    }

    public NamespaceData LoadNamespace(string ns) => _inner.LoadNamespace(ns);
    public IReadOnlyList<string> GetPersistedNamespaces() => _inner.GetPersistedNamespaces();
    public void ScheduleSave(string ns, Func<NamespaceData> dataProvider) => _inner.ScheduleSave(ns, dataProvider);
    public void SaveNamespaceSync(string ns, NamespaceData data) => _inner.SaveNamespaceSync(ns, data);
    public bool SupportsIncrementalWrites => _inner.SupportsIncrementalWrites;
    public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) => _inner.ScheduleUpsertEntry(ns, entry);
    public void ScheduleDeleteEntry(string ns, string entryId) => _inner.ScheduleDeleteEntry(ns, entryId);
    public void ScheduleDeleteEntry(string ns, string entryId, string tenantId) => _inner.ScheduleDeleteEntry(ns, entryId, tenantId);
    public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider) => _inner.ScheduleSaveGlobalEdges(dataProvider);
    public List<SemanticCluster> LoadClusters() => _inner.LoadClusters();
    public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider) => _inner.ScheduleSaveClusters(dataProvider);
    public List<CollapseRecord> LoadCollapseHistory() => _inner.LoadCollapseHistory();
    public void ScheduleSaveCollapseHistory(Func<List<CollapseRecord>> dataProvider) => _inner.ScheduleSaveCollapseHistory(dataProvider);
    public Dictionary<string, DecayConfig> LoadDecayConfigs() => _inner.LoadDecayConfigs();
    public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider) => _inner.ScheduleSaveDecayConfigs(dataProvider);
    public HnswSnapshot? LoadHnswSnapshot(string ns) => _inner.LoadHnswSnapshot(ns);
    public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot) => _inner.SaveHnswSnapshotSync(ns, snapshot);
    public void DeleteHnswSnapshot(string ns) => _inner.DeleteHnswSnapshot(ns);
    public Task DeleteNamespaceAsync(string ns) => _inner.DeleteNamespaceAsync(ns);
    public Task DeleteNamespaceAsync(string ns, string tenantId) => _inner.DeleteNamespaceAsync(ns, tenantId);
    public void Flush() => _inner.Flush();

    // The inner provider belongs to the fixture, which disposes it. Tearing it down here would take
    // the store out from under the rest of the test.
    public void Dispose() { }
}

/// <summary>
/// A storage provider that records every edge-save provider it is handed instead of (or as well as)
/// forwarding it.
///
/// THE SEAM FOR WHEN THE SNAPSHOT IS TAKEN. The graph hands persistence a delegate; whether that
/// delegate closes over a list captured inside the write lock or reads the graph when it runs is
/// invisible to every existing test, because the real providers debounce on a timer and the
/// fixtures never look at what the timer was given. Holding the delegate and invoking it later is
/// what makes the difference observable, with no timing and no threads.
///
/// <c>forward: false</c> keeps the real debounce out of the test entirely, so the recorded
/// delegates are invoked exactly when the test invokes them.
/// </summary>
file sealed class EdgeSaveRecorder : IStorageProvider
{
    private readonly IStorageProvider _inner;
    private readonly bool _forward;
    private readonly List<Func<List<GraphEdge>>> _providers = new();

    public EdgeSaveRecorder(IStorageProvider inner, bool forward)
    {
        _inner = inner;
        _forward = forward;
    }

    /// <summary>Every provider handed to <see cref="ScheduleSaveGlobalEdges"/>, oldest first.</summary>
    public IReadOnlyList<Func<List<GraphEdge>>> Providers => _providers;

    public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider)
    {
        _providers.Add(dataProvider);
        if (_forward) _inner.ScheduleSaveGlobalEdges(dataProvider);
    }

    public List<GraphEdge> LoadGlobalEdges() => _inner.LoadGlobalEdges();
    public NamespaceData LoadNamespace(string ns) => _inner.LoadNamespace(ns);
    public IReadOnlyList<string> GetPersistedNamespaces() => _inner.GetPersistedNamespaces();
    public void ScheduleSave(string ns, Func<NamespaceData> dataProvider) => _inner.ScheduleSave(ns, dataProvider);
    public void SaveNamespaceSync(string ns, NamespaceData data) => _inner.SaveNamespaceSync(ns, data);
    public bool SupportsIncrementalWrites => _inner.SupportsIncrementalWrites;
    public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) => _inner.ScheduleUpsertEntry(ns, entry);
    public void ScheduleDeleteEntry(string ns, string entryId) => _inner.ScheduleDeleteEntry(ns, entryId);
    public void ScheduleDeleteEntry(string ns, string entryId, string tenantId) => _inner.ScheduleDeleteEntry(ns, entryId, tenantId);
    public List<SemanticCluster> LoadClusters() => _inner.LoadClusters();
    public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider) => _inner.ScheduleSaveClusters(dataProvider);
    public List<CollapseRecord> LoadCollapseHistory() => _inner.LoadCollapseHistory();
    public void ScheduleSaveCollapseHistory(Func<List<CollapseRecord>> dataProvider) => _inner.ScheduleSaveCollapseHistory(dataProvider);
    public Dictionary<string, DecayConfig> LoadDecayConfigs() => _inner.LoadDecayConfigs();
    public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider) => _inner.ScheduleSaveDecayConfigs(dataProvider);
    public HnswSnapshot? LoadHnswSnapshot(string ns) => _inner.LoadHnswSnapshot(ns);
    public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot) => _inner.SaveHnswSnapshotSync(ns, snapshot);
    public void DeleteHnswSnapshot(string ns) => _inner.DeleteHnswSnapshot(ns);
    public Task DeleteNamespaceAsync(string ns) => _inner.DeleteNamespaceAsync(ns);
    public Task DeleteNamespaceAsync(string ns, string tenantId) => _inner.DeleteNamespaceAsync(ns, tenantId);
    public void Flush() => _inner.Flush();

    // See EdgeSeedingLoadHook: the inner provider belongs to the fixture.
    public void Dispose() { }
}
