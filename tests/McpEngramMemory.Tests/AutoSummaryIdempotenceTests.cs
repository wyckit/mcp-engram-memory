using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;
using Xunit;

namespace McpEngramMemory.Tests;

/// <summary>
/// Auto-summary nodes must not accumulate duplicates across rescans.
///
/// Observed live: the `synthesis` namespace held five cluster-summary entries where
/// list_clusters reported only two clusters. Three summaries were orphans whose cluster was
/// "not found", and two pairs had byte-identical text, identical score and identical access
/// counts — the same memories summarised more than once, competing for the same recall slots.
///
/// The mechanism is a persistence asymmetry, not a clustering bug. A cluster and its summary
/// are written through two independent debounced paths: cluster metadata via
/// ScheduleSaveClusters, which fires only when clusters change, and the summary via a normal
/// entry upsert, which rides the constant stream of entry writes. A process that exits without
/// flushing — killed rather than shut down — can therefore persist the summary entry while
/// losing the cluster that explains it.
///
/// On the next scan, HasExistingCluster finds no matching cluster because the cluster is gone,
/// mints a fresh Guid, and writes a second summary with identical text. Every unclean exit adds
/// another copy, which is why the duplicates number in the handful rather than one per 30-minute
/// scan interval.
///
/// The fix is to derive the cluster id from its member set, so re-summarising the same memories
/// re-uses the same id and the upsert overwrites in place. That makes rescans idempotent whatever
/// the reason the cluster metadata went missing, rather than only for the cause found here.
/// </summary>
public sealed class AutoSummaryIdempotenceTests : IDisposable
{
    private readonly string _dataPath =
        Path.Combine(Path.GetTempPath(), $"autosummary_{Guid.NewGuid():N}");
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly AccretionScanner _scanner;
    private readonly StubEmbedding _embedding = new();

    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    public AutoSummaryIdempotenceTests()
    {
        _persistence = new PersistenceManager(_dataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _scanner = new AccretionScanner(_index);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        try { if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, recursive: true); }
        catch { /* best-effort */ }
    }

    private void SeedTightCluster(string ns, int count)
    {
        for (int i = 0; i < count; i++)
        {
            // Near-identical vectors so DBSCAN groups them deterministically.
            var vector = new[] { 1f, 0.001f * i };
            _index.Upsert(new CognitiveEntry(
                $"{ns}-e{i}", vector, ns, $"clusterable body {i}", lifecycleState: "ltm"));
        }
    }

    private int SummaryCount(string ns)
        => _index.GetAllInNamespace(ns).Count(e => e.IsSummaryNode);

    [Fact]
    public void Rescan_AfterClusterMetadataLost_DoesNotDuplicateTheSummary()
    {
        const string ns = "recovery";
        SeedTightCluster(ns, 4);

        // First scan: cluster + summary created.
        var first = new ClusterManager(_index, _persistence);
        _scanner.ScanNamespace(ns, minPoints: 2, autoSummarize: true, clusters: first, embedding: _embedding);
        Assert.Equal(1, SummaryCount(ns));

        // A fresh ClusterManager with no clusters loaded stands in for the real failure: the
        // process died before the debounced cluster save flushed, so on restart the summary entry
        // is present in the index and the cluster that produced it is not.
        var afterRestart = new ClusterManager(_index, _persistence);
        _scanner.ScanNamespace(ns, minPoints: 2, autoSummarize: true, clusters: afterRestart, embedding: _embedding);

        Assert.Equal(1, SummaryCount(ns));
    }

    [Fact]
    public void RepeatedScans_OverUnchangedMembers_ProduceOneSummary()
    {
        const string ns = "stable";
        SeedTightCluster(ns, 5);
        var clusters = new ClusterManager(_index, _persistence);

        for (int i = 0; i < 3; i++)
            _scanner.ScanNamespace(ns, minPoints: 2, autoSummarize: true, clusters: clusters, embedding: _embedding);

        Assert.Equal(1, SummaryCount(ns));
    }

    [Fact]
    public void SameMemberSet_ProducesTheSameClusterId()
    {
        const string ns = "deterministic";
        SeedTightCluster(ns, 4);

        var a = new ClusterManager(_index, _persistence);
        var firstScan = _scanner.ScanNamespace(ns, minPoints: 2, autoSummarize: true, clusters: a, embedding: _embedding);

        var b = new ClusterManager(_index, _persistence);
        var secondScan = _scanner.ScanNamespace(ns, minPoints: 2, autoSummarize: true, clusters: b, embedding: _embedding);

        var firstId = Assert.Single(firstScan.AutoSummaries).ClusterId;
        var secondId = Assert.Single(secondScan.AutoSummaries).ClusterId;

        // Identity must come from the members, not from when the scan happened. Without this the
        // summary id changes every run and each orphan becomes permanent recall noise.
        Assert.Equal(firstId, secondId);
    }
}
