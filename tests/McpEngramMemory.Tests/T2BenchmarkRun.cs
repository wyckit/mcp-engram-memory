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
/// Executes the four T2 intelligence-claim datasets (reasoning-ladder-v1, contradiction-arena-v1,
/// adversarial-retrieval-v1, counterfactual-v1) through the offline AgentOutcomeBenchmarkRunner
/// with ablations enabled and writes JSON artifacts to benchmarks/YYYY-MM-DD/.
///
/// Offline-only — no live model required. Uses the real ONNX embedding service so results reflect
/// what production retrieval would actually surface.
/// </summary>
[Trait("Category", "T2Benchmark")]
public sealed class T2BenchmarkRun : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly OnnxEmbeddingService _embedding;
    private readonly KnowledgeGraph _graph;
    private readonly LifecycleEngine _lifecycle;
    private readonly AgentOutcomeBenchmarkRunner _runner;

    private static readonly JsonSerializerOptions ArtifactJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public T2BenchmarkRun(ITestOutputHelper output)
    {
        _output = output;
        _testDataPath = Path.Combine(Path.GetTempPath(), $"t2_bench_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _embedding = new OnnxEmbeddingService();
        _graph = new KnowledgeGraph(_persistence, _index);
        _lifecycle = new LifecycleEngine(_index, _persistence);
        _runner = new AgentOutcomeBenchmarkRunner(_index, _embedding, _graph, _lifecycle);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (!Directory.Exists(_testDataPath)) return;

        // The PersistenceManager has a 50ms debounced writer; its Dispose cancels
        // the timer but a final flush can still be racing with this teardown when
        // the test wrote many ablation artifacts. On CI this shows up as
        // IOException: Directory not empty — files reappear during the recursive
        // walk. Short retry loop lets the filesystem settle; last attempt
        // swallows the exception so we don't fail an otherwise-passing test.
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Directory.Delete(_testDataPath, recursive: true);
                return;
            }
            catch (IOException) when (i < 4)
            {
                Thread.Sleep(50 * (i + 1));
            }
            catch (UnauthorizedAccessException) when (i < 4)
            {
                Thread.Sleep(50 * (i + 1));
            }
        }
        // Final attempt — swallow so we don't fail a green test on cleanup.
        try { Directory.Delete(_testDataPath, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("reasoning-ladder-v1")]
    [InlineData("contradiction-arena-v1")]
    [InlineData("adversarial-retrieval-v1")]
    [InlineData("counterfactual-v1")]
    public void RunOfflineWithAblations(string datasetId)
    {
        var dataset = AgentOutcomeBenchmarkRunner.CreateDataset(datasetId);
        Assert.NotNull(dataset);

        var result = _runner.Run(dataset!, useContextualPrefix: false, runAblations: true);

        string root = FindRepoRoot();
        string datedDir = Path.Combine(root, "benchmarks", $"{result.RunAt:yyyy-MM-dd}");
        Directory.CreateDirectory(datedDir);
        string artifactPath = Path.Combine(datedDir, $"{result.DatasetId}-agent-outcome.json");
        File.WriteAllText(artifactPath, JsonSerializer.Serialize(result, ArtifactJson));

        _output.WriteLine($"=== {datasetId} ===");
        _output.WriteLine($"Artifact: {artifactPath}");
        _output.WriteLine($"Baseline (no_memory)        : success={result.Baseline.MeanSuccessScore:F3}  pass={result.Baseline.PassRate:F3}");

        foreach (var cmp in result.Comparisons)
        {
            var r = cmp.Result;
            _output.WriteLine(
                $"{cmp.Condition,-28}: success={r.MeanSuccessScore:F3}  pass={r.PassRate:F3}  RPV={r.MeanReasoningPathValidity:F3}  CHS={r.MeanContradictionHandlingScore:F3}  NRS={r.MeanNoiseResistanceScore:F3}  StaleP={r.MeanStaleMemoryPenalty:F3}  MinEv={r.MeanMinimalEvidenceScore:F3}");
        }

        Assert.Equal(datasetId, result.DatasetId);
        Assert.Equal(6, result.Comparisons.Count); // 3 core + 3 ablations
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
