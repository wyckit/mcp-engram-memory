using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class AgentOutcomeBenchmarkRunnerTests : IDisposable
{
    private readonly string _dataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly LifecycleEngine _lifecycle;
    private readonly AgentOutcomeBenchmarkRunner _runner;

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

    public AgentOutcomeBenchmarkRunnerTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"agent_outcome_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_dataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _lifecycle = new LifecycleEngine(_index, _persistence);
        _runner = new AgentOutcomeBenchmarkRunner(_index, new OutcomeEmbeddingService(), _graph, _lifecycle);
    }

    [Fact]
    public void CreateDataset_ReturnsExpectedStructure()
    {
        var dataset = AgentOutcomeBenchmarkRunner.CreateDataset("agent-outcome-v1");

        Assert.NotNull(dataset);
        Assert.Equal("agent-outcome-v1", dataset!.DatasetId);
        Assert.True(dataset.SeedEntries.Count >= 8);
        Assert.True(dataset.Tasks.Count >= 5);
        Assert.NotEmpty(dataset.Edges);
    }

    [Fact]
    public void AdditionalDatasets_AreAvailable()
    {
        var ids = AgentOutcomeBenchmarkRunner.GetAvailableDatasets();
        Assert.Contains("agent-outcome-repo-v1", ids);
        Assert.Contains("agent-outcome-hard-v1", ids);

        var dataset = AgentOutcomeBenchmarkRunner.CreateDataset("agent-outcome-repo-v1");
        Assert.NotNull(dataset);
        Assert.Equal("agent-outcome-repo-v1", dataset!.DatasetId);
        Assert.True(dataset.Tasks.Count >= 4);

        var hard = AgentOutcomeBenchmarkRunner.CreateDataset("agent-outcome-hard-v1");
        Assert.NotNull(hard);
        Assert.Equal("agent-outcome-hard-v1", hard!.DatasetId);
        Assert.True(hard.Tasks.Count >= 3);
    }

    [Fact]
    public void Run_ReturnsBaselineAndAllComparisons()
    {
        var dataset = AgentOutcomeBenchmarkRunner.CreateAgentOutcomeDataset();

        var result = _runner.Run(dataset);

        Assert.Equal("agent-outcome-v1", result.DatasetId);
        Assert.Equal(AgentOutcomeBenchmarkRunner.NoMemoryCondition, result.BaselineCondition);
        Assert.Equal(3, result.Comparisons.Count);
        Assert.Contains(result.Comparisons, c => c.Condition == AgentOutcomeBenchmarkRunner.TranscriptReplayCondition);
        Assert.Contains(result.Comparisons, c => c.Condition == AgentOutcomeBenchmarkRunner.VectorMemoryCondition);
        Assert.Contains(result.Comparisons, c => c.Condition == AgentOutcomeBenchmarkRunner.FullEngramCondition);
    }

    [Fact]
    public void FullEngram_BeatsVector_OnArchivedAndHybridRescueTasks()
    {
        var dataset = AgentOutcomeBenchmarkRunner.CreateAgentOutcomeDataset();

        var result = _runner.Run(dataset);
        var vector = result.Comparisons.Single(c => c.Condition == AgentOutcomeBenchmarkRunner.VectorMemoryCondition).Result;
        var full = result.Comparisons.Single(c => c.Condition == AgentOutcomeBenchmarkRunner.FullEngramCondition).Result;

        var vectorArchived = vector.TaskScores.Single(t => t.TaskId == "task-archived-decision");
        var fullArchived = full.TaskScores.Single(t => t.TaskId == "task-archived-decision");
        Assert.False(vectorArchived.Passed);
        Assert.True(fullArchived.Passed);

        var vectorBuildLock = vector.TaskScores.Single(t => t.TaskId == "task-build-lock");
        var fullBuildLock = full.TaskScores.Single(t => t.TaskId == "task-build-lock");
        Assert.False(vectorBuildLock.Passed);
        Assert.True(fullBuildLock.Passed,
            $"Full Engram build-lock task retrieved [{string.Join(", ", fullBuildLock.RetrievedMemoryIds)}].");
    }

    [Fact]
    public void NoMemory_BaselineHasZeroPassRate()
    {
        var dataset = AgentOutcomeBenchmarkRunner.CreateAgentOutcomeDataset();

        var result = _runner.Run(dataset);

        Assert.Equal(0f, result.Baseline.PassRate);
        Assert.True(result.Baseline.MeanSuccessScore < 0.01f);
    }

    [Fact]
    public void HardDataset_FullEngram_BeatsTranscriptReplay()
    {
        var dataset = AgentOutcomeBenchmarkRunner.CreateHardOutcomeDataset();

        var result = _runner.Run(dataset);
        var transcript = result.Comparisons.Single(c => c.Condition == AgentOutcomeBenchmarkRunner.TranscriptReplayCondition).Result;
        var full = result.Comparisons.Single(c => c.Condition == AgentOutcomeBenchmarkRunner.FullEngramCondition).Result;

        var transcriptReturn = transcript.TaskScores.Single(t => t.TaskId == "hard-expired-return");
        var fullReturn = full.TaskScores.Single(t => t.TaskId == "hard-expired-return");
        Assert.False(transcriptReturn.Passed);
        Assert.True(fullReturn.Passed);

        var transcriptGraph = transcript.TaskScores.Single(t => t.TaskId == "hard-graph-inversion");
        var fullGraph = full.TaskScores.Single(t => t.TaskId == "hard-graph-inversion");
        Assert.False(transcriptGraph.Passed);
        Assert.True(fullGraph.Passed);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, true);
    }
}
