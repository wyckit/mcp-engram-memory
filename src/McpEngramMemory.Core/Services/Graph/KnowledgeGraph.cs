using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Core.Services.Graph;

/// <summary>
/// The ACL-blind ambiguity test that gates every TOPOLOGY operation reached by a bare id.
///
/// Two resolutions coexist in this server and they are NOT interchangeable. Using the wrong one
/// is the whole bug this type exists to close.
///
/// ENTRY-scoped operations — get_memory's primary object, promote, feedback, delete, and the
/// endpoint authorization on a link — resolve through <c>EntryAccessResolver</c>, which is
/// deliberately ACL-FILTERED: "unique among the namespaces the caller's verb-predicate admits".
/// That is correct there. The object such an operation mutates or discloses is the qualified
/// (tenant, namespace, id) entry the caller can see, so a twin in a namespace the caller cannot
/// see is a different object and must contribute neither a match nor an ambiguity signal.
///
/// TOPOLOGY operations cannot use that resolution, because the object they touch is not the
/// qualified entry. <see cref="KnowledgeGraph"/> adjacency is keyed (tenant, id) and
/// <see cref="Intelligence.ClusterManager"/> membership is keyed (tenant, id); neither carries a
/// namespace. Two same-id entries in two namespaces of one tenant therefore SHARE one graph node
/// and one membership bucket — physically the same object, not two objects that happen to look
/// alike. Authorizing through the twin you can see and then reading or writing that shared node
/// authorizes object A and acts on object B, which is exactly how a principal creates a writable
/// twin and then adds, removes, or reads topology that belongs to somebody else's private entry.
///
/// So the test below is deliberately ACL-BLIND. It asks whether the TENANT holds this id in more
/// than one namespace at all — never whether the caller can see those namespaces — because the
/// twin that makes the node shared is precisely the one the caller cannot see.
///
/// It lives in Core, next to the writers, because being ACL-blind is what makes that possible: it
/// needs no principal, so it can sit at the boundary where topology is actually written rather
/// than at each tool that reaches the boundary. It used to sit at the tools, and three writers —
/// merge_memories, background auto-linking, and accretion's cluster maintenance — simply never
/// applied it. Enforcing here closes them, and every future writer, by construction.
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
/// Fails closed by construction: a refused write is a write that did not happen, and a refused
/// read hands back the empty result a caller with no attributable topology already sees, so
/// not-found, not-permitted and ambiguous stay indistinguishable.
/// </summary>
public static class TopologyGuard
{
    /// <summary>
    /// The one refusal reply for a bare id that cannot be attributed to a single entry.
    ///
    /// It deliberately reads as a plain miss and is the same string the tool layer returns for an
    /// id that genuinely does not exist and for one the caller may not write. Three reasons, one
    /// reply: any wording that distinguished them would confirm, one probe at a time, that a
    /// same-id entry exists in a namespace the caller cannot see.
    /// </summary>
    public static string Unattributable(string id) => $"Error: Entry '{id}' not found.";

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
    /// A blank id is not safe. It names no node at all, so there is nothing to attribute — and
    /// this is the guard every call site consults first, so it has to answer for a blank id rather
    /// than hand one to the index scan.
    /// </summary>
    public static bool IsSafe(CognitiveIndex index, string id, string tenantId)
        => !string.IsNullOrWhiteSpace(id)
           && index.CountNamespacesContaining(id, tenantId: tenantId) <= 1;

    /// <summary>
    /// As <see cref="IsSafe(CognitiveIndex, string, string)"/>, against a namespace listing the
    /// caller already holds. The listing is a snapshot by design: one operation guarding many ids
    /// must judge them all against the same view of the tenant, or two ids in one reply can
    /// disagree about whether a namespace exists.
    /// </summary>
    public static bool IsSafe(
        CognitiveIndex index, string id, string tenantId, IReadOnlyList<string> namespaceSnapshot)
        => !string.IsNullOrWhiteSpace(id)
           && index.CountNamespacesContaining(id, tenantId: tenantId, namespaceSnapshot) <= 1;

    /// <summary>
    /// A reusable guard for an operation that tests many ids: one namespace listing for the whole
    /// sweep, and one answer memoized per distinct id. A bulk edge write, a cluster's member list
    /// and a BFS all test the same id repeatedly (once per edge that names it, once per seed that
    /// reaches it), and the answer cannot change mid-operation because the snapshot is fixed.
    /// </summary>
    public static Sweep ForSweep(CognitiveIndex index, string tenantId) => new(index, tenantId);

    /// <summary>Per-operation topology guard — see <see cref="ForSweep"/>.</summary>
    public sealed class Sweep
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

        /// <inheritdoc cref="TopologyGuard.IsSafe(CognitiveIndex, string, string)"/>
        public bool IsTopologySafe(string id)
        {
            // Ahead of the memo, not inside it: a blank id is not a dictionary key.
            if (string.IsNullOrWhiteSpace(id))
                return false;

            if (_memo.TryGetValue(id, out var cached))
                return cached;

            var safe = IsSafe(_index, id, _tenantId, _namespaces);
            _memo[id] = safe;
            return safe;
        }

        /// <summary>
        /// THE INVARIANT, and the only predicate a topology site should be reaching for: an edge is
        /// usable — readable, writable, transferable, traversable, boostable — only when BOTH of
        /// its endpoints are attributable.
        ///
        /// Stated about the EDGE rather than about an operation's arguments, because four review
        /// rounds were spent rediscovering that those are different sets. An edge is a claim about
        /// two nodes, and whichever one the caller named, the other is disclosed (its id, and
        /// through resolution its text and namespace) or rewritten (its adjacency list) exactly the
        /// same. Guarding the arguments left <c>TransferEdges</c> rewriting an edge whose third
        /// endpoint was shared, and left a safe seed handing back an edge that pointed into a node
        /// two entries answer to. Testing the edge removes the place where the far endpoint could
        /// be forgotten.
        /// </summary>
        public bool IsEdgeUsable(GraphEdge edge)
            => edge is not null && IsTopologySafe(edge.SourceId) && IsTopologySafe(edge.TargetId);

        /// <summary>
        /// The memoized answer alone: false for an id this sweep has not already judged.
        ///
        /// For the one situation that has to re-check while holding the graph's write lock. The
        /// full test resolves through <see cref="CognitiveIndex"/>, and taking the index's locks
        /// under the graph's would let the two be acquired in opposite orders — so a re-check asks
        /// only what the sweep already knows and treats anything it does not know as unsafe. An
        /// endpoint that appeared between the snapshot and the lock is precisely the case that must
        /// fail closed.
        /// </summary>
        public bool IsKnownSafe(string id)
            => !string.IsNullOrWhiteSpace(id) && _memo.TryGetValue(id, out var safe) && safe;

        /// <inheritdoc cref="IsKnownSafe"/>
        public bool IsEdgeKnownUsable(GraphEdge edge)
            => edge is not null && IsKnownSafe(edge.SourceId) && IsKnownSafe(edge.TargetId);
    }
}

/// <summary>
/// What a batch write does about an edge whose endpoints are already related.
/// </summary>
public enum EdgeAddMode
{
    /// <summary>
    /// Replace an edge with the same source, target AND relation; leave every other relation between
    /// the same two ids alone. The historic behaviour, and the right one for a caller asserting one
    /// specific relationship.
    /// </summary>
    ReplaceSameRelation = 0,

    /// <summary>
    /// Write the edge only when NO relation yet runs between its endpoints, in either direction.
    ///
    /// For a caller whose precondition is that the two are unrelated — auto-link, which must never
    /// lay a derived <c>similar_to</c> over a manually-asserted <c>contradicts</c>. That
    /// precondition cannot be established outside the write lock: any pre-filter reads a snapshot,
    /// and a relation added between the read and the write is invisible to it.
    /// </summary>
    OnlyIfUnlinked = 1,
}

/// <summary>
/// In-memory knowledge graph using adjacency lists for directed edges between cognitive entries.
///
/// Tenant isolation: adjacency is keyed by <c>(tenant, entryId)</c>, and every <see cref="GraphEdge"/>
/// carries its own <see cref="GraphEdge.TenantId"/>. A lookup for tenant T only ever sees T's edges,
/// so the graph never connects entries across tenants — while still allowing cross-namespace
/// association WITHIN a tenant (the legacy behavior for tenant <c>""</c> is byte-for-byte identical,
/// since every legacy edge and lookup uses tenant <c>""</c>). Entry resolution is tenant-scoped:
/// the fast legacy id locator for tenant <c>""</c>, a tenant-scoped scan otherwise.
///
/// Bare-id attribution: within one tenant an id is not an identity — entries are identified by
/// (tenant, namespace, id) — so an id the tenant holds in two namespaces names ONE node shared by
/// two entries. The rule every method here applies is
/// <see cref="TopologyGuard.Sweep.IsEdgeUsable"/>: an edge is usable only when BOTH of its
/// endpoints are attributable, tested per edge rather than per argument. Guarding the ids an
/// operation happens to NAME is what failed repeatedly — the transferred edge's third endpoint and
/// the neighbor behind a safe seed are both nodes no argument mentions, and both are disclosed or
/// rewritten all the same.
///
/// The guard is enforced here rather than at each tool because it is ACL-blind and so needs no
/// principal: putting it at the boundary is what makes it impossible for a new reader or writer to
/// forget. Consumers that only consume topology — spreading activation, recall expansion, the
/// diffusion kernel, the visualizer — need no guard of their own precisely because the edges they
/// are handed have already passed it.
///
/// The <c>GetStored*</c> pair is the deliberate exception, and it is not a hole: those return the
/// stored bytes for persistence and diagnostics, are never projected to a principal, and carry no
/// resolution step that could turn a bare id into somebody else's entry.
///
/// Locking strategy:
/// - Read-only methods use EnterUpgradeableReadLock, upgrading to write only if EnsureLoaded needs to load.
/// - Mutating methods use EnterWriteLock directly.
/// - Methods that need CognitiveIndex snapshot data under graph lock, then resolve entries outside
///   to avoid lock-ordering deadlocks. The topology guard resolves through CognitiveIndex too, so
///   it runs BEFORE the graph lock is taken, never inside it.
/// </summary>
public sealed class KnowledgeGraph
{
    // outgoing[(tenant, sourceId)] = list of edges from sourceId within that tenant
    private readonly Dictionary<(string Tenant, string Id), List<GraphEdge>> _outgoing = new();
    // incoming[(tenant, targetId)] = list of edges to targetId within that tenant
    private readonly Dictionary<(string Tenant, string Id), List<GraphEdge>> _incoming = new();
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly IStorageProvider _persistence;
    private readonly CognitiveIndex _index;
    private bool _loaded;
    private long _revision;

    /// <summary>
    /// Monotonic counter incremented on any topology change (edge added or removed).
    /// Consumers that cache derived structures (e.g., MemoryDiffusionKernel) compare against
    /// this to detect staleness. Read is lock-free via Interlocked.
    /// </summary>
    public long Revision => Interlocked.Read(ref _revision);

    // Per-tenant topology revision. Bumped only when an edge in that tenant changes, so a
    // tenant-scoped derived cache (the diffusion kernel) is not invalidated by unrelated tenants'
    // writes. The global _revision still bumps on every change for callers that want it.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _tenantRevisions = new();

    /// <summary>Monotonic topology revision for one tenant (0 if the tenant has no edges yet).</summary>
    public long RevisionFor(string tenantId)
        => _tenantRevisions.TryGetValue(tenantId ?? string.Empty, out var r) ? r : 0;

    private void BumpTenant(string tenantId)
        => _tenantRevisions.AddOrUpdate(tenantId ?? string.Empty, 1L, static (_, v) => v + 1);

    public KnowledgeGraph(IStorageProvider persistence, CognitiveIndex index)
    {
        _persistence = persistence;
        _index = index;
    }

    /// <summary>Total number of edges in the graph, across all tenants.</summary>
    public int EdgeCount
    {
        get
        {
            _lock.EnterUpgradeableReadLock();
            try
            {
                EnsureLoaded();
                return _outgoing.Values.Sum(l => l.Count);
            }
            finally { _lock.ExitUpgradeableReadLock(); }
        }
    }

    /// <summary>
    /// Create a directed edge between two entries. The edge's own TenantId scopes the partition.
    ///
    /// An endpoint whose id names more than one of the tenant's namespaces is refused: the edge
    /// would land on a node shared with a twin the caller was never shown. The refusal reads as an
    /// ordinary miss so it cannot be told apart from an endpoint that does not exist — which is
    /// also why a caller that needs to KNOW whether the edge landed must use
    /// <see cref="TryAddEdge"/> rather than inspect this string.
    /// </summary>
    public string AddEdge(GraphEdge edge)
    {
        TryAddEdge(edge, out var reply);
        return reply;
    }

    /// <summary>
    /// As <see cref="AddEdge"/>, but says whether the edge was actually written.
    ///
    /// The boolean is the point. <see cref="AddEdge"/> answers with a sentence, so a caller that
    /// counts calls counts refusals as successes — which is exactly how the background auto-link
    /// sweep reported edges it had never created. Parsing the sentence would be worse: the refusal
    /// is deliberately byte-identical to a genuine miss, so it is not a distinguishable token.
    ///
    /// <paramref name="reply"/> stays the caller-visible string, and stays indistinguishable
    /// between "endpoint absent", "endpoint not writable" and "endpoint shared with a twin you
    /// cannot see". Only the in-process caller learns the difference, and only as one bit it must
    /// not forward: reporting "1 of 3 skipped as ambiguous" to a principal would rebuild the
    /// existence oracle this whole mechanism exists to close.
    /// </summary>
    public bool TryAddEdge(GraphEdge edge, out string reply)
    {
        ArgumentNullException.ThrowIfNull(edge);

        // Guarded before the write lock, never inside it: the test resolves through CognitiveIndex,
        // which holds its own locks, and this class's rule is that index work happens outside the
        // graph lock so the two can never be acquired in opposite orders.
        var guard = TopologyGuard.ForSweep(_index, edge.TenantId);
        if (!guard.IsEdgeUsable(edge))
        {
            // Named after the endpoint that failed, so the reply matches what a caller naming that
            // same id alone would get. Source first, arbitrarily but consistently — the two
            // refusals are the same sentence with a different id in it, and a caller who can tell
            // WHICH endpoint was refused learns nothing it did not already supply.
            reply = TopologyGuard.Unattributable(
                guard.IsTopologySafe(edge.SourceId) ? edge.TargetId : edge.SourceId);
            return false;
        }

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            AddEdgeInternal(edge);

            // Auto-create reverse edge for cross_reference (same tenant partition)
            if (edge.Relation == "cross_reference")
            {
                var reverse = new GraphEdge(edge.TargetId, edge.SourceId, "cross_reference",
                    edge.Weight, edge.Metadata.Count > 0 ? new Dictionary<string, string>(edge.Metadata) : null,
                    edge.TenantId);
                AddEdgeInternal(reverse);
            }

            Interlocked.Increment(ref _revision);
            BumpTenant(edge.TenantId);
            ScheduleSaveEdges();
            reply = edge.Relation == "cross_reference"
                ? $"Linked '{edge.SourceId}' <-> '{edge.TargetId}' (cross_reference, bidirectional)."
                : $"Linked '{edge.SourceId}' -> '{edge.TargetId}' ({edge.Relation}).";
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Create multiple edges in a single write lock acquisition. Returns the number actually
    /// written, which is what makes the count honest when an endpoint was declined.
    ///
    /// <paramref name="mode"/> chooses the write boundary. The default one replaces an edge with the
    /// same source, target AND relation and is blind to every other relation between the same two
    /// ids, which is correct for a caller asserting a specific relationship and wrong for one whose
    /// precondition is "these two are not related at all": that caller can only have tested the
    /// precondition against a snapshot, and a relation added between its test and this write lands
    /// on top of it. <see cref="EdgeAddMode.OnlyIfUnlinked"/> moves the test to the only place it can
    /// be atomic with the write.
    /// </summary>
    public int AddEdges(IEnumerable<GraphEdge> edges, EdgeAddMode mode = EdgeAddMode.ReplaceSameRelation)
    {
        ArgumentNullException.ThrowIfNull(edges);

        // Screened before the lock, for the same lock-ordering reason as AddEdge, and with one
        // namespace listing per tenant in the batch rather than one per edge: listing a tenant's
        // namespaces reloads the store, so a per-edge guard would turn a bulk write into one full
        // store reload per edge. A batch may legitimately span tenants, so the sweeps are keyed by
        // the edge's own tenant — one sweep may never judge another tenant's id.
        var sweeps = new Dictionary<string, TopologyGuard.Sweep>(StringComparer.Ordinal);
        var admitted = new List<GraphEdge>();
        foreach (var edge in edges)
        {
            if (!sweeps.TryGetValue(edge.TenantId, out var guard))
                sweeps[edge.TenantId] = guard = TopologyGuard.ForSweep(_index, edge.TenantId);
            if (guard.IsEdgeUsable(edge))
                admitted.Add(edge);
        }

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            int count = 0;
            var touchedTenants = new HashSet<string>();
            foreach (var edge in admitted)
            {
                // The whole point of the mode: the condition is evaluated against the graph as it is
                // at the instant of the write, holding the write lock, so nothing can slip between
                // the test and the mutation. A caller's own pre-filter cannot achieve that however
                // carefully it is written — it reads a snapshot, and the graph stays mutable.
                //
                // Nothing is allocated here. The check walks the two adjacency lists this edge's
                // source already has, in place; a correctness fix that widened a lock around a
                // dictionary build in this codebase once delayed every writer behind it, and a
                // per-edge allocation inside a batch write lock would repeat that mistake.
                if (mode == EdgeAddMode.OnlyIfUnlinked
                    && AnyEdgeBetweenUnderLock(edge.TenantId, edge.SourceId, edge.TargetId))
                    continue;

                AddEdgeInternal(edge);
                touchedTenants.Add(edge.TenantId);
                if (edge.Relation == "cross_reference")
                {
                    var reverse = new GraphEdge(edge.TargetId, edge.SourceId, "cross_reference",
                        edge.Weight, edge.Metadata.Count > 0 ? new Dictionary<string, string>(edge.Metadata) : null,
                        edge.TenantId);
                    AddEdgeInternal(reverse);
                }
                count++;
            }
            if (count > 0)
            {
                Interlocked.Increment(ref _revision);
                foreach (var t in touchedTenants) BumpTenant(t);
                ScheduleSaveEdges();
            }
            return count;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Remove edges between two entries within a tenant, optionally filtered by relation
    /// (pass <c>relation: null</c> for all relations; <c>tenantId: ""</c> targets the legacy partition).
    /// All parameters are required: an optional tenant here silently fell back to the legacy
    /// partition, and tenantId cannot jump ahead of <paramref name="relation"/> without an old
    /// positional relation string rebinding into the tenant slot.
    ///
    /// Removal is a topology mutation like any other, so an endpoint the tenant holds in two
    /// namespaces is declined: the edge being named hangs off a node the caller's twin shares with
    /// an entry they cannot see. The refusal reuses the no-such-edge reply verbatim rather than
    /// inventing a second one, so "nothing to remove" and "declined to guess" are one answer.
    ///
    /// Testing the two arguments IS the edge test here, and that is worth stating because it is
    /// what fails elsewhere: every edge this touches runs between exactly
    /// <paramref name="sourceId"/> and <paramref name="targetId"/>, so there is no third endpoint
    /// for the argument test to miss. Contrast <see cref="TransferEdges"/>, where there is one on
    /// every edge.
    /// </summary>
    public string RemoveEdges(string sourceId, string targetId, string? relation, string tenantId)
    {
        var guard = TopologyGuard.ForSweep(_index, tenantId);
        if (!guard.IsTopologySafe(sourceId) || !guard.IsTopologySafe(targetId))
            return NoEdgesBetween(sourceId, targetId);

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            int removed = 0;
            removed += RemoveMatching(_outgoing, (tenantId, sourceId), e => e.TargetId == targetId && (relation == null || e.Relation == relation));
            removed += RemoveMatching(_incoming, (tenantId, targetId), e => e.SourceId == sourceId && (relation == null || e.Relation == relation));

            if (removed > 0)
            {
                Interlocked.Increment(ref _revision);
                BumpTenant(tenantId);
                ScheduleSaveEdges();
            }

            return removed > 0
                ? $"Removed {removed} edge(s) between '{sourceId}' and '{targetId}'."
                : NoEdgesBetween(sourceId, targetId);
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>The one "nothing here" reply for <see cref="RemoveEdges"/>, shared by its genuine
    /// miss and its topology refusal so the two can never drift into an existence oracle.</summary>
    private static string NoEdgesBetween(string sourceId, string targetId)
        => $"No edges found between '{sourceId}' and '{targetId}'.";

    /// <summary>
    /// Remove ALL edges referencing an entry within a tenant (cascade delete). Pass "" for the
    /// legacy partition.
    ///
    /// It used to be the one unguarded mutator, on the argument that its sanctioned caller
    /// (<see cref="TopologyCascade"/>) already tested the id being swept. That covered the id this
    /// method NAMES and nothing else: every edge it deletes also rewrites the adjacency list of an
    /// endpoint at the other end, and that endpoint is named by no argument. So the same edge-level
    /// rule applies here — an incident edge is deleted only when both of its endpoints are
    /// attributable, which subsumes the caller's test of <paramref name="id"/> as the special case
    /// where the shared endpoint is the swept one.
    ///
    /// The retained edges dangle once the entry is gone, and that is the intended outcome rather
    /// than a leak: a dangling edge is an already-tolerated graph state, every read path resolves
    /// its endpoints and drops what it cannot find, and the alternative is stripping an edge off a
    /// node that belongs to an entry nobody authorized us to touch.
    ///
    /// It also keeps <see cref="TopologyCascade"/>'s dry run honest. The preview counts through
    /// <see cref="GetEdgesForEntry"/>, which applies this same edge test, so a preview and the
    /// purge it previews can no longer report different figures.
    ///
    /// Call the <c>guard</c> overload from a sweep over many entries: one namespace listing and one
    /// memo for the whole purge instead of one per entry.
    /// </summary>
    public int RemoveAllEdgesForEntry(string id, string tenantId)
        => RemoveAllEdgesForEntry(id, tenantId, TopologyGuard.ForSweep(_index, tenantId));

    /// <inheritdoc cref="RemoveAllEdgesForEntry(string, string)"/>
    public int RemoveAllEdgesForEntry(string id, string tenantId, TopologyGuard.Sweep guard)
    {
        ArgumentNullException.ThrowIfNull(guard);

        // Snapshot the incident edges under the read lock, judge them after releasing it, then
        // delete exactly the ones that passed. The judgement resolves through CognitiveIndex, so it
        // cannot happen inside the graph lock; deleting a precomputed set rather than a whole
        // adjacency list is what lets the two phases be separated safely — an edge that appeared in
        // between is simply not in the set, so it survives rather than being deleted unexamined.
        List<GraphEdge> outgoing, incoming;
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            outgoing = _outgoing.TryGetValue((tenantId, id), out var o) ? o.ToList() : new List<GraphEdge>();
            incoming = _incoming.TryGetValue((tenantId, id), out var i) ? i.ToList() : new List<GraphEdge>();
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        var removableOut = outgoing.Where(guard.IsEdgeUsable).ToList();
        var removableIn = incoming.Where(guard.IsEdgeUsable).ToList();
        if (removableOut.Count == 0 && removableIn.Count == 0)
            return 0;

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            int removed = 0;

            // Matched on (endpoint, relation) rather than by dropping the whole list: the list now
            // has survivors in it. AddEdgeInternal keeps (source, target, relation) unique, so each
            // match is the one edge meant.
            foreach (var edge in removableOut)
            {
                removed += RemoveMatching(_outgoing, (tenantId, id),
                    e => e.TargetId == edge.TargetId && e.Relation == edge.Relation);
                RemoveMatching(_incoming, (tenantId, edge.TargetId),
                    e => e.SourceId == id && e.Relation == edge.Relation);
            }

            foreach (var edge in removableIn)
            {
                removed += RemoveMatching(_incoming, (tenantId, id),
                    e => e.SourceId == edge.SourceId && e.Relation == edge.Relation);
                RemoveMatching(_outgoing, (tenantId, edge.SourceId),
                    e => e.TargetId == id && e.Relation == edge.Relation);
            }

            if (removed > 0)
            {
                Interlocked.Increment(ref _revision);
                BumpTenant(tenantId);
                ScheduleSaveEdges();
            }

            return removed;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Get directly connected entries within a tenant. Pass <c>relation: null</c> for all relations,
    /// <c>direction: "both"</c> for both directions, and <c>tenantId: ""</c> for the legacy partition.
    /// All parameters are required: tenantId cannot jump ahead of the string-typed relation/direction
    /// slots without an old positional call like <c>GetNeighbors(id, "supports")</c> silently binding
    /// the relation into the tenant slot — the exact fail-open this shape exists to kill.
    ///
    /// Every returned edge has passed <see cref="TopologyGuard.Sweep.IsEdgeUsable"/>, which covers
    /// the seed and the far endpoint together. This used to be left to the caller on the argument
    /// that one hop is containable from outside — and it was not: spreading activation consumes
    /// this raw, and a safe seed with an edge into a shared node was enough to move a private
    /// entry's activation energy. The filter belongs to whoever hands out the edge.
    ///
    /// The sweep is built only when there is adjacency to judge, and after the graph lock has been
    /// released — it resolves through <see cref="CognitiveIndex"/>, which has its own lock, and a
    /// node with no edges must not pay for a namespace listing it will not consult.
    /// </summary>
    public GetNeighborsResult GetNeighbors(string id, string? relation, string direction, string tenantId)
    {
        // Snapshot edge data under graph lock, then resolve entries outside the lock
        // to avoid lock-ordering issues with CognitiveIndex.
        List<(GraphEdge edge, string resolveId)> edgesToResolve;

        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            edgesToResolve = new();

            if (direction is "both" or "outgoing")
            {
                if (_outgoing.TryGetValue((tenantId, id), out var outEdges))
                {
                    foreach (var edge in outEdges)
                    {
                        if (relation is not null && edge.Relation != relation) continue;
                        edgesToResolve.Add((edge, edge.TargetId));
                    }
                }
            }

            if (direction is "both" or "incoming")
            {
                if (_incoming.TryGetValue((tenantId, id), out var inEdges))
                {
                    foreach (var edge in inEdges)
                    {
                        if (relation is not null && edge.Relation != relation) continue;
                        edgesToResolve.Add((edge, edge.SourceId));
                    }
                }
            }
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        if (edgesToResolve.Count == 0)
            return new GetNeighborsResult(id, Array.Empty<NeighborResult>());

        // Resolve entries outside graph lock (CognitiveIndex has its own lock), scoped to the tenant.
        var guard = TopologyGuard.ForSweep(_index, tenantId);
        var neighbors = new List<NeighborResult>();
        foreach (var (edge, resolveId) in edgesToResolve)
        {
            // Ahead of the resolve, not after it. Resolution of an ambiguous bare id is what turns
            // a shared node into "whichever twin this caller can read", so an edge filtered
            // afterwards has already been given a face that does not belong to it.
            if (!guard.IsEdgeUsable(edge)) continue;

            var entry = ResolveEntry(resolveId, tenantId);
            if (entry is not null)
                neighbors.Add(new NeighborResult(edge, ToEntryInfo(entry)));
        }

        return new GetNeighborsResult(id, neighbors);
    }

    /// <summary>
    /// Multi-hop graph traversal via BFS, scoped to a tenant. Pass "" for the legacy partition.
    /// tenantId sits directly after the identity parameter so a stale positional call fails
    /// loudly (the old third argument was an int, which cannot convert to string).
    ///
    /// The walk refuses to cross a node it cannot attribute to a single entry. That has to happen
    /// inside the BFS, before a target is resolved or enqueued: dropping such edges from the
    /// finished result is too late, because by then the walk has already crossed the shared node
    /// and discovered its descendants, and those descendants are the disclosure — a caller learns
    /// what an invisible twin is connected to even when every edge naming it has been stripped.
    /// </summary>
    public TraversalResult Traverse(string startId, string tenantId, int maxDepth = 2, string? relation = null,
        float minWeight = 0f, int maxResults = 20)
    {
        maxDepth = Math.Clamp(maxDepth, 1, 5);

        // One sweep for the whole walk: the root test and every hop's test would otherwise each
        // re-list the tenant's namespaces, and that listing reloads the store.
        var guard = TopologyGuard.ForSweep(_index, tenantId);

        // A shared root cannot be attributed to the entry the caller meant, so there is no walk to
        // start — and the empty result is exactly what an id with no edges already returns.
        if (!guard.IsTopologySafe(startId))
            return new TraversalResult(startId, Array.Empty<CognitiveEntryInfo>(), Array.Empty<GraphEdge>());

        // Snapshot this tenant's adjacency under the graph lock (keyed by bare id for BFS),
        // then resolve entries outside the lock to avoid lock-ordering issues.
        Dictionary<string, List<GraphEdge>> outgoingSnapshot;
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            // Shallow copy of this tenant's adjacency lists (edges are immutable)
            outgoingSnapshot = _outgoing
                .Where(kv => kv.Key.Tenant == tenantId)
                .ToDictionary(kv => kv.Key.Id, kv => kv.Value.ToList());
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        // BFS on snapshot, resolving entries via CognitiveIndex (its own lock)
        var visited = new HashSet<string>();
        var queue = new Queue<(string id, int depth)>();
        var resultEntries = new List<CognitiveEntryInfo>();
        var resultEdges = new List<GraphEdge>();

        queue.Enqueue((startId, 0));
        visited.Add(startId);

        var startEntry = ResolveEntry(startId, tenantId);
        if (startEntry is not null)
            resultEntries.Add(ToEntryInfo(startEntry));

        while (queue.Count > 0 && resultEntries.Count < maxResults)
        {
            var (currentId, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            if (outgoingSnapshot.TryGetValue(currentId, out var edges))
            {
                foreach (var edge in edges)
                {
                    if (relation is not null && edge.Relation != relation) continue;
                    if (edge.Weight < minWeight) continue;
                    if (visited.Contains(edge.TargetId)) continue;

                    // The stop, and it has to be here — ahead of the edge, the resolve and the
                    // enqueue. An edge with an unattributable endpoint reaches a node two entries
                    // share, so neither the edge, the entry it resolves to, nor anything beyond it
                    // is attributable to the entry this walk is about. Not marked visited: it was
                    // never visited, and the sweep memoizes so a second path costs nothing.
                    if (!guard.IsEdgeUsable(edge)) continue;

                    visited.Add(edge.TargetId);
                    resultEdges.Add(edge);

                    var entry = ResolveEntry(edge.TargetId, tenantId);
                    if (entry is not null)
                        resultEntries.Add(ToEntryInfo(entry));

                    if (resultEntries.Count < maxResults)
                        queue.Enqueue((edge.TargetId, depth + 1));
                }
            }
        }

        return new TraversalResult(startId, resultEntries, resultEdges);
    }

    /// <summary>
    /// The attributable edges incident to an entry within a tenant, both directions. Pass "" for
    /// the legacy partition.
    ///
    /// An edge is withheld unless BOTH endpoints are attributable, so a safe id whose edge points
    /// into a node two entries share hands back nothing for that edge. The far endpoint is the half
    /// that matters here: an edge carries the other endpoint's id, relation, weight and metadata,
    /// and every consumer of this list either shows those to a principal or resolves the bare id
    /// into an entry.
    /// </summary>
    public IReadOnlyList<GraphEdge> GetEdgesForEntry(string id, string tenantId)
    {
        var stored = GetStoredEdgesForEntry(id, tenantId);
        if (stored.Count == 0)
            return stored;

        // Built after the lock, and only when there is something to judge — see GetNeighbors.
        var guard = TopologyGuard.ForSweep(_index, tenantId);
        return stored.Where(guard.IsEdgeUsable).ToList();
    }

    /// <summary>
    /// The STORED adjacency of one node, endpoint attribution NOT applied — what is on disk rather
    /// than what is attributable.
    ///
    /// For persistence, diagnostics, and tests that have to observe that a suppressed edge really
    /// is still there. Never project this to a principal and never resolve its endpoint ids into
    /// entries: those two steps are exactly what <see cref="GetEdgesForEntry"/> exists to gate.
    /// </summary>
    public IReadOnlyList<GraphEdge> GetStoredEdgesForEntry(string id, string tenantId)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            var edges = new List<GraphEdge>();
            if (_outgoing.TryGetValue((tenantId, id), out var outEdges))
                edges.AddRange(outEdges);
            if (_incoming.TryGetValue((tenantId, id), out var inEdges))
                edges.AddRange(inEdges);
            return edges;
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>
    /// Get all attributable 'contradicts' edges touching a namespace within a tenant. Pass "" for
    /// the legacy partition. An edge with an unattributable endpoint is withheld before either
    /// endpoint is resolved — this is the one read that hands back whole ENTRIES for both ends.
    /// </summary>
    public IReadOnlyList<(GraphEdge Edge, CognitiveEntry? Source, CognitiveEntry? Target)> GetContradictions(string ns, string tenantId)
    {
        List<GraphEdge> contradictEdges;

        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            contradictEdges = _outgoing
                .Where(kv => kv.Key.Tenant == tenantId)
                .SelectMany(kv => kv.Value)
                .Where(e => e.Relation == "contradicts")
                .ToList();
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        if (contradictEdges.Count == 0)
            return Array.Empty<(GraphEdge, CognitiveEntry?, CognitiveEntry?)>();

        // Resolve entries outside graph lock (scoped to tenant), filter to namespace
        var guard = TopologyGuard.ForSweep(_index, tenantId);
        var results = new List<(GraphEdge, CognitiveEntry?, CognitiveEntry?)>();
        foreach (var edge in contradictEdges)
        {
            // This method resolves BOTH endpoints and hands the entries back, so an unattributable
            // endpoint here discloses a whole entry rather than just an id — and a "contradicts"
            // edge is a claim about the pair, which is meaningless if one half is the wrong twin.
            if (!guard.IsEdgeUsable(edge)) continue;

            var source = ResolveEntry(edge.SourceId, tenantId);
            var target = ResolveEntry(edge.TargetId, tenantId);

            // Include if either entry is in the requested namespace
            if ((source?.Ns == ns) || (target?.Ns == ns))
                results.Add((edge, source, target));
        }

        return results;
    }

    /// <summary>
    /// Every stored edge across every tenant, for persistence. Attribution is per-tenant and there
    /// is no tenant here to attribute within, so this one is raw by construction — which is safe
    /// only because it is never projected to a principal and never resolves an id into an entry.
    /// A caller that has a tenant wants <see cref="GetAllEdges(string)"/>.
    /// </summary>
    public IReadOnlyList<GraphEdge> GetAllEdges()
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            return _outgoing.Values.SelectMany(l => l).ToList();
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>
    /// The attributable edges of one tenant, for per-tenant graph derivations (diffusion,
    /// visualization, tallies).
    ///
    /// Edges with an unattributable endpoint are withheld. A derivation is a read like any other:
    /// the diffusion kernel turns this list into a basis that BOOSTS the entries it names, and the
    /// visualizer draws the endpoint ids straight into its reply — so an edge into a shared node
    /// moves or reveals an entry belonging to somebody the caller was never shown.
    /// </summary>
    public IReadOnlyList<GraphEdge> GetAllEdges(string tenantId)
    {
        var stored = GetStoredEdges(tenantId);
        if (stored.Count == 0)
            return stored;

        var guard = TopologyGuard.ForSweep(_index, tenantId);
        return stored.Where(guard.IsEdgeUsable).ToList();
    }

    /// <summary>
    /// The STORED edges of one tenant, endpoint attribution NOT applied.
    /// Same contract as <see cref="GetStoredEdgesForEntry"/>: diagnostics and tests only, never
    /// projected to a principal and never resolved into entries.
    /// </summary>
    public IReadOnlyList<GraphEdge> GetStoredEdges(string tenantId)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            return _outgoing
                .Where(kv => kv.Key.Tenant == tenantId)
                .SelectMany(kv => kv.Value)
                .ToList();
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>
    /// Transfer all edges from one entry to another within a tenant (for merge operations).
    /// Returns count transferred. Pass "" for the legacy partition.
    ///
    /// This is the widest bare-id write in the class — it rewires and then DELETES a whole node's
    /// adjacency — and <paramref name="fromId"/> and <paramref name="toId"/> are not the only nodes
    /// it touches. Every incident edge has a THIRD endpoint, and rewriting <c>from -&gt; far</c>
    /// into <c>to -&gt; far</c> mutates <c>far</c>'s adjacency list as surely as it mutates the two
    /// named ones. So the precondition is the edge-level rule over every incident edge in both
    /// directions, not a test of the two arguments.
    ///
    /// ALL OR NOTHING. Skipping just the offending edges would leave the merge half-applied — some
    /// of the archived entry's topology moved, some still hanging off a node that is about to be
    /// abandoned — which is a correctness problem on top of the disclosure. Returning 0 is what a
    /// merge of two edgeless entries already reports, so the caller's reply stays truthful without
    /// becoming a signal; the count of what was declined must never reach a principal.
    /// </summary>
    public int TransferEdges(string fromId, string toId, string tenantId)
    {
        var guard = TopologyGuard.ForSweep(_index, tenantId);
        if (!guard.IsTopologySafe(fromId) || !guard.IsTopologySafe(toId))
            return 0;

        // Snapshot every incident edge and judge it before any mutation. The judgement resolves
        // through CognitiveIndex, so it cannot run under the graph lock; EnsureLoaded here is what
        // makes the re-check below meaningful, because it guarantees the write phase is not the
        // first thing to pull persisted edges into memory.
        List<GraphEdge> incident;
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            incident = new List<GraphEdge>();
            if (_outgoing.TryGetValue((tenantId, fromId), out var outSnapshot))
                incident.AddRange(outSnapshot);
            if (_incoming.TryGetValue((tenantId, fromId), out var inSnapshot))
                incident.AddRange(inSnapshot);
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        foreach (var edge in incident)
            if (!guard.IsEdgeUsable(edge))
                return 0;

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();

            // Re-checked under the write lock against what the sweep already decided, because the
            // pre-check ran without it. An edge that arrived in between names an endpoint this
            // sweep has never judged, and judging it here would take the index's locks under the
            // graph's — so an unknown endpoint aborts instead. Fail closed: 0, nothing mutated.
            foreach (var dict in new[] { _outgoing, _incoming })
                if (dict.TryGetValue((tenantId, fromId), out var current))
                    foreach (var edge in current)
                        if (!guard.IsEdgeKnownUsable(edge))
                            return 0;

            int transferred = 0;
            var fromKey = (tenantId, fromId);

            // Transfer outgoing edges: fromId -> X becomes toId -> X
            if (_outgoing.TryGetValue(fromKey, out var outEdges))
            {
                foreach (var edge in outEdges.ToList())
                {
                    // Skip self-referential edges that would result from the transfer
                    if (edge.TargetId == toId) continue;

                    var newEdge = new GraphEdge(toId, edge.TargetId, edge.Relation, edge.Weight, edge.Metadata, tenantId);
                    AddEdgeInternal(newEdge);
                    transferred++;
                }
                // Remove old outgoing list
                _outgoing.Remove(fromKey);
            }

            // Transfer incoming edges: X -> fromId becomes X -> toId
            if (_incoming.TryGetValue(fromKey, out var inEdges))
            {
                foreach (var edge in inEdges.ToList())
                {
                    if (edge.SourceId == toId) continue;

                    // Remove the old outgoing reference (X -> fromId) from the source's outgoing list
                    RemoveMatching(_outgoing, (tenantId, edge.SourceId), e => e.TargetId == fromId && e.Relation == edge.Relation);

                    var newEdge = new GraphEdge(edge.SourceId, toId, edge.Relation, edge.Weight, edge.Metadata, tenantId);
                    AddEdgeInternal(newEdge);
                    transferred++;
                }
                _incoming.Remove(fromKey);
            }

            if (transferred > 0)
            {
                Interlocked.Increment(ref _revision);
                BumpTenant(tenantId);
                ScheduleSaveEdges();
            }

            return transferred;
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ── Internals ──

    /// <summary>Resolve an entry id within a tenant: fast legacy locator for tenant "", tenant-scoped scan otherwise.</summary>
    private CognitiveEntry? ResolveEntry(string id, string tenantId)
        => tenantId.Length == 0 ? _index.Get(id) : _index.GetForTenant(id, tenantId: tenantId);

    /// <summary>
    /// True when ANY edge already runs between the two ids inside one tenant, in either direction
    /// and under any relation. Caller must hold the write lock.
    ///
    /// No attribution test is needed and none could be taken: an edge between these two ids has
    /// exactly these two endpoints, and both of them already passed the pre-lock topology screen, so
    /// such an edge is attributable by construction. Taking the screen here instead would resolve
    /// through CognitiveIndex under the graph write lock, which is the lock ordering this class
    /// exists to never do.
    /// </summary>
    private bool AnyEdgeBetweenUnderLock(string tenantId, string sourceId, string targetId)
    {
        if (_outgoing.TryGetValue((tenantId, sourceId), out var fromSource))
            for (int i = 0; i < fromSource.Count; i++)
                if (fromSource[i].TargetId == targetId) return true;

        if (_incoming.TryGetValue((tenantId, sourceId), out var toSource))
            for (int i = 0; i < toSource.Count; i++)
                if (toSource[i].SourceId == targetId) return true;

        return false;
    }

    private void AddEdgeInternal(GraphEdge edge)
    {
        var srcKey = (edge.TenantId, edge.SourceId);
        var tgtKey = (edge.TenantId, edge.TargetId);

        // Remove existing edge with same source/target/relation to avoid duplicates
        if (_outgoing.TryGetValue(srcKey, out var outList))
            outList.RemoveAll(e => e.TargetId == edge.TargetId && e.Relation == edge.Relation);
        if (_incoming.TryGetValue(tgtKey, out var inList))
            inList.RemoveAll(e => e.SourceId == edge.SourceId && e.Relation == edge.Relation);

        if (!_outgoing.ContainsKey(srcKey))
            _outgoing[srcKey] = new();
        _outgoing[srcKey].Add(edge);

        if (!_incoming.ContainsKey(tgtKey))
            _incoming[tgtKey] = new();
        _incoming[tgtKey].Add(edge);
    }

    private static int RemoveMatching(Dictionary<(string Tenant, string Id), List<GraphEdge>> dict, (string, string) key, Func<GraphEdge, bool> predicate)
    {
        if (!dict.TryGetValue(key, out var list))
            return 0;
        int removed = list.RemoveAll(e => predicate(e));
        if (list.Count == 0)
            dict.Remove(key);
        return removed;
    }

    private static CognitiveEntryInfo ToEntryInfo(CognitiveEntry e) =>
        new(e.Id, e.Text, e.Ns, e.Category, e.LifecycleState);

    // Called under upgradeable read lock — upgrades to write only if loading needed.
    private void EnsureLoaded()
    {
        if (_loaded) return;

        _lock.EnterWriteLock();
        try
        {
            if (_loaded) return; // Double-check
            var globalEdges = _persistence.LoadGlobalEdges();
            foreach (var edge in globalEdges)
                AddEdgeInternal(edge);
            _loaded = true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    // Called when already holding write lock.
    private void EnsureLoadedUnderWrite()
    {
        if (_loaded) return;
        var globalEdges = _persistence.LoadGlobalEdges();
        foreach (var edge in globalEdges)
            AddEdgeInternal(edge);
        _loaded = true;
    }

    // Snapshot edge data (all tenants) and schedule save. MUST be called within write lock.
    private void ScheduleSaveEdges()
    {
        var snapshot = _outgoing.Values.SelectMany(l => l).ToList();
        _persistence.ScheduleSaveGlobalEdges(() => snapshot);
    }
}
