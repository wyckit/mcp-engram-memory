using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class GraphToolsTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly GraphTools _tools;

    public GraphToolsTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"graphtools_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _tools = new GraphTools(_graph);

        // Seed entries
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", "entry a"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f }, "test", "entry b"));
        _index.Upsert(new CognitiveEntry("c", new[] { 1f, 1f }, "test", "entry c"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.5f, 0.5f }, "test", "entry d"));
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    // ── LinkMemories ──

    [Fact]
    public void LinkMemories_ValidInput_CreatesEdge()
    {
        var result = _tools.LinkMemories("a", "b", "similar_to");
        Assert.Contains("Linked", result);
        Assert.Contains("a", result);
        Assert.Contains("b", result);
        Assert.Equal(1, _graph.EdgeCount);
    }

    [Fact]
    public void LinkMemories_CrossReference_CreatesBidirectional()
    {
        var result = _tools.LinkMemories("a", "b", "cross_reference");
        Assert.Contains("bidirectional", result);
        Assert.Equal(2, _graph.EdgeCount); // both directions
    }

    [Fact]
    public void LinkMemories_WithWeightAndMetadata()
    {
        var metadata = new Dictionary<string, string> { ["reason"] = "test link" };
        var result = _tools.LinkMemories("a", "b", "elaborates", weight: 0.7f, metadata: metadata);

        Assert.Contains("Linked", result);

        var neighbors = _graph.GetNeighbors("a", direction: "outgoing");
        Assert.Single(neighbors.Neighbors);
        Assert.Equal(0.7f, neighbors.Neighbors[0].Edge.Weight);
        Assert.Equal("test link", neighbors.Neighbors[0].Edge.Metadata["reason"]);
    }

    [Fact]
    public void LinkMemories_EmptySourceId_ReturnsError()
    {
        var result = _tools.LinkMemories("", "b", "similar_to");
        Assert.StartsWith("Error:", result);
    }

    // ── UnlinkMemories ──

    [Fact]
    public void UnlinkMemories_ExistingEdge_Removes()
    {
        _tools.LinkMemories("a", "b", "similar_to");
        var result = _tools.UnlinkMemories("a", "b");

        Assert.Contains("Removed", result);
        Assert.Equal(0, _graph.EdgeCount);
    }

    [Fact]
    public void UnlinkMemories_SpecificRelation_RemovesOnlyThat()
    {
        _tools.LinkMemories("a", "b", "similar_to");
        _tools.LinkMemories("a", "b", "elaborates");

        var result = _tools.UnlinkMemories("a", "b", relation: "similar_to");
        Assert.Contains("Removed", result);

        var neighbors = _graph.GetNeighbors("a", direction: "outgoing");
        Assert.Single(neighbors.Neighbors);
        Assert.Equal("elaborates", neighbors.Neighbors[0].Edge.Relation);
    }

    [Fact]
    public void UnlinkMemories_NonExistent_HandlesGracefully()
    {
        var result = _tools.UnlinkMemories("a", "b");
        Assert.Contains("No edges found", result);
    }

    // ── GetNeighbors ──

    [Fact]
    public void GetNeighbors_OutgoingDirection()
    {
        _tools.LinkMemories("a", "b", "similar_to");
        _tools.LinkMemories("c", "a", "depends_on");

        var result = _tools.GetNeighbors("a", direction: "outgoing");
        Assert.Equal("a", result.Id);
        Assert.Single(result.Neighbors);
        Assert.Equal("b", result.Neighbors[0].Entry.Id);
    }

    [Fact]
    public void GetNeighbors_IncomingDirection()
    {
        _tools.LinkMemories("a", "b", "similar_to");
        _tools.LinkMemories("c", "a", "depends_on");

        var result = _tools.GetNeighbors("a", direction: "incoming");
        Assert.Single(result.Neighbors);
        Assert.Equal("c", result.Neighbors[0].Entry.Id);
    }

    [Fact]
    public void GetNeighbors_BothDirections()
    {
        _tools.LinkMemories("a", "b", "similar_to");
        _tools.LinkMemories("c", "a", "depends_on");

        var result = _tools.GetNeighbors("a", direction: "both");
        Assert.Equal(2, result.Neighbors.Count);
    }

    [Fact]
    public void GetNeighbors_FilterByRelation()
    {
        _tools.LinkMemories("a", "b", "similar_to");
        _tools.LinkMemories("a", "c", "depends_on");

        var result = _tools.GetNeighbors("a", relation: "similar_to", direction: "outgoing");
        Assert.Single(result.Neighbors);
        Assert.Equal("b", result.Neighbors[0].Entry.Id);
    }

    // ── TraverseGraph ──

    [Fact]
    public void TraverseGraph_SingleHop()
    {
        _tools.LinkMemories("a", "b", "similar_to");
        _tools.LinkMemories("b", "c", "similar_to");

        var result = _tools.TraverseGraph("a", maxDepth: 1);
        Assert.Equal("a", result.StartId);
        Assert.Equal(2, result.Entries.Count); // a, b
        Assert.Single(result.Edges);
    }

    [Fact]
    public void TraverseGraph_MultiHop()
    {
        _tools.LinkMemories("a", "b", "similar_to");
        _tools.LinkMemories("b", "c", "similar_to");

        var result = _tools.TraverseGraph("a", maxDepth: 2);
        Assert.Equal(3, result.Entries.Count); // a, b, c
        Assert.Equal(2, result.Edges.Count);
    }

    [Fact]
    public void TraverseGraph_MaxDepthLimit()
    {
        _tools.LinkMemories("a", "b", "similar_to");
        _tools.LinkMemories("b", "c", "similar_to");
        _tools.LinkMemories("c", "d", "similar_to");

        var result = _tools.TraverseGraph("a", maxDepth: 1);
        Assert.Equal(2, result.Entries.Count); // a, b only — c and d beyond depth 1
    }

    [Fact]
    public void TraverseGraph_MinWeightFilter()
    {
        _tools.LinkMemories("a", "b", "similar_to", weight: 0.3f);
        _tools.LinkMemories("a", "c", "similar_to", weight: 0.8f);

        var result = _tools.TraverseGraph("a", maxDepth: 1, minWeight: 0.5f);
        Assert.Equal(2, result.Entries.Count); // a, c (b filtered out by weight)
        Assert.Single(result.Edges);
    }
}
