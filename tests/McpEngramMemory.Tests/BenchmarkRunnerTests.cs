using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Storage;
using Xunit.Abstractions;

namespace McpEngramMemory.Tests;

public class BenchmarkRunnerTests
{
    private readonly ITestOutputHelper _output;

    public BenchmarkRunnerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void RecallAtK_AllRelevantRetrieved_Returns1()
    {
        var retrieved = new[] { "a", "b", "c" };
        var relevant = new HashSet<string> { "a", "b", "c" };
        Assert.Equal(1f, BenchmarkRunner.ComputeRecallAtK(retrieved, relevant));
    }

    [Fact]
    public void RecallAtK_NoneRetrieved_Returns0()
    {
        var retrieved = new[] { "x", "y" };
        var relevant = new HashSet<string> { "a", "b" };
        Assert.Equal(0f, BenchmarkRunner.ComputeRecallAtK(retrieved, relevant));
    }

    [Fact]
    public void RecallAtK_PartialRetrieval()
    {
        var retrieved = new[] { "a", "x", "b" };
        var relevant = new HashSet<string> { "a", "b", "c", "d" };
        Assert.Equal(0.5f, BenchmarkRunner.ComputeRecallAtK(retrieved, relevant));
    }

    [Fact]
    public void RecallAtK_EmptyRelevant_Returns1()
    {
        var retrieved = new[] { "a" };
        var relevant = new HashSet<string>();
        Assert.Equal(1f, BenchmarkRunner.ComputeRecallAtK(retrieved, relevant));
    }

    [Fact]
    public void PrecisionAtK_AllRelevant()
    {
        var retrieved = new[] { "a", "b", "c" };
        var relevant = new HashSet<string> { "a", "b", "c" };
        Assert.Equal(1f, BenchmarkRunner.ComputePrecisionAtK(retrieved, relevant, 3));
    }

    [Fact]
    public void PrecisionAtK_HalfRelevant()
    {
        var retrieved = new[] { "a", "x", "b", "y" };
        var relevant = new HashSet<string> { "a", "b" };
        Assert.Equal(0.5f, BenchmarkRunner.ComputePrecisionAtK(retrieved, relevant, 4));
    }

    [Fact]
    public void PrecisionAtK_ZeroK_Returns0()
    {
        var retrieved = new[] { "a" };
        var relevant = new HashSet<string> { "a" };
        Assert.Equal(0f, BenchmarkRunner.ComputePrecisionAtK(retrieved, relevant, 0));
    }

    [Fact]
    public void MRR_FirstResult()
    {
        var retrieved = new[] { "a", "b", "c" };
        var relevant = new HashSet<string> { "a" };
        Assert.Equal(1f, BenchmarkRunner.ComputeMRR(retrieved, relevant));
    }

    [Fact]
    public void MRR_SecondResult()
    {
        var retrieved = new[] { "x", "a", "b" };
        var relevant = new HashSet<string> { "a" };
        Assert.Equal(0.5f, BenchmarkRunner.ComputeMRR(retrieved, relevant));
    }

    [Fact]
    public void MRR_ThirdResult()
    {
        var retrieved = new[] { "x", "y", "a" };
        var relevant = new HashSet<string> { "a" };
        Assert.Equal(1f / 3f, BenchmarkRunner.ComputeMRR(retrieved, relevant), 4);
    }

    [Fact]
    public void MRR_NoRelevant_Returns0()
    {
        var retrieved = new[] { "x", "y" };
        var relevant = new HashSet<string> { "a" };
        Assert.Equal(0f, BenchmarkRunner.ComputeMRR(retrieved, relevant));
    }

    [Fact]
    public void NdcgAtK_PerfectRanking_Returns1()
    {
        var grades = new Dictionary<string, int> { ["a"] = 3, ["b"] = 2, ["c"] = 1 };
        var retrieved = new[] { "a", "b", "c" };
        Assert.Equal(1f, BenchmarkRunner.ComputeNdcgAtK(retrieved, grades, 3), 4);
    }

    [Fact]
    public void NdcgAtK_ReversedRanking_LessThan1()
    {
        var grades = new Dictionary<string, int> { ["a"] = 3, ["b"] = 2, ["c"] = 1 };
        var retrieved = new[] { "c", "b", "a" };
        var ndcg = BenchmarkRunner.ComputeNdcgAtK(retrieved, grades, 3);
        Assert.True(ndcg < 1f);
        Assert.True(ndcg > 0f);
    }

    [Fact]
    public void NdcgAtK_NoRelevantResults_Returns0()
    {
        var grades = new Dictionary<string, int> { ["a"] = 3 };
        var retrieved = new[] { "x", "y", "z" };
        Assert.Equal(0f, BenchmarkRunner.ComputeNdcgAtK(retrieved, grades, 3));
    }

    [Fact]
    public void NdcgAtK_EmptyGrades_Returns0()
    {
        var grades = new Dictionary<string, int>();
        var retrieved = new[] { "a", "b" };
        Assert.Equal(0f, BenchmarkRunner.ComputeNdcgAtK(retrieved, grades, 2));
    }

    [Fact]
    public void DefaultDataset_Has25SeedsAnd20Queries()
    {
        var dataset = BenchmarkRunner.CreateDefaultDataset();
        Assert.Equal(25, dataset.SeedEntries.Count);
        Assert.Equal(20, dataset.Queries.Count);
        Assert.Equal("default-v1", dataset.DatasetId);
    }

    [Fact]
    public void DefaultDataset_AllQueryReferencesExistInSeeds()
    {
        var dataset = BenchmarkRunner.CreateDefaultDataset();
        var seedIds = dataset.SeedEntries.Select(s => s.Id).ToHashSet();
        foreach (var query in dataset.Queries)
        {
            foreach (var gradeId in query.RelevanceGrades.Keys)
                Assert.Contains(gradeId, seedIds);
        }
    }

    [Fact]
    public void DefaultDataset_AllQueriesHaveValidRelevanceGrades()
    {
        var dataset = BenchmarkRunner.CreateDefaultDataset();
        foreach (var query in dataset.Queries)
        {
            Assert.NotEmpty(query.RelevanceGrades);
            Assert.True(query.RelevanceGrades.Values.All(v => v >= 0 && v <= 3));
        }
    }

    [Fact]
    public void DefaultDataset_SeedIdsAreUnique()
    {
        var dataset = BenchmarkRunner.CreateDefaultDataset();
        var ids = dataset.SeedEntries.Select(s => s.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void DefaultDataset_QueryIdsAreUnique()
    {
        var dataset = BenchmarkRunner.CreateDefaultDataset();
        var ids = dataset.Queries.Select(q => q.QueryId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Theory]
    [InlineData("paraphrase-v1")]
    [InlineData("multihop-v1")]
    [InlineData("scale-v1")]
    [InlineData("realworld-v1")]
    public void Dataset_SeedAndQueryIdsAreUnique(string datasetId)
    {
        var dataset = BenchmarkRunner.CreateDataset(datasetId);
        Assert.NotNull(dataset);
        var seedIds = dataset.SeedEntries.Select(s => s.Id).ToList();
        Assert.Equal(seedIds.Count, seedIds.Distinct().Count());
        var queryIds = dataset.Queries.Select(q => q.QueryId).ToList();
        Assert.Equal(queryIds.Count, queryIds.Distinct().Count());
    }

    [Theory]
    [InlineData("paraphrase-v1")]
    [InlineData("multihop-v1")]
    [InlineData("scale-v1")]
    [InlineData("realworld-v1")]
    public void Dataset_AllRelevanceGradesReferenceValidSeeds(string datasetId)
    {
        var dataset = BenchmarkRunner.CreateDataset(datasetId)!;
        var seedIds = dataset.SeedEntries.Select(s => s.Id).ToHashSet();
        foreach (var query in dataset.Queries)
        {
            foreach (var grade in query.RelevanceGrades)
                Assert.True(seedIds.Contains(grade.Key),
                    $"Query '{query.QueryId}' references non-existent seed '{grade.Key}'");
        }
    }

    [Fact]
    public void GetAvailableDatasets_ContainsAllFive()
    {
        var ids = BenchmarkRunner.GetAvailableDatasets();
        Assert.Contains("default-v1", ids);
        Assert.Contains("paraphrase-v1", ids);
        Assert.Contains("multihop-v1", ids);
        Assert.Contains("scale-v1", ids);
        Assert.Contains("realworld-v1", ids);
    }

    [Fact]
    public void CreateDataset_UnknownId_ReturnsNull()
    {
        Assert.Null(BenchmarkRunner.CreateDataset("nonexistent"));
    }

    [Fact]
    public void ParaphraseDataset_Has25Seeds15Queries()
    {
        var ds = BenchmarkRunner.CreateParaphraseDataset();
        Assert.Equal(25, ds.SeedEntries.Count);
        Assert.Equal(15, ds.Queries.Count);
    }

    [Fact]
    public void MultihopDataset_Has25Seeds15Queries()
    {
        var ds = BenchmarkRunner.CreateMultiHopDataset();
        Assert.Equal(25, ds.SeedEntries.Count);
        Assert.Equal(15, ds.Queries.Count);
    }

    [Fact]
    public void ScaleDataset_Has80Seeds30Queries()
    {
        var ds = BenchmarkRunner.CreateScaleDataset();
        Assert.Equal(80, ds.SeedEntries.Count);
        Assert.Equal(30, ds.Queries.Count);
    }

    [Fact]
    public void RealWorldDataset_Has30Seeds20Queries()
    {
        var ds = BenchmarkRunner.CreateRealWorldDataset();
        Assert.Equal(30, ds.SeedEntries.Count);
        Assert.Equal(20, ds.Queries.Count);
        Assert.Equal("realworld-v1", ds.DatasetId);
    }

    [Theory]
    [InlineData("default-v1")]
    [InlineData("paraphrase-v1")]
    [InlineData("multihop-v1")]
    [InlineData("scale-v1")]
    public void RunAllBenchmarks_OutputResults(string datasetId)
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"bench_{Guid.NewGuid():N}");
        var persistence = new PersistenceManager(dataPath);
        var embedding = new HashEmbeddingService();
        var index = new CognitiveIndex(persistence);
        var runner = new BenchmarkRunner(index, embedding);

        var dataset = BenchmarkRunner.CreateDataset(datasetId)!;
        var result = runner.Run(dataset);

        _output.WriteLine($"=== {datasetId} ===");
        _output.WriteLine($"  Seeds: {result.TotalEntries}, Queries: {result.TotalQueries}");
        _output.WriteLine($"  Recall@K:    {result.MeanRecallAtK:F3}");
        _output.WriteLine($"  Precision@K: {result.MeanPrecisionAtK:F3}");
        _output.WriteLine($"  MRR:         {result.MeanMRR:F3}");
        _output.WriteLine($"  nDCG@K:      {result.MeanNdcgAtK:F3}");
        _output.WriteLine($"  Latency:     {result.MeanLatencyMs:F3}ms (P95: {result.P95LatencyMs:F3}ms)");

        Assert.True(result.MeanRecallAtK >= 0f);
        Assert.True(result.MeanMRR >= 0f);
        Assert.Equal(dataset.SeedEntries.Count, result.TotalEntries);
        Assert.Equal(dataset.Queries.Count, result.TotalQueries);

        index.Dispose();
        persistence.Dispose();
        if (Directory.Exists(dataPath))
            Directory.Delete(dataPath, true);
    }

    // ── Ablation Study ──

    [Theory]
    [InlineData("default-v1")]
    [InlineData("scale-v1")]
    public void RunAblation_OutputsDeltas(string datasetId)
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"ablation_{Guid.NewGuid():N}");
        var persistence = new PersistenceManager(dataPath);
        using var embedding = new OnnxEmbeddingService();
        var index = new CognitiveIndex(persistence);
        var runner = new BenchmarkRunner(index, embedding);

        var dataset = BenchmarkRunner.CreateDataset(datasetId)!;
        var ablation = runner.RunAblation(dataset);

        _output.WriteLine($"=== ABLATION: {datasetId} ===");
        _output.WriteLine($"  Baseline (Vector, quantized):");
        _output.WriteLine($"    Recall@K: {ablation.Baseline.MeanRecallAtK:F3}  Precision@K: {ablation.Baseline.MeanPrecisionAtK:F3}  MRR: {ablation.Baseline.MeanMRR:F3}  nDCG@K: {ablation.Baseline.MeanNdcgAtK:F3}  Latency: {ablation.Baseline.MeanLatencyMs:F3}ms");

        foreach (var c in ablation.Comparisons)
        {
            _output.WriteLine($"  {c.Mode}:");
            _output.WriteLine($"    Recall@K: {c.Result.MeanRecallAtK:F3} ({c.RecallDelta:+0.000;-0.000})  Precision@K: {c.Result.MeanPrecisionAtK:F3} ({c.PrecisionDelta:+0.000;-0.000})  MRR: {c.Result.MeanMRR:F3} ({c.MrrDelta:+0.000;-0.000})  nDCG@K: {c.Result.MeanNdcgAtK:F3} ({c.NdcgDelta:+0.000;-0.000})  Latency: {c.Result.MeanLatencyMs:F3}ms ({c.LatencyDeltaMs:+0.000;-0.000}ms)");
        }

        // Structural assertions: all comparisons ran
        Assert.Equal(4, ablation.Comparisons.Count);
        Assert.Equal(datasetId, ablation.DatasetId);
        Assert.True(ablation.Baseline.MeanRecallAtK >= 0f);

        // Each mode should produce valid results
        foreach (var c in ablation.Comparisons)
        {
            Assert.True(c.Result.MeanRecallAtK >= 0f);
            Assert.True(c.Result.MeanRecallAtK <= 1f);
        }

        index.Dispose();
        persistence.Dispose();
        if (Directory.Exists(dataPath))
            Directory.Delete(dataPath, true);
    }

    // ── ONNX Benchmarks ──

    [Theory]
    [InlineData("default-v1", "vector")]
    [InlineData("default-v1", "hybrid")]
    [InlineData("default-v1", "vector_rerank")]
    [InlineData("default-v1", "hybrid_rerank")]
    [InlineData("paraphrase-v1", "vector")]
    [InlineData("multihop-v1", "vector")]
    [InlineData("scale-v1", "vector")]
    public void RunOnnxBenchmark(string datasetId, string mode)
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"onnx_bench_{Guid.NewGuid():N}");
        var persistence = new PersistenceManager(dataPath);
        using var embedding = new OnnxEmbeddingService();
        var index = new CognitiveIndex(persistence);
        var runner = new BenchmarkRunner(index, embedding);

        var searchMode = mode switch
        {
            "hybrid" => BenchmarkRunner.SearchMode.Hybrid,
            "vector_rerank" => BenchmarkRunner.SearchMode.VectorRerank,
            "hybrid_rerank" => BenchmarkRunner.SearchMode.HybridRerank,
            _ => BenchmarkRunner.SearchMode.Vector
        };

        var dataset = BenchmarkRunner.CreateDataset(datasetId)!;
        var result = runner.Run(dataset, searchMode);

        _output.WriteLine($"=== {datasetId} [{mode}] (ONNX bge-micro-v2) ===");
        _output.WriteLine($"  Seeds: {result.TotalEntries}, Queries: {result.TotalQueries}");
        _output.WriteLine($"  Recall@K:    {result.MeanRecallAtK:F3}");
        _output.WriteLine($"  Precision@K: {result.MeanPrecisionAtK:F3}");
        _output.WriteLine($"  MRR:         {result.MeanMRR:F3}");
        _output.WriteLine($"  nDCG@K:      {result.MeanNdcgAtK:F3}");
        _output.WriteLine($"  Latency:     {result.MeanLatencyMs:F3}ms (P95: {result.P95LatencyMs:F3}ms)");

        // Per-query breakdown for diagnostics
        foreach (var q in result.QueryScores)
        {
            _output.WriteLine($"    {q.QueryId}: R={q.RecallAtK:F2} P={q.PrecisionAtK:F2} MRR={q.MRR:F2} nDCG={q.NdcgAtK:F2} [{q.LatencyMs:F1}ms]");
        }

        Assert.True(result.MeanRecallAtK >= 0f);
        Assert.True(result.MeanMRR >= 0f);

        index.Dispose();
        persistence.Dispose();
        if (Directory.Exists(dataPath))
            Directory.Delete(dataPath, true);
    }
}
