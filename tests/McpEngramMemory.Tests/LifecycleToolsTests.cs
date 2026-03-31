using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class LifecycleToolsTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly LifecycleEngine _lifecycle;
    private readonly LifecycleTools _tools;

    private sealed class StubEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    public LifecycleToolsTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"lctools_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _lifecycle = new LifecycleEngine(_index);
        _tools = new LifecycleTools(_lifecycle, new StubEmbeddingService());
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    // ── PromoteMemory ──

    [Fact]
    public void PromoteMemory_StmToLtm_ChangesState()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "stm"));

        var result = _tools.PromoteMemory("a", "ltm");
        Assert.Contains("stm -> ltm", result);
        Assert.Equal("ltm", _index.Get("a")!.LifecycleState);
    }

    [Fact]
    public void PromoteMemory_LtmToArchived_ChangesState()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "ltm"));

        var result = _tools.PromoteMemory("a", "archived");
        Assert.Contains("ltm -> archived", result);
        Assert.Equal("archived", _index.Get("a")!.LifecycleState);
    }

    [Fact]
    public void PromoteMemory_NonExistent_ReturnsError()
    {
        var result = _tools.PromoteMemory("missing", "ltm");
        Assert.StartsWith("Error:", result);
    }

    // ── DeepRecall ──

    [Fact]
    public void DeepRecall_FindsArchivedEntries()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", "active entry", lifecycleState: "stm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 1f, 0.01f }, "test", "archived entry", lifecycleState: "archived"));

        var result = _tools.DeepRecall("test", vector: new[] { 1f, 0f }, minScore: 0f, resurrectionThreshold: 999f);
        var results = (IReadOnlyList<CognitiveSearchResult>)result;
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void DeepRecall_TextOnly_AutoEmbeds()
    {
        // Stub returns [0.5, 0.5], so store an entry with that vector
        _index.Upsert(new CognitiveEntry("a", new[] { 0.5f, 0.5f }, "test", "some entry", lifecycleState: "stm"));

        var result = _tools.DeepRecall("test", text: "search text", minScore: 0f, resurrectionThreshold: 999f);
        var results = (IReadOnlyList<CognitiveSearchResult>)result;
        Assert.Single(results);
        Assert.Equal("a", results[0].Id);
    }

    [Fact]
    public void DeepRecall_NoVectorNoText_ReturnsError()
    {
        var result = _tools.DeepRecall("test");
        Assert.IsType<string>(result);
        Assert.StartsWith("Error:", (string)result);
    }

    [Fact]
    public void DeepRecall_ResurrectsHighScoring()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", "archived memory", lifecycleState: "archived"));

        // Search with same vector — should score 1.0, well above resurrection threshold
        var result = _tools.DeepRecall("test", vector: new[] { 1f, 0f }, minScore: 0f, resurrectionThreshold: 0.5f);
        var results = (IReadOnlyList<CognitiveSearchResult>)result;
        Assert.Single(results);
        Assert.Equal("stm", results[0].LifecycleState);

        // Verify the entry was actually resurrected in the index
        var entry = _index.Get("a");
        Assert.Equal("stm", entry!.LifecycleState);
    }

    // ── MemoryFeedback ──

    [Fact]
    public void MemoryFeedback_PositiveDelta_BoostsEnergy()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "stm"));

        var result = _tools.MemoryFeedback("a", 2.0f);
        Assert.IsType<FeedbackResult>(result);
        var feedback = (FeedbackResult)result;
        Assert.Equal(2f, feedback.NewActivationEnergy);
        Assert.True(feedback.NewActivationEnergy > feedback.PreviousActivationEnergy);
    }

    [Fact]
    public void MemoryFeedback_NegativeDelta_SuppressesEnergy()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "stm"));

        var result = _tools.MemoryFeedback("a", -3.0f);
        Assert.IsType<FeedbackResult>(result);
        var feedback = (FeedbackResult)result;
        Assert.Equal(-3f, feedback.NewActivationEnergy);
        Assert.True(feedback.NewActivationEnergy < feedback.PreviousActivationEnergy);
    }

    [Fact]
    public void MemoryFeedback_NonExistent_ReturnsError()
    {
        var result = _tools.MemoryFeedback("missing", 1.0f);
        Assert.IsType<string>(result);
        Assert.StartsWith("Error:", (string)result);
    }

    // ── DecayCycle ──

    [Fact]
    public void DecayCycle_DemotesStaleEntries()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "stm"));

        // Aggressive decay to force STM → LTM
        var result = _tools.DecayCycle("test", decayRate: 100f, stmThreshold: 100f);
        Assert.Equal(1, result.StmToLtm);
        Assert.Contains("a", result.StmToLtmIds);
        Assert.Equal("ltm", _index.Get("a")!.LifecycleState);
    }

    // ── ConfigureDecay ──

    [Fact]
    public void ConfigureDecay_ValidConfig_Stores()
    {
        var result = _tools.ConfigureDecay("test", decayRate: 0.5f, stmThreshold: 5.0f);
        Assert.IsType<DecayConfig>(result);
        var config = (DecayConfig)result;
        Assert.Equal(0.5f, config.DecayRate);
        Assert.Equal(5.0f, config.StmThreshold);
    }

    [Fact]
    public void ConfigureDecay_EmptyNamespace_ReturnsError()
    {
        var result = _tools.ConfigureDecay("");
        Assert.IsType<string>(result);
        Assert.StartsWith("Error:", (string)result);
    }
}
