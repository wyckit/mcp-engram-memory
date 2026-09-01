using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// Deterministic regressions for the spreading-activation attribution bracket. The walk WRITES —
/// activation boosts land on neighbor entries and alter their retrieval ordering — so an ambiguity
/// crossing anywhere in the tenant during the walk must discard the whole accumulation: a stale
/// walk can push energy into a twin's neighborhood that nobody showed this caller. The bracket's
/// baseline is captured before the root admission check, so a crossing in any part of the window
/// lands inside it.
/// </summary>
public sealed class SpreadingActivationDiscardTests : IDisposable
{
    private const string Tenant = "spread-tenant";
    private const string Ns = "primary";
    private const string OtherNs = "secondary";

    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly SpreadingActivationService _spreading;

    public SpreadingActivationDiscardTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"spreading_discard_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 10);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
        _spreading = new SpreadingActivationService(_index, _graph, _clusters);
    }

    public void Dispose()
    {
        _graph.OnAttributableEdgeRead = null;
        _spreading.OnBeforeRootGate = null;
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    private void Seed(string id, string ns)
        => _index.Upsert(new CognitiveEntry(id, [0.6f, 0.8f], ns, $"entry {id}", tenantId: Tenant));

    [Fact]
    public void PropagateAccess_DiscardsBoostsWhenAttributionMovesDuringTheWalk()
    {
        Seed("root", Ns);
        Seed("peer", Ns);
        Seed("bystander", Ns);
        Assert.True(_graph.TryAddEdge(new GraphEdge("root", "peer", "similar_to", tenantId: Tenant), out _),
            "fixture edge was refused");

        float peerEnergyBefore = _index.Get("peer", Ns, tenantId: Tenant)!.ActivationEnergy;

        // One-shot seam: the first admitted edge read of the walk plants an UNRELATED crossing —
        // "bystander" gains a twin, going from one namespace to two. The graph read itself
        // retries against a fresh sweep and still returns the neighbor, so only the walk's own
        // revision bracket can notice that the tenant's attribution moved mid-walk.
        int calls = 0;
        _graph.OnAttributableEdgeRead = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                _graph.OnAttributableEdgeRead = null;
                Seed("bystander", OtherNs);
            }
        };

        var result = _spreading.PropagateAccess("root", Ns, tenantId: Tenant);

        Assert.True(Volatile.Read(ref calls) >= 1, "the seam never fired; the walk read no edges");
        Assert.True(result.NodesReached > 0, "the walk accumulated nothing; the discard was not exercised");
        Assert.Equal(0, result.NodesUpdated);
        Assert.Equal(0f, result.TotalEnergySpread);
        Assert.Equal(peerEnergyBefore, _index.Get("peer", Ns, tenantId: Tenant)!.ActivationEnergy);
    }

    [Fact]
    public void PropagateAccess_BaselineIsCapturedBeforeTheRootAdmissionCheck()
    {
        Seed("root", Ns);
        Seed("peer", Ns);
        Seed("bystander", Ns);
        Assert.True(_graph.TryAddEdge(new GraphEdge("root", "peer", "similar_to", tenantId: Tenant), out _),
            "fixture edge was refused");

        float peerEnergyBefore = _index.Get("peer", Ns, tenantId: Tenant)!.ActivationEnergy;

        // The seam fires between the baseline capture and the IsSafe gate — the exact interval
        // the ordering fix closed. With the baseline captured FIRST, this crossing lands inside
        // the bracket and the walk is discarded. Under the buggy ordering (capture after the
        // gate) the crossing would already be part of the baseline, the discard would compare
        // equal, and the boosts would land — failing this test.
        bool planted = false;
        _spreading.OnBeforeRootGate = () =>
        {
            if (planted) return;
            planted = true;
            Seed("bystander", OtherNs);
        };

        var result = _spreading.PropagateAccess("root", Ns, tenantId: Tenant);

        Assert.True(planted, "the seam never fired");
        Assert.True(result.NodesReached > 0, "the walk accumulated nothing; the discard was not exercised");
        Assert.Equal(0, result.NodesUpdated);
        Assert.Equal(peerEnergyBefore, _index.Get("peer", Ns, tenantId: Tenant)!.ActivationEnergy);
    }

    [Fact]
    public void PropagateAccess_ColdStoreLoad_DoesNotFalselyDiscard()
    {
        // A store holding a same-id twin pair: loading it tracks the crossing and bumps the
        // attribution revision. On a COLD process, the walk's own admission probe is what
        // triggers that load — so a baseline captured before any probe would see the load-time
        // bump as churn and discard every first walk. The cold-load barrier probes once before
        // the baseline precisely so those bumps land outside the bracket.
        Seed("root", Ns);
        Seed("peer", Ns);
        Seed("dup", Ns);
        Seed("dup", OtherNs);
        Assert.True(_graph.TryAddEdge(new GraphEdge("root", "peer", "similar_to", tenantId: Tenant), out _),
            "fixture edge was refused");
        _persistence.Flush();

        _index.Dispose();
        _persistence.Dispose();

        using var coldPersistence = new PersistenceManager(_path, debounceMs: 10);
        using var coldIndex = new CognitiveIndex(coldPersistence);
        var coldGraph = new KnowledgeGraph(coldPersistence, coldIndex);
        var coldClusters = new ClusterManager(coldIndex, coldPersistence);
        var coldSpreading = new SpreadingActivationService(coldIndex, coldGraph, coldClusters);

        var result = coldSpreading.PropagateAccess("root", Ns, tenantId: Tenant);

        Assert.True(result.NodesReached > 0, "the cold walk reached nothing; the fixture edge did not persist");
        Assert.True(result.NodesUpdated > 0,
            "a quiet tenant's first walk after startup was discarded — load-time attribution bumps leaked into the bracket");
    }

    [Fact]
    public void PropagateAccess_AppliesBoostsWhenAttributionIsQuiet()
    {
        Seed("root", Ns);
        Seed("peer", Ns);
        Assert.True(_graph.TryAddEdge(new GraphEdge("root", "peer", "similar_to", tenantId: Tenant), out _),
            "fixture edge was refused");

        float peerEnergyBefore = _index.Get("peer", Ns, tenantId: Tenant)!.ActivationEnergy;
        var result = _spreading.PropagateAccess("root", Ns, tenantId: Tenant);

        Assert.True(result.NodesUpdated > 0, "a quiet tenant's walk must still apply its boosts");
        Assert.True(_index.Get("peer", Ns, tenantId: Tenant)!.ActivationEnergy > peerEnergyBefore);
    }
}
