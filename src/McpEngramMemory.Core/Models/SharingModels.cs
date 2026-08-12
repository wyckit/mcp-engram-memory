using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// Host-supplied identity and tenant boundary for a cognitive operation.
/// Implementations are provided by the embedding host; MCP arguments must not be
/// allowed to choose or override these values.
/// </summary>
public interface IPrincipalContext
{
    string TenantId { get; }
    string AgentId { get; }
    bool IsSystem { get; }
    bool IsLegacyUnisolated { get; }
}

/// <summary>
/// Immutable principal context. The empty-tenant/default-agent combination is the
/// explicit compatibility mode used by historical single-user deployments; it is
/// intentionally unisolated and must never be described as authenticated.
/// </summary>
public sealed record PrincipalContext(
    string TenantId,
    string AgentId,
    bool IsSystem = false) : IPrincipalContext
{
    public static PrincipalContext LegacyUnisolated { get; } =
        new(string.Empty, AgentIdentity.DefaultAgentId);

    public bool IsLegacyUnisolated =>
        !IsSystem && string.IsNullOrEmpty(TenantId) && AgentId == AgentIdentity.DefaultAgentId;
}

/// <summary>
/// Agent identity for multi-agent memory sharing.
/// </summary>
public sealed record AgentIdentity(
    [property: JsonPropertyName("agentId")] string AgentId)
{
    /// <summary>Default agent identity when AGENT_ID is not set.</summary>
    public const string DefaultAgentId = "default";

    public static AgentIdentity Default { get; } = new(DefaultAgentId);

    public bool IsDefault => AgentId == DefaultAgentId;
}

/// <summary>
/// Namespace access permission entry.
/// </summary>
public sealed record NamespacePermission(
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("sharedWith")] IReadOnlyList<ShareGrant> SharedWith);

/// <summary>
/// A share grant giving an agent access to a namespace.
/// </summary>
public sealed record ShareGrant(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("accessLevel")] string AccessLevel);

/// <summary>
/// Result of a cross-namespace search showing results with their source namespace.
/// </summary>
public sealed record CrossSearchResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("score")] float Score,
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("lifecycleState")] string LifecycleState,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("metadata")] Dictionary<string, string>? Metadata,
    [property: JsonPropertyName("accessCount")] int AccessCount = 0);

/// <summary>
/// Result of cross_search tool.
/// </summary>
public sealed record CrossSearchResponse(
    [property: JsonPropertyName("results")] IReadOnlyList<CrossSearchResult> Results,
    [property: JsonPropertyName("namespacesSearched")] int NamespacesSearched,
    [property: JsonPropertyName("totalResults")] int TotalResults);

/// <summary>
/// Result of share_namespace tool.
/// </summary>
public sealed record ShareResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("accessLevel")] string AccessLevel);

/// <summary>
/// Agent identity and accessible namespaces.
/// </summary>
public sealed record WhoAmIResult(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("ownedNamespaces")] IReadOnlyList<string> OwnedNamespaces,
    [property: JsonPropertyName("sharedNamespaces")] IReadOnlyList<NamespacePermission> SharedNamespaces);
