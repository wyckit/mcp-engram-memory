using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class AutoSummarizeIntegrationTests : IDisposable
{
    private readonly string _dataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly AccretionScanner _scanner;
    private readonly ClusterManager _clusters;
    private readonly HashEmbeddingService _embedding;

    public AutoSummarizeIntegrationTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"autosumm_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_dataPath);
        _index = new CognitiveIndex(_persistence);
        _scanner = new AccretionScanner(_index);
        _clusters = new ClusterManager(_index, _persistence);
        _embedding = new HashEmbeddingService();
    }

    [Fact]
    public void ScanWithAutoSummarize_CreatesClustersAndSummaries()
    {
        // Create a tight cluster of 4 entries (cosine similarity > 0.85)
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test",
            "SIMD vector operations for search", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test",
            "SIMD acceleration for vector dot product", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test",
            "Hardware SIMD intrinsics for vector norms", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test",
            "SIMD optimized cosine similarity computation", lifecycleState: "ltm"));

        var result = _scanner.ScanNamespace("test", tenantId: "",
            autoSummarize: true, clusters: _clusters, embedding: _embedding);

        Assert.Equal(4, result.ScannedCount);
        Assert.Equal(1, result.ClustersDetected);
        Assert.NotNull(result.AutoSummaries);
        Assert.Single(result.AutoSummaries!);
        Assert.Equal(4, result.AutoSummaries![0].MemberCount);

        // Verify cluster was created
        var clusterList = _clusters.ListClusters("test", tenantId: "");
        Assert.Single(clusterList);
        Assert.True(clusterList[0].HasSummary);

        // Verify summary entry exists in the index as a searchable entry
        var summary = _index.Get(result.AutoSummaries[0].SummaryId, "test", tenantId: "");
        Assert.NotNull(summary);
        Assert.True(summary!.IsSummaryNode);
        Assert.Equal("ltm", summary.LifecycleState);
        Assert.Equal("cluster-summary", summary.Category);
    }

    [Fact]
    public void ScanWithAutoSummarize_MembersNotArchived()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test",
            "Test entry A", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test",
            "Test entry B", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test",
            "Test entry C", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test",
            "Test entry D", lifecycleState: "ltm"));

        _scanner.ScanNamespace("test", tenantId: "",
            autoSummarize: true, clusters: _clusters, embedding: _embedding);

        // Members should still be LTM (NOT archived — that's what distinguishes this from collapse)
        foreach (var id in new[] { "a", "b", "c", "d" })
        {
            var entry = _index.Get(id, "test", tenantId: "");
            Assert.NotNull(entry);
            Assert.Equal("ltm", entry!.LifecycleState);
        }
    }

    [Fact]
    public void ScanWithAutoSummarize_DoesNotDuplicateExistingClusters()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        // First scan creates cluster
        var result1 = _scanner.ScanNamespace("test", tenantId: "",
            autoSummarize: true, clusters: _clusters, embedding: _embedding);
        Assert.Single(result1.AutoSummaries!);

        // Second scan should not create duplicates
        var result2 = _scanner.ScanNamespace("test", tenantId: "",
            autoSummarize: true, clusters: _clusters, embedding: _embedding);
        Assert.Empty(result2.AutoSummaries!);

        // Still only one cluster
        Assert.Single(_clusters.ListClusters("test", tenantId: ""));
    }

    [Fact]
    public void ScanWithoutAutoSummarize_OnlyCreatesPendingCollapses()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var result = _scanner.ScanNamespace("test", tenantId: "");

        Assert.True(result.AutoSummaries is null || result.AutoSummaries.Count == 0);
        Assert.Single(result.NewCollapses);
        Assert.Empty(_clusters.ListClusters("test", tenantId: ""));
    }

    [Fact]
    public void ScanWithAutoSummarize_SummarySearchable()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test",
            "SIMD vector operations", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test",
            "SIMD acceleration for dot product", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test",
            "Hardware SIMD vector norms", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test",
            "SIMD cosine similarity", lifecycleState: "ltm"));

        var scanResult = _scanner.ScanNamespace("test", tenantId: "",
            autoSummarize: true, clusters: _clusters, embedding: _embedding);
        var summaryId = scanResult.AutoSummaries![0].SummaryId;

        // Verify the summary entry exists and is marked as a summary node
        var summaryEntry = _index.Get(summaryId, "test", tenantId: "");
        Assert.NotNull(summaryEntry);
        Assert.True(summaryEntry!.IsSummaryNode);

        // Search using the summary's own vector — should find it
        var searchResults = _index.Search(summaryEntry.Vector, "test", tenantId: "", k: 10);
        Assert.Contains(searchResults, r => r.Id == summaryId);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, true);
    }
}
