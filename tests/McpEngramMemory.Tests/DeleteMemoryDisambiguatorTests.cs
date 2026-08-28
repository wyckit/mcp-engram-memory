using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

/// <summary>
/// delete_memory against same-id twins (issue #21).
///
/// An entry's identity is (tenant, namespace, id) — ids are unique only per
/// (tenant, namespace) — and delete_memory used to resolve bare ids through the ACL-blind
/// tenant-wide unique-match. That guard made the topology cascade safe, but it also made two
/// same-id entries in one tenant mutually undeletable, and let a twin in a namespace the caller
/// cannot even see blank a legitimate delete into "not found". The fix is two-fold: an optional
/// `ns` disambiguator for an exact (tenant, ns, id) delete, and bare-id resolution converged
/// onto <see cref="EntryAccessResolver"/> with the WRITE predicate — unique among namespaces the
/// caller may write.
///
/// Every collision test seeds a NON-legacy tenant: in the legacy "" partition the global
/// _idToNamespace alias makes two same-id entries alias one another, so a legacy-seeded version
/// would pass or fail for reasons unrelated to the resolution under test. The one legacy test
/// here pins that the default single-identity deployment is behaviourally unchanged.
/// </summary>
public class DeleteMemoryDisambiguatorTests : IDisposable
{
    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    private const string Tenant = "t1";

    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly NamespaceRegistry _registry;
    private readonly StubEmbedding _embedding = new();

    public DeleteMemoryDisambiguatorTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"delete_disambig_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 10);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
        _registry = new NamespaceRegistry(_index, _embedding);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    /// <summary>
    /// Tools as an identified principal in tenant <see cref="Tenant"/> (unless overridden). Under
    /// a non-legacy tenant even the default agent gets no HasAccess short-circuit, so every
    /// delete below really passes the write gate; the first store to a fresh namespace claims it.
    /// </summary>
    private CoreMemoryTools Core(string agentId, string tenantId = Tenant) => new(
        _index, new PhysicsEngine(), _embedding, new MetricsCollector(), _graph,
        new QueryExpander(), new SpreadingActivationService(_index, _graph, _clusters),
        _clusters, _registry, new PrincipalContext(tenantId, agentId));

    [Fact]
    public void ExplicitNs_DeletesOneTwin_SurvivorKeepsSharedTopology_ThenCascadesWhenUnambiguous()
    {
        var alice = Core("alice");
        Assert.Contains("Stored entry", alice.StoreMemory("twin", "ns-b", "second twin"));
        Assert.Contains("Stored entry", alice.StoreMemory("anchor", "ns-b", "anchor entry"));

        // Graph adjacency and cluster membership are keyed (tenant, bare id), so this topology
        // is physically SHARED between both twins — nothing at the cascade level can attribute
        // it to one of them.
        //
        // Seed order mirrors how the state actually arises: the topology is written while "twin"
        // is still UNIQUE, and the second twin appears afterwards. Written the other way round it
        // would be refused outright — topology writes fail closed on a tenant-wide duplicate — so
        // seeding both twins first would be testing a state that cannot occur.
        _graph.AddEdge(new GraphEdge("twin", "anchor", "similar_to", tenantId: Tenant));
        _clusters.CreateCluster("twin-cluster", "ns-b", new[] { "twin", "anchor" },
            "twin cluster", tenantId: Tenant);
        Assert.Single(_graph.GetEdgesForEntry("twin", tenantId: Tenant));

        Assert.Contains("Stored entry", alice.StoreMemory("twin", "ns-a", "first twin"));

        var first = alice.DeleteMemory("twin", ns: "ns-a");

        // Exact reply shape: "Removed 0 edge(s)" is truthful (the tenant-wide cascade guard
        // skipped), and the equality also pins that no "skipped"/"ambiguous" wording leaks —
        // that would be a one-bit "a same-id twin exists in your tenant" oracle.
        Assert.Equal("Deleted entry 'twin'. Removed 0 edge(s) and cleaned cluster memberships.", first);
        Assert.Null(_index.Get("twin", "ns-a", tenantId: Tenant));

        // The surviving twin keeps the shared topology it never should have lost.
        Assert.Equal("second twin", _index.Get("twin", "ns-b", tenantId: Tenant)?.Text);
        var edge = Assert.Single(_graph.GetEdgesForEntry("twin", tenantId: Tenant));
        Assert.Equal("anchor", edge.TargetId);
        Assert.Equal("similar_to", edge.Relation);
        Assert.Equal(2, _clusters.GetCluster("twin-cluster", tenantId: Tenant)!.MemberCount);

        // With one twin gone the id is unambiguous again: a bare-id delete of the survivor
        // resolves and cascades normally.
        var second = alice.DeleteMemory("twin");

        Assert.Equal("Deleted entry 'twin'. Removed 1 edge(s) and cleaned cluster memberships.", second);
        Assert.Null(_index.Get("twin", "ns-b", tenantId: Tenant));
        Assert.Empty(_graph.GetEdgesForEntry("twin", tenantId: Tenant));
        Assert.Empty(_clusters.GetClusterMembershipsForEntry("twin", tenantId: Tenant));
        Assert.Equal(1, _clusters.GetCluster("twin-cluster", tenantId: Tenant)!.MemberCount);
    }

    [Fact]
    public void ExplicitNs_IdAbsentFromNamedNamespace_IsByteEqualToGenuineMiss()
    {
        var alice = Core("alice");
        Assert.Contains("Stored entry", alice.StoreMemory("solo", "ns-a", "the only copy"));
        Assert.Contains("Stored entry", alice.StoreMemory("other", "ns-b", "unrelated entry"));

        var genuineMiss = alice.DeleteMemory("ghost", ns: "ns-a");
        var wrongNs = alice.DeleteMemory("solo", ns: "ns-b");

        // Byte-equal once the id this test itself varied is normalized: "exists, but not in the
        // namespace you named" must be indistinguishable from "exists nowhere".
        Assert.Equal("Entry 'ghost' not found.", genuineMiss);
        Assert.Equal(genuineMiss.Replace("ghost", "solo", StringComparison.Ordinal), wrongNs);
        Assert.Equal("the only copy", _index.Get("solo", "ns-a", tenantId: Tenant)?.Text);
    }

    [Fact]
    public void ExplicitNs_UnwritableNamespace_IsByteEqualToGenuineMiss_AndEntrySurvives()
    {
        var alice = Core("alice");
        Assert.Contains("Stored entry", alice.StoreMemory("secret-note", "alice-private", "private launch notes"));
        Assert.Contains("Stored entry", alice.StoreMemory("alice-anchor", "alice-private", "private anchor"));
        _graph.AddEdge(new GraphEdge("secret-note", "alice-anchor", "similar_to", tenantId: Tenant));
        Assert.False(_registry.HasAccess("bob", "alice-private", "write", tenantId: Tenant));

        var control = Core("bob").DeleteMemory("ghost", ns: "alice-private");
        var probe = Core("bob").DeleteMemory("secret-note", ns: "alice-private");

        // THIS EQUALITY IS THE SECURITY PROPERTY: a distinct denial for the entry that really
        // exists would confirm it exists in a namespace Bob cannot see, one id per call.
        Assert.Equal("Entry 'ghost' not found.", control);
        Assert.Equal(control.Replace("ghost", "secret-note", StringComparison.Ordinal), probe);

        // Nothing was destroyed — neither the entry nor, via a premature cascade, its topology.
        Assert.Equal("private launch notes", _index.Get("secret-note", "alice-private", tenantId: Tenant)?.Text);
        Assert.Single(_graph.GetEdgesForEntry("secret-note", tenantId: Tenant));
    }

    [Fact]
    public void NoNs_UnwritableTwin_NoLongerBlanksTheWritableDelete()
    {
        // The convergence delta, pinned deliberately: bare-id resolution previously used the
        // ACL-blind tenant-wide unique-match, so Alice's twin — in a namespace Bob cannot even
        // read — blanked Bob's legitimate delete into "not found". Resolution now applies the
        // write predicate BEFORE matching, so the id is unique among namespaces Bob may write
        // and his copy deletes.
        Assert.Contains("Stored entry", Core("alice").StoreMemory(
            "postmortem", "alice-private", "alice's private postmortem"));
        var bob = Core("bob");
        Assert.Contains("Stored entry", bob.StoreMemory("bob-anchor", "bob-work", "bob's anchor"));

        // Alice's topology is established while "postmortem" is still hers alone; Bob's colliding
        // copy arrives after. Seeding Bob's twin first would make both writes below fail closed on
        // the tenant-wide duplicate, so the test would be pinning an unreachable state.
        _graph.AddEdge(new GraphEdge("postmortem", "bob-anchor", "similar_to", tenantId: Tenant));
        _clusters.CreateCluster("alice-cluster", "alice-private", new[] { "postmortem" },
            "alice's cluster", tenantId: Tenant);
        Assert.Single(_graph.GetEdgesForEntry("postmortem", tenantId: Tenant));

        Assert.Contains("Stored entry", bob.StoreMemory("postmortem", "bob-work", "bob's working copy"));

        var reply = bob.DeleteMemory("postmortem");

        // The cascade stays TENANT-WIDE and ACL-blind — deliberately stricter than the
        // resolution — because the (tenant, id) topology is physically shared with Alice's
        // twin. Her twin and its topology survive, and the reply discloses none of it.
        Assert.Equal("Deleted entry 'postmortem'. Removed 0 edge(s) and cleaned cluster memberships.", reply);
        Assert.Null(_index.Get("postmortem", "bob-work", tenantId: Tenant));
        Assert.Equal("alice's private postmortem", _index.Get("postmortem", "alice-private", tenantId: Tenant)?.Text);
        Assert.Single(_graph.GetEdgesForEntry("postmortem", tenantId: Tenant));
        var membership = Assert.Single(_clusters.GetClusterMembershipsForEntry("postmortem", tenantId: Tenant));
        Assert.Equal("alice-cluster", membership.ClusterId);
    }

    [Fact]
    public void NoNs_TwoWritableTwins_StillRefusesAsNotFound()
    {
        // Ambiguity among namespaces the caller may write still fails closed — guessing would
        // let namespace enumeration order decide which entry dies. Same reply as a genuine
        // miss, so the refusal is not an ambiguity oracle either.
        var alice = Core("alice");
        Assert.Contains("Stored entry", alice.StoreMemory("twin", "ns-a", "first twin"));
        Assert.Contains("Stored entry", alice.StoreMemory("twin", "ns-b", "second twin"));

        var reply = alice.DeleteMemory("twin");

        Assert.Equal("Entry 'twin' not found.", reply);
        Assert.Equal("first twin", _index.Get("twin", "ns-a", tenantId: Tenant)?.Text);
        Assert.Equal("second twin", _index.Get("twin", "ns-b", tenantId: Tenant)?.Text);
    }

    [Fact]
    public void LegacyDefaultAgent_NoNs_UniqueId_BehaviourUnchanged()
    {
        // The single-user deployment: default agent, legacy "" tenant, unique id. The write
        // predicate admits every namespace, so unique-among-writable degenerates to the old
        // unique-match and the delete + full cascade + counts are exactly as before.
        var legacy = Core(AgentIdentity.DefaultAgentId, tenantId: "");
        Assert.Contains("Stored entry", legacy.StoreMemory("solo-legacy", "legacy-ns", "the only holder of this id"));
        Assert.Contains("Stored entry", legacy.StoreMemory("legacy-anchor", "legacy-ns", "legacy anchor"));
        _graph.AddEdge(new GraphEdge("solo-legacy", "legacy-anchor", "similar_to", tenantId: ""));
        _clusters.CreateCluster("legacy-cluster", "legacy-ns", new[] { "solo-legacy", "legacy-anchor" },
            "legacy cluster", tenantId: "");

        var reply = legacy.DeleteMemory("solo-legacy");

        Assert.Equal("Deleted entry 'solo-legacy'. Removed 1 edge(s) and cleaned cluster memberships.", reply);
        Assert.Null(_index.Get("solo-legacy", "legacy-ns", tenantId: ""));
        Assert.Empty(_graph.GetEdgesForEntry("solo-legacy", tenantId: ""));
        Assert.Empty(_clusters.GetClusterMembershipsForEntry("solo-legacy", tenantId: ""));
        Assert.Equal(1, _clusters.GetCluster("legacy-cluster", tenantId: "")!.MemberCount);
    }
}
