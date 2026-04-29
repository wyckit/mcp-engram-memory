using System.Collections.Concurrent;
using System.Diagnostics;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// Tests that explicitly validate the per-namespace lock hierarchy on CognitiveIndex:
/// single-namespace operations serialize within a namespace but parallelize across
/// different namespaces; cross-namespace reads stay lock-free; random-op stress
/// completes without deadlock.
/// </summary>
public class LockHierarchyTests : IDisposable
{
    private readonly string _dataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly HashEmbeddingService _embedding;

    public LockHierarchyTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"lockhier_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_dataPath);
        _index = new CognitiveIndex(_persistence);
        _embedding = new HashEmbeddingService();
    }

    private CognitiveEntry MakeEntry(string id, string ns, string text, string state = "ltm")
        => new(id, _embedding.Embed(text), ns, text, lifecycleState: state);

    // ── 1. Cross-namespace write parallelism ──────────────────────────────────

    /// <summary>
    /// Two orthogonal signals in one test:
    ///
    /// 1. Events fire AFTER the per-ns write lock is released. We verify this by
    ///    parking the ns=A handler on a gate: if events fired INSIDE the lock,
    ///    ns=A's lock would still be held, but since events fire AFTER release
    ///    an ns=B writer is free to grab its own (independent) lock immediately.
    ///
    /// 2. Per-ns locks are independent. The ns=B writer proceeds while the ns=A
    ///    writer is parked post-lock-release; if they shared a lock, the ns=B
    ///    writer would block waiting for ns=A to finish (which cannot happen
    ///    until the gate opens). This only fully discriminates if callers ever
    ///    interleave writer + handler; in practice the stronger proof of lock
    ///    independence comes from ConcurrentWrites_SameNamespace_Serialize +
    ///    UpsertBatch_MixedNamespaces_ParallelizesByGroup + RandomOps_*.
    /// </summary>
    [Fact]
    public async Task Events_FireAfterLockRelease_DoNotBlockOtherNamespaceWrites()
    {
        using var gate = new ManualResetEventSlim(false);
        using var aReached = new ManualResetEventSlim(false);
        int stalledNs = 0;

        void Handler(object? sender, CognitiveEntry e)
        {
            if (e.Ns == "ns-a" && Interlocked.Exchange(ref stalledNs, 1) == 0)
            {
                aReached.Set();
                gate.Wait(TimeSpan.FromSeconds(5));
            }
        }

        _index.EntryUpserted += Handler;
        try
        {
            var aTask = Task.Run(() => _index.Upsert(MakeEntry("a-1", "ns-a", "A content")));
            // 10 s gives slack for a saturated CI ThreadPool (xunit runs many test classes in
            // parallel) where Task.Run scheduling alone can spike past 2 s under load. The
            // healthy path fires the handler in milliseconds, so this only relaxes the false-
            // negative bound, not the test's discriminative power.
            Assert.True(aReached.Wait(TimeSpan.FromSeconds(10)), "ns-a writer did not reach handler");

            // A is parked in its handler — its lock is already released. B must proceed.
            // Await the task after the wait so any exception thrown inside the Upsert
            // propagates instead of being stashed on the Task and silently lost.
            var bTask = Task.Run(() => _index.Upsert(MakeEntry("b-1", "ns-b", "B content")));
            Assert.True(
                ReferenceEquals(await Task.WhenAny(bTask, Task.Delay(TimeSpan.FromSeconds(2))), bTask),
                "ns-b writer blocked while ns-a was in its event handler — either events fire inside the lock, or per-ns locks are not independent");
            await bTask;

            gate.Set();
            await aTask;

            Assert.NotNull(_index.Get("a-1", "ns-a"));
            Assert.NotNull(_index.Get("b-1", "ns-b"));
        }
        finally
        {
            gate.Set();
            _index.EntryUpserted -= Handler;
        }
    }

    /// <summary>
    /// Writers to the SAME namespace serialize through that namespace's write lock.
    /// If writes raced and dropped updates, the final count would be &lt; writers.
    /// Also tracks how many event callbacks fire (one per accepted Upsert) — must
    /// equal the writer count with no dropped events.
    /// </summary>
    [Fact]
    public async Task ConcurrentWrites_SameNamespace_Serialize()
    {
        int eventCount = 0;
        void Handler(object? sender, CognitiveEntry e)
        {
            if (e.Ns == "serial-ns") Interlocked.Increment(ref eventCount);
        }

        _index.EntryUpserted += Handler;
        try
        {
            const int writers = 20;
            var tasks = Enumerable.Range(0, writers).Select(i => Task.Run(() =>
                _index.Upsert(MakeEntry($"s-{i:D2}", "serial-ns", $"line {i}")))).ToArray();
            await Task.WhenAll(tasks);

            // No lost updates, no dropped events — proof that writes serialized cleanly.
            Assert.Equal(writers, _index.CountInNamespace("serial-ns"));
            Assert.Equal(writers, eventCount);
            for (int i = 0; i < writers; i++)
                Assert.NotNull(_index.Get($"s-{i:D2}", "serial-ns"));
        }
        finally
        {
            _index.EntryUpserted -= Handler;
        }
    }

    /// <summary>
    /// A reader on ns=B must not block on a writer active on ns=A.
    /// We start a long-running read loop on B, then do 50 writes to A. The read loop
    /// must continue making forward progress throughout — we count iterations per
    /// 100ms and require non-trivial throughput during the write burst.
    /// </summary>
    [Fact]
    public async Task Reader_OnDifferentNamespace_NotBlockedByWriter()
    {
        _index.Upsert(MakeEntry("seed", "reader-ns", "seed entry"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        long readerIterations = 0;
        var readerTask = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var r = _index.Get("seed", "reader-ns");
                Assert.NotNull(r);
                Interlocked.Increment(ref readerIterations);
            }
        });

        var writerTask = Task.Run(() =>
        {
            for (int i = 0; i < 50 && !cts.IsCancellationRequested; i++)
                _index.Upsert(MakeEntry($"w-{i:D2}", "writer-ns", $"write {i}"));
        });

        await Task.WhenAll(readerTask, writerTask);

        // If the writer had held a global write lock the reader would have been blocked
        // during each write; with per-ns locks the reader sees thousands of iterations
        // easily. Even on a slow CI we expect > 100.
        Assert.True(readerIterations > 100,
            $"Reader only completed {readerIterations} iterations — looks blocked by writer");
    }

    // ── 2. Cross-namespace read consistency ───────────────────────────────────

    /// <summary>
    /// Count (cross-ns, lock-free via Interlocked TotalCount) must stay consistent
    /// with the sum of per-ns counts at quiescent moments.
    /// </summary>
    [Fact]
    public async Task TotalCount_ConsistentUnderConcurrentWrites()
    {
        const int ns = 6;
        const int per = 25;

        var tasks = Enumerable.Range(0, ns).Select(n => Task.Run(() =>
        {
            for (int i = 0; i < per; i++)
                _index.Upsert(MakeEntry($"n{n}-{i:D3}", $"count-ns-{n}", $"entry {i}"));
        })).ToArray();

        await Task.WhenAll(tasks);

        int sumOfPerNs = 0;
        for (int n = 0; n < ns; n++)
            sumOfPerNs += _index.CountInNamespace($"count-ns-{n}");

        Assert.Equal(ns * per, sumOfPerNs);
        Assert.Equal(ns * per, _index.Count);
    }

    // ── 3. Random-op stress (deadlock detector) ───────────────────────────────

    /// <summary>
    /// 2000 random ops (Upsert/Delete/Search/Get across 10 ns) on 8 threads.
    /// Must complete without deadlock or exception within a generous timeout.
    /// A deadlock manifests as a hung test (xUnit longRunningTestSeconds=60 catches it).
    /// </summary>
    [Fact]
    public async Task RandomOps_MultiNamespace_NoDeadlockNoException()
    {
        const int threads = 8;
        const int opsPerThread = 250;

        var seed = Environment.TickCount;
        var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            var rng = new Random(seed + t);
            for (int i = 0; i < opsPerThread; i++)
            {
                var nsIdx = rng.Next(0, 10);
                var ns = $"stress-{nsIdx}";
                var id = $"t{t}-i{i}";

                switch (rng.Next(0, 5))
                {
                    case 0:
                    case 1: // Upsert (40%)
                        _index.Upsert(MakeEntry(id, ns, $"stress entry {id}"));
                        break;
                    case 2: // Search (20%)
                        _index.Search(_embedding.Embed("stress entry"), ns, k: 3);
                        break;
                    case 3: // Get (20%)
                        _index.Get(id, ns);
                        break;
                    case 4: // Delete (20%)
                        _index.Delete(id);
                        break;
                }
            }
        })).ToArray();

        // Generous but finite — a deadlock would blow through this.
        var allDone = Task.WhenAll(tasks);
        var winner = await Task.WhenAny(allDone, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(ReferenceEquals(winner, allDone),
            "RandomOps deadlocked — Task.WhenAll did not complete within 30 s");
        Assert.All(tasks, t => Assert.True(t.IsCompletedSuccessfully, t.Exception?.Message));
    }

    /// <summary>
    /// UpsertBatch with entries spanning multiple namespaces takes each namespace's
    /// write lock exactly once for its sub-batch. Concurrent UpsertBatch calls to
    /// disjoint namespaces must not block each other.
    /// </summary>
    [Fact]
    public async Task UpsertBatch_MixedNamespaces_ParallelizesByGroup()
    {
        var batchA = Enumerable.Range(0, 30).Select(i =>
            MakeEntry($"a-{i:D2}", "batch-a", $"A{i}")).ToList();
        var batchB = Enumerable.Range(0, 30).Select(i =>
            MakeEntry($"b-{i:D2}", "batch-b", $"B{i}")).ToList();

        // Two concurrent UpsertBatch calls to disjoint namespaces
        var t1 = Task.Run(() => _index.UpsertBatch(batchA));
        var t2 = Task.Run(() => _index.UpsertBatch(batchB));
        await Task.WhenAll(t1, t2);

        Assert.Equal(30, t1.Result);
        Assert.Equal(30, t2.Result);
        Assert.Equal(30, _index.CountInNamespace("batch-a"));
        Assert.Equal(30, _index.CountInNamespace("batch-b"));
        Assert.Equal(60, _index.Count);
    }

    // ── 4. Resource management ────────────────────────────────────────────────

    /// <summary>
    /// Per-ns RWLs must be cleaned up on Dispose so long-lived tests don't leak
    /// OS handles. We exercise many namespaces then Dispose and check we don't
    /// throw.
    /// </summary>
    [Fact]
    public void Dispose_ReleasesAllPerNsLocks_NoThrow()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"lockhier_dispose_{Guid.NewGuid():N}");
        var persistence = new PersistenceManager(dataPath);
        var index = new CognitiveIndex(persistence);

        for (int i = 0; i < 50; i++)
            index.Upsert(MakeEntry($"d-{i}", $"dispose-ns-{i}", $"d{i}"));

        // Should complete without exception even after heavy namespace creation.
        // Second Dispose must be a no-op (idempotent).
        index.Dispose();
        index.Dispose();
        persistence.Dispose();

        try { Directory.Delete(dataPath, recursive: true); } catch { }
    }

    /// <summary>
    /// Regression: RemoveNamespace must not remove locator entries whose current value
    /// points at a DIFFERENT namespace. If id X was upserted to ns=A (becoming an orphan
    /// there) and then re-upserted to ns=B, deleting ns=A must leave ns=B's X intact
    /// and the total count correct.
    ///
    /// Before the fix, RemoveNamespace("A") called _idToNamespace.TryRemove("X") which
    /// blew away the ns=B locator AND decremented TotalCount, driving Count negative
    /// while ns=B's entries dict still held X.
    /// </summary>
    [Fact]
    public void RemoveNamespace_PreservesOrphanedIdInOtherNamespace()
    {
        // Step 1: upsert X into ns=A
        _index.Upsert(MakeEntry("X", "orphan-ns-a", "first home"));
        Assert.Equal(1, _index.Count);

        // Step 2: upsert X into ns=B — per the pre-existing orphan invariant, X still
        // lives in ns=A's entries dict but the locator now points at ns=B. Count stays
        // at 1 because TrackEntry does not double-count (the id was already present in
        // the locator; overwriting a locator value does not increment).
        _index.Upsert(MakeEntry("X", "orphan-ns-b", "second home"));
        Assert.Equal(1, _index.Count);

        // Step 3: delete ns=A. The orphan in ns=A is gone; ns=B still has X.
        _index.DeleteAllInNamespace("orphan-ns-a");

        // Count must stay at 1 — not 0, not negative.
        Assert.Equal(1, _index.Count);
        Assert.Equal(1, _index.CountInNamespace("orphan-ns-b"));
        Assert.NotNull(_index.Get("X", "orphan-ns-b"));
        Assert.NotNull(_index.Get("X")); // resolves via locator
    }

    /// <summary>
    /// After Dispose, any method that needs a per-ns lock must throw
    /// ObjectDisposedException rather than reaching into a torn-down
    /// ReaderWriterLockSlim. The volatile _disposed flag is set BEFORE locks are
    /// torn down so a racing caller gets a clean exception instead of a hard
    /// failure from deep inside ReaderWriterLockSlim.
    /// </summary>
    [Fact]
    public void Operations_AfterDispose_ThrowObjectDisposedException()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"lockhier_afterdispose_{Guid.NewGuid():N}");
        var persistence = new PersistenceManager(dataPath);
        var index = new CognitiveIndex(persistence);

        // Prime a namespace so its lock exists in _nsLocks.
        index.Upsert(MakeEntry("primed", "primed-ns", "before dispose"));
        index.Dispose();

        // A representative sample of methods that must all fail cleanly.
        Assert.Throws<ObjectDisposedException>(() =>
            index.Upsert(MakeEntry("late", "primed-ns", "after dispose")));
        Assert.Throws<ObjectDisposedException>(() => index.CountInNamespace("primed-ns"));
        Assert.Throws<ObjectDisposedException>(() => index.Get("primed", "primed-ns"));
        Assert.Throws<ObjectDisposedException>(() => index.Delete("primed"));
        Assert.Throws<ObjectDisposedException>(() => index.GetAllInNamespace("primed-ns"));

        persistence.Dispose();
        try { Directory.Delete(dataPath, recursive: true); } catch { }
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }
}
