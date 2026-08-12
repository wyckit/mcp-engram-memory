using System.ComponentModel;
using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Sharing;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IPrincipalContext _principal;

    [ActivatorUtilitiesConstructor]
    public MultiAgentTools(
        CognitiveIndex index,
        IEmbeddingService embedding,
        MetricsCollector metrics,
        NamespaceRegistry registry,
        IPrincipalContext principal)
    {
        _index = index;
        _embedding = embedding;
        _metrics = metrics;
        _registry = registry;
        _principal = principal;
    }

    public MultiAgentTools(
        CognitiveIndex index,
        IEmbeddingService embedding,
        MetricsCollector metrics,
        NamespaceRegistry registry,
        AgentIdentity agent)
        : this(index, embedding, metrics, registry,
            new PrincipalContext(string.Empty, agent.AgentId)) { }

    /// <summary>
    /// Convenience overload for in-process callers embedding these tools directly: takes the
    /// namespace list as a plain comma-separated string. Deliberately not attributed as an MCP
    /// tool — the tolerant overload below is the single wire surface.
    /// </summary>
    public object CrossSearch(
        string namespaces,
        string text,
        int k = 10,
        bool hybrid = false,
        bool rerank = false,
        string? includeStates = null,
        bool summaryFirst = false,
        float minScore = 0f,
        string? category = null,
        bool diversity = false,
        float diversityLambda = 0.5f)
        => CrossSearch(
            JsonSerializer.SerializeToElement(namespaces), text, null, k, hybrid, rerank,
            includeStates, summaryFirst, minScore, category, diversity, diversityLambda);

    [McpServerTool(Name = "cross_search", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Find memories across multiple specific namespaces in one call, merging results by relevance rank. Don't use it when you know which single namespace to search — use `recall` for that, which also adds graph expansion and archived-entry fallback.")]
    public object CrossSearch(
        [Description("Namespaces to search. Accepts a JSON array, a comma-separated string (e.g. 'work,synthesis,mcp-engram-memory'), or a single namespace. Alias: `ns`.")] JsonElement? namespaces,
        [Description("The text query to search for.")] string text,
        [Description("Alias for `namespaces` — supply either, not both.")] JsonElement? ns = null,
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
        // `namespaces` is plural because it is a list, while every other tool's `ns` is a
        // single namespace — so the names are both correct and inconsistent, and callers
        // reach for `ns` out of habit. Rather than rename (which would break every existing
        // caller of a published tool), accept either name and any reasonable shape.
        if (namespaces is not null && ns is not null)
            return "Error: supply either namespaces or ns, not both — they are the same parameter.";

        var nsInput = namespaces ?? ns;
        if (!StringListNormalizer.TryNormalize(nsInput, "namespaces", out var nsList, out var nsError))
            return $"Error: {nsError}";
        if (nsList is null || nsList.Length == 0)
            return "Error: namespaces must not be empty.";
        if (string.IsNullOrWhiteSpace(text))
            return "Error: text must not be empty.";

        using var timer = _metrics.StartTimer("cross_search");

        // Filter to namespaces the agent can access
        var accessible = nsList.Where(ns => _principal.IsSystem ||
            _registry.HasAccess(_principal.AgentId, ns, tenantId: _principal.TenantId)).ToList();
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
            diversity: diversity, diversityLambda: diversityLambda,
            tenantId: _principal.TenantId);

        return new CrossSearchResponse(results, accessible.Count, results.Count);
    }

    [McpServerTool(Name = "share_namespace", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Grant another agent read or write access to a namespace you own. Don't use it to check what's already shared; use `list_shared` for that.")]
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
        return _registry.Share(ns, _principal.AgentId, agentId, accessLevel, _principal.TenantId);
    }

    [McpServerTool(Name = "unshare_namespace", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Revoke another agent's access to a namespace you own. Don't use it to check current sharing state first; call `list_shared` to confirm what to revoke before calling this.")]
    public object UnshareNamespace(
        [Description("The namespace to unshare.")] string ns,
        [Description("The agent ID to revoke access from.")] string agentId)
    {
        if (string.IsNullOrWhiteSpace(ns))
            return "Error: namespace must not be empty.";
        if (string.IsNullOrWhiteSpace(agentId))
            return "Error: agentId must not be empty.";

        using var timer = _metrics.StartTimer("unshare_namespace");
        return _registry.Unshare(ns, _principal.AgentId, agentId, _principal.TenantId);
    }

    [McpServerTool(Name = "list_shared", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List every namespace other agents have shared with you, showing owner and access level. Don't use it to check your own namespaces or identity; use `whoami` for the full picture of what you own and can access.")]
    public object ListShared()
    {
        using var timer = _metrics.StartTimer("list_shared");
        var result = _registry.GetAccessibleNamespaces(_principal.AgentId, _principal.TenantId);
        return result.SharedNamespaces;
    }

    [McpServerTool(Name = "whoami", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Check this agent's ID and the full list of namespaces it owns or has access to. Don't use it only to see shared namespaces; use `list_shared` when you specifically want the inbound-sharing view with owner attribution.")]
    public object WhoAmI()
    {
        using var timer = _metrics.StartTimer("whoami");
        return _registry.GetAccessibleNamespaces(_principal.AgentId, _principal.TenantId);
    }
}
