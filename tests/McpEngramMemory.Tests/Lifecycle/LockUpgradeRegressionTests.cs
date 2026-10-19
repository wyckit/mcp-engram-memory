// This file exercises the "snapshot-then-write" lock pattern used by all four
// maintenance entry points: RunDecayCycle, RunConsolidationPass,
// AutoLinkScanner.Scan, and AccretionScanner.ScanNamespace.
//
// Central claim under test:
//   Every maintenance pass acquires a read lock, snapshots entry IDs/objects
//   into a local List<T>, RELEASES the read lock, then calls per-entry write
//   methods (each of which independently acquires a write lock). There is no
//   point at which a maintenance pass holds a read lock and then attempts a
//   write lock on the same namespace — the pattern that ReaderWriterLockSlim
//   cannot service without deadlock.
//
// What the tests prove:
//   1. Cross-namespace independence: maintenance on namespace A does not block
//      foreground writes to namespace B.
//   2. Same-namespace serialization: maintenance on namespace A and foreground
//      writes to namespace A serialize gracefully under the RWLS write-lock
//      protocol — no deadlock, forward progress guaranteed.
//   3. Quantitative: every writer completes its exact workload, each write in
//      <10 s and all lanes within 60 s. These are progress bounds, not a proof
//      that any timeout necessarily means a production deadlock.

using System.Diagnostics;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;
using Xunit;

namespace McpEngramMemory.Tests.Lifecycle;

/// <summary>
/// Regression tests that detect lock-upgrade deadlocks in maintenance passes.
/// A lock-upgrade deadlock occurs when code holds a read lock on a namespace and
/// then attempts to acquire a write lock on the same namespace — RWLS does not
/// support this without explicit UpgradeableReadLock, and none of these paths use
/// one. The safe pattern (snapshot-under-read, release, per-entry-write) is what
/// this suite verifies remains intact.
/// </summary>
public class LockUpgradeRegressionTests : IDisposable
{
    // ── Setup ──────────────────────────────────────────────────────────────────

    private readonly string _dataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly MemoryDiffusionKernel _diffusion;
    private readonly LifecycleEngine _lifecycle;

    public LockUpgradeRegressionTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"lockupgrade_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_dataPath);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _diffusion = new MemoryDiffusionKernel(_index, _graph);
        _lifecycle = new LifecycleEngine(_index, _persistence, _diffusion);
    }

    private readonly ConcurrentTestWorkers _workers = new();

    public void Dispose()
    {
        // A detected, uninterruptible deadlock must fail without disposing the index under it.
        if (!_workers.IsQuiescent) return;
        _index.Dispose();
        _persistence.Dispose();
        try { Directory.Delete(_dataPath, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Deterministic vector: a one-hot at position (i % dims). Produces valid
    /// unit vectors without requiring a real embedding service.
    /// </summary>
    private static float[] MakeVector(int i, int dims = 64)
    {
        var v = new float[dims];
        v[i % dims] = 1f;
        return v;
    }

    /// <summary>
    /// Seed <paramref name="count"/> entries into <paramref name="ns"/> with
    /// varying lifecycle states and activation energies so that maintenance
    /// passes actually find work to do (state transitions, etc.).
    /// </summary>
    private void SeedNamespace(string ns, int count)
    {
        for (int i = 0; i < count; i++)
        {
            string state = (i % 3) switch
            {
                0 => "stm",
                1 => "ltm",
                _ => "archived"
            };
            var entry = new CognitiveEntry(
                $"{ns}-{i:D4}",
                MakeVector(i),
                ns,
                $"seed entry {i} in {ns}",
                lifecycleState: state);
            entry.ActivationEnergy = (i % 7) - 3f; // mix of positive and negative
            _index.Upsert(entry);
        }

        // Add a few graph edges so the diffusion kernel has something to work on
        // (otherwise ConsolidationPass skips the namespace entirely).
        for (int i = 0; i < Math.Min(count - 1, 50); i++)
            _graph.AddEdge(new GraphEdge($"{ns}-{i:D4}", $"{ns}-{(i + 1):D4}", "similar_to", 0.9f));
    }

    // ── Test 1: RunConsolidationPass — cross-namespace independence ────────────

    // One dedicated worker per lane, not one blocking ThreadPool work item per write.
    // Rejoin at every batch so maintenance and writers each make measured progress before
    // another batch starts. Work counts, seed sizes and latency limits stay explicit.
    private void RunBatches(int batches, params Action<int, CancellationToken>[] lanes)
    {
        var phase = new Barrier(lanes.Length);
        var completed = new int[lanes.Length];
        try
        {
            _workers.Run(TimeSpan.FromSeconds(60), lanes.Select((lane, i) =>
                (Action<CancellationToken>)(token =>
                {
                    for (int batch = 0; batch < batches; batch++)
                    {
                        phase.SignalAndWait(token);
                        token.ThrowIfCancellationRequested();
                        lane(batch, token);
                        completed[i]++;
                    }
                })).ToArray());
            Assert.All(completed, count => Assert.Equal(batches, count));
        }
        finally
        {
            if (_workers.IsQuiescent) phase.Dispose();
        }
    }

    private Action<int, CancellationToken> Writer(string ns, int count, int batches, int seedCount)
    {
        var latencies = new List<long>(); // only this lane writes; assertions follow its last batch
        return (batch, token) =>
        {
            for (int i = batch; i < count; i += batches)
            {
                token.ThrowIfCancellationRequested();
                var elapsed = Stopwatch.StartNew();
                _index.Upsert(new CognitiveEntry($"{ns}-new-{i:D4}", MakeVector(i + seedCount), ns,
                    $"concurrent write {i}"));
                latencies.Add(elapsed.ElapsedMilliseconds);
            }
            if (batch == batches - 1)
            {
                Assert.Equal(count, latencies.Count);
                Assert.True(latencies.Max() < 10000,
                    $"{ns} write latency exceeded 10000 ms (max={latencies.Max()} ms).");
                Assert.Equal(seedCount + count, _index.CountInNamespace(ns));
            }
        };
    }

    private void Decay(string ns)
    {
        var result = _lifecycle.RunDecayCycle(ns, tenantId: "");
        Assert.Empty(result.FailedNamespaces ?? Array.Empty<string>());
        Assert.True(result.ProcessedCount > 0, "decay must process entries, not merely return");
    }

    private void Consolidate(string ns)
    {
        var result = _lifecycle.RunConsolidationPass(ns, tenantId: "");
        Assert.Empty(result.FailedNamespaces ?? Array.Empty<string>());
        Assert.True(result.ProcessedEntries > 0, "consolidation must process entries, not silently fail or skip");
    }

    [Fact]
    public void ConsolidationPass_OnNsA_DoesNotBlockWritesToNsB()
    {
        SeedNamespace("ns-a", 500);
        SeedNamespace("ns-b", 500);
        RunBatches(3, (_, _) => Consolidate("ns-a"),
            Writer("ns-b", 100, 3, 500), Writer("ns-a", 100, 3, 500));
    }

    [Fact]
    public void DecayCycle_OnNsA_DoesNotBlockWritesToNsB()
    {
        SeedNamespace("ns-decay-a", 500);
        SeedNamespace("ns-decay-b", 500);
        RunBatches(4, (_, _) => Decay("ns-decay-a"), Writer("ns-decay-b", 100, 4, 500));
    }

    [Fact]
    public void AutoLinkScan_DoesNotBlockCognitiveIndexWrites()
    {
        SeedNamespace("ns-autolink", 200);
        var scanner = new AutoLinkScanner(_index, _graph,
            new McpEngramMemory.Core.Services.Intelligence.DuplicateDetector());
        RunBatches(3, (_, _) => Assert.True(scanner.Scan("ns-autolink", threshold: null,
            maxNewEdges: null, tenantId: "").ScannedEntries > 0), Writer("ns-autolink", 50, 3, 200));
    }

    [Fact]
    public void AccretionScan_DoesNotBlockCognitiveIndexWrites()
    {
        for (int i = 0; i < 300; i++)
            _index.Upsert(new CognitiveEntry($"ns-accretion-{i:D4}", MakeVector(i), "ns-accretion",
                $"ltm seed {i}", lifecycleState: "ltm"));
        SeedNamespace("ns-accretion-b", 300);
        var scanner = new McpEngramMemory.Core.Services.Intelligence.AccretionScanner(_index);
        RunBatches(2, (_, _) => Assert.True(scanner.ScanNamespace("ns-accretion", tenantId: "",
            epsilon: 0.15f, minPoints: 3).ScannedCount > 0), Writer("ns-accretion-b", 100, 2, 300));
    }

    [Fact]
    public void AllMaintenancePasses_ConcurrentWithWrites_NoDeadlock()
    {
        SeedNamespace("stress-a", 200);
        SeedNamespace("stress-b", 200);
        foreach (var entry in _index.GetAllInNamespace("stress-b", tenantId: ""))
            _index.SetLifecycleState(entry.Id, "ltm", "stress-b", tenantId: "");
        var autoLink = new AutoLinkScanner(_index, _graph,
            new McpEngramMemory.Core.Services.Intelligence.DuplicateDetector());
        var accretion = new McpEngramMemory.Core.Services.Intelligence.AccretionScanner(_index);

        // Every maintenance lane completes two passes, instead of running an unbounded loop
        // until 200 queued writer tasks happen to get scheduled. Both namespaces get 100 writes.
        RunBatches(2,
            (_, _) => Decay("stress-a"),
            (_, _) => Consolidate("stress-a"),
            (_, _) => Assert.True(autoLink.Scan("stress-b", threshold: null,
                maxNewEdges: null, tenantId: "").ScannedEntries > 0),
            (_, _) => Assert.True(accretion.ScanNamespace("stress-b", tenantId: "").ScannedCount > 0),
            Writer("stress-a", 100, 2, 200),
            Writer("stress-b", 100, 2, 200));
    }
}
