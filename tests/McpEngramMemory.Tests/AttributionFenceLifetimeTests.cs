using System.Diagnostics;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// THE FENCE'S LIFETIME AND ITS HOLD LENGTH — the two properties
/// <see cref="AttributionFenceTests"/> could not reach, because that suite only ever asks whether a
/// crossing is EXCLUDED from the window. It rendezvouses on a waiting writer and then lets the
/// mutator finish, so nothing in it observes what happens when the fence a holder is standing on
/// is torn down underneath it, what the blocked crossing is still holding while it waits, or how
/// long the holder intends to stand there.
///
/// THREE SHAPES, all of them deterministic and none of them timed:
///
///  1. TEARDOWN UNDER A HOLDER. <see cref="CognitiveIndex.Dispose"/> runs while a mutator holds the
///     fence and a crossing is parked on its exclusive side. The release must land on the instance
///     that was entered, the parked crossing must wake, and the contended fence must still be
///     published — because it is the object both of those threads are standing on.
///
///  2. THE MULTI-TENANT FENCE SET. <see cref="KnowledgeGraph.AddEdges"/> is the one mutator that can
///     hold several fences at once, and no test drove it with more than one. Both must be acquired
///     and both released, or the tenant whose release was skipped becomes permanently unwritable.
///
///  3. THE BOUNDED HOLD. A batch larger than <see cref="KnowledgeGraph.AddEdgesFenceChunk"/> must
///     release the fence between chunks, and the only way to state that from outside is to park a
///     crossing during the first chunk and observe it complete before the batch does.
///
/// THE RENDEZVOUS IS ALWAYS A CONDITION, NEVER A DELAY. Every wait below ends the instant an
/// observable state holds — a waiting writer appears, an event is set — and a budget that expires
/// without it is a failure with a message rather than a quiet pass.
/// </summary>
public sealed class AttributionFenceLifetimeTests : IDisposable
{
    private const string Tenant = "acme";
    private const string OtherTenant = "globex";
    private const string MainNs = "main";
    private const string ShadowNs = "shadow";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;

    public AttributionFenceLifetimeTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"fence_lifetime_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
    }

    public void Dispose()
    {
        _graph.OnValidatedUnderFence = null;
        try { _index.Dispose(); } catch { /* a test may already have disposed it */ }
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    private void Seed(string id, string ns, string tenantId = Tenant)
        => _index.Upsert(new CognitiveEntry(id, [0.5f, 0.5f], ns, $"entry '{id}' in {ns}", tenantId: tenantId));

    private static GraphEdge Edge(string src, string dst, string tenantId = Tenant, string relation = "supports")
        => new(src, dst, relation, 0.9f, null, tenantId);

    /// <summary>Spin until <paramref name="condition"/> holds, or fail with <paramref name="what"/>.</summary>
    private static void Until(Func<bool> condition, string what)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < Budget)
        {
            if (condition()) return;
            Thread.Yield();
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 1. TEARDOWN WHILE THE FENCE IS HELD
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The index is disposed while a mutator holds the fence's shared side AND a crossing is parked
    /// on its exclusive side. Three things must hold afterwards, and all three were broken.
    ///
    /// THE RELEASE MUST LAND ON THE FENCE THAT WAS ENTERED. Release used to re-resolve the fence by
    /// tenant through a <c>GetOrAdd</c>, against a dictionary teardown had just cleared — so it
    /// minted a brand-new lock and called <c>ExitReadLock</c> on it, throwing
    /// <see cref="SynchronizationLockException"/> out of the mutator's finally block and replacing
    /// its return value with an exception.
    ///
    /// THE PARKED CROSSING MUST WAKE. It is parked on the ORIGINAL fence, whose reader count that
    /// bogus release never decremented — so it slept forever, holding the partition write lock its
    /// upsert had taken, which makes that namespace permanently unreadable and unwritable and hangs
    /// the host's shutdown.
    ///
    /// THE CONTENDED FENCE MUST STILL BE PUBLISHED. It is the object both threads are standing on;
    /// unpublishing it is what made the release resolve somewhere else in the first place.
    /// </summary>
    [Fact]
    public void Dispose_WhileTheFenceIsHeldAndACrossingIsParked_ReleasesTheFenceThatWasEnteredAndWakesTheCrossing()
    {
        Seed("src", MainNs);
        Seed("dst", MainNs);

        var crossingDone = new ManualResetEventSlim(false);
        Exception? crossingFailure = null;
        Exception? disposeFailure = null;
        bool windowEntered = false;

        var crossing = new Thread(() =>
        {
            // A 1 -> 2 crossing: "src" already lives in MainNs, so placing it in ShadowNs takes the
            // fence's EXCLUSIVE side — while holding ShadowNs's partition write lock.
            try { Seed("src", ShadowNs); }
            catch (Exception ex) { crossingFailure = ex; }
            finally { crossingDone.Set(); }
        })
        { IsBackground = true, Name = "parked-crossing" };

        _graph.OnValidatedUnderFence = () =>
        {
            windowEntered = true;
            crossing.Start();

            // Park it, observably, before tearing anything down. This is the state the test is about:
            // one reader, one waiting writer, and then a Dispose.
            Until(() => _index.AttributionFenceWaitingWriters(Tenant) > 0,
                "the crossing to park on the fence's exclusive side");

            // Teardown under both of them. Dispose must not unpublish the fence they are standing on.
            disposeFailure = Record.Exception(() => _index.Dispose());
        };

        // The mutator's own release runs in its finally, after the hook returns.
        var mutation = Record.Exception(() => _graph.TryAddEdge(Edge("src", "dst"), out _));

        Assert.True(windowEntered, "the mutator never reached its validated-under-fence seam");
        Assert.Null(disposeFailure);
        Assert.Null(mutation);

        Assert.True(crossingDone.Wait(Budget),
            "the crossing parked on the fence's exclusive side never woke — the holder's release " +
            "landed on a different lock, so the fence it really held kept its reader forever");
        Assert.Null(crossingFailure);
        crossing.Join(Budget);

        // The contended fence was left in place rather than cleared out from under its holder, and
        // the disposal says so rather than discarding the figure.
        Assert.Equal(1, _index.DisposalContendedFenceCount);
        Assert.Equal(1, _index.AttributionFenceCount);
    }

    /// <summary>
    /// The same teardown with NO waiting writer — the simpler half, and the one that fired at every
    /// mutator site rather than only under contention. The holder's release must still not throw.
    /// </summary>
    [Fact]
    public void Dispose_WhileTheFenceIsHeldWithNoWaiter_DoesNotThrowOutOfTheHoldersFinally()
    {
        Seed("src", MainNs);
        Seed("dst", MainNs);

        bool windowEntered = false;
        _graph.OnValidatedUnderFence = () =>
        {
            windowEntered = true;
            _index.Dispose();
        };

        var mutation = Record.Exception(() => _graph.TryAddEdge(Edge("src", "dst"), out _));

        Assert.True(windowEntered);
        Assert.Null(mutation);
        Assert.Equal(1, _index.DisposalContendedFenceCount);
    }

    /// <summary>
    /// After teardown, a fence must not be MINTED. An unguarded accessor handed a late crossing the
    /// exclusive side of a brand-new lock — exclusion against nobody — and republished it into a
    /// dictionary nothing would ever walk again.
    /// </summary>
    [Fact]
    public void AfterDispose_TakingTheFenceIsRefusedRatherThanMintingANewOne()
    {
        Seed("src", MainNs);
        _index.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _index.EnterAttributionFence(Tenant));

        // Nothing was published by the refused attempt.
        Assert.Equal(0, _index.AttributionFenceCount);

        // And the diagnostic read does not mint one either — a probe that publishes a lock is how
        // the dictionary refills after the walk that was supposed to empty it.
        Assert.Equal(0, _index.AttributionFenceWaitingWriters(Tenant));
        Assert.Equal(0, _index.AttributionFenceCount);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 2. THE MULTI-TENANT FENCE SET
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A batch spanning two tenants takes both fences and RELEASES BOTH.
    ///
    /// No test drove <see cref="KnowledgeGraph.AddEdges"/> with more than one tenant at all, so
    /// neither the ordered acquisition nor the reverse release had ever run with a set to get wrong.
    /// The release loop was an unguarded <c>for</c>: a throw on one iteration abandoned every fence
    /// below it, held, for the life of the process — after which every ambiguity crossing in those
    /// tenants parks forever while holding a partition write lock.
    ///
    /// "Both were released" is stated by CROSSING IN EACH TENANT afterwards. A crossing takes the
    /// exclusive side, so it completes only if no reader is left standing on that fence; an orphaned
    /// fence turns this into a hang rather than a failed assertion, which is why each wait has a
    /// budget.
    /// </summary>
    [Fact]
    public void AddEdges_SpanningTwoTenants_TakesBothFencesAndReleasesBoth()
    {
        Seed("a", MainNs);
        Seed("b", MainNs);
        Seed("x", MainNs, OtherTenant);
        Seed("y", MainNs, OtherTenant);

        int written = _graph.AddEdges(new[]
        {
            Edge("a", "b"),
            Edge("x", "y", OtherTenant),
        });

        Assert.Equal(2, written);
        Assert.Single(_graph.GetStoredEdges(Tenant));
        Assert.Single(_graph.GetStoredEdges(OtherTenant));

        // Both fences are free: a crossing in each tenant completes without parking.
        var acmeCrossed = new ManualResetEventSlim(false);
        var globexCrossed = new ManualResetEventSlim(false);

        var t1 = new Thread(() => { Seed("a", ShadowNs); acmeCrossed.Set(); }) { IsBackground = true };
        var t2 = new Thread(() => { Seed("x", ShadowNs, OtherTenant); globexCrossed.Set(); }) { IsBackground = true };
        t1.Start();
        t2.Start();

        Assert.True(acmeCrossed.Wait(Budget),
            $"a crossing in '{Tenant}' never completed — that tenant's fence was left held by the batch");
        Assert.True(globexCrossed.Wait(Budget),
            $"a crossing in '{OtherTenant}' never completed — that tenant's fence was left held by the batch");
        t1.Join(Budget);
        t2.Join(Budget);

        Assert.Equal(0, _index.AttributionFenceWaitingWriters(Tenant));
        Assert.Equal(0, _index.AttributionFenceWaitingWriters(OtherTenant));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 3. THE BOUNDED HOLD
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A batch larger than one chunk RELEASES THE FENCE between chunks, and the crossing that was
    /// parked against it gets through.
    ///
    /// WHY THE HOLD LENGTH IS A CORRECTNESS-ADJACENT PROPERTY AND NOT A BENCHMARK. The fence's
    /// exclusive side is taken by a crossing that is ALREADY holding a partition write lock, and
    /// <see cref="ReaderWriterLockSlim"/> prefers writers — so for as long as a shared holder stands
    /// there, every later reader and writer of that partition queues behind the blocked crossing.
    /// One background auto-link batch could therefore freeze all traffic in a namespace it never
    /// named, for the length of a caller-chosen batch. Nothing in the previous suite measured what
    /// the blocked writer was still holding while it waited.
    ///
    /// THE OBSERVATION IS EXACT, not a timing. The crossing is parked during chunk one. If the fence
    /// spans the whole batch it cannot land until every edge is written, so all
    /// <see cref="EdgeCount"/> edges appear. If the fence is released at the chunk boundary the
    /// crossing lands there, the next chunk's attribution compare trips, and the batch stops with
    /// exactly one chunk written. The returned count distinguishes the two worlds with no clock
    /// involved.
    /// </summary>
    [Fact]
    public void AddEdges_LargerThanOneChunk_ReleasesTheFenceBetweenChunksSoAParkedCrossingLands()
    {
        const int NodeCount = 40;
        for (int i = 0; i < NodeCount; i++)
            Seed($"n{i}", MainNs);

        var batch = new List<GraphEdge>();
        for (int i = 0; i < NodeCount && batch.Count < KnowledgeGraph.AddEdgesFenceChunk * 2 + 5; i++)
            for (int j = 0; j < NodeCount && batch.Count < KnowledgeGraph.AddEdgesFenceChunk * 2 + 5; j++)
                if (i != j) batch.Add(Edge($"n{i}", $"n{j}"));

        Assert.True(batch.Count > KnowledgeGraph.AddEdgesFenceChunk,
            "the batch must span more than one chunk or this test proves nothing");

        var crossingDone = new ManualResetEventSlim(false);
        var crossing = new Thread(() => { Seed("n0", ShadowNs); crossingDone.Set(); })
        { IsBackground = true, Name = "chunk-boundary-crossing" };

        int seamInvocations = 0;
        _graph.OnValidatedUnderFence = () =>
        {
            seamInvocations++;
            if (seamInvocations > 1) return;

            crossing.Start();
            Until(() => _index.AttributionFenceWaitingWriters(Tenant) > 0,
                "the crossing to park on the fence's exclusive side during the first chunk");
        };

        int written = _graph.AddEdges(batch);

        Assert.True(crossingDone.Wait(Budget), "the parked crossing never completed");
        crossing.Join(Budget);

        Assert.Equal(1, seamInvocations);
        Assert.Equal(KnowledgeGraph.AddEdgesFenceChunk, written);
        Assert.Equal(KnowledgeGraph.AddEdgesFenceChunk, _graph.GetStoredEdges(Tenant).Count);
        Assert.Empty(_graph.FindAdjacencyMirrorViolations());
    }

    /// <summary>
    /// The control for the test above: with nothing racing, a multi-chunk batch writes EVERY edge.
    /// Chunking must bound the hold, not the work — a version that quietly dropped the tail would
    /// satisfy the assertion above and be useless.
    /// </summary>
    [Fact]
    public void AddEdges_LargerThanOneChunk_WithNothingRacing_WritesEveryEdge()
    {
        const int NodeCount = 40;
        for (int i = 0; i < NodeCount; i++)
            Seed($"n{i}", MainNs);

        var batch = new List<GraphEdge>();
        for (int i = 0; i < NodeCount && batch.Count < KnowledgeGraph.AddEdgesFenceChunk + 7; i++)
            for (int j = 0; j < NodeCount && batch.Count < KnowledgeGraph.AddEdgesFenceChunk + 7; j++)
                if (i != j) batch.Add(Edge($"n{i}", $"n{j}"));

        int seamInvocations = 0;
        _graph.OnValidatedUnderFence = () => seamInvocations++;

        int written = _graph.AddEdges(batch);

        Assert.Equal(batch.Count, written);
        Assert.Equal(batch.Count, _graph.GetStoredEdges(Tenant).Count);
        Assert.Equal(2, seamInvocations);
        Assert.Empty(_graph.FindAdjacencyMirrorViolations());
    }

    /// <summary>
    /// A batch that fits in one chunk still takes the fence exactly once, so the shape above did not
    /// quietly multiply the acquisitions every ordinary caller pays for.
    /// </summary>
    [Fact]
    public void AddEdges_WithinOneChunk_TakesTheFenceOnce()
    {
        Seed("a", MainNs);
        Seed("b", MainNs);
        Seed("c", MainNs);

        int seamInvocations = 0;
        _graph.OnValidatedUnderFence = () => seamInvocations++;

        Assert.Equal(2, _graph.AddEdges(new[] { Edge("a", "b"), Edge("a", "c") }));
        Assert.Equal(1, seamInvocations);
    }

    /// <summary>
    /// An entirely declined batch still reaches the seam once, holding the fence. The invariant is
    /// "every mutator validates under the fence", and a path that wrote nothing by skipping the
    /// validation would be a rule with a hole in it even though it writes nothing.
    /// </summary>
    [Fact]
    public void AddEdges_WithEveryEdgeDeclined_StillValidatesUnderTheFenceOnce()
    {
        // "ghost" is ambiguous before the batch is built, so every edge naming it is declined.
        Seed("ghost", MainNs);
        Seed("ghost", ShadowNs);
        Seed("real", MainNs);

        int seamInvocations = 0;
        _graph.OnValidatedUnderFence = () => seamInvocations++;

        Assert.Equal(0, _graph.AddEdges(new[] { Edge("ghost", "real") }));
        Assert.Equal(1, seamInvocations);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 4. THE EXCLUSIVE SIDE IS TAKEN UNDER A PARTITION READ LOCK TOO
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A crossing reached from inside a LAZY LOAD, on the read path, parks on the fence exactly like
    /// one reached from an upsert.
    ///
    /// This is the nesting the fence remarks now state and no test exercised: <c>Search</c> holds the
    /// partition READ lock across <c>EnsureLoaded -> LoadEntries</c>, and <c>LoadEntries</c> TRACKS
    /// every row it materializes — so materializing a namespace whose rows make an id ambiguous
    /// takes the fence's exclusive side from under a read lock. It is the same asymmetry as the write
    /// path (fence outermost on its shared side, innermost on its exclusive side) reached through a
    /// method nobody thinks of as a writer.
    ///
    /// The store is built and flushed by one index, then re-opened by a second one that has loaded
    /// only <c>main</c>. Searching <c>shadow</c> is then the first thing to materialize it, and the
    /// twin it carries is the crossing.
    ///
    /// The fence is held DIRECTLY here rather than through a mutator, and it has to be: every
    /// mutator builds a <c>TopologyGuard.Sweep</c> first, whose constructor calls
    /// <c>EnsureAllNamespacesLoaded</c> — which would materialize <c>shadow</c> itself, perform the
    /// crossing before the fence was ever taken, and leave the reader with nothing to cross.
    /// </summary>
    [Fact]
    public void ALazyLoadThatMakesAnIdAmbiguous_ParksOnTheFenceFromUnderAPartitionReadLock()
    {
        // Build a store holding "twin" in two namespaces of one tenant, then flush it to disk.
        Seed("twin", MainNs);
        Seed("twin", ShadowNs);
        Seed("anchor", MainNs);
        _persistence.Flush();

        // A second index over the same store: nothing is resident until something asks.
        using var reopened = new CognitiveIndex(_persistence);

        // Materialize MainNs and nothing else, so "twin" is tracked in exactly one namespace.
        // CountInNamespace takes that one partition's read lock and loads that one namespace.
        Assert.Equal(2, reopened.CountInNamespace(MainNs, Tenant));
        Assert.Equal(0, reopened.AttributionRevisionFor(Tenant));

        var searchDone = new ManualResetEventSlim(false);
        Exception? searchFailure = null;
        bool parked = false;

        var reader = new Thread(() =>
        {
            try
            {
                // The read path: partition read lock -> EnsureLoaded -> LoadEntries -> TrackCandidate,
                // which crosses 1 -> 2 for "twin" and therefore asks for the fence's exclusive side
                // while the partition read lock is still held.
                reopened.Search([0.5f, 0.5f], ShadowNs, tenantId: Tenant, k: 5);
            }
            catch (Exception ex) { searchFailure = ex; }
            finally { searchDone.Set(); }
        })
        { IsBackground = true, Name = "lazy-loading-reader" };

        var fence = reopened.EnterAttributionFence(Tenant);
        try
        {
            reader.Start();

            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < Budget)
            {
                if (reopened.AttributionFenceWaitingWriters(Tenant) > 0) { parked = true; break; }
                if (searchDone.IsSet) break;
                Thread.Yield();
            }
        }
        finally { CognitiveIndex.ExitAttributionFence(fence); }

        Assert.True(parked,
            "a lazy load that makes an id ambiguous did not park on the fence — the read path " +
            "reaches TrackCandidate through LoadEntries and must take the exclusive side there too");
        Assert.True(searchDone.Wait(Budget), "the loading reader never completed after the fence was released");
        Assert.Null(searchFailure);
        reader.Join(Budget);

        // The crossing really happened, so the parking above was the real path and not an artefact.
        Assert.Equal(1, reopened.AttributionRevisionFor(Tenant));
    }
}
