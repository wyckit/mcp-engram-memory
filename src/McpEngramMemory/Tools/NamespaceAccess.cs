using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Sharing;

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
    private readonly AgentIdentity _agent;

    public NamespaceAccess(NamespaceRegistry registry, AgentIdentity agent)
    {
        _registry = registry;
        _agent = agent;
    }

    /// <summary>The identity these checks are made against.</summary>
    public string AgentId => _agent.AgentId;

    public bool CanRead(string ns) => _registry.HasAccess(_agent.AgentId, ns);

    public bool CanWrite(string ns) => _registry.HasAccess(_agent.AgentId, ns, "write");

    /// <summary>
    /// Claim ownership of a namespace on write. A no-op for the default agent, so servers
    /// that never set <c>AGENT_ID</c> create no records and are entirely unaffected — see
    /// <see cref="NamespaceRegistry.ClaimOwnershipOnWrite"/> for why that matters.
    /// </summary>
    public void ClaimOnWrite(string ns) => _registry.ClaimOwnershipOnWrite(ns, _agent.AgentId);

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
