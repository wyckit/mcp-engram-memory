using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class LifecycleEngineTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly LifecycleEngine _lifecycle;

    public LifecycleEngineTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"lifecycle_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _lifecycle = new LifecycleEngine(_index);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    [Fact]
    public void PromoteMemory_ChangesState()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "stm"));
        var result = _lifecycle.PromoteMemory("a", "ltm");
        Assert.Contains("stm -> ltm", result);
        Assert.Equal("ltm", _index.Get("a")!.LifecycleState);
    }

    [Fact]
    public void PromoteMemory_InvalidState_ReturnsError()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test"));
        var result = _lifecycle.PromoteMemory("a", "invalid");
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void PromoteMemory_NotFound_ReturnsError()
    {
        var result = _lifecycle.PromoteMemory("missing", "ltm");
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void DecayCycle_DemotesStmToLtm()
    {
        // Create an STM entry that hasn't been accessed
        var entry = new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "stm");
        _index.Upsert(entry);

        // Run decay with very aggressive settings so that the entry with 0 access count demotes
        var result = _lifecycle.RunDecayCycle("test", decayRate: 100f, stmThreshold: 100f);
        Assert.Equal(1, result.StmToLtm);
        Assert.Contains("a", result.StmToLtmIds);
    }

    [Fact]
    public void DecayCycle_DemotesLtmToArchived()
    {
        var entry = new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "ltm");
        _index.Upsert(entry);

        // Very aggressive thresholds
        var result = _lifecycle.RunDecayCycle("test", decayRate: 100f, archiveThreshold: 100f);
        Assert.Equal(1, result.LtmToArchived);
        Assert.Contains("a", result.LtmToArchivedIds);
    }

    [Fact]
    public void DecayCycle_AllNamespaces()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "work", lifecycleState: "stm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f }, "personal", lifecycleState: "stm"));

        var result = _lifecycle.RunDecayCycle("*", decayRate: 100f, stmThreshold: 100f);
        Assert.Equal(2, result.StmToLtm);
    }

    [Fact]
    public void DecayCycle_SkipsSummaryNodes()
    {
        var entry = new CognitiveEntry("summary:c1", new[] { 1f, 0f }, "test", lifecycleState: "stm")
        {
            IsSummaryNode = true
        };
        _index.Upsert(entry);

        var result = _lifecycle.RunDecayCycle("test", decayRate: 100f, stmThreshold: 100f);
        Assert.Equal(0, result.ProcessedCount);
    }

    [Fact]
    public void DecayCycle_HighAccessCountPreservesState()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "stm"));
        // Access many times to build activation energy
        for (int i = 0; i < 50; i++)
            _index.RecordAccess("a");

        var result = _lifecycle.RunDecayCycle("test", decayRate: 0.01f, stmThreshold: 2.0f);
        Assert.Equal(0, result.StmToLtm); // Should stay in STM
    }

    [Fact]
    public void DeepRecall_IncludesArchivedEntries()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "stm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 1f, 0.01f }, "test", lifecycleState: "archived"));

        var results = _lifecycle.DeepRecall(new float[] { 1f, 0f }, "test", minScore: 0f);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void DeepRecall_AutoResurrectsHighScoringArchived()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "archived"));

        _lifecycle.DeepRecall(new float[] { 1f, 0f }, "test", resurrectionThreshold: 0.5f);

        // Entry should be resurrected to STM
        var entry = _index.Get("a");
        Assert.Equal("stm", entry!.LifecycleState);
    }

    [Fact]
    public void DeepRecall_DoesNotResurrectLowScoringArchived()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 0f, 1f }, "test", lifecycleState: "archived"));

        // Search with a very different vector
        _lifecycle.DeepRecall(new float[] { 1f, 0f }, "test", resurrectionThreshold: 0.9f, minScore: -1f);

        var entry = _index.Get("a");
        Assert.Equal("archived", entry!.LifecycleState);
    }

    // Issue 15: Two decay cycles should transition STM → LTM → Archived
    [Fact]
    public void DecayCycle_TwoCycles_StmToLtmToArchived()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "stm"));

        // First cycle: STM → LTM (aggressive decay)
        var result1 = _lifecycle.RunDecayCycle("test", decayRate: 100f, stmThreshold: 100f, archiveThreshold: -99999f);
        Assert.Equal(1, result1.StmToLtm);
        Assert.Equal(0, result1.LtmToArchived);
        Assert.Equal("ltm", _index.Get("a")!.LifecycleState);

        // Second cycle: LTM → Archived (very aggressive archive threshold)
        var result2 = _lifecycle.RunDecayCycle("test", decayRate: 100f, stmThreshold: 100f, archiveThreshold: 100f);
        Assert.Equal(0, result2.StmToLtm);
        Assert.Equal(1, result2.LtmToArchived);
        Assert.Equal("archived", _index.Get("a")!.LifecycleState);
    }
}
