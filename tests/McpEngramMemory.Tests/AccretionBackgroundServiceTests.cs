using System.Diagnostics;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Core.Services.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpEngramMemory.Tests;

public class AccretionBackgroundServiceTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly AccretionScanner _scanner;
    private readonly ClusterManager _clusters;
    private readonly HashEmbeddingService _embedding;

    public AccretionBackgroundServiceTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"accretion_bg_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _scanner = new AccretionScanner(_index);
        _clusters = new ClusterManager(_index, _persistence);
        _embedding = new HashEmbeddingService();
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    [Fact]
    public async Task ExecuteAsync_ScansAndDetectsClusters()
    {
        // 4 entries so each has 3 external neighbors (meets default minPoints=3)
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var service = new AccretionBackgroundService(_scanner, _index, _clusters, _embedding,
            NullLogger<AccretionBackgroundService>.Instance)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        // Poll for the first scan cycle to land instead of relying on a fixed delay.
        // A saturated CI ThreadPool (xunit runs many test classes in parallel) can push
        // the first scan well past a fixed 200 ms window, leaving pending empty and the
        // test falsely failing. The healthy path completes in tens of ms, so polling only
        // relaxes the false-negative bound, not the test's discriminative power.
        var pending = _scanner.GetPendingCollapses("test", tenantId: "");
        var sw = Stopwatch.StartNew();
        while (pending.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(25);
            pending = _scanner.GetPendingCollapses("test", tenantId: "");
        }

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // The scanner should have detected the cluster
        Assert.Single(pending);
        Assert.Equal(4, pending[0].MemberCount);
    }

    [Fact]
    public async Task ExecuteAsync_StopsOnCancellation()
    {
        var service = new AccretionBackgroundService(_scanner, _index, _clusters, _embedding,
            NullLogger<AccretionBackgroundService>.Instance)
        {
            Interval = TimeSpan.FromMinutes(60)
        };

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);
        // Should complete without hanging
    }

    /// <summary>
    /// Fault isolation belongs at the partition, not at the sweep.
    ///
    /// When the accretion sweep grew a tenant loop around the namespace loop, the try/catch stayed
    /// where it was — outside both. A single throwing partition stopped being a one-namespace
    /// outage and became starvation for every partition ordered after it, permanently: the sweep
    /// unwinds, the next cycle enumerates in the same order and dies in the same place, and the
    /// data behind the failing partition is never scanned again.
    ///
    /// Seam note: the throw is injected through the <see cref="IEmbeddingService"/> the service
    /// already takes, which <c>AccretionScanner.ScanNamespace</c> calls once per detected cluster
    /// while auto-summarizing. That keeps this a pure dependency-injection test — no production
    /// type's sealed-ness or virtual-ness is changed to make it observable.
    ///
    /// Ordering note: partition enumeration walks a ConcurrentDictionary, so which partition comes
    /// first is not something the test can pin. Rather than guess, the stub poisons whichever
    /// partition asks it to embed FIRST and keeps failing that one. That makes the failing
    /// partition the first one enumerated by construction, which is precisely the arrangement that
    /// starves everything behind it when the guard is at the wrong level — so the test fails
    /// deterministically without the per-partition catch, instead of passing on a coin flip.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OneFailingPartition_StillScansRemainingPartitions()
    {
        const string alphaTenant = "t-alpha";
        const string betaTenant = "t-beta";
        const string alphaNs = "alpha-ns";
        const string betaNs = "beta-ns";
        const string alphaMarker = "alphamarker";
        const string betaMarker = "betamarker";

        SeedScannablePartition(alphaTenant, alphaNs, alphaMarker);
        SeedScannablePartition(betaTenant, betaNs, betaMarker);

        var poison = new PartitionPoisoningEmbedding(alphaMarker, betaMarker);
        var tracker = new BackgroundWorkerStatusTracker();

        var service = new AccretionBackgroundService(_scanner, _index, _clusters, poison,
            NullLogger<AccretionBackgroundService>.Instance, tracker)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        // Poll rather than sleep a fixed span: a saturated ThreadPool can push the first cycle
        // well past any fixed window, and a timeout here is a false negative, not a real failure.
        var sw = Stopwatch.StartNew();
        while (poison.PoisonedMarker is null && sw.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(25);

        Assert.NotNull(poison.PoisonedMarker);

        bool alphaFailed = poison.PoisonedMarker == alphaMarker;
        var failedTenant = alphaFailed ? alphaTenant : betaTenant;
        var failedNs = alphaFailed ? alphaNs : betaNs;
        var survivingTenant = alphaFailed ? betaTenant : alphaTenant;
        var survivingNs = alphaFailed ? betaNs : alphaNs;

        // The surviving partition is, by the latch above, the one enumerated AFTER the failure.
        sw.Restart();
        while (SummaryCount(survivingNs, survivingTenant) == 0 && sw.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(25);

        // Read telemetry while the service is still running: cancelling mid-sweep would let the
        // cancellation itself become the recorded error and mask the partial-failure message.
        // Wait for the specific message rather than for any message — RecordCycle overwrites the
        // slot each cycle, so settling for the first non-null value would let an unrelated transient
        // (a mid-flush storage read, say) satisfy the poll and turn this into a flake.
        var failedLabel = $"{failedTenant}/{failedNs}";
        sw.Restart();
        var status = tracker.GetSnapshot().Accretion;
        while (status.LastErrorMessage?.Contains(failedLabel, StringComparison.Ordinal) != true
               && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(25);
            status = tracker.GetSnapshot().Accretion;
        }

        int survivingSummaries = SummaryCount(survivingNs, survivingTenant);
        int failedSummaries = SummaryCount(failedNs, failedTenant);
        int throwCount = poison.ThrowCount;

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // The failure was real and kept happening — otherwise the sweep never had anything to
        // isolate and the rest of this test proves nothing.
        Assert.True(throwCount > 0, "the poisoned partition never threw, so nothing was isolated");
        Assert.Equal(0, failedSummaries);

        // The regression, stated directly: the partition behind the failure is still scanned.
        Assert.Equal(1, survivingSummaries);

        // Over-correction control. Containing the fault must not mean swallowing it: a partition
        // that starves every cycle is indistinguishable from an idle one unless it is counted and
        // named, and the surviving partition must NOT be reported as having failed too.
        var lastError = status.LastErrorMessage;
        Assert.NotNull(lastError);
        Assert.Contains(failedLabel, lastError);
        Assert.DoesNotContain($"{survivingTenant}/{survivingNs}", lastError);
    }

    /// <summary>
    /// Four near-identical vectors, which DBSCAN groups at the scanner's default epsilon (0.15) and
    /// minPoints (3) — the defaults the background service scans with. The marker rides in each
    /// entry's text so it reaches AutoSummarizer's snippet section and therefore the summary string
    /// handed to <see cref="IEmbeddingService.Embed"/>, which is how the stub tells partitions apart.
    /// </summary>
    private void SeedScannablePartition(string tenantId, string ns, string marker)
    {
        for (int i = 0; i < 4; i++)
        {
            _index.Upsert(new CognitiveEntry(
                $"{ns}-e{i}", new[] { 1f, 0.001f * i, 0f }, ns,
                text: $"{marker} clusterable body {i}",
                lifecycleState: "ltm", tenantId: tenantId));
        }
    }

    private int SummaryCount(string ns, string tenantId)
        => _index.GetAllInNamespace(ns, tenantId).Count(e => e.IsSummaryNode);

    /// <summary>
    /// Embedding stub that permanently fails exactly one partition, chosen by enumeration order
    /// rather than by name: the first marker it is ever asked to embed becomes the poisoned one.
    ///
    /// The state is behind a lock because the assertions read it from the test thread while the
    /// background service writes it from its own.
    /// </summary>
    private sealed class PartitionPoisoningEmbedding : IEmbeddingService
    {
        private readonly object _gate = new();
        private readonly string[] _markers;
        private string? _poisonedMarker;
        private int _throwCount;

        public PartitionPoisoningEmbedding(params string[] markers) => _markers = markers;

        public int Dimensions => 3;

        /// <summary>The marker latched on the first Embed call, or null if never called.</summary>
        public string? PoisonedMarker
        {
            get { lock (_gate) { return _poisonedMarker; } }
        }

        /// <summary>How many times the poisoned partition has been failed.</summary>
        public int ThrowCount
        {
            get { lock (_gate) { return _throwCount; } }
        }

        public float[] Embed(string text)
        {
            var marker = _markers.FirstOrDefault(m => text.Contains(m, StringComparison.Ordinal));

            lock (_gate)
            {
                if (marker is not null)
                {
                    _poisonedMarker ??= marker;
                    if (_poisonedMarker == marker)
                    {
                        _throwCount++;
                        throw new InvalidOperationException(
                            $"embedding backend unavailable for partition '{marker}'");
                    }
                }
            }

            return new[] { 0.5f, 0.5f, 0.5f };
        }
    }
}
