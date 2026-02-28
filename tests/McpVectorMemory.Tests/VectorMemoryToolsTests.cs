using McpVectorMemory;

namespace McpVectorMemory.Tests;

public class VectorMemoryToolsTests
{
    private readonly VectorIndex _index = new();
    private readonly VectorMemoryTools _tools;

    public VectorMemoryToolsTests()
    {
        _tools = new VectorMemoryTools(_index);
    }

    // ── StoreMemory ──────────────────────────────────────────────────────────

    [Fact]
    public void StoreMemory_ValidInput_StoresAndReturnsMessage()
    {
        string result = _tools.StoreMemory("test1", new float[] { 1f, 0f }, "hello");
        Assert.Contains("test1", result);
        Assert.Contains("2-dim", result);
        Assert.Equal(1, _index.Count);
    }

    [Fact]
    public void StoreMemory_WithMetadata_StoresSuccessfully()
    {
        var metadata = new Dictionary<string, string> { ["source"] = "test" };
        string result = _tools.StoreMemory("m1", new float[] { 1f, 2f }, "text", metadata);
        Assert.Contains("m1", result);
        Assert.Equal(1, _index.Count);
    }

    [Fact]
    public void StoreMemory_EmptyId_ReturnsError()
    {
        string result = _tools.StoreMemory("", new float[] { 1f, 0f });
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void StoreMemory_EmptyVector_ReturnsError()
    {
        string result = _tools.StoreMemory("test1", Array.Empty<float>());
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void StoreMemory_SameId_Replaces()
    {
        _tools.StoreMemory("a", new float[] { 1f, 0f }, "first");
        _tools.StoreMemory("a", new float[] { 0f, 1f }, "second");
        Assert.Equal(1, _index.Count);
    }

    [Fact]
    public void StoreMemory_ZeroMagnitudeVector_ReturnsError()
    {
        string result = _tools.StoreMemory("a", new float[] { 0f, 0f, 0f });
        Assert.StartsWith("Error:", result);
        Assert.Equal(0, _index.Count);
    }

    // ── SearchMemory ─────────────────────────────────────────────────────────

    [Fact]
    public void SearchMemory_ReturnsResults()
    {
        _tools.StoreMemory("a", new float[] { 1f, 0f }, "first");
        _tools.StoreMemory("b", new float[] { 0f, 1f }, "second");

        var result = _tools.SearchMemory(new float[] { 1f, 0f }, k: 1);
        var results = Assert.IsType<SearchResult[]>(result);
        Assert.Single(results);
        Assert.Equal("a", results[0].Entry.Id);
    }

    [Fact]
    public void SearchMemory_EmptyIndex_ReturnsEmpty()
    {
        var result = _tools.SearchMemory(new float[] { 1f, 0f });
        var results = Assert.IsType<SearchResult[]>(result);
        Assert.Empty(results);
    }

    [Fact]
    public void SearchMemory_ZeroMagnitudeVector_ReturnsError()
    {
        _tools.StoreMemory("a", new float[] { 1f, 0f });
        var result = _tools.SearchMemory(new float[] { 0f, 0f });
        Assert.IsType<string>(result);
        Assert.StartsWith("Error:", (string)result);
    }

    [Fact]
    public void SearchMemory_ZeroK_ReturnsError()
    {
        var result = _tools.SearchMemory(new float[] { 1f, 0f }, k: 0);
        Assert.IsType<string>(result);
        Assert.StartsWith("Error:", (string)result);
    }

    [Fact]
    public void SearchMemory_MinScoreOutOfRange_ReturnsError()
    {
        var result = _tools.SearchMemory(new float[] { 1f, 0f }, minScore: 1.5f);
        Assert.IsType<string>(result);
        Assert.StartsWith("Error:", (string)result);
    }

    // ── DeleteMemory ─────────────────────────────────────────────────────────

    [Fact]
    public void DeleteMemory_Existing_ReturnsDeleted()
    {
        _tools.StoreMemory("a", new float[] { 1f, 0f });
        string result = _tools.DeleteMemory("a");
        Assert.Contains("Deleted", result);
        Assert.Equal(0, _index.Count);
    }

    [Fact]
    public void DeleteMemory_NonExistent_ReturnsNotFound()
    {
        string result = _tools.DeleteMemory("missing");
        Assert.Contains("not found", result);
    }

    // ── Constructor ──────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullIndex_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new VectorMemoryTools(null!));
    }
}
