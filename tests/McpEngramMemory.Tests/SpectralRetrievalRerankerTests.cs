using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class SpectralRetrievalRerankerTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly MemoryDiffusionKernel _kernel;
    private readonly SpectralRetrievalReranker _reranker;

    public SpectralRetrievalRerankerTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"spectral_recall_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _kernel = new MemoryDiffusionKernel(_index, _graph);
        _reranker = new SpectralRetrievalReranker(_kernel);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    /// <summary>
    /// Mode None should preserve the input ordering exactly — passthrough.
    /// </summary>
    [Fact]
    public void NoneModeIsPassthrough()
    {
        const string ns = "passthrough";
        SeedClusterPlusIsolated(ns, clusterSize: 16, isolatedCount: 16);

        var input = new List<(string Id, float Score)>
        {
            ("c_5", 0.9f),
            ("iso_3", 0.7f),
            ("c_2", 0.5f),
        };
        var output = _reranker.Rerank(ns, input, SpectralRetrievalMode.None, topK: 3);

        Assert.Equal(3, output.Count);
        Assert.Equal("c_5", output[0].Id);
        Assert.Equal("iso_3", output[1].Id);
        Assert.Equal("c_2", output[2].Id);
    }

    /// <summary>
    /// Broad mode (low-pass) should boost members of a cluster where any one
    /// member scored well, surfacing them ahead of an isolated entry that
    /// scored only slightly higher individually.
    /// </summary>
    [Fact]
    public void BroadModeBoostsClusterMembers()
    {
        const string ns = "broad";
        SeedClusterPlusIsolated(ns, clusterSize: 16, isolatedCount: 16);

        // One cluster member scores high; an isolated entry scores slightly
        // higher individually. Without spectral, the isolated entry wins.
        var input = new List<(string Id, float Score)>
        {
            ("iso_0", 0.95f),
            ("c_0", 0.90f),
        };

        var noneResult = _reranker.Rerank(ns, input, SpectralRetrievalMode.None, topK: 5);
        var broadResult = _reranker.Rerank(ns, input, SpectralRetrievalMode.Broad, topK: 5);

        // Without spectral: isolated wins.
        Assert.Equal("iso_0", noneResult[0].Id);

        // With broad: at least one cluster member outranks the isolated entry —
        // the cluster's diffused score lifts its members above the singleton.
        bool clusterMemberFirst = broadResult.Count > 0 && broadResult[0].Id.StartsWith("c_");
        Assert.True(clusterMemberFirst,
            $"Broad mode should surface cluster members first; got top result {broadResult[0].Id}.");

        // Multiple cluster members should appear in the top results since
        // the diffused signal spreads c_0's score through the cluster.
        int clusterCount = broadResult.Count(r => r.Id.StartsWith("c_"));
        Assert.True(clusterCount >= 2,
            $"Broad mode should surface multiple cluster members; got {clusterCount}.");
    }

    /// <summary>
    /// Specific mode (high-pass) should preserve outliers — entries that score
    /// high relative to their cluster — better than Broad mode would.
    /// </summary>
    [Fact]
    public void SpecificModePreservesOutliers()
    {
        const string ns = "specific";
        SeedClusterPlusIsolated(ns, clusterSize: 16, isolatedCount: 16);

        // c_0 scores high (it's the actual answer); cluster mean is 0 (no
        // others matched). Isolated iso_0 also scores high.
        var input = new List<(string Id, float Score)>
        {
            ("c_0", 0.9f),
            ("iso_0", 0.85f),
        };

        var specificResult = _reranker.Rerank(ns, input, SpectralRetrievalMode.Specific, topK: 5);

        // Specific mode subtracts the cluster mean, so c_0's score after
        // re-ranking reflects how much it stands out from its cluster (which
        // mostly scored zero) — it should still rank highly.
        Assert.NotEmpty(specificResult);
        Assert.Contains(specificResult, r => r.Id == "c_0");
        Assert.Contains(specificResult, r => r.Id == "iso_0");
    }

    /// <summary>
    /// When the namespace doesn't qualify for a diffusion basis (too small),
    /// the reranker must fall back to passthrough sort regardless of mode.
    /// </summary>
    [Fact]
    public void NoQualifyingBasisFallsBackToPassthrough()
    {
        const string ns = "tiny";
        for (int i = 0; i < 10; i++)
            _index.Upsert(new CognitiveEntry($"t_{i}", new[] { (float)i, 0f }, ns));

        var input = new List<(string Id, float Score)>
        {
            ("t_3", 0.5f),
            ("t_1", 0.9f),
            ("t_7", 0.7f),
        };
        var result = _reranker.Rerank(ns, input, SpectralRetrievalMode.Broad, topK: 3);

        // Sorted descending by score; no spectral effect.
        Assert.Equal("t_1", result[0].Id);
        Assert.Equal("t_7", result[1].Id);
        Assert.Equal("t_3", result[2].Id);
    }

    /// <summary>
    /// Empty input returns empty output without error.
    /// </summary>
    [Fact]
    public void EmptyInputReturnsEmpty()
    {
        var output = _reranker.Rerank("anywhere",
            Array.Empty<(string, float)>(), SpectralRetrievalMode.Broad, topK: 5);
        Assert.Empty(output);
    }

    /// <summary>
    /// topK cap is honored: even if many entries surface spectrally, only K
    /// are returned.
    /// </summary>
    [Fact]
    public void TopKCapHonored()
    {
        const string ns = "capped";
        SeedClusterPlusIsolated(ns, clusterSize: 16, isolatedCount: 16);

        var input = new List<(string Id, float Score)>
        {
            ("c_0", 0.9f),
        };
        var result = _reranker.Rerank(ns, input, SpectralRetrievalMode.Broad, topK: 3);
        Assert.True(result.Count <= 3);
    }

    private void SeedClusterPlusIsolated(string ns, int clusterSize, int isolatedCount)
    {
        var rng = new Random(99);
        for (int i = 0; i < clusterSize; i++)
            _index.Upsert(new CognitiveEntry($"c_{i}", new[] { (float)i, 0f }, ns, $"cluster {i}"));
        for (int i = 0; i < isolatedCount; i++)
            _index.Upsert(new CognitiveEntry($"iso_{i}", new[] { 100f + i, 0f }, ns, $"isolated {i}"));

        for (int i = 0; i < clusterSize; i++)
            for (int j = i + 1; j < clusterSize; j++)
                if (rng.NextDouble() < 0.5)
                    _graph.AddEdge(new GraphEdge($"c_{i}", $"c_{j}", "similar_to", 1.0f));
    }
}
