using System.Numerics;

namespace McpVectorMemory;

/// <summary>
/// Thread-safe in-memory vector index supporting upsert, delete, and
/// k-nearest-neighbor search via cosine similarity.
/// </summary>
public sealed class VectorIndex
{
    private readonly Dictionary<string, VectorEntry> _entries = new();
    private readonly ReaderWriterLockSlim _lock = new();

    /// <summary>Number of vectors currently stored in the index.</summary>
    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _entries.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Adds or replaces a vector entry.
    /// </summary>
    public void Upsert(VectorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _lock.EnterWriteLock();
        try { _entries[entry.Id] = entry; }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Removes a vector entry by id. Returns <c>true</c> if it existed.
    /// </summary>
    public bool Delete(string id)
    {
        _lock.EnterWriteLock();
        try { return _entries.Remove(id); }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Searches for the <paramref name="k"/> nearest neighbors of
    /// <paramref name="query"/> using cosine similarity (higher = more similar).
    /// </summary>
    /// <param name="query">Query vector (must have the same dimension as stored vectors).</param>
    /// <param name="k">Maximum number of results to return.</param>
    /// <param name="minScore">Minimum cosine-similarity threshold (0–1).</param>
    public IReadOnlyList<SearchResult> Search(float[] query, int k = 5, float minScore = 0f)
    {
        if (query is null || query.Length == 0)
            throw new ArgumentException("Query vector must not be null or empty.", nameof(query));
        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k), "k must be positive.");

        float queryNorm = Norm(query);
        if (queryNorm == 0f)
            return Array.Empty<SearchResult>();

        _lock.EnterReadLock();
        List<(VectorEntry entry, float score)> scored;
        try
        {
            scored = new List<(VectorEntry, float)>(_entries.Count);
            foreach (var entry in _entries.Values)
            {
                if (entry.Vector.Length != query.Length)
                    continue;

                float entryNorm = Norm(entry.Vector);
                if (entryNorm == 0f)
                    continue;

                float dot = Dot(query, entry.Vector);
                float score = dot / (queryNorm * entryNorm);

                if (score >= minScore)
                    scored.Add((entry, score));
            }
        }
        finally { _lock.ExitReadLock(); }

        scored.Sort((a, b) => b.score.CompareTo(a.score));

        int take = Math.Min(k, scored.Count);
        var results = new SearchResult[take];
        for (int i = 0; i < take; i++)
            results[i] = new SearchResult(scored[i].entry, scored[i].score);
        return results;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static float Dot(float[] a, float[] b)
    {
        float sum = 0f;
        int i = 0;

        if (Vector.IsHardwareAccelerated)
        {
            int simdLength = Vector<float>.Count;
            int simdEnd = a.Length - (a.Length % simdLength);
            for (; i < simdEnd; i += simdLength)
                sum += Vector.Dot(new Vector<float>(a, i), new Vector<float>(b, i));
        }

        for (; i < a.Length; i++)
            sum += a[i] * b[i];

        return sum;
    }

    private static float Norm(float[] v)
    {
        float dot = Dot(v, v);
        return dot == 0f ? 0f : MathF.Sqrt(dot);
    }
}
