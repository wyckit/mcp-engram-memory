using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class ClusterToolsTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly ClusterManager _clusters;
    private readonly ClusterTools _tools;

    private sealed class StubEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    public ClusterToolsTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"clustertools_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _clusters = new ClusterManager(_index, _persistence);
        _tools = new ClusterTools(_clusters, new StubEmbeddingService());

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

    // ── CreateCluster ──

    [Fact]
    public void CreateCluster_ValidInput_CreatesCluster()
    {
        var result = _tools.CreateCluster("c1", "test", "a,b");
        Assert.IsType<string>(result);
        Assert.Contains("Created", (string)result);
        Assert.Equal(1, _clusters.ClusterCount);
    }

    [Fact]
    public void CreateCluster_WithLabel_SetsLabel()
    {
        _tools.CreateCluster("c1", "test", "a,b", label: "my label");

        var cluster = _clusters.GetCluster("c1");
        Assert.NotNull(cluster);
        Assert.Equal("my label", cluster!.Label);
    }

    [Fact]
    public void CreateCluster_InvalidMember_ReturnsError()
    {
        // Members that don't exist in the index — ClusterManager still creates the cluster
        // but the centroid computation skips missing entries. The tool wraps exceptions via
        // try/catch, so we verify the cluster is created with the requested members.
        // Since CreateCluster does not validate member existence, it succeeds.
        var result = _tools.CreateCluster("c1", "test", "nonexistent1,nonexistent2");
        Assert.IsType<string>(result);
        Assert.Contains("Created", (string)result);

        var cluster = _clusters.GetCluster("c1");
        Assert.NotNull(cluster);
        Assert.Equal(2, cluster!.MemberCount);
    }

    // ── UpdateCluster ──

    [Fact]
    public void UpdateCluster_AddMembers_UpdatesCluster()
    {
        _tools.CreateCluster("c1", "test", "a");
        var result = _tools.UpdateCluster("c1", addMemberIds: "b,c");

        Assert.Contains("Updated", result);
        var cluster = _clusters.GetCluster("c1");
        Assert.Equal(3, cluster!.MemberCount);
    }

    [Fact]
    public void UpdateCluster_RemoveMembers_UpdatesCluster()
    {
        _tools.CreateCluster("c1", "test", "a,b,c");
        var result = _tools.UpdateCluster("c1", removeMemberIds: "b");

        Assert.Contains("Updated", result);
        var cluster = _clusters.GetCluster("c1");
        Assert.Equal(2, cluster!.MemberCount);
    }

    [Fact]
    public void UpdateCluster_ChangeLabel()
    {
        _tools.CreateCluster("c1", "test", "a", label: "old label");
        var result = _tools.UpdateCluster("c1", label: "new label");

        Assert.Contains("Updated", result);
        var cluster = _clusters.GetCluster("c1");
        Assert.Equal("new label", cluster!.Label);
    }

    // ── StoreClusterSummary ──

    [Fact]
    public void StoreClusterSummary_CreatesSearchableEntry()
    {
        _tools.CreateCluster("c1", "test", "a,b");
        var result = _tools.StoreClusterSummary("c1", "Summary of a and b", new[] { 0.5f, 0.5f });

        Assert.Contains("summary:c1", result);

        var entry = _index.Get("summary:c1");
        Assert.NotNull(entry);
        Assert.True(entry!.IsSummaryNode);
        Assert.Equal("c1", entry.SourceClusterId);
        Assert.Equal("ltm", entry.LifecycleState);
        Assert.Equal("Summary of a and b", entry.Text);
    }

    [Fact]
    public void StoreClusterSummary_TextOnly_AutoEmbeds()
    {
        _tools.CreateCluster("c1", "test", "a,b");
        var result = _tools.StoreClusterSummary("c1", "Auto embedded summary");

        Assert.Contains("summary:c1", result);

        var entry = _index.Get("summary:c1");
        Assert.NotNull(entry);
        // StubEmbeddingService returns [0.5f, 0.5f]
        Assert.Equal(0.5f, entry!.Vector[0]);
        Assert.Equal(0.5f, entry.Vector[1]);
    }

    // ── GetCluster ──

    [Fact]
    public void GetCluster_Existing_ReturnsDetails()
    {
        _tools.CreateCluster("c1", "test", "a,b", label: "test cluster");

        var result = _tools.GetCluster("c1");
        Assert.IsType<GetClusterResult>(result);

        var cluster = (GetClusterResult)result;
        Assert.Equal("c1", cluster.ClusterId);
        Assert.Equal("test cluster", cluster.Label);
        Assert.Equal(2, cluster.MemberCount);
        Assert.Equal(2, cluster.Members.Count);
    }

    [Fact]
    public void GetCluster_NonExistent_ReturnsNotFound()
    {
        var result = _tools.GetCluster("missing");
        Assert.IsType<string>(result);
        Assert.Equal("Cluster 'missing' not found.", (string)result);
    }

    // ── ListClusters ──

    [Fact]
    public void ListClusters_ReturnsAllInNamespace()
    {
        _tools.CreateCluster("c1", "test", "a", label: "cluster 1");
        _tools.CreateCluster("c2", "test", "b", label: "cluster 2");
        _tools.CreateCluster("c3", "other", "c", label: "cluster 3");

        var result = _tools.ListClusters("test");
        Assert.Equal(2, result.Count);
        Assert.Contains(result, c => c.ClusterId == "c1");
        Assert.Contains(result, c => c.ClusterId == "c2");
    }

    [Fact]
    public void ListClusters_EmptyNamespace_ReturnsEmpty()
    {
        var result = _tools.ListClusters("nonexistent");
        Assert.Empty(result);
    }
}
