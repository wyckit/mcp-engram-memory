using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace McpEngramMemory.Tests;

/// <summary>
/// Executes the T2 datasets through the live Ollama-backed benchmark runner with ablations
/// enabled, and persists artifacts to benchmarks/YYYY-MM-DD/. Requires Ollama running at
/// localhost:11434 with the configured model installed (default: phi3.5:3.8b). Each dataset
/// × 7 conditions × ~4 tasks × ~3–5s per generation ≈ 90–150 s per dataset.
/// </summary>
[Trait("Category", "LiveBenchmark")]
public sealed class T2LiveBenchmarkRun : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly OnnxEmbeddingService _embedding;
    private readonly KnowledgeGraph _graph;
    private readonly LifecycleEngine _lifecycle;
    private readonly LiveAgentOutcomeBenchmarkRunner _runner;
    private readonly AgentOutcomeModelClientFactory _clientFactory;

    private static readonly JsonSerializerOptions ArtifactJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public T2LiveBenchmarkRun(ITestOutputHelper output)
    {
        _output = output;
        _testDataPath = Path.Combine(Path.GetTempPath(), $"t2_live_bench_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _embedding = new OnnxEmbeddingService();
        _graph = new KnowledgeGraph(_persistence, _index);
        _lifecycle = new LifecycleEngine(_index, _persistence);
        _runner = new LiveAgentOutcomeBenchmarkRunner(_index, _embedding, _graph, _lifecycle);
        _clientFactory = new AgentOutcomeModelClientFactory();
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    [Theory]
    [InlineData("reasoning-ladder-v1")]
    [InlineData("contradiction-arena-v1")]
    [InlineData("adversarial-retrieval-v1")]
    [InlineData("counterfactual-v1")]
    public async Task RunLiveWithAblations(string datasetId)
    {
        string model = Environment.GetEnvironmentVariable("T2_LIVE_MODEL") ?? "phi3.5:3.8b";

        var dataset = AgentOutcomeBenchmarkRunner.CreateDataset(datasetId);
        Assert.NotNull(dataset);

        using var client = _clientFactory.Create("ollama", endpoint: null);
        if (!await client.IsAvailableAsync(model, CancellationToken.None))
        {
            _output.WriteLine($"SKIP {datasetId}: model {model} not available via ollama.");
            return;
        }

        var options = new LiveAgentOutcomeGenerationOptions(
            Provider: "ollama",
            Model: model,
            Endpoint: null,
            ContextualPrefix: false,
            MaxTokens: 320,
            Temperature: 0.1f,
            RunAblations: true);

        var result = await _runner.RunAsync(dataset!, options, client, CancellationToken.None);

        string root = FindRepoRoot();
        string datedDir = Path.Combine(root, "benchmarks", $"{result.RunAt:yyyy-MM-dd}");
        Directory.CreateDirectory(datedDir);
        string modelSegment = model.Replace(':', '-').Replace('/', '-');
        string artifactPath = Path.Combine(
            datedDir,
            $"{result.DatasetId}-live-agent-outcome-ollama-{modelSegment}.json");
        File.WriteAllText(artifactPath, JsonSerializer.Serialize(result, ArtifactJson));

        _output.WriteLine($"=== {datasetId} (live:{model}) ===");
        _output.WriteLine($"Artifact: {artifactPath}");
        _output.WriteLine($"Baseline (no_memory)        : succ={result.Baseline.MeanSuccessScore:F3} pass={result.Baseline.PassRate:F3}");
        foreach (var cmp in result.Comparisons)
        {
            var r = cmp.Result;
            _output.WriteLine(
                $"{cmp.Condition,-28}: succ={r.MeanSuccessScore:F3} pass={r.PassRate:F3} RPV={r.MeanReasoningPathValidity:F3} CHS={r.MeanContradictionHandlingScore:F3} NRS={r.MeanNoiseResistanceScore:F3} Stale={r.MeanStaleMemoryPenalty:F3} fmt={r.FormatValidityRate:F3}");
        }

        Assert.Equal(datasetId, result.DatasetId);
        Assert.Equal(6, result.Comparisons.Count);
    }

    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "McpEngramMemory.slnx")) &&
               Path.GetDirectoryName(dir) is { } parent && parent != dir)
        {
            dir = parent;
        }
        return dir;
    }
}
