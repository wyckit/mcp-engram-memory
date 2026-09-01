using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

/// <summary>Deterministic controls for the final coherent merge transaction.</summary>
public sealed class FinalizationMergeTests : IDisposable
{
    private const string Ns = "merge-final";
    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly IntelligenceTools _tools;

    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 3;
        public float[] Embed(string text) => [1f, 0f, 0f];
    }

    public FinalizationMergeTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"final_merge_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 60_000);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
        var embedding = new StubEmbedding();
        var scanner = new AccretionScanner(_index, _persistence);
        var lifecycle = new LifecycleEngine(_index);
        var access = new NamespaceAccess(
            new NamespaceRegistry(_index, embedding), AgentIdentity.Default);
        _tools = new IntelligenceTools(
            _index, _graph, embedding, scanner, _clusters, lifecycle, access);
    }

    private void SeedMergeShape()
    {
        _index.Upsert(new CognitiveEntry(
            "keep", [1f, 0f, 0f], Ns, "keep", metadata: new() { ["keep"] = "yes" }));
        _index.Upsert(new CognitiveEntry(
            "archive", [0.99f, 0.01f, 0f], Ns, "archive",
            metadata: new() { ["archive"] = "yes" }));
        _index.Upsert(new CognitiveEntry("far", [0f, 1f, 0f], Ns, "far"));
        Assert.DoesNotContain("Error", _graph.AddEdge(
            new GraphEdge("archive", "far", "depends_on", tenantId: "")));
        Assert.DoesNotContain("Error", _clusters.CreateCluster(
            "cluster", Ns, ["archive", "far"], label: null, tenantId: ""));
    }

    [Fact]
    public void InPlaceLifecycleAndAccessChanges_RefuseBeforeAnyMergeMutation()
    {
        SeedMergeShape();
        _index.OnBeforeMergeCommit = () =>
        {
            _index.OnBeforeMergeCommit = null;
            _index.RecordAccess("keep", Ns, tenantId: "");
            Assert.True(_index.SetActivationEnergyAndState(
                "archive", 17f, "ltm", Ns, tenantId: ""));
        };

        string reply = _tools.MergeMemories("keep", "archive", Ns);

        Assert.StartsWith("Error:", reply);
        var keep = _index.Get("keep", Ns, tenantId: "")!;
        var archive = _index.Get("archive", Ns, tenantId: "")!;
        Assert.Equal(2, keep.AccessCount); // the concurrent access survives
        Assert.False(keep.Metadata.ContainsKey("archive"));
        Assert.Equal("ltm", archive.LifecycleState); // the concurrent lifecycle change survives
        Assert.Equal(17f, archive.ActivationEnergy);
        Assert.Contains(_graph.GetStoredEdges(""), e =>
            e.SourceId == "archive" && e.TargetId == "far" && e.Relation == "depends_on");
        Assert.DoesNotContain(_graph.GetStoredEdges(""), e =>
            e.SourceId == "keep" && e.TargetId == "archive" && e.Relation == "similar_to");
        Assert.Contains("cluster", _clusters.GetClustersForEntry("archive", tenantId: ""));
        Assert.DoesNotContain("cluster", _clusters.GetClustersForEntry("keep", tenantId: ""));
    }

    [Fact]
    public void ArchiveReplacementBeforeCommit_LeavesKeepAndAllTopologyUntouched()
    {
        SeedMergeShape();
        _index.OnBeforeMergeCommit = () =>
        {
            _index.OnBeforeMergeCommit = null;
            _index.Upsert(new CognitiveEntry(
                "archive", [0f, 0f, 1f], Ns, "replacement", lifecycleState: "stm"));
        };

        string reply = _tools.MergeMemories("keep", "archive", Ns);

        Assert.StartsWith("Error:", reply);
        var keep = _index.Get("keep", Ns, tenantId: "")!;
        var replacement = _index.Get("archive", Ns, tenantId: "")!;
        Assert.Equal("keep", keep.Text);
        Assert.False(keep.Metadata.ContainsKey("archive"));
        Assert.Equal("replacement", replacement.Text);
        Assert.Equal("stm", replacement.LifecycleState);
        Assert.Contains(_graph.GetStoredEdges(""), e =>
            e.SourceId == "archive" && e.TargetId == "far" && e.Relation == "depends_on");
        Assert.DoesNotContain(_graph.GetStoredEdges(""), e => e.SourceId == "keep");
        Assert.Contains("cluster", _clusters.GetClustersForEntry("archive", tenantId: ""));
        Assert.DoesNotContain("cluster", _clusters.GetClustersForEntry("keep", tenantId: ""));
    }

    [Fact]
    public async Task ArchiveReplacement_CannotEnterBetweenEntryCommitAndTopologyPublication()
    {
        SeedMergeShape();
        Task? replacement = null;
        _graph.OnMergeEntriesCommitted = () =>
        {
            _graph.OnMergeEntriesCommitted = null;
            replacement = Task.Run(() => _index.Upsert(new CognitiveEntry(
                "archive", [0f, 0f, 1f], Ns, "replacement", lifecycleState: "stm")));

            Assert.True(SpinWait.SpinUntil(
                () => _index.PartitionWaitingWriters(Ns, "") > 0,
                TimeSpan.FromSeconds(5)));
            Assert.False(replacement.IsCompleted);
        };

        string reply = _tools.MergeMemories("keep", "archive", Ns);
        Assert.NotNull(replacement);
        await replacement!;

        Assert.StartsWith("Merged", reply);
        Assert.Equal("replacement", _index.Get("archive", Ns, tenantId: "")!.Text);
        Assert.Contains(_graph.GetStoredEdges(""), e =>
            e.SourceId == "keep" && e.TargetId == "far" && e.Relation == "depends_on");
        Assert.DoesNotContain(_graph.GetStoredEdges(""), e =>
            e.SourceId == "archive" && e.TargetId == "far");
        Assert.DoesNotContain(_graph.GetStoredEdges(""), e =>
            e.SourceId == "keep" && e.TargetId == "archive" && e.Relation == "similar_to");
        Assert.Contains("cluster", _clusters.GetClustersForEntry("keep", tenantId: ""));
        Assert.DoesNotContain("cluster", _clusters.GetClustersForEntry("archive", tenantId: ""));
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path))
            Directory.Delete(_path, recursive: true);
    }
}
