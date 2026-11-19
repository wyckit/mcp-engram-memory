using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Retrieval;

/// <summary>
/// Hierarchical Navigable Small World (HNSW) index for approximate nearest neighbor search.
/// Provides O(log N) search complexity vs O(N) linear scan.
/// NOT thread-safe — callers must manage their own locking (CognitiveIndex holds the lock).
/// </summary>
public sealed class HnswIndex
{
    private readonly int _m;
    private readonly int _mMax0;
    private readonly int _efConstruction;
    private readonly double _levelMultiplier;

    private readonly List<float[]> _vectors = new();       // Normalized vectors
    private readonly List<float> _originalNorms = new();   // Original norms for de-normalization
    private readonly List<string> _ids = new();
    private readonly Dictionary<string, int> _idToIndex = new();
    private readonly List<int> _nodeLevels = new();
    private readonly List<List<HashSet<int>>> _connections = new(); // [nodeIndex][layer] = neighbor indices
    private readonly HashSet<int> _deleted = new();

    private int _entryPoint = -1;
    private int _maxLevel = -1;
    private readonly Random _rng = new(42); // Deterministic for reproducibility

    /// <summary>Number of active (non-deleted) entries in the index.</summary>
    public int Count => _vectors.Count - _deleted.Count;

    /// <summary>Total allocated nodes including deleted (for rebuild threshold).</summary>
    public int AllocatedCount => _vectors.Count;

    /// <summary>
    /// Create a new HNSW index.
    /// </summary>
    /// <param name="m">Max connections per node per layer (default 16). Higher = better recall, more memory.</param>
    /// <param name="efConstruction">Beam width during construction (default 200). Higher = better graph quality, slower builds.</param>
    public HnswIndex(int m = 16, int efConstruction = 200)
    {
        _m = m;
        _mMax0 = 2 * m;
        _efConstruction = efConstruction;
        _levelMultiplier = 1.0 / Math.Log(m);
    }

    /// <summary>
    /// Add a vector to the index. If the ID already exists, it is removed and re-added.
    /// </summary>
    public void Add(string id, float[] vector)
    {
        if (_idToIndex.ContainsKey(id))
            Remove(id);

        float norm = VectorMath.Norm(vector);
        if (norm == 0f) return; // Skip zero vectors

        // Store normalized vector for efficient cosine via dot product
        var normalized = Normalize(vector, norm);

        int nodeIndex = _vectors.Count;
        _vectors.Add(normalized);
        _originalNorms.Add(norm);
        _ids.Add(id);
        _idToIndex[id] = nodeIndex;

        int level = RandomLevel();
        _nodeLevels.Add(level);

        // Initialize connection lists for all layers
        var nodeConnections = new List<HashSet<int>>(level + 1);
        for (int i = 0; i <= level; i++)
            nodeConnections.Add(new HashSet<int>());
        _connections.Add(nodeConnections);

        if (_entryPoint == -1)
        {
            _entryPoint = nodeIndex;
            _maxLevel = level;
            return;
        }

        // Greedy search from entry point down to level+1
        int current = _entryPoint;
        for (int l = _maxLevel; l > level; l--)
            current = GreedyClosest(normalized, current, l);

        // For layers min(level, maxLevel) down to 0: search and connect
        for (int l = Math.Min(level, _maxLevel); l >= 0; l--)
        {
            var neighbors = SearchLayer(normalized, new[] { current }, _efConstruction, l);
            int maxConn = l == 0 ? _mMax0 : _m;

            var selected = SelectNeighbors(normalized, neighbors, maxConn);

            foreach (var neighbor in selected)
            {
                _connections[nodeIndex][l].Add(neighbor);

                EnsureLayerExists(neighbor, l);
                _connections[neighbor][l].Add(nodeIndex);

                // Prune neighbor if too many connections
                if (_connections[neighbor][l].Count > maxConn)
                {
                    var pruned = SelectNeighbors(_vectors[neighbor], _connections[neighbor][l], maxConn);
                    _connections[neighbor][l] = new HashSet<int>(pruned);
                }
            }

            if (selected.Count > 0)
                current = selected[0];
        }

        if (level > _maxLevel)
        {
            _entryPoint = nodeIndex;
            _maxLevel = level;
        }
    }

    /// <summary>Default search beam width. Tuned for good recall at 10K+ entries while maintaining low latency.</summary>
    private const int DefaultEfSearch = 128;

    /// <summary>
    /// Search for the k nearest neighbors of the query vector.
    /// Returns (Id, CosineScore) pairs sorted by descending score.
    /// </summary>
    /// <param name="query">Query vector (does not need to be pre-normalized).</param>
    /// <param name="k">Number of results to return.</param>
    /// <param name="ef">Search beam width (default: max(k, 64)). Higher = better recall, slower search.</param>
    public List<(string Id, float Score)> Search(float[] query, int k, int ef = -1)
    {
        if (_entryPoint == -1 || Count == 0)
            return new();

        float norm = VectorMath.Norm(query);
        if (norm == 0f) return new();

        var normalizedQuery = Normalize(query, norm);
        if (ef < 0) ef = Math.Max(k, DefaultEfSearch);

        // Greedy search from top to layer 1
        int current = _entryPoint;
        // Skip deleted entry point
        if (_deleted.Contains(current))
        {
            current = FindNonDeletedEntryPoint();
            if (current == -1) return new();
        }

        for (int l = _maxLevel; l > 0; l--)
            current = GreedyClosest(normalizedQuery, current, l);

        // Beam search at layer 0
        var candidates = SearchLayer(normalizedQuery, new[] { current }, ef, 0);

        return candidates
            .Where(idx => !_deleted.Contains(idx) && _vectors[idx].Length == normalizedQuery.Length)
            .Select(idx => (_ids[idx], VectorMath.Dot(normalizedQuery, _vectors[idx]))) // dot of normalized = cosine
            .OrderByDescending(x => x.Item2)
            .Take(k)
            .ToList();
    }

    /// <summary>Mark an entry as deleted. The node remains in the graph but is excluded from results.</summary>
    public bool Remove(string id)
    {
        if (!_idToIndex.TryGetValue(id, out var index))
            return false;

        _deleted.Add(index);
        _idToIndex.Remove(id);

        if (index == _entryPoint)
        {
            var newEp = FindNonDeletedEntryPoint();
            if (newEp != -1)
            {
                _entryPoint = newEp;
                _maxLevel = _nodeLevels[newEp];
            }
            else
            {
                _entryPoint = -1;
                _maxLevel = -1;
            }
        }

        return true;
    }

    /// <summary>Check if deleted nodes exceed 30% of total, suggesting a rebuild would be beneficial.</summary>
    public bool NeedsRebuild => _vectors.Count > 100 && _deleted.Count > _vectors.Count * 0.3;

    /// <summary>Rebuild the index from scratch, removing deleted nodes and compacting storage.</summary>
    public HnswIndex Rebuild()
    {
        var rebuilt = new HnswIndex(_m, _efConstruction);
        for (int i = 0; i < _vectors.Count; i++)
        {
            if (_deleted.Contains(i)) continue;
            // De-normalize to get original vector
            var original = new float[_vectors[i].Length];
            float norm = _originalNorms[i];
            for (int j = 0; j < original.Length; j++)
                original[j] = _vectors[i][j] * norm;
            rebuilt.Add(_ids[i], original);
        }
        return rebuilt;
    }

    // ── Serialization ──

    /// <summary>
    /// Create a topology-only snapshot for persistence. Vectors are excluded —
    /// they are reconstructed from namespace entries on restore.
    /// </summary>
    public HnswSnapshot CreateSnapshot()
    {
        var connections = new List<List<List<int>>>(_connections.Count);
        for (int i = 0; i < _connections.Count; i++)
        {
            var nodeConns = new List<List<int>>(_connections[i].Count);
            foreach (var layer in _connections[i])
                nodeConns.Add(layer.ToList());
            connections.Add(nodeConns);
        }

        return new HnswSnapshot
        {
            M = _m,
            EfConstruction = _efConstruction,
            EntryPoint = _entryPoint,
            MaxLevel = _maxLevel,
            NodeIds = new List<string>(_ids),
            NodeLevels = new List<int>(_nodeLevels),
            Connections = connections,
            Deleted = _deleted.ToList()
        };
    }

    /// <summary>
    /// Restore an HNSW index from a persisted snapshot.
    /// Vectors are looked up from entries via the provided callback.
    /// Returns null if the snapshot is empty or structurally invalid.
    /// </summary>
    /// <param name="snapshot">The persisted graph topology.</param>
    /// <param name="vectorLookup">Callback to resolve entry ID → raw vector. Return null if entry no longer exists.</param>
    public static HnswIndex? RestoreFromSnapshot(HnswSnapshot snapshot, Func<string, float[]?> vectorLookup)
    {
        if (snapshot is null || snapshot.NodeIds.Count == 0 || snapshot.NodeLevels.Count != snapshot.NodeIds.Count)
            return null;

        var index = new HnswIndex(snapshot.M, snapshot.EfConstruction);
        var deletedSet = new HashSet<int>(snapshot.Deleted);
        int nodeCount = snapshot.NodeIds.Count;

        for (int i = 0; i < nodeCount; i++)
        {
            var id = snapshot.NodeIds[i];
            var rawVector = vectorLookup(id);

            // Preserve valid neighbor indices even for tombstoned nodes so traversal can bridge
            // across deleted nodes without disconnecting the graph.
            var nodeConns = new List<HashSet<int>>();
            if (i < snapshot.Connections.Count)
            {
                foreach (var layer in snapshot.Connections[i])
                {
                    var validLayer = new HashSet<int>();
                    foreach (var neighbor in layer)
                        if (neighbor >= 0 && neighbor < nodeCount)
                            validLayer.Add(neighbor);
                    nodeConns.Add(validLayer);
                }
            }

            if (rawVector == null)
            {
                // Entry was deleted — mark as deleted in the restored index.
                // We still need a placeholder to keep indices aligned.
                index._vectors.Add(Array.Empty<float>());
                index._originalNorms.Add(0f);
                index._ids.Add(id);
                index._nodeLevels.Add(snapshot.NodeLevels[i]);
                deletedSet.Add(i);
                index._connections.Add(nodeConns);
                continue;
            }

            float norm = VectorMath.Norm(rawVector);
            if (norm == 0f)
            {
                index._vectors.Add(Array.Empty<float>());
                index._originalNorms.Add(0f);
                index._ids.Add(id);
                index._nodeLevels.Add(snapshot.NodeLevels[i]);
                deletedSet.Add(i);
                index._connections.Add(nodeConns);
                continue;
            }

            var normalized = Normalize(rawVector, norm);
            index._vectors.Add(normalized);
            index._originalNorms.Add(norm);
            index._ids.Add(id);
            if (!deletedSet.Contains(i))
                index._idToIndex[id] = i;
            index._nodeLevels.Add(snapshot.NodeLevels[i]);
            index._connections.Add(nodeConns);
        }

        index._entryPoint = snapshot.EntryPoint;
        index._maxLevel = snapshot.MaxLevel;

        foreach (var d in deletedSet)
            if (d >= 0 && d < nodeCount)
                index._deleted.Add(d);

        // Validate entry point
        if (index._entryPoint < 0 || index._entryPoint >= index._vectors.Count)
            return null;

        if (index._deleted.Contains(index._entryPoint))
        {
            var newEp = index.FindNonDeletedEntryPoint();
            if (newEp == -1) return null;
            index._entryPoint = newEp;
            index._maxLevel = index._nodeLevels[newEp];
        }

        return index;
    }

    /// <summary>
    /// Restore an HNSW index from a persisted snapshot, additionally verifying that every live
    /// non-zero-vector entry in <paramref name="expectedLiveIds"/> is represented in the snapshot.
    /// Returns null if the snapshot is stale (missing any live entry) so the caller rebuilds cleanly
    /// instead of permanently hiding newer entries from ANN search.
    /// </summary>
    public static HnswIndex? RestoreFromSnapshot(
        HnswSnapshot snapshot,
        Func<string, float[]?> vectorLookup,
        IEnumerable<string> expectedLiveIds)
    {
        var index = RestoreFromSnapshot(snapshot, vectorLookup);
        if (index is null)
            return null;

        foreach (var liveId in expectedLiveIds)
        {
            if (index._idToIndex.ContainsKey(liveId))
                continue;

            // Zero-vector entries are intentionally skipped by Add(), so only non-zero vectors
            // missing from the restored index indicate a stale snapshot.
            var vec = vectorLookup(liveId);
            if (vec is not null && vec.Length > 0 && VectorMath.Norm(vec) > 0f)
                return null;
        }

        return index;
    }

    // ── Private Helpers ──

    private int RandomLevel()
    {
        return (int)(-Math.Log(_rng.NextDouble()) * _levelMultiplier);
    }

    private static float[] Normalize(float[] v, float norm)
    {
        var result = new float[v.Length];
        float invNorm = 1f / norm;
        for (int i = 0; i < v.Length; i++)
            result[i] = v[i] * invNorm;
        return result;
    }

    private int GreedyClosest(float[] query, int entryPoint, int layer)
    {
        int current = entryPoint;
        float currentSim = _vectors[current].Length == query.Length
            ? VectorMath.Dot(query, _vectors[current]) // Higher = closer
            : -2f;

        bool changed = true;
        while (changed)
        {
            changed = false;
            if (layer < _connections[current].Count)
            {
                foreach (var neighbor in _connections[current][layer])
                {
                    // Traverse through tombstoned bridge nodes so deletions never disconnect
                    // upper-layer routing paths. Deleted nodes are excluded only from final results.
                    var neighborVec = _vectors[neighbor];
                    if (neighborVec.Length != query.Length) continue;
                    float sim = VectorMath.Dot(query, neighborVec);
                    if (sim > currentSim)
                    {
                        current = neighbor;
                        currentSim = sim;
                        changed = true;
                    }
                }
            }
        }

        return current;
    }

    private List<int> SearchLayer(float[] query, IEnumerable<int> entryPoints, int ef, int layer)
    {
        var visited = new HashSet<int>();
        // Candidates: closest first (max similarity = min negative similarity).
        // Includes tombstoned bridge nodes so their neighbors remain reachable.
        var candidates = new PriorityQueue<int, float>();
        // Result: farthest first (min similarity at top) — live (non-deleted) nodes only.
        var result = new PriorityQueue<int, float>();
        float worstResultSim = float.MinValue;

        foreach (var ep in entryPoints)
        {
            if (!visited.Add(ep)) continue;
            bool isDeleted = _deleted.Contains(ep);
            var epVec = _vectors[ep];
            bool hasValidVec = epVec.Length == query.Length;
            float sim = hasValidVec ? VectorMath.Dot(query, epVec) : -2f;

            candidates.Enqueue(ep, -sim); // negate so closest (highest sim) dequeues first
            if (!isDeleted && hasValidVec)
            {
                result.Enqueue(ep, sim);       // lowest sim dequeues first (farthest)
                if (result.Count > ef)
                    result.Dequeue();
                if (result.TryPeek(out _, out var worst))
                    worstResultSim = worst;
            }
        }

        while (candidates.Count > 0)
        {
            candidates.TryDequeue(out var closest, out var negSim);
            float closestSim = -negSim;

            if (closestSim < worstResultSim && result.Count >= ef)
                break;

            if (layer >= _connections[closest].Count) continue;

            foreach (var neighbor in _connections[closest][layer])
            {
                if (!visited.Add(neighbor)) continue;

                bool isDeleted = _deleted.Contains(neighbor);
                var neighborVec = _vectors[neighbor];
                if (neighborVec.Length != query.Length)
                {
                    // Restored deleted nodes without vectors still carry topology links; walk
                    // through them while we have not yet filled ef live candidates.
                    if (isDeleted && result.Count < ef)
                        candidates.Enqueue(neighbor, 2f);
                    continue;
                }

                float sim = VectorMath.Dot(query, neighborVec);

                if (sim > worstResultSim || result.Count < ef)
                {
                    candidates.Enqueue(neighbor, -sim);
                    if (!isDeleted)
                    {
                        result.Enqueue(neighbor, sim);

                        if (result.Count > ef)
                            result.Dequeue(); // Remove farthest

                        if (result.TryPeek(out _, out var worst))
                            worstResultSim = worst;
                    }
                }
            }
        }

        var resultList = new List<int>(result.Count);
        while (result.Count > 0)
            resultList.Add(result.Dequeue());
        return resultList;
    }

    private List<int> SelectNeighbors(float[] query, IEnumerable<int> candidates, int maxCount)
    {
        return candidates
            .Where(idx => !_deleted.Contains(idx) && _vectors[idx].Length == query.Length)
            .OrderByDescending(idx => VectorMath.Dot(query, _vectors[idx]))
            .Take(maxCount)
            .ToList();
    }

    private void EnsureLayerExists(int nodeIndex, int layer)
    {
        while (_connections[nodeIndex].Count <= layer)
            _connections[nodeIndex].Add(new HashSet<int>());
    }

    private int FindNonDeletedEntryPoint()
    {
        int bestNode = -1;
        int bestLevel = -1;
        for (int i = 0; i < _vectors.Count; i++)
        {
            if (_deleted.Contains(i)) continue;
            if (_nodeLevels[i] > bestLevel)
            {
                bestNode = i;
                bestLevel = _nodeLevels[i];
            }
        }
        return bestNode;
    }
}
