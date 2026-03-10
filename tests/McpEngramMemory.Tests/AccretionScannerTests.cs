using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class AccretionScannerTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly ClusterManager _clusters;
    private readonly LifecycleEngine _lifecycle;
    private readonly AccretionScanner _scanner;

    public AccretionScannerTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"accretion_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _clusters = new ClusterManager(_index, _persistence);
        _lifecycle = new LifecycleEngine(_index);
        _scanner = new AccretionScanner(_index);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    // ── DBSCAN Unit Tests ──

    [Fact]
    public void Dbscan_EmptyInput_ReturnsNoClusters()
    {
        var clusters = AccretionScanner.Dbscan(new List<CognitiveEntry>(), 0.15f, 3);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Dbscan_SinglePoint_NoCluster()
    {
        var entries = new List<CognitiveEntry>
        {
            new("a", new[] { 1f, 0f }, "test", lifecycleState: "ltm")
        };
        var clusters = AccretionScanner.Dbscan(entries, 0.15f, 3);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Dbscan_TightCluster_DetectedAsSingleCluster()
    {
        // 4 nearly identical vectors — each has 3 external neighbors, meets minPoints=3
        var entries = new List<CognitiveEntry>
        {
            new("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"),
            new("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"),
            new("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"),
            new("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"),
        };

        var clusters = AccretionScanner.Dbscan(entries, 0.15f, 3);
        Assert.Single(clusters);
        Assert.Equal(4, clusters[0].Count);
    }

    [Fact]
    public void Dbscan_TwoDistinctClusters_DetectedSeparately()
    {
        // Cluster 1: 4 vectors near (1, 0, 0)
        // Cluster 2: 4 vectors near (0, 1, 0)
        var entries = new List<CognitiveEntry>
        {
            new("a1", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"),
            new("a2", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"),
            new("a3", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"),
            new("a4", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"),
            new("b1", new[] { 0f, 1f, 0f }, "test", lifecycleState: "ltm"),
            new("b2", new[] { 0.01f, 0.99f, 0f }, "test", lifecycleState: "ltm"),
            new("b3", new[] { 0.02f, 0.98f, 0f }, "test", lifecycleState: "ltm"),
            new("b4", new[] { 0.03f, 0.97f, 0f }, "test", lifecycleState: "ltm"),
        };

        var clusters = AccretionScanner.Dbscan(entries, 0.15f, 3);
        Assert.Equal(2, clusters.Count);
    }

    [Fact]
    public void Dbscan_ScatteredPoints_AllNoise()
    {
        // Orthogonal vectors — very far apart
        var entries = new List<CognitiveEntry>
        {
            new("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"),
            new("b", new[] { 0f, 1f, 0f }, "test", lifecycleState: "ltm"),
            new("c", new[] { 0f, 0f, 1f }, "test", lifecycleState: "ltm"),
        };

        var clusters = AccretionScanner.Dbscan(entries, 0.01f, 3);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Dbscan_BelowMinPoints_NoCluster()
    {
        // 2 close vectors but minPoints=3 (need 3 external neighbors)
        var entries = new List<CognitiveEntry>
        {
            new("a", new[] { 1f, 0f }, "test", lifecycleState: "ltm"),
            new("b", new[] { 0.99f, 0.01f }, "test", lifecycleState: "ltm"),
        };

        var clusters = AccretionScanner.Dbscan(entries, 0.15f, 3);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Dbscan_SelfExcluded_MinPointsMeansExternalNeighbors()
    {
        // 3 close vectors with minPoints=3 — each has only 2 external neighbors, not enough
        var entries = new List<CognitiveEntry>
        {
            new("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"),
            new("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"),
            new("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"),
        };

        var clusters = AccretionScanner.Dbscan(entries, 0.15f, 3);
        Assert.Empty(clusters); // 3 points, each with 2 neighbors < minPoints=3

        // But with minPoints=2, they cluster
        var clusters2 = AccretionScanner.Dbscan(entries, 0.15f, 2);
        Assert.Single(clusters2);
        Assert.Equal(3, clusters2[0].Count);
    }

    // ── ScanNamespace Integration Tests ──

    [Fact]
    public void ScanNamespace_OnlyScansLtmEntries()
    {
        // STM entries should be ignored even if they cluster
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "stm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f }, "test", lifecycleState: "stm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f }, "test", lifecycleState: "stm"));

        var result = _scanner.ScanNamespace("test");
        Assert.Equal(0, result.ScannedCount);
        Assert.Equal(0, result.ClustersDetected);
    }

    [Fact]
    public void ScanNamespace_DetectsLtmCluster()
    {
        // 4 entries so each has 3 external neighbors (meets default minPoints=3)
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var result = _scanner.ScanNamespace("test");
        Assert.Equal(4, result.ScannedCount);
        Assert.Equal(1, result.ClustersDetected);
        Assert.Single(result.NewCollapses);
        Assert.Equal(4, result.NewCollapses[0].MemberCount);
    }

    [Fact]
    public void ScanNamespace_SkipsSummaryNodes()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f }, "test", lifecycleState: "ltm"));
        var summaryEntry = new CognitiveEntry("s", new[] { 0.995f, 0.005f }, "test", lifecycleState: "ltm")
        {
            IsSummaryNode = true
        };
        _index.Upsert(summaryEntry);

        var result = _scanner.ScanNamespace("test", minPoints: 1);
        // Summary node should be excluded from scan — only 2 entries scanned
        Assert.Equal(2, result.ScannedCount);
    }

    [Fact]
    public void ScanNamespace_DuplicateScan_DoesNotDuplicateCollapses()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var result1 = _scanner.ScanNamespace("test");
        Assert.Equal(1, result1.ClustersDetected);
        Assert.Single(result1.NewCollapses);

        // Scan again — same entries should not produce a new collapse
        var result2 = _scanner.ScanNamespace("test");
        Assert.Equal(1, result2.ClustersDetected);
        Assert.Empty(result2.NewCollapses); // Already pending
    }

    // ── Pending Collapse Lifecycle ──

    [Fact]
    public void GetPendingCollapses_ReturnsOnlyForNamespace()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "ns1", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "ns1", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "ns1", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "ns1", lifecycleState: "ltm"));

        _scanner.ScanNamespace("ns1");

        Assert.Single(_scanner.GetPendingCollapses("ns1"));
        Assert.Empty(_scanner.GetPendingCollapses("ns2"));
    }

    [Fact]
    public void ExecuteCollapse_ArchivesMembersAndCreatesCluster()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var scanResult = _scanner.ScanNamespace("test");
        var collapseId = scanResult.NewCollapses[0].CollapseId;

        var result = _scanner.ExecuteCollapse(
            collapseId, "Summary of a, b, c, d", new[] { 0.99f, 0.01f, 0f },
            _clusters, _lifecycle);

        Assert.Contains("Collapsed 4 entries", result);

        // Members should be archived
        Assert.Equal("archived", _index.Get("a")!.LifecycleState);
        Assert.Equal("archived", _index.Get("b")!.LifecycleState);
        Assert.Equal("archived", _index.Get("c")!.LifecycleState);
        Assert.Equal("archived", _index.Get("d")!.LifecycleState);

        // Cluster should exist
        Assert.Equal(1, _clusters.ClusterCount);

        // Pending collapse should be removed
        Assert.Equal(0, _scanner.PendingCount);
    }

    [Fact]
    public void ExecuteCollapse_NonExistentId_ReturnsError()
    {
        var result = _scanner.ExecuteCollapse(
            "nonexistent", "summary", new[] { 1f, 0f },
            _clusters, _lifecycle);
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void ExecuteCollapse_WhenArchiveStepFails_PreservesPendingCollapse()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var scanResult = _scanner.ScanNamespace("test");
        var collapseId = scanResult.NewCollapses[0].CollapseId;

        // Simulate partial failure: one member disappears before collapse execution.
        _index.Delete("d");

        var result = _scanner.ExecuteCollapse(
            collapseId, "Summary of a, b, c, d", new[] { 0.99f, 0.01f, 0f },
            _clusters, _lifecycle);

        Assert.StartsWith("Error:", result);
        Assert.Contains("partially failed during archive step", result);
        Assert.Equal(1, _scanner.PendingCount);
    }

    [Fact]
    public void ExecuteCollapse_AfterArchiveFailure_RetrySucceedsAndClearsPending()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var scanResult = _scanner.ScanNamespace("test");
        var collapseId = scanResult.NewCollapses[0].CollapseId;

        // First attempt fails because one member disappears.
        _index.Delete("d");
        var firstAttempt = _scanner.ExecuteCollapse(
            collapseId, "Summary of a, b, c, d", new[] { 0.99f, 0.01f, 0f },
            _clusters, _lifecycle);
        Assert.StartsWith("Error:", firstAttempt);
        Assert.Equal(1, _scanner.PendingCount);

        // Restore missing member and retry the same pending collapse.
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));
        var secondAttempt = _scanner.ExecuteCollapse(
            collapseId, "Summary of a, b, c, d", new[] { 0.99f, 0.01f, 0f },
            _clusters, _lifecycle);

        Assert.Contains("Collapsed 4 entries", secondAttempt);
        Assert.Equal(0, _scanner.PendingCount);
        Assert.Equal("archived", _index.Get("a")!.LifecycleState);
        Assert.Equal("archived", _index.Get("b")!.LifecycleState);
        Assert.Equal("archived", _index.Get("c")!.LifecycleState);
        Assert.Equal("archived", _index.Get("d")!.LifecycleState);
    }

    [Fact]
    public void DismissCollapse_MarksEntriesAsExcluded()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var scanResult = _scanner.ScanNamespace("test");
        var collapseId = scanResult.NewCollapses[0].CollapseId;

        var dismissResult = _scanner.DismissCollapse(collapseId);
        Assert.Contains("Dismissed", dismissResult);

        // Pending count should be 0
        Assert.Equal(0, _scanner.PendingCount);

        // Subsequent scan should not detect these entries again
        var scanResult2 = _scanner.ScanNamespace("test");
        Assert.Equal(0, scanResult2.ScannedCount);
        Assert.Empty(scanResult2.NewCollapses);
    }

    [Fact]
    public void DismissCollapse_NonExistent_ReturnsError()
    {
        var result = _scanner.DismissCollapse("nonexistent");
        Assert.StartsWith("Error:", result);
    }
}
