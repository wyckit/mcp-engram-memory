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
public class DeepSeekBenchmarkRun : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly ITestOutputHelper _output;

    public DeepSeekBenchmarkRun(ITestOutputHelper output)
    {
        _output = output;
        _testDataPath = Path.Combine(Path.GetTempPath(), $"deepseek_run_{Guid.NewGuid():N}");
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
    public async Task Run_DeepSeek_Hard_Dataset()
    {
        string model = "deepseek-r1:8b";
        _output.WriteLine($"Starting benchmark for {model}...");

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

        var dataset = AgentOutcomeBenchmarkRunner.CreateHardOutcomeDataset();
        var result = await runner.RunAsync(
            dataset,
            new LiveAgentOutcomeGenerationOptions("ollama", model),
            client);

        _output.WriteLine($"Benchmark completed for {model}.");
        _output.WriteLine($"Pass Rate: {result.Comparisons.FirstOrDefault(c => c.Condition == "full_engram")?.Result.PassRate:P2}");
        _output.WriteLine($"Success Score: {result.Comparisons.FirstOrDefault(c => c.Condition == "full_engram")?.Result.MeanSuccessScore:F3}");

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
