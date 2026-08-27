using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class ClusterManagerTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly ClusterManager _clusters;

    public ClusterManagerTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"cluster_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _clusters = new ClusterManager(_index, _persistence);

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
    public void CreateCluster_Success()
    {
        var result = _clusters.CreateCluster("c1", "test", new[] { "a", "b" }, "my cluster");
        Assert.Contains("Created", result);
        Assert.Equal(1, _clusters.ClusterCount);
    }

    [Fact]
    public void CreateCluster_Duplicate_ReturnsError()
    {
        _clusters.CreateCluster("c1", "test", new[] { "a" });
        var result = _clusters.CreateCluster("c1", "test", new[] { "b" });
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void UpdateCluster_AddMembers()
    {
        _clusters.CreateCluster("c1", "test", new[] { "a" });
        _clusters.UpdateCluster("c1", addIds: new[] { "b", "c" });
        var cluster = _clusters.GetCluster("c1");
        Assert.Equal(3, cluster!.MemberCount);
    }

    [Fact]
    public void UpdateCluster_RemoveMembers()
    {
        _clusters.CreateCluster("c1", "test", new[] { "a", "b", "c" });
        _clusters.UpdateCluster("c1", removeIds: new[] { "b" });
        var cluster = _clusters.GetCluster("c1");
        Assert.Equal(2, cluster!.MemberCount);
    }

    [Fact]
    public void UpdateCluster_ChangeLabel()
    {
        _clusters.CreateCluster("c1", "test", new[] { "a" }, "old label");
        _clusters.UpdateCluster("c1", label: "new label");
        var cluster = _clusters.GetCluster("c1");
        Assert.Equal("new label", cluster!.Label);
    }

    [Fact]
    public void UpdateCluster_NotFound_ReturnsError()
    {
        var result = _clusters.UpdateCluster("missing", addIds: new[] { "a" });
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void StoreSummary_CreatesSearchableEntry()
    {
        _clusters.CreateCluster("c1", "test", new[] { "a", "b" });
        var summaryId = _clusters.StoreSummary("c1", "Summary of a and b", new[] { 0.5f, 0.5f });
        Assert.Equal("summary:c1", summaryId);

        var entry = _index.Get("summary:c1");
        Assert.NotNull(entry);
        Assert.True(entry.IsSummaryNode);
        Assert.Equal("c1", entry.SourceClusterId);
        Assert.Equal("ltm", entry.LifecycleState);
    }

    [Fact]
    public void StoreSummary_ClusterNotFound_ReturnsError()
    {
        var result = _clusters.StoreSummary("missing", "summary", new[] { 1f });
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void GetCluster_ReturnsFullDetails()
    {
        _clusters.CreateCluster("c1", "test", new[] { "a", "b" }, "test cluster");
        var result = _clusters.GetCluster("c1");
        Assert.NotNull(result);
        Assert.Equal("c1", result.ClusterId);
        Assert.Equal("test cluster", result.Label);
        Assert.Equal(2, result.MemberCount);
        Assert.Equal(2, result.Members.Count);
    }

    [Fact]
    public void GetCluster_NotFound_ReturnsNull()
    {
        Assert.Null(_clusters.GetCluster("missing"));
    }

    [Fact]
    public void ListClusters_FiltersByNamespace()
    {
        _clusters.CreateCluster("c1", "test", new[] { "a" }, "cluster 1");
        _clusters.CreateCluster("c2", "other", new string[] { }, "cluster 2");

        var result = _clusters.ListClusters("test");
        Assert.Single(result);
        Assert.Equal("c1", result[0].ClusterId);
    }

    [Fact]
    public void ListClusters_IncludesSummaryStatus()
    {
        _clusters.CreateCluster("c1", "test", new[] { "a" });
        var list = _clusters.ListClusters("test");
        Assert.False(list[0].HasSummary);

        _clusters.StoreSummary("c1", "summary", new[] { 1f, 0f });
        list = _clusters.ListClusters("test");
        Assert.True(list[0].HasSummary);
    }

    [Fact]
    public void GetClustersForEntry_ReturnsMatchingClusters()
    {
        _clusters.CreateCluster("c1", "test", new[] { "a", "b" });
        _clusters.CreateCluster("c2", "test", new[] { "b", "c" });

        var clusters = _clusters.GetClustersForEntry("b");
        Assert.Equal(2, clusters.Count);
    }

    [Fact]
    public void TransferMembership_MovesMemberAcrossClusters()
    {
        _clusters.CreateCluster("c1", "test", new[] { "a", "b" });
        _clusters.CreateCluster("c2", "test", new[] { "a", "c" });

        int transferred = _clusters.TransferMembership("a", "b");

        Assert.Equal(2, transferred);

        // c1: had a,b → now just b (a removed, b already present)
        var c1 = _clusters.GetCluster("c1");
        Assert.Equal(1, c1!.MemberCount);
        Assert.Contains(c1.Members, m => m.Id == "b");

        // c2: had a,c → now b,c (a replaced by b)
        var c2 = _clusters.GetCluster("c2");
        Assert.Equal(2, c2!.MemberCount);
        Assert.Contains(c2.Members, m => m.Id == "b");
        Assert.Contains(c2.Members, m => m.Id == "c");
    }

    [Fact]
    public void TransferMembership_NoMembership_ReturnsZero()
    {
        _clusters.CreateCluster("c1", "test", new[] { "a", "b" });

        int transferred = _clusters.TransferMembership("c", "a");
        Assert.Equal(0, transferred);
    }

    [Fact]
    public void RemoveEntryFromAllClusters_CascadeDelete()
    {
        _clusters.CreateCluster("c1", "test", new[] { "a", "b" });
        _clusters.CreateCluster("c2", "test", new[] { "b", "c" });

        _clusters.RemoveEntryFromAllClusters("b");

        var c1 = _clusters.GetCluster("c1");
        var c2 = _clusters.GetCluster("c2");
        Assert.Equal(1, c1!.MemberCount);
        Assert.Equal(1, c2!.MemberCount);
    }

    /// <summary>
    /// A single entry can belong to clusters that live in different namespaces — cross-namespace
    /// membership is intentional, not a corruption to be normalised away. That is exactly why a
    /// caller which has to authorize what it returns cannot derive the namespace once for the whole
    /// result set: the ACL check has to be made against the namespace of the cluster it is actually
    /// about to reveal. GetClusterMembershipsForEntry exists to hand back that pairing, so the
    /// pairing itself is the contract downstream filtering depends on.
    ///
    /// Asserting on the pair rather than on the two projections separately is deliberate: a version
    /// that returned the right cluster ids and the right set of namespaces, but mismatched between
    /// them, would satisfy any per-column assertion and still authorize the wrong object.
    /// </summary>
    [Fact]
    public void GetClusterMembershipsForEntry_PairsEachClusterWithItsOwnNamespace()
    {
        // "b" is a member of both, but the clusters sit in different namespaces.
        _clusters.CreateCluster("cm1", "test", new[] { "a", "b" }, "in test");
        _clusters.CreateCluster("cm2", "other", new[] { "b", "c" }, "in other");
        // A cluster "b" is NOT in, to prove the membership predicate still filters.
        _clusters.CreateCluster("cm3", "other", new[] { "a" }, "without b");

        var memberships = _clusters.GetClusterMembershipsForEntry("b", "");

        Assert.Equal(2, memberships.Count);
        // ClusterMembershipInfo is a record struct, so this compares the (ClusterId, Ns) pair as a
        // unit — the namespace has to travel attached to the cluster it belongs to.
        Assert.Contains(new ClusterMembershipInfo("cm1", "test"), memberships);
        Assert.Contains(new ClusterMembershipInfo("cm2", "other"), memberships);
        Assert.DoesNotContain(memberships, m => m.ClusterId == "cm3");

        // GetClustersForEntry is now a projection over the method above rather than a second copy
        // of the membership predicate, so the two views can never disagree about which clusters
        // contain the entry. Pin that equivalence: if they drift, an ACL filter reading one and a
        // cascade reading the other would act on different sets.
        var ids = _clusters.GetClustersForEntry("b");
        Assert.Equal(
            memberships.Select(m => m.ClusterId).OrderBy(id => id, StringComparer.Ordinal).ToList(),
            ids.OrderBy(id => id, StringComparer.Ordinal).ToList());
    }
}
