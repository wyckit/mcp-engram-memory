using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;
using System.Text.Json;
using Xunit;

namespace McpEngramMemory.Tests;

[Trait("Category", "MSA")]
public class LiveAgentOutcomeRegressionTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly MetricsCollector _metrics;
    private readonly BenchmarkTools _tools;
    private readonly ITestOutputHelper _output;

    public LiveAgentOutcomeRegressionTests(ITestOutputHelper output)
    {
        _output = output;
        _testDataPath = Path.Combine(Path.GetTempPath(), $"regression_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _metrics = new MetricsCollector();
        
        var embedding = new McpEngramMemory.Core.Services.OnnxEmbeddingService();
            
        var graph = new McpEngramMemory.Core.Services.Graph.KnowledgeGraph(_persistence, _index);
        var lifecycle = new McpEngramMemory.Core.Services.Lifecycle.LifecycleEngine(_index, _persistence);
        
        var outcomeRunner = new AgentOutcomeBenchmarkRunner(_index, embedding, graph, lifecycle);
        var liveOutcomeRunner = new LiveAgentOutcomeBenchmarkRunner(_index, embedding, graph, lifecycle);
        
        _tools = new BenchmarkTools(
            new BenchmarkRunner(_index, embedding),
            outcomeRunner,
            liveOutcomeRunner,
            new AgentOutcomeModelClientFactory(),
            _metrics);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    [Theory]
    [InlineData("agent-outcome-v1", "benchmarks/baselines/agent-outcome-v1-baseline.json")]
    [InlineData("agent-outcome-repo-v1", "benchmarks/baselines/agent-outcome-repo-v1-baseline.json")]
    [InlineData("agent-outcome-hard-v1", "benchmarks/baselines/agent-outcome-hard-v1-baseline.json")]
    public void Verify_No_Regression_Against_Baseline(string datasetId, string baselineRelativePath)
    {
        // Find project root by looking for the .slnx file
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "McpEngramMemory.slnx")) && Path.GetDirectoryName(root) != null)
        {
            root = Path.GetDirectoryName(root)!;
        }

        string baselinePath = Path.Combine(root, baselineRelativePath);

        // Derive candidate model from the baseline so we never compare apples-to-oranges.
        // Prior bug: candidate glob hard-coded "qwen2.5-7b" while baselines were generated
        // with phi3.5:3.8b — every "regression" was pure model-variance between two
        // different LLMs. The model-match assertion below locks this down.
        string baselineModel;
        using (var baselineDoc = JsonDocument.Parse(File.ReadAllText(baselinePath)))
        {
            baselineModel = baselineDoc.RootElement.GetProperty("model").GetString()!;
        }

        // Filename slug uses '-' in place of the model-tag ':' (e.g. "phi3.5:3.8b" → "phi3.5-3.8b").
        string modelSlug = baselineModel.Replace(':', '-');
        string pattern = $"{datasetId}-live-agent-outcome-ollama-{modelSlug}.json";

        string? candidatePath = FindNewestCandidate(root, pattern);
        if (candidatePath == null)
        {
            // No run has produced a candidate for this dataset on the baseline's model.
            // Legitimately not a regression signal (fresh clone, or that model was not
            // re-run), so report the case accurately as a dynamic xUnit v3 skip.
            Assert.Skip(
                $"No artifact matching '{pattern}' exists in any dated folder under " +
                $"'{ResolveArtifactRoot(root)}'. Baseline model '{baselineModel}' has not been re-run.");
        }

        _output.WriteLine(
            $"{datasetId}: comparing against " +
            $"'{Path.GetFileName(Path.GetDirectoryName(candidatePath))}/{Path.GetFileName(candidatePath)}'.");

        // Apples-to-apples: baseline and candidate MUST share the same LLM. Catches
        // future drift where someone updates the baseline model without regenerating
        // matching candidates.
        string candidateModel;
        using (var candidateDoc = JsonDocument.Parse(File.ReadAllText(candidatePath)))
        {
            candidateModel = candidateDoc.RootElement.GetProperty("model").GetString()!;
        }
        Assert.Equal(baselineModel, candidateModel);

        // We allow 2% success regression and 5% pass rate regression for stochastic
        // run-to-run variance on the SAME model.
        var result = _tools.CheckForRegression(
            baselinePath,
            candidatePath,
            successThreshold: 0.02f,
            passRateThreshold: 0.05f);

        Assert.True(result.Status == "passed" || result.Status == "completed",
            $"Regression detected for {datasetId} ({baselineModel}): {result.Message}");
    }

    /// <summary>
    /// Artifact root, matching how the writers resolve it (BenchmarkTools,
    /// ColdStartBenchmarkRunner, MrcrPilotCommand) so tests read from wherever
    /// a run actually wrote. Falls back to the repo-root benchmarks directory
    /// rather than Directory.GetCurrentDirectory(), which under `dotnet test`
    /// is the test output directory.
    /// </summary>
    private static string ResolveArtifactRoot(string repoRoot)
        => Environment.GetEnvironmentVariable("BENCHMARK_ARTIFACTS_PATH")
           ?? Path.Combine(repoRoot, "benchmarks");

    /// <summary>
    /// Newest artifact matching <paramref name="pattern"/>, searching dated
    /// benchmark folders newest-first. Returns null when no folder has one.
    ///
    /// Previously the folder was hard-coded to "benchmarks/2026-04-17". That was
    /// stale, but load-bearing: it is the only committed folder holding
    /// phi3.5-3.8b candidates for all three datasets, so it was doing the real
    /// comparison. Naively switching to "newest dated folder" would have selected
    /// 2026-05-07 — which holds only a cold-start A/B record — and silently turned
    /// all three cases into vacuous passes. Selecting on candidate presence rather
    /// than folder date keeps today's coverage and automatically prefers a fresher
    /// run as soon as one emits matching artifacts.
    ///
    /// Directory names are yyyy-MM-dd, optionally suffixed (e.g.
    /// "2026-03-10-ablation"), so an ordinal sort is chronological. This matches
    /// the glob used by the CI regression gate in benchmark.yml.
    /// </summary>
    private static string? FindNewestCandidate(string repoRoot, string pattern)
    {
        string artifactRoot = ResolveArtifactRoot(repoRoot);
        if (!Directory.Exists(artifactRoot)) return null;

        foreach (string dir in Directory.GetDirectories(artifactRoot)
                     .Where(d => IsDatedFolder(Path.GetFileName(d)))
                     .OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            string? match = Directory.GetFiles(dir, pattern).FirstOrDefault();
            if (match != null) return match;
        }

        return null;
    }

    private static bool IsDatedFolder(string name)
        => name.Length >= 10
           && name[4] == '-' && name[7] == '-'
           && char.IsAsciiDigit(name[0]) && char.IsAsciiDigit(name[1])
           && char.IsAsciiDigit(name[2]) && char.IsAsciiDigit(name[3])
           && char.IsAsciiDigit(name[5]) && char.IsAsciiDigit(name[6])
           && char.IsAsciiDigit(name[8]) && char.IsAsciiDigit(name[9]);
}
