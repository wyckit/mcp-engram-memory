using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class VisualizationToolsTests : IDisposable
{
    private readonly string _dataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly VisualizationTools _tools;

    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    public VisualizationToolsTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"viz_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_dataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
        _tools = new VisualizationTools(_index, _graph, _clusters);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, true);
    }

    [Fact]
    public void GetGraphSnapshot_EmptyIndex_ReturnsEmptySnapshot()
    {
        var snap = _tools.GetGraphSnapshot();

        Assert.Equal("*", snap.Namespace);
        Assert.Empty(snap.Nodes);
        Assert.Empty(snap.Edges);
        Assert.Empty(snap.Clusters);
        Assert.Equal(0, snap.Stats.NodeCount);
    }

    [Fact]
    public void GetGraphSnapshot_ReturnsAllNodes()
    {
        _index.Upsert(new CognitiveEntry("a", [0.5f, 0.5f], "ns1", "alpha", lifecycleState: "stm"));
        _index.Upsert(new CognitiveEntry("b", [0.5f, 0.5f], "ns1", "beta",  lifecycleState: "ltm"));

        var snap = _tools.GetGraphSnapshot("ns1");

        Assert.Equal(2, snap.Stats.NodeCount);
        Assert.Contains(snap.Nodes, n => n.Id == "a" && n.LifecycleState == "stm");
        Assert.Contains(snap.Nodes, n => n.Id == "b" && n.LifecycleState == "ltm");
    }

    [Fact]
    public void GetGraphSnapshot_ExcludesArchivedByDefault()
    {
        _index.Upsert(new CognitiveEntry("a", [0.5f, 0.5f], "ns1", "active",   lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", [0.5f, 0.5f], "ns1", "archived", lifecycleState: "archived"));

        var snap = _tools.GetGraphSnapshot("ns1");

        Assert.Single(snap.Nodes);
        Assert.Equal("a", snap.Nodes[0].Id);
    }

    [Fact]
    public void GetGraphSnapshot_IncludesArchivedWhenRequested()
    {
        _index.Upsert(new CognitiveEntry("a", [0.5f, 0.5f], "ns1", "active",   lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", [0.5f, 0.5f], "ns1", "archived", lifecycleState: "archived"));

        var snap = _tools.GetGraphSnapshot("ns1", includeArchived: true);

        Assert.Equal(2, snap.Nodes.Count);
    }

    [Fact]
    public void GetGraphSnapshot_ReturnsTypedEdges()
    {
        _index.Upsert(new CognitiveEntry("a", [0.5f, 0.5f], "ns1", "alpha", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", [0.5f, 0.5f], "ns1", "beta",  lifecycleState: "ltm"));
        _graph.AddEdge(new GraphEdge("a", "b", "elaborates"));

        var snap = _tools.GetGraphSnapshot("ns1");

        Assert.Single(snap.Edges);
        Assert.Equal("a",          snap.Edges[0].Source);
        Assert.Equal("b",          snap.Edges[0].Target);
        Assert.Equal("elaborates", snap.Edges[0].Relation);
    }

    [Fact]
    public void GetGraphSnapshot_EdgesBetweenFilteredNodesExcluded()
    {
        _index.Upsert(new CognitiveEntry("a", [0.5f, 0.5f], "ns1", "visible",  lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", [0.5f, 0.5f], "ns1", "archived", lifecycleState: "archived"));
        _graph.AddEdge(new GraphEdge("a", "b", "depends_on"));

        // Default excludes archived — edge a→b should not appear
        var snap = _tools.GetGraphSnapshot("ns1");

        Assert.Empty(snap.Edges);
    }

    [Fact]
    public void GetGraphSnapshot_ReturnsClustersWithMemberIds()
    {
        _index.Upsert(new CognitiveEntry("a", [0.5f, 0.5f], "ns1", "alpha", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", [0.5f, 0.5f], "ns1", "beta",  lifecycleState: "ltm"));
        _clusters.CreateCluster("c1", "ns1", ["a", "b"], "Test Cluster");

        var snap = _tools.GetGraphSnapshot("ns1");

        Assert.Single(snap.Clusters);
        var cluster = snap.Clusters[0];
        Assert.Equal("c1",           cluster.ClusterId);
        Assert.Equal("Test Cluster", cluster.Label);
        Assert.Contains("a",         cluster.MemberIds);
        Assert.Contains("b",         cluster.MemberIds);
    }

    [Fact]
    public void GetGraphSnapshot_StatsAreAccurate()
    {
        _index.Upsert(new CognitiveEntry("a", [0.5f, 0.5f], "ns1", "stm entry",  lifecycleState: "stm"));
        _index.Upsert(new CognitiveEntry("b", [0.5f, 0.5f], "ns1", "ltm entry",  lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", [0.5f, 0.5f], "ns1", "arch entry", lifecycleState: "archived"));
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to"));

        var snap = _tools.GetGraphSnapshot("ns1", includeArchived: true);

        Assert.Equal(3, snap.Stats.NodeCount);
        Assert.Equal(1, snap.Stats.EdgeCount);
        Assert.Equal(1, snap.Stats.Stm);
        Assert.Equal(1, snap.Stats.Ltm);
        Assert.Equal(1, snap.Stats.Archived);
    }

    [Fact]
    public void GetGraphSnapshot_WildcardNs_SpansAllNamespaces()
    {
        _index.Upsert(new CognitiveEntry("a", [0.5f, 0.5f], "ns1", "in ns1", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", [0.5f, 0.5f], "ns2", "in ns2", lifecycleState: "ltm"));

        var snap = _tools.GetGraphSnapshot("*");

        Assert.Equal(2, snap.Nodes.Count);
        Assert.Contains(snap.Stats.Namespaces, ns => ns == "ns1");
        Assert.Contains(snap.Stats.Namespaces, ns => ns == "ns2");
    }

    [Fact]
    public void GetGraphSnapshot_NodeFieldsPopulated()
    {
        _index.Upsert(new CognitiveEntry("a", [0.5f, 0.5f], "ns1",
            text: "important memory",
            category: "pattern",
            lifecycleState: "ltm",
            keywords: "kw1 kw2"));

        var snap = _tools.GetGraphSnapshot("ns1");
        var node = snap.Nodes[0];

        Assert.Equal("a",                node.Id);
        Assert.Equal("important memory", node.Text);
        Assert.Equal("ns1",              node.Ns);
        Assert.Equal("ltm",              node.LifecycleState);
        Assert.Equal("pattern",          node.Category);
        Assert.Equal("kw1 kw2",          node.Keywords);
    }
}
