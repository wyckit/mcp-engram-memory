using System.ComponentModel;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Sharing;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for multi-agent memory sharing.
/// cross_search searches across multiple namespaces.
/// share_namespace / unshare_namespace manage access permissions.
/// list_shared shows accessible namespaces.
/// whoami returns current agent identity.
/// </summary>
[McpServerToolType]
public sealed class MultiAgentTools
{
    private readonly CognitiveIndex _index;
    private readonly IEmbeddingService _embedding;
    private readonly MetricsCollector _metrics;
    private readonly NamespaceRegistry _registry;
    private readonly AgentIdentity _agent;

    public MultiAgentTools(
        CognitiveIndex index,
        IEmbeddingService embedding,
        MetricsCollector metrics,
        NamespaceRegistry registry,
        AgentIdentity agent)
    {
        _index = index;
        _embedding = embedding;
        _metrics = metrics;
        _registry = registry;
        _agent = agent;
    }

    [McpServerTool(Name = "cross_search")]
    [Description("Search multiple namespaces in one call with RRF-merged results. " +
        "Use when information may span multiple knowledge domains. Supports hybrid search, reranking, " +
        "cluster-aware MMR diversity, min-score filtering, and category filtering. " +
        "Note: expand_graph, expand_query, use_physics, and temperature are single-namespace features " +
        "of search_memory and are not yet supported by cross_search.")]
    public object CrossSearch(
        [Description("Comma-separated list of namespaces to search (e.g. 'work,synthesis,mcp-engram-memory').")] string namespaces,
        [Description("The text query to search for.")] string text,
        [Description("Maximum number of results to return across all namespaces (default: 10).")] int k = 10,
        [Description("When true, use hybrid BM25+vector search (default: false).")] bool hybrid = false,
        [Description("When true, apply token-level reranking (default: false).")] bool rerank = false,
        [Description("Comma-separated lifecycle states to include (default: 'stm,ltm').")] string? includeStates = null,
        [Description("Prioritize cluster summaries over individual members (default: false).")] bool summaryFirst = false,
        [Description("Minimum cosine-similarity score threshold per namespace (default: 0).")] float minScore = 0f,
        [Description("Filter by category within each namespace (default: null).")] string? category = null,
        [Description("When true, apply cluster-aware MMR diversity reranking per namespace before RRF merge (default: false).")] bool diversity = false,
        [Description("Diversity trade-off [0.0-1.0]. 1.0=pure relevance, 0.0=pure diversity (default: 0.5).")] float diversityLambda = 0.5f)
    {
        if (string.IsNullOrWhiteSpace(namespaces))
            return "Error: namespaces must not be empty.";
        if (string.IsNullOrWhiteSpace(text))
            return "Error: text must not be empty.";

        using var timer = _metrics.StartTimer("cross_search");

        var nsList = namespaces.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // Filter to namespaces the agent can access
        var accessible = nsList.Where(ns => _registry.HasAccess(_agent.AgentId, ns)).ToList();
        if (accessible.Count == 0)
            return "Error: no accessible namespaces in the provided list.";

        var states = includeStates is not null
            ? new HashSet<string>(includeStates.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            : new HashSet<string> { "stm", "ltm" };

        var vector = _embedding.Embed(text);
        var results = _index.SearchMultiple(
            vector, accessible, queryText: text, k: k,
            minScore: minScore, category: category,
            includeStates: states, hybrid: hybrid, rerank: rerank,
            summaryFirst: summaryFirst,
            diversity: diversity, diversityLambda: diversityLambda);

        return new CrossSearchResponse(results, accessible.Count, results.Count);
    }

    [McpServerTool(Name = "share_namespace")]
    [Description("Grant another agent read or write access to a namespace you own.")]
    public object ShareNamespace(
        [Description("The namespace to share.")] string ns,
        [Description("The agent ID to grant access to.")] string agentId,
        [Description("Access level: 'read' (search only) or 'write' (search + store). Default: 'read'.")] string accessLevel = "read")
    {
        if (string.IsNullOrWhiteSpace(ns))
            return "Error: namespace must not be empty.";
        if (string.IsNullOrWhiteSpace(agentId))
            return "Error: agentId must not be empty.";

        using var timer = _metrics.StartTimer("share_namespace");
        return _registry.Share(ns, _agent.AgentId, agentId, accessLevel);
    }

    [McpServerTool(Name = "unshare_namespace")]
    [Description("Revoke an agent's access to a namespace you own.")]
    public object UnshareNamespace(
        [Description("The namespace to unshare.")] string ns,
        [Description("The agent ID to revoke access from.")] string agentId)
    {
        if (string.IsNullOrWhiteSpace(ns))
            return "Error: namespace must not be empty.";
        if (string.IsNullOrWhiteSpace(agentId))
            return "Error: agentId must not be empty.";

        using var timer = _metrics.StartTimer("unshare_namespace");
        return _registry.Unshare(ns, _agent.AgentId, agentId);
    }

    [McpServerTool(Name = "list_shared")]
    [Description("List all namespaces that OTHER agents have shared with the current agent, showing owner and access level for each.")]
    public object ListShared()
    {
        using var timer = _metrics.StartTimer("list_shared");
        var result = _registry.GetAccessibleNamespaces(_agent.AgentId);
        return result.SharedNamespaces;
    }

    [McpServerTool(Name = "whoami")]
    [Description("Return current agent identity and accessible namespaces (owned + shared with this agent). Use to verify multi-agent configuration.")]
    public object WhoAmI()
    {
        using var timer = _metrics.StartTimer("whoami");
        return _registry.GetAccessibleNamespaces(_agent.AgentId);
    }
}
