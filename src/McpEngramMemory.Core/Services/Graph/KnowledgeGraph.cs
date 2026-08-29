using System.Runtime.InteropServices;
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

        /// <summary>
        /// The tenant's attribution revision as of the instant this sweep's view was fixed —
        /// see <see cref="CognitiveIndex.AttributionRevisionFor"/>.
        ///
        /// The one thing a writer holding a lock can use to find out that this sweep's answers have
        /// gone stale. Every judgement here resolves through <see cref="CognitiveIndex"/>, which
        /// takes its own locks, so re-judging under a write lock would invert this codebase's lock
        /// order; comparing this number does not, because reading it is a single lock-free atomic
        /// read of a per-tenant counter. Compared, never interpreted: any difference means some id
        /// in this tenant crossed the ambiguity boundary after the sweep was built, so whatever the
        /// sweep admitted must be refused rather than written.
        /// </summary>
        public long AttributionRevision { get; }

        internal Sweep(CognitiveIndex index, string tenantId)
        {
            ArgumentNullException.ThrowIfNull(index);
            _index = index;
            _tenantId = tenantId;

            // THE ORDER OF THESE THREE LINES IS THE CONTRACT, in both directions.
            //
            // Warm BEFORE capturing. Listing is what materializes persisted partitions, and a lazy
            // load tracks every row it materializes — so a cold store's own load bumps the counter
            // once per id that is already ambiguous on disk. Capturing after the warm keeps that
            // from reading as somebody else's concurrent crossing, which would refuse the first
            // write after start-up for no reason at all.
            //
            // Capture BEFORE listing. The listing is the snapshot every judgement below is made
            // against, and a crossing into a namespace the snapshot does not name is invisible to
            // that judgement. Read the counter after the listing and a crossing landing between the
            // two would be seen by neither — the sweep would call the id attributable and a writer
            // re-reading the counter would find nothing had moved. Read it first and every crossing
            // this sweep could be wrong about is one the counter has already recorded.
            index.EnsureAllNamespacesLoaded();
            AttributionRevision = index.AttributionRevisionFor(tenantId);
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
            => edge is not null && IsEdgeUsable(edge.SourceId, edge.TargetId);

        /// <summary>
        /// <inheritdoc cref="IsEdgeUsable(GraphEdge)" path="/summary/node()"/>
        ///
        /// This overload is the SAME edge-level test against an edge that has not been built yet —
        /// it takes both endpoints, never one, so there is still no way to ask about half an edge.
        /// It exists for a producer that ranks candidate pairs before deciding which of them are
        /// worth materializing: constructing a <see cref="GraphEdge"/> merely to ask whether it is
        /// admissible allocates an object (and its metadata dictionary) per pair examined, and the
        /// auto-link scan examines every pair in its window rather than a short prefix of them.
        /// </summary>
        public bool IsEdgeUsable(string sourceId, string targetId)
            => IsTopologySafe(sourceId) && IsTopologySafe(targetId);

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
/// <see cref="TopologyGuard.Sweep.IsEdgeUsable(GraphEdge)"/>: an edge is usable only when BOTH of its
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
/// Tenant keys are NORMALIZED, everywhere, and that is an invariant this class has to maintain by
/// hand: <see cref="GraphEdge"/> normalizes its own <see cref="GraphEdge.TenantId"/> in its
/// constructor, so every key ever WRITTEN into the adjacency dictionaries is normalized, while a
/// tenant id arriving as a method argument is whatever the principal supplied. Comparing the two
/// forms is a split-brain that makes a tenant's own edges look absent to it — the shipped stdio
/// host normalizes at PrincipalContext, but <c>IPrincipalContext</c> is an extension point with no
/// normalization of its own, so a host returning a padded claim value would write under
/// <c>"acme"</c> and read under <c>"acme "</c>. Every public method here therefore normalizes its
/// tenant argument as its FIRST statement, exactly as <see cref="Intelligence.ClusterManager"/>
/// already does for its own map.
///
/// Locking strategy:
/// - Read-only methods use EnterUpgradeableReadLock, upgrading to write only if EnsureLoaded needs to load.
/// - Mutating methods use EnterWriteLock directly.
/// - Methods that need CognitiveIndex snapshot data under graph lock, then resolve entries outside
///   to avoid lock-ordering deadlocks. The topology guard resolves through CognitiveIndex too, so
///   it runs BEFORE the graph lock is taken, never inside it.
/// - The one thing a write path does consult inside the lock is
///   <see cref="TopologyGuard.Sweep.AttributionRevision"/> against
///   <see cref="CognitiveIndex.AttributionRevisionFor"/>, through the single helper
///   <see cref="AttributionMovedSince"/>. That is a comparison, not a resolution: it reads a
///   per-tenant counter with no lock at all, so it cannot invert the order above, and it is what
///   makes an admission decided outside the lock safe to act on inside it. EVERY mutator that
///   admits through a guard built outside its own write lock consults it — all five of them
///   (<see cref="TryAddEdge"/>, <see cref="AddEdges"/>, <see cref="RemoveEdges"/>,
///   <see cref="RemoveAllEdgesForEntry(string, string, TopologyGuard.Sweep)"/>,
///   <see cref="TransferEdges"/>) — and each fails closed with its own existing no-op reply. Naming
///   the complete set here is deliberate: the previous round wired the check into the two ADD paths
///   and left the three remove/transfer paths, which have strictly WIDER admission-to-mutation
///   windows, consulting nothing. A new mutator that takes a sweep before the lock and does not
///   call the helper reopens that hole.
/// - Nothing O(edges) runs inside the write lock. Persistence is scheduled by handing the storage
///   layer a method group (<see cref="SnapshotEdgesForSave"/>) that snapshots under the READ lock
///   on the debounce thread; see the note there for why deferring is also strictly more correct.
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

    /// <summary>
    /// Monotonic topology revision for one tenant (0 if the tenant has no edges yet).
    ///
    /// Normalized on the way in for the same reason every other tenant argument is: the bumps come
    /// from <see cref="BumpTenant"/>, whose callers all key by the normalized tenant, so a reader
    /// passing an unnormalized spelling would watch a counter nothing ever moves and serve a stale
    /// derivation forever.
    /// </summary>
    public long RevisionFor(string tenantId)
        => _tenantRevisions.TryGetValue(Tenancy.Normalize(tenantId), out var r) ? r : 0;

    // Every caller passes an already-normalized tenant — either GraphEdge.TenantId, which the edge
    // constructor normalized, or a method argument this class normalized at its entry point. The
    // null coalesce is the belt-and-braces floor, not the normalization.
    private void BumpTenant(string tenantId)
        => _tenantRevisions.AddOrUpdate(tenantId ?? string.Empty, 1L, static (_, v) => v + 1);

    /// <summary>
    /// THE ADMISSION-TO-MUTATION CHECK, in one place so a mutator cannot half-have it.
    ///
    /// True when some id in <paramref name="tenantId"/> crossed the ambiguity boundary after
    /// <paramref name="guard"/> fixed its view — meaning whatever the guard admitted outside the
    /// write lock must be refused rather than acted on inside it.
    ///
    /// Every mutator here resolves attribution BEFORE taking the write lock, deliberately: the
    /// guard resolves through <see cref="CognitiveIndex"/>, which holds its own locks, and index
    /// work under the graph lock is the lock-order inversion this class exists to avoid. That
    /// leaves a gap, and the gap is writable — inserting a same-id twin is an ordinary entry write
    /// that takes none of this class's locks, creates no edge and moves no graph revision, so
    /// nothing in the graph observes it. Re-running the guard here is not available; re-reading
    /// this counter is, because it is a single lock-free read of a per-tenant
    /// ConcurrentDictionary: no index lock, no allocation, no inversion.
    ///
    /// Conservative in the safe direction, deliberately: a crossing anywhere in the tenant refuses
    /// the operation even when it named neither endpoint. A refused write is a write that did not
    /// happen and the caller may simply retry; a mutation applied against stale attribution
    /// rewrites topology on a node two entries answer to, which is the failure this whole mechanism
    /// exists to prevent.
    ///
    /// Call it after EnsureLoadedUnderWrite and before the first mutation — as late as the check
    /// can be while still preceding anything that would have to be rolled back.
    /// </summary>
    private bool AttributionMovedSince(TopologyGuard.Sweep guard, string tenantId)
        => _index.AttributionRevisionFor(tenantId) != guard.AttributionRevision;

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
                return EdgeCountUnderLock();
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

            // ADMISSION IS NOT ATTRIBUTION AT THE INSTANT OF THE WRITE, and the gap is writable —
            // see AttributionMovedSince for why this comparison is the only re-check the lock order
            // permits, and why it is conservative in the safe direction.
            if (AttributionMovedSince(guard, edge.TenantId))
            {
                // The same sentence a genuine miss produces, for the same reason as the refusal
                // above: a caller that could tell "attribution moved under you" apart from "no such
                // entry" would have an oracle for twins it was never shown.
                reply = TopologyGuard.Unattributable(edge.SourceId);
                return false;
            }

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
    ///
    /// Returns 0 without writing anything when attribution moved in any tenant of the batch between
    /// admission and the write lock. That is deliberately indistinguishable from a batch whose every
    /// edge the graph declined: the count stays "what the graph accepted", and no caller-visible
    /// signal says which of the two happened.
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

        // Allocated BEFORE the lock, not inside it. It is a per-batch scratch set whose size is the
        // number of tenants in the batch (almost always one), and the write lock is the one place
        // in this class where an allocation is paid for by every reader in the process rather than
        // by the caller. Ordinal, matching `sweeps`, so the two agree about tenant identity.
        var touchedTenants = new HashSet<string>(StringComparer.Ordinal);

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();

            // THE ADMISSION-TO-MUTATION GAP, closed the only way the lock order allows — see
            // AttributionMovedSince. One lock-free atomic read per tenant in the batch: no index
            // lock, no allocation, no inversion.
            //
            // Refuses the WHOLE batch rather than the moved tenant's share of it. A batch is one
            // caller's proposal; splitting it would report a partial write whose composition
            // depends on which tenant raced, and every caller of this method already treats the
            // returned count as "what the graph accepted" and retries the rest later. The
            // enumeration itself allocates nothing — Dictionary's enumerator is a struct.
            foreach (var (tenant, sweep) in sweeps)
            {
                if (AttributionMovedSince(sweep, tenant))
                    return 0;
            }

            int count = 0;
            foreach (var edge in admitted)
            {
                // THE INVARIANT FOR THIS WHOLE LOOP, not just for the line under it: no iteration
                // allocates, and no iteration walks an adjacency list it does not have to.
                //
                // The mode check is the point of the mode — the condition is evaluated against the
                // graph as it is at the instant of the write, holding the write lock, so nothing
                // can slip between the test and the mutation. A caller's own pre-filter cannot
                // achieve that however carefully it is written: it reads a snapshot, and the graph
                // stays mutable. AnyEdgeBetweenUnderLock walks the two adjacency lists in place,
                // with no delegate and no closure.
                //
                // AddEdgeInternal's same-relation dedup is then SKIPPED on this path, and that is a
                // deduction rather than an optimism: AnyEdgeBetweenUnderLock just returned false,
                // so no edge in _outgoing[(t, source)] has TargetId == target — which is the exact
                // first clause of the dedup's outgoing predicate — and since the two adjacency
                // indexes mirror each other, no source->target edge can sit in _incoming[(t,
                // target)] either. Auto-link is the only caller that uses this mode and it was
                // paying two full O(degree) delegate-driven scans per edge for a guaranteed zero
                // removals, on a node whose degree auto-link itself is manufacturing.
                //
                // That deduction rests on the mirror invariant, so it is only sound while the
                // invariant holds — FindAdjacencyMirrorViolations is the seam that pins it.
                bool unlinkedMode = mode == EdgeAddMode.OnlyIfUnlinked;
                if (unlinkedMode && AnyEdgeBetweenUnderLock(edge.TenantId, edge.SourceId, edge.TargetId))
                    continue;

                AddEdgeInternal(edge, dedupeSameRelation: !unlinkedMode);
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
        // Adjacency is keyed by the normalized tenant because GraphEdge normalized it on the way
        // in, so a removal keyed by the raw argument would probe a key that cannot exist.
        tenantId = Tenancy.Normalize(tenantId);

        var guard = TopologyGuard.ForSweep(_index, tenantId);
        if (!guard.IsTopologySafe(sourceId) || !guard.IsTopologySafe(targetId))
            return NoEdgesBetween(sourceId, targetId);

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();

            // Same gap, same close, same failure reply as the add paths — see AttributionMovedSince.
            // A removal is a topology mutation like any other: it rewrites the adjacency list of a
            // node that may have become shared between the screen above and this lock, and deleting
            // somebody else's edge off it is exactly as wrong as adding one. Reuses the no-such-edge
            // reply verbatim so "nothing to remove", "declined to guess" and "attribution moved
            // under you" stay one answer.
            if (AttributionMovedSince(guard, tenantId))
                return NoEdgesBetween(sourceId, targetId);

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

        // See RemoveEdges: adjacency keys are normalized because GraphEdge normalized them.
        tenantId = Tenancy.Normalize(tenantId);

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

            // The WIDEST of the admission-to-mutation windows in this class, and the last one to
            // get the check: removableOut/removableIn were judged against CognitiveIndex after the
            // read lock was released and before this write lock was taken, so every endpoint they
            // name — including the far ones no argument mentions — was judged in a window that is
            // linear in this node's degree. See AttributionMovedSince. Fails closed the way the
            // method already fails: 0, nothing deleted, which is what a node with no attributable
            // incident edges already reports.
            if (AttributionMovedSince(guard, tenantId))
                return 0;

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
    /// Every returned edge has passed <see cref="TopologyGuard.Sweep.IsEdgeUsable(GraphEdge)"/>, which covers
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
        // Adjacency is keyed by the normalized tenant (GraphEdge normalizes on construction), so a
        // read keyed by the raw argument would miss every edge the same tenant wrote.
        tenantId = Tenancy.Normalize(tenantId);

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

        // See GetNeighbors: the adjacency snapshot below filters on Key.Tenant, which is normalized.
        tenantId = Tenancy.Normalize(tenantId);

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
        // Normalized here as well as in the delegate below, so the class-wide rule ("every public
        // method normalizes its tenant argument first") holds by inspection rather than by tracing
        // which helper happens to do it.
        tenantId = Tenancy.Normalize(tenantId);

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
        // See GetNeighbors — the dictionary probe below is the lookup that must match the key the
        // write used, and the write used GraphEdge.TenantId, which is normalized.
        tenantId = Tenancy.Normalize(tenantId);

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
        // See GetNeighbors: the Key.Tenant filter below compares against normalized keys.
        tenantId = Tenancy.Normalize(tenantId);

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
        // See GetEdgesForEntry.
        tenantId = Tenancy.Normalize(tenantId);

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
        // See GetNeighbors: the Key.Tenant filter below compares against normalized keys.
        tenantId = Tenancy.Normalize(tenantId);

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
        // See RemoveEdges: every dictionary probe below is keyed by this string, and every key that
        // exists was written from a normalized GraphEdge.TenantId.
        tenantId = Tenancy.Normalize(tenantId);

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

            // TWO DIFFERENT STALENESS PROBLEMS, and neither check sees the other's case.
            //
            // The counter compare catches attribution MOVING under an endpoint this sweep already
            // judged. That is the case IsEdgeKnownUsable structurally cannot see: an id the memo
            // judged safe is returned safe forever, so a twin planted after the snapshot leaves the
            // memo saying "safe" while the node has become shared. This method's window is the
            // widest of the class — the incident snapshot is taken under the upgradeable read lock
            // and then every incident edge is walked through CognitiveIndex, one partition read per
            // far endpoint — so for a high-degree node it is milliseconds wide, not nanoseconds.
            // See AttributionMovedSince.
            if (AttributionMovedSince(guard, tenantId))
                return 0;

            // The IsEdgeKnownUsable walk catches the other case: an edge that ARRIVED between the
            // snapshot and this lock, naming an endpoint the sweep has never judged at all. Judging
            // it here would take the index's locks under the graph's, so an unknown endpoint aborts
            // instead. Both fail closed the same way: 0, nothing mutated.
            foreach (var dict in new[] { _outgoing, _incoming })
                if (dict.TryGetValue((tenantId, fromId), out var current))
                    foreach (var edge in current)
                        if (!guard.IsEdgeKnownUsable(edge))
                            return 0;

            int transferred = 0;
            var fromKey = (tenantId, fromId);

            // THE MIRROR RULE FOR BOTH BRANCHES: _outgoing and _incoming are two halves of one
            // structure, and dropping a node's list from one half obliges this method to strip that
            // node out of the other half FIRST — for every edge in the list, whether or not the
            // edge is transferred. Both loops below therefore delete the far endpoint's mirror
            // before they decide anything else.
            //
            // The outgoing branch used to do this only implicitly, via AddEdgeInternal's dedup,
            // which matches on SourceId == toId and so could never remove a from -> X entry. It
            // then dropped _outgoing[(t, from)] wholesale, leaving from -> X alive in
            // _incoming[(t, X)] with no counterpart anywhere. That phantom is observable three
            // ways, from one merge and no concurrency: get_neighbors(X, "incoming") reports a
            // predecessor that no longer exists; EdgeCount, GetAllEdges and the edge save all read
            // _outgoing only, so diagnostics and persistence disagree with what the tool returns
            // and a restart answers differently from the live process; and AutoLinkScanner's
            // HasAnyEdgeBetween unions _incoming, so the pair (X, from) counts as already linked
            // forever — permanent, deterministic starvation if `from` is ever re-created under the
            // same id, which is the documented upsert-by-id workflow.
            //
            // The self-referential skips leak the same way and are handled by the same rule rather
            // than by `continue`-ing past everything: from -> to must vanish, not survive in
            // _incoming[(t, to)], because the edge it would become (to -> to) is exactly what the
            // skip refuses to create.

            // Transfer outgoing edges: fromId -> X becomes toId -> X
            if (_outgoing.TryGetValue(fromKey, out var outEdges))
            {
                foreach (var edge in outEdges.ToList())
                {
                    // The mirror first: _outgoing[(t, from)] is removed wholesale below, so any
                    // from -> X still sitting in _incoming[(t, X)] would be a phantom. Matched on
                    // (source, relation) because AddEdgeInternal keeps (source, target, relation)
                    // unique, so this is the one edge meant.
                    RemoveMatching(_incoming, (tenantId, edge.TargetId),
                        e => e.SourceId == fromId && e.Relation == edge.Relation);

                    // Skip self-referential edges that would result from the transfer. The mirror
                    // above has already been dropped, so this edge is gone rather than orphaned.
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
                    // The mirror first, symmetrically: remove the old outgoing reference
                    // (X -> fromId) from the source's outgoing list. Hoisted above the self-edge
                    // skip because a to -> from edge is dropped from _incoming[(t, from)] below and
                    // would otherwise survive in _outgoing[(t, to)] as the same phantom.
                    RemoveMatching(_outgoing, (tenantId, edge.SourceId),
                        e => e.TargetId == fromId && e.Relation == edge.Relation);

                    if (edge.SourceId == toId) continue;

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

    /// <summary>
    /// Publish one edge into both halves of the adjacency structure. Caller must hold the write
    /// lock.
    ///
    /// ALLOCATION-FREE ON THE PUBLISH PATH, and that is the point of the shape rather than a happy
    /// accident: this runs once per edge inside a batch write lock that blocks every reader in the
    /// process, and the previous form allocated a display class plus one or two
    /// <c>Predicate&lt;GraphEdge&gt;</c> per call (the <c>RemoveAll</c> lambdas capture
    /// <paramref name="edge"/>, so Roslyn hoists it unconditionally at method entry) and probed
    /// each dictionary three times. A thousand-edge batch into a node of degree 200 paid ~3,000
    /// closure/delegate allocations and ~8,000 dictionary probes for work that needs neither.
    ///
    /// <paramref name="dedupeSameRelation"/> false is for a caller that has ALREADY established,
    /// under this same lock, that no edge runs between the endpoints — see the deduction written
    /// out in <see cref="AddEdges"/>. It skips two O(degree) scans that are provably empty.
    /// </summary>
    private void AddEdgeInternal(GraphEdge edge, bool dedupeSameRelation = true)
    {
        var srcKey = (edge.TenantId, edge.SourceId);
        var tgtKey = (edge.TenantId, edge.TargetId);

        // Remove existing edge with same source/target/relation to avoid duplicates. Compacted in
        // place rather than with RemoveAll: same O(degree) walk, no delegate call per element and
        // no closure. Deliberately leaves an emptied list published — RemoveMatching drops the key,
        // this never has, and callers rely on the difference nowhere but would notice the churn.
        if (dedupeSameRelation)
        {
            if (_outgoing.TryGetValue(srcKey, out var existingOut))
                CompactOut(existingOut, edge.TargetId, edge.Relation);
            if (_incoming.TryGetValue(tgtKey, out var existingIn))
                CompactIn(existingIn, edge.SourceId, edge.Relation);
        }

        // One hashed probe per dictionary instead of three. The ref is into the bucket array, so
        // nothing may touch that dictionary between taking it and writing through it — nothing
        // here does, and the two dictionaries are distinct instances.
        ref var outList = ref CollectionsMarshal.GetValueRefOrAddDefault(_outgoing, srcKey, out _);
        (outList ??= new List<GraphEdge>()).Add(edge);

        ref var inList = ref CollectionsMarshal.GetValueRefOrAddDefault(_incoming, tgtKey, out _);
        (inList ??= new List<GraphEdge>()).Add(edge);
    }

    /// <summary>Drop every edge to <paramref name="targetId"/> under <paramref name="relation"/>,
    /// in place. Survivors are shifted down and the tail trimmed — no allocation, no delegate.</summary>
    private static void CompactOut(List<GraphEdge> list, string targetId, string relation)
    {
        int write = 0;
        for (int read = 0; read < list.Count; read++)
        {
            var e = list[read];
            if (e.TargetId == targetId && e.Relation == relation) continue;
            list[write++] = e;
        }
        if (write < list.Count)
            list.RemoveRange(write, list.Count - write);
    }

    /// <inheritdoc cref="CompactOut"/>
    private static void CompactIn(List<GraphEdge> list, string sourceId, string relation)
    {
        int write = 0;
        for (int read = 0; read < list.Count; read++)
        {
            var e = list[read];
            if (e.SourceId == sourceId && e.Relation == relation) continue;
            list[write++] = e;
        }
        if (write < list.Count)
            list.RemoveRange(write, list.Count - write);
    }

    // Predicate rather than Func: List.RemoveAll wants a Predicate, so a Func parameter forced an
    // extra delegate allocation per call, on top of the caller's closure, inside a write lock.
    private static int RemoveMatching(Dictionary<(string Tenant, string Id), List<GraphEdge>> dict, (string, string) key, Predicate<GraphEdge> predicate)
    {
        if (!dict.TryGetValue(key, out var list))
            return 0;
        int removed = list.RemoveAll(predicate);
        if (list.Count == 0)
            dict.Remove(key);
        return removed;
    }

    /// <summary>Total stored edges. Caller must hold at least a read lock.</summary>
    private int EdgeCountUnderLock()
    {
        int total = 0;
        // Dictionary.ValueCollection's enumerator is a struct and this foreach binds it by its
        // concrete type, so the walk boxes nothing — unlike the LINQ Sum it replaces, which boxed
        // one enumerator per adjacency bucket and allocated a delegate.
        foreach (var list in _outgoing.Values)
            total += list.Count;
        return total;
    }

    /// <summary>
    /// TEST SEAM. Every violation of the mirror invariant currently in the graph: each edge in
    /// <c>_outgoing[(t, src)]</c> must also appear in <c>_incoming[(t, tgt)]</c> and vice versa.
    /// Empty means the two halves agree.
    ///
    /// It exists because this is the THIRD distinct way the two indexes have been allowed to drift,
    /// and each time the suite could not see it: every consistency assertion in the tests compared
    /// two views that both read <c>_outgoing</c> (<c>GetStoredEdges</c> against <c>GetAllEdges</c>),
    /// which is structurally incapable of witnessing an <c>_incoming</c>-only phantom.
    ///
    /// NOT called from the mutators, deliberately. The check is O(E) and running it inside each
    /// write lock would put back exactly the cost this class just took out of the edge save — even
    /// under a DEBUG-only conditional, since the tests are the Debug build. A caller asks for it
    /// after the operation it is pinning, under an ordinary read lock, on a fixture-sized graph.
    /// </summary>
    internal IReadOnlyList<string> FindAdjacencyMirrorViolations()
    {
        _lock.EnterReadLock();
        try
        {
            var violations = new List<string>();

            foreach (var (key, list) in _outgoing)
                foreach (var edge in list)
                    if (!Mirrored(_incoming, (key.Tenant, edge.TargetId), edge))
                        violations.Add(
                            $"_outgoing[({key.Tenant}, {key.Id})] holds '{edge.SourceId}' -> " +
                            $"'{edge.TargetId}' ({edge.Relation}) with no counterpart in _incoming.");

            foreach (var (key, list) in _incoming)
                foreach (var edge in list)
                    if (!Mirrored(_outgoing, (key.Tenant, edge.SourceId), edge))
                        violations.Add(
                            $"_incoming[({key.Tenant}, {key.Id})] holds '{edge.SourceId}' -> " +
                            $"'{edge.TargetId}' ({edge.Relation}) with no counterpart in _outgoing.");

            return violations;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>True when <paramref name="other"/> holds the same edge under
    /// <paramref name="key"/>. Caller must hold at least a read lock.</summary>
    private static bool Mirrored(
        Dictionary<(string Tenant, string Id), List<GraphEdge>> other,
        (string, string) key, GraphEdge edge)
    {
        if (!other.TryGetValue(key, out var list)) return false;
        foreach (var candidate in list)
            if (candidate.SourceId == edge.SourceId
                && candidate.TargetId == edge.TargetId
                && candidate.Relation == edge.Relation)
                return true;
        return false;
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

    /// <summary>
    /// Schedule a debounced save of the whole cross-tenant edge set. Called from within the write
    /// lock; it takes NO snapshot there.
    ///
    /// What it hands over is a method group, so the O(E) copy happens on the debounce thread under
    /// this class's read lock instead of inside the exclusive write lock. That matters because the
    /// eager form ran on EVERY mutation and blocked every reader in the process — recall's graph
    /// expansion, get_neighbors, traverse_graph, the diffusion basis build — behind a memcpy of the
    /// entire graph. A background auto-link sweep is 40 such batches; at 200,000 edges that was
    /// ~130 MB of churn per sweep, most of it large-object-heap, plus one boxed enumerator per
    /// adjacency bucket from the LINQ chain.
    ///
    /// And almost all of it was discarded: <c>ScheduleSaveGlobalEdges</c> debounces by DISPOSING
    /// the pending timer and OVERWRITING the pending provider, so only the last proposal in a
    /// window is ever serialized. Thirty-nine of those forty snapshots never reached storage.
    ///
    /// DEFERRING IS NOT A WEAKER SAVE. "edges" is a full-replace blob written from whatever the
    /// provider returns, and the debounce had already collapsed the window to one write, so
    /// serializing the graph as of the debounce firing is a LATER and equally valid state rather
    /// than a lost one.
    ///
    /// SAFE AGAINST THE PERSISTENCE CONTRACT, which is the part worth checking before adopting it.
    /// Every provider invocation — the timer callback and <c>Flush</c>, in all three providers —
    /// happens AFTER the storage layer's own timer lock has been released, so a writer that is
    /// inside the graph write lock calling <c>ScheduleSaveGlobalEdges</c> (graph lock, then timer
    /// lock) can never be waiting on a debounce thread that is inside the timer lock waiting on the
    /// graph lock. The provider also takes no CognitiveIndex lock, so it cannot invert this class's
    /// graph/index order either. The provider's older doc line asking for "a pre-captured snapshot
    /// (no lock re-entry)" is satisfied: the snapshot is captured under a lock this thread does not
    /// hold, not re-entered under one it does.
    /// </summary>
    private void ScheduleSaveEdges() => _persistence.ScheduleSaveGlobalEdges(SnapshotEdgesForSave);

    /// <summary>
    /// The edge snapshot handed to persistence, taken on the debounce thread under the read lock.
    ///
    /// Sized exactly before it is filled and walked with nested index loops over the value
    /// collection's struct enumerator, so it allocates one right-sized array and nothing else — the
    /// <c>SelectMany(...).ToList()</c> it replaces enumerated a non-<c>ICollection</c> source and
    /// doubled its way up, leaving ~2E references' worth of intermediate arrays behind with the
    /// last several on the large object heap.
    ///
    /// It deliberately does NOT call EnsureLoaded, and cannot need to: the only thing that
    /// schedules a save is a mutator that already ran EnsureLoadedUnderWrite inside its own write
    /// lock, and <c>_loaded</c> never goes back to false — so there is no interleaving in which
    /// this writes an empty in-memory graph over a populated store.
    /// </summary>
    private List<GraphEdge> SnapshotEdgesForSave()
    {
        _lock.EnterReadLock();
        try
        {
            var snapshot = new List<GraphEdge>(EdgeCountUnderLock());
            foreach (var list in _outgoing.Values)
                for (int i = 0; i < list.Count; i++)
                    snapshot.Add(list[i]);
            return snapshot;
        }
        finally { _lock.ExitReadLock(); }
    }
}
