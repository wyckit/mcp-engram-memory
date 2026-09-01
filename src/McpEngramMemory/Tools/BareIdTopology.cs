using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;

namespace McpEngramMemory.Tools;

/// <summary>
/// The tool layer's handle on the ACL-blind ambiguity test that gates every TOPOLOGY operation
/// reached by a bare id. The rule itself, and the doctrine explaining why it is ACL-blind, why a
/// count of zero is safe, and what one bit it costs, live on
/// <see cref="McpEngramMemory.Core.Services.Graph.TopologyGuard"/> — read that first.
///
/// Enforcement moved to Core, and this type is now a delegation rather than an implementation.
/// That matters: a guard that lives at the tools has to be remembered by every tool, and three
/// writers — merge_memories, background auto-linking, and accretion's cluster maintenance — never
/// were. <see cref="McpEngramMemory.Core.Services.Graph.KnowledgeGraph"/> and
/// <see cref="McpEngramMemory.Core.Services.Intelligence.ClusterManager"/> now apply the predicate
/// at the point topology is actually written, so those paths are covered by construction.
///
/// What stays here is the subset of tests that shape a CALLER-VISIBLE REPLY. Core can refuse a
/// write, but it cannot decide what the tool should say about it: link_memories has to answer with
/// the ordinary not-found reply rather than reporting a link it did not draw, and the traversal
/// verbs have to hand back an empty result rather than a partially filtered one. Those sites keep
/// their own test — and because it is this delegation, and this delegation is the Core predicate,
/// the two can never drift into disagreeing about which ids are safe.
/// </summary>
internal static class BareIdTopology
{
    /// <inheritdoc cref="TopologyGuard.IsSafe(CognitiveIndex, string, string)"/>
    public static bool IsTopologySafe(CognitiveIndex index, string id, string tenantId)
        => TopologyGuard.IsSafe(index, id, tenantId);

    /// <inheritdoc cref="TopologyGuard.IsSafe(CognitiveIndex, string, string, IReadOnlyList{string})"/>
    public static bool IsTopologySafe(
        CognitiveIndex index, string id, string tenantId, IReadOnlyList<string> namespaceSnapshot)
        => TopologyGuard.IsSafe(index, id, tenantId, namespaceSnapshot);

    /// <inheritdoc cref="TopologyGuard.ForSweep"/>
    public static TopologyGuard.Sweep ForSweep(CognitiveIndex index, string tenantId)
        => TopologyGuard.ForSweep(index, tenantId);
}
