using McpEngramMemory.Core.Services;

namespace McpEngramMemory.Tools;

/// <summary>
/// The ACL-blind ambiguity test that gates every TOPOLOGY operation reached by a bare id.
///
/// Two resolutions coexist in this server and they are NOT interchangeable. Using the wrong one
/// is the whole bug this type exists to close.
///
/// ENTRY-scoped operations — get_memory's primary object, promote, feedback, delete, and the
/// endpoint authorization on a link — resolve through <see cref="EntryAccessResolver"/>, which is
/// deliberately ACL-FILTERED: "unique among the namespaces the caller's verb-predicate admits".
/// That is correct there. The object such an operation mutates or discloses is the qualified
/// (tenant, namespace, id) entry the caller can see, so a twin in a namespace the caller cannot
/// see is a different object and must contribute neither a match nor an ambiguity signal.
///
/// TOPOLOGY operations cannot use that resolution, because the object they touch is not the
/// qualified entry. <see cref="McpEngramMemory.Core.Services.Graph.KnowledgeGraph"/> adjacency is
/// keyed (tenant, id) and <see cref="McpEngramMemory.Core.Services.Intelligence.ClusterManager"/>
/// membership is keyed (tenant, id); neither carries a namespace. Two same-id entries in two
/// namespaces of one tenant therefore SHARE one graph node and one membership bucket — physically
/// the same object, not two objects that happen to look alike. Authorizing through the twin you
/// can see and then reading or writing that shared node authorizes object A and acts on object B,
/// which is exactly how a principal creates a writable twin and then adds, removes, or reads
/// topology that belongs to somebody else's private entry.
///
/// So the test below is deliberately ACL-BLIND. It asks whether the TENANT holds this id in more
/// than one namespace at all — never whether the caller can see those namespaces — because the
/// twin that makes the node shared is precisely the one the caller cannot see. This is the same
/// posture <see cref="McpEngramMemory.Core.Services.Graph.TopologyCascade"/> already takes for the
/// delete/purge cascade; this type extends that established rule from the write cascade to every
/// other bare-id topology site rather than inventing a second one.
///
/// A count of zero is safe, and that is not an oversight: with no entry in the tenant answering to
/// the id there is no twin to confuse it with, so whatever topology is keyed there is dangling but
/// unambiguous. Dangling edges are an already-tolerated graph state (purge_debates leaves them
/// behind on purpose) and every read path still filters endpoints by the caller's read predicate,
/// so nothing is disclosed by letting a dangling node answer for itself.
///
/// THE ACCEPTED LEAK, stated honestly: suppression is itself one bit of information — "a same-id
/// twin exists somewhere in this tenant" — observable as topology that disappears. It is not
/// leak-free and must not be described as such. It is strictly better than the alternative, which
/// is disclosing another principal's actual edge ids, relation types, weights, metadata and
/// cluster co-membership, or letting a caller mutate them. It leaks one bit where the current
/// behaviour leaks the payload. The real fix is namespace-qualified graph and cluster endpoints,
/// tracked as issue #19; when that lands this type and its call sites go away, and with them the
/// bit.
///
/// Fails closed by construction: every caller treats "not safe" as the ordinary not-found /
/// nothing-here reply, so not-found, not-permitted and ambiguous stay indistinguishable.
/// </summary>
internal static class BareIdTopology
{
    /// <summary>
    /// True when <paramref name="id"/> names at most one of <paramref name="tenantId"/>'s
    /// namespaces, so the (tenant, id) graph node and membership bucket can be attributed to a
    /// single entry.
    ///
    /// For a site guarding ONE id. A site guarding many ids in one operation must use
    /// <see cref="ForSweep"/> instead: this overload re-lists the tenant's namespaces per call and
    /// that listing reloads the store, which turns an expansion over a result set into one full
    /// store reload per seed.
    ///
    /// An absent id is not safe. It names no node at all, so there is nothing to attribute — and
    /// this is the guard every call site consults first, so it has to answer for a blank id rather
    /// than hand one to the index scan.
    /// </summary>
    public static bool IsTopologySafe(CognitiveIndex index, string id, string tenantId)
        => !string.IsNullOrWhiteSpace(id)
           && index.CountNamespacesContaining(id, tenantId: tenantId) <= 1;

    /// <summary>
    /// As <see cref="IsTopologySafe(CognitiveIndex, string, string)"/>, against a namespace listing
    /// the caller already holds. The listing is a snapshot by design: one operation guarding many
    /// ids must judge them all against the same view of the tenant, or two ids in one reply can
    /// disagree about whether a namespace exists.
    /// </summary>
    public static bool IsTopologySafe(
        CognitiveIndex index, string id, string tenantId, IReadOnlyList<string> namespaceSnapshot)
        => !string.IsNullOrWhiteSpace(id)
           && index.CountNamespacesContaining(id, tenantId: tenantId, namespaceSnapshot) <= 1;

    /// <summary>
    /// A reusable guard for an operation that tests many ids: one namespace listing for the whole
    /// sweep, and one answer memoized per distinct id. Search expansion and the graph snapshot
    /// both test the same id repeatedly (once per seed that reaches it, once per edge endpoint),
    /// and the answer cannot change mid-operation because the snapshot is fixed.
    /// </summary>
    public static Sweep ForSweep(CognitiveIndex index, string tenantId) => new(index, tenantId);

    /// <summary>Per-operation topology guard — see <see cref="ForSweep"/>.</summary>
    internal sealed class Sweep
    {
        private readonly CognitiveIndex _index;
        private readonly string _tenantId;
        private readonly IReadOnlyList<string> _namespaces;
        private readonly Dictionary<string, bool> _memo = new(StringComparer.Ordinal);

        internal Sweep(CognitiveIndex index, string tenantId)
        {
            ArgumentNullException.ThrowIfNull(index);
            _index = index;
            _tenantId = tenantId;
            _namespaces = index.GetNamespaces(tenantId);
        }

        /// <inheritdoc cref="BareIdTopology.IsTopologySafe(CognitiveIndex, string, string)"/>
        public bool IsTopologySafe(string id)
        {
            // Ahead of the memo, not inside it: a blank id is not a dictionary key.
            if (string.IsNullOrWhiteSpace(id))
                return false;

            if (_memo.TryGetValue(id, out var cached))
                return cached;

            var safe = BareIdTopology.IsTopologySafe(_index, id, _tenantId, _namespaces);
            _memo[id] = safe;
            return safe;
        }
    }
}
