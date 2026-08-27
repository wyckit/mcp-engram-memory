using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Core.Services.Graph;

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
/// Locking strategy:
/// - Read-only methods use EnterUpgradeableReadLock, upgrading to write only if EnsureLoaded needs to load.
/// - Mutating methods use EnterWriteLock directly.
/// - Methods that need CognitiveIndex snapshot data under graph lock, then resolve entries outside
///   to avoid lock-ordering deadlocks.
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

    /// <summary>Create a directed edge between two entries. The edge's own TenantId scopes the partition.</summary>
    public string AddEdge(GraphEdge edge)
    {
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
            ScheduleSaveEdges();
            return edge.Relation == "cross_reference"
                ? $"Linked '{edge.SourceId}' <-> '{edge.TargetId}' (cross_reference, bidirectional)."
                : $"Linked '{edge.SourceId}' -> '{edge.TargetId}' ({edge.Relation}).";
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Create multiple edges in a single write lock acquisition.</summary>
    public int AddEdges(IEnumerable<GraphEdge> edges)
    {
        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            int count = 0;
            foreach (var edge in edges)
            {
                AddEdgeInternal(edge);
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
                ScheduleSaveEdges();
            }
            return count;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Remove edges between two entries within a tenant, optionally filtered by relation.</summary>
    public string RemoveEdges(string sourceId, string targetId, string? relation = null, string tenantId = "")
    {
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
                ScheduleSaveEdges();
            }

            return removed > 0
                ? $"Removed {removed} edge(s) between '{sourceId}' and '{targetId}'."
                : $"No edges found between '{sourceId}' and '{targetId}'.";
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Remove ALL edges referencing an entry within a tenant (cascade delete).</summary>
    public int RemoveAllEdgesForEntry(string id, string tenantId = "")
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
                ScheduleSaveEdges();
            }

            return removed;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Get directly connected entries within a tenant.</summary>
    public GetNeighborsResult GetNeighbors(string id, string? relation = null, string direction = "both", string tenantId = "")
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

    /// <summary>Multi-hop graph traversal via BFS, scoped to a tenant.</summary>
    public TraversalResult Traverse(string startId, int maxDepth = 2, string? relation = null,
        float minWeight = 0f, int maxResults = 20, string tenantId = "")
    {
        maxDepth = Math.Clamp(maxDepth, 1, 5);

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

    /// <summary>Get all edges for an entry within a tenant (both directions).</summary>
    public IReadOnlyList<GraphEdge> GetEdgesForEntry(string id, string tenantId = "")
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

    /// <summary>Get all edges with a 'contradicts' relation for entries in a namespace within a tenant.</summary>
    public IReadOnlyList<(GraphEdge Edge, CognitiveEntry? Source, CognitiveEntry? Target)> GetContradictions(string ns, string tenantId = "")
    {
        List<GraphEdge> contradictEdges;

        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            contradictEdges = _outgoing.Values
                .SelectMany(l => l)
                .Where(e => e.Relation == "contradicts" && e.TenantId == tenantId)
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

    /// <summary>Transfer all edges from one entry to another within a tenant (for merge operations). Returns count transferred.</summary>
    public int TransferEdges(string fromId, string toId, string tenantId = "")
    {
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
                ScheduleSaveEdges();
            }

            return transferred;
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ── Internals ──

    /// <summary>Resolve an entry id within a tenant: fast legacy locator for tenant "", tenant-scoped scan otherwise.</summary>
    private CognitiveEntry? ResolveEntry(string id, string tenantId)
        => tenantId.Length == 0 ? _index.Get(id) : _index.GetForTenant(id, tenantId);

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
