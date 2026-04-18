using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for benchmarking and operational metrics.
/// </summary>
[McpServerToolType]
public sealed class BenchmarkTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly BenchmarkRunner _runner;
    private readonly AgentOutcomeBenchmarkRunner _outcomeRunner;
    private readonly LiveAgentOutcomeBenchmarkRunner _liveOutcomeRunner;
    private readonly IAgentOutcomeModelClientFactory _modelClientFactory;
    private readonly MetricsCollector _metrics;

    public BenchmarkTools(
        BenchmarkRunner runner,
        AgentOutcomeBenchmarkRunner outcomeRunner,
        LiveAgentOutcomeBenchmarkRunner liveOutcomeRunner,
        IAgentOutcomeModelClientFactory modelClientFactory,
        MetricsCollector metrics)
    {
        _runner = runner;
        _outcomeRunner = outcomeRunner;
        _liveOutcomeRunner = liveOutcomeRunner;
        _modelClientFactory = modelClientFactory;
        _metrics = metrics;
    }

    [McpServerTool(Name = "run_benchmark")]
    [Description("Run an IR quality benchmark in an isolated namespace. Computes Recall@K, Precision@K, MRR, nDCG@K, and latency percentiles. Namespace is cleaned up after.")]
    public object RunBenchmark(
        [Description("Dataset ID: 'default-v1' (25 seeds, 20 queries), 'paraphrase-v1' (rephrased), 'multihop-v1' (cross-topic), 'scale-v1' (80 seeds stress test), 'realworld-v1' (cognitive patterns), 'compound-v1' (domain jargon), 'lifecycle-v1', 'adversarial-v1', 'accretion-v1', 'cluster-summary-v1'.")] string datasetId = "default-v1",
        [Description("Search mode: 'vector' (default), 'hybrid' (BM25+vector RRF fusion), 'vector_rerank' (vector + token reranker), 'hybrid_rerank' (hybrid + token reranker).")] string mode = "vector",
        [Description("When true, prepend category/namespace context to text before embedding (contextual retrieval). Default: false.")] bool contextualPrefix = false)
    {
        var dataset = BenchmarkRunner.CreateDataset(datasetId);
        if (dataset is null)
            return $"Error: Unknown dataset '{datasetId}'. Available: {string.Join(", ", BenchmarkRunner.GetAvailableDatasets())}";

        var searchMode = mode.ToLowerInvariant() switch
        {
            "hybrid" => BenchmarkRunner.SearchMode.Hybrid,
            "vector_rerank" or "vectorrerank" => BenchmarkRunner.SearchMode.VectorRerank,
            "hybrid_rerank" or "hybridrerank" => BenchmarkRunner.SearchMode.HybridRerank,
            _ => BenchmarkRunner.SearchMode.Vector
        };

        return _runner.Run(dataset, searchMode, contextualPrefix);
    }

    [McpServerTool(Name = "run_agent_outcome_benchmark")]
    [Description("Run a task-style benchmark across four memory conditions: no memory, transcript replay, vector memory, and full Engram memory. Scores task success, evidence coverage, conflict rate, and latency, and persists a JSON artifact by default.")]
    public AgentOutcomeBenchmarkRunOutput RunAgentOutcomeBenchmark(
        [Description("Dataset ID. Core: 'agent-outcome-v1', 'agent-outcome-repo-v1', 'agent-outcome-hard-v1', 'agent-outcome-reasoning-v1'. T2 intelligence: 'reasoning-ladder-v1' (hop-depth 1-4), 'contradiction-arena-v1' (old/new fact pairs), 'adversarial-retrieval-v1' (distractor/synonym/stale traps), 'counterfactual-v1' (what-breaks-if dependency reasoning).")] string datasetId = "agent-outcome-v1",
        [Description("When true, prepend category context before embedding seed memories. Default: false.")] bool contextualPrefix = false,
        [Description("When true, write the benchmark result to a JSON artifact under benchmarks/YYYY-MM-DD. Default: true.")] bool persistArtifact = true,
        [Description("Optional artifact root directory. Defaults to BENCHMARK_ARTIFACTS_PATH env var or ./benchmarks.")] string? artifactDirectory = null)
    {
        var dataset = AgentOutcomeBenchmarkRunner.CreateDataset(datasetId);
        if (dataset is null)
        {
            return new AgentOutcomeBenchmarkRunOutput(
                "error",
                null,
                null,
                $"Unknown agent-outcome dataset '{datasetId}'. Available: {string.Join(", ", AgentOutcomeBenchmarkRunner.GetAvailableDatasets())}");
        }

        var result = _outcomeRunner.Run(dataset, contextualPrefix);
        if (!persistArtifact)
            return new AgentOutcomeBenchmarkRunOutput("completed", result, null, "Artifact persistence disabled.");

        try
        {
            var artifactPath = PersistAgentOutcomeArtifact(result, artifactDirectory);
            return new AgentOutcomeBenchmarkRunOutput("completed", result, artifactPath, null);
        }
        catch (Exception ex)
        {
            return new AgentOutcomeBenchmarkRunOutput(
                "completed_with_warning",
                result,
                null,
                $"Benchmark completed but artifact persistence failed: {ex.Message}");
        }
    }

    [McpServerTool(Name = "run_live_agent_outcome_benchmark")]
    [Description("Run a real generation model across no-memory, transcript replay, vector memory, and full Engram conditions. Optionally includes T2 ablation conditions (no_graph, no_lifecycle, no_hybrid) for per-module attribution. Requires a live provider. The first supported provider is Ollama. Scores task success plus T2 intelligence metrics (ReasoningPathValidity, ContradictionHandling, NoiseResistance, StaleMemoryPenalty, DependencyCompletion, MinimalEvidence) from model-cited memory IDs and persists a JSON artifact by default.")]
    public async Task<LiveAgentOutcomeBenchmarkRunOutput> RunLiveAgentOutcomeBenchmark(
        [Description("Generation model name exposed by the live provider. Required. Example for Ollama: 'qwen2.5:3b'.")] string model,
        [Description("Dataset ID. Core: 'agent-outcome-v1', 'agent-outcome-repo-v1', 'agent-outcome-hard-v1', 'agent-outcome-reasoning-v1'. T2 intelligence: 'reasoning-ladder-v1', 'contradiction-arena-v1', 'adversarial-retrieval-v1', 'counterfactual-v1'.")] string datasetId = "agent-outcome-v1",
        [Description("Live provider. Supported: 'ollama'.")] string provider = "ollama",
        [Description("Optional provider endpoint. Defaults to OLLAMA_URL or http://localhost:11434 for Ollama.")] string? endpoint = null,
        [Description("When true, prepend category context before embedding seed memories. Default: false.")] bool contextualPrefix = false,
        [Description("Maximum completion tokens per task. Default: 320.")] int maxTokens = 320,
        [Description("Sampling temperature for the live model. Default: 0.1.")] float temperature = 0.1f,
        [Description("When true, also run three T2 ablation conditions against the live model (full_engram_no_graph, full_engram_no_lifecycle, full_engram_no_hybrid). Triples the generation cost. Default: false.")] bool runAblations = false,
        [Description("When true, write the benchmark result to a JSON artifact under benchmarks/YYYY-MM-DD. Default: true.")] bool persistArtifact = true,
        [Description("Optional artifact root directory. Defaults to BENCHMARK_ARTIFACTS_PATH env var or ./benchmarks.")] string? artifactDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return new LiveAgentOutcomeBenchmarkRunOutput(
                "error",
                null,
                null,
                "Model is required for run_live_agent_outcome_benchmark.");
        }

        var dataset = AgentOutcomeBenchmarkRunner.CreateDataset(datasetId);
        if (dataset is null)
        {
            return new LiveAgentOutcomeBenchmarkRunOutput(
                "error",
                null,
                null,
                $"Unknown agent-outcome dataset '{datasetId}'. Available: {string.Join(", ", AgentOutcomeBenchmarkRunner.GetAvailableDatasets())}");
        }

        try
        {
            using var client = _modelClientFactory.Create(provider, endpoint);
            bool available = await client.IsAvailableAsync(model, cancellationToken);
            if (!available)
            {
                return new LiveAgentOutcomeBenchmarkRunOutput(
                    "error",
                    null,
                    null,
                    $"Model '{model}' is not available via provider '{provider}'.");
            }

            var options = new LiveAgentOutcomeGenerationOptions(
                provider,
                model,
                endpoint,
                contextualPrefix,
                maxTokens,
                temperature,
                runAblations);

            var result = await _liveOutcomeRunner.RunAsync(dataset, options, client, cancellationToken);
            if (!persistArtifact)
                return new LiveAgentOutcomeBenchmarkRunOutput("completed", result, null, "Artifact persistence disabled.");

            try
            {
                var artifactPath = PersistLiveAgentOutcomeArtifact(result, artifactDirectory);
                return new LiveAgentOutcomeBenchmarkRunOutput("completed", result, artifactPath, null);
            }
            catch (Exception ex)
            {
                return new LiveAgentOutcomeBenchmarkRunOutput(
                    "completed_with_warning",
                    result,
                    null,
                    $"Live benchmark completed but artifact persistence failed: {ex.Message}");
            }
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return new LiveAgentOutcomeBenchmarkRunOutput("error", null, null, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return new LiveAgentOutcomeBenchmarkRunOutput(
                "error",
                null,
                null,
                "Live benchmark canceled.");
        }
        catch (Exception ex)
        {
            return new LiveAgentOutcomeBenchmarkRunOutput(
                "error",
                null,
                null,
                $"Live benchmark failed: {ex.Message}");
        }
    }

    [McpServerTool(Name = "compare_live_agent_outcome_artifacts")]
    [Description("Compare two JSON artifacts produced by run_live_agent_outcome_benchmark. Returns condition-level deltas plus per-task improvements and regressions for the same dataset.")]
    public LiveAgentOutcomeArtifactComparisonOutput CompareLiveAgentOutcomeArtifacts(
        [Description("Baseline artifact path produced by run_live_agent_outcome_benchmark.")] string baselineArtifactPath,
        [Description("Candidate artifact path produced by run_live_agent_outcome_benchmark.")] string candidateArtifactPath)
    {
        if (string.IsNullOrWhiteSpace(baselineArtifactPath))
        {
            return new LiveAgentOutcomeArtifactComparisonOutput(
                "error",
                null,
                "Baseline artifact path is required.");
        }

        if (string.IsNullOrWhiteSpace(candidateArtifactPath))
        {
            return new LiveAgentOutcomeArtifactComparisonOutput(
                "error",
                null,
                "Candidate artifact path is required.");
        }

        try
        {
            var report = LiveAgentOutcomeBenchmarkComparer.CompareArtifacts(baselineArtifactPath, candidateArtifactPath);
            return new LiveAgentOutcomeArtifactComparisonOutput("completed", report, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or InvalidOperationException or JsonException or ArgumentException)
        {
            return new LiveAgentOutcomeArtifactComparisonOutput("error", null, ex.Message);
        }
    }

    [McpServerTool(Name = "check_for_regression")]
    [Description("Check for benchmark regressions between two artifacts. Returns 'error' status if full_engram pass rate or success score regresses beyond the allowed threshold.")]
    public LiveAgentOutcomeArtifactComparisonOutput CheckForRegression(
        [Description("Baseline artifact path.")] string baselineArtifactPath,
        [Description("Candidate artifact path.")] string candidateArtifactPath,
        [Description("Allowed success score regression (e.g. 0.01 for 1% drop). Default is 0.0.")] float successThreshold = 0.0f,
        [Description("Allowed pass rate regression (e.g. 0.05 for 5% drop). Default is 0.0.")] float passRateThreshold = 0.0f)
    {
        var comparison = CompareLiveAgentOutcomeArtifacts(baselineArtifactPath, candidateArtifactPath);
        if (comparison.Status == "error") return comparison;

        var report = comparison.Report!;
        var fullEngramDiff = report.ConditionDiffs.FirstOrDefault(d => d.Condition == "full_engram");

        if (fullEngramDiff is null)
        {
            return new LiveAgentOutcomeArtifactComparisonOutput(
                "error",
                report,
                "Comparison missing 'full_engram' condition results.");
        }

        if (fullEngramDiff.SuccessDelta < -successThreshold)
        {
            return new LiveAgentOutcomeArtifactComparisonOutput(
                "regressed",
                report,
                $"Full Engram success score regressed by {Math.Abs(fullEngramDiff.SuccessDelta):P2} (allowed: {successThreshold:P2}).");
        }

        if (fullEngramDiff.PassRateDelta < -passRateThreshold)
        {
            return new LiveAgentOutcomeArtifactComparisonOutput(
                "regressed",
                report,
                $"Full Engram pass rate regressed by {Math.Abs(fullEngramDiff.PassRateDelta):P2} (allowed: {passRateThreshold:P2}).");
        }

        return new LiveAgentOutcomeArtifactComparisonOutput("passed", report, "No regressions detected.");
    }

    [McpServerTool(Name = "get_metrics")]
    [Description("Get operational metrics: P50/P95/P99 latency, throughput, and call counts per operation type.")]
    public IReadOnlyList<MetricsSummary> GetMetrics(
        [Description("Operation type to filter (e.g. 'search', 'store'). Leave empty for all.")] string? operationType = null)
    {
        if (operationType is not null)
        {
            var summary = _metrics.GetSummary(operationType);
            return summary.Count > 0 ? new[] { summary } : Array.Empty<MetricsSummary>();
        }
        return _metrics.GetAllSummaries();
    }

    [McpServerTool(Name = "reset_metrics")]
    [Description("Reset operational metrics counters. Optionally filter by operation type.")]
    public string ResetMetrics(
        [Description("Operation type to reset. Leave empty to reset all.")] string? operationType = null)
    {
        _metrics.Reset(operationType);
        return operationType is not null
            ? $"Reset metrics for '{operationType}'."
            : "All metrics reset.";
    }

    private static string PersistAgentOutcomeArtifact(AgentOutcomeBenchmarkResult result, string? artifactDirectory)
    {
        string root = ResolveArtifactRoot(artifactDirectory);

        string datedDir = Path.Combine(root, $"{result.RunAt:yyyy-MM-dd}");
        Directory.CreateDirectory(datedDir);

        string path = Path.Combine(datedDir, $"{result.DatasetId}-agent-outcome.json");
        File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions));
        return Path.GetFullPath(path);
    }

    private static string PersistLiveAgentOutcomeArtifact(LiveAgentOutcomeBenchmarkResult result, string? artifactDirectory)
    {
        string root = ResolveArtifactRoot(artifactDirectory);
        string datedDir = Path.Combine(root, $"{result.RunAt:yyyy-MM-dd}");
        Directory.CreateDirectory(datedDir);

        string providerSegment = SanitizeArtifactSegment(result.Provider);
        string modelSegment = SanitizeArtifactSegment(result.Model);
        string path = Path.Combine(
            datedDir,
            $"{result.DatasetId}-live-agent-outcome-{providerSegment}-{modelSegment}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions));
        return Path.GetFullPath(path);
    }

    private static string ResolveArtifactRoot(string? artifactDirectory)
        => artifactDirectory
            ?? Environment.GetEnvironmentVariable("BENCHMARK_ARTIFACTS_PATH")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "benchmarks");

    private static string SanitizeArtifactSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "default";

        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(value.Length);

        foreach (char ch in value.Trim().ToLowerInvariant())
        {
            if (invalid.Contains(ch) || ch == ':' || char.IsWhiteSpace(ch))
                builder.Append('-');
            else
                builder.Append(ch);
        }

        var sanitized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }
}

public sealed record AgentOutcomeBenchmarkRunOutput(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("result")] AgentOutcomeBenchmarkResult? Result,
    [property: JsonPropertyName("artifactPath")] string? ArtifactPath,
    [property: JsonPropertyName("message")] string? Message);

public sealed record LiveAgentOutcomeBenchmarkRunOutput(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("result")] LiveAgentOutcomeBenchmarkResult? Result,
    [property: JsonPropertyName("artifactPath")] string? ArtifactPath,
    [property: JsonPropertyName("message")] string? Message);

public sealed record LiveAgentOutcomeArtifactComparisonOutput(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("report")] LiveAgentOutcomeBenchmarkDiffReport? Report,
    [property: JsonPropertyName("message")] string? Message);
