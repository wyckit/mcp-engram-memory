using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Core.Services.Graph;

/// <summary>
/// In-memory knowledge graph using adjacency lists for directed edges between cognitive entries.
///
/// Locking strategy:
/// - Read-only methods use EnterUpgradeableReadLock, upgrading to write only if EnsureLoaded needs to load.
/// - Mutating methods use EnterWriteLock directly.
/// - Methods that need CognitiveIndex snapshot data under graph lock, then resolve entries outside
///   to avoid lock-ordering deadlocks.
/// </summary>
public sealed class KnowledgeGraph
{
    // outgoing[sourceId] = list of edges from sourceId
    private readonly Dictionary<string, List<GraphEdge>> _outgoing = new();
    // incoming[targetId] = list of edges to targetId
    private readonly Dictionary<string, List<GraphEdge>> _incoming = new();
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

    /// <summary>Total number of edges in the graph.</summary>
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

    /// <summary>Create a directed edge between two entries.</summary>
    public string AddEdge(GraphEdge edge)
    {
        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            AddEdgeInternal(edge);

            // Auto-create reverse edge for cross_reference
            if (edge.Relation == "cross_reference")
            {
                var reverse = new GraphEdge(edge.TargetId, edge.SourceId, "cross_reference",
                    edge.Weight, edge.Metadata.Count > 0 ? new Dictionary<string, string>(edge.Metadata) : null);
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
                        edge.Weight, edge.Metadata.Count > 0 ? new Dictionary<string, string>(edge.Metadata) : null);
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

    /// <summary>Remove edges between two entries, optionally filtered by relation.</summary>
    public string RemoveEdges(string sourceId, string targetId, string? relation = null)
    {
        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            int removed = 0;
            removed += RemoveMatching(_outgoing, sourceId, e => e.TargetId == targetId && (relation == null || e.Relation == relation));
            removed += RemoveMatching(_incoming, targetId, e => e.SourceId == sourceId && (relation == null || e.Relation == relation));

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

    /// <summary>Remove ALL edges referencing an entry (cascade delete).</summary>
    public int RemoveAllEdgesForEntry(string id)
    {
        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            int removed = 0;

            // Remove outgoing edges and their incoming references
            if (_outgoing.TryGetValue(id, out var outEdges))
            {
                foreach (var edge in outEdges)
                    RemoveMatching(_incoming, edge.TargetId, e => e.SourceId == id);
                removed += outEdges.Count;
                _outgoing.Remove(id);
            }

            // Remove incoming edges and their outgoing references
            if (_incoming.TryGetValue(id, out var inEdges))
            {
                foreach (var edge in inEdges)
                    RemoveMatching(_outgoing, edge.SourceId, e => e.TargetId == id);
                removed += inEdges.Count;
                _incoming.Remove(id);
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

    /// <summary>Get directly connected entries.</summary>
    public GetNeighborsResult GetNeighbors(string id, string? relation = null, string direction = "both")
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
                if (_outgoing.TryGetValue(id, out var outEdges))
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
                if (_incoming.TryGetValue(id, out var inEdges))
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

        // Resolve entries outside graph lock (CognitiveIndex has its own lock)
        var neighbors = new List<NeighborResult>();
        foreach (var (edge, resolveId) in edgesToResolve)
        {
            var entry = _index.Get(resolveId);
            if (entry is not null)
                neighbors.Add(new NeighborResult(edge, ToEntryInfo(entry)));
        }

        return new GetNeighborsResult(id, neighbors);
    }

    /// <summary>Multi-hop graph traversal via BFS.</summary>
    public TraversalResult Traverse(string startId, int maxDepth = 2, string? relation = null,
        float minWeight = 0f, int maxResults = 20)
    {
        maxDepth = Math.Clamp(maxDepth, 1, 5);

        // Snapshot the graph adjacency data under the graph lock,
        // then resolve entries outside the lock to avoid lock-ordering issues.
        Dictionary<string, List<GraphEdge>> outgoingSnapshot;
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            // Shallow copy of adjacency lists (edges are immutable)
            outgoingSnapshot = _outgoing.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        // BFS on snapshot, resolving entries via CognitiveIndex (its own lock)
        var visited = new HashSet<string>();
        var queue = new Queue<(string id, int depth)>();
        var resultEntries = new List<CognitiveEntryInfo>();
        var resultEdges = new List<GraphEdge>();

        queue.Enqueue((startId, 0));
        visited.Add(startId);

        var startEntry = _index.Get(startId);
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

                    var entry = _index.Get(edge.TargetId);
                    if (entry is not null)
                        resultEntries.Add(ToEntryInfo(entry));

                    if (resultEntries.Count < maxResults)
                        queue.Enqueue((edge.TargetId, depth + 1));
                }
            }
        }

        return new TraversalResult(startId, resultEntries, resultEdges);
    }

    /// <summary>Get all edges for an entry (both directions).</summary>
    public IReadOnlyList<GraphEdge> GetEdgesForEntry(string id)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            var edges = new List<GraphEdge>();
            if (_outgoing.TryGetValue(id, out var outEdges))
                edges.AddRange(outEdges);
            if (_incoming.TryGetValue(id, out var inEdges))
                edges.AddRange(inEdges);
            return edges;
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>Get all edges with a 'contradicts' relation for entries in a namespace.</summary>
    public IReadOnlyList<(GraphEdge Edge, CognitiveEntry? Source, CognitiveEntry? Target)> GetContradictions(string ns)
    {
        List<GraphEdge> contradictEdges;

        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            contradictEdges = _outgoing.Values
                .SelectMany(l => l)
                .Where(e => e.Relation == "contradicts")
                .ToList();
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        // Resolve entries outside graph lock, filter to namespace
        var results = new List<(GraphEdge, CognitiveEntry?, CognitiveEntry?)>();
        foreach (var edge in contradictEdges)
        {
            var source = _index.Get(edge.SourceId);
            var target = _index.Get(edge.TargetId);

            // Include if either entry is in the requested namespace
            if ((source?.Ns == ns) || (target?.Ns == ns))
                results.Add((edge, source, target));
        }

        return results;
    }

    /// <summary>Get all edges (for persistence).</summary>
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

    /// <summary>Transfer all edges from one entry to another (for merge operations). Returns count of edges transferred.</summary>
    public int TransferEdges(string fromId, string toId)
    {
        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            int transferred = 0;

            // Transfer outgoing edges: fromId -> X becomes toId -> X
            if (_outgoing.TryGetValue(fromId, out var outEdges))
            {
                foreach (var edge in outEdges.ToList())
                {
                    // Skip self-referential edges that would result from the transfer
                    if (edge.TargetId == toId) continue;

                    var newEdge = new GraphEdge(toId, edge.TargetId, edge.Relation, edge.Weight, edge.Metadata);
                    AddEdgeInternal(newEdge);
                    transferred++;
                }
                // Remove old outgoing list
                _outgoing.Remove(fromId);
            }

            // Transfer incoming edges: X -> fromId becomes X -> toId
            if (_incoming.TryGetValue(fromId, out var inEdges))
            {
                foreach (var edge in inEdges.ToList())
                {
                    if (edge.SourceId == toId) continue;

                    // Remove the old outgoing reference (X -> fromId) from the source's outgoing list
                    RemoveMatching(_outgoing, edge.SourceId, e => e.TargetId == fromId && e.Relation == edge.Relation);

                    var newEdge = new GraphEdge(edge.SourceId, toId, edge.Relation, edge.Weight, edge.Metadata);
                    AddEdgeInternal(newEdge);
                    transferred++;
                }
                _incoming.Remove(fromId);
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

    private void AddEdgeInternal(GraphEdge edge)
    {
        // Remove existing edge with same source/target/relation to avoid duplicates
        if (_outgoing.TryGetValue(edge.SourceId, out var outList))
            outList.RemoveAll(e => e.TargetId == edge.TargetId && e.Relation == edge.Relation);
        if (_incoming.TryGetValue(edge.TargetId, out var inList))
            inList.RemoveAll(e => e.SourceId == edge.SourceId && e.Relation == edge.Relation);

        if (!_outgoing.ContainsKey(edge.SourceId))
            _outgoing[edge.SourceId] = new();
        _outgoing[edge.SourceId].Add(edge);

        if (!_incoming.ContainsKey(edge.TargetId))
            _incoming[edge.TargetId] = new();
        _incoming[edge.TargetId].Add(edge);
    }

    private static int RemoveMatching(Dictionary<string, List<GraphEdge>> dict, string key, Func<GraphEdge, bool> predicate)
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

    // Snapshot edge data and schedule save. MUST be called within write lock.
    private void ScheduleSaveEdges()
    {
        var snapshot = _outgoing.Values.SelectMany(l => l).ToList();
        _persistence.ScheduleSaveGlobalEdges(() => snapshot);
    }
}
