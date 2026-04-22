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

    public LiveAgentOutcomeRegressionTests()
    {
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
        string latestDir = Path.Combine(root, "benchmarks", "2026-04-17");

        if (!Directory.Exists(latestDir)) return;

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
        string? candidatePath = Directory.GetFiles(latestDir, pattern).FirstOrDefault();

        if (candidatePath == null)
        {
            // Skip if no candidate found for this model in the latest run — baseline
            // model may not have been re-run. Not a regression signal.
            return;
        }

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
}
