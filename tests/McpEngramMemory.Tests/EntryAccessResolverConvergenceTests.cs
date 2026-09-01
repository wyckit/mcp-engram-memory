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
/// Pins the deliberate behavior delta from converging the four ad-hoc bare-id resolvers
/// (GraphTools.DenyIfCannotWrite, LifecycleTools.Resolve, AdminTools.CanReadEndpoint,
/// CoreMemoryTools.CanReadEntryById) onto <see cref="EntryAccessResolver"/>.
///
/// The delta, precisely: the legacy branch in GraphTools/LifecycleTools resolved a bare id via
/// the global id-to-namespace map, where an id duplicated across namespaces resolved to
/// whichever twin the map happened to hold ("first match wins"). After convergence the rule is
/// "unique match among the namespaces the caller's verb-predicate admits" — an ambiguous id
/// refuses with the same reply as a genuine miss instead of linking/promoting/feeding back
/// against an arbitrary twin. AdminTools/CoreMemoryTools already used the unique-among-all
/// GetForTenant, so their only delta is identified-principal and in the safe direction: an
/// invisible same-id twin can no longer blank (or win) resolution of the one visible entry.
///
/// That convergence covers ENTRY-scoped operations only. Resolving to the twin the caller can see
/// authorizes an operation on that qualified entry, and nothing more — it cannot authorize a
/// TOPOLOGY operation, because graph adjacency and cluster membership are keyed (tenant, id) with
/// no namespace and the two twins share one node. Topology sites therefore take the ACL-BLIND
/// tenant-wide test in <see cref="BareIdTopology"/> on top of resolution, which is why the last
/// test here is the mirror image of the entry-scoped ones rather than another instance of them.
/// </summary>
public class EntryAccessResolverConvergenceTests : IDisposable
{
    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => new[] { 0.5f, 0.5f };
    }

    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly NamespaceRegistry _registry;
    private readonly StubEmbedding _embedding = new();

    // Default-agent (legacy, unisolated) tool set — the predicate admits every namespace, so
    // resolution degenerates to unique-among-all-legacy-namespaces.
    private readonly GraphTools _graphTools;
    private readonly LifecycleTools _lifecycleTools;
    private readonly AdminTools _adminTools;

    public EntryAccessResolverConvergenceTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"resolver_convergence_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
        _registry = new NamespaceRegistry(_index, _embedding);

        var access = new NamespaceAccess(_registry, AgentIdentity.Default);
        var autoLink = new AutoLinkScanner(_index, _graph, new DuplicateDetector());
        _graphTools = new GraphTools(_graph, autoLink, _index, access);
        _lifecycleTools = new LifecycleTools(new LifecycleEngine(_index), _embedding, _index, access);
        _adminTools = new AdminTools(_index, _graph, _clusters, _persistence, _registry, AgentIdentity.Default);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    /// <summary>Identified-agent tool factory for the ACL-facing case, modeled on NamespaceAclEnforcementTests.</summary>
    private CoreMemoryTools Core(string agentId) => new(
        _index, new PhysicsEngine(), _embedding, new MetricsCollector(), _graph,
        new QueryExpander(), new SpreadingActivationService(_index, _graph, _clusters),
        _clusters, _registry, new PrincipalContext(string.Empty, agentId));

    private GraphTools Graph(string agentId) => new(
        _graph, new AutoLinkScanner(_index, _graph, new DuplicateDetector()), _index,
        new NamespaceAccess(_registry, new PrincipalContext(string.Empty, agentId)));

    /// <summary>Seed the same id into two legacy namespaces, making it ambiguous among all.</summary>
    private void SeedAmbiguousTwins()
    {
        _index.Upsert(new CognitiveEntry("dup", new[] { 1f, 0f }, "work", "twin in work", lifecycleState: "stm"));
        _index.Upsert(new CognitiveEntry("dup", new[] { 0f, 1f }, "personal", "twin in personal", lifecycleState: "stm"));
    }

    // ── The legacy-semantics change, pinned deliberately ──

    [Fact]
    public void LinkMemories_LegacyAmbiguousId_RefusesInsteadOfResolvingThroughGlobalMap()
    {
        // Through 1.6.0 the legacy branch resolved "dup" via the global id->ns map, so this
        // call linked whichever twin the map happened to hold. This test documents the change:
        // ambiguous-among-visible now refuses, indistinguishably from a genuine miss.
        SeedAmbiguousTwins();
        _index.Upsert(new CognitiveEntry("b", new[] { 1f, 1f }, "work", "unambiguous target"));

        var result = _graphTools.LinkMemories("dup", "b", relation: "similar_to");

        Assert.Equal("Error: Entry 'dup' not found.", result);
        // Byte-equal to a genuine miss's reply once the id this test varied is normalized away.
        // THIS EQUALITY IS THE PROPERTY: a distinct "ambiguous" reply would be a one-bit oracle.
        var genuineMiss = _graphTools.LinkMemories("no-such-entry-anywhere", "b", relation: "similar_to");
        Assert.Equal(genuineMiss.Replace("no-such-entry-anywhere", "dup", StringComparison.Ordinal), result);

        Assert.Empty(_graph.GetEdgesForEntry("dup", tenantId: ""));
    }

    [Fact]
    public void PromoteMemory_LegacyAmbiguousId_Refuses()
    {
        SeedAmbiguousTwins();

        var result = _lifecycleTools.PromoteMemory("dup", "ltm");

        Assert.Equal("Error: Entry 'dup' not found.", result);
        var genuineMiss = _lifecycleTools.PromoteMemory("no-such-entry-anywhere", "ltm");
        Assert.Equal(genuineMiss.Replace("no-such-entry-anywhere", "dup", StringComparison.Ordinal), result);

        // Neither twin moved: the refusal is a refusal, not a promotion of an arbitrary twin.
        Assert.Equal("stm", _index.Get("dup", "work", tenantId: "")!.LifecycleState);
        Assert.Equal("stm", _index.Get("dup", "personal", tenantId: "")!.LifecycleState);
    }

    [Fact]
    public void MemoryFeedback_LegacyAmbiguousId_Refuses()
    {
        SeedAmbiguousTwins();

        var result = Assert.IsType<string>(_lifecycleTools.MemoryFeedback("dup", 2.0f));

        Assert.Equal("Error: Entry 'dup' not found.", result);
        var genuineMiss = Assert.IsType<string>(_lifecycleTools.MemoryFeedback("no-such-entry-anywhere", 2.0f));
        Assert.Equal(genuineMiss.Replace("no-such-entry-anywhere", "dup", StringComparison.Ordinal), result);
    }

    // ── AdminTools: ambiguity refusal at the edge filter, previously only implicit ──

    [Fact]
    public void GetMemory_EdgeToLegacyAmbiguousEndpoint_IsFiltered()
    {
        SeedAmbiguousTwins();
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 1f }, "work", "entry a"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.5f, 0.5f }, "work", "entry b"));
        _graph.AddEdge(new GraphEdge("a", "dup", "similar_to", tenantId: ""));
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to", tenantId: ""));

        var result = Assert.IsType<GetMemoryResult>(_adminTools.GetMemory("a"));

        // The edge to the unambiguous endpoint survives; the edge whose endpoint cannot be
        // resolved to a single namespace is dropped rather than resolved by guesswork.
        var edge = Assert.Single(result.Edges);
        Assert.Equal("b", edge.TargetId);
        Assert.DoesNotContain(result.Edges, e => e.TargetId == "dup");
    }

    // ── Identified principal: the graph path is stricter than the entry path, on purpose ──

    [Fact]
    public void LinkMemories_IdentifiedAgent_RefusesWhenAnInvisibleTwinSharesTheGraphNode()
    {
        // This test used to assert the opposite, and asserting the opposite was the bug. The
        // reasoning that produced it is sound for an ENTRY-scoped verb and wrong for this one.
        //
        // For an entry-scoped verb — promote, feedback, get_memory's primary object — resolution
        // is rightly ACL-filtered: alice's invisible twin is a DIFFERENT object living at a
        // different (tenant, namespace, id), so it must contribute neither a match nor an
        // ambiguity signal, or "your operation silently did nothing" announces that a private
        // twin exists. Bob's entry stays unique among the namespaces his write predicate admits
        // and the verb lands on it. T1-T4 above are all that shape and all still hold.
        //
        // link_memories is not that verb. It writes GRAPH TOPOLOGY, and KnowledgeGraph keys
        // adjacency (tenant, id) with no namespace — so bob's "shared-id" and alice's
        // "shared-id" are not two nodes that resemble each other, they are ONE node. Authorizing
        // through the twin bob can see and then writing to that node mutates topology that reads
        // as belonging to alice's private entry: authorize object A, act on object B. Resolution
        // is ACL-filtered and structurally cannot see the twin that makes the node shared, which
        // is exactly why the topology gate has to be the ACL-BLIND tenant-wide test in
        // BareIdTopology and cannot be folded into resolution.
        //
        // Namespace-qualified endpoints (issue #19) are the real fix and would let this succeed
        // safely. Until then a shared node fails closed, and the cost — one bit, "a twin exists
        // somewhere in this tenant" — is documented on BareIdTopology and accepted.
        Assert.Contains("Stored entry", Core("alice").StoreMemory(
            "shared-id", "alice-private", "alice's private postmortem"));
        Assert.Contains("Stored entry", Core("bob").StoreMemory(
            "shared-id", "bob-work", "bob's working copy"));
        Assert.Contains("Stored entry", Core("bob").StoreMemory(
            "bob-note", "bob-work", "bob's follow-up note"));
        Assert.False(_registry.HasAccess("bob", "alice-private", "write", tenantId: ""));

        var result = Graph("bob").LinkMemories("shared-id", "bob-note", relation: "elaborates");

        Assert.Equal("Error: Entry 'shared-id' not found.", result);

        // Byte-equal to a genuine miss once the id this test varied is normalized away. THIS
        // EQUALITY IS THE PROPERTY, and it is why the refusal reply had to be the ordinary
        // not-found string: a distinct "ambiguous" or "shared node" reply would hand bob the
        // very existence oracle the suppression exists to shrink to one bit.
        var genuineMiss = Graph("bob").LinkMemories("no-such-entry-anywhere", "bob-note", relation: "elaborates");
        Assert.Equal(genuineMiss.Replace("no-such-entry-anywhere", "shared-id", StringComparison.Ordinal), result);

        // And nothing was written to the shared node.
        Assert.Empty(_graph.GetEdgesForEntry("shared-id", tenantId: ""));
    }
}
