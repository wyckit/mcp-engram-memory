using System.Diagnostics;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// THE ATTRIBUTION FENCE — that an ambiguity-changing entry write cannot land between a topology
/// mutator's final attribution validation and its mutation, for every mutator in
/// <see cref="KnowledgeGraph"/> and <see cref="ClusterManager"/>.
///
/// WHY THIS SUITE EXISTS AND THE PREVIOUS ONE DID NOT REACH IT. The previous round closed the
/// admission-to-mutation window by sampling a per-tenant attribution counter inside the graph write
/// lock. Sampling a counter does not close a race; it moves the race to the interval between the
/// sample and the mutation. Nothing in <see cref="CognitiveIndex"/> coordinates with the graph or
/// cluster locks, so a twin could still be planted after the comparison and before the write — and
/// every test written for that fix injected its interfering write BEFORE the comparison (from
/// inside <c>LoadGlobalEdges</c>, or by handing over a deliberately stale sweep), so none of them
/// could observe the interval that was still open. A test that cannot fail on the remaining window
/// is not evidence about the remaining window.
///
/// THE SEAM. <c>OnValidatedUnderFence</c> is an internal hook each mutator invokes while it holds
/// the fence's shared side and its own write lock, after the validation and before the first
/// mutation. Suspending a REAL mutator there is the only way to put an interfering write in the
/// exact window, and it has to be a real one: a reconstruction of the sequence would prove things
/// about the reconstruction.
///
/// THE RENDEZVOUS IS A CONDITION, NEVER A DELAY. The interfering write runs on a second thread and
/// the suspended mutator waits until one of two OBSERVABLE states holds — the write has parked on
/// the fence's exclusive side (<see cref="CognitiveIndex.AttributionFenceWaitingWriters"/> &gt; 0),
/// or it has run to completion inside the window. Those two are the fenced and unfenced worlds, and
/// the assertion is about which one happened. Remove the fence and the second state is what the
/// test sees; there is no timing to get lucky with, and a budget that expires without either state
/// fails loudly rather than passing quietly.
///
/// The twin always lands in a namespace that did not exist when the sweep listed the tenant's, so
/// the guard's own namespace snapshot cannot catch it and the fence is the only thing under test.
/// </summary>
public class AttributionFenceTests : IDisposable
{
    private const string Tenant = "acme";
    private const string MainNs = "main";

    /// <summary>Exists only once a twin is planted in it — see the class note.</summary>
    private const string ShadowNs = "shadow";

    /// <summary>
    /// The budget for a state to become observable, not a sleep. Nothing waits it out on a passing
    /// run: each wait ends the instant the condition holds, and exhausting it is a failure with a
    /// message rather than a silent pass.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusterManager;

    public AttributionFenceTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"attribution_fence_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusterManager = new ClusterManager(_index, _persistence);
    }

    public void Dispose()
    {
        _graph.OnValidatedUnderFence = null;
        _clusterManager.OnValidatedUnderFence = null;
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    // ── fixtures ──

    /// <summary>Seed straight into the index — no principal, no ownership, no tool.</summary>
    private void Seed(string id, string ns, string tenantId = Tenant)
        => _index.Upsert(new CognitiveEntry(id, [0.5f, 0.5f], ns, $"entry '{id}' in {ns}", tenantId: tenantId));

    private static GraphEdge Edge(string src, string dst, string relation = "supports")
        => new(src, dst, relation, 0.9f, null, Tenant);

    /// <summary>Build a fixture edge, asserting it was actually written — a silently refused fixture
    /// edge would make every assertion about it vacuous.</summary>
    private void Link(string src, string dst, string relation = "supports")
        => Assert.True(_graph.TryAddEdge(Edge(src, dst, relation), out _),
            $"fixture edge '{src}' -> '{dst}' ({relation}) was refused");

    private static void AssertHalvesAgree(KnowledgeGraph graph)
    {
        var violations = graph.FindAdjacencyMirrorViolations();
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // THE RENDEZVOUS
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private sealed class RaceOutcome
    {
        /// <summary>The interfering write parked on the fence's exclusive side inside the window.</summary>
        public bool BlockedInWindow;

        /// <summary>The interfering write ran to completion inside the window — the defect.</summary>
        public bool CompletedInWindow;

        /// <summary>One of the two above became observable before the budget ran out.</summary>
        public bool Observed => BlockedInWindow || CompletedInWindow;

        /// <summary>The window was entered at all — i.e. the mutator reached its seam.</summary>
        public bool WindowEntered;

        public bool CompletedEventually;
        public Exception? Failure;
    }

    /// <summary>
    /// Run <paramref name="mutate"/> and land <paramref name="interferingWrite"/> in the interval
    /// between that mutator's attribution validation and its mutation.
    ///
    /// <paramref name="setHook"/> is how the caller reaches whichever class's seam is under test;
    /// it is set for the duration of the one call and cleared in a finally, so a mutator running
    /// later in the same test is unaffected.
    /// </summary>
    private RaceOutcome Race(Action<Action?> setHook, Action mutate, Action interferingWrite)
    {
        var outcome = new RaceOutcome();

        // Deliberately not disposed. Both events are reachable from the worker, and the only path
        // that reaches the end of this method with the worker still alive is one where Join timed
        // out — exactly the failing run, where disposing would replace the assertion's message with
        // an ObjectDisposedException from a background thread. Two event handles per test is a
        // price worth paying for a legible failure.
        var started = new ManualResetEventSlim(false);
        var done = new ManualResetEventSlim(false);
        bool workerLaunched = false;

        var worker = new Thread(() =>
        {
            started.Set();
            try { interferingWrite(); }
            catch (Exception ex) { outcome.Failure = ex; }
            finally { done.Set(); }
        })
        { IsBackground = true, Name = "interfering-writer" };

        setHook(() =>
        {
            // Inside the mutator: past its under-fence attribution validation, before its first
            // mutation, holding the fence's shared side and its own write lock. This is the
            // interval the earlier round's tests could not reach.
            //
            // Guarded because a mutator that invoked the seam twice would call Thread.Start twice;
            // the guard turns that into a visible assertion failure rather than an exception from
            // deep inside a lock.
            Assert.False(workerLaunched, "the mutator entered its validated-under-fence seam twice");
            workerLaunched = true;
            outcome.WindowEntered = true;

            worker.Start();
            Assert.True(started.Wait(Budget), "the interfering writer never started");

            // THE CONDITION. Exactly one of these becomes true, and which one is the whole result:
            // the write parks on the fence's exclusive side (fenced), or it completes inside the
            // window (unfenced). Spun rather than slept, so a passing run leaves here immediately.
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < Budget)
            {
                if (_index.AttributionFenceWaitingWriters(Tenant) > 0)
                {
                    outcome.BlockedInWindow = true;
                    break;
                }
                if (done.IsSet)
                {
                    outcome.CompletedInWindow = true;
                    break;
                }
                Thread.Yield();
            }
        });

        try { mutate(); }
        finally { setHook(null); }

        // Only wait on a thread that was actually started. A mutator that refused before reaching
        // its seam never launched the worker, and Join on an unstarted thread throws — which would
        // replace the informative "never reached its seam" assertion below with a ThreadStateException.
        if (workerLaunched)
        {
            outcome.CompletedEventually = done.Wait(Budget);
            worker.Join(Budget);
        }

        return outcome;
    }

    /// <summary>
    /// The fence held: the crossing could not complete inside the window, and completed afterwards.
    /// </summary>
    private static void AssertCrossingWasFenced(RaceOutcome o)
    {
        Assert.True(o.WindowEntered,
            "the mutator never reached its validated-under-fence seam — it refused before mutating, " +
            "so this run proves nothing about the fence");
        Assert.True(o.Observed,
            "the interfering entry write neither parked on the fence nor completed within the budget");
        Assert.False(o.CompletedInWindow,
            "an ambiguity-changing entry write completed between the mutator's attribution validation " +
            "and its mutation — the fence did not hold, which is exactly the interval a revision " +
            "compare leaves open");
        Assert.True(o.BlockedInWindow);
        Assert.True(o.CompletedEventually,
            "the interfering entry write never completed after the fence was released");
        Assert.Null(o.Failure);
    }

    /// <summary>
    /// The over-correction control: a write that crosses NO ambiguity boundary must not be fenced
    /// at all. If it were, every ordinary upsert in a tenant would queue behind every graph and
    /// cluster write in it.
    /// </summary>
    private static void AssertWriteWasNotFenced(RaceOutcome o)
    {
        Assert.True(o.WindowEntered, "the mutator never reached its validated-under-fence seam");
        Assert.True(o.Observed,
            "the entry write neither completed nor parked within the budget");
        Assert.False(o.BlockedInWindow,
            "an entry write that crosses no ambiguity boundary was serialized by the attribution " +
            "fence — the fence is supposed to exclude crossings, not ordinary writes");
        Assert.True(o.CompletedInWindow);
        Assert.Null(o.Failure);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 1. THE GRAPH'S FIVE MUTATORS — a twin planted AFTER validation, in the exact window
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TryAddEdge_TwinPlantedAfterValidation_CannotLandBeforeTheMutation()
    {
        Seed("src", MainNs);
        Seed("dst", MainNs);

        var outcome = Race(
            h => _graph.OnValidatedUnderFence = h,
            () => Assert.True(_graph.TryAddEdge(Edge("src", "dst"), out _)),
            () => Seed("src", ShadowNs));

        AssertCrossingWasFenced(outcome);

        // CONSISTENT EITHER WAY, which is the second half of the property. The edge was written
        // while both endpoints were attributable, so it is stored; the twin then landed, so the
        // attributable view withholds it. What must never exist is an edge written AFTER the twin
        // landed — that is the unattributable write the fence prevents.
        Assert.Single(_graph.GetStoredEdgesForEntry("src", Tenant));
        Assert.Empty(_graph.GetEdgesForEntry("src", Tenant));
        AssertHalvesAgree(_graph);
    }

    [Fact]
    public void AddEdges_TwinPlantedAfterValidation_CannotLandBeforeTheMutation()
    {
        Seed("src", MainNs);
        Seed("dst", MainNs);

        int written = 0;
        var outcome = Race(
            h => _graph.OnValidatedUnderFence = h,
            () => written = _graph.AddEdges(new[] { Edge("src", "dst") }),
            () => Seed("src", ShadowNs));

        AssertCrossingWasFenced(outcome);
        Assert.Equal(1, written);
        Assert.Single(_graph.GetStoredEdgesForEntry("src", Tenant));
        Assert.Empty(_graph.GetEdgesForEntry("src", Tenant));
        AssertHalvesAgree(_graph);
    }

    [Fact]
    public void RemoveEdges_TwinPlantedAfterValidation_CannotLandBeforeTheMutation()
    {
        Seed("src", MainNs);
        Seed("dst", MainNs);
        Link("src", "dst");

        string reply = "";
        var outcome = Race(
            h => _graph.OnValidatedUnderFence = h,
            () => reply = _graph.RemoveEdges("src", "dst", "supports", Tenant),
            () => Seed("src", ShadowNs));

        AssertCrossingWasFenced(outcome);

        // The removal ran to completion against attribution that was still valid, so it removed.
        Assert.Contains("Removed", reply);
        Assert.Empty(_graph.GetStoredEdgesForEntry("src", Tenant));
        AssertHalvesAgree(_graph);
    }

    [Fact]
    public void RemoveAllEdgesForEntry_TwinPlantedAfterValidation_CannotLandBeforeTheMutation()
    {
        Seed("anchor", MainNs);
        Seed("far", MainNs);
        Link("anchor", "far", "elaborates");

        int removed = 0;
        var outcome = Race(
            h => _graph.OnValidatedUnderFence = h,
            () => removed = _graph.RemoveAllEdgesForEntry("anchor", Tenant),
            () => Seed("anchor", ShadowNs));

        AssertCrossingWasFenced(outcome);
        Assert.Equal(1, removed);
        Assert.Empty(_graph.GetStoredEdgesForEntry("anchor", Tenant));
        Assert.Empty(_graph.GetStoredEdgesForEntry("far", Tenant));
        AssertHalvesAgree(_graph);
    }

    [Fact]
    public void TransferEdges_TwinPlantedAfterValidation_CannotLandBeforeTheMutation()
    {
        Seed("from", MainNs);
        Seed("to", MainNs);
        Seed("far", MainNs);
        Link("from", "far", "elaborates");

        int transferred = 0;
        var outcome = Race(
            h => _graph.OnValidatedUnderFence = h,
            () => transferred = _graph.TransferEdges("from", "to", Tenant),
            () => Seed("from", ShadowNs));

        AssertCrossingWasFenced(outcome);
        Assert.Equal(1, transferred);
        Assert.Empty(_graph.GetStoredEdgesForEntry("from", Tenant));
        Assert.Single(_graph.GetStoredEdgesForEntry("to", Tenant));
        AssertHalvesAgree(_graph);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 2. THE CLUSTER MUTATORS — the same window, the same fence
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CreateCluster_TwinPlantedAfterValidation_CannotLandBeforeMembershipIsPublished()
    {
        Seed("m1", MainNs);
        Seed("m2", MainNs);

        string reply = "";
        var outcome = Race(
            h => _clusterManager.OnValidatedUnderFence = h,
            () => reply = _clusterManager.CreateCluster("c1", MainNs, new[] { "m1", "m2" }, "label", Tenant),
            () => Seed("m1", ShadowNs));

        AssertCrossingWasFenced(outcome);

        // The membership published is the one admitted while both ids were attributable — which is
        // the reviewer's case: a deterministic IReadOnlyList planted a same-id twin after admission
        // and CreateCluster persisted the now-shared bare-id membership anyway.
        Assert.Contains("2 members", reply);

        // Consistent afterwards: the stored membership still names m1 (ground truth), while every
        // projection withholds it now that it is ambiguous.
        Assert.Contains("c1", _clusterManager.GetClustersForEntry("m1", Tenant));
        Assert.Equal(1, _clusterManager.GetCluster("c1", Tenant)!.MemberCount);
    }

    [Fact]
    public void UpdateCluster_TwinPlantedAfterValidation_CannotLandBeforeMembershipIsPublished()
    {
        Seed("m1", MainNs);
        Seed("m2", MainNs);
        Assert.Contains("Created", _clusterManager.CreateCluster("c1", MainNs, new[] { "m1" }, "label", Tenant));

        string reply = "";
        var outcome = Race(
            h => _clusterManager.OnValidatedUnderFence = h,
            () => reply = _clusterManager.UpdateCluster("c1", new[] { "m2" }, null, null, Tenant),
            () => Seed("m2", ShadowNs));

        AssertCrossingWasFenced(outcome);
        Assert.Contains("Updated", reply);
        Assert.Contains("c1", _clusterManager.GetClustersForEntry("m2", Tenant));
    }

    [Fact]
    public void RemoveEntryFromAllClusters_TwinPlantedAfterValidation_CannotLandBeforeTheEviction()
    {
        Seed("m1", MainNs);
        Seed("m2", MainNs);
        Assert.Contains("Created", _clusterManager.CreateCluster("c1", MainNs, new[] { "m1", "m2" }, "label", Tenant));

        var outcome = Race(
            h => _clusterManager.OnValidatedUnderFence = h,
            () => _clusterManager.RemoveEntryFromAllClusters("m1", Tenant),
            () => Seed("m1", ShadowNs));

        AssertCrossingWasFenced(outcome);

        // The eviction ran against attribution that was still valid, so it evicted; had the twin
        // landed first it would have evicted the invisible twin's membership too.
        Assert.Empty(_clusterManager.GetClustersForEntry("m1", Tenant));
        Assert.Contains("c1", _clusterManager.GetClustersForEntry("m2", Tenant));
    }

    [Fact]
    public void TransferMembership_TwinPlantedAfterValidation_CannotLandBeforeTheRewire()
    {
        Seed("m1", MainNs);
        Seed("m2", MainNs);
        Assert.Contains("Created", _clusterManager.CreateCluster("c1", MainNs, new[] { "m1" }, "label", Tenant));

        int affected = 0;
        var outcome = Race(
            h => _clusterManager.OnValidatedUnderFence = h,
            () => affected = _clusterManager.TransferMembership("m1", "m2", Tenant),
            () => Seed("m2", ShadowNs));

        AssertCrossingWasFenced(outcome);
        Assert.Equal(1, affected);
        Assert.Empty(_clusterManager.GetClustersForEntry("m1", Tenant));
        Assert.Contains("c1", _clusterManager.GetClustersForEntry("m2", Tenant));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 3. OVER-CORRECTION CONTROLS — the fence excludes crossings, not writes
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A first placement (0 -> 1) changes no id's attributability, so it must run straight through a
    /// held fence. If it did not, every entry write in a tenant would serialize behind every graph
    /// write in it — a throughput regression disguised as a security fix.
    /// </summary>
    [Fact]
    public void FirstPlacementOfAnUnrelatedId_IsNotSerializedByTheFence()
    {
        Seed("src", MainNs);
        Seed("dst", MainNs);

        var outcome = Race(
            h => _graph.OnValidatedUnderFence = h,
            () => Assert.True(_graph.TryAddEdge(Edge("src", "dst"), out _)),
            () => Seed("unrelated", ShadowNs));

        AssertWriteWasNotFenced(outcome);
        Assert.NotNull(_index.GetForTenant("unrelated", Tenant));
    }

    /// <summary>
    /// Re-upserting an id into a namespace it already occupies is the hot path — the documented
    /// upsert-by-id workflow — and crosses nothing. It must not be fenced either.
    /// </summary>
    [Fact]
    public void RePlacementIntoTheSameNamespace_IsNotSerializedByTheFence()
    {
        Seed("src", MainNs);
        Seed("dst", MainNs);

        var outcome = Race(
            h => _graph.OnValidatedUnderFence = h,
            () => Assert.True(_graph.TryAddEdge(Edge("src", "dst"), out _)),
            () => Seed("dst", MainNs));

        AssertWriteWasNotFenced(outcome);
    }

    /// <summary>
    /// With nothing racing at all, every fenced mutator still does its work. A fence that refused
    /// everything would satisfy every safety assertion in this file and be useless.
    /// </summary>
    [Fact]
    public void WithNothingRacing_EveryFencedMutatorStillWrites()
    {
        Seed("a", MainNs);
        Seed("b", MainNs);
        Seed("c", MainNs);
        Seed("d", MainNs);

        // Graph: all five.
        Assert.True(_graph.TryAddEdge(Edge("a", "b"), out _));
        Assert.Equal(1, _graph.AddEdges(new[] { Edge("a", "c") }));
        Assert.Contains("Removed", _graph.RemoveEdges("a", "c", "supports", Tenant));
        Assert.Equal(1, _graph.RemoveAllEdgesForEntry("a", Tenant));

        Link("c", "d", "elaborates");
        Assert.Equal(1, _graph.TransferEdges("c", "b", Tenant));
        AssertHalvesAgree(_graph);

        // Clusters: all four.
        Assert.Contains("2 members", _clusterManager.CreateCluster("k", MainNs, new[] { "a", "b" }, "l", Tenant));
        Assert.Contains("Updated", _clusterManager.UpdateCluster("k", new[] { "c" }, null, null, Tenant));
        Assert.Equal(1, _clusterManager.TransferMembership("c", "d", Tenant));
        _clusterManager.RemoveEntryFromAllClusters("d", Tenant);
        Assert.Empty(_clusterManager.GetClustersForEntry("d", Tenant));
        Assert.Contains("k", _clusterManager.GetClustersForEntry("a", Tenant));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 4. THE SELF-ONLY TRANSFER — a durability bug, not a counting one
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// When the only edge is <c>from -&gt; to</c>, the transfer removes both adjacency halves and
    /// rewires nothing, so the returned count is 0 — correctly, because nothing moved onto
    /// <c>to</c>. Gating persistence on that count left the in-memory graph and the store
    /// disagreeing: no revision bump, no scheduled save, and a restart resurrecting an edge the
    /// caller had watched disappear.
    ///
    /// Three assertions because three things were wrong and any one of them can regress alone: the
    /// revision moved, a save was actually scheduled, and a reload over what that save persisted
    /// does not bring the edge back.
    /// </summary>
    [Fact]
    public void TransferEdges_SelfOnlyResult_BumpsTheRevisionAndPersistsTheDeletion()
    {
        var store = new EdgeCapturingProvider(_persistence);
        var graph = new KnowledgeGraph(store, _index);

        Seed("from", MainNs);
        Seed("to", MainNs);
        Assert.True(graph.TryAddEdge(Edge("from", "to", "elaborates"), out _));

        // The fixture edge's own save is not the one under test.
        store.Commit();
        Assert.Single(store.PersistedEdges);

        long globalBefore = graph.Revision;
        long tenantBefore = graph.RevisionFor(Tenant);
        int savesBefore = store.ScheduledSaveCount;

        // The whole point: zero transferred, and topology changed anyway.
        Assert.Equal(0, graph.TransferEdges("from", "to", Tenant));

        Assert.Empty(graph.GetStoredEdgesForEntry("from", Tenant));
        Assert.Empty(graph.GetStoredEdgesForEntry("to", Tenant));
        AssertHalvesAgree(graph);

        Assert.True(graph.Revision > globalBefore,
            "topology changed but the global graph revision did not move, so every derived cache " +
            "keeps serving the deleted edge");
        Assert.True(graph.RevisionFor(Tenant) > tenantBefore,
            "topology changed but the tenant's graph revision did not move");
        Assert.True(store.ScheduledSaveCount > savesBefore,
            "topology changed but no save was scheduled — the deletion never reaches the store");

        // Durability, end to end: run the debounce the real provider would have run, then reload.
        store.Commit();
        Assert.Empty(store.PersistedEdges);

        var reloaded = new KnowledgeGraph(store, _index);
        Assert.Empty(reloaded.GetStoredEdgesForEntry("from", Tenant));
        Assert.Empty(reloaded.GetStoredEdgesForEntry("to", Tenant));
        Assert.Empty(reloaded.GetStoredEdges(Tenant));
    }

    /// <summary>
    /// The mirror case: the only edge runs <c>to -&gt; from</c>, so the incoming branch is the one
    /// that removes without transferring. Both branches gate persistence on the same flag and both
    /// have to, or the bug simply moves to the other direction.
    /// </summary>
    [Fact]
    public void TransferEdges_SelfOnlyIncomingResult_BumpsTheRevisionAndPersistsTheDeletion()
    {
        var store = new EdgeCapturingProvider(_persistence);
        var graph = new KnowledgeGraph(store, _index);

        Seed("from", MainNs);
        Seed("to", MainNs);
        Assert.True(graph.TryAddEdge(Edge("to", "from", "elaborates"), out _));
        store.Commit();
        Assert.Single(store.PersistedEdges);

        long tenantBefore = graph.RevisionFor(Tenant);
        int savesBefore = store.ScheduledSaveCount;

        Assert.Equal(0, graph.TransferEdges("from", "to", Tenant));

        Assert.True(graph.RevisionFor(Tenant) > tenantBefore);
        Assert.True(store.ScheduledSaveCount > savesBefore);
        AssertHalvesAgree(graph);

        store.Commit();
        Assert.Empty(store.PersistedEdges);
        Assert.Empty(new KnowledgeGraph(store, _index).GetStoredEdges(Tenant));
    }

    /// <summary>
    /// The control for the two above: a transfer that genuinely moves nothing — no incident edges at
    /// all — must NOT bump the revision or schedule a save. The fix is "persist when topology
    /// changed", not "persist always".
    /// </summary>
    [Fact]
    public void TransferEdges_WithNoIncidentEdges_ChangesNothingAndSchedulesNothing()
    {
        var store = new EdgeCapturingProvider(_persistence);
        var graph = new KnowledgeGraph(store, _index);

        Seed("from", MainNs);
        Seed("to", MainNs);
        Seed("other", MainNs);
        Assert.True(graph.TryAddEdge(Edge("other", "to", "elaborates"), out _));
        store.Commit();

        long tenantBefore = graph.RevisionFor(Tenant);
        int savesBefore = store.ScheduledSaveCount;

        Assert.Equal(0, graph.TransferEdges("from", "to", Tenant));

        Assert.Equal(tenantBefore, graph.RevisionFor(Tenant));
        Assert.Equal(savesBefore, store.ScheduledSaveCount);
        Assert.Single(graph.GetStoredEdges(Tenant));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 5. SYNCHRONOUS STORAGE PROVIDERS
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>IStorageProvider</c> permits an implementation to invoke the save provider synchronously —
    /// the shipped ones debounce, but the interface promises nothing and a deterministic test
    /// provider that calls it inline is a legitimate implementation.
    ///
    /// The graph hands over a METHOD GROUP that takes the graph's read lock when it runs. Scheduled
    /// from inside the write lock, that is a read lock requested by the thread already holding the
    /// write lock, and <c>ReaderWriterLockSlim</c> is non-recursive here: every mutation threw
    /// <c>LockRecursionException</c>. Scheduling after the release is the fix, and this exercises
    /// every mutator on both classes because the defect was in the call site, not in any one of
    /// them.
    /// </summary>
    [Fact]
    public void EveryMutator_WithASynchronousStorageProvider_DoesNotThrow()
    {
        var store = new EdgeCapturingProvider(_persistence, synchronous: true);
        var graph = new KnowledgeGraph(store, _index);
        var clusters = new ClusterManager(_index, store);

        Seed("a", MainNs);
        Seed("b", MainNs);
        Seed("c", MainNs);
        Seed("d", MainNs);

        Assert.True(graph.TryAddEdge(Edge("a", "b"), out _));
        Assert.Equal(1, graph.AddEdges(new[] { Edge("a", "c") }));
        Assert.Contains("Removed", graph.RemoveEdges("a", "c", "supports", Tenant));
        Assert.Equal(1, graph.RemoveAllEdgesForEntry("a", Tenant));

        Assert.True(graph.TryAddEdge(Edge("c", "d", "elaborates"), out _));
        Assert.Equal(1, graph.TransferEdges("c", "b", Tenant));
        AssertHalvesAgree(graph);

        Assert.Contains("2 members", clusters.CreateCluster("k", MainNs, new[] { "a", "b" }, "l", Tenant));
        Assert.Contains("Updated", clusters.UpdateCluster("k", new[] { "c" }, null, null, Tenant));
        Assert.Equal(1, clusters.TransferMembership("c", "d", Tenant));
        clusters.RemoveEntryFromAllClusters("d", Tenant);
        Assert.StartsWith("summary:", clusters.StoreSummary("k", "text", [0.1f, 0.2f], Tenant));

        // The synchronous provider really did run inline rather than being quietly ignored.
        Assert.True(store.SynchronousInvocations > 0);
        Assert.True(store.SynchronousClusterInvocations > 0);
    }

    /// <summary>
    /// The graph and cluster halves of a cascade are separate fenced transactions. A crossing that
    /// lands from the graph's synchronous save callback is after graph mutation and before cluster
    /// admission; the cluster half must capture a fresh sweep rather than silently skip cleanup.
    /// </summary>
    [Fact]
    public void CascadeUsesAFreshSweepForClusterRemovalAfterAnInterveningCrossing()
    {
        var store = new EdgeCapturingProvider(_persistence, synchronous: true);
        var graph = new KnowledgeGraph(store, _index);
        var clusters = new ClusterManager(_index, store);

        Seed("a", MainNs);
        Seed("b", MainNs);
        Seed("unrelated", MainNs);
        Assert.True(graph.TryAddEdge(Edge("a", "b"), out _));
        Assert.Contains("Created",
            clusters.CreateCluster("k", MainNs, new[] { "a", "b" }, "l", Tenant));

        store.BeforeScheduleGlobalEdges = () =>
        {
            store.BeforeScheduleGlobalEdges = null;
            Seed("unrelated", ShadowNs);
        };

        TopologyCascade.CascadeAll(_index, graph, clusters, new[] { "a" }, Tenant, apply: true);

        Assert.Empty(graph.GetStoredEdgesForEntry("a", Tenant));
        Assert.Empty(clusters.GetClustersForEntry("a", Tenant));
    }
}

/// <summary>
/// A storage provider that owns the edge blob so a test can observe what a save would actually
/// persist, and reload a fresh graph over it.
///
/// Two modes, and both are legitimate <c>IStorageProvider</c> implementations:
///  - DEBOUNCED (default): the scheduled provider is held and invoked only when the test calls
///    <see cref="Commit"/>. That is the shipped behaviour, made deterministic — no timer, no
///    thread, no waiting.
///  - SYNCHRONOUS: the scheduled provider is invoked INLINE at the call site. The interface allows
///    it and nothing in the contract forbids it, which is why the graph's deferred snapshot
///    callback must not be scheduled from inside the graph's own write lock.
///
/// Everything that is not an edge or cluster save is delegated to the fixture's real provider, so
/// namespaces load and persist exactly as they normally would.
/// </summary>
file sealed class EdgeCapturingProvider : IStorageProvider
{
    private readonly IStorageProvider _inner;
    private readonly bool _synchronous;
    private List<GraphEdge> _persistedEdges = new();
    private List<SemanticCluster> _persistedClusters = new();
    private Func<List<GraphEdge>>? _pendingEdges;

    public EdgeCapturingProvider(IStorageProvider inner, bool synchronous = false)
    {
        _inner = inner;
        _synchronous = synchronous;
    }

    /// <summary>How many times an edge save was scheduled.</summary>
    public int ScheduledSaveCount { get; private set; }

    /// <summary>How many edge-save providers were invoked inline (synchronous mode only).</summary>
    public int SynchronousInvocations { get; private set; }

    /// <summary>How many cluster-save providers were invoked inline (synchronous mode only).</summary>
    public int SynchronousClusterInvocations { get; private set; }

    /// <summary>Test-only callback invoked outside the graph lock, immediately before scheduling.</summary>
    public Action? BeforeScheduleGlobalEdges { get; set; }

    /// <summary>What a reload would see.</summary>
    public IReadOnlyList<GraphEdge> PersistedEdges => _persistedEdges;

    /// <summary>Run the debounce the shipped providers would have run, deterministically.</summary>
    public void Commit()
    {
        var pending = _pendingEdges;
        _pendingEdges = null;
        if (pending is not null)
            _persistedEdges = pending();
    }

    // A fresh list per call: the graph takes ownership of what it is handed.
    public List<GraphEdge> LoadGlobalEdges() => new(_persistedEdges);

    public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider)
    {
        BeforeScheduleGlobalEdges?.Invoke();
        ScheduledSaveCount++;
        if (_synchronous)
        {
            SynchronousInvocations++;
            _persistedEdges = dataProvider();
            return;
        }
        _pendingEdges = dataProvider;
    }

    public List<SemanticCluster> LoadClusters() => new(_persistedClusters);

    public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider)
    {
        if (_synchronous)
        {
            SynchronousClusterInvocations++;
            _persistedClusters = dataProvider();
        }
    }

    public NamespaceData LoadNamespace(string ns) => _inner.LoadNamespace(ns);
    public IReadOnlyList<string> GetPersistedNamespaces() => _inner.GetPersistedNamespaces();
    public void ScheduleSave(string ns, Func<NamespaceData> dataProvider) => _inner.ScheduleSave(ns, dataProvider);
    public void SaveNamespaceSync(string ns, NamespaceData data) => _inner.SaveNamespaceSync(ns, data);
    public bool SupportsIncrementalWrites => _inner.SupportsIncrementalWrites;
    public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) => _inner.ScheduleUpsertEntry(ns, entry);
    public void ScheduleDeleteEntry(string ns, string entryId) => _inner.ScheduleDeleteEntry(ns, entryId);
    public void ScheduleDeleteEntry(string ns, string entryId, string tenantId) => _inner.ScheduleDeleteEntry(ns, entryId, tenantId);
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
