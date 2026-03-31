using System.ComponentModel;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Graph;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// V2 Panel of Experts composite MCP tools: consult, map, resolve.
/// Reduces 15+ atomic tool calls to 3 macro-commands.
/// </summary>
[McpServerToolType]
public sealed class DebateTools
{
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly IEmbeddingService _embedding;
    private readonly DebateSessionManager _sessions;
    private readonly MetricsCollector _metrics;

    public DebateTools(
        CognitiveIndex index,
        KnowledgeGraph graph,
        IEmbeddingService embedding,
        DebateSessionManager sessions,
        MetricsCollector metrics)
    {
        _index = index;
        _graph = graph;
        _embedding = embedding;
        _sessions = sessions;
        _metrics = metrics;
    }

    [McpServerTool(Name = "consult_expert_panel")]
    [Description("Search multiple expert namespaces in parallel and collect perspectives into a debate session. " +
        "Returns integer-aliased nodes for easy reference. Use for multi-expert deliberation on a problem.")]
    public object ConsultExpertPanel(
        [Description("The problem or question to present to the panel.")] string problemStatement,
        [Description("List of expert namespace names to consult (e.g. ['expert-arch', 'expert-sec']).")] string[] experts,
        [Description("Session identifier for this debate (e.g. 'debate-101'). Used to track node aliases.")] string sessionId,
        [Description("Max results per expert namespace (default: 3).")] int perExpertK = 3,
        [Description("Min similarity score threshold (default: 0.3).")] float minScore = 0.3f,
        [Description("Comma-separated lifecycle states to search (default: 'stm,ltm').")] string? includeStates = null)
    {
        using var timer = _metrics.StartTimer("consult_expert_panel");

        if (string.IsNullOrWhiteSpace(problemStatement))
            return "Error: problemStatement must not be empty.";
        if (experts is null || experts.Length == 0)
            return "Error: At least one expert namespace must be provided.";
        if (string.IsNullOrWhiteSpace(sessionId))
            return "Error: sessionId must not be empty.";

        if (!_sessions.TryCreateSession(sessionId))
            return $"Error: Session '{sessionId}' already exists. Use a new sessionId or call resolve_debate first.";

        var states = ParseStates(includeStates);

        // Embed the problem statement once
        float[] queryVector = _embedding.Embed(problemStatement);
        string debateNs = DebateSessionManager.GetDebateNamespace(sessionId);

        // Search each expert namespace sequentially — CognitiveIndex.Search uses
        // EnterUpgradeableReadLock which serializes concurrent callers anyway
        var perspectives = new List<ExpertPerspective>();
        int expertsWithContext = 0;

        foreach (var expertNs in experts)
        {
            IReadOnlyList<CognitiveSearchResult> results;
            try
            {
                results = _index.Search(queryVector, expertNs, perExpertK, minScore, includeStates: states);
            }
            catch (Exception ex)
            {
                return $"Error searching expert '{expertNs}': {ex.Message}";
            }

            bool hadContext = results.Count > 0;
            if (hadContext) expertsWithContext++;

            if (!hadContext)
            {
                // Cold-start: create a placeholder noting no prior context
                string coldStartId = $"debate-{sessionId}-{expertNs}-cold";
                string coldStartText = $"[{expertNs}] No historical context found for: {problemStatement}. Relying on pre-trained persona.";
                var coldVector = _embedding.Embed(coldStartText);
                var coldEntry = new CognitiveEntry(coldStartId, coldVector, debateNs, coldStartText,
                    category: "debate-perspective");
                _index.Upsert(coldEntry);

                int alias = _sessions.RegisterNode(sessionId, coldStartId);
                perspectives.Add(new ExpertPerspective(alias, expertNs, coldStartId, coldStartText, 0f, false));
                continue;
            }

            // Store each result as a debate node
            foreach (var result in results)
            {
                string debateEntryId = $"debate-{sessionId}-{expertNs}-{result.Id}";
                string perspectiveText = $"[{expertNs}] {result.Text ?? result.Id}";
                var perspectiveVector = _embedding.Embed(perspectiveText);
                var debateEntry = new CognitiveEntry(debateEntryId, perspectiveVector, debateNs,
                    perspectiveText, category: "debate-perspective",
                    metadata: new Dictionary<string, string>
                    {
                        ["sourceExpert"] = expertNs,
                        ["sourceEntryId"] = result.Id,
                        ["debateSessionId"] = sessionId
                    });
                _index.Upsert(debateEntry);

                int alias = _sessions.RegisterNode(sessionId, debateEntryId);
                perspectives.Add(new ExpertPerspective(alias, expertNs, debateEntryId, result.Text, result.Score, true));
            }
        }

        return new ConsultPanelResult(
            sessionId, problemStatement, perspectives, debateNs,
            experts.Length, expertsWithContext);
    }

    [McpServerTool(Name = "map_debate_graph")]
    [Description("Create knowledge graph edges between debate nodes using integer aliases. " +
        "Call after consult_expert_panel to map agreements, contradictions, and dependencies between perspectives.")]
    public object MapDebateGraph(
        [Description("The debate session ID from consult_expert_panel.")] string sessionId,
        [Description("List of edges to create. Each edge has: sourceNode (int), targetNode (int), relation (string), weight (float 0-1).")] DebateEdge[] edges)
    {
        using var timer = _metrics.StartTimer("map_debate_graph");

        if (string.IsNullOrWhiteSpace(sessionId))
            return "Error: sessionId must not be empty.";
        if (edges is null || edges.Length == 0)
            return "Error: At least one edge must be provided.";
        if (!_sessions.HasSession(sessionId))
            return $"Error: Session '{sessionId}' not found. Call consult_expert_panel first.";

        var edgeDetails = new List<string>();
        var graphEdges = new List<GraphEdge>();

        foreach (var debateEdge in edges)
        {
            var sourceId = _sessions.ResolveAlias(sessionId, debateEdge.SourceNode);
            var targetId = _sessions.ResolveAlias(sessionId, debateEdge.TargetNode);

            if (sourceId is null)
            {
                edgeDetails.Add($"Skipped: Node {debateEdge.SourceNode} not found in session.");
                continue;
            }
            if (targetId is null)
            {
                edgeDetails.Add($"Skipped: Node {debateEdge.TargetNode} not found in session.");
                continue;
            }

            graphEdges.Add(new GraphEdge(sourceId, targetId, debateEdge.Relation, debateEdge.Weight,
                new Dictionary<string, string>
                {
                    ["debateSessionId"] = sessionId,
                    ["sourceAlias"] = debateEdge.SourceNode.ToString(),
                    ["targetAlias"] = debateEdge.TargetNode.ToString()
                }));
            edgeDetails.Add($"[Node {debateEdge.SourceNode}] --({debateEdge.Relation}, w={debateEdge.Weight})--> [Node {debateEdge.TargetNode}]");
        }

        // Batch-add all edges in a single lock acquisition
        int created = graphEdges.Count > 0 ? _graph.AddEdges(graphEdges) : 0;

        return new MapDebateGraphResult(sessionId, created, edgeDetails);
    }

    [McpServerTool(Name = "resolve_debate")]
    [Description("Finalize a debate: store consensus as LTM, link to winning perspective, archive debate nodes, and clean up session.")]
    public object ResolveDebate(
        [Description("The debate session ID.")] string sessionId,
        [Description("Integer alias of the winning/primary perspective node.")] int winningNode,
        [Description("LLM-generated consensus summary text.")] string consensusSummary,
        [Description("Target namespace for the consensus entry (e.g. 'decisions', 'architecture').")] string targetNamespace,
        [Description("Optional category for the consensus entry.")] string? category = null)
    {
        using var timer = _metrics.StartTimer("resolve_debate");

        if (string.IsNullOrWhiteSpace(sessionId))
            return "Error: sessionId must not be empty.";
        if (string.IsNullOrWhiteSpace(consensusSummary))
            return "Error: consensusSummary must not be empty.";
        if (string.IsNullOrWhiteSpace(targetNamespace))
            return "Error: targetNamespace must not be empty.";

        // Resolve winning node atomically (also validates session exists)
        var winningEntryId = _sessions.ResolveAlias(sessionId, winningNode);
        if (winningEntryId is null)
        {
            return _sessions.HasSession(sessionId)
                ? $"Error: Winning node {winningNode} not found in session '{sessionId}'."
                : $"Error: Session '{sessionId}' not found.";
        }

        // 1. Store the consensus as a new LTM entry in the target namespace
        string consensusId = $"consensus-{sessionId}";
        var consensusVector = _embedding.Embed(consensusSummary);
        var consensusEntry = new CognitiveEntry(consensusId, consensusVector, targetNamespace,
            consensusSummary, category: category,
            metadata: new Dictionary<string, string>
            {
                ["debateSessionId"] = sessionId,
                ["winningNode"] = winningEntryId,
                ["resolvedAt"] = DateTimeOffset.UtcNow.ToString("O")
            },
            lifecycleState: "ltm");
        _index.Upsert(consensusEntry);

        // 2. Link winning node to consensus (parent_child)
        var parentEdge = new GraphEdge(winningEntryId, consensusId, "parent_child", 1.0f,
            new Dictionary<string, string> { ["debateSessionId"] = sessionId });
        _graph.AddEdge(parentEdge);

        // 3. Archive all debate nodes in a single lock acquisition
        var allDebateEntryIds = _sessions.GetAllEntryIds(sessionId);
        int archivedCount = _index.SetLifecycleStateBatch(allDebateEntryIds, "archived");

        // 4. Clean up session state
        _sessions.RemoveSession(sessionId);

        return new ResolveDebateResult(
            sessionId, consensusId, targetNamespace, winningEntryId,
            archivedCount, consensusSummary);
    }

    private static HashSet<string> ParseStates(string? includeStates)
    {
        return includeStates is not null
            ? new HashSet<string>(includeStates.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            : new HashSet<string> { "stm", "ltm" };
    }
}
