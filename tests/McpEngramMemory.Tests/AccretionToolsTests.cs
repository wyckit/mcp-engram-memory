using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class AccretionToolsTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly ClusterManager _clusters;
    private readonly LifecycleEngine _lifecycle;
    private readonly AccretionScanner _scanner;
    private readonly AccretionTools _tools;

    private sealed class StubEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    public AccretionToolsTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"accretion_tools_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _clusters = new ClusterManager(_index, _persistence);
        _lifecycle = new LifecycleEngine(_index);
        _scanner = new AccretionScanner(_index);
        _tools = new AccretionTools(_scanner, _clusters, _lifecycle, new StubEmbeddingService());
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    [Fact]
    public void TriggerAccretionScan_ReturnsResults()
    {
        // 4 entries so each has 3 external neighbors (meets default minPoints=3)
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var result = _tools.TriggerAccretionScan("test");
        Assert.Equal(4, result.ScannedCount);
        Assert.Equal(1, result.ClustersDetected);
        Assert.Single(result.NewCollapses);
    }

    [Fact]
    public void GetPendingCollapses_AfterScan_ReturnsCollapses()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        _tools.TriggerAccretionScan("test");
        var pending = _tools.GetPendingCollapses("test");
        Assert.Single(pending);
        Assert.Equal(4, pending[0].MemberCount);
        Assert.Equal("test", pending[0].Ns);
    }

    [Fact]
    public void CollapseCluster_FullFlow_ArchivesAndCreatesSummary()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", text: "item a", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", text: "item b", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", text: "item c", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", text: "item d", lifecycleState: "ltm"));

        var scanResult = _tools.TriggerAccretionScan("test");
        var collapseId = scanResult.NewCollapses[0].CollapseId;

        var result = _tools.CollapseCluster(collapseId, "Summary of items a, b, c, d", new[] { 0.99f, 0.01f, 0f });
        Assert.Contains("Collapsed 4 entries", result);

        // All original entries should be archived
        Assert.Equal("archived", _index.Get("a")!.LifecycleState);
        Assert.Equal("archived", _index.Get("b")!.LifecycleState);
        Assert.Equal("archived", _index.Get("c")!.LifecycleState);
        Assert.Equal("archived", _index.Get("d")!.LifecycleState);

        // A cluster should exist
        Assert.Equal(1, _clusters.ClusterCount);

        // Summary entry should be searchable
        var searchResults = _index.Search(
            new[] { 0.99f, 0.01f, 0f }, "test", k: 5,
            includeStates: new HashSet<string> { "ltm" });
        Assert.Contains(searchResults, r => r.IsSummaryNode);

        // No more pending collapses
        Assert.Empty(_tools.GetPendingCollapses("test"));
    }

    [Fact]
    public void DismissCollapse_RemovesPendingAndExcludesFromFutureScans()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var scanResult = _tools.TriggerAccretionScan("test");
        var collapseId = scanResult.NewCollapses[0].CollapseId;

        var result = _tools.DismissCollapse(collapseId);
        Assert.Contains("Dismissed", result);

        // No pending collapses
        Assert.Empty(_tools.GetPendingCollapses("test"));

        // Rescan should not detect these entries
        var scanResult2 = _tools.TriggerAccretionScan("test");
        Assert.Equal(0, scanResult2.ScannedCount);
    }

    [Fact]
    public void CollapseCluster_InvalidId_ReturnsError()
    {
        var result = _tools.CollapseCluster("invalid", "summary", new[] { 1f, 0f });
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void TriggerAccretionScan_CustomParameters()
    {
        // 3 close entries: each has 2 external neighbors, meets minPoints=2
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));

        // With minPoints=2, these should cluster (each has 2 external neighbors)
        var result = _tools.TriggerAccretionScan("test", epsilon: 0.15f, minPoints: 2);
        Assert.Equal(1, result.ClustersDetected);
    }

    [Fact]
    public void TriggerAccretionScan_EmptyNamespace_NoCollapses()
    {
        var result = _tools.TriggerAccretionScan("empty_ns");
        Assert.Equal(0, result.ScannedCount);
        Assert.Equal(0, result.ClustersDetected);
        Assert.Empty(result.NewCollapses);
    }
}
