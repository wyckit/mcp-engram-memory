using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Evaluation;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for the MRCR v2 (8-needle) long-context A/B benchmark.
/// </summary>
[McpServerToolType]
public sealed class MrcrBenchmarkTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly MrcrBenchmarkRunner _runner;
    private readonly IAgentOutcomeModelClientFactory _modelClientFactory;

    public MrcrBenchmarkTools(
        MrcrBenchmarkRunner runner,
        IAgentOutcomeModelClientFactory modelClientFactory)
    {
        _runner = runner;
        _modelClientFactory = modelClientFactory;
    }

    [McpServerTool(Name = "run_mrcr_benchmark")]
    [Description("Run the MRCR v2 (8-needle) long-context A/B benchmark. Two arms: 'full_context' stuffs the entire conversation into the prompt; 'engram_retrieval' ingests each turn into a scratch namespace and feeds only top-K hybrid-search snippets to the model. Scoring is mean cosine similarity via the local ONNX embedding model. Drives generation through the Claude Code CLI (`claude -p`) by default so it charges against the Claude subscription rather than the API.")]
    public async Task<MrcrBenchmarkRunOutput> RunMrcrBenchmark(
        [Description("Generation model name. For Claude CLI use 'opus', 'sonnet', or 'haiku'. For Ollama, e.g. 'qwen2.5:7b'.")] string model,
        [Description("Dataset identifier (used for artifact naming). Default: 'mrcr-v2-8needle'.")] string datasetId = "mrcr-v2-8needle",
        [Description("Path to the MRCR JSONL dataset. Defaults to MRCR_DATASET_PATH env var or benchmarks/datasets/mrcr-v2/mrcr_v2_8needle.jsonl.")] string? datasetPath = null,
        [Description("Live provider: 'claude-cli' (default, uses subscription) or 'ollama'.")] string provider = "claude-cli",
        [Description("Optional provider endpoint. For claude-cli, this overrides the CLI executable path.")] string? endpoint = null,
        [Description("Max number of probes to run. Default: 25 (pilot). Set 0 for the full dataset.")] int limit = 25,
        [Description("Top-K snippets retrieved by the engram arm. Default: 8.")] int topK = 8,
        [Description("Max completion tokens per probe. Default: 512.")] int maxTokens = 512,
        [Description("Sampling temperature. Default: 0.0 for deterministic recall.")] float temperature = 0.0f,
        [Description("Hard cap on prompt tokens for the full_context arm (skips probes over the cap). Default: 131072.")] int maxContextTokens = 131072,
        [Description("When true, run the full_context baseline arm. Default: true.")] bool runFullContextArm = true,
        [Description("When true, run the engram_retrieval arm. Default: true.")] bool runEngramArm = true,
        [Description("When true, write the benchmark result to a JSON artifact under benchmarks/YYYY-MM-DD. Default: true.")] bool persistArtifact = true,
        [Description("Optional artifact root directory. Defaults to BENCHMARK_ARTIFACTS_PATH env var or ./benchmarks.")] string? artifactDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
            return new MrcrBenchmarkRunOutput("error", null, null, "Model is required for run_mrcr_benchmark.");

        IReadOnlyList<MrcrTask> tasks;
        try
        {
            var resolvedPath = MrcrDatasetLoader.ResolveDefaultPath(datasetPath);
            tasks = MrcrDatasetLoader.Load(resolvedPath, limit > 0 ? limit : null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            return new MrcrBenchmarkRunOutput("error", null, null, ex.Message);
        }

        if (tasks.Count == 0)
            return new MrcrBenchmarkRunOutput("error", null, null, "MRCR dataset contains no tasks.");

        try
        {
            using var client = _modelClientFactory.Create(provider, endpoint);
            bool available = await client.IsAvailableAsync(model, cancellationToken);
            if (!available)
                return new MrcrBenchmarkRunOutput("error", null, null,
                    $"Provider '{provider}' reported model '{model}' is not available.");

            var options = new MrcrGenerationOptions(
                provider, model, endpoint,
                limit, topK, maxTokens, temperature, maxContextTokens,
                runFullContextArm, runEngramArm);

            var result = await _runner.RunAsync(datasetId, tasks, options, client, cancellationToken);

            if (!persistArtifact)
                return new MrcrBenchmarkRunOutput("completed", result, null, "Artifact persistence disabled.");

            try
            {
                string artifactPath = PersistArtifact(result, artifactDirectory);
                return new MrcrBenchmarkRunOutput("completed", result, artifactPath, null);
            }
            catch (Exception ex)
            {
                return new MrcrBenchmarkRunOutput("completed_with_warning", result, null,
                    $"Benchmark completed but artifact persistence failed: {ex.Message}");
            }
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return new MrcrBenchmarkRunOutput("error", null, null, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return new MrcrBenchmarkRunOutput("error", null, null, "MRCR benchmark canceled.");
        }
        catch (Exception ex)
        {
            return new MrcrBenchmarkRunOutput("error", null, null, $"MRCR benchmark failed: {ex.Message}");
        }
    }

    [McpServerTool(Name = "compare_mrcr_artifacts")]
    [Description("Compare two MRCR artifacts produced by run_mrcr_benchmark. Returns per-arm similarity and pass-rate deltas plus the change in prompt-token reduction ratio.")]
    public MrcrArtifactComparisonOutput CompareMrcrArtifacts(
        [Description("Baseline MRCR artifact path.")] string baselineArtifactPath,
        [Description("Candidate MRCR artifact path.")] string candidateArtifactPath)
    {
        if (string.IsNullOrWhiteSpace(baselineArtifactPath))
            return new MrcrArtifactComparisonOutput("error", null, "Baseline artifact path is required.");
        if (string.IsNullOrWhiteSpace(candidateArtifactPath))
            return new MrcrArtifactComparisonOutput("error", null, "Candidate artifact path is required.");

        try
        {
            var report = MrcrBenchmarkComparer.CompareArtifacts(baselineArtifactPath, candidateArtifactPath);
            return new MrcrArtifactComparisonOutput("completed", report, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or InvalidOperationException or JsonException or ArgumentException)
        {
            return new MrcrArtifactComparisonOutput("error", null, ex.Message);
        }
    }

    private static string PersistArtifact(MrcrBenchmarkResult result, string? artifactDirectory)
    {
        string root = artifactDirectory
            ?? Environment.GetEnvironmentVariable("BENCHMARK_ARTIFACTS_PATH")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "benchmarks");

        string datedDir = Path.Combine(root, $"{result.RunAt:yyyy-MM-dd}");
        Directory.CreateDirectory(datedDir);

        string provider = SanitizeSegment(result.Provider);
        string model = SanitizeSegment(result.Model);
        string path = Path.Combine(datedDir, $"{result.DatasetId}-mrcr-{provider}-{model}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions));
        return Path.GetFullPath(path);
    }

    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "default";
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sb = new StringBuilder(value.Length);
        foreach (char ch in value.Trim().ToLowerInvariant())
            sb.Append(invalid.Contains(ch) || ch == ':' || char.IsWhiteSpace(ch) ? '-' : ch);
        var s = sb.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(s) ? "default" : s;
    }
}

public sealed record MrcrBenchmarkRunOutput(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("result")] MrcrBenchmarkResult? Result,
    [property: JsonPropertyName("artifactPath")] string? ArtifactPath,
    [property: JsonPropertyName("message")] string? Message);

public sealed record MrcrArtifactComparisonOutput(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("report")] MrcrBenchmarkDiffReport? Report,
    [property: JsonPropertyName("message")] string? Message);
