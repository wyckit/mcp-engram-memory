using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class BenchmarkToolsTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly MetricsCollector _metrics;
    private readonly BenchmarkRunner _runner;
    private readonly BenchmarkTools _tools;

    private sealed class StubEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    public BenchmarkToolsTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"benchmark_tools_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _metrics = new MetricsCollector();
        _runner = new BenchmarkRunner(_index, new StubEmbeddingService());
        _tools = new BenchmarkTools(_runner, _metrics);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    // ── RunBenchmark ──

    [Fact]
    public void RunBenchmark_DefaultDataset_ReturnsResults()
    {
        var result = _tools.RunBenchmark("default-v1");
        Assert.IsType<BenchmarkRunResult>(result);

        var bench = (BenchmarkRunResult)result;
        Assert.Equal("default-v1", bench.DatasetId);
        Assert.NotEmpty(bench.QueryScores);
        Assert.True(bench.TotalEntries > 0);
    }

    [Fact]
    public void RunBenchmark_InvalidDataset_ReturnsError()
    {
        var result = _tools.RunBenchmark("nonexistent-dataset");
        Assert.IsType<string>(result);

        var error = (string)result;
        Assert.StartsWith("Error:", error);
        Assert.Contains("nonexistent-dataset", error);
    }

    [Fact]
    public void RunBenchmark_HybridMode_ReturnsResults()
    {
        var result = _tools.RunBenchmark("default-v1", mode: "hybrid");
        Assert.IsType<BenchmarkRunResult>(result);

        var bench = (BenchmarkRunResult)result;
        Assert.Equal("default-v1", bench.DatasetId);
        Assert.NotEmpty(bench.QueryScores);
    }

    // ── GetMetrics ──

    [Fact]
    public void GetMetrics_NoData_ReturnsEmpty()
    {
        var result = _tools.GetMetrics();
        Assert.Empty(result);
    }

    [Fact]
    public void GetMetrics_AfterOperations_ReturnsData()
    {
        // Record some metrics
        _metrics.Record("search", 5.0);
        _metrics.Record("search", 10.0);
        _metrics.Record("store", 2.0);

        var result = _tools.GetMetrics();
        Assert.Equal(2, result.Count);

        var searchMetrics = result.FirstOrDefault(m => m.OperationType == "search");
        Assert.NotNull(searchMetrics);
        Assert.Equal(2, searchMetrics!.Count);
        Assert.True(searchMetrics.MeanMs > 0);

        var storeMetrics = result.FirstOrDefault(m => m.OperationType == "store");
        Assert.NotNull(storeMetrics);
        Assert.Equal(1, storeMetrics!.Count);
    }

    [Fact]
    public void GetMetrics_FilterByType()
    {
        _metrics.Record("search", 5.0);
        _metrics.Record("store", 2.0);

        var result = _tools.GetMetrics("search");
        Assert.Single(result);
        Assert.Equal("search", result[0].OperationType);
        Assert.Equal(1, result[0].Count);
    }

    // ── ResetMetrics ──

    [Fact]
    public void ResetMetrics_All_ClearsCounters()
    {
        _metrics.Record("search", 5.0);
        _metrics.Record("store", 2.0);

        var message = _tools.ResetMetrics();
        Assert.Equal("All metrics reset.", message);

        var result = _tools.GetMetrics();
        Assert.Empty(result);
    }

    [Fact]
    public void ResetMetrics_ByType_ClearsSpecific()
    {
        _metrics.Record("search", 5.0);
        _metrics.Record("store", 2.0);

        var message = _tools.ResetMetrics("search");
        Assert.Equal("Reset metrics for 'search'.", message);

        // "search" should be gone, "store" should remain
        var searchResult = _tools.GetMetrics("search");
        Assert.Empty(searchResult);

        var storeResult = _tools.GetMetrics("store");
        Assert.Single(storeResult);
        Assert.Equal(1, storeResult[0].Count);
    }
}
