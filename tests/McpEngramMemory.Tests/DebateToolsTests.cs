using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class DebateToolsTests : IDisposable
{
    /// <summary>The legacy, pre-tenant partition. Not a sentinel — "" is a real partition.</summary>
    private const string LegacyTenant = "";

    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    /// <summary>
    /// Deliberately the SAME agent id on both sides of the tenant boundary. The agent-level ACL
    /// must not be what denies a cross-tenant hijack, or the cross-tenant tests below would pass
    /// with tenant keying removed entirely and prove nothing. The agent-level control is a
    /// separate, still-required guarantee — see
    /// <see cref="MapDebateGraph_OtherAgentCannotDiscoverOrMutateOwnedSession"/>.
    /// </summary>
    private const string SharedAgentId = "analyst";

    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly DebateSessionManager _sessions;
    private readonly HashEmbeddingService _embedding;
    private readonly NamespaceRegistry _registry;
    private readonly DebateTools _tools;

    public DebateToolsTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"debate_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _sessions = new DebateSessionManager();
        _embedding = new HashEmbeddingService(dimensions: 4);
        _registry = new NamespaceRegistry(_index, _embedding);
        _tools = CreateTools(AgentIdentity.DefaultAgentId);
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    private DebateTools CreateTools(string agentId)
    {
        var access = new NamespaceAccess(_registry, new AgentIdentity(agentId));
        return new DebateTools(_index, _graph, _embedding, _sessions, new MetricsCollector(), access);
    }

    /// <summary>Tools bound to an identified principal inside a specific tenant partition.</summary>
    private DebateTools CreateTools(string agentId, string tenantId)
    {
        var access = new NamespaceAccess(_registry, new PrincipalContext(tenantId, agentId));
        return new DebateTools(_index, _graph, _embedding, _sessions, new MetricsCollector(), access);
    }

    // ── consult_expert_panel ──

    [Fact]
    public void ConsultExpertPanel_EmptyProblem_ReturnsError()
    {
        var result = _tools.ConsultExpertPanel("", ["expert-a"], "session-1");
        Assert.IsType<string>(result);
        Assert.Contains("Error", (string)result);
    }

    [Fact]
    public void ConsultExpertPanel_NoExperts_ReturnsError()
    {
        var result = _tools.ConsultExpertPanel("problem", Array.Empty<string>(), "session-1");
        Assert.IsType<string>(result);
        Assert.Contains("Error", (string)result);
    }

    [Fact]
    public void ConsultExpertPanel_EmptySessionId_ReturnsError()
    {
        var result = _tools.ConsultExpertPanel("problem", ["expert-a"], "");
        Assert.IsType<string>(result);
        Assert.Contains("Error", (string)result);
    }

    [Fact]
    public void ConsultExpertPanel_ColdStart_CreatesPlaceholderNodes()
    {
        // No prior data in expert namespaces - should create cold-start nodes
        var result = _tools.ConsultExpertPanel(
            "Should we use GraphQL?",
            ["expert-arch", "expert-sec"],
            "debate-cold");

        var panel = Assert.IsType<ConsultPanelResult>(result);
        Assert.Equal("debate-cold", panel.SessionId);
        Assert.Equal("Should we use GraphQL?", panel.ProblemStatement);
        Assert.Equal(2, panel.TotalExperts);
        Assert.Equal(0, panel.ExpertsWithContext);
        Assert.Equal(2, panel.Perspectives.Count);

        // Verify cold-start nodes
        foreach (var perspective in panel.Perspectives)
        {
            Assert.False(perspective.HadPriorContext);
            Assert.Equal(0f, perspective.Score);
            Assert.Contains("No historical context", perspective.Text);
        }

        // Verify session state created
        Assert.True(_sessions.HasSession(LegacyTenant, "debate-cold"));
    }

    [Fact]
    public void ConsultExpertPanel_WithExistingData_RetrievesAndStores()
    {
        // Seed with the same vector the tool will use to query (embed the problem statement)
        var v1 = new HashEmbeddingService(dimensions: 4).Embed("Microservices vs monolith?");
        _index.Upsert(new CognitiveEntry("arch-1", v1, "expert-arch",
            "Microservices provide better scalability", category: "architecture"));

        var result = _tools.ConsultExpertPanel(
            "Microservices vs monolith?",
            ["expert-arch"],
            "debate-with-data",
            minScore: 0f);

        var panel = Assert.IsType<ConsultPanelResult>(result);
        Assert.Equal(1, panel.TotalExperts);
        Assert.Equal(1, panel.ExpertsWithContext);

        // At least one perspective should have prior context
        Assert.Contains(panel.Perspectives, p => p.HadPriorContext);

        // Verify entries stored in debate namespace
        var debateNs = DebateSessionManager.GetDebateNamespace("debate-with-data");
        Assert.Equal(debateNs, panel.DebateNamespace);
    }

    [Fact]
    public void ConsultExpertPanel_DuplicateSessionId_ReturnsError()
    {
        _tools.ConsultExpertPanel("problem 1", ["expert-a"], "dup-session");

        var result = _tools.ConsultExpertPanel("problem 2", ["expert-b"], "dup-session");

        Assert.IsType<string>(result);
        Assert.Contains("already exists", (string)result);
    }

    [Fact]
    public void ConsultExpertPanel_AssignsSequentialAliases()
    {
        // Seed two expert namespaces
        _index.Upsert(new CognitiveEntry("a1", [0.9f, 0.1f, 0f, 0f], "expert-a", "Point A"));
        _index.Upsert(new CognitiveEntry("b1", [0.1f, 0.9f, 0f, 0f], "expert-b", "Point B"));

        var result = _tools.ConsultExpertPanel(
            "Compare approaches",
            ["expert-a", "expert-b"],
            "alias-test",
            minScore: 0f);

        var panel = Assert.IsType<ConsultPanelResult>(result);
        var aliases = panel.Perspectives.Select(p => p.NodeAlias).ToList();

        // Aliases should be sequential starting from 1
        Assert.Contains(1, aliases);
        Assert.Contains(2, aliases);
    }

    // ── map_debate_graph ──

    [Fact]
    public void MapDebateGraph_NoSession_ReturnsError()
    {
        var edges = new[] { new DebateEdge(1, 2, "contradicts", 0.9f) };
        var result = _tools.MapDebateGraph("nonexistent", edges);

        Assert.IsType<string>(result);
        Assert.Contains("not found", (string)result);
    }

    [Fact]
    public void MapDebateGraph_EmptyEdges_ReturnsError()
    {
        var result = _tools.MapDebateGraph("session-1", Array.Empty<DebateEdge>());

        Assert.IsType<string>(result);
        Assert.Contains("Error", (string)result);
    }

    [Fact]
    public void MapDebateGraph_ValidEdges_CreatesGraphEdges()
    {
        // Set up a session with nodes
        _index.Upsert(new CognitiveEntry("a1", [0.9f, 0.1f, 0f, 0f], "expert-a", "Point A"));
        _index.Upsert(new CognitiveEntry("b1", [0.1f, 0.9f, 0f, 0f], "expert-b", "Point B"));

        var panelResult = _tools.ConsultExpertPanel(
            "Compare approaches",
            ["expert-a", "expert-b"],
            "graph-test",
            minScore: 0f);

        var panel = Assert.IsType<ConsultPanelResult>(panelResult);
        Assert.True(panel.Perspectives.Count >= 2);

        int node1 = panel.Perspectives[0].NodeAlias;
        int node2 = panel.Perspectives[1].NodeAlias;

        // Map edges
        var edges = new[]
        {
            new DebateEdge(node1, node2, "contradicts", 0.9f),
        };

        var result = _tools.MapDebateGraph("graph-test", edges);
        var graphResult = Assert.IsType<MapDebateGraphResult>(result);

        Assert.Equal("graph-test", graphResult.SessionId);
        Assert.Equal(1, graphResult.EdgesCreated);
        Assert.Single(graphResult.EdgeDetails);
        Assert.Contains("contradicts", graphResult.EdgeDetails[0]);
    }

    [Fact]
    public void MapDebateGraph_InvalidAlias_SkipsWithMessage()
    {
        // Set up session with one node
        _sessions.RegisterNode(LegacyTenant, "skip-test", "entry-a");

        var edges = new[]
        {
            new DebateEdge(1, 99, "elaborates", 0.5f), // Node 99 doesn't exist
        };

        var result = _tools.MapDebateGraph("skip-test", edges);
        var graphResult = Assert.IsType<MapDebateGraphResult>(result);

        Assert.Equal(0, graphResult.EdgesCreated);
        Assert.Contains(graphResult.EdgeDetails, d => d.Contains("not found"));
    }

    [Fact]
    public void MapDebateGraph_OtherAgentCannotDiscoverOrMutateOwnedSession()
    {
        var alice = CreateTools("alice");
        var bob = CreateTools("bob");
        var panel = Assert.IsType<ConsultPanelResult>(alice.ConsultExpertPanel(
            "Compare approaches", ["expert-a", "expert-b"], "alice-map-session"));
        var debateNs = DebateSessionManager.GetDebateNamespace("alice-map-session");

        Assert.False(_registry.HasAccess("bob", debateNs, "write"));
        int before = _graph.EdgeCount;
        var denied = bob.MapDebateGraph("alice-map-session",
            [new DebateEdge(panel.Perspectives[0].NodeAlias, panel.Perspectives[1].NodeAlias, "contradicts", 0.9f)]);

        var message = Assert.IsType<string>(denied);
        Assert.Contains("not found", message);
        Assert.Equal(before, _graph.EdgeCount);
        Assert.True(_sessions.HasSession(LegacyTenant, "alice-map-session"));

        var allowed = alice.MapDebateGraph("alice-map-session",
            [new DebateEdge(panel.Perspectives[0].NodeAlias, panel.Perspectives[1].NodeAlias, "contradicts", 0.9f)]);
        Assert.Equal(1, Assert.IsType<MapDebateGraphResult>(allowed).EdgesCreated);
    }

    // ── resolve_debate ──

    [Fact]
    public void ResolveDebate_NoSession_ReturnsError()
    {
        var result = _tools.ResolveDebate("nonexistent", 1, "consensus", "decisions");
        Assert.IsType<string>(result);
        Assert.Contains("not found", (string)result);
    }

    [Fact]
    public void ResolveDebate_InvalidWinningNode_ReturnsError()
    {
        _sessions.RegisterNode(LegacyTenant, "resolve-bad", "entry-a");

        var result = _tools.ResolveDebate("resolve-bad", 99, "consensus", "decisions");
        Assert.IsType<string>(result);
        Assert.Contains("not found", (string)result);
    }

    [Fact]
    public void ResolveDebate_ValidSession_StoresConsensusAndArchives()
    {
        // Full pipeline: consult -> resolve
        _index.Upsert(new CognitiveEntry("a1", [0.9f, 0.1f, 0f, 0f], "expert-a", "Point A"));

        var panelResult = _tools.ConsultExpertPanel(
            "Test problem",
            ["expert-a"],
            "full-pipeline",
            minScore: 0f);
        var panel = Assert.IsType<ConsultPanelResult>(panelResult);

        int winningAlias = panel.Perspectives[0].NodeAlias;

        // Resolve
        var resolveResult = _tools.ResolveDebate(
            "full-pipeline", winningAlias,
            "We decided to go with approach A.",
            "decisions", category: "architecture");

        var resolved = Assert.IsType<ResolveDebateResult>(resolveResult);
        Assert.Equal("full-pipeline", resolved.SessionId);
        Assert.Equal("consensus-full-pipeline", resolved.ConsensusEntryId);
        Assert.Equal("decisions", resolved.ConsensusNamespace);
        Assert.Equal("We decided to go with approach A.", resolved.Summary);
        Assert.True(resolved.ArchivedCount >= 1);

        // Verify consensus entry stored as LTM
        var consensus = _index.Get("consensus-full-pipeline", "decisions");
        Assert.NotNull(consensus);
        Assert.Equal("ltm", consensus.LifecycleState);
        Assert.Equal("We decided to go with approach A.", consensus.Text);

        // Verify session cleaned up
        Assert.False(_sessions.HasSession(LegacyTenant, "full-pipeline"));
    }

    [Fact]
    public void ResolveDebate_EmptyConsensus_ReturnsError()
    {
        _sessions.RegisterNode(LegacyTenant, "empty-consensus", "entry-a");

        var result = _tools.ResolveDebate("empty-consensus", 1, "", "decisions");
        Assert.IsType<string>(result);
        Assert.Contains("Error", (string)result);
    }

    [Fact]
    public void ResolveDebate_EmptyTargetNamespace_ReturnsError()
    {
        _sessions.RegisterNode(LegacyTenant, "empty-ns", "entry-a");

        var result = _tools.ResolveDebate("empty-ns", 1, "consensus text", "");
        Assert.IsType<string>(result);
        Assert.Contains("Error", (string)result);
    }

    [Fact]
    public void ResolveDebate_OtherAgentCannotDiscoverResolveOrRemoveOwnedSession()
    {
        var alice = CreateTools("alice");
        var bob = CreateTools("bob");
        var panel = Assert.IsType<ConsultPanelResult>(alice.ConsultExpertPanel(
            "Choose an approach", ["expert-a"], "alice-resolve-session"));
        int winningNode = panel.Perspectives[0].NodeAlias;

        var denied = bob.ResolveDebate(
            "alice-resolve-session", winningNode, "Bob's consensus", "bob-decisions");

        var message = Assert.IsType<string>(denied);
        Assert.Contains("not found", message);
        Assert.Null(_index.Get("consensus-alice-resolve-session", "bob-decisions"));
        Assert.True(_sessions.HasSession(LegacyTenant, "alice-resolve-session"));
        Assert.All(_sessions.GetAllEntryIds(LegacyTenant, "alice-resolve-session"), id =>
            Assert.NotEqual("archived", _index.Get(id, panel.DebateNamespace)?.LifecycleState));

        var allowed = alice.ResolveDebate(
            "alice-resolve-session", winningNode, "Alice's consensus", "alice-decisions");
        Assert.IsType<ResolveDebateResult>(allowed);
        Assert.False(_sessions.HasSession(LegacyTenant, "alice-resolve-session"));
    }

    // ── Cross-tenant session isolation ──
    //
    // Debate session state is keyed by (tenant, sessionId). The sessionId is caller-supplied, so
    // when the alias table was keyed by sessionId alone two tenants that picked the same id shared
    // one table: either could read the other's entry ids and destroy its session. Every test in
    // this section drives two identified principals that differ ONLY in tenant, so the tenant
    // boundary — not the agent ACL — is the sole control under test.

    [Fact]
    public void ResolveDebate_TenantCannotHijackAnotherTenantsSession()
    {
        const string sessionId = "cross-tenant-resolve";
        var tenantA = CreateTools(SharedAgentId, TenantA);
        var tenantB = CreateTools(SharedAgentId, TenantB);

        // Baseline: exactly what tenant B is told about this session id while it exists nowhere.
        var beforeAnyoneHeldIt = Assert.IsType<string>(tenantB.ResolveDebate(
            sessionId, 1, "Tenant B's consensus", "tenant-b-decisions"));

        var panel = Assert.IsType<ConsultPanelResult>(tenantA.ConsultExpertPanel(
            "Should we ship on Friday?", ["expert-a"], sessionId));
        int winningNode = panel.Perspectives[0].NodeAlias;
        var tenantAEntryIds = _sessions.GetAllEntryIds(TenantA, sessionId);
        Assert.NotEmpty(tenantAEntryIds);

        // The exploit. Pre-fix this resolved A's winning node through A's alias table, wrote a
        // consensus naming A's private entry id, archived A's debate nodes and dropped A's session.
        var hijack = Assert.IsType<string>(tenantB.ResolveDebate(
            sessionId, winningNode, "Tenant B's consensus", "tenant-b-decisions"));

        // Not-found and not-yours must be the SAME reply. Asserting only "denied" would still pass
        // against an implementation that answered "not yours" here and "not found" above, which
        // turns resolve_debate into an existence oracle for other tenants' session ids.
        Assert.Equal(beforeAnyoneHeldIt, hijack);

        // ...and the destructive half: nothing of A's moved.
        Assert.True(_sessions.HasSession(TenantA, sessionId));
        Assert.Equal(tenantAEntryIds, _sessions.GetAllEntryIds(TenantA, sessionId));
        Assert.Null(_index.Get($"consensus-{sessionId}", "tenant-b-decisions", TenantB));
        Assert.All(tenantAEntryIds, id =>
            Assert.NotEqual("archived", _index.Get(id, panel.DebateNamespace, TenantA)?.LifecycleState));

        // Over-correction control: the owning tenant still resolves its own session.
        var owner = Assert.IsType<ResolveDebateResult>(tenantA.ResolveDebate(
            sessionId, winningNode, "Tenant A's consensus", "tenant-a-decisions"));
        Assert.Equal($"consensus-{sessionId}", owner.ConsensusEntryId);
        Assert.Equal(tenantAEntryIds[winningNode - 1], owner.WinningNodeId);
        Assert.False(_sessions.HasSession(TenantA, sessionId));
    }

    [Fact]
    public void MapDebateGraph_TenantCannotDiscoverAnotherTenantsSession()
    {
        const string sessionId = "cross-tenant-map";
        var tenantA = CreateTools(SharedAgentId, TenantA);
        var tenantB = CreateTools(SharedAgentId, TenantB);

        // Baseline: tenant B probing an id that exists nowhere.
        var beforeAnyoneHeldIt = Assert.IsType<string>(tenantB.MapDebateGraph(
            sessionId, [new DebateEdge(1, 2, "contradicts", 0.9f)]));

        var panel = Assert.IsType<ConsultPanelResult>(tenantA.ConsultExpertPanel(
            "Adopt gRPC or REST?", ["expert-a", "expert-b"], sessionId));
        Assert.True(panel.Perspectives.Count >= 2);
        var tenantAEntryIds = _sessions.GetAllEntryIds(TenantA, sessionId);
        int edgesBefore = _graph.EdgeCount;

        var probe = Assert.IsType<string>(tenantB.MapDebateGraph(sessionId,
            [new DebateEdge(panel.Perspectives[0].NodeAlias, panel.Perspectives[1].NodeAlias,
                "contradicts", 0.9f)]));

        // Confidentiality: B's reply cannot distinguish "tenant A holds this session" from
        // "nobody holds it". A per-edge "node N not found" reply would already be a leak.
        Assert.Equal(beforeAnyoneHeldIt, probe);

        // Pre-fix the aliases resolved through A's table and A's entry ids were persisted into B's
        // own tenant as graph edges — a durable record of ids B was never allowed to learn.
        Assert.Equal(edgesBefore, _graph.EdgeCount);
        Assert.DoesNotContain(_graph.GetAllEdges(TenantB),
            e => tenantAEntryIds.Contains(e.SourceId) || tenantAEntryIds.Contains(e.TargetId));

        // Over-correction control: the owning tenant still maps its own session.
        var owned = Assert.IsType<MapDebateGraphResult>(tenantA.MapDebateGraph(sessionId,
            [new DebateEdge(panel.Perspectives[0].NodeAlias, panel.Perspectives[1].NodeAlias,
                "contradicts", 0.9f)]));
        Assert.Equal(1, owned.EdgesCreated);
    }

    [Fact]
    public void ConsultExpertPanel_SameSessionIdInTwoTenants_BothSucceed()
    {
        const string sessionId = "same-id-two-tenants";
        var tenantA = CreateTools(SharedAgentId, TenantA);
        var tenantB = CreateTools(SharedAgentId, TenantB);

        var panelA = Assert.IsType<ConsultPanelResult>(tenantA.ConsultExpertPanel(
            "Should we adopt GraphQL?", ["expert-a"], sessionId));

        // The over-correction control. Pre-fix, one shared alias table meant the second tenant was
        // refused with "Session already exists" — a denial driven entirely by another tenant's
        // choice of id, and itself an existence oracle. Tenant keying must not be implemented by
        // refusing any id already in use somewhere.
        var panelB = Assert.IsType<ConsultPanelResult>(tenantB.ConsultExpertPanel(
            "Should we adopt gRPC?", ["expert-a"], sessionId));

        Assert.Equal(sessionId, panelA.SessionId);
        Assert.Equal(sessionId, panelB.SessionId);
        Assert.True(_sessions.HasSession(TenantA, sessionId));
        Assert.True(_sessions.HasSession(TenantB, sessionId));

        // Aliases are per-session, so each tenant numbers its own nodes from 1.
        Assert.Equal(1, panelA.Perspectives[0].NodeAlias);
        Assert.Equal(1, panelB.Perspectives[0].NodeAlias);

        // An entry's identity is (tenant, namespace, id): the cold-start id and the debate
        // namespace collide exactly, and each tenant still reads back its own content.
        string collidingId = panelA.Perspectives[0].EntryId;
        Assert.Equal(collidingId, panelB.Perspectives[0].EntryId);
        Assert.Equal(panelA.DebateNamespace, panelB.DebateNamespace);

        var storedForA = _index.Get(collidingId, panelA.DebateNamespace, TenantA);
        var storedForB = _index.Get(collidingId, panelB.DebateNamespace, TenantB);
        Assert.NotNull(storedForA);
        Assert.NotNull(storedForB);
        Assert.Contains("GraphQL", storedForA.Text ?? "");
        Assert.Contains("gRPC", storedForB.Text ?? "");
        Assert.DoesNotContain("gRPC", storedForA.Text ?? "");

        // Duplicate detection is still enforced WITHIN a tenant.
        var duplicate = tenantA.ConsultExpertPanel("A third question?", ["expert-a"], sessionId);
        Assert.Contains("already exists", Assert.IsType<string>(duplicate));
    }

    // ── Full Pipeline Integration ──

    [Fact]
    public void FullPipeline_ConsultMapResolve_WorksEndToEnd()
    {
        // Seed expert data
        _index.Upsert(new CognitiveEntry("arch-1", [0.9f, 0.1f, 0f, 0f], "expert-arch",
            "GraphQL enables flexible data fetching"));
        _index.Upsert(new CognitiveEntry("sec-1", [0.1f, 0.9f, 0f, 0f], "expert-sec",
            "GraphQL is vulnerable to deep nesting attacks"));

        // Step 1: Consult
        var consultResult = _tools.ConsultExpertPanel(
            "Should we adopt GraphQL?",
            ["expert-arch", "expert-sec"],
            "e2e-test",
            minScore: 0f);
        var panel = Assert.IsType<ConsultPanelResult>(consultResult);
        Assert.Equal(2, panel.TotalExperts);
        Assert.True(panel.Perspectives.Count >= 2);

        // Identify node aliases by expert namespace
        var archNode = panel.Perspectives.First(p => p.ExpertNamespace == "expert-arch");
        var secNode = panel.Perspectives.First(p => p.ExpertNamespace == "expert-sec");

        // Step 2: Map relationships
        var mapResult = _tools.MapDebateGraph("e2e-test", new[]
        {
            new DebateEdge(secNode.NodeAlias, archNode.NodeAlias, "contradicts", 0.9f),
        });
        var mapped = Assert.IsType<MapDebateGraphResult>(mapResult);
        Assert.Equal(1, mapped.EdgesCreated);

        // Step 3: Resolve
        var resolveResult = _tools.ResolveDebate(
            "e2e-test", archNode.NodeAlias,
            "Adopt GraphQL with strict query depth limiting.",
            "decisions");
        var resolved = Assert.IsType<ResolveDebateResult>(resolveResult);
        Assert.Equal("decisions", resolved.ConsensusNamespace);

        // Verify final state
        var consensus = _index.Get("consensus-e2e-test", "decisions");
        Assert.NotNull(consensus);
        Assert.Equal("ltm", consensus.LifecycleState);
        Assert.False(_sessions.HasSession(LegacyTenant, "e2e-test"));
    }
}
