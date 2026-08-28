using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpEngramMemory.Tests;

/// <summary>
/// Per-namespace fault isolation: a spectral kernel failure in one namespace
/// (e.g. the eigensolver's orthonormality guard throwing) must not abort decay
/// or consolidation for every other namespace, the failing namespace must still
/// receive non-spectral decay, the partial failure must surface in
/// engram_status via <see cref="LifecyclePartialFailure"/>, and the kernel's
/// negative cache must bound the expensive eigensolve to once per graph revision.
/// </summary>
public class LifecycleFaultIsolationTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;

    public LifecycleFaultIsolationTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"lifecycle_fault_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    // ── decay isolation ─────────────────────────────────────────────────────────

    /// <summary>
    /// A throwing spectral kernel on one namespace must not abort the decay cycle:
    /// other namespaces still decay, and the failing namespace still receives full
    /// non-spectral pointwise decay (recorded in SpectralFallbackNamespaces).
    /// </summary>
    [Fact]
    public void RunDecayCycle_SpectralFailureFallsBackToPointwiseDecay()
    {
        SeedBackdatedStmEntries("bad", "b", count: 2);
        SeedBackdatedStmEntries("good", "g", count: 2);

        var kernel = new ThrowingDiffusionKernel(_index, _graph, failingNs: "bad");
        var lifecycle = new LifecycleEngine(_index, _persistence, kernel);

        // Stored-config path with no stored config defaults useSpectral=true
        // whenever a kernel is injected — so "bad" hits the throwing kernel.
        var result = lifecycle.RunDecayCycle("*", tenantId: "", useStoredConfig: true);

        // (a) Other namespaces still decayed: 0-10h backdate at decayRate 0.1 with
        // stm multiplier 3.0 gives debt 3, AccessCount 1 => AE = 1 - 3 = -2.
        foreach (var e in _index.GetAllInNamespace("good"))
            Assert.True(e.ActivationEnergy < 0f,
                $"Entry '{e.Id}' in 'good' should have decayed; AE={e.ActivationEnergy:F2}.");

        // (b) The failing namespace still received non-spectral pointwise decay.
        foreach (var e in _index.GetAllInNamespace("bad"))
            Assert.True(e.ActivationEnergy < 0f,
                $"Entry '{e.Id}' in 'bad' should have received pointwise fallback decay; AE={e.ActivationEnergy:F2}.");

        Assert.Equal(new[] { "bad" }, result.SpectralFallbackNamespaces);
        Assert.NotNull(result.FailedNamespaces);
        Assert.Empty(result.FailedNamespaces!);
        Assert.Equal(2, result.TotalNamespaces);
        Assert.Equal(4, result.ProcessedCount);
    }

    // ── consolidation isolation ─────────────────────────────────────────────────

    /// <summary>
    /// Consolidation must skip a namespace whose kernel throws (recorded in
    /// FailedNamespaces, no raw-activation fallback) and continue with the rest.
    /// </summary>
    [Fact]
    public void RunConsolidationPass_KernelFailureSkipsNamespaceAndContinues()
    {
        for (int i = 0; i < 4; i++)
            _index.Upsert(new CognitiveEntry($"b_{i}", new[] { (float)i, 0f }, "bad", $"bad {i}", lifecycleState: "stm"));

        SeedQualifyingCluster("good", "g", clusterSize: 32);
        foreach (var e in _index.GetAllInNamespace("good"))
            e.ActivationEnergy = 5.0f;

        var kernel = new ThrowingDiffusionKernel(_index, _graph, failingNs: "bad");
        var lifecycle = new LifecycleEngine(_index, _persistence, kernel);

        var result = lifecycle.RunConsolidationPass("*", tenantId: "");

        Assert.Equal(new[] { "bad" }, result.FailedNamespaces);
        Assert.Equal(1, result.ProcessedNamespaces);

        // The qualifying namespace was consolidated: cluster support promotes stm -> ltm.
        foreach (var e in _index.GetAllInNamespace("good"))
            Assert.Equal("ltm", e.LifecycleState);

        // The failing namespace was skipped whole — no raw-activation fallback.
        foreach (var e in _index.GetAllInNamespace("bad"))
            Assert.Equal("stm", e.LifecycleState);
    }

    // ── status tracker wiring ───────────────────────────────────────────────────

    /// <summary>
    /// DecayBackgroundService must record a partial failure as a completed cycle
    /// (entries processed, cycle counted) with the aggregate message in
    /// LastErrorMessage rather than treating it as a total abort.
    /// </summary>
    [Fact]
    public async Task DecayBackgroundService_RecordsPartialFailureInStatusTracker()
    {
        SeedBackdatedStmEntries("bad", "b", count: 1);
        SeedBackdatedStmEntries("good", "g", count: 1);

        var kernel = new ThrowingDiffusionKernel(_index, _graph, failingNs: "bad");
        var lifecycle = new LifecycleEngine(_index, _persistence, kernel);
        var tracker = new BackgroundWorkerStatusTracker();
        var service = new DecayBackgroundService(lifecycle, NullLogger<DecayBackgroundService>.Instance, tracker)
        {
            Interval = TimeSpan.FromMilliseconds(50) // Fast interval for testing
        };

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        // Poll for at least one completed cycle — robust to scheduler jitter
        // under parallel xUnit execution where a fixed-delay-then-assert flakes.
        //
        // The deadline is deliberately generous: the loop breaks the instant a
        // cycle lands, so a long deadline costs nothing on a healthy run and only
        // extends the genuinely-broken case. A 5s deadline failed on a 2-core CI
        // runner where thread-pool contention from parallel test classes (ONNX
        // model loads) starved the 50ms timer for the whole window.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        EngramWorkerStatus? decay = null;
        while (DateTime.UtcNow < deadline)
        {
            decay = tracker.GetSnapshot().Decay;
            if (decay.CyclesCompleted >= 1)
                break;
            await Task.Delay(50);
        }

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        Assert.NotNull(decay);
        Assert.True(decay!.CyclesCompleted >= 1, "At least one decay cycle should have completed.");
        Assert.True(decay.TotalEntriesProcessed > 0,
            "The cycle should be a partial success (entries processed), not an abort.");
        Assert.NotNull(decay.LastErrorMessage);
        Assert.Contains("spectral filter failed", decay.LastErrorMessage);
        Assert.Contains("bad", decay.LastErrorMessage);
    }

    // ── LifecyclePartialFailure formatting ──────────────────────────────────────

    [Fact]
    public void LifecyclePartialFailure_DescribeDecay_FormatsSpectralFallback()
    {
        var result = new DecayCycleResult(
            0, 0, 0, Array.Empty<string>(), Array.Empty<string>(),
            TotalNamespaces: 781,
            SpectralFallbackNamespaces: new[] { "ns1", "ns2" },
            FailedNamespaces: Array.Empty<string>());

        Assert.Equal(
            "spectral filter failed for 2/781 namespaces: ns1, ns2 — ran non-spectral fallback",
            LifecyclePartialFailure.DescribeDecay(result));
    }

    [Fact]
    public void LifecyclePartialFailure_DescribeDecay_ReturnsNullWhenClean()
    {
        var emptyLists = new DecayCycleResult(
            10, 0, 0, Array.Empty<string>(), Array.Empty<string>(),
            TotalNamespaces: 3,
            SpectralFallbackNamespaces: Array.Empty<string>(),
            FailedNamespaces: Array.Empty<string>());
        Assert.Null(LifecyclePartialFailure.DescribeDecay(emptyLists));

        var nullLists = new DecayCycleResult(
            10, 0, 0, Array.Empty<string>(), Array.Empty<string>());
        Assert.Null(LifecyclePartialFailure.DescribeDecay(nullLists));
    }

    [Fact]
    public void LifecyclePartialFailure_TruncatesLongNamespaceLists()
    {
        var failing = new[] { "ns1", "ns2", "ns3", "ns4", "ns5", "ns6", "ns7" };
        var result = new ConsolidationResult(
            2, 1, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>(),
            FailedNamespaces: failing);

        var message = LifecyclePartialFailure.DescribeConsolidation(result);

        Assert.NotNull(message);
        Assert.Contains("consolidation failed for 7/10 namespaces", message);
        Assert.Contains("ns1, ns2, ns3, ns4, ns5", message);
        Assert.Contains(", +2 more", message);
        Assert.DoesNotContain("ns6", message);
        Assert.DoesNotContain("ns7", message);
    }

    // ── negative failure cache ──────────────────────────────────────────────────

    /// <summary>
    /// Failures are deterministic per (namespace, graph revision) — the eigensolver
    /// RNG is seeded from revision ^ ns hash — so GetBasis must not re-run the
    /// expensive computation until the graph changes, while keeping the failure
    /// visible by rethrowing a cheap exception every call.
    /// </summary>
    [Fact]
    public void GetBasis_CachesFailurePerGraphRevision()
    {
        _index.Upsert(new CognitiveEntry("b_0", new[] { 1f, 0f }, "bad", "bad 0"));
        _index.Upsert(new CognitiveEntry("b_1", new[] { 0f, 1f }, "bad", "bad 1"));

        var kernel = new ThrowingDiffusionKernel(_index, _graph, failingNs: "bad");

        Assert.Throws<InvalidOperationException>(() => kernel.GetBasis("bad", tenantId: ""));
        var second = Assert.Throws<InvalidOperationException>(() => kernel.GetBasis("bad", tenantId: ""));

        Assert.Equal(1, kernel.ComputeAttempts);
        Assert.Contains("previously failed", second.Message);

        // A graph mutation bumps KnowledgeGraph.Revision and re-arms one retry.
        _graph.AddEdge(new GraphEdge("b_0", "b_1", "similar_to", 1.0f));

        Assert.Throws<InvalidOperationException>(() => kernel.GetBasis("bad", tenantId: ""));
        Assert.Equal(2, kernel.ComputeAttempts);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Seed <paramref name="count"/> STM entries with LastAccessedAt backdated 10h
    /// so each carries nonzero decay debt at cycle time (same mutation style as
    /// SpectralDecayTests).
    /// </summary>
    private void SeedBackdatedStmEntries(string ns, string idPrefix, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var id = $"{idPrefix}_{i}";
            _index.Upsert(new CognitiveEntry(id, new[] { (float)i, 0f }, ns, $"{ns} {i}", lifecycleState: "stm"));
            _index.Get(id)!.LastAccessedAt = DateTimeOffset.UtcNow.AddHours(-10);
        }
    }

    /// <summary>
    /// Seed a densely-connected cluster large enough to genuinely qualify for the
    /// real diffusion kernel (MinimumNodesForSpectral=32, MinimumEdgesForSpectral=8).
    /// Same shape as ConsolidationTests.SeedTestGraph: similar_to edges at 0.6
    /// density with a fixed rng seed.
    /// </summary>
    private void SeedQualifyingCluster(string ns, string idPrefix, int clusterSize)
    {
        var rng = new Random(42);
        for (int i = 0; i < clusterSize; i++)
            _index.Upsert(new CognitiveEntry($"{idPrefix}_{i}", new[] { (float)i, 0f }, ns, $"cluster {i}", lifecycleState: "stm"));

        for (int i = 0; i < clusterSize; i++)
            for (int j = i + 1; j < clusterSize; j++)
                if (rng.NextDouble() < 0.6)
                    _graph.AddEdge(new GraphEdge($"{idPrefix}_{i}", $"{idPrefix}_{j}", "similar_to", 1.0f));
    }

    /// <summary>
    /// Test double: routes every namespace through the real kernel except
    /// <c>failingNs</c>, whose basis computation throws the same shape of error
    /// the RandomizedEigensolver's orthonormality guard produces in production.
    /// </summary>
    private sealed class ThrowingDiffusionKernel : MemoryDiffusionKernel
    {
        private readonly string _failingNs;

        /// <summary>Times ComputeBasis was invoked for the failing namespace.</summary>
        public int ComputeAttempts { get; private set; }

        public ThrowingDiffusionKernel(CognitiveIndex index, KnowledgeGraph graph, string failingNs)
            : base(index, graph)
        {
            _failingNs = failingNs;
        }

        // No default on tenantId: the base signature dropped its fail-open "" default,
        // and a default re-added on an override would re-open that surface for calls
        // made through this static type.
        protected override DiffusionBasis? ComputeBasis(string ns, int topK, long graphRevision, string tenantId)
        {
            if (ns == _failingNs)
            {
                ComputeAttempts++;
                throw new InvalidOperationException("Q after final power iteration: column 0 has norm^2 0.5, expected 1.");
            }
            return base.ComputeBasis(ns, topK, graphRevision, tenantId);
        }
    }
}
