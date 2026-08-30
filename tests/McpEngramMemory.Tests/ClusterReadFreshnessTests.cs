using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// Optimistic attribution validation for cluster reads. The crossing is injected after a real
/// guard admits the member and before the member is resolved or counted, so the first projection is
/// a mixed view that must be discarded rather than returned.
/// </summary>
public sealed class ClusterReadFreshnessTests : IDisposable
{
    private const string MainNs = "main";
    private const string ShadowNs = "shadow";

    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly ClusterManager _clusters;

    public ClusterReadFreshnessTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"cluster_read_freshness_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 600_000);
        _index = new CognitiveIndex(_persistence);
        _clusters = new ClusterManager(_index, _persistence);
    }

    public void Dispose()
    {
        _clusters.OnTopologyReadAdmitted = null;
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("acme")]
    public void GetCluster_DiscardsAProjectionWhoseMemberBecameAmbiguousBeforeResolution(string tenant)
    {
        Seed("member", MainNs, "original", tenant);
        Assert.Contains("Created",
            _clusters.CreateCluster("c1", MainNs, new[] { "member" }, "one", tenant));

        _clusters.OnTopologyReadAdmitted = () =>
        {
            _clusters.OnTopologyReadAdmitted = null;
            Seed("member", ShadowNs, "shadow twin", tenant);
        };

        var result = _clusters.GetCluster("c1", tenant);

        Assert.NotNull(result);
        Assert.Equal(0, result!.MemberCount);
        Assert.Empty(result.Members);
    }

    [Fact]
    public void ListClusters_DiscardsCountsComputedAcrossAnAttributionCrossing()
    {
        const string tenant = "acme";
        Seed("member", MainNs, "original", tenant);
        Assert.Contains("Created",
            _clusters.CreateCluster("c1", MainNs, new[] { "member" }, "one", tenant));

        _clusters.OnTopologyReadAdmitted = () =>
        {
            _clusters.OnTopologyReadAdmitted = null;
            Seed("member", ShadowNs, "shadow twin", tenant);
        };

        var result = Assert.Single(_clusters.ListClusters(MainNs, tenant));

        Assert.Equal(0, result.MemberCount);
    }

    private void Seed(string id, string ns, string text, string tenant)
        => _index.Upsert(new CognitiveEntry(id, [0.5f, 0.5f], ns, text, tenantId: tenant));
}
