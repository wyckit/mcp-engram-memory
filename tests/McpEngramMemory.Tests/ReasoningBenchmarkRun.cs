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

[Trait("Category", "LiveBenchmark")]
public class ReasoningBenchmarkRun : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly ITestOutputHelper _output;

    public ReasoningBenchmarkRun(ITestOutputHelper output)
    {
        _output = output;
        _testDataPath = Path.Combine(Path.GetTempPath(), $"reasoning_run_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    [Fact]
    public async Task Run_Phi35_Reasoning_Dataset()
    {
        string model = "phi3.5:3.8b";
        _output.WriteLine($"Starting Reasoning benchmark for {model}...");

        var embedding = new OnnxEmbeddingService();
        var graph = new KnowledgeGraph(_persistence, _index);
        var lifecycle = new LifecycleEngine(_index, _persistence);
        var runner = new LiveAgentOutcomeBenchmarkRunner(_index, embedding, graph, lifecycle);

        using var client = new OllamaAgentOutcomeModelClient();
        if (!await client.IsAvailableAsync(model))
        {
            _output.WriteLine($"Model {model} is not available in Ollama. Skipping.");
            return;
        }

        var dataset = AgentOutcomeBenchmarkRunner.CreateReasoningOutcomeDataset();
        var result = await runner.RunAsync(
            dataset,
            new LiveAgentOutcomeGenerationOptions("ollama", model),
            client);

        _output.WriteLine($"Benchmark completed for {model}.");
        
        var full = result.Comparisons.FirstOrDefault(c => c.Condition == "full_engram")?.Result;
        _output.WriteLine($"Full Engram Pass Rate: {full?.PassRate:P2}");
        _output.WriteLine($"Full Engram Success Score: {full?.MeanSuccessScore:F3}");
        
        var transcript = result.Comparisons.FirstOrDefault(c => c.Condition == "transcript_replay")?.Result;
        _output.WriteLine($"Transcript Replay Pass Rate: {transcript?.PassRate:P2}");

        // Find project root to save the result
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "McpEngramMemory.slnx")) && Path.GetDirectoryName(root) != null)
        {
            root = Path.GetDirectoryName(root)!;
        }

        string dateDir = DateTime.UtcNow.ToString("yyyy-MM-dd");
        string artifactDir = Path.Combine(root, "benchmarks", dateDir);
        Directory.CreateDirectory(artifactDir);

        string fileName = $"{dataset.DatasetId}-live-agent-outcome-ollama-{model.Replace(":", "-")}.json";
        string filePath = Path.Combine(artifactDir, fileName);
        
        File.WriteAllText(filePath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        _output.WriteLine($"Result saved to {filePath}");
    }
}
