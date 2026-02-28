namespace McpVectorMemory;

/// <summary>
/// Thread-safe vector index supporting upsert, delete, and k-nearest-neighbor
/// search via cosine similarity. Uses HNSW for sub-linear search and optionally
/// persists entries to disk as JSON.
/// </summary>
public sealed class VectorIndex : IDisposable
{
    // Per-dimension HNSW graphs (vectors of different dimensions live in separate graphs)
    private readonly Dictionary<int, HnswGraph> _graphs = new();

    // String ID → internal integer ID
    private readonly Dictionary<string, int> _idMap = new();

    // Internal ID → (entry, dimension)
    private readonly Dictionary<int, (VectorEntry Entry, int Dim)> _entries = new();

    private readonly ReaderWriterLockSlim _lock = new();
    private readonly string? _dataPath;
    private readonly int _hnswM;
    private readonly int _hnswEfConstruction;
    private readonly int _hnswEfSearch;
    private int _nextId;
    private int _count;
    private int _deletedNodeCount; // tracks soft-deleted HNSW nodes for compaction

    /// <summary>Number of vectors currently stored in the index.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// Creates a new vector index.
    /// </summary>
    /// <param name="dataPath">
    /// File path for JSON persistence. Pass <c>null</c> for ephemeral in-memory only.
    /// </param>
    /// <param name="hnswM">HNSW M parameter — max connections per node per layer (default 16).</param>
    /// <param name="hnswEfConstruction">HNSW construction search effort (default 200).</param>
    /// <param name="hnswEfSearch">HNSW search effort (default 50).</param>
    public VectorIndex(
        string? dataPath = null,
        int hnswM = 16,
        int hnswEfConstruction = 200,
        int hnswEfSearch = 50)
    {
        _dataPath = dataPath;
        _hnswM = hnswM;
        _hnswEfConstruction = hnswEfConstruction;
        _hnswEfSearch = hnswEfSearch;

        if (_dataPath is not null)
            LoadFromDisk();
    }

    /// <summary>
    /// Adds or replaces a vector entry.
    /// </summary>
    public void Upsert(VectorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _lock.EnterWriteLock();
        try
        {
            int dim = entry.Vector.Length;

            // Handle replacement: remove old internal mapping
            if (_idMap.TryGetValue(entry.Id, out int oldInternalId))
            {
                var (_, oldDim) = _entries[oldInternalId];
                GetOrCreateGraph(oldDim).MarkDeleted(oldInternalId);
                _entries.Remove(oldInternalId);
                _deletedNodeCount++;
                // count stays the same (replace, not add)
            }
            else
            {
                _count++;
            }

            int internalId = _nextId++;
            _idMap[entry.Id] = internalId;
            _entries[internalId] = (entry, dim);

            var graph = GetOrCreateGraph(dim);
            graph.Add(internalId, entry.Vector);

            CompactIfNeeded();
            PersistUnsafe();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Removes a vector entry by id. Returns <c>true</c> if it existed.
    /// </summary>
    public bool Delete(string id)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_idMap.TryGetValue(id, out int internalId))
                return false;

            var (_, dim) = _entries[internalId];
            GetOrCreateGraph(dim).MarkDeleted(internalId);
            _entries.Remove(internalId);
            _idMap.Remove(id);
            _count--;
            _deletedNodeCount++;

            CompactIfNeeded();
            PersistUnsafe();
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Searches for the <paramref name="k"/> nearest neighbors of
    /// <paramref name="query"/> using cosine similarity (higher = more similar).
    /// </summary>
    /// <param name="query">Query vector (must have the same dimension as stored vectors).</param>
    /// <param name="k">Maximum number of results to return.</param>
    /// <param name="minScore">Minimum cosine-similarity threshold (-1 to 1).</param>
    public IReadOnlyList<SearchResult> Search(float[] query, int k = 5, float minScore = 0f)
    {
        if (query is null || query.Length == 0)
            throw new ArgumentException("Query vector must not be null or empty.", nameof(query));
        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k), "k must be positive.");

        float queryNorm = VectorMath.Norm(query);
        if (queryNorm == 0f)
            throw new ArgumentException("Query vector must not be zero-magnitude.", nameof(query));

        _lock.EnterReadLock();
        try
        {
            int dim = query.Length;
            if (!_graphs.TryGetValue(dim, out var graph))
                return Array.Empty<SearchResult>();

            // Use HNSW search with ef >= k
            int ef = Math.Max(k * 2, _hnswEfSearch);
            float maxDistance = 1f - minScore; // cosine distance threshold
            var hnswResults = graph.Search(query, k: ef, ef: ef);

            var results = new List<SearchResult>(Math.Min(k, hnswResults.Count));
            foreach (var (internalId, distance) in hnswResults)
            {
                // HNSW results are sorted by distance ascending — once past the
                // threshold, all remaining results are also too far.
                if (distance > maxDistance)
                    break;

                if (_entries.TryGetValue(internalId, out var e))
                {
                    float score = 1f - distance;
                    results.Add(new SearchResult(e.Entry, score));
                    if (results.Count >= k)
                        break;
                }
            }

            return results.ToArray();
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lock.Dispose();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private HnswGraph GetOrCreateGraph(int dimension)
    {
        if (!_graphs.TryGetValue(dimension, out var graph))
        {
            graph = new HnswGraph(_hnswM, _hnswEfConstruction);
            _graphs[dimension] = graph;
        }
        return graph;
    }

    /// <summary>
    /// Loads entries from disk and rebuilds the HNSW graphs.
    /// Must be called before any concurrent access (i.e. from the constructor).
    /// </summary>
    private void LoadFromDisk()
    {
        var entries = IndexPersistence.Load(_dataPath!);
        foreach (var entry in entries)
        {
            int dim = entry.Vector.Length;
            int internalId = _nextId++;
            _idMap[entry.Id] = internalId;
            _entries[internalId] = (entry, dim);

            var graph = GetOrCreateGraph(dim);
            graph.Add(internalId, entry.Vector);
            _count++;
        }
    }

    /// <summary>
    /// Compacts HNSW graphs when the number of soft-deleted nodes exceeds a
    /// threshold relative to live entries. Must be called under write lock.
    /// </summary>
    private void CompactIfNeeded()
    {
        // Compact when deleted nodes exceed the live count (or at least 100)
        int threshold = Math.Max(_count, 100);
        if (_deletedNodeCount < threshold)
            return;

        foreach (var graph in _graphs.Values)
            graph.Compact();

        _deletedNodeCount = 0;
    }

    /// <summary>
    /// Persists current entries to disk. Must be called under write lock.
    /// </summary>
    private void PersistUnsafe()
    {
        if (_dataPath is null) return;
        IndexPersistence.Save(_dataPath, _entries.Values.Select(e => e.Entry));
    }
}
