using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;
using System.Text.Json;

namespace McpEngramMemory.Tests;

public class BenchmarkToolsTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly MetricsCollector _metrics;
    private readonly BenchmarkRunner _runner;
    private readonly AgentOutcomeBenchmarkRunner _outcomeRunner;
    private readonly LiveAgentOutcomeBenchmarkRunner _liveOutcomeRunner;
    private readonly BenchmarkTools _tools;

    private sealed class StubEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    private sealed class StubAgentOutcomeModelClient : IAgentOutcomeModelClient
    {
        public Task<bool> IsAvailableAsync(string model, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<string?> GenerateAsync(
            string model,
            string prompt,
            int maxTokens = 320,
            float temperature = 0.1f,
            CancellationToken ct = default)
        {
            var ids = prompt.Split(Environment.NewLine)
                .Where(line => line.StartsWith("[", StringComparison.Ordinal))
                .Select(line => line[1..line.IndexOf(']')])
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var response = ids.Count == 0
                ? new LiveAgentOutcomeModelResponse("Insufficient context.", Array.Empty<string>(), InsufficientContext: true)
                : new LiveAgentOutcomeModelResponse($"Use {string.Join(", ", ids)}.", ids, InsufficientContext: false);

            return Task.FromResult<string?>(JsonSerializer.Serialize(response));
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubAgentOutcomeModelClientFactory : IAgentOutcomeModelClientFactory
    {
        public IAgentOutcomeModelClient Create(string provider, string? endpoint = null)
        {
            if (!string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported live benchmark provider. Supported providers: ollama.");

            return new StubAgentOutcomeModelClient();
        }
    }

    public BenchmarkToolsTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"benchmark_tools_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _metrics = new MetricsCollector();
        var embedding = new StubEmbeddingService();
        _runner = new BenchmarkRunner(_index, embedding);
        _outcomeRunner = new AgentOutcomeBenchmarkRunner(
            _index,
            embedding,
            new McpEngramMemory.Core.Services.Graph.KnowledgeGraph(_persistence, _index),
            new McpEngramMemory.Core.Services.Lifecycle.LifecycleEngine(_index, _persistence));
        _liveOutcomeRunner = new LiveAgentOutcomeBenchmarkRunner(
            _index,
            embedding,
            new McpEngramMemory.Core.Services.Graph.KnowledgeGraph(_persistence, _index),
            new McpEngramMemory.Core.Services.Lifecycle.LifecycleEngine(_index, _persistence));
        _tools = new BenchmarkTools(
            _runner,
            _outcomeRunner,
            _liveOutcomeRunner,
            new StubAgentOutcomeModelClientFactory(),
            _metrics);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

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

    [Fact]
    public void RunAgentOutcomeBenchmark_DefaultDataset_ReturnsResults()
    {
        var artifactRoot = Path.Combine(_testDataPath, "benchmarks");
        var result = _tools.RunAgentOutcomeBenchmark(artifactDirectory: artifactRoot);
        Assert.IsType<AgentOutcomeBenchmarkRunOutput>(result);

        var output = (AgentOutcomeBenchmarkRunOutput)result;
        Assert.Equal("completed", output.Status);
        Assert.NotNull(output.Result);
        Assert.NotNull(output.ArtifactPath);
        Assert.True(File.Exists(output.ArtifactPath));
        Assert.Equal("agent-outcome-v1", output.Result!.DatasetId);
        Assert.Equal(3, output.Result.Comparisons.Count);
        Assert.NotEmpty(output.Result.Baseline.TaskScores);
    }

    [Fact]
    public void RunAgentOutcomeBenchmark_InvalidDataset_ReturnsError()
    {
        var result = _tools.RunAgentOutcomeBenchmark("nonexistent-outcome-dataset");
        Assert.IsType<AgentOutcomeBenchmarkRunOutput>(result);

        var output = (AgentOutcomeBenchmarkRunOutput)result;
        Assert.Equal("error", output.Status);
        Assert.Null(output.Result);
        Assert.Contains("nonexistent-outcome-dataset", output.Message);
    }

    [Fact]
    public void RunAgentOutcomeBenchmark_PersistDisabled_SkipsArtifact()
    {
        var result = _tools.RunAgentOutcomeBenchmark(persistArtifact: false);

        Assert.Equal("completed", result.Status);
        Assert.NotNull(result.Result);
        Assert.Null(result.ArtifactPath);
        Assert.Equal("Artifact persistence disabled.", result.Message);
    }

    [Fact]
    public async Task RunLiveAgentOutcomeBenchmark_DefaultDataset_ReturnsResults()
    {
        var artifactRoot = Path.Combine(_testDataPath, "benchmarks-live");
        var result = await _tools.RunLiveAgentOutcomeBenchmark(
            model: "scripted",
            artifactDirectory: artifactRoot);

        Assert.Equal("completed", result.Status);
        Assert.NotNull(result.Result);
        Assert.NotNull(result.ArtifactPath);
        Assert.True(File.Exists(result.ArtifactPath));
        Assert.Equal("agent-outcome-v1", result.Result!.DatasetId);
        Assert.Equal(3, result.Result.Comparisons.Count);
        Assert.NotEmpty(result.Result.Baseline.TaskResults);
    }

    [Fact]
    public async Task RunLiveAgentOutcomeBenchmark_UnsupportedProvider_ReturnsError()
    {
        var result = await _tools.RunLiveAgentOutcomeBenchmark(
            model: "scripted",
            provider: "unsupported-provider");

        Assert.Equal("error", result.Status);
        Assert.Null(result.Result);
        Assert.Contains("Supported providers: ollama", result.Message);
    }

    [Fact]
    public void CompareLiveAgentOutcomeArtifacts_ReturnsStructuredDiff()
    {
        var artifactRoot = Path.Combine(_testDataPath, "compare");
        Directory.CreateDirectory(artifactRoot);

        string baselinePath = Path.Combine(artifactRoot, "baseline.json");
        string candidatePath = Path.Combine(artifactRoot, "candidate.json");

        File.WriteAllText(baselinePath, JsonSerializer.Serialize(CreateLiveArtifact(
            datasetId: "agent-outcome-hard-v1",
            model: "phi3.5:3.8b",
            fullEngramTaskPassed: false,
            fullEngramSuccess: 0.5f,
            fullEngramCoverage: 0.5f,
            fullEngramEvidence: ["hard-lock-order"])));

        File.WriteAllText(candidatePath, JsonSerializer.Serialize(CreateLiveArtifact(
            datasetId: "agent-outcome-hard-v1",
            model: "qwen2.5:7b",
            fullEngramTaskPassed: true,
            fullEngramSuccess: 1.0f,
            fullEngramCoverage: 1.0f,
            fullEngramEvidence: ["hard-lock-order", "hard-graph-snapshot"])));

        var result = _tools.CompareLiveAgentOutcomeArtifacts(baselinePath, candidatePath);

        Assert.Equal("completed", result.Status);
        Assert.NotNull(result.Report);
        Assert.Equal("agent-outcome-hard-v1", result.Report!.DatasetId);
        Assert.Empty(result.Report.RegressedTasks);
        Assert.Single(result.Report.ImprovedTasks);
        Assert.Equal("full_engram", result.Report.ImprovedTasks[0].Condition);
        Assert.Equal("hard-graph-inversion", result.Report.ImprovedTasks[0].TaskId);
        Assert.Contains("qwen2.5:7b", result.Report.Summary);
        var fullEngram = Assert.Single(result.Report.ConditionDiffs.Where(d => d.Condition == "full_engram"));
        Assert.True(fullEngram.SuccessDelta > 0.49f);
    }

    [Fact]
    public void CheckForRegression_ReportsRegressionCorrectly()
    {
        var artifactRoot = Path.Combine(_testDataPath, "regression-check");
        Directory.CreateDirectory(artifactRoot);

        string baselinePath = Path.Combine(artifactRoot, "baseline.json");
        string candidatePath = Path.Combine(artifactRoot, "candidate.json");

        File.WriteAllText(baselinePath, JsonSerializer.Serialize(CreateLiveArtifact(
            datasetId: "agent-outcome-hard-v1",
            model: "phi3.5:3.8b",
            fullEngramTaskPassed: true,
            fullEngramSuccess: 1.0f,
            fullEngramCoverage: 1.0f,
            fullEngramEvidence: ["hard-lock-order"])));

        File.WriteAllText(candidatePath, JsonSerializer.Serialize(CreateLiveArtifact(
            datasetId: "agent-outcome-hard-v1",
            model: "qwen2.5:7b",
            fullEngramTaskPassed: false,
            fullEngramSuccess: 0.5f,
            fullEngramCoverage: 0.5f,
            fullEngramEvidence: ["hard-lock-order"])));

        // Default thresholds (0.0)
        var result = _tools.CheckForRegression(baselinePath, candidatePath);
        Assert.Equal("regressed", result.Status);
        Assert.Contains("success score regressed by 50.00%", result.Message);

        // Relaxed thresholds
        var passedResult = _tools.CheckForRegression(baselinePath, candidatePath, successThreshold: 0.6f, passRateThreshold: 1.1f);
        Assert.Equal("passed", passedResult.Status);
    }

    [Fact]
    public void CompareLiveAgentOutcomeArtifacts_DatasetMismatch_ReturnsError()
    {
        var artifactRoot = Path.Combine(_testDataPath, "compare-mismatch");
        Directory.CreateDirectory(artifactRoot);

        string baselinePath = Path.Combine(artifactRoot, "baseline.json");
        string candidatePath = Path.Combine(artifactRoot, "candidate.json");

        File.WriteAllText(baselinePath, JsonSerializer.Serialize(CreateLiveArtifact(
            datasetId: "agent-outcome-v1",
            model: "phi3.5:3.8b",
            fullEngramTaskPassed: true,
            fullEngramSuccess: 1.0f,
            fullEngramCoverage: 1.0f,
            fullEngramEvidence: ["task-build-lock"])));

        File.WriteAllText(candidatePath, JsonSerializer.Serialize(CreateLiveArtifact(
            datasetId: "agent-outcome-hard-v1",
            model: "qwen2.5:7b",
            fullEngramTaskPassed: true,
            fullEngramSuccess: 1.0f,
            fullEngramCoverage: 1.0f,
            fullEngramEvidence: ["hard-lock-order", "hard-graph-snapshot"])));

        var result = _tools.CompareLiveAgentOutcomeArtifacts(baselinePath, candidatePath);

        Assert.Equal("error", result.Status);
        Assert.Null(result.Report);
        Assert.Contains("different datasets", result.Message);
    }

    [Fact]
    public void GetMetrics_NoData_ReturnsEmpty()
    {
        var result = _tools.GetMetrics();
        Assert.Empty(result);
    }

    [Fact]
    public void GetMetrics_AfterOperations_ReturnsData()
    {
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

        var searchResult = _tools.GetMetrics("search");
        Assert.Empty(searchResult);

        var storeResult = _tools.GetMetrics("store");
        Assert.Single(storeResult);
        Assert.Equal(1, storeResult[0].Count);
    }

    private static LiveAgentOutcomeBenchmarkResult CreateLiveArtifact(
        string datasetId,
        string model,
        bool fullEngramTaskPassed,
        float fullEngramSuccess,
        float fullEngramCoverage,
        IReadOnlyList<string> fullEngramEvidence)
    {
        var baselineTask = new LiveAgentOutcomeTaskResult(
            "hard-graph-inversion",
            0,
            0,
            0,
            0,
            false,
            100,
            true,
            true,
            Array.Empty<string>(),
            Array.Empty<string>(),
            "Insufficient context.",
            "{\"answer\":\"Insufficient context.\",\"evidence_ids\":[],\"insufficient_context\":true}");

        var transcriptTask = baselineTask with
        {
            RequiredCoverage = 0.5f,
            HelpfulCoverage = 1f,
            SuccessScore = 0.5f,
            InsufficientContext = false,
            ContextMemoryIds = ["hard-lock-order"],
            CitedMemoryIds = ["hard-lock-order"],
            Answer = "Snapshot shared state under the owning lock.",
            RawResponse = "{\"answer\":\"Snapshot shared state under the owning lock.\",\"evidence_ids\":[\"hard-lock-order\"],\"insufficient_context\":false}"
        };

        var fullEngramTask = baselineTask with
        {
            RequiredCoverage = fullEngramCoverage,
            HelpfulCoverage = 1f,
            SuccessScore = fullEngramSuccess,
            Passed = fullEngramTaskPassed,
            InsufficientContext = false,
            ContextMemoryIds = fullEngramEvidence.ToArray(),
            CitedMemoryIds = fullEngramEvidence.ToArray(),
            Answer = "Snapshot shared state under the owning lock and resolve dependent data outside it.",
            RawResponse = "{\"answer\":\"Snapshot shared state under the owning lock and resolve dependent data outside it.\",\"evidence_ids\":[\"hard-lock-order\",\"hard-graph-snapshot\"],\"insufficient_context\":false}"
        };

        var baselineCondition = new LiveAgentOutcomeConditionResult(
            "no_memory",
            [baselineTask],
            0,
            0,
            0,
            0,
            100,
            1);

        var transcriptCondition = new LiveAgentOutcomeConditionResult(
            "transcript_replay",
            [transcriptTask],
            transcriptTask.SuccessScore,
            transcriptTask.Passed ? 1 : 0,
            transcriptTask.RequiredCoverage,
            transcriptTask.ConflictRate,
            transcriptTask.LatencyMs,
            1);

        var vectorCondition = new LiveAgentOutcomeConditionResult(
            "vector_memory",
            [transcriptTask],
            transcriptTask.SuccessScore,
            transcriptTask.Passed ? 1 : 0,
            transcriptTask.RequiredCoverage,
            transcriptTask.ConflictRate,
            transcriptTask.LatencyMs,
            1);

        var fullEngramCondition = new LiveAgentOutcomeConditionResult(
            "full_engram",
            [fullEngramTask],
            fullEngramTask.SuccessScore,
            fullEngramTask.Passed ? 1 : 0,
            fullEngramTask.RequiredCoverage,
            fullEngramTask.ConflictRate,
            fullEngramTask.LatencyMs,
            1);

        return new LiveAgentOutcomeBenchmarkResult(
            datasetId,
            DateTimeOffset.Parse("2026-04-17T16:00:00Z"),
            "ollama",
            model,
            "http://localhost:11434",
            "no_memory",
            baselineCondition,
            [
                new LiveAgentOutcomeConditionComparison(
                    "transcript_replay",
                    transcriptCondition,
                    transcriptCondition.MeanSuccessScore,
                    transcriptCondition.PassRate,
                    transcriptCondition.MeanRequiredCoverage,
                    0,
                    0,
                    0),
                new LiveAgentOutcomeConditionComparison(
                    "vector_memory",
                    vectorCondition,
                    vectorCondition.MeanSuccessScore,
                    vectorCondition.PassRate,
                    vectorCondition.MeanRequiredCoverage,
                    0,
                    0,
                    0),
                new LiveAgentOutcomeConditionComparison(
                    "full_engram",
                    fullEngramCondition,
                    fullEngramCondition.MeanSuccessScore,
                    fullEngramCondition.PassRate,
                    fullEngramCondition.MeanRequiredCoverage,
                    0,
                    0,
                    0)
            ],
            "Synthetic compare artifact");
    }
}
