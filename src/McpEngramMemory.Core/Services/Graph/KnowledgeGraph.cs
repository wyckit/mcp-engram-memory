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
    }
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
/// two entries. Every method here that creates or moves an edge, and the traversal that walks
/// them, therefore consults <see cref="TopologyGuard"/> first and declines the ambiguous id. The
/// guard is enforced here rather than at each tool because it is ACL-blind and so needs no
/// principal: putting it at the boundary is what makes it impossible for a new writer to forget.
/// <see cref="RemoveAllEdgesForEntry"/> is the one deliberate exception — see its remarks.
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
    /// ordinary miss so it cannot be told apart from an endpoint that does not exist.
    /// </summary>
    public string AddEdge(GraphEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);

        // Guarded before the write lock, never inside it: the test resolves through CognitiveIndex,
        // which holds its own locks, and this class's rule is that index work happens outside the
        // graph lock so the two can never be acquired in opposite orders.
        var guard = TopologyGuard.ForSweep(_index, edge.TenantId);
        if (!guard.IsTopologySafe(edge.SourceId))
            return TopologyGuard.Unattributable(edge.SourceId);
        if (!guard.IsTopologySafe(edge.TargetId))
            return TopologyGuard.Unattributable(edge.TargetId);

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
            return edge.Relation == "cross_reference"
                ? $"Linked '{edge.SourceId}' <-> '{edge.TargetId}' (cross_reference, bidirectional)."
                : $"Linked '{edge.SourceId}' -> '{edge.TargetId}' ({edge.Relation}).";
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Create multiple edges in a single write lock acquisition. Returns the number actually
    /// written, which is what makes the count honest when an endpoint was declined.
    /// </summary>
    public int AddEdges(IEnumerable<GraphEdge> edges)
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
            if (guard.IsTopologySafe(edge.SourceId) && guard.IsTopologySafe(edge.TargetId))
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
    /// Deliberately NOT guarded here, unlike every other mutator on this class. Its sanctioned
    /// caller is <see cref="TopologyCascade"/>, which applies the identical
    /// <see cref="TopologyGuard"/> predicate once per sweep against a single namespace listing;
    /// re-testing here would re-list the tenant per swept entry and turn a namespace purge into one
    /// full store reload per entry. The guard is not weaker for living one frame up — it is the
    /// same predicate — but a new caller of this primitive must go through TopologyCascade rather
    /// than call it raw.
    /// </summary>
    public int RemoveAllEdgesForEntry(string id, string tenantId)
    {
        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            int removed = 0;
            var key = (tenantId, id);

            // Remove outgoing edges and their incoming references
            if (_outgoing.TryGetValue(key, out var outEdges))
            {
                foreach (var edge in outEdges)
                    RemoveMatching(_incoming, (tenantId, edge.TargetId), e => e.SourceId == id);
                removed += outEdges.Count;
                _outgoing.Remove(key);
            }

            // Remove incoming edges and their outgoing references
            if (_incoming.TryGetValue(key, out var inEdges))
            {
                foreach (var edge in inEdges)
                    RemoveMatching(_outgoing, (tenantId, edge.SourceId), e => e.TargetId == id);
                removed += inEdges.Count;
                _incoming.Remove(key);
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
    /// Unguarded here, unlike <see cref="Traverse"/>, and the asymmetry is about cost rather than
    /// principle: this is called once per search hit inside the recall expansion loops, and each of
    /// those already holds a <see cref="TopologyGuard.Sweep"/> for its whole result set. Building a
    /// second sweep per call would re-list the tenant — and so reload the store — once per hit.
    /// One hop is also containable from outside: the caller sees each neighbor's own id and
    /// namespace and can drop an unattributable one, which a multi-hop walk cannot do because by
    /// then it has already used the shared node to find things.
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

        // Resolve entries outside graph lock (CognitiveIndex has its own lock), scoped to the tenant.
        var neighbors = new List<NeighborResult>();
        foreach (var (edge, resolveId) in edgesToResolve)
        {
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
                    // enqueue. An ambiguous target is a node two entries share, so neither the
                    // edge reaching it, the entry it resolves to, nor anything beyond it is
                    // attributable to the entry this walk is about. Not marked visited: it was
                    // never visited, and the sweep memoizes so a second path costs nothing.
                    if (!guard.IsTopologySafe(edge.TargetId)) continue;

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

    /// <summary>Get all edges for an entry within a tenant (both directions). Pass "" for the legacy partition.</summary>
    public IReadOnlyList<GraphEdge> GetEdgesForEntry(string id, string tenantId)
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

    /// <summary>Get all edges with a 'contradicts' relation for entries in a namespace within a tenant. Pass "" for the legacy partition.</summary>
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

        // Resolve entries outside graph lock (scoped to tenant), filter to namespace
        var results = new List<(GraphEdge, CognitiveEntry?, CognitiveEntry?)>();
        foreach (var edge in contradictEdges)
        {
            var source = ResolveEntry(edge.SourceId, tenantId);
            var target = ResolveEntry(edge.TargetId, tenantId);

            // Include if either entry is in the requested namespace
            if ((source?.Ns == ns) || (target?.Ns == ns))
                results.Add((edge, source, target));
        }

        return results;
    }

    /// <summary>Get all edges across every tenant (for persistence).</summary>
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

    /// <summary>Get all edges belonging to one tenant (for per-tenant graph derivations, e.g. diffusion).</summary>
    public IReadOnlyList<GraphEdge> GetAllEdges(string tenantId)
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
    /// adjacency — so either endpoint being ambiguous refuses the transfer outright rather than
    /// moving what it can. Returning 0 is what a merge of two entries with no edges already
    /// reports, so the caller's own reply stays truthful without becoming a signal.
    /// </summary>
    public int TransferEdges(string fromId, string toId, string tenantId)
    {
        var guard = TopologyGuard.ForSweep(_index, tenantId);
        if (!guard.IsTopologySafe(fromId) || !guard.IsTopologySafe(toId))
            return 0;

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
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
