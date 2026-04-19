using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class MrcrBenchmarkRunnerTests : IDisposable
{
    private readonly string _dataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly StubEmbeddingService _embedding;
    private readonly MrcrScorer _scorer;
    private readonly MrcrBenchmarkRunner _runner;

    public MrcrBenchmarkRunnerTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"mrcr_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_dataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _embedding = new StubEmbeddingService();
        _scorer = new MrcrScorer(_embedding, passThreshold: 0.99f);
        _runner = new MrcrBenchmarkRunner(_index, _embedding, _scorer);
    }

    [Fact]
    public async Task RunAsync_BothArms_ReturnOneResultPerTask()
    {
        var tasks = BuildSyntheticTasks();
        var options = new MrcrGenerationOptions(
            Provider: "stub",
            Model: "stub-model",
            Limit: 0);
        var client = new EchoGoldClient();

        var result = await _runner.RunAsync("mrcr-test", tasks, options, client);

        Assert.Equal("mrcr-test", result.DatasetId);
        Assert.NotNull(result.FullContext);
        Assert.NotNull(result.EngramRetrieval);
        Assert.Equal(tasks.Count, result.FullContext!.TaskResults.Count);
        Assert.Equal(tasks.Count, result.EngramRetrieval!.TaskResults.Count);
    }

    [Fact]
    public async Task RunAsync_EngramArm_UsesFewerPromptTokensThanFullContext()
    {
        var tasks = BuildSyntheticTasks();
        var options = new MrcrGenerationOptions("stub", "stub-model", Limit: 0, TopK: 2);
        var client = new EchoGoldClient();

        var result = await _runner.RunAsync("mrcr-test", tasks, options, client);

        Assert.NotNull(result.FullContext);
        Assert.NotNull(result.EngramRetrieval);
        Assert.True(result.EngramRetrieval!.TotalPromptTokens < result.FullContext!.TotalPromptTokens,
            $"engram total tokens {result.EngramRetrieval.TotalPromptTokens} should be < full context {result.FullContext.TotalPromptTokens}");
        Assert.True(result.PromptTokenReductionRatio > 0f);
    }

    [Fact]
    public async Task RunAsync_OnlyEngramArm_SkipsFullContext()
    {
        var tasks = BuildSyntheticTasks();
        var options = new MrcrGenerationOptions("stub", "stub-model",
            Limit: 0, RunFullContextArm: false, RunEngramArm: true);
        var client = new EchoGoldClient();

        var result = await _runner.RunAsync("mrcr-test", tasks, options, client);

        Assert.Null(result.FullContext);
        Assert.NotNull(result.EngramRetrieval);
    }

    [Fact]
    public void DatasetLoader_ReadsJsonl()
    {
        string path = Path.Combine(_dataPath, "fixture.jsonl");
        Directory.CreateDirectory(_dataPath);

        var tasks = BuildSyntheticTasks();
        File.WriteAllLines(path, tasks.Select(t => JsonSerializer.Serialize(t)));

        var loaded = MrcrDatasetLoader.Load(path);

        Assert.Equal(tasks.Count, loaded.Count);
        Assert.Equal(tasks[0].TaskId, loaded[0].TaskId);
        Assert.Equal(tasks[0].Turns.Count, loaded[0].Turns.Count);
    }

    [Fact]
    public void DatasetLoader_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => MrcrDatasetLoader.Load(Path.Combine(_dataPath, "nope.jsonl")));
    }

    private static IReadOnlyList<MrcrTask> BuildSyntheticTasks()
    {
        return new[]
        {
            new MrcrTask(
                TaskId: "t1",
                ContextTokens: 64,
                Turns: new[]
                {
                    new MrcrTurn("user", "remember token ALPHA-77"),
                    new MrcrTurn("assistant", "noted"),
                    new MrcrTurn("user", "remember token BRAVO-92"),
                    new MrcrTurn("assistant", "noted")
                },
                Probe: "what was the first token",
                GoldAnswer: "ALPHA-77",
                NeedleIndex: 0,
                Bucket: "tiny"),
            new MrcrTask(
                TaskId: "t2",
                ContextTokens: 72,
                Turns: new[]
                {
                    new MrcrTurn("user", "the color is PURPLE"),
                    new MrcrTurn("assistant", "ok"),
                    new MrcrTurn("user", "the shape is HEXAGON"),
                    new MrcrTurn("assistant", "ok")
                },
                Probe: "what shape was mentioned",
                GoldAnswer: "HEXAGON",
                NeedleIndex: 1,
                Bucket: "tiny")
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, recursive: true);
        }
        catch { }
    }

    /// <summary>
    /// Embedding stub: hashes content into a deterministic 8-dim vector keyed on distinct
    /// tokens so synthetic probes retrieve the needle-bearing turn.
    /// </summary>
    private sealed class StubEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 8;

        public float[] Embed(string text)
        {
            var vec = new float[8];
            var lower = text.ToLowerInvariant();
            if (lower.Contains("alpha-77") || lower.Contains("first token") || lower.Contains("token alpha")) vec[0] = 1f;
            if (lower.Contains("bravo-92") || lower.Contains("second token") || lower.Contains("token bravo")) vec[1] = 1f;
            if (lower.Contains("purple") || lower.Contains("color")) vec[2] = 1f;
            if (lower.Contains("hexagon") || lower.Contains("shape")) vec[3] = 1f;
            if (vec.All(v => v == 0f)) vec[4] = 1f;
            return Normalize(vec);
        }

        private static float[] Normalize(float[] v)
        {
            float norm = VectorMath.Norm(v);
            if (norm == 0f) return v;
            var n = new float[v.Length];
            for (int i = 0; i < v.Length; i++) n[i] = v[i] / norm;
            return n;
        }
    }

    /// <summary>
    /// A fake client whose answer is whatever the prompt's last gold token looks like —
    /// just enough to make scoring hit near-1.0 when the needle is in the prompt.
    /// </summary>
    private sealed class EchoGoldClient : IAgentOutcomeModelClient
    {
        public Task<bool> IsAvailableAsync(string model, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<string?> GenerateAsync(string model, string prompt, int maxTokens = 320,
            float temperature = 0.1f, CancellationToken ct = default)
        {
            string lower = prompt.ToLowerInvariant();
            if (lower.Contains("alpha-77")) return Task.FromResult<string?>("ALPHA-77");
            if (lower.Contains("bravo-92")) return Task.FromResult<string?>("BRAVO-92");
            if (lower.Contains("hexagon")) return Task.FromResult<string?>("HEXAGON");
            if (lower.Contains("purple")) return Task.FromResult<string?>("PURPLE");
            return Task.FromResult<string?>("unknown");
        }

        public void Dispose() { }
    }
}
