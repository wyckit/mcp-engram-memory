using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class FeedbackTests : IDisposable
{
    private readonly string _dataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly LifecycleEngine _lifecycle;
    private readonly HashEmbeddingService _embedding;

    public FeedbackTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"feedback_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_dataPath);
        _index = new CognitiveIndex(_persistence);
        _lifecycle = new LifecycleEngine(_index, _persistence);
        _embedding = new HashEmbeddingService();
    }

    private CognitiveEntry CreateEntry(string id, string ns = "test", string state = "stm")
    {
        var vector = _embedding.Embed($"test entry {id}");
        return new CognitiveEntry(id, vector, ns, $"Test entry {id}", lifecycleState: state);
    }

    [Fact]
    public void ApplyFeedback_PositiveDelta_BoostsActivationEnergy()
    {
        var entry = CreateEntry("e1");
        _index.Upsert(entry);

        var result = _lifecycle.ApplyFeedback("e1", 2.0f);

        Assert.NotNull(result);
        Assert.Equal("e1", result.Id);
        Assert.Equal(0f, result.PreviousActivationEnergy);
        Assert.Equal(2f, result.NewActivationEnergy);
    }

    [Fact]
    public void ApplyFeedback_NegativeDelta_ReducesActivationEnergy()
    {
        var entry = CreateEntry("e1");
        _index.Upsert(entry);

        var result = _lifecycle.ApplyFeedback("e1", -3.0f);

        Assert.NotNull(result);
        Assert.Equal(-3f, result.NewActivationEnergy);
    }

    [Fact]
    public void ApplyFeedback_NonExistentId_ReturnsNull()
    {
        Assert.Null(_lifecycle.ApplyFeedback("nonexistent", 1.0f));
    }

    [Fact]
    public void ApplyFeedback_DeltaClamped()
    {
        var entry = CreateEntry("e1");
        _index.Upsert(entry);

        var result = _lifecycle.ApplyFeedback("e1", 100f); // Should clamp to 10
        Assert.Equal(10f, result!.NewActivationEnergy);

        result = _lifecycle.ApplyFeedback("e1", -200f); // Should clamp to -10
        Assert.Equal(0f, result!.NewActivationEnergy); // 10 + (-10) = 0
    }

    [Fact]
    public void ApplyFeedback_PositiveDelta_RecordsAccess()
    {
        var entry = CreateEntry("e1");
        _index.Upsert(entry);
        int initialAccess = entry.AccessCount;

        _lifecycle.ApplyFeedback("e1", 1.0f);

        var updated = _index.Get("e1");
        Assert.True(updated!.AccessCount > initialAccess);
    }

    [Fact]
    public void ApplyFeedback_NegativeDelta_DoesNotRecordAccess()
    {
        var entry = CreateEntry("e1");
        _index.Upsert(entry);
        int initialAccess = entry.AccessCount;

        _lifecycle.ApplyFeedback("e1", -1.0f);

        var updated = _index.Get("e1");
        Assert.Equal(initialAccess, updated!.AccessCount);
    }

    [Fact]
    public void ApplyFeedback_NegativeOnStm_TransitionsToLtm()
    {
        var entry = CreateEntry("e1", state: "stm");
        _index.Upsert(entry);

        // Negative feedback below stmThreshold (default 2.0) should transition to LTM
        var result = _lifecycle.ApplyFeedback("e1", -1.0f);

        Assert.True(result!.StateChanged);
        Assert.Equal("stm", result.PreviousState);
        Assert.Equal("ltm", result.NewState);
    }

    [Fact]
    public void ApplyFeedback_StrongNegativeOnLtm_TransitionsToArchived()
    {
        var entry = CreateEntry("e1", state: "ltm");
        _index.Upsert(entry);

        // Push well below archiveThreshold (default -5.0)
        var result = _lifecycle.ApplyFeedback("e1", -6.0f);

        Assert.True(result!.StateChanged);
        Assert.Equal("ltm", result.PreviousState);
        Assert.Equal("archived", result.NewState);
    }

    [Fact]
    public void ApplyFeedback_PositiveOnArchived_Resurrects()
    {
        var entry = CreateEntry("e1", state: "archived");
        _index.Upsert(entry);

        // Strong positive feedback should resurrect from archived
        var result = _lifecycle.ApplyFeedback("e1", 5.0f);

        Assert.True(result!.StateChanged);
        Assert.Equal("archived", result.PreviousState);
        Assert.Equal("stm", result.NewState); // Energy 5.0 >= stmThreshold 2.0
    }

    [Fact]
    public void ApplyFeedback_MildPositiveOnArchived_GoesToLtm()
    {
        var entry = CreateEntry("e1", state: "archived");
        _index.Upsert(entry);

        // Mild positive: energy 1.0 < stmThreshold 2.0 → LTM
        var result = _lifecycle.ApplyFeedback("e1", 1.0f);

        Assert.True(result!.StateChanged);
        Assert.Equal("archived", result.PreviousState);
        Assert.Equal("ltm", result.NewState);
    }

    [Fact]
    public void ApplyFeedback_Cumulative()
    {
        var entry = CreateEntry("e1");
        _index.Upsert(entry);

        _lifecycle.ApplyFeedback("e1", 3.0f);
        _lifecycle.ApplyFeedback("e1", 2.0f);
        var result = _lifecycle.ApplyFeedback("e1", 1.0f);

        Assert.Equal(6f, result!.NewActivationEnergy);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, true);
    }
}
