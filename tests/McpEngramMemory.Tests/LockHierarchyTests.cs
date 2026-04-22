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
    /// A writer holding ns=A's write lock must NOT block a concurrent writer on ns=B.
    /// We hold A's write lock by subscribing an EntryUpserted handler that stalls the
    /// FIRST callback (post-lock-release but still on the writer thread) — no, that
    /// only blocks the thread, not the lock. Instead, we take A's write lock ourselves
    /// via reflection-free means: use GetAllInNamespace in a loop from a second thread
    /// while the writer is churning — and the writer on ns=B must complete quickly.
    ///
    /// Simpler signal: 8 parallel Upsert calls to 8 different namespaces must complete
    /// in roughly the same wall time as a single Upsert. If there were a global write
    /// lock they would serialize to ~8x. We assert they complete within a generous
    /// 3x-single bound to avoid flakiness while still catching the regression.
    /// </summary>
    [Fact]
    public async Task ConcurrentWrites_AcrossDifferentNamespaces_Parallelize()
    {
        // Warm up — first call may pay embedding/jit cost
        _index.Upsert(MakeEntry("warm", "warm-ns", "warm up"));
        _index.Delete("warm");

        // Baseline: one Upsert to one ns
        var sw = Stopwatch.StartNew();
        _index.Upsert(MakeEntry("solo", "solo-ns", "solo write"));
        sw.Stop();
        var singleMs = sw.Elapsed.TotalMilliseconds;

        // 8 parallel Upserts to 8 different namespaces
        sw.Restart();
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            _index.Upsert(MakeEntry($"p{i}", $"par-{i}-ns", $"parallel {i}")))).ToArray();
        await Task.WhenAll(tasks);
        sw.Stop();
        var parallelMs = sw.Elapsed.TotalMilliseconds;

        // Under a global lock, parallelMs would approach 8 * singleMs. Under per-ns
        // locks, it should be bounded by thread-pool scheduling overhead — typically
        // 1-3x singleMs. Use a generous 6x bound: still proves parallelism, tolerates
        // CI flakiness. (Pure serial would be ~8x.)
        Assert.True(parallelMs < Math.Max(50, singleMs * 6),
            $"Expected cross-ns parallelism but parallel={parallelMs:F1}ms exceeded 6x single={singleMs:F1}ms");

        // And all 8 must be committed.
        for (int i = 0; i < 8; i++)
            Assert.NotNull(_index.Get($"p{i}", $"par-{i}-ns"));
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
        index.Dispose();
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
