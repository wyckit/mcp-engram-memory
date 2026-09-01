using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// Deterministic regressions for attribution crossings that land after a read sweep admits an edge
/// but before the bare far endpoint is projected. The legacy tenant is deliberate: its locator
/// returns the newest twin, making the stale projection observable rather than merely empty.
/// </summary>
public sealed class KnowledgeGraphReadFreshnessTests : IDisposable
{
    private const string Tenant = "";
    private const string AliceNs = "alice-private";
    private const string BobNs = "bob-work";

    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;

    public KnowledgeGraphReadFreshnessTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"graph_read_freshness_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 10);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
    }

    public void Dispose()
    {
        _graph.OnAttributableEdgeRead = null;
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    private void Seed(string id, string ns, string text)
        => _index.Upsert(new CognitiveEntry(id, [0.5f, 0.5f], ns, text, tenantId: Tenant));

    private void Link(string sourceId, string targetId, string relation = "elaborates")
        => Assert.True(
            _graph.TryAddEdge(new GraphEdge(sourceId, targetId, relation, tenantId: Tenant), out _),
            $"fixture edge '{sourceId}' -> '{targetId}' was refused");

    private void PlantTwinAtNextAdmittedEdge(string id, string secret, out Func<int> seamCalls)
    {
        int calls = 0;
        _graph.OnAttributableEdgeRead = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                _graph.OnAttributableEdgeRead = null;
                Seed(id, BobNs, secret);
            }
        };
        seamCalls = () => Volatile.Read(ref calls);
    }

    [Fact]
    public void GetNeighbors_DiscardsLegacyTwinResolvedAfterItsSafetyCheck()
    {
        const string seedId = "seed";
        const string farId = "shared-during-read";
        const string secret = "new twin selected by the legacy locator";

        Seed(seedId, AliceNs, "seed entry");
        Seed(farId, AliceNs, "original far entry");
        Link(seedId, farId);

        Assert.Equal("original far entry", Assert.Single(
            _graph.GetNeighbors(seedId, relation: null, direction: "outgoing", tenantId: Tenant).Neighbors)
            .Entry.Text);

        PlantTwinAtNextAdmittedEdge(farId, secret, out var seamCalls);

        var result = _graph.GetNeighbors(
            seedId, relation: null, direction: "outgoing", tenantId: Tenant);

        Assert.Empty(result.Neighbors);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result));
        Assert.Equal(secret, _index.Get(farId)!.Text);
        Assert.Equal(1, seamCalls());
        Assert.Single(_graph.GetStoredEdgesForEntry(seedId, tenantId: Tenant));
    }

    [Fact]
    public void Traverse_DiscardsAWalkThatCrossedANodeMadeAmbiguousInFlight()
    {
        const string startId = "start";
        const string crossingId = "crossing";
        const string beyondId = "behind-crossing";

        Seed(startId, AliceNs, "start entry");
        Seed(crossingId, AliceNs, "crossing entry");
        Seed(beyondId, AliceNs, "entry reachable only through crossing");
        Link(startId, crossingId);
        Link(crossingId, beyondId);

        PlantTwinAtNextAdmittedEdge(crossingId, "new crossing twin", out var seamCalls);

        var result = _graph.Traverse(startId, tenantId: Tenant, maxDepth: 3);

        Assert.Equal([startId], result.Entries.Select(entry => entry.Id));
        Assert.Empty(result.Edges);
        Assert.DoesNotContain(crossingId, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(beyondId, JsonSerializer.Serialize(result));
        Assert.Equal(1, seamCalls());
        Assert.Equal(2, _graph.GetStoredEdges(tenantId: Tenant).Count);
    }

    [Theory]
    [InlineData("incident")]
    [InlineData("tenant")]
    [InlineData("contradictions")]
    public void OtherAttributableReadProjections_DiscardAnInFlightCrossing(string projection)
    {
        const string sourceId = "claim";
        const string targetId = "counter-claim";

        Seed(sourceId, AliceNs, "source");
        Seed(targetId, AliceNs, "original target");
        Link(sourceId, targetId, relation: "contradicts");

        PlantTwinAtNextAdmittedEdge(targetId, "new target twin", out var seamCalls);

        int count = projection switch
        {
            "incident" => _graph.GetEdgesForEntry(sourceId, tenantId: Tenant).Count,
            "tenant" => _graph.GetAllEdges(tenantId: Tenant).Count,
            "contradictions" => _graph.GetContradictions(AliceNs, tenantId: Tenant).Count,
            _ => throw new ArgumentOutOfRangeException(nameof(projection)),
        };

        Assert.Equal(0, count);
        Assert.Equal(1, seamCalls());
        Assert.Single(_graph.GetStoredEdges(tenantId: Tenant));
    }
}
