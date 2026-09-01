using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// The invariant is about the EDGE, not about the operation's arguments.
///
///     An edge is usable — readable, writable, transferable, traversable, boostable — only when
///     BOTH of its endpoints are attributable. A cluster member is exposable only when the member
///     itself is attributable.
///
/// Four review passes each guarded the ids an operation NAMES, and each time the next site was one
/// where the operation touched or exposed a node it does not name: <c>TransferEdges(from, to)</c>
/// rewriting an edge whose THIRD endpoint became shared, a safe seed handing back an edge that
/// points into a shared node, <c>get_cluster</c> resolving a bare member id into whichever twin the
/// caller can read. These tests are written the same way round: every fixture builds a node the
/// operation's signature never mentions, and then asks whether that node moved or showed up.
///
/// Why the exploit fixtures use the LEGACY tenant (""). Under a named tenant an ambiguous bare id
/// simply fails to resolve, so several of these paths would come back empty whether or not the
/// guard exists — the assertion would pass against the unfixed code and prove nothing. The legacy
/// id locator instead resolves an ambiguous id to whichever twin was written last, which is what
/// turns a shared node into a readable face. The over-correction control runs under a named tenant
/// so the fix is not shown working only in the partition the exploits use.
///
/// Every fixture also builds its topology while the ids are still UNIQUE and introduces the twin
/// afterwards. That is not staging convenience: topology writes fail closed on a tenant-wide
/// duplicate, so a fixture that created both twins first would be testing an unreachable state.
///
/// No ACL appears anywhere here, deliberately. <see cref="TopologyGuard"/> is ACL-blind — that is
/// the property that lets it live in Core — so these drive <see cref="KnowledgeGraph"/> and
/// <see cref="ClusterManager"/> with no principal and no registry at all. The tests that pin what
/// a PRINCIPAL is told need a genuinely identified agent (NamespaceRegistry.HasAccess
/// short-circuits the default agent to unrestricted); those live in BareIdTopologyIsolationTests,
/// and nothing here duplicates them.
/// </summary>
public class EdgeEndpointInvariantTests : IDisposable
{
    private const string LegacyTenant = "";
    private const string NamedTenant = "t1";

    private const string AliceNs = "alice-private";
    private const string BobNs = "bob-work";

    /// <summary>Named in an edge's metadata so a leak assertion can pin the payload, not just a count.</summary>
    private const string EdgeSecret = "alice-edge-metadata-hunter2";

    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;

    public EdgeEndpointInvariantTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"edge_endpoint_invariant_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 10);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    // ── fixtures ──

    private static string Json(object? o) => JsonSerializer.Serialize(o);

    /// <summary>Seed straight into the index — no principal, no ownership, no tool.</summary>
    private void Seed(string id, string ns, string text, string tenantId)
        => _index.Upsert(new CognitiveEntry(id, [0.5f, 0.5f], ns, text, tenantId: tenantId));

    /// <summary>
    /// Build a fixture edge, asserting it was actually written. A fixture that was silently refused
    /// would make every "nothing came back" assertion below vacuous.
    /// </summary>
    private void Link(string src, string dst, string relation, string tenantId,
        Dictionary<string, string>? metadata = null)
        => Assert.True(
            _graph.TryAddEdge(new GraphEdge(src, dst, relation, 0.9f, metadata, tenantId), out _),
            $"fixture edge '{src}' -> '{dst}' ({relation}) was refused");

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 1. THE TRANSFER EXPLOIT — the third endpoint of an edge the signature never names
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The reviewer's case. <c>from -&gt; far</c> is written while <c>far</c> is unique; <c>far</c>
    /// then gains a twin in a namespace of the same tenant; the merge asks for
    /// <c>TransferEdges(from, to)</c>. Both NAMED ids are attributable and always were, so an
    /// argument-level guard opens — and the rewrite lands <c>to -&gt; far</c> on a node that now
    /// belongs to two entries.
    ///
    /// All-or-nothing is asserted separately from the refusal, because "skip the offending edge"
    /// would also make the first assertion pass while leaving the merge half-applied:
    /// <c>from -&gt; safe-target</c> is perfectly attributable and must NOT move either.
    /// </summary>
    [Fact]
    public void TransferEdges_AbortsEverything_WhenAnyIncidentEdgeHasASharedThirdEndpoint()
    {
        Seed("from", AliceNs, "the entry being merged away", LegacyTenant);
        Seed("to", AliceNs, "the entry being merged into", LegacyTenant);
        Seed("far", AliceNs, "alice's private postmortem", LegacyTenant);
        Seed("safe-target", AliceNs, "an endpoint with no twin anywhere", LegacyTenant);
        Seed("origin", AliceNs, "an entry that points AT from", LegacyTenant);

        Link("from", "far", "elaborates", LegacyTenant,
            new Dictionary<string, string> { ["note"] = EdgeSecret });
        Link("from", "safe-target", "elaborates", LegacyTenant);
        // Incoming as well as outgoing: the pre-check has to see every incident edge in BOTH
        // directions, and an incoming-only exploit would slip past an outgoing-only screen.
        Link("origin", "from", "depends_on", LegacyTenant);

        // The twin arrives, and the node under "far" is now shared by two entries.
        Seed("far", BobNs, "bob's own working copy", LegacyTenant);

        var sharedNodeBefore = Json(_graph.GetStoredEdgesForEntry("far", tenantId: LegacyTenant));
        var wholeGraphBefore = Json(_graph.GetStoredEdges(tenantId: LegacyTenant));
        // Name the payload at stake so the byte-equality below is a statement about alice's
        // relation, weight and metadata rather than about a list staying the same length.
        Assert.Contains(EdgeSecret, sharedNodeBefore);

        int moved = _graph.TransferEdges("from", "to", tenantId: LegacyTenant);

        // Zero is what a merge of two edgeless entries already reports, so it is truthful without
        // being a signal — and the count of what was declined never reaches the caller.
        Assert.Equal(0, moved);

        // THE PROPERTY: the shared node is byte-for-byte what it was, and so is everything else.
        Assert.Equal(sharedNodeBefore, Json(_graph.GetStoredEdgesForEntry("far", tenantId: LegacyTenant)));
        Assert.Equal(wholeGraphBefore, Json(_graph.GetStoredEdges(tenantId: LegacyTenant)));

        // ALL OR NOTHING: the attributable edges did not move either, so the merge is not left
        // half-applied with some topology on "to" and some still on the abandoned node.
        Assert.Empty(_graph.GetStoredEdgesForEntry("to", tenantId: LegacyTenant));
        Assert.Equal(3, _graph.GetStoredEdgesForEntry("from", tenantId: LegacyTenant).Count);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 2. THE READ EXPLOIT — a SAFE seed whose edge points into a shared node.
    //    Asserted once per read path, because the whole finding is that one path was missed.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private const string SeedId = "alice-index-note";
    private const string FarId = "victim-id";
    private const string BeyondId = "alice-note-behind-the-shared-node";

    /// <summary>
    /// A seed that is and stays unique, one hop out to a node that becomes shared, and one more hop
    /// beyond it. The seed test opens — correctly, it is attributable — so anything withheld here
    /// was withheld by the FAR endpoint and by nothing else.
    /// </summary>
    private void SafeSeedPointingAtASharedNode()
    {
        Seed(SeedId, AliceNs, "alice's shared index note", LegacyTenant);
        Seed(FarId, AliceNs, "alice's private postmortem", LegacyTenant);
        Seed(BeyondId, AliceNs, "what sits behind the shared node", LegacyTenant);

        Link(SeedId, FarId, "elaborates", LegacyTenant,
            new Dictionary<string, string> { ["note"] = EdgeSecret });
        Link(FarId, BeyondId, "elaborates", LegacyTenant);

        Seed(FarId, BobNs, "bob's own working copy", LegacyTenant);
    }

    [Fact]
    public void GetNeighbors_OnASafeSeed_WithholdsTheEdgeIntoTheSharedNode()
    {
        SafeSeedPointingAtASharedNode();

        var neighbors = _graph.GetNeighbors(SeedId, relation: null, direction: "both", tenantId: LegacyTenant);

        Assert.Empty(neighbors.Neighbors);
        // Emptiness alone would pass against a reply that leaked the same facts through some other
        // field, so pin the payload itself.
        var json = Json(neighbors);
        Assert.DoesNotContain(EdgeSecret, json);
        Assert.DoesNotContain(FarId, json);

        // Control on the same fixture: the edge really is stored, so this is suppression and not an
        // empty graph. This is the read that spreading activation consumes — an edge handed back
        // here is an activation boost applied to whichever twin the id resolves to.
        Assert.NotEmpty(_graph.GetStoredEdgesForEntry(SeedId, tenantId: LegacyTenant));
    }

    [Fact]
    public void GetEdgesForEntry_OnASafeSeed_WithholdsTheEdgeIntoTheSharedNode()
    {
        SafeSeedPointingAtASharedNode();

        Assert.Empty(_graph.GetEdgesForEntry(SeedId, tenantId: LegacyTenant));
        Assert.DoesNotContain(EdgeSecret, Json(_graph.GetEdgesForEntry(SeedId, tenantId: LegacyTenant)));

        Assert.Single(_graph.GetStoredEdgesForEntry(SeedId, tenantId: LegacyTenant));
    }

    [Fact]
    public void GetAllEdges_ForATenant_WithholdsEveryEdgeTouchingTheSharedNode()
    {
        SafeSeedPointingAtASharedNode();

        // Both stored edges name the shared node at one end, so the attributable view is empty
        // while the stored view still has two. This is the list the diffusion kernel turns into a
        // basis and the visualizer draws, so an edge surviving here is an entry boosted or drawn.
        Assert.Empty(_graph.GetAllEdges(tenantId: LegacyTenant));
        Assert.Equal(2, _graph.GetStoredEdges(tenantId: LegacyTenant).Count);
    }

    [Fact]
    public void Traverse_FromASafeSeed_StopsAtTheSharedNodeAndNeverReachesWhatIsBehindIt()
    {
        SafeSeedPointingAtASharedNode();

        var result = _graph.Traverse(SeedId, tenantId: LegacyTenant, maxDepth: 3);

        // Absence from Entries is the property, not an edge count of zero: stripping unsafe edges
        // from a finished result leaves the twin and everything downstream sitting in Entries,
        // because by then the walk has already crossed the shared node to find them.
        Assert.Equal(new[] { SeedId }, result.Entries.Select(e => e.Id).ToArray());
        Assert.Empty(result.Edges);
        Assert.DoesNotContain(BeyondId, Json(result));

        Assert.Equal(2, _graph.GetStoredEdges(tenantId: LegacyTenant).Count);
    }

    [Fact]
    public void GetContradictions_WithholdsAPairWhoseOtherHalfIsShared()
    {
        Seed("claim", AliceNs, "cats are better", LegacyTenant);
        Seed("counter-claim", AliceNs, "dogs are better", LegacyTenant);
        Link("claim", "counter-claim", "contradicts", LegacyTenant,
            new Dictionary<string, string> { ["note"] = EdgeSecret });

        // Control first: while both halves are unique the pair really is surfaced, so the emptiness
        // below is the guard and not a fixture that never worked.
        Assert.Single(_graph.GetContradictions(AliceNs, tenantId: LegacyTenant));

        Seed("counter-claim", BobNs, "bob's own working copy", LegacyTenant);

        // This method resolves BOTH endpoints and hands the entries back, so an unattributable half
        // discloses a whole entry — and a "contradicts" claim about the wrong twin is not even true.
        Assert.Empty(_graph.GetContradictions(AliceNs, tenantId: LegacyTenant));
        Assert.Single(_graph.GetStoredEdges(tenantId: LegacyTenant));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 3. THE CLUSTER PROJECTION — a member id the caller never named
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void GetClusterAndListClusters_WithholdASharedMemberAndCountOnlyWhatTheyShow()
    {
        Seed("safe-member", AliceNs, "a member with no twin anywhere", LegacyTenant);
        Seed("shared-member", AliceNs, "alice's private postmortem", LegacyTenant);

        // Membership established while the id is unique — the only way a real deployment gets here.
        Assert.Equal("Created cluster 'alice-cluster' with 2 members.",
            _clusters.CreateCluster("alice-cluster", AliceNs,
                ["safe-member", "shared-member"], "alice's grouping", tenantId: LegacyTenant));

        Seed("shared-member", BobNs, "bob's own working copy", LegacyTenant);

        var view = _clusters.GetCluster("alice-cluster", tenantId: LegacyTenant);
        Assert.NotNull(view);

        // The projection resolves a BARE member id, so an unscreened member comes back as whichever
        // twin the caller can read, presented as a member of THIS cluster.
        Assert.Equal("safe-member", Assert.Single(view!.Members).Id);
        Assert.DoesNotContain("shared-member", Json(view));

        // The count follows the members it shows. A cluster reporting 2 while showing 1 restates
        // "a twin exists somewhere in this tenant" as arithmetic.
        Assert.Equal(1, view.MemberCount);
        Assert.Equal(1, Assert.Single(_clusters.ListClusters(AliceNs, tenantId: LegacyTenant)).MemberCount);

        // Control on the same fixture: the membership really is still stored, so the above is
        // suppression and not an empty cluster.
        Assert.Single(_clusters.GetClusterMembershipsForEntry("shared-member", tenantId: LegacyTenant));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 4. OVER-CORRECTION CONTROLS — no duplicate anywhere, under a NAMED tenant
    //
    // The cheap wrong fix is to withhold topology whenever anything is uncertain, which deletes the
    // graph and clustering features for everyone and would still make every test above green.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WithNoDuplicateAnywhere_EveryReadPathReturnsTheFullTopology()
    {
        Seed("n1", BobNs, "note one", NamedTenant);
        Seed("n2", BobNs, "note two", NamedTenant);
        Seed("n3", BobNs, "note three", NamedTenant);
        Link("n1", "n2", "similar_to", NamedTenant);
        Link("n2", "n3", "contradicts", NamedTenant);

        Assert.Equal("n2", Assert.Single(
            _graph.GetNeighbors("n1", relation: null, direction: "both", tenantId: NamedTenant).Neighbors)
            .Entry.Id);
        Assert.Single(_graph.GetEdgesForEntry("n1", tenantId: NamedTenant));
        Assert.Equal(2, _graph.GetAllEdges(tenantId: NamedTenant).Count);
        Assert.Single(_graph.GetContradictions(BobNs, tenantId: NamedTenant));

        var traversal = _graph.Traverse("n1", tenantId: NamedTenant, maxDepth: 3);
        Assert.Contains(traversal.Entries, e => e.Id == "n2");
        Assert.Contains(traversal.Entries, e => e.Id == "n3");
        Assert.Equal(2, traversal.Edges.Count);

        // ...and the attributable view agrees with what is stored, member for member.
        Assert.Equal(Json(_graph.GetStoredEdges(tenantId: NamedTenant)),
            Json(_graph.GetAllEdges(tenantId: NamedTenant)));
    }

    [Fact]
    public void WithNoDuplicateAnywhere_TransferMovesEveryIncidentEdgeAndClustersStayComplete()
    {
        Seed("from", BobNs, "the entry being merged away", NamedTenant);
        Seed("to", BobNs, "the entry being merged into", NamedTenant);
        Seed("far", BobNs, "an endpoint", NamedTenant);
        Seed("safe-target", BobNs, "another endpoint", NamedTenant);
        Seed("origin", BobNs, "an entry that points AT from", NamedTenant);

        Link("from", "far", "elaborates", NamedTenant);
        Link("from", "safe-target", "elaborates", NamedTenant);
        Link("origin", "from", "depends_on", NamedTenant);

        // Same shape as the exploit fixture, minus the twin: everything moves.
        Assert.Equal(3, _graph.TransferEdges("from", "to", tenantId: NamedTenant));
        Assert.Empty(_graph.GetStoredEdgesForEntry("from", tenantId: NamedTenant));
        Assert.Equal(3, _graph.GetEdgesForEntry("to", tenantId: NamedTenant).Count);

        Assert.Equal("Created cluster 'c1' with 2 members.",
            _clusters.CreateCluster("c1", BobNs, ["far", "safe-target"], "a grouping", tenantId: NamedTenant));
        Assert.Equal("Updated cluster 'c1' (3 members).",
            _clusters.UpdateCluster("c1", addIds: ["origin"], removeIds: null, label: null, tenantId: NamedTenant));

        var view = _clusters.GetCluster("c1", tenantId: NamedTenant);
        Assert.Equal(3, view!.MemberCount);
        Assert.Equal(3, view.Members.Count);
        Assert.Equal(3, Assert.Single(_clusters.ListClusters(BobNs, tenantId: NamedTenant)).MemberCount);
        Assert.Equal(1, _clusters.TransferMembership("far", "to", tenantId: NamedTenant));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 5. LEGACY MIRROR — default agent, unique ids, nothing changes
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The common single-user deployment sets no AGENT_ID and lives entirely in the legacy tenant.
    /// The rule is tenant-wide and so applies there too, but it triggers on AMBIGUITY rather than
    /// on identity — with unique ids there is nothing for it to catch and every path must look
    /// exactly as it did.
    /// </summary>
    [Fact]
    public void DefaultAgent_WithUniqueIds_SeesEveryEdgePathBehaveExactlyAsBefore()
    {
        const string ns = "legacy";
        Seed("leg-1", ns, "legacy one", LegacyTenant);
        Seed("leg-2", ns, "legacy two", LegacyTenant);
        Seed("leg-3", ns, "legacy three", LegacyTenant);

        Assert.Equal("Linked 'leg-1' -> 'leg-2' (similar_to).",
            _graph.AddEdge(new GraphEdge("leg-1", "leg-2", "similar_to", tenantId: LegacyTenant)));
        Assert.Equal(1, _graph.AddEdges([new GraphEdge("leg-2", "leg-3", "elaborates", tenantId: LegacyTenant)]));

        Assert.Equal(2, _graph.GetEdgesForEntry("leg-2", tenantId: LegacyTenant).Count);
        Assert.Equal(2, _graph.GetAllEdges(tenantId: LegacyTenant).Count);
        Assert.Equal(2, _graph.GetNeighbors("leg-2", relation: null, direction: "both", tenantId: LegacyTenant).Neighbors.Count);

        var traversal = _graph.Traverse("leg-1", tenantId: LegacyTenant, maxDepth: 3);
        Assert.Equal(3, traversal.Entries.Count);
        Assert.Equal(2, traversal.Edges.Count);

        Assert.Equal("Created cluster 'lc1' with 2 members.",
            _clusters.CreateCluster("lc1", ns, ["leg-1", "leg-2"], "legacy grouping", tenantId: LegacyTenant));
        Assert.Equal(2, _clusters.GetCluster("lc1", tenantId: LegacyTenant)!.MemberCount);

        Assert.Equal(2, _graph.RemoveAllEdgesForEntry("leg-2", tenantId: LegacyTenant));
        Assert.Equal(0, _graph.EdgeCount);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 6. DANGLING ENDPOINTS — an id in ZERO namespaces counts 0 and is SAFE
    //
    // The regression an over-eager fix is most likely to cause. A dangling edge is an
    // already-tolerated graph state (purge_debates leaves them behind on purpose), and an id no
    // entry answers to names no shared node, so there is nothing to be ambiguous about.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ADanglingEndpoint_IsWritableReadableTraversableAndRemovable()
    {
        Seed("anchor", AliceNs, "the only real entry here", LegacyTenant);

        // Written, and reported as written.
        Assert.True(_graph.TryAddEdge(
            new GraphEdge("anchor", "nothing-answers-to-this", "elaborates", tenantId: LegacyTenant),
            out var reply));
        Assert.StartsWith("Linked", reply);

        // ...and every edge-carrying read still hands it back.
        Assert.Single(_graph.GetEdgesForEntry("anchor", tenantId: LegacyTenant));
        Assert.Single(_graph.GetAllEdges(tenantId: LegacyTenant));
        Assert.Single(_graph.Traverse("anchor", tenantId: LegacyTenant, maxDepth: 2).Edges);

        // The cascade still reaches it: the far endpoint is dangling, not ambiguous.
        Assert.Equal(1, _graph.RemoveAllEdgesForEntry("anchor", tenantId: LegacyTenant));
        Assert.Equal(0, _graph.EdgeCount);
    }

    [Fact]
    public void ADanglingClusterMember_IsStillAdmittedAndStillCounted()
    {
        Seed("real-member", AliceNs, "a member that exists", LegacyTenant);

        // "ghost" answers to no entry at all, so it is unambiguous and admitted. It resolves to
        // nothing, so it cannot appear in Members — that divergence between MemberCount and
        // Members predates the guard and must survive it.
        Assert.Equal("Created cluster 'dangling' with 2 members.",
            _clusters.CreateCluster("dangling", AliceNs, ["real-member", "ghost"], "a grouping",
                tenantId: LegacyTenant));

        var view = _clusters.GetCluster("dangling", tenantId: LegacyTenant);
        Assert.Equal(2, view!.MemberCount);
        Assert.Equal("real-member", Assert.Single(view.Members).Id);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 7. EDGE ACCEPTANCE IS OBSERVABLE — the contract AutoLinkScanner counts against
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>AddEdge</c> answers with a sentence, so a caller that counted calls counted refusals as
    /// successes — the auto-link sweep reported edges it had never created. Parsing the sentence
    /// would be worse: the refusal is deliberately byte-identical to a genuine miss. Hence the bool.
    /// </summary>
    [Fact]
    public void TryAddEdge_ReportsRefusalForEitherEndpoint_AndWritesNothing()
    {
        Seed("shared", AliceNs, "alice's copy", LegacyTenant);
        Seed("shared", BobNs, "bob's copy", LegacyTenant);
        Seed("anchor", AliceNs, "an unambiguous anchor", LegacyTenant);

        Assert.False(_graph.TryAddEdge(
            new GraphEdge("shared", "anchor", "elaborates", tenantId: LegacyTenant), out var asSource));
        Assert.False(_graph.TryAddEdge(
            new GraphEdge("anchor", "shared", "elaborates", tenantId: LegacyTenant), out var asTarget));

        // Both name the endpoint that failed, and both read as an ordinary miss — the same string
        // the tool layer returns for an id that does not exist and for one the caller may not write.
        Assert.Equal("Error: Entry 'shared' not found.", asSource);
        Assert.Equal("Error: Entry 'shared' not found.", asTarget);

        // The string-returning overload is the same call, so the two can never drift apart.
        Assert.Equal(asSource, _graph.AddEdge(new GraphEdge("shared", "anchor", "elaborates", tenantId: LegacyTenant)));

        // "Reported as refused" and "actually refused" are different claims; pin both.
        Assert.Empty(_graph.GetStoredEdgesForEntry("anchor", tenantId: LegacyTenant));
        Assert.Empty(_graph.GetStoredEdgesForEntry("shared", tenantId: LegacyTenant));

        // And the true case reports true, so a caller counting on this cannot under-report either.
        Assert.True(_graph.TryAddEdge(
            new GraphEdge("anchor", "also-dangling", "elaborates", tenantId: LegacyTenant), out _));
    }
}
