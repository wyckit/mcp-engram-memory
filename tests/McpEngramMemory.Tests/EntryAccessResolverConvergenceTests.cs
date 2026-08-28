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

    // ── Identified principal: filter-before-match on the converged write path ──

    [Fact]
    public void LinkMemories_IdentifiedAgent_VisibleUniqueId_NotBlankedByInvisibleTwin()
    {
        // The GraphTools analogue of NamespaceAclEnforcementTests'
        // Reflect_LinksOwnEntryEvenWhenTheSameIdExistsInAPrivateNamespace, without preferredNs:
        // alice's invisible twin must contribute neither a match nor an ambiguity signal, so
        // bob's id stays unique among the namespaces his write predicate admits.
        Assert.Contains("Stored entry", Core("alice").StoreMemory(
            "shared-id", "alice-private", "alice's private postmortem"));
        Assert.Contains("Stored entry", Core("bob").StoreMemory(
            "shared-id", "bob-work", "bob's working copy"));
        Assert.Contains("Stored entry", Core("bob").StoreMemory(
            "bob-note", "bob-work", "bob's follow-up note"));
        Assert.False(_registry.HasAccess("bob", "alice-private", "write", tenantId: ""));

        var result = Graph("bob").LinkMemories("shared-id", "bob-note", relation: "elaborates");

        Assert.Contains("Linked", result);
        Assert.Contains(_graph.GetEdgesForEntry("shared-id", tenantId: ""),
            e => e.SourceId == "shared-id" && e.TargetId == "bob-note" && e.Relation == "elaborates");
    }
}
