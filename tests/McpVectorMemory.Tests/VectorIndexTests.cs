using McpVectorMemory;

namespace McpVectorMemory.Tests;

public class VectorIndexTests
{
    // ── Count ────────────────────────────────────────────────────────────────

    [Fact]
    public void Count_EmptyIndex_ReturnsZero()
    {
        var index = new VectorIndex();
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void Count_AfterUpsert_ReturnsOne()
    {
        var index = new VectorIndex();
        index.Upsert(new VectorEntry("a", new float[] { 1f, 0f }));
        Assert.Equal(1, index.Count);
    }

    // ── Upsert ───────────────────────────────────────────────────────────────

    [Fact]
    public void Upsert_SameId_Replaces()
    {
        var index = new VectorIndex();
        index.Upsert(new VectorEntry("a", new float[] { 1f, 0f }, "first"));
        index.Upsert(new VectorEntry("a", new float[] { 0f, 1f }, "second"));

        Assert.Equal(1, index.Count);
        var results = index.Search(new float[] { 0f, 1f }, k: 1);
        Assert.Equal("second", results[0].Entry.Text);
    }

    [Fact]
    public void Upsert_NullEntry_Throws()
    {
        var index = new VectorIndex();
        Assert.Throws<ArgumentNullException>(() => index.Upsert(null!));
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_ExistingEntry_ReturnsTrueAndReducesCount()
    {
        var index = new VectorIndex();
        index.Upsert(new VectorEntry("a", new float[] { 1f, 0f }));
        bool removed = index.Delete("a");
        Assert.True(removed);
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void Delete_NonExistentId_ReturnsFalse()
    {
        var index = new VectorIndex();
        Assert.False(index.Delete("missing"));
    }

    // ── Search ───────────────────────────────────────────────────────────────

    [Fact]
    public void Search_ExactMatch_ReturnsScoreOfOne()
    {
        var index = new VectorIndex();
        index.Upsert(new VectorEntry("a", new float[] { 1f, 0f, 0f }));

        var results = index.Search(new float[] { 1f, 0f, 0f }, k: 1);

        Assert.Single(results);
        Assert.Equal("a", results[0].Entry.Id);
        Assert.Equal(1f, results[0].Score, precision: 5);
    }

    [Fact]
    public void Search_OppositeDirection_ReturnsScoreOfNegativeOne()
    {
        var index = new VectorIndex();
        index.Upsert(new VectorEntry("a", new float[] { 1f, 0f }));

        var results = index.Search(new float[] { -1f, 0f }, k: 1, minScore: -1f);

        Assert.Single(results);
        Assert.Equal(-1f, results[0].Score, precision: 5);
    }

    [Fact]
    public void Search_ReturnsKNearest()
    {
        var index = new VectorIndex();
        index.Upsert(new VectorEntry("close",  new float[] { 1f, 0.1f }));
        index.Upsert(new VectorEntry("medium", new float[] { 0.5f, 0.5f }));
        index.Upsert(new VectorEntry("far",    new float[] { 0f, 1f }));

        var results = index.Search(new float[] { 1f, 0f }, k: 2);

        Assert.Equal(2, results.Count);
        Assert.Equal("close", results[0].Entry.Id);
    }

    [Fact]
    public void Search_MinScore_FiltersLowScores()
    {
        var index = new VectorIndex();
        index.Upsert(new VectorEntry("a", new float[] { 1f, 0f }));
        index.Upsert(new VectorEntry("b", new float[] { 0f, 1f }));

        var results = index.Search(new float[] { 1f, 0f }, k: 10, minScore: 0.99f);

        Assert.Single(results);
        Assert.Equal("a", results[0].Entry.Id);
    }

    [Fact]
    public void Search_EmptyIndex_ReturnsEmpty()
    {
        var index = new VectorIndex();
        var results = index.Search(new float[] { 1f, 0f }, k: 5);
        Assert.Empty(results);
    }

    [Fact]
    public void Search_ZeroQueryVector_Throws()
    {
        var index = new VectorIndex();
        index.Upsert(new VectorEntry("a", new float[] { 1f, 0f }));

        Assert.Throws<ArgumentException>(() => index.Search(new float[] { 0f, 0f }, k: 5));
    }

    [Fact]
    public void Search_NegativeK_Throws()
    {
        var index = new VectorIndex();
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Search(new float[] { 1f, 0f }, k: -1));
    }

    [Fact]
    public void Search_ZeroK_Throws()
    {
        var index = new VectorIndex();
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Search(new float[] { 1f, 0f }, k: 0));
    }

    [Fact]
    public void Search_DimensionMismatch_SkipsEntry()
    {
        var index = new VectorIndex();
        index.Upsert(new VectorEntry("a", new float[] { 1f, 0f, 0f })); // 3-dim
        index.Upsert(new VectorEntry("b", new float[] { 1f, 0f }));     // 2-dim

        var results = index.Search(new float[] { 1f, 0f }, k: 5);        // 2-dim query

        Assert.Single(results);
        Assert.Equal("b", results[0].Entry.Id);
    }

    [Fact]
    public void Search_ResultsOrderedByDescendingScore()
    {
        var index = new VectorIndex();
        index.Upsert(new VectorEntry("low",  new float[] { 0f, 1f }));
        index.Upsert(new VectorEntry("mid",  new float[] { 0.7071f, 0.7071f }));
        index.Upsert(new VectorEntry("high", new float[] { 1f, 0f }));

        var results = index.Search(new float[] { 1f, 0f }, k: 3);

        Assert.Equal("high", results[0].Entry.Id);
        Assert.Equal("mid",  results[1].Entry.Id);
        Assert.Equal("low",  results[2].Entry.Id);
    }

    // ── Concurrency ──────────────────────────────────────────────────────────

    [Fact]
    public void ConcurrentUpsertAndSearch_DoesNotThrow()
    {
        var index = new VectorIndex();
        var tasks = new List<Task>();

        for (int i = 0; i < 100; i++)
        {
            int id = i;
            tasks.Add(Task.Run(() =>
                index.Upsert(new VectorEntry($"v{id}", new float[] { id + 1f, id + 2f }))));
        }

        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
                index.Search(new float[] { 1f, 2f }, k: 5)));
        }

        Task.WaitAll(tasks.ToArray());
        Assert.Equal(100, index.Count);
    }

    // ── Dispose ──────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_CanBeCalledSafely()
    {
        var index = new VectorIndex();
        index.Upsert(new VectorEntry("a", new float[] { 1f, 0f }));
        index.Dispose();
    }

    // ── VectorEntry validation ───────────────────────────────────────────────

    [Fact]
    public void VectorEntry_EmptyId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new VectorEntry("", new float[] { 1f }));
    }

    [Fact]
    public void VectorEntry_EmptyVector_Throws()
    {
        Assert.Throws<ArgumentException>(() => new VectorEntry("a", Array.Empty<float>()));
    }

    [Fact]
    public void VectorEntry_NullVector_Throws()
    {
        Assert.Throws<ArgumentException>(() => new VectorEntry("a", null!));
    }

    [Fact]
    public void VectorEntry_DefensiveCopy_VectorNotMutatedExternally()
    {
        var original = new float[] { 1f, 2f, 3f };
        var entry = new VectorEntry("a", original);
        original[0] = 999f;
        Assert.Equal(1f, entry.Vector[0]);
    }

    [Fact]
    public void VectorEntry_DefensiveCopy_MetadataNotMutatedExternally()
    {
        var metadata = new Dictionary<string, string> { ["key"] = "value" };
        var entry = new VectorEntry("a", new float[] { 1f }, metadata: metadata);
        metadata["key"] = "changed";
        Assert.Equal("value", entry.Metadata["key"]);
    }
}
