using McpEngramMemory.Core.Services.Retrieval;

namespace McpEngramMemory.Tests;

public class HnswIndexTests
{
    private static float[] RandomVector(int dim, Random rng)
    {
        var v = new float[dim];
        for (int i = 0; i < dim; i++)
            v[i] = (float)(rng.NextDouble() * 2 - 1);
        return v;
    }

    [Fact]
    public void Add_SingleEntry_CountIs1()
    {
        var index = new HnswIndex();
        index.Add("a", new float[] { 1, 0, 0 });
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void Search_FindsExactMatch()
    {
        var index = new HnswIndex();
        index.Add("a", new float[] { 1, 0, 0 });
        index.Add("b", new float[] { 0, 1, 0 });
        index.Add("c", new float[] { 0, 0, 1 });

        var results = index.Search(new float[] { 1, 0, 0 }, 1);
        Assert.Single(results);
        Assert.Equal("a", results[0].Id);
        Assert.True(results[0].Score > 0.99f);
    }

    [Fact]
    public void Search_ReturnsKResults()
    {
        var index = new HnswIndex();
        index.Add("a", new float[] { 1, 0, 0 });
        index.Add("b", new float[] { 0.9f, 0.1f, 0 });
        index.Add("c", new float[] { 0, 1, 0 });
        index.Add("d", new float[] { 0, 0, 1 });

        var results = index.Search(new float[] { 1, 0, 0 }, 2);
        Assert.Equal(2, results.Count);
        Assert.Equal("a", results[0].Id);
        Assert.Equal("b", results[1].Id);
    }

    [Fact]
    public void Search_EmptyIndex_ReturnsEmpty()
    {
        var index = new HnswIndex();
        var results = index.Search(new float[] { 1, 0, 0 }, 5);
        Assert.Empty(results);
    }

    [Fact]
    public void Remove_ExcludesFromResults()
    {
        var index = new HnswIndex();
        index.Add("a", new float[] { 1, 0, 0 });
        index.Add("b", new float[] { 0.9f, 0.1f, 0 });

        Assert.True(index.Remove("a"));
        Assert.Equal(1, index.Count);

        var results = index.Search(new float[] { 1, 0, 0 }, 5);
        Assert.Single(results);
        Assert.Equal("b", results[0].Id);
    }

    [Fact]
    public void Remove_NonExistent_ReturnsFalse()
    {
        var index = new HnswIndex();
        Assert.False(index.Remove("nonexistent"));
    }

    [Fact]
    public void Add_DuplicateId_ReplacesEntry()
    {
        var index = new HnswIndex();
        index.Add("a", new float[] { 1, 0, 0 });
        index.Add("a", new float[] { 0, 1, 0 }); // Replace

        Assert.Equal(1, index.Count);
        var results = index.Search(new float[] { 0, 1, 0 }, 1);
        Assert.Equal("a", results[0].Id);
        Assert.True(results[0].Score > 0.99f);
    }

    [Fact]
    public void Search_ZeroVector_ReturnsEmpty()
    {
        var index = new HnswIndex();
        index.Add("a", new float[] { 1, 0, 0 });
        var results = index.Search(new float[] { 0, 0, 0 }, 1);
        Assert.Empty(results);
    }

    [Fact]
    public void Add_ZeroVector_IsIgnored()
    {
        var index = new HnswIndex();
        index.Add("zero", new float[] { 0, 0, 0 });
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void NeedsRebuild_FalseWhenFewDeletes()
    {
        var index = new HnswIndex();
        for (int i = 0; i < 10; i++)
            index.Add($"v{i}", new float[] { i, 0, 0 });

        index.Remove("v0");
        Assert.False(index.NeedsRebuild);
    }

    [Fact]
    public void Rebuild_ProducesEquivalentIndex()
    {
        var rng = new Random(42);
        var index = new HnswIndex(m: 8, efConstruction: 50);
        var vectors = new Dictionary<string, float[]>();

        for (int i = 0; i < 50; i++)
        {
            var v = RandomVector(32, rng);
            vectors[$"v{i}"] = v;
            index.Add($"v{i}", v);
        }

        // Delete some entries
        for (int i = 0; i < 15; i++)
            index.Remove($"v{i}");

        var rebuilt = index.Rebuild();
        Assert.Equal(35, rebuilt.Count);

        // Search should find the same nearest neighbor
        var query = RandomVector(32, rng);
        var origResults = index.Search(query, 5);
        var rebuildResults = rebuilt.Search(query, 5);

        // Both should find the same top result
        Assert.Equal(origResults[0].Id, rebuildResults[0].Id);
    }

    [Fact]
    public void Search_HighDimensional_FindsNearestNeighbor()
    {
        var rng = new Random(123);
        var index = new HnswIndex(m: 16, efConstruction: 100);

        // Insert 200 random 384-dim vectors (matching bge-micro-v2 dimensions)
        var vectors = new Dictionary<string, float[]>();
        for (int i = 0; i < 200; i++)
        {
            var v = RandomVector(384, rng);
            vectors[$"v{i}"] = v;
            index.Add($"v{i}", v);
        }

        Assert.Equal(200, index.Count);

        // Brute-force find the true nearest neighbor
        var query = RandomVector(384, rng);
        float queryNorm = VectorMath.Norm(query);
        string? trueNearest = null;
        float bestScore = float.MinValue;

        foreach (var (id, vec) in vectors)
        {
            float dot = VectorMath.Dot(query, vec);
            float score = dot / (queryNorm * VectorMath.Norm(vec));
            if (score > bestScore)
            {
                bestScore = score;
                trueNearest = id;
            }
        }

        // HNSW should find it (with high probability for well-constructed index)
        var results = index.Search(query, 10);
        Assert.Contains(results, r => r.Id == trueNearest);
    }

    [Fact]
    public void Search_ScoresAreDescending()
    {
        var rng = new Random(77);
        var index = new HnswIndex();

        for (int i = 0; i < 50; i++)
            index.Add($"v{i}", RandomVector(16, rng));

        var results = index.Search(RandomVector(16, rng), 10);
        for (int i = 1; i < results.Count; i++)
            Assert.True(results[i - 1].Score >= results[i].Score);
    }

    [Fact]
    public void RemoveEntryPoint_StillSearchable()
    {
        var index = new HnswIndex();
        index.Add("a", new float[] { 1, 0, 0 });
        index.Add("b", new float[] { 0, 1, 0 });
        index.Add("c", new float[] { 0, 0, 1 });

        // First entry is likely the entry point
        index.Remove("a");

        var results = index.Search(new float[] { 0, 1, 0 }, 1);
        Assert.Single(results);
        Assert.Equal("b", results[0].Id);
    }
}
