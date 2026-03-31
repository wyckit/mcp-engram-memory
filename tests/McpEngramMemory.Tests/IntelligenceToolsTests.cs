using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class IntelligenceToolsTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly LifecycleEngine _lifecycle;
    private readonly AccretionScanner _scanner;
    private readonly IntelligenceTools _tools;

    private sealed class StubEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    public IntelligenceToolsTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"inteltools_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
        _lifecycle = new LifecycleEngine(_index);
        _scanner = new AccretionScanner(_index);
        _tools = new IntelligenceTools(_index, _graph, new StubEmbeddingService(), _scanner, _clusters, _lifecycle);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    // ── DetectDuplicates ──

    [Fact]
    public void DetectDuplicates_HighSimilarity_FindsPairs()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", "first entry"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f }, "test", "second entry"));

        var result = (DuplicateDetectionResult)_tools.DetectDuplicates("test", threshold: 0.95f);
        Assert.True(result.Duplicates.Count > 0);
        Assert.Contains(result.Duplicates, d =>
            (d.EntryA.Id == "a" && d.EntryB.Id == "b") ||
            (d.EntryA.Id == "b" && d.EntryB.Id == "a"));
    }

    [Fact]
    public void DetectDuplicates_LowSimilarity_NoPairs()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", "first"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f }, "test", "second"));

        var result = (DuplicateDetectionResult)_tools.DetectDuplicates("test", threshold: 0.95f);
        Assert.Empty(result.Duplicates);
    }

    [Fact]
    public void DetectDuplicates_InvalidThreshold_ReturnsError()
    {
        var aboveOne = _tools.DetectDuplicates("test", threshold: 1.5f);
        Assert.IsType<string>(aboveOne);
        Assert.StartsWith("Error:", (string)aboveOne);

        var belowZero = _tools.DetectDuplicates("test", threshold: -0.1f);
        Assert.IsType<string>(belowZero);
        Assert.StartsWith("Error:", (string)belowZero);
    }

    [Fact]
    public void DetectDuplicates_EmptyNamespace_NoPairs()
    {
        var result = (DuplicateDetectionResult)_tools.DetectDuplicates("empty", threshold: 0.95f);
        Assert.Empty(result.Duplicates);
        Assert.Equal(0, result.ScannedCount);
    }

    [Fact]
    public void DetectDuplicates_CategoryFilter()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", "first", category: "notes"));
        _index.Upsert(new CognitiveEntry("b", new[] { 1f, 0f }, "test", "second", category: "notes"));
        _index.Upsert(new CognitiveEntry("c", new[] { 1f, 0f }, "test", "third", category: "other"));

        // With category filter, only "notes" entries are compared
        var filtered = (DuplicateDetectionResult)_tools.DetectDuplicates("test", threshold: 0.95f, category: "notes");
        Assert.Single(filtered.Duplicates);

        // Without filter, all 3 identical vectors form 3 pairs
        var all = (DuplicateDetectionResult)_tools.DetectDuplicates("test", threshold: 0.95f);
        Assert.Equal(3, all.Duplicates.Count);
    }

    // ── FindContradictions ──

    [Fact]
    public void FindContradictions_ExplicitEdges_Found()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", "cats are better"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f }, "test", "dogs are better"));
        _graph.AddEdge(new GraphEdge("a", "b", "contradicts", 0.9f));

        var result = (ContradictionResult)_tools.FindContradictions("test");
        Assert.Equal(1, result.GraphEdgeCount);
        Assert.Single(result.Contradictions);
        Assert.Equal("graph_edge", result.Contradictions[0].Source);
    }

    [Fact]
    public void FindContradictions_NoContradictions_ReturnsEmpty()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", "hello"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f }, "test", "world"));

        var result = (ContradictionResult)_tools.FindContradictions("test");
        Assert.Empty(result.Contradictions);
        Assert.Equal(0, result.GraphEdgeCount);
        Assert.Equal(0, result.HighSimilarityCount);
    }

    [Fact]
    public void FindContradictions_WithTopic_FindsHighSimilarity()
    {
        // Two entries with high similarity to each other and to the topic vector
        // Stub returns [0.5, 0.5] for any text, so store entries near that vector
        _index.Upsert(new CognitiveEntry("a", new[] { 0.5f, 0.5f }, "test", "statement A"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.5f, 0.5f }, "test", "statement B"));

        var result = (ContradictionResult)_tools.FindContradictions("test", topic: "relevant topic", similarityThreshold: 0.8f);
        Assert.True(result.HighSimilarityCount > 0);
        Assert.Contains(result.Contradictions, c => c.Source == "high_similarity");
    }

    // ── UncollapseCluster ──

    [Fact]
    public void UncollapseCluster_InvalidId_ReturnsError()
    {
        var result = _tools.UncollapseCluster("nonexistent-collapse-id");
        Assert.StartsWith("Error:", result);
    }

    // ── ListCollapseHistory ──

    [Fact]
    public void ListCollapseHistory_EmptyNamespace_ReturnsEmpty()
    {
        var history = _tools.ListCollapseHistory("nonexistent");
        Assert.Empty(history);
    }

    // ── MergeMemories ──

    [Fact]
    public void MergeMemories_ValidPair_MergesAndArchives()
    {
        _index.Upsert(new CognitiveEntry("keep", new[] { 1f, 0f }, "test", "keeper entry"));
        _index.Upsert(new CognitiveEntry("dup", new[] { 0.99f, 0.01f }, "test", "duplicate entry"));

        var result = _tools.MergeMemories("keep", "dup", "test");
        Assert.DoesNotContain("Error:", result);
        Assert.Contains("Merged", result);

        // Verify dup is archived
        var dupEntry = _index.Get("dup", "test");
        Assert.Equal("archived", dupEntry!.LifecycleState);

        // Verify keep is still active
        var keepEntry = _index.Get("keep", "test");
        Assert.NotEqual("archived", keepEntry!.LifecycleState);
    }

    [Fact]
    public void MergeMemories_NonExistentKeep_ReturnsError()
    {
        _index.Upsert(new CognitiveEntry("dup", new[] { 1f, 0f }, "test", "duplicate"));

        var result = _tools.MergeMemories("missing", "dup", "test");
        Assert.StartsWith("Error:", result);
        Assert.Contains("missing", result);
    }

    [Fact]
    public void MergeMemories_NonExistentArchive_ReturnsError()
    {
        _index.Upsert(new CognitiveEntry("keep", new[] { 1f, 0f }, "test", "keeper"));

        var result = _tools.MergeMemories("keep", "missing", "test");
        Assert.StartsWith("Error:", result);
        Assert.Contains("missing", result);
    }

    [Fact]
    public void MergeMemories_TransfersEdgesAndClusters()
    {
        _index.Upsert(new CognitiveEntry("keep", new[] { 1f, 0f }, "test", "keeper entry"));
        var dupEntry = new CognitiveEntry("dup", new[] { 0.99f, 0.01f }, "test", "duplicate entry");
        dupEntry.AccessCount = 3;
        _index.Upsert(dupEntry);
        _index.Upsert(new CognitiveEntry("other", new[] { 0f, 1f }, "test", "other entry"));

        // Add edges to the duplicate
        _graph.AddEdge(new GraphEdge("dup", "other", "similar_to"));
        _graph.AddEdge(new GraphEdge("other", "dup", "depends_on"));

        // Create cluster with dup as member
        _clusters.CreateCluster("c1", "test", new[] { "dup", "other" });

        // Merge
        var result = _tools.MergeMemories("keep", "dup", "test");
        Assert.DoesNotContain("Error:", result);

        // Verify access count transferred: keep started at 1, dup had 3 → keep should have 4
        var keepEntry = _index.Get("keep", "test");
        Assert.True(keepEntry!.AccessCount >= 4);

        // Verify dup is archived
        var archived = _index.Get("dup", "test");
        Assert.Equal("archived", archived!.LifecycleState);

        // Verify edges were transferred — keep now has edges
        var keepEdges = _graph.GetEdgesForEntry("keep");
        Assert.True(keepEdges.Count >= 2);

        // Verify cluster membership was transferred
        var clusters = _clusters.GetClustersForEntry("keep");
        Assert.Contains("c1", clusters);
    }
}
