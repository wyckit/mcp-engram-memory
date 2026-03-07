using System.Numerics;
using McpVectorMemory.Core.Models;

namespace McpVectorMemory.Core.Services;

/// <summary>
/// Thread-safe namespace-partitioned vector index with lifecycle awareness.
///
/// Locking strategy:
/// - Read-only methods use EnterUpgradeableReadLock, upgrading to write only if EnsureNamespaceLoaded needs to load.
/// - Mutating methods use EnterWriteLock directly.
/// - Only one upgradeable-read or write lock can be held at a time, but multiple read locks can coexist.
/// </summary>
public sealed class CognitiveIndex : IDisposable
{
    private readonly Dictionary<string, Dictionary<string, (CognitiveEntry Entry, float Norm)>> _namespaces = new();
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly IStorageProvider _persistence;
    private readonly HashSet<string> _loadedNamespaces = new();
    private readonly BM25Index _bm25 = new();
    private readonly TokenReranker _reranker = new();

    public CognitiveIndex(IStorageProvider persistence)
    {
        _persistence = persistence;
    }

    /// <summary>Total entry count across all loaded namespaces.</summary>
    public int Count
    {
        get
        {
            _lock.EnterUpgradeableReadLock();
            try
            {
                LoadAllNamespaces();
                return _namespaces.Values.Sum(ns => ns.Count);
            }
            finally { _lock.ExitUpgradeableReadLock(); }
        }
    }

    /// <summary>Count entries in a specific namespace.</summary>
    public int CountInNamespace(string ns)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureNamespaceLoaded(ns);
            return _namespaces.TryGetValue(ns, out var entries) ? entries.Count : 0;
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>Get all known namespace names.</summary>
    public IReadOnlyList<string> GetNamespaces()
    {
        _lock.EnterReadLock();
        try
        {
            var persisted = _persistence.GetPersistedNamespaces();
            var inMemory = _namespaces.Keys;
            return persisted.Union(inMemory).Distinct().ToList();
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Add or replace a cognitive entry.</summary>
    public void Upsert(CognitiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        float norm = Norm(entry.Vector);

        _lock.EnterWriteLock();
        try
        {
            EnsureNamespaceLoadedUnderWrite(entry.Ns);
            if (!_namespaces.TryGetValue(entry.Ns, out var nsEntries))
            {
                nsEntries = new();
                _namespaces[entry.Ns] = nsEntries;
            }
            nsEntries[entry.Id] = (entry, norm);
            _bm25.Index(entry);
            ScheduleSave(entry.Ns);
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Get an entry by ID, searching all namespaces.</summary>
    public CognitiveEntry? Get(string id)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            LoadAllNamespaces();
            foreach (var ns in _namespaces.Values)
            {
                if (ns.TryGetValue(id, out var tuple))
                    return tuple.Entry;
            }
            return null;
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>Get an entry by ID within a specific namespace.</summary>
    public CognitiveEntry? Get(string id, string ns)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureNamespaceLoaded(ns);
            if (_namespaces.TryGetValue(ns, out var entries) && entries.TryGetValue(id, out var tuple))
                return tuple.Entry;
            return null;
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>Delete an entry by ID, searching all namespaces.</summary>
    public bool Delete(string id)
    {
        _lock.EnterWriteLock();
        try
        {
            LoadAllNamespacesUnderWrite();
            foreach (var (ns, entries) in _namespaces)
            {
                if (entries.Remove(id))
                {
                    _bm25.Remove(id, ns);
                    ScheduleSave(ns);
                    return true;
                }
            }
            return false;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Namespace-scoped k-nearest-neighbor search with lifecycle filtering.
    /// </summary>
    public IReadOnlyList<CognitiveSearchResult> Search(
        float[] query,
        string ns,
        int k = 5,
        float minScore = 0f,
        string? category = null,
        HashSet<string>? includeStates = null,
        bool summaryFirst = false)
    {
        if (query is null || query.Length == 0)
            throw new ArgumentException("Query vector must not be null or empty.", nameof(query));
        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k), "k must be positive.");

        includeStates ??= new HashSet<string> { "stm", "ltm" };
        float queryNorm = Norm(query);
        if (queryNorm == 0f)
            throw new ArgumentException("Query vector must not be zero-magnitude.", nameof(query));

        List<(CognitiveEntry entry, float score)> scored;
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureNamespaceLoaded(ns);
            if (!_namespaces.TryGetValue(ns, out var nsEntries))
                return Array.Empty<CognitiveSearchResult>();

            scored = new(nsEntries.Count);
            foreach (var (entry, entryNorm) in nsEntries.Values)
            {
                if (!includeStates.Contains(entry.LifecycleState))
                    continue;
                if (category is not null && entry.Category != category)
                    continue;
                if (entry.Vector.Length != query.Length)
                    continue;
                if (entryNorm == 0f)
                    continue;

                float dot = Dot(query, entry.Vector);
                float score = dot / (queryNorm * entryNorm);

                if (score >= minScore)
                    scored.Add((entry, score));
            }
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        // Sorting happens outside the lock on a local list
        if (summaryFirst)
        {
            scored.Sort((a, b) =>
            {
                if (a.entry.IsSummaryNode != b.entry.IsSummaryNode)
                    return a.entry.IsSummaryNode ? -1 : 1;
                return b.score.CompareTo(a.score);
            });
        }
        else
        {
            scored.Sort((a, b) => b.score.CompareTo(a.score));
        }

        int take = Math.Min(k, scored.Count);
        var results = new CognitiveSearchResult[take];
        for (int i = 0; i < take; i++)
        {
            var e = scored[i].entry;
            results[i] = new CognitiveSearchResult(
                e.Id, e.Text, scored[i].score, e.LifecycleState,
                e.ActivationEnergy, e.Category, e.Metadata,
                e.IsSummaryNode, e.SourceClusterId, e.AccessCount);
        }
        return results;
    }

    /// <summary>
    /// Hybrid search combining vector cosine similarity with BM25 keyword matching,
    /// fused via Reciprocal Rank Fusion (RRF). Optionally applies token-level reranking.
    /// </summary>
    public IReadOnlyList<CognitiveSearchResult> HybridSearch(
        float[] query,
        string queryText,
        string ns,
        int k = 5,
        float minScore = 0f,
        string? category = null,
        HashSet<string>? includeStates = null,
        bool rerank = false,
        int rrfK = 60)
    {
        // Stage 1: Get broader candidate sets from both retrieval methods
        int candidateK = Math.Max(k * 4, 20);
        var vectorResults = Search(query, ns, candidateK, minScore, category, includeStates);

        // Build set of eligible IDs (those that pass lifecycle/category filters)
        var eligibleIds = vectorResults.Select(r => r.Id).ToHashSet();

        // Stage 2: BM25 search over eligible entries
        var bm25Results = _bm25.Search(queryText, ns, candidateK, eligibleIds);
        // Also include BM25 results that weren't in vector results (they may be keyword matches)
        var bm25Unfiltered = _bm25.Search(queryText, ns, candidateK);

        // Add BM25-only results that pass filters
        foreach (var (id, _) in bm25Unfiltered)
        {
            if (!eligibleIds.Contains(id))
            {
                // Check if this entry passes lifecycle/category filters
                var entry = Get(id, ns);
                if (entry is not null)
                {
                    var states = includeStates ?? new HashSet<string> { "stm", "ltm" };
                    if (states.Contains(entry.LifecycleState) &&
                        (category is null || entry.Category == category))
                    {
                        eligibleIds.Add(id);
                    }
                }
            }
        }

        // Stage 3: Reciprocal Rank Fusion
        var vectorRanks = new Dictionary<string, int>();
        for (int i = 0; i < vectorResults.Count; i++)
            vectorRanks[vectorResults[i].Id] = i + 1;

        var bm25Ranks = new Dictionary<string, int>();
        for (int i = 0; i < bm25Unfiltered.Count; i++)
            bm25Ranks[bm25Unfiltered[i].Id] = i + 1;

        var allIds = vectorRanks.Keys.Union(bm25Ranks.Keys).ToList();
        var rrfScores = new List<(string Id, float RrfScore)>(allIds.Count);

        foreach (var id in allIds)
        {
            float score = 0f;
            if (vectorRanks.TryGetValue(id, out int vRank))
                score += 1f / (rrfK + vRank);
            if (bm25Ranks.TryGetValue(id, out int bRank))
                score += 1f / (rrfK + bRank);
            rrfScores.Add((id, score));
        }

        rrfScores.Sort((a, b) => b.RrfScore.CompareTo(a.RrfScore));

        // Stage 4: Build result objects with RRF scores
        var vectorLookup = vectorResults.ToDictionary(r => r.Id);
        var results = new List<CognitiveSearchResult>(Math.Min(k, rrfScores.Count));

        foreach (var (id, rrfScore) in rrfScores.Take(rerank ? k * 2 : k))
        {
            if (vectorLookup.TryGetValue(id, out var vr))
            {
                results.Add(new CognitiveSearchResult(
                    vr.Id, vr.Text, rrfScore,
                    vr.LifecycleState, vr.ActivationEnergy,
                    vr.Category, vr.Metadata,
                    vr.IsSummaryNode, vr.SourceClusterId, vr.AccessCount));
            }
            else
            {
                // Entry found by BM25 only — look up metadata
                var entry = Get(id, ns);
                if (entry is not null)
                {
                    results.Add(new CognitiveSearchResult(
                        entry.Id, entry.Text, rrfScore,
                        entry.LifecycleState, entry.ActivationEnergy,
                        entry.Category, entry.Metadata,
                        entry.IsSummaryNode, entry.SourceClusterId, entry.AccessCount));
                }
            }
        }

        // Stage 5: Optional reranking
        if (rerank && results.Count > 0)
        {
            results = _reranker.Rerank(queryText, results).Take(k).ToList();
        }
        else if (results.Count > k)
        {
            results = results.Take(k).ToList();
        }

        return results;
    }

    /// <summary>
    /// Apply token-level reranking to existing search results.
    /// </summary>
    public IReadOnlyList<CognitiveSearchResult> Rerank(
        string queryText,
        IReadOnlyList<CognitiveSearchResult> results)
    {
        return _reranker.Rerank(queryText, results);
    }

    /// <summary>
    /// Find near-duplicates for a single entry within its namespace (O(N) scan).
    /// Used by store_memory for on-ingest duplicate warning.
    /// </summary>
    public IReadOnlyList<(string IdA, string IdB, float Similarity)> FindDuplicatesForEntry(
        string ns, string entryId, float threshold = 0.95f)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureNamespaceLoaded(ns);
            if (!_namespaces.TryGetValue(ns, out var nsEntries))
                return Array.Empty<(string, string, float)>();

            if (!nsEntries.TryGetValue(entryId, out var target))
                return Array.Empty<(string, string, float)>();

            if (target.Norm == 0f)
                return Array.Empty<(string, string, float)>();

            var duplicates = new List<(string IdA, string IdB, float Similarity)>();
            foreach (var (id, (entry, norm)) in nsEntries)
            {
                if (id == entryId || norm == 0f) continue;
                if (entry.Vector.Length != target.Entry.Vector.Length) continue;

                float dot = Dot(target.Entry.Vector, entry.Vector);
                float sim = dot / (target.Norm * norm);
                if (sim >= threshold)
                    duplicates.Add((entryId, id, sim));
            }

            duplicates.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
            return duplicates;
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>
    /// Find near-duplicate entries within a namespace by pairwise cosine similarity.
    /// Optimized: sorts candidates by norm for early-exit on impossible pairs,
    /// and caps results to avoid unbounded output.
    /// </summary>
    public IReadOnlyList<(string IdA, string IdB, float Similarity)> FindDuplicates(
        string ns, float threshold = 0.95f, string? category = null,
        HashSet<string>? includeStates = null, int maxResults = 100)
    {
        if (threshold < 0f || threshold > 1f)
            throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be between 0 and 1.");

        includeStates ??= new HashSet<string> { "stm", "ltm" };

        List<(CognitiveEntry entry, float norm)> candidates;
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureNamespaceLoaded(ns);
            if (!_namespaces.TryGetValue(ns, out var nsEntries))
                return Array.Empty<(string, string, float)>();

            candidates = nsEntries.Values
                .Where(t => includeStates.Contains(t.Entry.LifecycleState)
                    && (category is null || t.Entry.Category == category)
                    && t.Norm > 0f)
                .ToList();
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        // Sort by norm ascending — allows early exit when norm ratio makes threshold impossible.
        // For unit-norm vectors this has no effect, but for non-normalized vectors it skips pairs
        // where the Cauchy-Schwarz upper bound (normA * normB) can't reach the threshold.
        candidates.Sort((a, b) => a.norm.CompareTo(b.norm));

        var duplicates = new List<(string IdA, string IdB, float Similarity)>();
        for (int i = 0; i < candidates.Count && duplicates.Count < maxResults; i++)
        {
            for (int j = i + 1; j < candidates.Count && duplicates.Count < maxResults; j++)
            {
                var a = candidates[i];
                var b = candidates[j];
                if (a.entry.Vector.Length != b.entry.Vector.Length) continue;

                float dot = Dot(a.entry.Vector, b.entry.Vector);
                float sim = dot / (a.norm * b.norm);

                if (sim >= threshold)
                    duplicates.Add((a.entry.Id, b.entry.Id, sim));
            }
        }

        duplicates.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
        return duplicates;
    }

    /// <summary>
    /// Search ALL states including archived (for deep_recall).
    /// </summary>
    public IReadOnlyList<CognitiveSearchResult> SearchAllStates(
        float[] query, string ns, int k = 10, float minScore = 0.3f)
    {
        return Search(query, ns, k, minScore,
            includeStates: new HashSet<string> { "stm", "ltm", "archived" });
    }

    /// <summary>Record an access (increments count and updates timestamp).</summary>
    public void RecordAccess(string id)
    {
        _lock.EnterWriteLock();
        try
        {
            LoadAllNamespacesUnderWrite();
            foreach (var (ns, entries) in _namespaces)
            {
                if (entries.TryGetValue(id, out var tuple))
                {
                    tuple.Entry.AccessCount++;
                    tuple.Entry.LastAccessedAt = DateTimeOffset.UtcNow;
                    ScheduleSave(ns);
                    return;
                }
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Record an access hit for an entry within a known namespace (avoids loading all namespaces).</summary>
    public void RecordAccess(string id, string ns)
    {
        _lock.EnterWriteLock();
        try
        {
            EnsureNamespaceLoadedUnderWrite(ns);
            if (_namespaces.TryGetValue(ns, out var entries) && entries.TryGetValue(id, out var tuple))
            {
                tuple.Entry.AccessCount++;
                tuple.Entry.LastAccessedAt = DateTimeOffset.UtcNow;
                ScheduleSave(ns);
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Update an entry's lifecycle state.</summary>
    public bool SetLifecycleState(string id, string state)
    {
        _lock.EnterWriteLock();
        try
        {
            LoadAllNamespacesUnderWrite();
            foreach (var (ns, entries) in _namespaces)
            {
                if (entries.TryGetValue(id, out var tuple))
                {
                    tuple.Entry.LifecycleState = state;
                    ScheduleSave(ns);
                    return true;
                }
            }
            return false;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Update activation energy and lifecycle state for an entry atomically.
    /// Used by LifecycleEngine to avoid unsynchronized mutation.
    /// </summary>
    public bool SetActivationEnergyAndState(string id, float activationEnergy, string? newState = null)
    {
        _lock.EnterWriteLock();
        try
        {
            LoadAllNamespacesUnderWrite();
            foreach (var (ns, entries) in _namespaces)
            {
                if (entries.TryGetValue(id, out var tuple))
                {
                    tuple.Entry.ActivationEnergy = activationEnergy;
                    if (newState is not null)
                        tuple.Entry.LifecycleState = newState;
                    ScheduleSave(ns);
                    return true;
                }
            }
            return false;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Get all entries in a namespace (for lifecycle processing).</summary>
    public IReadOnlyList<CognitiveEntry> GetAllInNamespace(string ns)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureNamespaceLoaded(ns);
            if (!_namespaces.TryGetValue(ns, out var entries))
                return Array.Empty<CognitiveEntry>();
            return entries.Values.Select(t => t.Entry).ToList();
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>Get all entries across all namespaces.</summary>
    public IReadOnlyList<CognitiveEntry> GetAll()
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            LoadAllNamespaces();
            return _namespaces.Values
                .SelectMany(ns => ns.Values.Select(t => t.Entry))
                .ToList();
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>Get count of entries by lifecycle state.</summary>
    public (int stm, int ltm, int archived) GetStateCounts(string? ns = null)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            if (ns is null || ns == "*")
                LoadAllNamespaces();
            else
                EnsureNamespaceLoaded(ns);

            var entries = ns is null || ns == "*"
                ? _namespaces.Values.SelectMany(n => n.Values.Select(t => t.Entry))
                : _namespaces.TryGetValue(ns, out var nsEntries)
                    ? nsEntries.Values.Select(t => t.Entry)
                    : Enumerable.Empty<CognitiveEntry>();

            int stm = 0, ltm = 0, archived = 0;
            foreach (var e in entries)
            {
                switch (e.LifecycleState)
                {
                    case "stm": stm++; break;
                    case "ltm": ltm++; break;
                    case "archived": archived++; break;
                }
            }
            return (stm, ltm, archived);
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    // Load a namespace from disk if not already loaded.
    // Called under an upgradeable read lock — upgrades to write lock only if loading is needed.
    private void EnsureNamespaceLoaded(string ns)
    {
        if (_loadedNamespaces.Contains(ns))
            return;

        // Upgrade to write lock for the actual mutation
        _lock.EnterWriteLock();
        try
        {
            // Double-check after acquiring write lock
            if (_loadedNamespaces.Contains(ns))
                return;

            var data = _persistence.LoadNamespace(ns);
            if (!_namespaces.ContainsKey(ns))
                _namespaces[ns] = new();

            foreach (var entry in data.Entries)
            {
                float norm = Norm(entry.Vector);
                _namespaces[ns][entry.Id] = (entry, norm);
            }

            // Build BM25 index for this namespace
            if (!_bm25.HasNamespace(ns))
                _bm25.RebuildNamespace(ns, data.Entries);

            _loadedNamespaces.Add(ns);
        }
        finally { _lock.ExitWriteLock(); }
    }

    // Load a namespace from disk if not already loaded.
    // Called when already holding a write lock.
    private void EnsureNamespaceLoadedUnderWrite(string ns)
    {
        if (_loadedNamespaces.Contains(ns))
            return;

        var data = _persistence.LoadNamespace(ns);
        if (!_namespaces.ContainsKey(ns))
            _namespaces[ns] = new();

        foreach (var entry in data.Entries)
        {
            float norm = Norm(entry.Vector);
            _namespaces[ns][entry.Id] = (entry, norm);
        }

        // Build BM25 index for this namespace
        if (!_bm25.HasNamespace(ns))
            _bm25.RebuildNamespace(ns, data.Entries);

        _loadedNamespaces.Add(ns);
    }

    // Load all namespaces. Called under upgradeable read lock.
    private void LoadAllNamespaces()
    {
        foreach (var ns in _persistence.GetPersistedNamespaces())
            EnsureNamespaceLoaded(ns);
    }

    // Load all namespaces. Called when already holding write lock.
    private void LoadAllNamespacesUnderWrite()
    {
        foreach (var ns in _persistence.GetPersistedNamespaces())
            EnsureNamespaceLoadedUnderWrite(ns);
    }

    // Snapshot current namespace data and schedule a debounced write.
    // MUST be called within a write lock (data is captured immediately).
    private void ScheduleSave(string ns)
    {
        // Capture snapshot now while we hold the write lock — no lock re-entry needed
        var data = new NamespaceData();
        if (_namespaces.TryGetValue(ns, out var entries))
            data.Entries = entries.Values.Select(t => t.Entry).ToList();

        _persistence.ScheduleSave(ns, () => data);
    }

    // ── SIMD-accelerated vector math ──

    public static float Dot(float[] a, float[] b)
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

    public static float Norm(float[] v)
    {
        float dot = Dot(v, v);
        return dot == 0f ? 0f : MathF.Sqrt(dot);
    }
}
