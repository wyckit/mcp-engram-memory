using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class KnowledgeGraphTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;

    public KnowledgeGraphTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"graph_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);

        // Seed entries
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", "entry a"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f }, "test", "entry b"));
        _index.Upsert(new CognitiveEntry("c", new[] { 1f, 1f }, "test", "entry c"));
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    [Fact]
    public void AddEdge_CreatesDirectedEdge()
    {
        var edge = new GraphEdge("a", "b", "similar_to");
        _graph.AddEdge(edge);
        Assert.Equal(1, _graph.EdgeCount);
    }

    [Fact]
    public void AddEdge_CrossReference_CreatesBidirectional()
    {
        var edge = new GraphEdge("a", "b", "cross_reference");
        _graph.AddEdge(edge);
        Assert.Equal(2, _graph.EdgeCount); // both directions
    }

    [Fact]
    public void RemoveEdges_RemovesSpecificRelation()
    {
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to"));
        _graph.AddEdge(new GraphEdge("a", "b", "elaborates"));
        _graph.RemoveEdges("a", "b", "similar_to");

        var neighbors = _graph.GetNeighbors("a", direction: "outgoing");
        Assert.Single(neighbors.Neighbors);
        Assert.Equal("elaborates", neighbors.Neighbors[0].Edge.Relation);
    }

    [Fact]
    public void RemoveEdges_AllRelations()
    {
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to"));
        _graph.AddEdge(new GraphEdge("a", "b", "elaborates"));
        _graph.RemoveEdges("a", "b");

        var neighbors = _graph.GetNeighbors("a", direction: "outgoing");
        Assert.Empty(neighbors.Neighbors);
    }

    [Fact]
    public void RemoveAllEdgesForEntry_CascadeDelete()
    {
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to"));
        _graph.AddEdge(new GraphEdge("c", "a", "depends_on"));
        int removed = _graph.RemoveAllEdgesForEntry("a");
        Assert.Equal(2, removed);
        Assert.Equal(0, _graph.EdgeCount);
    }

    [Fact]
    public void GetNeighbors_BothDirections()
    {
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to"));
        _graph.AddEdge(new GraphEdge("c", "a", "depends_on"));

        var neighbors = _graph.GetNeighbors("a");
        Assert.Equal(2, neighbors.Neighbors.Count);
    }

    [Fact]
    public void GetNeighbors_OutgoingOnly()
    {
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to"));
        _graph.AddEdge(new GraphEdge("c", "a", "depends_on"));

        var neighbors = _graph.GetNeighbors("a", direction: "outgoing");
        Assert.Single(neighbors.Neighbors);
        Assert.Equal("b", neighbors.Neighbors[0].Entry.Id);
    }

    [Fact]
    public void GetNeighbors_IncomingOnly()
    {
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to"));
        _graph.AddEdge(new GraphEdge("c", "a", "depends_on"));

        var neighbors = _graph.GetNeighbors("a", direction: "incoming");
        Assert.Single(neighbors.Neighbors);
        Assert.Equal("c", neighbors.Neighbors[0].Entry.Id);
    }

    [Fact]
    public void GetNeighbors_FilterByRelation()
    {
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to"));
        _graph.AddEdge(new GraphEdge("a", "c", "depends_on"));

        var neighbors = _graph.GetNeighbors("a", relation: "similar_to", direction: "outgoing");
        Assert.Single(neighbors.Neighbors);
        Assert.Equal("b", neighbors.Neighbors[0].Entry.Id);
    }

    [Fact]
    public void Traverse_MultiHop()
    {
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to"));
        _graph.AddEdge(new GraphEdge("b", "c", "similar_to"));

        var result = _graph.Traverse("a", maxDepth: 2);
        Assert.Equal(3, result.Entries.Count); // a, b, c
        Assert.Equal(2, result.Edges.Count);
    }

    [Fact]
    public void Traverse_MaxDepthLimitsHops()
    {
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to"));
        _graph.AddEdge(new GraphEdge("b", "c", "similar_to"));

        var result = _graph.Traverse("a", maxDepth: 1);
        Assert.Equal(2, result.Entries.Count); // a, b only
    }

    [Fact]
    public void Traverse_MinWeightFiltersEdges()
    {
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to", weight: 0.3f));
        _graph.AddEdge(new GraphEdge("a", "c", "similar_to", weight: 0.8f));

        var result = _graph.Traverse("a", maxDepth: 1, minWeight: 0.5f);
        Assert.Equal(2, result.Entries.Count); // a, c (b filtered out)
    }

    [Fact]
    public void Traverse_NoCycles()
    {
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to"));
        _graph.AddEdge(new GraphEdge("b", "a", "similar_to"));

        var result = _graph.Traverse("a", maxDepth: 3);
        Assert.Equal(2, result.Entries.Count); // a, b only (no cycle)
    }

    [Fact]
    public void GetEdgesForEntry_ReturnsBothDirections()
    {
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to"));
        _graph.AddEdge(new GraphEdge("c", "a", "depends_on"));

        var edges = _graph.GetEdgesForEntry("a");
        Assert.Equal(2, edges.Count);
    }

    [Fact]
    public void TransferEdges_MovesOutgoingAndIncoming()
    {
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to"));
        _graph.AddEdge(new GraphEdge("a", "c", "depends_on"));
        _graph.AddEdge(new GraphEdge("c", "a", "elaborates"));

        int transferred = _graph.TransferEdges("a", "b");

        // a->c and c->a should now be b->c and c->b
        Assert.Equal(2, transferred);

        var bEdges = _graph.GetEdgesForEntry("b");
        Assert.Contains(bEdges, e => e.SourceId == "b" && e.TargetId == "c" && e.Relation == "depends_on");
        Assert.Contains(bEdges, e => e.SourceId == "c" && e.TargetId == "b" && e.Relation == "elaborates");

        // a should have no edges left
        var aEdges = _graph.GetEdgesForEntry("a");
        Assert.Empty(aEdges);
    }

    [Fact]
    public void TransferEdges_SkipsSelfReferential()
    {
        // a->b edge: transferring a to b would make b->b (self-referential), so it should be skipped
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to"));
        _graph.AddEdge(new GraphEdge("a", "c", "depends_on"));

        int transferred = _graph.TransferEdges("a", "b");

        // Only a->c should transfer (a->b skipped as it would become b->b)
        Assert.Equal(1, transferred);
    }

    [Fact]
    public void TransferEdges_NoEdges_ReturnsZero()
    {
        int transferred = _graph.TransferEdges("a", "b");
        Assert.Equal(0, transferred);
    }

    [Fact]
    public void GraphEdge_EmptySourceId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new GraphEdge("", "b", "similar_to"));
    }

    [Fact]
    public void GraphEdge_EmptyRelation_Throws()
    {
        Assert.Throws<ArgumentException>(() => new GraphEdge("a", "b", ""));
    }

    [Fact]
    public void GraphEdge_WeightClamped()
    {
        var edge = new GraphEdge("a", "b", "similar_to", weight: 2.0f);
        Assert.Equal(1.0f, edge.Weight);
    }
}
