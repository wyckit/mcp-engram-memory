using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Core.Services;

/// <summary>
/// Thread-safe namespace-partitioned vector index with lifecycle awareness.
/// Thin facade that delegates search to VectorSearchEngine/HybridSearchEngine,
/// duplicate detection to DuplicateDetector, and manages CRUD + locking.
/// </summary>
public sealed class CognitiveIndex : IDisposable
{
    private static readonly HashSet<string> AllStates = new() { "stm", "ltm", "archived" };

    private readonly NamespaceStore _store;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly BM25Index _bm25 = new();
    private readonly TokenReranker _reranker = new();
    private readonly VectorSearchEngine _vectorSearch = new();
    private readonly HybridSearchEngine _hybridSearch = new();
    private readonly DuplicateDetector _duplicateDetector = new();
    private readonly SynonymExpander _synonymExpander = new();
    private readonly DocumentEnricher _documentEnricher = new();
    private readonly QueryExpander _queryExpander = new();
    private readonly MemoryLimitsConfig _limits;

    public CognitiveIndex(IStorageProvider persistence, MemoryLimitsConfig? limits = null)
    {
        _store = new NamespaceStore(persistence, _bm25);
        _limits = limits ?? new MemoryLimitsConfig();
    }

    // ── Counts + Metadata ──

    /// <summary>Total entry count across all loaded namespaces.</summary>
    public int Count
    {
        get
        {
            _lock.EnterUpgradeableReadLock();
            try
            {
                _store.LoadAll();
                return _store.TotalCount;
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
            _store.EnsureLoaded(ns);
            return _store.GetNamespace(ns)?.Count ?? 0;
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>Get all known namespace names.</summary>
    public IReadOnlyList<string> GetNamespaces()
    {
        _lock.EnterReadLock();
        try { return _store.GetNamespaceNames(); }
        finally { _lock.ExitReadLock(); }
    }

    // ── CRUD ──

    /// <summary>Add or replace a cognitive entry. LTM/archived entries are auto-quantized for fast search.</summary>
    public void Upsert(CognitiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Auto-enrich keywords for BM25 vocabulary bridging
        if (entry.Keywords is null && !string.IsNullOrWhiteSpace(entry.Text))
            entry.Keywords = _documentEnricher.Enrich(entry.Text);

        float norm = VectorMath.Norm(entry.Vector);
        var quantized = entry.LifecycleState is "ltm" or "archived"
            ? VectorQuantizer.Quantize(entry.Vector)
            : null;

        _lock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(entry.Ns);
            var nsEntries = _store.GetOrCreateNamespace(entry.Ns);

            // Enforce memory limits (skip for updates to existing entries)
            if (!nsEntries.ContainsKey(entry.Id))
            {
                if (nsEntries.Count >= _limits.MaxNamespaceSize)
                    throw new InvalidOperationException(
                        $"Namespace '{entry.Ns}' has reached the maximum size of {_limits.MaxNamespaceSize} entries.");
                if (_store.TotalCount >= _limits.MaxTotalCount)
                    throw new InvalidOperationException(
                        $"Total memory count has reached the maximum of {_limits.MaxTotalCount} entries.");
            }

            nsEntries[entry.Id] = (entry, norm, quantized);
            _store.TrackEntry(entry.Id, entry.Ns);
            _store.IndexBM25(entry);
            _store.AddToHnsw(entry.Ns, entry.Id, entry.Vector);
            _store.ScheduleEntryUpsert(entry.Ns, entry);
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Get an entry by ID, searching all namespaces.</summary>
    public CognitiveEntry? Get(string id)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            if (!_store.TryResolveOrLoad(id, out var ns))
                return null;

            var resolved = _store.GetNamespace(ns);
            if (resolved is not null && resolved.TryGetValue(id, out var tuple))
                return tuple.Entry;
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
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is not null && nsEntries.TryGetValue(id, out var tuple))
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
            if (!_store.TryResolveOrLoad(id, out var ns))
                return false;

            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is not null && nsEntries.Remove(id))
            {
                _store.UntrackEntry(id);
                _store.RemoveBM25(id, ns);
                _store.RemoveFromHnsw(ns, id);
                _store.ScheduleEntryDelete(ns, id);
                return true;
            }
            return false;
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ── Search ──

    /// <summary>Unified search entry point supporting vector-only, hybrid, and deep recall modes.</summary>
    public IReadOnlyList<CognitiveSearchResult> Search(SearchRequest request)
    {
        if (request.Query is null || request.Query.Length == 0)
            throw new ArgumentException("Query vector must not be null or empty.", nameof(request));
        if (request.K <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "K must be positive.");

        IReadOnlyCollection<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> snapshot;
        HnswIndex? hnswIndex;
        _lock.EnterUpgradeableReadLock();
        try
        {
            _store.EnsureLoaded(request.Namespace);
            var nsEntries = _store.GetNamespace(request.Namespace);
            if (nsEntries is null || nsEntries.Count == 0)
                return Array.Empty<CognitiveSearchResult>();
            snapshot = nsEntries.Values.ToList();
            hnswIndex = _store.GetHnswIndex(request.Namespace);
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        if (request.Hybrid && request.QueryText is not null)
        {
            // Expand query with domain synonyms for BM25 vocabulary bridging
            var expandedQueryText = _synonymExpander.Expand(request.QueryText);

            int candidateK = Math.Max(request.K * 6, 30);
            var vectorResults = _vectorSearch.Search(
                request.Query, snapshot, candidateK, request.MinScore,
                request.Category, request.IncludeStates, false, hnswIndex);
            var hybridResults = _hybridSearch.HybridSearch(
                vectorResults, expandedQueryText, request.Namespace, request.K,
                request.IncludeStates, request.Category,
                request.Rerank, request.RrfK, _bm25, _reranker, Get, request.Query, snapshot.Count);

            // Auto-PRF: if top hybrid result is low confidence, expand query with
            // terms from initial results and re-search for improved recall
            if (hybridResults.Count > 0 &&
                hybridResults[0].Score < 0.04f &&
                hybridResults.Count >= 3)
            {
                var prfQuery = _queryExpander.Expand(expandedQueryText, hybridResults, maxTerms: 6, minDocFreq: 2);
                if (prfQuery != expandedQueryText)
                {
                    var prfResults = _hybridSearch.HybridSearch(
                        vectorResults, prfQuery, request.Namespace, request.K,
                        request.IncludeStates, request.Category,
                        request.Rerank, request.RrfK, _bm25, _reranker, Get, request.Query, snapshot.Count);
                    // Use PRF results if they improve top score
                    if (prfResults.Count > 0 && prfResults[0].Score > hybridResults[0].Score)
                        return ApplyCategoryBoost(prfResults, request.QueryText);
                }
            }

            return ApplyCategoryBoost(hybridResults, request.QueryText);
        }

        // Vector-only search with auto-escalation to hybrid when confidence is low
        var vectorOnlyResults = _vectorSearch.Search(
            request.Query, snapshot, request.K, request.MinScore,
            request.Category, request.IncludeStates, request.SummaryFirst, hnswIndex);

        // Auto-escalate: if top vector result is low confidence and we have query text,
        // retry as hybrid search to let BM25 rescue keyword-dependent queries
        if (request.QueryText is not null &&
            vectorOnlyResults.Count > 0 &&
            vectorOnlyResults[0].Score < 0.50f &&
            !request.SummaryFirst)
        {
            int candidateK = Math.Max(request.K * 5, 25);
            var broadVectorResults = _vectorSearch.Search(
                request.Query, snapshot, candidateK, request.MinScore,
                request.Category, request.IncludeStates, false, hnswIndex);
            var expandedQueryText = _synonymExpander.Expand(request.QueryText);
            return ApplyCategoryBoost(_hybridSearch.HybridSearch(
                broadVectorResults, expandedQueryText, request.Namespace, request.K,
                request.IncludeStates, request.Category,
                false, request.RrfK, _bm25, _reranker, Get, request.Query, snapshot.Count), request.QueryText);
        }

        return vectorOnlyResults;
    }

    /// <summary>Namespace-scoped k-nearest-neighbor search with two-stage Int8 screening pipeline.</summary>
    public IReadOnlyList<CognitiveSearchResult> Search(
        float[] query, string ns, int k = 5, float minScore = 0f,
        string? category = null, HashSet<string>? includeStates = null, bool summaryFirst = false)
        => Search(new SearchRequest
        {
            Query = query, Namespace = ns, K = k, MinScore = minScore,
            Category = category, IncludeStates = includeStates, SummaryFirst = summaryFirst
        });

    /// <summary>Hybrid search combining vector + BM25 via Reciprocal Rank Fusion.</summary>
    public IReadOnlyList<CognitiveSearchResult> HybridSearch(
        float[] query, string queryText, string ns, int k = 5, float minScore = 0f,
        string? category = null, HashSet<string>? includeStates = null,
        bool rerank = false, int rrfK = 60)
        => Search(new SearchRequest
        {
            Query = query, QueryText = queryText, Namespace = ns, K = k, MinScore = minScore,
            Category = category, IncludeStates = includeStates, Hybrid = true, Rerank = rerank, RrfK = rrfK
        });

    /// <summary>Apply token-level reranking to existing search results.</summary>
    public IReadOnlyList<CognitiveSearchResult> Rerank(
        string queryText, IReadOnlyList<CognitiveSearchResult> results)
        => _reranker.Rerank(queryText, results);

    /// <summary>Search ALL states including archived (for deep_recall).</summary>
    public IReadOnlyList<CognitiveSearchResult> SearchAllStates(
        float[] query, string ns, int k = 10, float minScore = 0.3f)
        => Search(new SearchRequest
        {
            Query = query, Namespace = ns, K = k, MinScore = minScore,
            IncludeStates = AllStates
        });

    /// <summary>
    /// Search across multiple namespaces and merge results using Reciprocal Rank Fusion.
    /// Returns results annotated with their source namespace.
    /// </summary>
    public IReadOnlyList<Models.CrossSearchResult> SearchMultiple(
        float[] query, IReadOnlyList<string> namespaces, string? queryText = null,
        int k = 5, float minScore = 0f, string? category = null,
        HashSet<string>? includeStates = null, bool hybrid = false,
        bool rerank = false, int rrfK = 60)
    {
        if (namespaces.Count == 0)
            return Array.Empty<Models.CrossSearchResult>();

        // Search each namespace independently
        var allRanked = new Dictionary<string, (Models.CrossSearchResult Result, float RrfScore)>();

        foreach (var ns in namespaces)
        {
            IReadOnlyList<CognitiveSearchResult> nsResults;
            if (hybrid && queryText is not null)
            {
                nsResults = HybridSearch(query, queryText, ns, k, minScore, category, includeStates, rerank, rrfK);
            }
            else
            {
                nsResults = Search(query, ns, k, minScore, category, includeStates);
            }

            // Assign RRF scores based on rank within this namespace
            for (int rank = 0; rank < nsResults.Count; rank++)
            {
                var r = nsResults[rank];
                float rrfScore = 1.0f / (rrfK + rank + 1);
                var key = $"{ns}:{r.Id}";

                var crossResult = new Models.CrossSearchResult(
                    r.Id, r.Text, r.Score, ns, r.LifecycleState,
                    r.Category, r.Metadata, r.AccessCount);

                if (allRanked.TryGetValue(key, out var existing))
                {
                    // Same entry found in multiple namespaces — sum RRF scores
                    allRanked[key] = (crossResult, existing.RrfScore + rrfScore);
                }
                else
                {
                    allRanked[key] = (crossResult, rrfScore);
                }
            }
        }

        // Sort by RRF score descending, take top-K
        return allRanked.Values
            .OrderByDescending(x => x.RrfScore)
            .Take(k)
            .Select(x => x.Result)
            .ToList();
    }

    // ── Duplicate Detection (delegated to DuplicateDetector) ──

    /// <summary>Find near-duplicates for a single entry within its namespace (O(N) scan).</summary>
    public IReadOnlyList<(string IdA, string IdB, float Similarity)> FindDuplicatesForEntry(
        string ns, string entryId, float threshold = 0.95f)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is null)
                return Array.Empty<(string, string, float)>();

            nsEntries.TryGetValue(entryId, out var target);
            return _duplicateDetector.FindDuplicatesForEntry(entryId, target, nsEntries, threshold);
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>Find near-duplicate entries within a namespace by pairwise cosine similarity.</summary>
    public IReadOnlyList<(string IdA, string IdB, float Similarity)> FindDuplicates(
        string ns, float threshold = 0.95f, string? category = null,
        HashSet<string>? includeStates = null, int maxResults = 100)
    {
        if (threshold < 0f || threshold > 1f)
            throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be between 0 and 1.");

        includeStates ??= new HashSet<string> { "stm", "ltm" };

        List<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates;
        _lock.EnterUpgradeableReadLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is null)
                return Array.Empty<(string, string, float)>();

            candidates = nsEntries.Values
                .Where(t => includeStates.Contains(t.Entry.LifecycleState)
                    && (category is null || t.Entry.Category == category)
                    && t.Norm > 0f)
                .ToList();
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        // Sort by norm ascending for early-exit optimization
        candidates.Sort((a, b) => a.Norm.CompareTo(b.Norm));
        return _duplicateDetector.FindDuplicates(candidates, threshold, maxResults);
    }

    // ── Access Tracking ──

    /// <summary>Record an access (increments count and updates timestamp).</summary>
    public void RecordAccess(string id)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_store.TryResolveOrLoad(id, out var ns))
                return;

            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is not null && nsEntries.TryGetValue(id, out var tuple))
            {
                tuple.Entry.AccessCount++;
                tuple.Entry.LastAccessedAt = DateTimeOffset.UtcNow;
                _store.ScheduleEntryUpsert(ns, tuple.Entry);
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Record an access hit within a known namespace.</summary>
    public void RecordAccess(string id, string ns)
    {
        _lock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is not null && nsEntries.TryGetValue(id, out var tuple))
            {
                tuple.Entry.AccessCount++;
                tuple.Entry.LastAccessedAt = DateTimeOffset.UtcNow;
                _store.ScheduleEntryUpsert(ns, tuple.Entry);
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ── Lifecycle State Management ──

    /// <summary>Update an entry's lifecycle state. Quantizes on STM→LTM, dequantizes on →STM.</summary>
    public bool SetLifecycleState(string id, string state)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_store.TryResolveOrLoad(id, out var ns))
                return false;

            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is not null && nsEntries.TryGetValue(id, out var tuple))
            {
                var previousState = tuple.Entry.LifecycleState;
                tuple.Entry.LifecycleState = state;
                UpdateQuantization(nsEntries, id, tuple, previousState, state);
                _store.ScheduleEntryUpsert(ns, tuple.Entry);
                return true;
            }
            return false;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Update lifecycle state for multiple entries in a single write lock acquisition.</summary>
    public int SetLifecycleStateBatch(IEnumerable<string> ids, string state)
    {
        _lock.EnterWriteLock();
        try
        {
            int updated = 0;

            foreach (var id in ids)
            {
                if (!_store.TryResolveOrLoad(id, out var ns))
                    continue;

                var nsEntries = _store.GetNamespace(ns);
                if (nsEntries is not null && nsEntries.TryGetValue(id, out var tuple))
                {
                    var previousState = tuple.Entry.LifecycleState;
                    tuple.Entry.LifecycleState = state;
                    UpdateQuantization(nsEntries, id, tuple, previousState, state);
                    _store.ScheduleEntryUpsert(ns, tuple.Entry);
                    updated++;
                }
            }

            return updated;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Update activation energy and lifecycle state atomically.</summary>
    public bool SetActivationEnergyAndState(string id, float activationEnergy, string? newState = null)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_store.TryResolveOrLoad(id, out var ns))
                return false;

            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is not null && nsEntries.TryGetValue(id, out var tuple))
            {
                var previousState = tuple.Entry.LifecycleState;
                tuple.Entry.ActivationEnergy = activationEnergy;
                if (newState is not null)
                {
                    tuple.Entry.LifecycleState = newState;
                    UpdateQuantization(nsEntries, id, tuple, previousState, newState);
                }
                _store.ScheduleEntryUpsert(ns, tuple.Entry);
                return true;
            }
            return false;
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ── Bulk Reads ──

    /// <summary>Get all entries in a namespace.</summary>
    public IReadOnlyList<CognitiveEntry> GetAllInNamespace(string ns)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is null)
                return Array.Empty<CognitiveEntry>();
            return nsEntries.Values.Select(t => t.Entry).ToList();
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>Delete all entries in a namespace and remove it from in-memory state. Does NOT cascade to graph edges or clusters — callers must handle that.</summary>
    public int DeleteAllInNamespace(string ns)
    {
        _lock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is null || nsEntries.Count == 0)
            {
                _store.RemoveNamespace(ns);
                return 0;
            }

            int count = nsEntries.Count;
            // Delete each entry from BM25 and HNSW indices
            foreach (var id in nsEntries.Keys.ToList())
            {
                _store.RemoveBM25(id, ns);
                _store.RemoveFromHnsw(ns, id);
                _store.ScheduleEntryDelete(ns, id);
            }

            _store.RemoveNamespace(ns);
            return count;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Get all entries across all namespaces.</summary>
    public IReadOnlyList<CognitiveEntry> GetAll()
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            _store.LoadAll();
            return _store.AllNamespaces
                .SelectMany(kv => kv.Value.Values.Select(t => t.Entry))
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
                _store.LoadAll();
            else
                _store.EnsureLoaded(ns);

            var entries = ns is null || ns == "*"
                ? _store.AllNamespaces.SelectMany(kv => kv.Value.Values.Select(t => t.Entry))
                : _store.GetNamespace(ns) is { } nsEntries
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

    /// <summary>Re-embed all entries in a namespace.</summary>
    public (int Updated, int Skipped) RebuildEmbeddings(string ns, IEmbeddingService embedding)
    {
        _lock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is null)
                return (0, 0);

            int updated = 0, skipped = 0;
            var ids = nsEntries.Keys.ToList();

            foreach (var id in ids)
            {
                var (oldEntry, _, _) = nsEntries[id];
                if (string.IsNullOrWhiteSpace(oldEntry.Text))
                {
                    skipped++;
                    continue;
                }

                float[] newVector = embedding.Embed(oldEntry.Text);
                var newEntry = new CognitiveEntry(
                    oldEntry.Id, newVector, oldEntry.Ns, oldEntry.Text,
                    oldEntry.Category, oldEntry.Metadata, oldEntry.LifecycleState,
                    oldEntry.CreatedAt, oldEntry.LastAccessedAt, oldEntry.AccessCount,
                    oldEntry.ActivationEnergy, oldEntry.IsSummaryNode, oldEntry.SourceClusterId);

                var quantized = newEntry.LifecycleState is "ltm" or "archived"
                    ? VectorQuantizer.Quantize(newVector)
                    : null;
                nsEntries[id] = (newEntry, VectorMath.Norm(newVector), quantized);
                _store.IndexBM25(newEntry);
                updated++;
            }

            if (updated > 0)
                _store.ScheduleSave(ns);

            return (updated, skipped);
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void Dispose() => _lock.Dispose();

    // ── Internal Helpers ──

    /// <summary>Apply a small score boost when query terms overlap with entry categories.</summary>
    private static IReadOnlyList<CognitiveSearchResult> ApplyCategoryBoost(
        IReadOnlyList<CognitiveSearchResult> results, string? queryText)
    {
        if (queryText is null || results.Count == 0)
            return results;

        var queryTokens = BM25Index.Tokenize(queryText).ToHashSet();
        if (queryTokens.Count == 0) return results;

        var boosted = new List<CognitiveSearchResult>(results.Count);
        foreach (var r in results)
        {
            float boost = 1f;
            if (r.Category is not null)
            {
                var catTokens = BM25Index.Tokenize(r.Category);
                foreach (var ct in catTokens)
                {
                    if (queryTokens.Contains(ct))
                    {
                        boost = 1.15f; // 15% category match boost
                        break;
                    }
                }
            }

            boosted.Add(new CognitiveSearchResult(
                r.Id, r.Text, r.Score * boost,
                r.LifecycleState, r.ActivationEnergy,
                r.Category, r.Metadata,
                r.IsSummaryNode, r.SourceClusterId, r.AccessCount));
        }

        boosted.Sort((a, b) => b.Score.CompareTo(a.Score));
        return boosted;
    }

    private static void UpdateQuantization(
        Dictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> entries,
        string id,
        (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized) tuple,
        string previousState, string newState)
    {
        bool wasQuantizable = previousState is "ltm" or "archived";
        bool isQuantizable = newState is "ltm" or "archived";

        if (!wasQuantizable && isQuantizable && tuple.Quantized is null)
            entries[id] = (tuple.Entry, tuple.Norm, VectorQuantizer.Quantize(tuple.Entry.Vector));
        else if (wasQuantizable && !isQuantizable && tuple.Quantized is not null)
            entries[id] = (tuple.Entry, tuple.Norm, null);
    }

}
