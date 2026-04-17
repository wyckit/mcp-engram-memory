using System.Text.Json;
using System.Text.RegularExpressions;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class LiveAgentOutcomeBenchmarkRunnerTests : IDisposable
{
    private readonly string _dataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly LifecycleEngine _lifecycle;
    private readonly LiveAgentOutcomeBenchmarkRunner _runner;

    private sealed class OutcomeEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 5;

        public float[] Embed(string text)
        {
            text = text.ToLowerInvariant();

            float[] vector = text switch
            {
                var t when t.Contains("automatic memory maintenance") => [0f, -1f, 0f, 1f, 0f],
                var t when t.Contains("cleanup job") || t.Contains("tidies stale notes") || t.Contains("compact digests") => [0f, 0f, 0f, 1f, 0f],
                var t when t.Contains("accretionscanner") || t.Contains("decay and collapse") || t.Contains("denser summaries") => [0f, 0f, 0f, 1f, 0f],
                var t when t.Contains("old notes expire") || t.Contains("preserved for later recall") => [0f, 0f, 0f, 0f, 1f],
                var t when t.Contains("deep recall resurrects archived entries") || t.Contains("resurrection threshold") || t.Contains("preserving them for later recall") || t.Contains("archives stale entries instead of deleting them") => [0f, 0f, 0f, 0f, 1f],
                var t when t.Contains("dll lock") || t.Contains("rebuild") => [0f, 1f, 0f, 0f, 0f],
                var t when t.Contains("buildprojectreferences") => [0.4f, 0.6f, 0f, 0f, 0f],
                var t when t.Contains("crash safety") || t.Contains("concurrent reads") || t.Contains("sqlite wal") => [1f, 0f, 0f, 0f, 0f],
                var t when t.Contains("formatted") || t.Contains("dark mode") || t.Contains("concise") || t.Contains("emoji") => [0f, 0f, 1f, 0f, 0f],
                var t when t.Contains("auto-commit") => [0f, 0f, 0.9f, 0.1f, 0f],
                var t when t.Contains("graph mutex bites us") || t.Contains("lock inversion") => [0f, 0.75f, 0f, 0f, 0.2f],
                var t when t.Contains("lock ordering") || t.Contains("deadlock") => [0f, 0.8f, 0.2f, 0f, 0f],
                _ => [-1f, -1f, -1f, -1f, -1f]
            };

            return Normalize(vector);
        }

        private static float[] Normalize(float[] vector)
        {
            float norm = VectorMath.Norm(vector);
            if (norm == 0f) return vector;
            var normalized = new float[vector.Length];
            for (int i = 0; i < vector.Length; i++)
                normalized[i] = vector[i] / norm;
            return normalized;
        }
    }

    private sealed class ScriptedAgentOutcomeModelClient : IAgentOutcomeModelClient
    {
        private readonly bool _returnInvalidJson;
        private readonly bool _returnSplitJson;

        public ScriptedAgentOutcomeModelClient(bool returnInvalidJson = false, bool returnSplitJson = false)
        {
            _returnInvalidJson = returnInvalidJson;
            _returnSplitJson = returnSplitJson;
        }

        public Task<bool> IsAvailableAsync(string model, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<string?> GenerateAsync(
            string model,
            string prompt,
            int maxTokens = 320,
            float temperature = 0.1f,
            CancellationToken ct = default)
        {
            if (_returnInvalidJson)
                return Task.FromResult<string?>("not-json");

            var ids = Regex.Matches(prompt, @"^\[(?<id>[^\]]+)\]\s", RegexOptions.Multiline)
                .Select(match => match.Groups["id"].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (ids.Count == 0)
            {
                var empty = new LiveAgentOutcomeModelResponse(
                    "Insufficient context.",
                    Array.Empty<string>(),
                    InsufficientContext: true);
                return Task.FromResult<string?>(JsonSerializer.Serialize(empty));
            }

            var response = new LiveAgentOutcomeModelResponse(
                $"Use {string.Join(", ", ids)}.",
                ids,
                InsufficientContext: false);

            if (_returnSplitJson)
            {
                string split = "{\"answer\":\"Use " + string.Join(", ", ids) + ".\"}\n" +
                    JsonSerializer.Serialize(new { evidence_ids = ids }) + "\n" +
                    "{\"insufficient_context\":false}";
                return Task.FromResult<string?>(split);
            }

            return Task.FromResult<string?>(JsonSerializer.Serialize(response));
        }

        public void Dispose()
        {
        }
    }

    public LiveAgentOutcomeBenchmarkRunnerTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"live_agent_outcome_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_dataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _lifecycle = new LifecycleEngine(_index, _persistence);
        _runner = new LiveAgentOutcomeBenchmarkRunner(_index, new OutcomeEmbeddingService(), _graph, _lifecycle);
    }

    [Fact]
    public async Task RunAsync_ReturnsBaselineAndAllComparisons()
    {
        var dataset = AgentOutcomeBenchmarkRunner.CreateAgentOutcomeDataset();
        using var client = new ScriptedAgentOutcomeModelClient();

        var result = await _runner.RunAsync(
            dataset,
            new LiveAgentOutcomeGenerationOptions("ollama", "scripted"),
            client);

        Assert.Equal("agent-outcome-v1", result.DatasetId);
        Assert.Equal(LiveAgentOutcomeBenchmarkRunner.NoMemoryCondition, result.BaselineCondition);
        Assert.Equal(3, result.Comparisons.Count);
        Assert.Contains(result.Comparisons, c => c.Condition == LiveAgentOutcomeBenchmarkRunner.TranscriptReplayCondition);
        Assert.Contains(result.Comparisons, c => c.Condition == LiveAgentOutcomeBenchmarkRunner.VectorMemoryCondition);
        Assert.Contains(result.Comparisons, c => c.Condition == LiveAgentOutcomeBenchmarkRunner.FullEngramCondition);
    }

    [Fact]
    public async Task FullEngram_BeatsVector_OnArchivedAndHybridTasks()
    {
        var dataset = AgentOutcomeBenchmarkRunner.CreateAgentOutcomeDataset();
        using var client = new ScriptedAgentOutcomeModelClient();

        var result = await _runner.RunAsync(
            dataset,
            new LiveAgentOutcomeGenerationOptions("ollama", "scripted"),
            client);

        var vector = result.Comparisons.Single(c => c.Condition == LiveAgentOutcomeBenchmarkRunner.VectorMemoryCondition).Result;
        var full = result.Comparisons.Single(c => c.Condition == LiveAgentOutcomeBenchmarkRunner.FullEngramCondition).Result;

        var vectorArchived = vector.TaskResults.Single(t => t.TaskId == "task-archived-decision");
        var fullArchived = full.TaskResults.Single(t => t.TaskId == "task-archived-decision");
        Assert.False(vectorArchived.Passed);
        Assert.True(fullArchived.Passed);

        var vectorBuildLock = vector.TaskResults.Single(t => t.TaskId == "task-build-lock");
        var fullBuildLock = full.TaskResults.Single(t => t.TaskId == "task-build-lock");
        Assert.False(vectorBuildLock.Passed);
        Assert.True(fullBuildLock.Passed);
    }

    [Fact]
    public async Task InvalidJson_IsCapturedAsFormatFailure()
    {
        var dataset = AgentOutcomeBenchmarkRunner.CreateAgentOutcomeDataset();
        using var client = new ScriptedAgentOutcomeModelClient(returnInvalidJson: true);

        var result = await _runner.RunAsync(
            dataset,
            new LiveAgentOutcomeGenerationOptions("ollama", "scripted"),
            client);

        Assert.Equal(0f, result.Baseline.FormatValidityRate);
        Assert.All(result.Baseline.TaskResults, task => Assert.False(task.ResponseFormatValid));
    }

    [Fact]
    public async Task SplitJsonFragments_AreRecoveredByParser()
    {
        var dataset = AgentOutcomeBenchmarkRunner.CreateAgentOutcomeDataset();
        using var client = new ScriptedAgentOutcomeModelClient(returnSplitJson: true);

        var result = await _runner.RunAsync(
            dataset,
            new LiveAgentOutcomeGenerationOptions("ollama", "scripted"),
            client);

        var full = result.Comparisons.Single(c => c.Condition == LiveAgentOutcomeBenchmarkRunner.FullEngramCondition).Result;
        Assert.All(full.TaskResults, task => Assert.True(task.ResponseFormatValid));
        Assert.Contains(full.TaskResults, task => task.CitedMemoryIds.Count > 0);
    }

    [Fact]
    public async Task HardDataset_FullEngram_BeatsTranscriptReplay()
    {
        var dataset = AgentOutcomeBenchmarkRunner.CreateHardOutcomeDataset();
        using var client = new ScriptedAgentOutcomeModelClient();

        var result = await _runner.RunAsync(
            dataset,
            new LiveAgentOutcomeGenerationOptions("ollama", "scripted"),
            client);

        var transcript = result.Comparisons.Single(c => c.Condition == LiveAgentOutcomeBenchmarkRunner.TranscriptReplayCondition).Result;
        var full = result.Comparisons.Single(c => c.Condition == LiveAgentOutcomeBenchmarkRunner.FullEngramCondition).Result;

        var transcriptReturn = transcript.TaskResults.Single(t => t.TaskId == "hard-expired-return");
        var fullReturn = full.TaskResults.Single(t => t.TaskId == "hard-expired-return");
        Assert.False(transcriptReturn.Passed);
        Assert.True(fullReturn.Passed);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, true);
    }
}
