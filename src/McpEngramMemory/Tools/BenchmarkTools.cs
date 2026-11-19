using System.ComponentModel;
using System.Net;
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

    [McpServerTool(Name = "run_benchmark", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
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

    [McpServerTool(Name = "run_agent_outcome_benchmark", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
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

        string? validatedArtifactRoot = null;
        if (persistArtifact || artifactDirectory is not null)
        {
            try
            {
                validatedArtifactRoot = BenchmarkSecurityGuard.ResolveArtifactRoot(artifactDirectory);
            }
            catch (ArgumentException ex)
            {
                return new AgentOutcomeBenchmarkRunOutput("error", null, null, ex.Message);
            }
        }

        var result = _outcomeRunner.Run(dataset, contextualPrefix);
        if (!persistArtifact)
            return new AgentOutcomeBenchmarkRunOutput("completed", result, null, "Artifact persistence disabled.");

        try
        {
            var artifactPath = PersistAgentOutcomeArtifact(result, validatedArtifactRoot!);
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

    [McpServerTool(Name = "run_live_agent_outcome_benchmark", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
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

        string? validatedArtifactRoot = null;
        string? validatedEndpoint;
        try
        {
            validatedEndpoint = BenchmarkSecurityGuard.ValidateProviderEndpoint(provider, endpoint);
            if (persistArtifact || artifactDirectory is not null)
                validatedArtifactRoot = BenchmarkSecurityGuard.ResolveArtifactRoot(artifactDirectory);
        }
        catch (ArgumentException ex)
        {
            return new LiveAgentOutcomeBenchmarkRunOutput("error", null, null, ex.Message);
        }

        try
        {
            using var client = _modelClientFactory.Create(provider, validatedEndpoint);
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
                validatedEndpoint,
                contextualPrefix,
                maxTokens,
                temperature,
                runAblations);

            var result = await _liveOutcomeRunner.RunAsync(dataset, options, client, cancellationToken);
            if (!persistArtifact)
                return new LiveAgentOutcomeBenchmarkRunOutput("completed", result, null, "Artifact persistence disabled.");

            try
            {
                var artifactPath = PersistLiveAgentOutcomeArtifact(result, validatedArtifactRoot!);
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
        catch (ArgumentException ex)
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

    [McpServerTool(Name = "compare_live_agent_outcome_artifacts", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
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
            var resolvedBaseline = BenchmarkSecurityGuard.ValidateReadableFilePath(
                baselineArtifactPath, nameof(baselineArtifactPath));
            var resolvedCandidate = BenchmarkSecurityGuard.ValidateReadableFilePath(
                candidateArtifactPath, nameof(candidateArtifactPath));
            var report = LiveAgentOutcomeBenchmarkComparer.CompareArtifacts(resolvedBaseline, resolvedCandidate);
            return new LiveAgentOutcomeArtifactComparisonOutput("completed", report, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or InvalidOperationException or JsonException or ArgumentException)
        {
            return new LiveAgentOutcomeArtifactComparisonOutput("error", null, ex.Message);
        }
    }

    [McpServerTool(Name = "check_for_regression", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
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
                FormattableString.Invariant(
                    $"Full Engram success score regressed by {Math.Abs(fullEngramDiff.SuccessDelta) * 100:F2}% (allowed: {successThreshold * 100:F2}%)."));
        }

        if (fullEngramDiff.PassRateDelta < -passRateThreshold)
        {
            return new LiveAgentOutcomeArtifactComparisonOutput(
                "regressed",
                report,
                FormattableString.Invariant(
                    $"Full Engram pass rate regressed by {Math.Abs(fullEngramDiff.PassRateDelta) * 100:F2}% (allowed: {passRateThreshold * 100:F2}%)."));
        }

        return new LiveAgentOutcomeArtifactComparisonOutput("passed", report, "No regressions detected.");
    }

    [McpServerTool(Name = "get_metrics", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Get operational metrics: P50/P95/P99 latency, throughput, and call counts per operation type. Event-style counters appear as zero-duration metric rows where Count is the useful value.")]
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

    [McpServerTool(Name = "reset_metrics", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Reset operational metrics counters. Optionally filter by operation type.")]
    public string ResetMetrics(
        [Description("Operation type to reset. Leave empty to reset all.")] string? operationType = null)
    {
        _metrics.Reset(operationType);
        return operationType is not null
            ? $"Reset metrics for '{operationType}'."
            : "All metrics reset.";
    }

    private static string PersistAgentOutcomeArtifact(AgentOutcomeBenchmarkResult result, string validatedRoot)
    {
        string datedDir = Path.Combine(validatedRoot, $"{result.RunAt:yyyy-MM-dd}");
        Directory.CreateDirectory(datedDir);

        string datasetSegment = BenchmarkSecurityGuard.ValidateDatasetId(result.DatasetId);
        string path = Path.Combine(datedDir, $"{datasetSegment}-agent-outcome.json");
        BenchmarkSecurityGuard.EnsurePathWithinDirectory(path, datedDir);
        File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions));
        return Path.GetFullPath(path);
    }

    private static string PersistLiveAgentOutcomeArtifact(LiveAgentOutcomeBenchmarkResult result, string validatedRoot)
    {
        string datedDir = Path.Combine(validatedRoot, $"{result.RunAt:yyyy-MM-dd}");
        Directory.CreateDirectory(datedDir);

        string datasetSegment = BenchmarkSecurityGuard.ValidateDatasetId(result.DatasetId);
        string providerSegment = SanitizeArtifactSegment(result.Provider);
        string modelSegment = SanitizeArtifactSegment(result.Model);
        string path = Path.Combine(
            datedDir,
            $"{datasetSegment}-live-agent-outcome-{providerSegment}-{modelSegment}.json");
        BenchmarkSecurityGuard.EnsurePathWithinDirectory(path, datedDir);
        File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions));
        return Path.GetFullPath(path);
    }

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

/// <summary>
/// Input boundary validation for full-profile benchmark MCP tools.
/// Blocks arbitrary CLI execution, Ollama SSRF, and path traversal / arbitrary file read-write.
/// </summary>
internal static class BenchmarkSecurityGuard
{
    internal const string AllowCustomEndpointEnvVar = "ENGRAM_ALLOW_CUSTOM_BENCHMARK_ENDPOINT";
    internal const string AllowCustomPathsEnvVar = "ENGRAM_ALLOW_CUSTOM_BENCHMARK_PATHS";

    private static bool IsEnvFlagEnabled(string envVarName)
    {
        var raw = Environment.GetEnvironmentVariable(envVarName);
        return string.Equals(raw?.Trim(), "1", StringComparison.Ordinal)
            || string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ValidateProviderEndpoint(string provider, string? endpoint)
    {
        var normalized = provider?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "ollama" => ValidateOllamaEndpoint(endpoint),
            "claude-cli" or "claude_cli" or "claudecli" => ValidateCliEndpoint(provider!, endpoint, ClaudeCliModelClient.DefaultExecutable),
            "codex-cli" or "codex_cli" or "codexcli" => ValidateCliEndpoint(provider!, endpoint, CodexCliModelClient.DefaultExecutable),
            "gemini-cli" or "gemini_cli" or "geminicli" => ValidateCliEndpoint(provider!, endpoint, GeminiCliModelClient.DefaultExecutable),
            _ => endpoint
        };
    }

    private static string? ValidateOllamaEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        var configuredOllamaUrl = Environment.GetEnvironmentVariable("OLLAMA_URL");
        string trimmed = endpoint.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            throw new ArgumentException("Invalid Ollama endpoint URL.", nameof(endpoint));

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Ollama endpoint must use http or https.", nameof(endpoint));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Ollama endpoint must not contain userinfo credentials.", nameof(endpoint));

        if (!IsEnvFlagEnabled(AllowCustomEndpointEnvVar) &&
            !IsLoopbackHost(uri) &&
            !MatchesConfiguredOllamaHost(uri, configuredOllamaUrl))
        {
            throw new ArgumentException(
                $"Ollama endpoint host '{uri.IdnHost}' is not permitted. Only loopback hosts (localhost, 127.0.0.1, ::1) or the operator-configured OLLAMA_URL host are allowed unless {AllowCustomEndpointEnvVar}=1 is set.",
                nameof(endpoint));
        }

        return trimmed;
    }

    private static string? ValidateCliEndpoint(string provider, string? endpoint, string canonicalExecutable)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        string trimmed = endpoint.Trim();
        if (trimmed.Any(char.IsControl))
            throw new ArgumentException("Endpoint must not contain control characters.", nameof(endpoint));

        if (IsEnvFlagEnabled(AllowCustomEndpointEnvVar))
            return trimmed;

        if (string.Equals(trimmed, canonicalExecutable, StringComparison.OrdinalIgnoreCase))
            return canonicalExecutable;

        throw new ArgumentException(
            $"Custom CLI executable endpoint is not permitted for provider '{provider}'. Only the canonical '{canonicalExecutable}' CLI is allowed unless {AllowCustomEndpointEnvVar}=1 is set.",
            nameof(endpoint));
    }

    internal static string ValidateDatasetId(string datasetId)
    {
        if (string.IsNullOrWhiteSpace(datasetId))
            throw new ArgumentException("Dataset ID must not be empty.", nameof(datasetId));

        string trimmed = datasetId.Trim();
        if (trimmed is "." or ".." ||
            trimmed.Contains("..", StringComparison.Ordinal) ||
            trimmed.IndexOfAny(['/', '\\', ':']) >= 0 ||
            trimmed.Any(char.IsControl) ||
            trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                "Dataset ID must be a single safe filename segment without path separators or '..'.",
                nameof(datasetId));
        }

        return trimmed;
    }

    internal static string ResolveArtifactRoot(string? artifactDirectory)
    {
        if (string.IsNullOrWhiteSpace(artifactDirectory))
        {
            string defaultRoot = Environment.GetEnvironmentVariable("BENCHMARK_ARTIFACTS_PATH")
                ?? Path.Combine(Directory.GetCurrentDirectory(), "benchmarks");
            return Path.GetFullPath(defaultRoot);
        }

        return ValidateConfinedPath(artifactDirectory, nameof(artifactDirectory), includeDatasetRoots: false);
    }

    internal static string ValidateReadableFilePath(
        string path,
        string parameterName,
        bool includeDatasetRoots = false)
        => ValidateConfinedPath(path, parameterName, includeDatasetRoots);

    internal static void EnsurePathWithinDirectory(string candidatePath, string parentDirectory)
    {
        string fullCandidate = Path.GetFullPath(candidatePath);
        string fullParent = Path.GetFullPath(parentDirectory);
        if (!IsWithinRoot(fullCandidate, fullParent))
        {
            throw new ArgumentException(
                "Resolved artifact path escapes the target artifact directory.",
                nameof(candidatePath));
        }
    }

    private static string ValidateConfinedPath(string rawPath, string parameterName, bool includeDatasetRoots)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            throw new ArgumentException($"Path '{parameterName}' must not be empty.", parameterName);

        string trimmed = rawPath.Trim();
        if (trimmed.Any(char.IsControl))
            throw new ArgumentException($"Path '{parameterName}' must not contain control characters.", parameterName);

        if (ContainsParentTraversal(trimmed))
            throw new ArgumentException($"Path '{parameterName}' must not contain '..' traversal segments.", parameterName);

        // Reject NTFS Alternate Data Streams (':' outside the Windows drive specifier).
        int colonIndex = trimmed.IndexOf(':');
        if (colonIndex >= 0)
        {
            bool validWindowsDrive = OperatingSystem.IsWindows()
                && colonIndex == 1
                && char.IsAsciiLetter(trimmed[0])
                && trimmed.IndexOf(':', 2) < 0;
            if (!validWindowsDrive)
                throw new ArgumentException($"Path '{parameterName}' contains an invalid ':' character.", parameterName);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"Path '{parameterName}' is not a valid filesystem path.", parameterName, ex);
        }

        if (IsEnvFlagEnabled(AllowCustomPathsEnvVar))
            return fullPath;

        var allowedRoots = GetAllowedRoots(includeDatasetRoots);
        if (!IsWithinAnyRoot(fullPath, allowedRoots))
        {
            throw new ArgumentException(
                $"Path '{parameterName}' must stay within the configured benchmarks or temp directory. Set {AllowCustomPathsEnvVar}=1 to permit custom paths.",
                parameterName);
        }

        // Guard against symlink escapes out of the allowed root set, including symlinks on
        // existing ancestor directories of a not-yet-created target path.
        ValidateNoEscapingSymlink(fullPath, allowedRoots, parameterName);

        return fullPath;
    }

    private static void ValidateNoEscapingSymlink(string fullPath, IReadOnlyList<string> allowedRoots, string parameterName)
    {
        string? current = Path.TrimEndingDirectorySeparator(fullPath);
        while (!string.IsNullOrEmpty(current) && IsWithinAnyRoot(current, allowedRoots))
        {
            FileSystemInfo? target = null;
            if (File.Exists(current))
                target = File.ResolveLinkTarget(current, returnFinalTarget: true);
            else if (Directory.Exists(current))
                target = Directory.ResolveLinkTarget(current, returnFinalTarget: true);

            if (target is not null && !IsWithinAnyRoot(target.FullName, allowedRoots))
            {
                throw new ArgumentException(
                    $"Path '{parameterName}' resolves via symlink outside allowed benchmark roots.",
                    parameterName);
            }

            // Stop once we have checked an allowed root itself so OS-level symlinks above the root
            // (e.g., macOS /var -> /private/var) do not produce false positives.
            if (IsExactRoot(current, allowedRoots))
                break;

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                break;
            current = Path.TrimEndingDirectorySeparator(parent);
        }
    }

    private static bool IsExactRoot(string normalizedPath, IReadOnlyList<string> roots)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var root in roots)
        {
            if (string.Equals(normalizedPath, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), comparison))
                return true;
        }
        return false;
    }

    private static bool ContainsParentTraversal(string path)
    {
        foreach (var segment in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
                return true;
        }
        return false;
    }

    private static List<string> GetAllowedRoots(bool includeDatasetRoots)
    {
        var roots = new List<string>();

        static void AddRoot(List<string> list, string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            try
            {
                string full = Path.GetFullPath(candidate.Trim());
                list.Add(full);
                if (Directory.Exists(full))
                {
                    var target = Directory.ResolveLinkTarget(full, returnFinalTarget: true);
                    if (target is not null)
                        list.Add(target.FullName);
                }
            }
            catch
            {
                // Ignore malformed environment paths when constructing allowlist.
            }
        }

        AddRoot(roots, Path.Combine(Directory.GetCurrentDirectory(), "benchmarks"));
        AddRoot(roots, Environment.GetEnvironmentVariable("BENCHMARK_ARTIFACTS_PATH"));
        AddRoot(roots, Path.GetTempPath());

        // When invoked from a nested working directory (e.g. dotnet test bin/...),
        // also permit the repository's top-level benchmarks directory if present.
        AddRoot(roots, TryFindRepoBenchmarksRoot(Directory.GetCurrentDirectory()));
        AddRoot(roots, TryFindRepoBenchmarksRoot(AppContext.BaseDirectory));

        if (includeDatasetRoots)
        {
            var mrcrEnv = Environment.GetEnvironmentVariable("MRCR_DATASET_PATH");
            if (!string.IsNullOrWhiteSpace(mrcrEnv))
            {
                try
                {
                    string mrcrFull = Path.GetFullPath(mrcrEnv.Trim());
                    AddRoot(roots, Directory.Exists(mrcrFull) ? mrcrFull : Path.GetDirectoryName(mrcrFull));
                }
                catch
                {
                    // Ignore malformed env path.
                }
            }
        }

        return roots;
    }

    private static string? TryFindRepoBenchmarksRoot(string startDir)
    {
        try
        {
            var dir = new DirectoryInfo(startDir);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "McpEngramMemory.slnx")))
                    return Path.Combine(dir.FullName, "benchmarks");
                dir = dir.Parent;
            }
        }
        catch
        {
            // Ignore IO errors while probing ancestors.
        }
        return null;
    }

    private static bool IsWithinAnyRoot(string fullPath, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            if (IsWithinRoot(fullPath, root))
                return true;
        }
        return false;
    }

    private static bool IsWithinRoot(string fullPath, string rootPath)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));

        if (string.Equals(normalizedPath, normalizedRoot, comparison))
            return true;

        string prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, comparison);
    }

    private static bool IsLoopbackHost(Uri uri)
    {
        string host = uri.IdnHost;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();
            return IPAddress.IsLoopback(ip);
        }

        return false;
    }

    private static bool MatchesConfiguredOllamaHost(Uri candidate, string? configuredOllamaUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredOllamaUrl))
            return false;

        if (!Uri.TryCreate(configuredOllamaUrl.Trim(), UriKind.Absolute, out var configuredUri))
            return false;

        if (!string.Equals(configuredUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(configuredUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(candidate.IdnHost, configuredUri.IdnHost, StringComparison.OrdinalIgnoreCase);
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
