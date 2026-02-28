namespace McpVectorMemory;

/// <summary>
/// Hierarchical Navigable Small World (HNSW) graph for approximate nearest-neighbor
/// search using cosine distance. NOT thread-safe — the caller must handle synchronization.
/// </summary>
internal sealed class HnswGraph
{
    private readonly int _m;              // max bi-directional connections per element per layer
    private readonly int _mMax0;          // max connections for layer 0 (typically 2*M)
    private readonly int _efConstruction; // size of dynamic candidate list during construction
    private readonly double _levelMult;   // 1 / ln(M) — controls level distribution

    private readonly Dictionary<int, HnswNode> _nodes = new();
    private int _entryPointId = -1;
    private int _maxLevel = -1;
    private readonly Random _random;

    public HnswGraph(int m = 16, int efConstruction = 200, int? seed = null)
    {
        if (m < 2) throw new ArgumentOutOfRangeException(nameof(m), "M must be at least 2.");
        if (efConstruction <= 0) throw new ArgumentOutOfRangeException(nameof(efConstruction));

        _m = m;
        _mMax0 = m * 2;
        _efConstruction = efConstruction;
        _levelMult = 1.0 / Math.Log(m);
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    /// <summary>
    /// Inserts a new node into the graph. If <paramref name="id"/> already exists,
    /// the previous node is soft-deleted and replaced.
    /// </summary>
    public void Add(int id, float[] vector)
    {
        // If the ID already exists, mark the old node as deleted first.
        if (_nodes.TryGetValue(id, out var existing))
            existing.IsDeleted = true;

        int level = RandomLevel();
        var node = new HnswNode(id, vector, level);
        _nodes[id] = node;

        if (_entryPointId == -1 || _maxLevel < 0)
        {
            _entryPointId = id;
            _maxLevel = level;
            return;
        }

        int ep = _entryPointId;

        // Phase 1: greedily descend from top level to level+1 (single nearest neighbor)
        for (int lc = _maxLevel; lc > level; lc--)
        {
            var nearest = SearchLayer(vector, ep, ef: 1, lc);
            if (nearest.Count > 0)
                ep = nearest[0].Id;
        }

        // Phase 2: insert at each layer from min(level, _maxLevel) down to 0
        for (int lc = Math.Min(level, _maxLevel); lc >= 0; lc--)
        {
            var candidates = SearchLayer(vector, ep, _efConstruction, lc);
            int mMax = lc == 0 ? _mMax0 : _m;
            var neighbors = SelectNeighbors(vector, candidates, mMax);

            // Add bi-directional connections
            foreach (var (nId, _) in neighbors)
            {
                node.AddConnection(lc, nId);
                var neighborNode = _nodes[nId];
                neighborNode.AddConnection(lc, id);

                // Shrink neighbor's connections if over capacity
                ShrinkConnections(neighborNode, lc, mMax);
            }

            if (candidates.Count > 0)
                ep = candidates[0].Id;
        }

        // Update entry point if the new node has a higher level
        if (level > _maxLevel)
        {
            _entryPointId = id;
            _maxLevel = level;
        }
    }

    /// <summary>
    /// Searches the graph for the <paramref name="k"/> nearest neighbors of
    /// <paramref name="query"/>. Returns results sorted by distance ascending.
    /// </summary>
    /// <param name="query">Query vector.</param>
    /// <param name="k">Number of nearest neighbors to return.</param>
    /// <param name="ef">Search effort — size of the dynamic candidate list (must be >= k).</param>
    public List<(int Id, float Distance)> Search(float[] query, int k, int ef)
    {
        if (_entryPointId == -1)
            return new List<(int, float)>();

        ef = Math.Max(ef, k);
        int ep = _entryPointId;

        // Phase 1: greedily descend from top level to layer 1
        for (int lc = _maxLevel; lc > 0; lc--)
        {
            var nearest = SearchLayer(query, ep, ef: 1, lc);
            if (nearest.Count > 0)
                ep = nearest[0].Id;
        }

        // Phase 2: search at layer 0
        var candidates = SearchLayer(query, ep, ef, layer: 0);

        // Filter deleted and return top k
        var results = new List<(int Id, float Distance)>(k);
        foreach (var c in candidates)
        {
            if (_nodes.TryGetValue(c.Id, out var n) && !n.IsDeleted)
            {
                results.Add(c);
                if (results.Count >= k)
                    break;
            }
        }
        return results;
    }

    /// <summary>Soft-deletes a node. The node remains in the graph for navigation.</summary>
    public void MarkDeleted(int id)
    {
        if (_nodes.TryGetValue(id, out var node))
            node.IsDeleted = true;
    }

    /// <summary>Removes all soft-deleted nodes and returns true if any were removed.</summary>
    public bool Compact()
    {
        var deletedIds = _nodes.Where(kv => kv.Value.IsDeleted).Select(kv => kv.Key).ToList();
        if (deletedIds.Count == 0)
            return false;

        var deletedSet = new HashSet<int>(deletedIds);

        // Remove deleted nodes from all neighbor lists
        foreach (var (_, node) in _nodes)
        {
            if (node.IsDeleted) continue;
            for (int layer = 0; layer <= node.Level; layer++)
            {
                var conns = node.GetConnections(layer);
                conns.RemoveAll(id => deletedSet.Contains(id));
            }
        }

        foreach (var id in deletedIds)
            _nodes.Remove(id);

        // Always recalculate entry point and max level after compaction,
        // since deleted nodes at high levels may have been removed.
        if (_nodes.Count == 0)
        {
            _entryPointId = -1;
            _maxLevel = -1;
        }
        else
        {
            var best = _nodes.Values.MaxBy(n => n.Level)!;
            _entryPointId = best.Id;
            _maxLevel = best.Level;
        }

        return true;
    }

    // ── Core HNSW search layer ──────────────────────────────────────────────

    /// <summary>
    /// Searches a single layer of the graph starting from <paramref name="entryPointId"/>.
    /// Returns up to <paramref name="ef"/> candidates sorted by distance ascending.
    /// </summary>
    private List<(int Id, float Distance)> SearchLayer(float[] query, int entryPointId, int ef, int layer)
    {
        if (!_nodes.TryGetValue(entryPointId, out var epNode))
            return new List<(int, float)>();

        float epDist = VectorMath.CosineDistance(query, epNode.Vector);

        var visited = new HashSet<int> { entryPointId };

        // Min-heap: candidates to explore (nearest first)
        var candidates = new PriorityQueue<int, float>();
        candidates.Enqueue(entryPointId, epDist);

        // Max-heap via negated priority: current best results (farthest = smallest negated priority)
        var results = new PriorityQueue<int, float>();
        results.Enqueue(entryPointId, -epDist);
        int resultCount = 1;
        float worstDist = epDist;

        while (candidates.Count > 0)
        {
            if (!candidates.TryDequeue(out int cId, out float cDist))
                break;

            // If the nearest candidate is farther than the worst result, stop
            if (cDist > worstDist && resultCount >= ef)
                break;

            if (!_nodes.TryGetValue(cId, out var cNode))
                continue;

            foreach (int neighborId in cNode.GetConnections(layer))
            {
                if (!visited.Add(neighborId))
                    continue;

                if (!_nodes.TryGetValue(neighborId, out var neighborNode))
                    continue;

                float nDist = VectorMath.CosineDistance(query, neighborNode.Vector);

                if (nDist < worstDist || resultCount < ef)
                {
                    candidates.Enqueue(neighborId, nDist);
                    results.Enqueue(neighborId, -nDist);
                    resultCount++;

                    if (resultCount > ef)
                    {
                        results.Dequeue(); // Remove farthest
                        resultCount--;
                        if (results.TryPeek(out _, out float negWorst))
                            worstDist = -negWorst;
                    }
                }
            }
        }

        // Drain results and sort by distance ascending
        var resultList = new List<(int Id, float Distance)>(resultCount);
        while (results.TryDequeue(out int id, out float negDist))
            resultList.Add((id, -negDist));

        resultList.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return resultList;
    }

    // ── Neighbor selection ──────────────────────────────────────────────────

    /// <summary>Simple neighbor selection: return the M nearest candidates.</summary>
    private static List<(int Id, float Distance)> SelectNeighbors(
        float[] query, List<(int Id, float Distance)> candidates, int m)
    {
        // candidates is already sorted by distance ascending
        return candidates.Count <= m ? candidates : candidates.GetRange(0, m);
    }

    /// <summary>
    /// Trims a node's connections at a given layer to at most <paramref name="mMax"/>
    /// by keeping only the nearest neighbors.
    /// </summary>
    private void ShrinkConnections(HnswNode node, int layer, int mMax)
    {
        var conns = node.GetConnections(layer);
        if (conns.Count <= mMax)
            return;

        // Score each connection by distance to the node
        var scored = new List<(int Id, float Distance)>(conns.Count);
        foreach (int connId in conns)
        {
            if (_nodes.TryGetValue(connId, out var connNode))
                scored.Add((connId, VectorMath.CosineDistance(node.Vector, connNode.Vector)));
        }

        scored.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        var trimmed = new List<int>(mMax);
        for (int i = 0; i < Math.Min(mMax, scored.Count); i++)
            trimmed.Add(scored[i].Id);

        node.SetConnections(layer, trimmed);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private int RandomLevel()
    {
        // NextDouble returns [0, 1). Use 1-NextDouble to get (0, 1] and avoid Log(0) = -inf.
        return (int)(-Math.Log(1.0 - _random.NextDouble()) * _levelMult);
    }
}

/// <summary>
/// A node in the HNSW graph. Stores the vector and per-layer connection lists.
/// </summary>
internal sealed class HnswNode
{
    public int Id { get; }
    public float[] Vector { get; }
    public int Level { get; }
    public bool IsDeleted { get; set; }

    private readonly List<int>[] _connections;

    public HnswNode(int id, float[] vector, int level)
    {
        Id = id;
        Vector = vector;
        Level = level;
        _connections = new List<int>[level + 1];
        for (int i = 0; i <= level; i++)
            _connections[i] = new List<int>();
    }

    public List<int> GetConnections(int layer)
    {
        return layer <= Level ? _connections[layer] : new List<int>();
    }

    public void AddConnection(int layer, int neighborId)
    {
        if (layer <= Level)
            _connections[layer].Add(neighborId);
    }

    public void SetConnections(int layer, List<int> connections)
    {
        if (layer <= Level)
            _connections[layer] = connections;
    }
}
