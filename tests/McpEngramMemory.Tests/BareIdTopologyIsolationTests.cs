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
/// Isolation of TOPOLOGY reached by a bare id, which is a different problem from isolation of the
/// entries topology points at — and the one the entry-scoped resolver structurally cannot solve.
///
/// The invariant: an entry is identified by (tenant, namespace, id) and ids are unique only per
/// (tenant, namespace), but <see cref="KnowledgeGraph"/> adjacency and <see cref="ClusterManager"/>
/// membership are keyed (tenant, id) with NO namespace. Two same-id entries in two namespaces of
/// one tenant therefore share ONE graph node and ONE membership bucket. They are not two similar
/// objects; they are the same object.
///
/// That is what makes "resolve the caller's visible twin, then act" unsafe here. Resolution
/// through <see cref="EntryAccessResolver"/> is ACL-filtered by design and so cannot see the twin
/// that makes the node shared — it authorizes the entry it found and then the operation touches a
/// node that also belongs to an entry it was never shown. Authorize object A, act on object B.
/// Topology sites therefore add the ACL-BLIND tenant-wide test in
/// <see cref="McpEngramMemory.Core.Services.Graph.TopologyGuard"/>.
///
/// These tests drive the TOOL surface, which is what they are for: they pin the caller-visible
/// reply, and the reply is the part the tool decides. That the predicate itself now lives in Core
/// is pinned separately by CentralizedTopologyGuardTests, which drives the writers with no tool in
/// the path at all.
///
/// Every principal here is genuinely IDENTIFIED. <c>NamespaceRegistry.HasAccess</c> short-circuits
/// <c>AgentIdentity.Default</c> to unrestricted, so a default-agent version of these tests would
/// pass with the guard deleted and prove nothing. The one deliberate default-agent test is the
/// legacy mirror at the bottom, whose entire job is to show nothing changed there.
/// </summary>
public class BareIdTopologyIsolationTests : IDisposable
{
    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 2;
        // Uniform embedding: anything reachable scores as a hit, so a leak can only be a
        // permission failure and never a similarity artifact.
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    // Alice's namespaces. The victim entry lives in the private one; the note it links to lives in
    // a namespace Alice shares READ with Bob. That split is what makes the read exploit real: if
    // both ends were private, AdminTools' pre-existing endpoint filter would drop the edge for the
    // wrong reason and the test would pass without the topology guard ever running.
    private const string AlicePrivateNs = "alice-private";
    private const string AliceSharedNs = "alice-shared";

    // Bob genuinely owns this. An identified principal inherits nothing from an unregistered
    // namespace, so Bob has to be a real owner or the gates would deny for the wrong reason.
    private const string BobNs = "bob-work";

    private const string VictimId = "victim-id";
    private const string AliceIndexNote = "alice-index-note";
    private const string AliceClusterId = "alice-cluster";
    private const string AliceEdgeSecret = "alice-edge-metadata-hunter2";
    private const string BobTwinText = "bob's own working copy";
    private const string MissingId = "no-such-entry-anywhere";

    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly NamespaceRegistry _registry;
    private readonly StubEmbedding _embedding = new();

    public BareIdTopologyIsolationTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"bare_id_topology_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 10);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
        _registry = new NamespaceRegistry(_index, _embedding);

        // ── Alice's world, established before any twin exists ──
        _index.Upsert(new CognitiveEntry(
            VictimId, _embedding.Embed("postmortem"), AlicePrivateNs, "alice's private postmortem"));
        _registry.EnsureOwnership(AlicePrivateNs, "alice", tenantId: "");

        _index.Upsert(new CognitiveEntry(
            AliceIndexNote, _embedding.Embed("index"), AliceSharedNs, "alice's shared index note"));
        _registry.EnsureOwnership(AliceSharedNs, "alice", tenantId: "");
        _registry.Share(AliceSharedNs, "alice", "bob", "read", tenantId: "");

        // Alice's private topology: her postmortem elaborates her shared note. The relation, the
        // weight and the metadata are all hers, and all of it hangs off the bare id "victim-id".
        _graph.AddEdge(new GraphEdge(
            VictimId, AliceIndexNote, "elaborates", 0.9f,
            new Dictionary<string, string> { ["note"] = AliceEdgeSecret },
            tenantId: ""));

        // ...and her grouping. The cluster lives in the namespace Bob may read, so the read gate
        // AdminTools already applies (CanRead of the cluster's OWN namespace) legitimately opens —
        // only the topology guard can keep this membership off Bob's twin.
        _clusters.CreateCluster(AliceClusterId, AliceSharedNs, new[] { VictimId }, "alice's grouping", tenantId: "");
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    // ── fixtures ──

    private static string Json(object? o) => System.Text.Json.JsonSerializer.Serialize(o);

    private CoreMemoryTools Core(string agentId) => new(
        _index, new PhysicsEngine(), _embedding, new MetricsCollector(), _graph,
        new QueryExpander(), new SpreadingActivationService(_index, _graph, _clusters),
        _clusters, _registry, new PrincipalContext(string.Empty, agentId));

    private GraphTools Graph(string agentId) => new(
        _graph, new AutoLinkScanner(_index, _graph, new DuplicateDetector()), _index,
        new NamespaceAccess(_registry, new AgentIdentity(agentId)));

    private AdminTools Admin(string agentId) => new(
        _index, _graph, _clusters, _persistence, _registry, new AgentIdentity(agentId));

    /// <summary>Bob creates his own writable entry under the id Alice's topology is keyed on.</summary>
    private void BobCreatesTheTwin() =>
        Assert.Contains("Stored entry", Core("bob").StoreMemory(VictimId, BobNs, BobTwinText));

    private void BobStores(string id, string text) =>
        Assert.Contains("Stored entry", Core("bob").StoreMemory(id, BobNs, text));

    // ── 1. WRITE EXPLOIT: create a writable twin, then mutate the shared node ──

    [Fact]
    public void LinkAndUnlink_ThroughAWritableTwin_AreRefusedAndLeaveTheSharedNodeUntouched()
    {
        BobCreatesTheTwin();
        BobStores("bob-note", "bob's follow-up note");
        Assert.False(_registry.HasAccess("bob", AlicePrivateNs, "write", tenantId: ""));

        var before = Json(_graph.GetEdgesForEntry(VictimId, tenantId: ""));
        var bob = Graph("bob");

        // Bob resolves "victim-id" to an entry he genuinely owns, so entry-scoped authorization
        // says yes. The node he is about to write is Alice's too.
        var linked = bob.LinkMemories(VictimId, "bob-note", relation: "elaborates");
        // And the reverse: naming Alice's actual edge and asking for it to be removed.
        var unlinked = bob.UnlinkMemories(VictimId, AliceIndexNote);

        Assert.Equal($"Error: Entry '{VictimId}' not found.", linked);
        Assert.Equal($"Error: Entry '{VictimId}' not found.", unlinked);

        // Byte-equal to a genuine miss once the id this test varied is normalized away. THIS
        // EQUALITY IS THE PROPERTY: a distinct "ambiguous" reply would tell Bob that a same-id
        // entry exists somewhere he cannot see, one probe at a time.
        var genuineMiss = bob.LinkMemories(MissingId, "bob-note", relation: "elaborates");
        Assert.Equal(genuineMiss.Replace(MissingId, VictimId, StringComparison.Ordinal), linked);

        // Alice's edge set is byte-for-byte what it was: nothing added, nothing removed, and the
        // relation/weight/metadata she set are intact.
        Assert.Equal(before, Json(_graph.GetEdgesForEntry(VictimId, tenantId: "")));
        var surviving = Assert.Single(_graph.GetEdgesForEntry(VictimId, tenantId: ""));
        Assert.Equal(AliceIndexNote, surviving.TargetId);
        Assert.Equal("elaborates", surviving.Relation);
    }

    // ── 2. READ EXPLOIT: get_memory serves the twin's entry, never the shared node ──

    [Fact]
    public void GetMemory_ThroughAReadableTwin_ReturnsTheEntryWithNoEdgesAndNoClusters()
    {
        BobCreatesTheTwin();

        var result = Assert.IsType<GetMemoryResult>(Admin("bob").GetMemory(VictimId));
        var json = Json(result);

        // The ENTRY resolution is unchanged and still correct — it is entry-scoped, and Bob's
        // entry is the object he asked about. Suppressing topology must not degrade into
        // suppressing the reply, or Bob could read his own permission level off its shape.
        Assert.Equal(BobNs, result.Entry.Namespace);
        Assert.Equal(BobTwinText, result.Text);

        // The topology is not his to receive. Both collections come back empty.
        Assert.Empty(result.Edges);
        Assert.Empty(result.ClusterIds);

        // Asserting emptiness alone would still pass against an implementation that leaked the
        // same facts through some other field, so pin the absence of the payload itself: Alice's
        // edge metadata, the endpoint her edge names, and the cluster she grouped it into.
        Assert.DoesNotContain(AliceEdgeSecret, json);
        Assert.DoesNotContain(AliceClusterId, json);
        Assert.DoesNotContain(AliceIndexNote, json);

        // Control on the same call: Alice's node really does carry that topology, so the emptiness
        // above is suppression and not an empty fixture.
        Assert.NotEmpty(_graph.GetEdgesForEntry(VictimId, tenantId: ""));
        Assert.NotEmpty(_clusters.GetClustersForEntry(VictimId, tenantId: ""));
    }

    // ── 3. READ EXPLOIT: the traversal verbs refuse an ambiguous seed ──

    [Fact]
    public void GetNeighbors_OnAnAmbiguousSeed_AnswersExactlyAsForAnIdThatDoesNotExist()
    {
        BobCreatesTheTwin();
        var bob = Graph("bob");

        var suppressed = Json(bob.GetNeighbors(VictimId));
        var genuineMiss = Json(bob.GetNeighbors(MissingId));

        Assert.Empty(bob.GetNeighbors(VictimId).Neighbors);
        Assert.DoesNotContain(AliceIndexNote, suppressed);
        Assert.Equal(genuineMiss.Replace(MissingId, VictimId, StringComparison.Ordinal), suppressed);
    }

    /// <summary>
    /// The half a safe seed does not buy. Bob may read <see cref="AliceSharedNs"/>, so
    /// <see cref="AliceIndexNote"/> is a legitimate seed for him and it is unique in the tenant —
    /// the seed test opens, correctly. The incoming edge hanging off it is ALICE'S, and its far
    /// endpoint is the shared id. Once Bob owns a twin of that id the legacy locator resolves the
    /// endpoint into HIS namespace, the read filter passes it, and Alice's edge comes back to him
    /// with her relation, weight and metadata attached to his own entry. This is a payload leak,
    /// not the one accepted bit, which is why the neighbor is filtered and not merely the seed.
    /// </summary>
    [Fact]
    public void GetNeighbors_OnASafeSeed_WithholdsAnEdgeWhoseFarEndpointIsAmbiguous()
    {
        BobCreatesTheTwin();
        var bob = Graph("bob");

        var neighbors = bob.GetNeighbors(AliceIndexNote);

        Assert.Empty(neighbors.Neighbors);
        // Emptiness alone would also pass against a reply that leaked the same facts elsewhere,
        // so pin the payload: Alice's edge metadata and the id her edge names.
        var json = Json(neighbors);
        Assert.DoesNotContain(AliceEdgeSecret, json);
        Assert.DoesNotContain(VictimId, json);

        // Control on the same fixture: the edge really is on that node, so the emptiness above is
        // suppression and not an empty graph.
        Assert.NotEmpty(_graph.GetEdgesForEntry(AliceIndexNote, tenantId: ""));
    }

    [Fact]
    public void TraverseGraph_OnAnAmbiguousSeed_AnswersExactlyAsForAnIdThatDoesNotExist()
    {
        BobCreatesTheTwin();
        var bob = Graph("bob");

        var suppressed = Json(bob.TraverseGraph(VictimId));
        var genuineMiss = Json(bob.TraverseGraph(MissingId));

        var result = bob.TraverseGraph(VictimId);
        Assert.Empty(result.Entries);
        Assert.Empty(result.Edges);
        Assert.DoesNotContain(AliceIndexNote, suppressed);
        Assert.Equal(genuineMiss.Replace(MissingId, VictimId, StringComparison.Ordinal), suppressed);
    }

    // ── 4. OVER-CORRECTION CONTROLS: no twin, same principals, full topology ──
    //
    // The cheap wrong fix is to suppress topology whenever anything is uncertain, which deletes
    // the graph feature for everyone and would still make tests 1-3 green. These are what tell a
    // fix apart from a deletion. Same identified principal, ids with no twin anywhere.

    [Fact]
    public void WithNoTwin_LinkGetNeighborsTraverseAndGetMemory_AllStillReturnFullTopology()
    {
        BobStores("bob-a", "bob's first note");
        BobStores("bob-b", "bob's second note");
        _clusters.CreateCluster("bob-cluster", BobNs, new[] { "bob-a" }, "bob's grouping", tenantId: "");

        var bob = Graph("bob");

        // link_memories still writes.
        Assert.Contains("Linked", bob.LinkMemories("bob-a", "bob-b", relation: "elaborates"));

        // get_neighbors still walks.
        var neighbor = Assert.Single(bob.GetNeighbors("bob-a").Neighbors);
        Assert.Equal("bob-b", neighbor.Entry.Id);

        // traverse_graph still walks.
        var traversal = bob.TraverseGraph("bob-a");
        Assert.Contains(traversal.Entries, e => e.Id == "bob-b");
        Assert.Contains(traversal.Edges, e => e.SourceId == "bob-a" && e.TargetId == "bob-b");

        // get_memory still reports edges AND cluster membership.
        var memory = Assert.IsType<GetMemoryResult>(Admin("bob").GetMemory("bob-a"));
        Assert.Contains(memory.Edges, e => e.TargetId == "bob-b");
        Assert.Contains("bob-cluster", memory.ClusterIds);

        // unlink_memories still removes.
        Assert.Contains("Removed", bob.UnlinkMemories("bob-a", "bob-b"));
        Assert.Empty(bob.GetNeighbors("bob-a").Neighbors);
    }

    // ── 5. LEGACY MIRROR: default agent, unique ids, nothing changes ──

    [Fact]
    public void DefaultAgent_WithUniqueIds_SeesEveryTopologyPathBehaveAsBefore()
    {
        // The common single-user deployment sets no AGENT_ID. The guard is tenant-wide and so
        // applies to the default agent too, but it triggers on AMBIGUITY, not on identity — with
        // unique ids there is nothing for it to catch and every path must look untouched.
        const string ns = "legacy-a";
        _index.Upsert(new CognitiveEntry("leg-1", _embedding.Embed("one"), ns, "legacy one"));
        _index.Upsert(new CognitiveEntry("leg-2", _embedding.Embed("two"), ns, "legacy two"));
        _index.Upsert(new CognitiveEntry("leg-3", _embedding.Embed("three"), ns, "legacy three"));
        _graph.AddEdge(new GraphEdge("leg-1", "leg-2", "similar_to", tenantId: ""));
        _clusters.CreateCluster("legacy-cluster", ns, new[] { "leg-1" }, "legacy grouping", tenantId: "");

        var legacy = new GraphTools(
            _graph, new AutoLinkScanner(_index, _graph, new DuplicateDetector()), _index,
            new NamespaceAccess(_registry, AgentIdentity.Default));
        var admin = new AdminTools(_index, _graph, _clusters, _persistence, _registry, AgentIdentity.Default);

        Assert.Contains("Linked", legacy.LinkMemories("leg-1", "leg-3", relation: "elaborates"));

        var neighborIds = legacy.GetNeighbors("leg-1").Neighbors.Select(n => n.Entry.Id).ToList();
        Assert.Contains("leg-2", neighborIds);
        Assert.Contains("leg-3", neighborIds);

        var traversal = legacy.TraverseGraph("leg-1");
        Assert.Contains(traversal.Entries, e => e.Id == "leg-1");
        Assert.Contains(traversal.Edges, e => e.TargetId == "leg-2");

        var memory = Assert.IsType<GetMemoryResult>(admin.GetMemory("leg-1"));
        Assert.Contains(memory.Edges, e => e.TargetId == "leg-2");
        Assert.Contains("legacy-cluster", memory.ClusterIds);
    }
}
