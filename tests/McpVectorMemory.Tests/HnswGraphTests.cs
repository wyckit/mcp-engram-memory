using McpVectorMemory;

namespace McpVectorMemory.Tests;

public class HnswGraphTests
{
    // ── Add & Search basics ─────────────────────────────────────────────────

    [Fact]
    public void Search_EmptyGraph_ReturnsEmpty()
    {
        var graph = new HnswGraph(m: 4, efConstruction: 16);
        var results = graph.Search(new float[] { 1f, 0f }, k: 5, ef: 10);
        Assert.Empty(results);
    }

    [Fact]
    public void Add_SingleNode_SearchFindsIt()
    {
        var graph = new HnswGraph(m: 4, efConstruction: 16, seed: 42);
        graph.Add(0, new float[] { 1f, 0f });

        var results = graph.Search(new float[] { 1f, 0f }, k: 1, ef: 10);
        Assert.Single(results);
        Assert.Equal(0, results[0].Id);
        Assert.Equal(0f, results[0].Distance, precision: 5); // identical vector = 0 distance
    }

    [Fact]
    public void Search_FindsNearestNeighbor()
    {
        var graph = new HnswGraph(m: 4, efConstruction: 50, seed: 42);
        graph.Add(0, new float[] { 1f, 0f });    // close to query
        graph.Add(1, new float[] { 0f, 1f });    // far from query
        graph.Add(2, new float[] { 0.9f, 0.1f }); // also close

        var results = graph.Search(new float[] { 1f, 0f }, k: 1, ef: 10);
        Assert.Single(results);
        Assert.Equal(0, results[0].Id);
    }

    [Fact]
    public void Search_ReturnsKResults()
    {
        var graph = new HnswGraph(m: 4, efConstruction: 50, seed: 42);
        for (int i = 0; i < 10; i++)
        {
            float angle = MathF.PI * i / 9f; // spread across 0 to PI
            graph.Add(i, new float[] { MathF.Cos(angle), MathF.Sin(angle) });
        }

        var results = graph.Search(new float[] { 1f, 0f }, k: 3, ef: 20);
        Assert.Equal(3, results.Count);
        // Results should be sorted by distance ascending
        Assert.True(results[0].Distance <= results[1].Distance);
        Assert.True(results[1].Distance <= results[2].Distance);
    }

    [Fact]
    public void Search_ResultsSortedByDistanceAscending()
    {
        var graph = new HnswGraph(m: 4, efConstruction: 50, seed: 42);
        graph.Add(0, new float[] { 1f, 0f });
        graph.Add(1, new float[] { 0.7071f, 0.7071f });
        graph.Add(2, new float[] { 0f, 1f });

        var results = graph.Search(new float[] { 1f, 0f }, k: 3, ef: 20);
        Assert.Equal(3, results.Count);
        for (int i = 0; i < results.Count - 1; i++)
            Assert.True(results[i].Distance <= results[i + 1].Distance);
    }

    // ── MarkDeleted ─────────────────────────────────────────────────────────

    [Fact]
    public void MarkDeleted_ExcludesFromSearchResults()
    {
        var graph = new HnswGraph(m: 4, efConstruction: 50, seed: 42);
        graph.Add(0, new float[] { 1f, 0f });
        graph.Add(1, new float[] { 0f, 1f });

        graph.MarkDeleted(0);

        var results = graph.Search(new float[] { 1f, 0f }, k: 5, ef: 20);
        Assert.Single(results);
        Assert.Equal(1, results[0].Id);
    }

    [Fact]
    public void MarkDeleted_AllNodes_SearchReturnsEmpty()
    {
        var graph = new HnswGraph(m: 4, efConstruction: 50, seed: 42);
        graph.Add(0, new float[] { 1f, 0f });
        graph.Add(1, new float[] { 0f, 1f });

        graph.MarkDeleted(0);
        graph.MarkDeleted(1);

        var results = graph.Search(new float[] { 1f, 0f }, k: 5, ef: 20);
        Assert.Empty(results);
    }

    // ── Compact ─────────────────────────────────────────────────────────────

    [Fact]
    public void Compact_RemovesDeletedNodes()
    {
        var graph = new HnswGraph(m: 4, efConstruction: 50, seed: 42);
        graph.Add(0, new float[] { 1f, 0f });
        graph.Add(1, new float[] { 0f, 1f });

        graph.MarkDeleted(0);
        bool compacted = graph.Compact();

        Assert.True(compacted);

        var results = graph.Search(new float[] { 1f, 0f }, k: 5, ef: 20);
        Assert.Single(results);
        Assert.Equal(1, results[0].Id);
    }

    [Fact]
    public void Compact_NothingDeleted_ReturnsFalse()
    {
        var graph = new HnswGraph(m: 4, efConstruction: 50, seed: 42);
        graph.Add(0, new float[] { 1f, 0f });
        Assert.False(graph.Compact());
    }

    // ── Add duplicate ID ──────────────────────────────────────────────────

    [Fact]
    public void Add_DuplicateId_Throws()
    {
        var graph = new HnswGraph(m: 4, efConstruction: 50, seed: 42);
        graph.Add(0, new float[] { 1f, 0f });
        Assert.Throws<ArgumentException>(() => graph.Add(0, new float[] { 0f, 1f }));
    }

    // ── Larger scale ────────────────────────────────────────────────────────

    [Fact]
    public void Search_100Nodes_FindsExactMatch()
    {
        var graph = new HnswGraph(m: 16, efConstruction: 200, seed: 42);
        for (int i = 0; i < 100; i++)
        {
            float angle = 2f * MathF.PI * i / 100f;
            graph.Add(i, new float[] { MathF.Cos(angle), MathF.Sin(angle) });
        }

        // Query exactly matches node 0 (angle=0 → [1, 0])
        var results = graph.Search(new float[] { 1f, 0f }, k: 1, ef: 50);
        Assert.Single(results);
        Assert.Equal(0, results[0].Id);
        Assert.True(results[0].Distance < 0.001f);
    }

    [Fact]
    public void Search_HighDimensional_FindsNearest()
    {
        var graph = new HnswGraph(m: 16, efConstruction: 200, seed: 42);
        var rng = new Random(123);

        // Insert 50 random 128-dim vectors
        var vectors = new float[50][];
        for (int i = 0; i < 50; i++)
        {
            vectors[i] = new float[128];
            for (int j = 0; j < 128; j++)
                vectors[i][j] = (float)(rng.NextDouble() * 2 - 1);
            graph.Add(i, vectors[i]);
        }

        // Query is exactly vectors[25]
        var results = graph.Search(vectors[25], k: 1, ef: 50);
        Assert.Single(results);
        Assert.Equal(25, results[0].Id);
    }

    // ── Compact edge cases ────────────────────────────────────────────────

    [Fact]
    public void Compact_EntryPointDeleted_PicksNewEntryPoint()
    {
        var graph = new HnswGraph(m: 4, efConstruction: 50, seed: 42);
        graph.Add(0, new float[] { 1f, 0f });
        graph.Add(1, new float[] { 0f, 1f });
        graph.Add(2, new float[] { 0.7071f, 0.7071f });

        // Delete the first node (likely entry point)
        graph.MarkDeleted(0);
        graph.Compact();

        // Search should still work with the remaining nodes
        var results = graph.Search(new float[] { 0f, 1f }, k: 1, ef: 20);
        Assert.Single(results);
        Assert.Equal(1, results[0].Id);
    }

    [Fact]
    public void Compact_AllNodesDeleted_SearchReturnsEmpty()
    {
        var graph = new HnswGraph(m: 4, efConstruction: 50, seed: 42);
        graph.Add(0, new float[] { 1f, 0f });
        graph.Add(1, new float[] { 0f, 1f });

        graph.MarkDeleted(0);
        graph.MarkDeleted(1);
        graph.Compact();

        var results = graph.Search(new float[] { 1f, 0f }, k: 5, ef: 20);
        Assert.Empty(results);
    }

    [Fact]
    public void Compact_GraphRemainsSearchableAfterPartialCompaction()
    {
        var graph = new HnswGraph(m: 4, efConstruction: 50, seed: 42);
        for (int i = 0; i < 20; i++)
        {
            float angle = 2f * MathF.PI * i / 20f;
            graph.Add(i, new float[] { MathF.Cos(angle), MathF.Sin(angle) });
        }

        // Delete every other node
        for (int i = 0; i < 20; i += 2)
            graph.MarkDeleted(i);
        graph.Compact();

        // Remaining nodes (odd) should still be searchable
        var results = graph.Search(new float[] { 1f, 0f }, k: 5, ef: 50);
        Assert.True(results.Count > 0);
        Assert.All(results, r => Assert.True(r.Id % 2 == 1)); // all odd
    }

    // ── Constructor validation ──────────────────────────────────────────────

    [Fact]
    public void Constructor_M1_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HnswGraph(m: 1));
    }

    [Fact]
    public void Constructor_ZeroM_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HnswGraph(m: 0));
    }

    [Fact]
    public void Constructor_ZeroEfConstruction_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HnswGraph(m: 4, efConstruction: 0));
    }
}
