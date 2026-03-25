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
    [InlineData("compound-v1")]
    [InlineData("ambiguity-v1")]
    [InlineData("distractor-v1")]
    [InlineData("specificity-v1")]
    [InlineData("physics-v1")]
    [InlineData("lifecycle-v1")]
    [InlineData("contamination-v1")]
    [InlineData("cluster-summary-v1")]
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
    [InlineData("compound-v1")]
    [InlineData("ambiguity-v1")]
    [InlineData("distractor-v1")]
    [InlineData("specificity-v1")]
    [InlineData("physics-v1")]
    [InlineData("lifecycle-v1")]
    [InlineData("contamination-v1")]
    [InlineData("cluster-summary-v1")]
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
    public void GetAvailableDatasets_ContainsAll()
    {
        var ids = BenchmarkRunner.GetAvailableDatasets();
        Assert.Contains("default-v1", ids);
        Assert.Contains("paraphrase-v1", ids);
        Assert.Contains("multihop-v1", ids);
        Assert.Contains("scale-v1", ids);
        Assert.Contains("realworld-v1", ids);
        Assert.Contains("compound-v1", ids);
        Assert.Contains("ambiguity-v1", ids);
        Assert.Contains("distractor-v1", ids);
        Assert.Contains("specificity-v1", ids);
        Assert.Contains("physics-v1", ids);
        Assert.Contains("lifecycle-v1", ids);
        Assert.Contains("contamination-v1", ids);
        Assert.Contains("cluster-summary-v1", ids);
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

    [Fact]
    public void AmbiguityDataset_Has24Seeds15Queries()
    {
        var ds = BenchmarkRunner.CreateAmbiguityDataset();
        Assert.Equal(24, ds.SeedEntries.Count);
        Assert.Equal(15, ds.Queries.Count);
        Assert.Equal("ambiguity-v1", ds.DatasetId);
    }

    [Fact]
    public void AmbiguityDataset_AllSeedsHaveCategories()
    {
        var ds = BenchmarkRunner.CreateAmbiguityDataset();
        foreach (var seed in ds.SeedEntries)
            Assert.False(string.IsNullOrEmpty(seed.Category),
                $"Ambiguity seed '{seed.Id}' should have a category for disambiguation testing");
    }

    [Fact]
    public void AmbiguityDataset_HasAmbiguousTermGroups()
    {
        var ds = BenchmarkRunner.CreateAmbiguityDataset();
        var seedIds = ds.SeedEntries.Select(s => s.Id).ToHashSet();
        // Verify key ambiguous term groups exist
        Assert.Contains("a-net-comp", seedIds);
        Assert.Contains("a-net-neural", seedIds);
        Assert.Contains("a-tree-ds", seedIds);
        Assert.Contains("a-tree-fs", seedIds);
        Assert.Contains("a-tree-dom", seedIds);
        Assert.Contains("a-mem-hw", seedIds);
        Assert.Contains("a-mem-mgmt", seedIds);
        Assert.Contains("a-mem-cognitive", seedIds);
    }

    [Fact]
    public void DistractorDataset_Has22Seeds15Queries()
    {
        var ds = BenchmarkRunner.CreateDistractorDataset();
        Assert.Equal(22, ds.SeedEntries.Count);
        Assert.Equal(15, ds.Queries.Count);
        Assert.Equal("distractor-v1", ds.DatasetId);
    }

    [Fact]
    public void DistractorDataset_AllQueriesHaveGrade0Distractors()
    {
        var ds = BenchmarkRunner.CreateDistractorDataset();
        foreach (var query in ds.Queries)
        {
            var hasDistractor = query.RelevanceGrades.Values.Any(v => v == 0);
            Assert.True(hasDistractor,
                $"Query '{query.QueryId}' should have at least one grade-0 distractor entry");
        }
    }

    [Fact]
    public void DistractorDataset_HasHomonymPairs()
    {
        var ds = BenchmarkRunner.CreateDistractorDataset();
        var seedIds = ds.SeedEntries.Select(s => s.Id).ToHashSet();
        // Verify key homonym pairs exist
        Assert.Contains("d-python-lang", seedIds);
        Assert.Contains("d-python-snake", seedIds);
        Assert.Contains("d-rust-lang", seedIds);
        Assert.Contains("d-rust-corrosion", seedIds);
        Assert.Contains("d-spring-framework", seedIds);
        Assert.Contains("d-spring-season", seedIds);
        Assert.Contains("d-spring-mechanical", seedIds);
    }

    [Theory]
    [InlineData("distractor-v1", "vector")]
    [InlineData("distractor-v1", "hybrid")]
    [InlineData("distractor-v1", "vector_rerank")]
    [InlineData("distractor-v1", "hybrid_rerank")]
    public void RunDistractorBenchmark(string datasetId, string mode)
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

        foreach (var q in result.QueryScores)
        {
            _output.WriteLine($"    {q.QueryId}: R={q.RecallAtK:F2} P={q.PrecisionAtK:F2} MRR={q.MRR:F2} nDCG={q.NdcgAtK:F2} [{q.LatencyMs:F1}ms] → [{string.Join(", ", q.ActualResultIds)}]");
        }

        Assert.True(result.MeanRecallAtK >= 0f);
        Assert.True(result.MeanMRR >= 0f);

        index.Dispose();
        persistence.Dispose();
        if (Directory.Exists(dataPath))
            Directory.Delete(dataPath, true);
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

    [Theory]
    [InlineData("ambiguity-v1", "vector")]
    [InlineData("ambiguity-v1", "hybrid")]
    [InlineData("ambiguity-v1", "vector_rerank")]
    [InlineData("ambiguity-v1", "hybrid_rerank")]
    public void RunAmbiguityBenchmark(string datasetId, string mode)
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

        foreach (var q in result.QueryScores)
        {
            _output.WriteLine($"    {q.QueryId}: R={q.RecallAtK:F2} P={q.PrecisionAtK:F2} MRR={q.MRR:F2} nDCG={q.NdcgAtK:F2} [{q.LatencyMs:F1}ms] → [{string.Join(", ", q.ActualResultIds)}]");
        }

        Assert.True(result.MeanRecallAtK >= 0f);
        Assert.True(result.MeanMRR >= 0f);

        index.Dispose();
        persistence.Dispose();
        if (Directory.Exists(dataPath))
            Directory.Delete(dataPath, true);
    }

    // ── Specificity Gradient Tests ──

    [Fact]
    public void SpecificityDataset_Has30Seeds18Queries()
    {
        var ds = BenchmarkRunner.CreateSpecificityDataset();
        Assert.Equal(30, ds.SeedEntries.Count);
        Assert.Equal(18, ds.Queries.Count);
        Assert.Equal("specificity-v1", ds.DatasetId);
    }

    [Fact]
    public void SpecificityDataset_AllSeedsHaveCategories()
    {
        var ds = BenchmarkRunner.CreateSpecificityDataset();
        foreach (var seed in ds.SeedEntries)
            Assert.False(string.IsNullOrEmpty(seed.Category),
                $"Seed '{seed.Id}' should have a category");
    }

    [Fact]
    public void SpecificityDataset_Has6TopicClusters()
    {
        var ds = BenchmarkRunner.CreateSpecificityDataset();
        var categories = ds.SeedEntries.Select(s => s.Category).Distinct().ToList();
        Assert.Equal(6, categories.Count);
        Assert.Contains("languages", categories);
        Assert.Contains("web", categories);
        Assert.Contains("databases", categories);
        Assert.Contains("ml", categories);
        Assert.Contains("systems", categories);
        Assert.Contains("security", categories);
    }

    [Fact]
    public void SpecificityDataset_Has3QueryTiers()
    {
        var ds = BenchmarkRunner.CreateSpecificityDataset();
        // Broad queries (sp-q01 through sp-q06) have 4-5 relevant seeds each
        var broadQueries = ds.Queries.Where(q =>
            int.Parse(q.QueryId.Replace("sp-q", "")) <= 6).ToList();
        Assert.Equal(6, broadQueries.Count);
        Assert.All(broadQueries, q =>
            Assert.True(q.RelevanceGrades.Count >= 4,
                $"Broad query '{q.QueryId}' should reference 4+ seeds, has {q.RelevanceGrades.Count}"));

        // Medium queries (sp-q07 through sp-q12) have 2-5 relevant seeds
        var mediumQueries = ds.Queries.Where(q =>
        {
            var num = int.Parse(q.QueryId.Replace("sp-q", ""));
            return num >= 7 && num <= 12;
        }).ToList();
        Assert.Equal(6, mediumQueries.Count);

        // Narrow queries (sp-q13 through sp-q18) have 1-3 relevant seeds
        var narrowQueries = ds.Queries.Where(q =>
            int.Parse(q.QueryId.Replace("sp-q", "")) >= 13).ToList();
        Assert.Equal(6, narrowQueries.Count);
        Assert.All(narrowQueries, q =>
            Assert.True(q.RelevanceGrades.Count <= 4,
                $"Narrow query '{q.QueryId}' should reference ≤4 seeds, has {q.RelevanceGrades.Count}"));
    }

    [Theory]
    [InlineData("specificity-v1", "vector")]
    [InlineData("specificity-v1", "hybrid")]
    [InlineData("specificity-v1", "vector_rerank")]
    [InlineData("specificity-v1", "hybrid_rerank")]
    public void RunSpecificityBenchmark(string datasetId, string mode)
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

        // Per-query breakdown with tier labels
        foreach (var q in result.QueryScores)
        {
            var num = int.Parse(q.QueryId.Replace("sp-q", ""));
            var tier = num <= 6 ? "BROAD" : num <= 12 ? "MEDIUM" : "NARROW";
            _output.WriteLine($"    [{tier}] {q.QueryId}: R={q.RecallAtK:F2} P={q.PrecisionAtK:F2} MRR={q.MRR:F2} nDCG={q.NdcgAtK:F2} [{q.LatencyMs:F1}ms] → [{string.Join(", ", q.ActualResultIds)}]");
        }

        Assert.True(result.MeanRecallAtK >= 0f);
        Assert.True(result.MeanMRR >= 0f);

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

    // ── Physics Re-Ranking Tests ──

    [Fact]
    public void PhysicsDataset_Has20Seeds10Queries()
    {
        var ds = BenchmarkRunner.CreatePhysicsDataset();
        Assert.Equal(20, ds.SeedEntries.Count);
        Assert.Equal(10, ds.Queries.Count);
        Assert.Equal("physics-v1", ds.DatasetId);
    }

    [Fact]
    public void PhysicsDataset_HasPairedActivationProfiles()
    {
        var ds = BenchmarkRunner.CreatePhysicsDataset();
        var coldSeeds = ds.SeedEntries.Where(s => s.Id.Contains("-cold")).ToList();
        var hotSeeds = ds.SeedEntries.Where(s => s.Id.Contains("-hot")).ToList();
        Assert.Equal(10, coldSeeds.Count);
        Assert.Equal(10, hotSeeds.Count);

        // Hot seeds should have higher access counts than cold seeds
        foreach (var hot in hotSeeds)
            Assert.True(hot.AccessCount >= 50, $"Hot seed '{hot.Id}' should have AccessCount >= 50, has {hot.AccessCount}");
        foreach (var cold in coldSeeds)
            Assert.True(cold.AccessCount <= 2, $"Cold seed '{cold.Id}' should have AccessCount <= 2, has {cold.AccessCount}");
    }

    [Theory]
    [InlineData("physics-v1", "vector")]
    [InlineData("physics-v1", "vector_rerank")]
    public void RunPhysicsBenchmark(string datasetId, string mode)
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"onnx_bench_{Guid.NewGuid():N}");
        var persistence = new PersistenceManager(dataPath);
        using var embedding = new OnnxEmbeddingService();
        var index = new CognitiveIndex(persistence);
        var runner = new BenchmarkRunner(index, embedding);

        var searchMode = mode switch
        {
            "vector_rerank" => BenchmarkRunner.SearchMode.VectorRerank,
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

        foreach (var q in result.QueryScores)
            _output.WriteLine($"    {q.QueryId}: R={q.RecallAtK:F2} P={q.PrecisionAtK:F2} MRR={q.MRR:F2} nDCG={q.NdcgAtK:F2} → [{string.Join(", ", q.ActualResultIds)}]");

        Assert.True(result.MeanRecallAtK >= 0f);
        Assert.True(result.MeanMRR >= 0f);

        index.Dispose();
        persistence.Dispose();
        if (Directory.Exists(dataPath))
            Directory.Delete(dataPath, true);
    }

    // ── Lifecycle-Aware Tests ──

    [Fact]
    public void LifecycleDataset_Has25Seeds15Queries()
    {
        var ds = BenchmarkRunner.CreateLifecycleDataset();
        Assert.Equal(25, ds.SeedEntries.Count);
        Assert.Equal(15, ds.Queries.Count);
        Assert.Equal("lifecycle-v1", ds.DatasetId);
    }

    [Fact]
    public void LifecycleDataset_Has3LifecycleStates()
    {
        var ds = BenchmarkRunner.CreateLifecycleDataset();
        var stm = ds.SeedEntries.Where(s => s.LifecycleState == "stm").ToList();
        var ltm = ds.SeedEntries.Where(s => s.LifecycleState == "ltm").ToList();
        var archived = ds.SeedEntries.Where(s => s.LifecycleState == "archived").ToList();
        Assert.Equal(10, stm.Count);
        Assert.Equal(10, ltm.Count);
        Assert.Equal(5, archived.Count);
    }

    [Theory]
    [InlineData("lifecycle-v1", "vector")]
    [InlineData("lifecycle-v1", "vector_rerank")]
    public void RunLifecycleBenchmark(string datasetId, string mode)
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"onnx_bench_{Guid.NewGuid():N}");
        var persistence = new PersistenceManager(dataPath);
        using var embedding = new OnnxEmbeddingService();
        var index = new CognitiveIndex(persistence);
        var runner = new BenchmarkRunner(index, embedding);

        var searchMode = mode switch
        {
            "vector_rerank" => BenchmarkRunner.SearchMode.VectorRerank,
            _ => BenchmarkRunner.SearchMode.Vector
        };

        var dataset = BenchmarkRunner.CreateDataset(datasetId)!;
        var result = runner.Run(dataset, searchMode);

        _output.WriteLine($"=== {datasetId} [{mode}] ===");
        _output.WriteLine($"  Seeds: {result.TotalEntries}, Queries: {result.TotalQueries}");
        _output.WriteLine($"  Recall@K:    {result.MeanRecallAtK:F3}");
        _output.WriteLine($"  Precision@K: {result.MeanPrecisionAtK:F3}");
        _output.WriteLine($"  MRR:         {result.MeanMRR:F3}");
        _output.WriteLine($"  nDCG@K:      {result.MeanNdcgAtK:F3}");

        foreach (var q in result.QueryScores)
        {
            // Tag archived queries for visibility
            string tag = q.QueryId.CompareTo("lc-q11") >= 0 ? " [ARCHIVED-TARGET]" : "";
            _output.WriteLine($"    {q.QueryId}{tag}: R={q.RecallAtK:F2} P={q.PrecisionAtK:F2} MRR={q.MRR:F2} nDCG={q.NdcgAtK:F2} → [{string.Join(", ", q.ActualResultIds)}]");
        }

        Assert.True(result.MeanRecallAtK >= 0f);
        Assert.True(result.MeanMRR >= 0f);

        index.Dispose();
        persistence.Dispose();
        if (Directory.Exists(dataPath))
            Directory.Delete(dataPath, true);
    }

    // ── Near-Duplicate Contamination Tests ──

    [Fact]
    public void ContaminationDataset_Has25Seeds12Queries()
    {
        var ds = BenchmarkRunner.CreateContaminationDataset();
        Assert.Equal(25, ds.SeedEntries.Count);
        Assert.Equal(12, ds.Queries.Count);
        Assert.Equal("contamination-v1", ds.DatasetId);
    }

    [Fact]
    public void ContaminationDataset_Has15UniquePlus10Duplicates()
    {
        var ds = BenchmarkRunner.CreateContaminationDataset();
        var unique = ds.SeedEntries.Count(s => s.Id.StartsWith("dup-u"));
        var dups = ds.SeedEntries.Count(s => s.Id.StartsWith("dup-d"));
        Assert.Equal(15, unique);
        Assert.Equal(10, dups);
    }

    [Theory]
    [InlineData("contamination-v1", "vector")]
    [InlineData("contamination-v1", "hybrid")]
    [InlineData("contamination-v1", "vector_rerank")]
    public void RunContaminationBenchmark(string datasetId, string mode)
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
            _ => BenchmarkRunner.SearchMode.Vector
        };

        var dataset = BenchmarkRunner.CreateDataset(datasetId)!;
        var result = runner.Run(dataset, searchMode);

        _output.WriteLine($"=== {datasetId} [{mode}] ===");
        _output.WriteLine($"  Seeds: {result.TotalEntries}, Queries: {result.TotalQueries}");
        _output.WriteLine($"  Recall@K:    {result.MeanRecallAtK:F3}");
        _output.WriteLine($"  Precision@K: {result.MeanPrecisionAtK:F3}");
        _output.WriteLine($"  MRR:         {result.MeanMRR:F3}");
        _output.WriteLine($"  nDCG@K:      {result.MeanNdcgAtK:F3}");

        foreach (var q in result.QueryScores)
            _output.WriteLine($"    {q.QueryId}: R={q.RecallAtK:F2} P={q.PrecisionAtK:F2} MRR={q.MRR:F2} nDCG={q.NdcgAtK:F2} → [{string.Join(", ", q.ActualResultIds)}]");

        Assert.True(result.MeanRecallAtK >= 0f);
        Assert.True(result.MeanMRR >= 0f);

        index.Dispose();
        persistence.Dispose();
        if (Directory.Exists(dataPath))
            Directory.Delete(dataPath, true);
    }

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

    // --- Cluster Summary Benchmark Tests ---

    [Fact]
    public void ClusterSummaryDataset_Has18Seeds10Queries()
    {
        var ds = BenchmarkRunner.CreateClusterSummaryDataset();
        Assert.Equal(18, ds.SeedEntries.Count);
        Assert.Equal(10, ds.Queries.Count);
    }

    [Fact]
    public void ClusterSummaryDataset_Has3SummaryNodes()
    {
        var ds = BenchmarkRunner.CreateClusterSummaryDataset();
        var summaries = ds.SeedEntries.Where(s => s.IsSummaryNode == true).ToList();
        Assert.Equal(3, summaries.Count);
        Assert.All(summaries, s => Assert.NotNull(s.SourceClusterId));
        var members = ds.SeedEntries.Where(s => s.IsSummaryNode != true).ToList();
        Assert.Equal(15, members.Count);
    }

    [Fact]
    public void ClusterSummaryDataset_Has3Clusters()
    {
        var ds = BenchmarkRunner.CreateClusterSummaryDataset();
        var summaries = ds.SeedEntries.Where(s => s.IsSummaryNode == true).ToList();
        var clusterIds = summaries.Select(s => s.SourceClusterId).Distinct().ToList();
        Assert.Equal(3, clusterIds.Count);
    }

    [Fact]
    public void ClusterSummaryDataset_SummaryFirstQueriesExist()
    {
        var ds = BenchmarkRunner.CreateClusterSummaryDataset();
        var sfQueries = ds.Queries.Where(q => q.SummaryFirst).ToList();
        Assert.True(sfQueries.Count >= 3, "Expected at least 3 summaryFirst queries");
        // Each summaryFirst query should grade the summary node as highest (3)
        foreach (var q in sfQueries)
        {
            var maxGrade = q.RelevanceGrades.Max(kvp => kvp.Value);
            var topGradeKeys = q.RelevanceGrades.Where(kvp => kvp.Value == maxGrade).Select(kvp => kvp.Key).ToList();
            Assert.True(topGradeKeys.Any(k => k.Contains("summary")),
                $"Query '{q.QueryId}' summaryFirst=true but no summary seed has max grade");
        }
    }

    [Theory]
    [InlineData("cluster-summary-v1", "vector")]
    [InlineData("cluster-summary-v1", "vector_rerank")]
    public void RunClusterSummaryBenchmark(string datasetId, string mode)
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"onnx_bench_{Guid.NewGuid():N}");
        var persistence = new PersistenceManager(dataPath);
        using var embedding = new OnnxEmbeddingService();
        var index = new CognitiveIndex(persistence);
        var runner = new BenchmarkRunner(index, embedding);

        var searchMode = mode switch
        {
            "vector_rerank" => BenchmarkRunner.SearchMode.VectorRerank,
            _ => BenchmarkRunner.SearchMode.Vector
        };

        var dataset = BenchmarkRunner.CreateDataset(datasetId)!;
        var result = runner.Run(dataset, searchMode);

        _output.WriteLine($"=== {datasetId} / {mode} ===");
        _output.WriteLine($"Recall@K={result.MeanRecallAtK:F3} Precision@K={result.MeanPrecisionAtK:F3} MRR={result.MeanMRR:F3} nDCG@K={result.MeanNdcgAtK:F3}");
        _output.WriteLine($"Mean latency={result.MeanLatencyMs:F2}ms, P95={result.P95LatencyMs:F2}ms");
        foreach (var q in result.QueryScores)
        {
            _output.WriteLine($"    {q.QueryId}: R={q.RecallAtK:F2} P={q.PrecisionAtK:F2} MRR={q.MRR:F2} nDCG={q.NdcgAtK:F2} [{q.LatencyMs:F1}ms] → [{string.Join(", ", q.ActualResultIds)}]");
        }

        Assert.True(result.MeanRecallAtK >= 0f);
        Assert.True(result.MeanMRR >= 0f);

        index.Dispose();
        persistence.Dispose();
        if (Directory.Exists(dataPath))
            Directory.Delete(dataPath, true);
    }
}
