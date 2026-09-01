using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;

namespace McpEngramMemory.Tools;

/// <summary>
/// Resolves a bare entry id to the entry it names, for a caller who is only allowed to see
/// part of the store.
///
/// This exists because an id is not an identity: an entry is identified by
/// (tenant, namespace, id), and ids are unique only per (tenant, namespace). Anything reached
/// through graph edges or cluster membership arrives as a bare id with its namespace stripped,
/// so it has to be resolved back to a namespace before it can be authorized — and the
/// resolution itself has to be authorized, or the resolution outcome leaks what the caller
/// cannot see.
///
/// Deliberately static, with <see cref="CognitiveIndex"/> passed in rather than taken as a
/// <see cref="NamespaceAccess"/> constructor dependency: this shape serves both tool families
/// unchanged — those that hold a <see cref="NamespaceAccess"/> and those that hold a
/// registry plus principal behind their own private guards — without touching the ~20 sites
/// that construct <see cref="NamespaceAccess"/>.
/// </summary>
public static class EntryAccessResolver
{
    /// <summary>
    /// Resolve <paramref name="id"/> to a single entry inside <paramref name="tenantId"/> that
    /// <paramref name="canAccess"/> admits, or null.
    ///
    /// Three properties are load-bearing, not incidental:
    ///
    /// (1) <paramref name="canAccess"/> is applied to a namespace BEFORE its contents are
    ///     considered a match. An invisible namespace can therefore neither win the resolution
    ///     nor contribute an ambiguity signal — otherwise "your link silently did nothing"
    ///     would disclose that a private twin of the id exists somewhere the caller cannot see.
    ///
    /// (2) <paramref name="preferredNs"/> — the namespace the call site is already authorized
    ///     for — short-circuits. A same-id entry elsewhere in the tenant can then neither
    ///     hijack the resolution nor blank it out as ambiguous, which is what keeps linking to
    ///     an entry in your own namespace working.
    ///
    /// (3) Not-found, not-permitted and ambiguous-among-visible all return null, and are
    ///     indistinguishable to the caller. Callers must keep them that way: a distinct reply
    ///     for any one of the three rebuilds the existence oracle this type exists to close.
    ///
    /// Fails closed by construction — when the namespace cannot be established, nothing is
    /// returned to touch.
    ///
    /// The namespaces considered come from <see cref="CognitiveIndex.GetNamespacesContaining"/>,
    /// which names only the ones actually holding the id, rather than from the tenant's full
    /// namespace list. That is a cost change, not a semantic one: a namespace without the id could
    /// never have matched and could never have contributed ambiguity, so the outcome is identical
    /// while the work drops from one partition read per namespace to one or two. Property (1) is
    /// unaffected — the candidate list is namespaces, not entries, and the predicate is still what
    /// decides whether any of them is looked into.
    /// </summary>
    /// <param name="index">Index to resolve against.</param>
    /// <param name="id">Bare entry id, as carried by a graph edge or cluster membership.</param>
    /// <param name="tenantId">The caller's tenant. "" is the legacy partition, not a wildcard.</param>
    /// <param name="canAccess">The verb-appropriate namespace predicate — read for a read, write for a write.</param>
    /// <param name="preferredNs">Namespace the call site already holds authorization for, if any.</param>
    public static CognitiveEntry? Resolve(
        CognitiveIndex index,
        string id,
        string tenantId,
        Func<string, bool> canAccess,
        string? preferredNs = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(canAccess);

        if (string.IsNullOrWhiteSpace(id))
            return null;

        // The preferred namespace is checked with the same predicate as any other: being the
        // call site's own namespace is a tie-breaker, never a bypass.
        if (!string.IsNullOrWhiteSpace(preferredNs) && canAccess(preferredNs))
        {
            var preferred = index.Get(id, preferredNs, tenantId);
            if (preferred is not null)
                return preferred;
        }

        CognitiveEntry? match = null;
        foreach (var ns in index.GetNamespacesContaining(id, tenantId))
        {
            // Filter first, then look. Reversing these two lines would make an inaccessible
            // namespace able to turn a valid resolution into an ambiguous one, which is a
            // one-bit existence oracle over namespaces the caller was never shown.
            if (!canAccess(ns))
                continue;

            // Still null-checked even though every candidate held the id when the index was read:
            // the index is maintained outside the per-partition locks, so a concurrent delete can
            // retire a placement between the lookup and this read. Fail closed on the stale one.
            var candidate = index.Get(id, ns, tenantId);
            if (candidate is null)
                continue;

            // Ambiguous among the namespaces this caller can see: refuse rather than guess.
            // Guessing would let whichever namespace happens to enumerate first decide which
            // entry a link, a promotion or a delete lands on.
            if (match is not null)
                return null;

            match = candidate;
        }

        return match;
    }
}
