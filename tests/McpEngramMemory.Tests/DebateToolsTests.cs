using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class DebateToolsTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly DebateSessionManager _sessions;
    private readonly DebateTools _tools;

    public DebateToolsTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"debate_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _sessions = new DebateSessionManager();
        var embedding = new HashEmbeddingService(dimensions: 4);
        _tools = new DebateTools(_index, _graph, embedding, _sessions, new MetricsCollector());
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
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
        Assert.True(_sessions.HasSession("debate-cold"));
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
        _sessions.RegisterNode("skip-test", "entry-a");

        var edges = new[]
        {
            new DebateEdge(1, 99, "elaborates", 0.5f), // Node 99 doesn't exist
        };

        var result = _tools.MapDebateGraph("skip-test", edges);
        var graphResult = Assert.IsType<MapDebateGraphResult>(result);

        Assert.Equal(0, graphResult.EdgesCreated);
        Assert.Contains(graphResult.EdgeDetails, d => d.Contains("not found"));
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
        _sessions.RegisterNode("resolve-bad", "entry-a");

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
        Assert.False(_sessions.HasSession("full-pipeline"));
    }

    [Fact]
    public void ResolveDebate_EmptyConsensus_ReturnsError()
    {
        _sessions.RegisterNode("empty-consensus", "entry-a");

        var result = _tools.ResolveDebate("empty-consensus", 1, "", "decisions");
        Assert.IsType<string>(result);
        Assert.Contains("Error", (string)result);
    }

    [Fact]
    public void ResolveDebate_EmptyTargetNamespace_ReturnsError()
    {
        _sessions.RegisterNode("empty-ns", "entry-a");

        var result = _tools.ResolveDebate("empty-ns", 1, "consensus text", "");
        Assert.IsType<string>(result);
        Assert.Contains("Error", (string)result);
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
        Assert.False(_sessions.HasSession("e2e-test"));
    }
}
