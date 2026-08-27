using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Sharing;
using Microsoft.Extensions.DependencyInjection;

namespace McpEngramMemory.Tools;

/// <summary>
/// The single namespace access check shared by every tool class.
///
/// This type exists because the original bug was structural, not a missed line: the check
/// lived only inside <c>cross_search</c>, so every other tool simply never acquired it. One
/// shared guard, taken as a constructor dependency, makes "did this tool check?" answerable
/// by looking at whether it holds a <see cref="NamespaceAccess"/> at all.
///
/// Denials are deliberately shaped like an empty or missing result rather than an explicit
/// "access denied". A distinct denial confirms that a namespace or entry exists, which turns
/// every read into an existence oracle for data the caller cannot see.
/// </summary>
public sealed class NamespaceAccess
{
    private readonly NamespaceRegistry _registry;
    private readonly IPrincipalContext _principal;

    [ActivatorUtilitiesConstructor]
    public NamespaceAccess(NamespaceRegistry registry, IPrincipalContext principal)
    {
        _registry = registry;
        _principal = principal;
    }

    /// <summary>Compatibility constructor for in-process hosts compiled against the v1 API.</summary>
    public NamespaceAccess(NamespaceRegistry registry, AgentIdentity agent)
        : this(registry, new PrincipalContext(string.Empty, agent.AgentId)) { }

    /// <summary>The identity these checks are made against.</summary>
    public string AgentId => _principal.AgentId;

    /// <summary>The host-bound tenant partition. Empty means the legacy partition.</summary>
    public string TenantId => _principal.TenantId;

    public bool IsLegacyUnisolated => _principal.IsLegacyUnisolated;

    public bool CanRead(string ns) => _principal.IsSystem ||
        _registry.HasAccess(_principal.AgentId, ns, tenantId: _principal.TenantId);

    public bool CanWrite(string ns) => _principal.IsSystem ||
        _registry.HasAccess(_principal.AgentId, ns, "write", _principal.TenantId);

    /// <summary>
    /// Claim ownership of a namespace on write. A no-op for the default agent, so servers
    /// that never set <c>AGENT_ID</c> create no records and are entirely unaffected — see
    /// <see cref="NamespaceRegistry.ClaimOwnershipOnWrite"/> for why that matters.
    /// </summary>
    public void ClaimOnWrite(string ns) =>
        _registry.ClaimOwnershipOnWrite(ns, _principal.AgentId, _principal.TenantId);

    /// <summary>Reply for a denied read. Indistinguishable from "there is nothing here".</summary>
    public static string ReadDenied(string ns) => $"No accessible memories in namespace '{ns}'.";

    /// <summary>Reply for a denied write. Naming the reason is fine here — the caller already knows the namespace.</summary>
    public static string WriteDenied(string ns) =>
        $"Error: namespace '{ns}' is owned by another agent. Ask its owner to share it with write access.";

    /// <summary>
    /// Access check for an entry reached by id rather than by namespace. Graph edges and
    /// cluster memberships are global and carry no namespace, so anything reached through
    /// them has to be resolved before it can be checked.
    /// </summary>
    public bool CanReadEntry(CognitiveEntry? entry) => entry is not null && CanRead(entry.Ns);
}
