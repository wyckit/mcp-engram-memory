using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// The two automatic background scans are quadratic in the candidate count and run on every
/// namespace unprompted — auto-link every 6 hours, accretion every 30 minutes — with no size
/// limit, since MEMORY_MAX_NAMESPACE_SIZE defaults to int.MaxValue.
///
/// Measured cost on 384-dim vectors: ~0.4s at 2,000 entries, ~1–2s at 4,200, ~3–5s at 8,000.
/// That is comfortable at realistic sizes and becomes minutes per sweep in the tens of
/// thousands, so these bounds are a predictability guarantee rather than a fix for a live
/// problem.
///
/// What matters most here is that truncation is *reported*. A scan that quietly examined
/// half a namespace returns the same shape as one that examined all of it and found nothing,
/// which is the more dangerous of the two failures.
/// </summary>
public class ScanBoundTests : IDisposable
{
    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;

    public ScanBoundTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"scanbound_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 10);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    private void Seed(int count, string ns, string state)
    {
        var rng = new Random(7);
        for (int i = 0; i < count; i++)
        {
            var v = new float[8];
            for (int d = 0; d < v.Length; d++) v[d] = (float)rng.NextDouble();
            _index.Upsert(new CognitiveEntry($"{ns}-e{i}", v, ns, $"entry {i}", lifecycleState: state));
        }
    }

    [Fact]
    public void AutoLinkScan_BoundsCandidatesAndReportsWhatItSkipped()
    {
        Seed(50, "big", "stm");
        var scanner = new AutoLinkScanner(_index, _graph, new DuplicateDetector());

        var result = scanner.Scan("big", threshold: null, maxNewEdges: null, tenantId: "", maxScanEntries: 20);

        Assert.Equal(20, result.ScannedEntries);
        Assert.Equal(30, result.EntriesNotScanned);
    }

    [Fact]
    public void AutoLinkScan_ReportsNothingSkippedWhenUnderTheBound()
    {
        Seed(10, "small", "stm");
        var scanner = new AutoLinkScanner(_index, _graph, new DuplicateDetector());

        var result = scanner.Scan("small", threshold: null, maxNewEdges: null, tenantId: "", maxScanEntries: 20);

        Assert.Equal(10, result.ScannedEntries);
        Assert.Equal(0, result.EntriesNotScanned);
    }

    [Fact]
    public void AccretionScan_BoundsCandidatesAndReportsWhatItSkipped()
    {
        // DBSCAN only considers LTM, non-summary entries.
        Seed(50, "bigltm", "ltm");
        var scanner = new AccretionScanner(_index);

        var result = scanner.ScanNamespace("bigltm", tenantId: "", maxScanEntries: 20);

        Assert.Equal(20, result.ScannedCount);
        Assert.Equal(30, result.EntriesNotScanned);
    }

    [Fact]
    public void AccretionScan_ReportsNothingSkippedWhenUnderTheBound()
    {
        Seed(10, "smallltm", "ltm");
        var scanner = new AccretionScanner(_index);

        var result = scanner.ScanNamespace("smallltm", tenantId: "", maxScanEntries: 20);

        Assert.Equal(10, result.ScannedCount);
        Assert.Equal(0, result.EntriesNotScanned);
    }

    [Fact]
    public void DefaultBoundIsWellAboveRealisticNamespaceSizes()
    {
        // Guards against someone lowering the default to a number that would silently start
        // truncating ordinary namespaces. The largest namespace observed in a real store was
        // ~4.2k entries; the default must stay comfortably clear of that.
        Assert.True(AutoLinkScanner.DefaultMaxScanEntries >= 10_000);
        Assert.True(AccretionScanner.DefaultMaxScanEntries >= 10_000);
    }
}
