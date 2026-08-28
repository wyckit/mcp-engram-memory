using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

/// <summary>
/// The ACL-blind duplicate test is enforced at the CORE WRITER, not at each tool that reaches it.
///
/// Why that distinction is the whole point: the guard first shipped in the tool layer, and three
/// writers never applied it — <c>merge_memories</c> authorized the caller's own two entries and
/// then ran a tenant-wide bare-id <c>TransferEdges</c>/<c>TransferMembership</c>, background
/// auto-linking called <see cref="KnowledgeGraph.AddEdge"/> in a loop with no guard at all, and
/// accretion created and updated clusters straight through <see cref="ClusterManager"/>. A guard
/// that every new writer has to remember is a guard three writers already forgot.
///
/// So these tests deliberately avoid the tool layer wherever the claim is about enforcement:
/// <see cref="KnowledgeGraph"/> and <see cref="ClusterManager"/> are driven directly, and the two
/// paths that no tool guard could ever have covered — the merge and a background auto-link sweep —
/// are driven through their real callers. <see cref="BareIdTopologyIsolationTests"/> is the
/// complement: it pins what a caller is TOLD, which is the part the tool still decides.
///
/// Every principal is genuinely IDENTIFIED wherever an ACL matters.
/// <c>NamespaceRegistry.HasAccess</c> short-circuits <c>AgentIdentity.Default</c> to unrestricted,
/// so a default-agent version of the exploit tests would pass with the guard deleted and prove
/// nothing. The one deliberate default-agent test is the legacy mirror at the bottom, whose entire
/// job is to show that unique ids still behave exactly as they did.
/// </summary>
public class CentralizedTopologyGuardTests : IDisposable
{
    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 2;
        // Uniform embedding: anything reachable scores as a hit, so a suppressed edge can only be
        // the guard and never a similarity artifact. It also makes the auto-link sweep below pair
        // every entry in the namespace with every other, which is what puts the ambiguous id in
        // range of a writer that has no ACL of its own.
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    private const string AlicePrivateNs = "alice-private";
    private const string AliceSharedNs = "alice-shared";
    private const string BobNs = "bob-work";

    private const string AliceEdgeSecret = "alice-edge-metadata-hunter2";

    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly NamespaceRegistry _registry;
    private readonly StubEmbedding _embedding = new();

    public CentralizedTopologyGuardTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"central_topology_guard_{Guid.NewGuid():N}");
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

    // ── fixtures ──

    private static string Json(object? o) => System.Text.Json.JsonSerializer.Serialize(o);

    private CoreMemoryTools Core(string agentId) => new(
        _index, new PhysicsEngine(), _embedding, new MetricsCollector(), _graph,
        new QueryExpander(), new SpreadingActivationService(_index, _graph, _clusters),
        _clusters, _registry, new PrincipalContext(string.Empty, agentId));

    private GraphTools Graph(string agentId) => new(
        _graph, new AutoLinkScanner(_index, _graph, new DuplicateDetector()), _index,
        new NamespaceAccess(_registry, new PrincipalContext(string.Empty, agentId)));

    private IntelligenceTools Intel(string agentId) => new(
        _index, _graph, _embedding, new AccretionScanner(_index), _clusters,
        new LifecycleEngine(_index),
        new NamespaceAccess(_registry, new PrincipalContext(string.Empty, agentId)));

    /// <summary>Seed straight into the index — no principal, no ownership, no tool.</summary>
    private void Seed(string id, string ns, string text) =>
        _index.Upsert(new CognitiveEntry(id, _embedding.Embed(text), ns, text));

    private void Stores(string agentId, string id, string ns, string text) =>
        Assert.Contains("Stored entry", Core(agentId).StoreMemory(id, ns, text));

    /// <summary>Make <paramref name="id"/> name two of the tenant's namespaces — one node, two entries.</summary>
    private void SeedTwins(string id)
    {
        Seed(id, AlicePrivateNs, $"alice's {id}");
        Seed(id, BobNs, $"bob's {id}");
    }

    // ── 1. ENFORCEMENT IS AT THE CORE BOUNDARY: no tool anywhere in the path ──

    [Fact]
    public void KnowledgeGraph_AddEdge_RefusesADuplicatedEndpointWithNoToolInThePath()
    {
        SeedTwins("shared");
        Seed("anchor", BobNs, "an unambiguous anchor");

        var result = _graph.AddEdge(new GraphEdge("shared", "anchor", "elaborates", tenantId: ""));

        // Pinned as a literal, not as a call to the helper that produces it: this exact wording is
        // also what the tool layer returns for an id that genuinely does not exist and for one the
        // caller may not write, and the three reasons have to stay one reply.
        Assert.Equal("Error: Entry 'shared' not found.", result);
        Assert.Empty(_graph.GetEdgesForEntry("shared", tenantId: ""));
        Assert.Empty(_graph.GetEdgesForEntry("anchor", tenantId: ""));

        // The refusal is about ambiguity, not about the endpoint being unknown: an id no entry
        // answers to names no shared node, so a dangling edge is still allowed.
        Assert.StartsWith("Linked", _graph.AddEdge(
            new GraphEdge("anchor", "nothing-answers-to-this", "elaborates", tenantId: "")));
    }

    [Fact]
    public void KnowledgeGraph_AddEdges_WritesOnlyTheAttributableEdgesAndCountsWhatItWrote()
    {
        SeedTwins("shared");
        Seed("solo-a", BobNs, "solo a");
        Seed("solo-b", BobNs, "solo b");

        int created = _graph.AddEdges(new[]
        {
            new GraphEdge("solo-a", "solo-b", "elaborates", tenantId: ""),
            new GraphEdge("solo-a", "shared", "elaborates", tenantId: ""),
            new GraphEdge("shared", "solo-b", "elaborates", tenantId: ""),
        });

        // The count is what was written, not what was offered — a bulk writer that reported three
        // and stored one would make its own caller lie downstream.
        Assert.Equal(1, created);
        Assert.Empty(_graph.GetEdgesForEntry("shared", tenantId: ""));
        var surviving = Assert.Single(_graph.GetEdgesForEntry("solo-a", tenantId: ""));
        Assert.Equal("solo-b", surviving.TargetId);
    }

    [Fact]
    public void ClusterManager_CreateAndUpdate_DropDuplicatedMembersAndReportTheRealCount()
    {
        SeedTwins("shared");
        Seed("solo-a", BobNs, "solo a");
        Seed("solo-b", BobNs, "solo b");

        var created = _clusters.CreateCluster(
            "c1", BobNs, new[] { "solo-a", "shared" }, "a grouping", tenantId: "");

        Assert.Equal("Created cluster 'c1' with 1 members.", created);
        Assert.Empty(_clusters.GetClustersForEntry("shared", tenantId: ""));

        var updated = _clusters.UpdateCluster(
            "c1", addIds: new[] { "solo-b", "shared" }, removeIds: null, label: null, tenantId: "");

        Assert.Equal("Updated cluster 'c1' (2 members).", updated);
        Assert.Empty(_clusters.GetClustersForEntry("shared", tenantId: ""));
    }

    [Fact]
    public void ClusterManager_Update_RefusesToEvictAMembershipTheDuplicateNowShares()
    {
        // Membership established while the id was still unique, which is the only way a real
        // deployment gets here: the twin arrives afterwards and the bucket becomes shared.
        Seed("shared", AlicePrivateNs, "alice's entry");
        _clusters.CreateCluster("alice-cluster", AlicePrivateNs, new[] { "shared" }, "alice's grouping", tenantId: "");
        var before = Json(_clusters.GetClusterMembershipsForEntry("shared", tenantId: ""));

        Seed("shared", BobNs, "bob's later twin");

        _clusters.UpdateCluster("alice-cluster", addIds: null, removeIds: new[] { "shared" },
            label: null, tenantId: "");

        // Removal is a mutation of the shared bucket like any other, so it fails closed.
        Assert.Equal(before, Json(_clusters.GetClusterMembershipsForEntry("shared", tenantId: "")));
    }

    // ── 2. THE MERGE EXPLOIT — the writer no tool-layer guard covered ──

    [Fact]
    public void MergeMemories_ThroughWritableTwins_LeavesTheVictimsEdgesAndMembershipsByteForByte()
    {
        const string keepId = "postmortem";
        const string archiveId = "postmortem-draft";
        const string aliceAnchor = "alice-anchor";
        const string aliceClusterId = "alice-cluster";

        // Alice's world, established before any twin exists.
        Stores("alice", keepId, AlicePrivateNs, "alice's postmortem");
        Stores("alice", archiveId, AlicePrivateNs, "alice's earlier draft");
        Stores("alice", aliceAnchor, AlicePrivateNs, "alice's anchor");
        _graph.AddEdge(new GraphEdge(archiveId, aliceAnchor, "elaborates", 0.9f,
            new Dictionary<string, string> { ["note"] = AliceEdgeSecret }, tenantId: ""));
        _clusters.CreateCluster(aliceClusterId, AlicePrivateNs, new[] { archiveId }, "alice's grouping", tenantId: "");

        var edgesBefore = Json(_graph.GetStoredEdgesForEntry(archiveId, tenantId: ""));
        var membershipBefore = Json(_clusters.GetClusterMembershipsForEntry(archiveId, tenantId: ""));
        // Name the payload at stake, so the byte-equality below is a statement about Alice's
        // relation, weight and metadata and not just about a list staying the same length.
        Assert.Contains(AliceEdgeSecret, edgesBefore);
        Assert.NotEqual("[]", membershipBefore);

        // Bob mints writable twins of both ids in a namespace he genuinely owns, then merges them.
        // Every entry-scoped check passes: he owns bob-work, and both entries resolve inside it.
        Stores("bob", keepId, BobNs, "bob's copy of the postmortem");
        Stores("bob", archiveId, BobNs, "bob's copy of the draft");
        Assert.False(_registry.HasAccess("bob", AlicePrivateNs, "write", tenantId: ""));

        var reply = Intel("bob").MergeMemories(keepId, archiveId, BobNs);

        // The entry-scoped half is correctly authorized and still runs — over-correcting into a
        // refusal would deny Bob a legitimate operation on his own two entries and announce that
        // somebody else holds the same ids.
        Assert.StartsWith($"Merged '{archiveId}' into '{keepId}'.", reply);
        Assert.Equal("archived", _index.Get(archiveId, BobNs, tenantId: "")!.LifecycleState);

        // ...and it reached only Bob's namespace.
        Assert.Equal("stm", _index.Get(archiveId, AlicePrivateNs, tenantId: "")!.LifecycleState);

        // The topology half moved nothing, and the reply says so rather than claiming a merge it
        // did not perform. Zero is also what merging two unlinked entries has always reported, so
        // the number is honest without being a signal.
        Assert.Contains("Transferred 0 edge(s), 0 cluster(s)", reply);
        Assert.DoesNotContain(AliceEdgeSecret, reply);

        // THE PROPERTY: Alice's topology is byte-for-byte what it was.
        Assert.Equal(edgesBefore, Json(_graph.GetStoredEdgesForEntry(archiveId, tenantId: "")));
        Assert.Equal(membershipBefore, Json(_clusters.GetClusterMembershipsForEntry(archiveId, tenantId: "")));

        // Nothing was hung off the shared node in the other direction either — the traceability
        // edge merge_memories adds at the end is topology too.
        Assert.Empty(_graph.GetEdgesForEntry(keepId, tenantId: ""));
        Assert.Empty(_clusters.GetClustersForEntry(keepId, tenantId: ""));
    }

    // ── 3. THE BACKGROUND WRITER — no tool, no principal, no ACL of its own ──

    [Fact]
    public void AutoLinkSweep_RunningAsBackgroundMaintenance_HangsNoEdgeOffADuplicatedId()
    {
        // AutoLinkScanner calls KnowledgeGraph.AddEdge directly in a loop. It has no NamespaceAccess
        // and never passed through a tool guard, so before enforcement moved into Core this sweep
        // could attach edges to any shared node it happened to scan.
        Seed("solo-a", BobNs, "solo a");
        Seed("solo-b", BobNs, "solo b");
        SeedTwins("shared");

        var scanner = new AutoLinkScanner(_index, _graph, new DuplicateDetector());
        scanner.Scan(BobNs, threshold: 0.85f, maxNewEdges: 100, tenantId: "");

        // Every pair scores 1.0 under the uniform stub, so the sweep genuinely tried all three
        // pairs; only the two attributable endpoints got an edge.
        Assert.Empty(_graph.GetEdgesForEntry("shared", tenantId: ""));
        var edge = Assert.Single(_graph.GetEdgesForEntry("solo-a", tenantId: ""));
        Assert.Equal("similar_to", edge.Relation);
        Assert.True(edge.SourceId == "solo-b" || edge.TargetId == "solo-b");
    }

    // ── 4. THE TRAVERSAL EXPLOIT — the walk must stop, not be filtered afterwards ──

    [Fact]
    public void Traverse_StopsAtAnAmbiguousNode_AndNeverDiscoversWhatIsBehindIt()
    {
        const string rootId = "bob-root";
        const string sharedId = "shared-middle";
        const string noteId = "alice-note";

        // The reviewer's exact case: a unique root, an ambiguous twin one hop out, and a further
        // note the caller CAN read behind it. The chain is built while every id is still unique,
        // which is how a real deployment gets here.
        Stores("bob", rootId, BobNs, "bob's root");
        Stores("alice", sharedId, AlicePrivateNs, "alice's private middle");
        Stores("alice", noteId, AliceSharedNs, "alice's shared note");
        Assert.Equal("shared", _registry.Share(AliceSharedNs, "alice", "bob", "read", tenantId: "").Status);

        _graph.AddEdge(new GraphEdge(rootId, sharedId, "elaborates", tenantId: ""));
        _graph.AddEdge(new GraphEdge(sharedId, noteId, "elaborates", tenantId: ""));
        Assert.Equal(2, _graph.GetAllEdges(tenantId: "").Count);

        // Now Bob mints the twin that makes the middle node shared — and, through the legacy id
        // locator, makes it resolve into HIS readable namespace.
        Stores("bob", sharedId, BobNs, "bob's copy of the middle");

        var result = Graph("bob").TraverseGraph(rootId, maxDepth: 3);

        // THE ASSERTION THE OLD CODE FAILED. Stripping the two unsafe edges from the finished
        // result left both the twin and the note sitting in Entries, because the BFS had already
        // crossed the shared node to find them. Absence from Entries is the property; an edge
        // count of zero is not.
        Assert.DoesNotContain(result.Entries, e => e.Id == sharedId);
        Assert.DoesNotContain(result.Entries, e => e.Id == noteId);
        Assert.Equal(new[] { rootId }, result.Entries.Select(e => e.Id).ToArray());
        Assert.Empty(result.Edges);

        // Control on the same fixture: the chain really is in the graph, so this is a stop and not
        // an empty traversal.
        Assert.Equal(2, _graph.GetStoredEdges(tenantId: "").Count);
    }

    // ── 5. OVER-CORRECTION CONTROLS: no duplicate anywhere, everything still works ──
    //
    // The cheap wrong fix is to decline topology whenever anything is uncertain, which deletes the
    // graph and clustering features for everyone and would still make every test above green.

    [Fact]
    public void WithNoDuplicate_MergeStillTransfersEveryEdgeAndMembership()
    {
        Stores("bob", "keep", BobNs, "the keeper");
        Stores("bob", "dup", BobNs, "the duplicate");
        Stores("bob", "other", BobNs, "the other one");
        _graph.AddEdge(new GraphEdge("dup", "other", "similar_to", tenantId: ""));
        _clusters.CreateCluster("bob-cluster", BobNs, new[] { "dup" }, "bob's grouping", tenantId: "");

        var reply = Intel("bob").MergeMemories("keep", "dup", BobNs);

        Assert.Contains("Transferred 1 edge(s), 1 cluster(s)", reply);
        Assert.Contains(_graph.GetEdgesForEntry("keep", tenantId: ""), e => e.TargetId == "other");
        Assert.Contains("bob-cluster", _clusters.GetClustersForEntry("keep", tenantId: ""));
        Assert.Equal("archived", _index.Get("dup", BobNs, tenantId: "")!.LifecycleState);
    }

    [Fact]
    public void WithNoDuplicate_LinkTraverseClusterAndAutoLink_AllStillWorkEndToEnd()
    {
        Stores("bob", "n1", BobNs, "note one");
        Stores("bob", "n2", BobNs, "note two");
        Stores("bob", "n3", BobNs, "note three");
        var bob = Graph("bob");

        // link_memories still writes, through the tool and into Core.
        Assert.Contains("Linked", bob.LinkMemories("n1", "n2", relation: "elaborates"));
        Assert.Contains("Linked", bob.LinkMemories("n2", "n3", relation: "elaborates"));

        // get_neighbors still walks, and still reports the far endpoint.
        var neighbor = Assert.Single(bob.GetNeighbors("n1").Neighbors);
        Assert.Equal("n2", neighbor.Entry.Id);

        // traverse_graph still crosses every hop.
        var traversal = bob.TraverseGraph("n1", maxDepth: 3);
        Assert.Contains(traversal.Entries, e => e.Id == "n2");
        Assert.Contains(traversal.Entries, e => e.Id == "n3");
        Assert.Equal(2, traversal.Edges.Count);

        // Cluster create and update still take every member offered.
        Assert.Equal("Created cluster 'oc1' with 2 members.",
            _clusters.CreateCluster("oc1", BobNs, new[] { "n1", "n2" }, "grouping", tenantId: ""));
        Assert.Equal("Updated cluster 'oc1' (3 members).",
            _clusters.UpdateCluster("oc1", addIds: new[] { "n3" }, removeIds: null, label: null, tenantId: ""));

        // unlink_memories still removes.
        Assert.Contains("Removed", bob.UnlinkMemories("n1", "n2"));

        // ...and the background sweep still densifies a clean namespace.
        new AutoLinkScanner(_index, _graph, new DuplicateDetector())
            .Scan(BobNs, threshold: 0.85f, maxNewEdges: 100, tenantId: "");
        Assert.Contains(_graph.GetEdgesForEntry("n1", tenantId: ""),
            e => e.Relation == "similar_to");
    }

    // ── 6. LEGACY MIRROR: default agent, unique ids, every Core writer unchanged ──

    [Fact]
    public void DefaultAgent_WithUniqueIds_SeesEveryCoreWriterBehaveExactlyAsBefore()
    {
        // The common single-user deployment sets no AGENT_ID. The guard is tenant-wide and so
        // applies there too, but it triggers on AMBIGUITY, not on identity — with unique ids there
        // is nothing for it to catch and every writer must look untouched.
        const string ns = "legacy";
        Seed("leg-1", ns, "legacy one");
        Seed("leg-2", ns, "legacy two");
        Seed("leg-3", ns, "legacy three");

        Assert.Equal("Linked 'leg-1' -> 'leg-2' (similar_to).",
            _graph.AddEdge(new GraphEdge("leg-1", "leg-2", "similar_to", tenantId: "")));
        Assert.Equal(1, _graph.AddEdges(new[] { new GraphEdge("leg-2", "leg-3", "elaborates", tenantId: "") }));

        var traversal = _graph.Traverse("leg-1", tenantId: "", maxDepth: 3);
        Assert.Equal(3, traversal.Entries.Count);
        Assert.Equal(2, traversal.Edges.Count);

        Assert.Equal("Created cluster 'lc1' with 2 members.",
            _clusters.CreateCluster("lc1", ns, new[] { "leg-1", "leg-2" }, "legacy grouping", tenantId: ""));
        Assert.Equal("Updated cluster 'lc1' (3 members).",
            _clusters.UpdateCluster("lc1", addIds: new[] { "leg-3" }, removeIds: null, label: null, tenantId: ""));

        Assert.Equal(1, _clusters.TransferMembership("leg-1", "leg-2", tenantId: ""));
        Assert.Equal(1, _graph.TransferEdges("leg-1", "leg-3", tenantId: ""));

        Assert.StartsWith("Removed", _graph.RemoveEdges("leg-2", "leg-3", relation: null, tenantId: ""));
    }
}
