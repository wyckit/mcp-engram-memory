using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Retrieval;

namespace McpEngramMemory.Tests;

public class DiversityRerankerTests
{
    private static CognitiveSearchResult MakeResult(
        string id, float score, string? clusterId = null, string? category = null)
        => new(id, $"text-{id}", score, "ltm", 1f, category, null, false, clusterId);

    private static readonly float[] QueryVector = [1f, 0f, 0f];

    private static readonly Dictionary<string, float[]> Vectors = new()
    {
        ["a"] = [0.95f, 0.1f, 0f],
        ["b"] = [0.93f, 0.12f, 0f],
        ["c"] = [0.90f, 0.15f, 0f],
        ["d"] = [0.1f, 0.9f, 0.1f],
        ["e"] = [0.1f, 0.85f, 0.15f],
        ["f"] = [0.1f, 0.1f, 0.9f],
    };

    private static float[]? LookupVector(string id) =>
        Vectors.TryGetValue(id, out var v) ? v : null;

    // ── Basic behavior ──

    [Fact]
    public void Rerank_EmptyResults_ReturnsEmpty()
    {
        var results = DiversityReranker.Rerank(
            Array.Empty<CognitiveSearchResult>(), QueryVector, LookupVector, 5);
        Assert.Empty(results);
    }

    [Fact]
    public void Rerank_SingleResult_ReturnsSame()
    {
        var input = new[] { MakeResult("a", 0.95f) };
        var results = DiversityReranker.Rerank(input, QueryVector, LookupVector, 5);
        Assert.Single(results);
        Assert.Equal("a", results[0].Id);
    }

    [Fact]
    public void Rerank_KGreaterThanResults_ReturnsAll()
    {
        var input = new[] { MakeResult("a", 0.95f), MakeResult("d", 0.50f) };
        var results = DiversityReranker.Rerank(input, QueryVector, LookupVector, 10);
        Assert.Equal(2, results.Count);
    }

    // ── Diversity selection ──

    [Fact]
    public void Rerank_DiverseVectors_SpreadAcrossDirections()
    {
        // a, b, c are similar (all point roughly in x direction)
        // d, e are similar (y direction), f is unique (z direction)
        var input = new[]
        {
            MakeResult("a", 0.95f),
            MakeResult("b", 0.93f),
            MakeResult("c", 0.90f),
            MakeResult("d", 0.50f),
            MakeResult("e", 0.48f),
            MakeResult("f", 0.45f),
        };

        var results = DiversityReranker.Rerank(
            input, QueryVector, LookupVector, 3, lambda: 0.5f);

        // First pick should be 'a' (highest relevance)
        Assert.Equal("a", results[0].Id);

        // With lambda=0.5, MMR should prefer 'd' or 'f' over 'b' for diversity
        // (b is redundant with a, while d and f are in different directions)
        var secondAndThird = new HashSet<string> { results[1].Id, results[2].Id };
        Assert.True(secondAndThird.Contains("d") || secondAndThird.Contains("f"),
            $"Expected diversity to pull in d or f, got: {string.Join(", ", secondAndThird)}");
    }

    [Fact]
    public void Rerank_PureRelevance_Lambda1_PreservesOrder()
    {
        var input = new[]
        {
            MakeResult("a", 0.95f),
            MakeResult("b", 0.93f),
            MakeResult("c", 0.90f),
            MakeResult("d", 0.50f),
        };

        // Lambda = 1.0 means pure relevance, no diversity penalty
        var results = DiversityReranker.Rerank(
            input, QueryVector, LookupVector, 3, lambda: 1.0f);

        Assert.Equal("a", results[0].Id);
        Assert.Equal("b", results[1].Id);
        Assert.Equal("c", results[2].Id);
    }

    // ── Cluster penalty ──

    [Fact]
    public void Rerank_SameCluster_PenalizesDuplicateClusters()
    {
        // a and b are in cluster-1, d is in cluster-2
        var input = new[]
        {
            MakeResult("a", 0.95f, clusterId: "cluster-1"),
            MakeResult("b", 0.93f, clusterId: "cluster-1"),
            MakeResult("d", 0.50f, clusterId: "cluster-2"),
        };

        var results = DiversityReranker.Rerank(
            input, QueryVector, LookupVector, 2, lambda: 0.7f);

        Assert.Equal("a", results[0].Id);
        // d should beat b despite lower score due to cluster penalty on b
        Assert.Equal("d", results[1].Id);
    }

    [Fact]
    public void Rerank_DifferentClusters_NoPenalty()
    {
        var input = new[]
        {
            MakeResult("a", 0.95f, clusterId: "cluster-1"),
            MakeResult("b", 0.93f, clusterId: "cluster-2"),
            MakeResult("c", 0.90f, clusterId: "cluster-3"),
        };

        // All different clusters — no cluster penalty, pure relevance + vector diversity
        var results = DiversityReranker.Rerank(
            input, QueryVector, LookupVector, 3, lambda: 0.8f);

        Assert.Equal(3, results.Count);
        Assert.Equal("a", results[0].Id);
    }

    // ── Category penalty ──

    [Fact]
    public void Rerank_SameCategory_PenalizesDuplicateCategories()
    {
        var input = new[]
        {
            MakeResult("a", 0.95f, category: "architecture"),
            MakeResult("b", 0.93f, category: "architecture"),
            MakeResult("d", 0.50f, category: "bug-fix"),
        };

        // Lambda=0.5 gives equal weight to relevance and diversity,
        // allowing category + vector diversity to overcome the relevance gap
        var results = DiversityReranker.Rerank(
            input, QueryVector, LookupVector, 2, lambda: 0.5f);

        Assert.Equal("a", results[0].Id);
        // d should beat b: b has high vector redundancy with a + same category penalty
        Assert.Equal("d", results[1].Id);
    }

    // ── Edge cases ──

    [Fact]
    public void Rerank_MissingVectors_SkipsEntriesGracefully()
    {
        var input = new[]
        {
            MakeResult("a", 0.95f),
            MakeResult("unknown", 0.80f), // Not in vector lookup
            MakeResult("d", 0.50f),
        };

        var results = DiversityReranker.Rerank(
            input, QueryVector, LookupVector, 3);

        // Should skip 'unknown' since it has no vector
        Assert.Equal(2, results.Count);
        Assert.Equal("a", results[0].Id);
        Assert.Equal("d", results[1].Id);
    }

    [Fact]
    public void Rerank_NullClusterIds_NoPenalty()
    {
        var input = new[]
        {
            MakeResult("a", 0.95f, clusterId: null),
            MakeResult("b", 0.93f, clusterId: null),
        };

        var results = DiversityReranker.Rerank(
            input, QueryVector, LookupVector, 2, lambda: 0.5f);

        Assert.Equal(2, results.Count);
        // Without cluster info, only vector diversity matters
    }

    [Fact]
    public void Rerank_K1_ReturnsTopResult()
    {
        var input = new[]
        {
            MakeResult("a", 0.95f),
            MakeResult("d", 0.50f),
        };

        var results = DiversityReranker.Rerank(
            input, QueryVector, LookupVector, 1);

        Assert.Single(results);
        Assert.Equal("a", results[0].Id);
    }

    [Fact]
    public void Rerank_DefaultLambda_IsPointFive()
    {
        Assert.Equal(0.5f, DiversityReranker.DefaultLambda);
    }
}
