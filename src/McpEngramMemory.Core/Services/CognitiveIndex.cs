using System.Collections.Concurrent;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Core.Services;

/// <summary>
/// Thread-safe namespace-partitioned vector index with lifecycle awareness.
///
/// Locking: per-namespace ReaderWriterLockSlim in <c>_nsLocks</c>. Each single-namespace
/// operation (Upsert, Search, Get(id, ns), Delete, RecordAccess, etc.) holds only the
/// target namespace's lock, so writers to different namespaces run in parallel. Readers
/// of a namespace parallelize with each other; a writer to the same namespace is exclusive
/// against other readers and writers OF THAT NAMESPACE only.
///
/// Cross-namespace reads (Count, GetNamespaces, GetAll, GetStateCounts(null)) are lock-free
/// and rely on ConcurrentDictionary semantics — they see a consistent snapshot per entry
/// but not a linearizable snapshot across the whole store. This is intentional and matches
/// the semantics of diagnostic counts.
///
/// Operations that resolve id → namespace (Get(id), Delete(id), RecordAccess(id),
/// SetLifecycleState, SetActivationEnergyAndState) resolve lock-free via the
/// NamespaceStore._idToNamespace ConcurrentDictionary, then acquire the resolved
/// namespace's lock for the actual work.
///
/// Events (<see cref="EntryUpserted"/>, <see cref="EntryDeleted"/>) fire AFTER the
/// per-namespace lock is released, so handlers can call back into the index safely.
/// </summary>
public sealed class CognitiveIndex : IDisposable
{
    private static readonly HashSet<string> AllStates = new() { "stm", "ltm", "archived" };

    private readonly NamespaceStore _store;
    private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> _nsLocks = new();
    private readonly BM25Index _bm25 = new();
    private readonly TokenReranker _reranker = new();
    private readonly VectorSearchEngine _vectorSearch = new();
    private readonly HybridSearchEngine _hybridSearch = new();
    private readonly DuplicateDetector _duplicateDetector = new();
    private readonly SynonymExpander _synonymExpander = new();
    private readonly DocumentEnricher _documentEnricher = new();
    private readonly QueryExpander _queryExpander = new();
    private readonly MemoryLimitsConfig _limits;

    /// <summary>
    /// Fires after an entry is successfully upserted (after the write lock is released).
    /// Parallel readers/agents can subscribe to observe new memories in real time without polling.
    /// Raised once per entry for both Upsert and UpsertBatch.
    /// Handlers run synchronously on the writer's thread — keep them cheap or offload.
    /// </summary>
    public event EventHandler<CognitiveEntry>? EntryUpserted;

    /// <summary>
    /// Fires after an entry is successfully deleted (after the write lock is released).
    /// Carries (namespace, id) so subscribers don't need to hold a stale entry reference.
    /// </summary>
    public event EventHandler<(string Namespace, string Id)>? EntryDeleted;

    public CognitiveIndex(IStorageProvider persistence, MemoryLimitsConfig? limits = null)
    {
        _store = new NamespaceStore(persistence, _bm25);
        _limits = limits ?? new MemoryLimitsConfig();
    }

    /// <summary>
    /// Get or lazily create the ReaderWriterLockSlim for a namespace. Lock-free lookup
    /// via ConcurrentDictionary.GetOrAdd — callers never coordinate on creation.
    /// </summary>
    private ReaderWriterLockSlim NsLock(string ns)
        => _nsLocks.GetOrAdd(ns, _ => new ReaderWriterLockSlim());

    // ── Counts + Metadata ──

    /// <summary>
    /// Total entry count across all loaded namespaces. Lock-free: TotalCount is an
    /// Interlocked atomic on NamespaceStore, so this returns an eventually-consistent
    /// snapshot under concurrent writers — exact for diagnostic / memory-limit checks.
    /// </summary>
    public int Count
    {
        get
        {
            _store.LoadAll();
            return _store.TotalCount;
        }
    }

    /// <summary>Count entries in a specific namespace. Per-namespace read lock.</summary>
    public int CountInNamespace(string ns)
    {
        var nsLock = NsLock(ns);
        nsLock.EnterReadLock();
        try
        {
            _store.EnsureLoaded(ns);
            return _store.GetNamespace(ns)?.Count ?? 0;
        }
        finally { nsLock.ExitReadLock(); }
    }

    /// <summary>
    /// Get all known namespace names. Lock-free: reads from a ConcurrentDictionary snapshot
    /// and the persisted-namespace list from storage.
    /// </summary>
    public IReadOnlyList<string> GetNamespaces()
        => _store.GetNamespaceNames();

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

        var nsLock = NsLock(entry.Ns);
        nsLock.EnterWriteLock();
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
        finally { nsLock.ExitWriteLock(); }

        // Fire event after lock release so handlers can call back into the index safely.
        EntryUpserted?.Invoke(this, entry);
    }

    /// <summary>Batch upsert entries with a single write-lock acquisition.</summary>
    public int UpsertBatch(IReadOnlyList<CognitiveEntry> entries)
    {
        if (entries.Count == 0) return 0;

        // Pre-compute enrichment, norms, and quantization outside the lock
        var prepared = new List<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Keywords is null && !string.IsNullOrWhiteSpace(entry.Text))
                entry.Keywords = _documentEnricher.Enrich(entry.Text);
            float norm = VectorMath.Norm(entry.Vector);
            var quantized = entry.LifecycleState is "ltm" or "archived"
                ? VectorQuantizer.Quantize(entry.Vector)
                : null;
            prepared.Add((entry, norm, quantized));
        }

        // Group by namespace so we can take one write lock per ns (parallel across ns).
        // Batch entries that belong to the same ns share that ns's write lock for the
        // duration of their sub-batch; ns A and ns B never block each other.
        var totalLimitHit = false;
        var accepted = new List<CognitiveEntry>(prepared.Count);
        foreach (var nsGroup in prepared.GroupBy(p => p.Entry.Ns))
        {
            if (totalLimitHit) break;

            var nsLock = NsLock(nsGroup.Key);
            nsLock.EnterWriteLock();
            try
            {
                _store.EnsureLoaded(nsGroup.Key);
                var nsEntries = _store.GetOrCreateNamespace(nsGroup.Key);

                foreach (var (entry, norm, quantized) in nsGroup)
                {
                    if (!nsEntries.ContainsKey(entry.Id))
                    {
                        if (nsEntries.Count >= _limits.MaxNamespaceSize)
                            continue; // skip entries that would exceed namespace limit
                        if (_store.TotalCount >= _limits.MaxTotalCount)
                        {
                            totalLimitHit = true;
                            break; // stop if total limit reached
                        }
                    }

                    nsEntries[entry.Id] = (entry, norm, quantized);
                    _store.TrackEntry(entry.Id, entry.Ns);
                    _store.IndexBM25(entry);
                    _store.AddToHnsw(entry.Ns, entry.Id, entry.Vector);
                    _store.ScheduleEntryUpsert(entry.Ns, entry);
                    accepted.Add(entry);
                }
            }
            finally { nsLock.ExitWriteLock(); }
        }

        // Fire events after all locks released, one per accepted entry.
        var handler = EntryUpserted;
        if (handler is not null)
        {
            foreach (var entry in accepted)
                handler(this, entry);
        }

        return accepted.Count;
    }

    /// <summary>Get an entry by ID, searching all namespaces. Resolves id→ns lock-free then takes that ns's read lock.</summary>
    public CognitiveEntry? Get(string id)
    {
        if (!_store.TryResolveOrLoad(id, out var ns))
            return null;

        var nsLock = NsLock(ns);
        nsLock.EnterReadLock();
        try
        {
            var resolved = _store.GetNamespace(ns);
            if (resolved is not null && resolved.TryGetValue(id, out var tuple))
                return tuple.Entry;
            return null;
        }
        finally { nsLock.ExitReadLock(); }
    }

    /// <summary>Get an entry by ID within a specific namespace. Per-namespace read lock.</summary>
    public CognitiveEntry? Get(string id, string ns)
    {
        var nsLock = NsLock(ns);
        nsLock.EnterReadLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is not null && nsEntries.TryGetValue(id, out var tuple))
                return tuple.Entry;
            return null;
        }
        finally { nsLock.ExitReadLock(); }
    }

    /// <summary>Delete an entry by ID, searching all namespaces. Resolves id→ns lock-free then takes that ns's write lock.</summary>
    public bool Delete(string id)
    {
        if (!_store.TryResolveOrLoad(id, out var ns))
            return false;

        string? deletedFromNs = null;
        var nsLock = NsLock(ns);
        nsLock.EnterWriteLock();
        try
        {
            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is not null && nsEntries.TryRemove(id, out _))
            {
                _store.UntrackEntry(id);
                _store.RemoveBM25(id, ns);
                _store.RemoveFromHnsw(ns, id);
                _store.ScheduleEntryDelete(ns, id);
                deletedFromNs = ns;
            }
        }
        finally { nsLock.ExitWriteLock(); }

        if (deletedFromNs is not null)
        {
            EntryDeleted?.Invoke(this, (deletedFromNs, id));
            return true;
        }
        return false;
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
        var nsLock = NsLock(request.Namespace);
        nsLock.EnterReadLock();
        try
        {
            _store.EnsureLoaded(request.Namespace);
            var nsEntries = _store.GetNamespace(request.Namespace);
            if (nsEntries is null || nsEntries.Count == 0)
                return Array.Empty<CognitiveSearchResult>();
            snapshot = nsEntries.Values.ToList();
            hnswIndex = _store.GetHnswIndex(request.Namespace);
        }
        finally { nsLock.ExitReadLock(); }

        // When diversity is active, fetch more candidates so MMR has a broader pool
        int diversityMultiplier = request.Diversity ? 3 : 1;

        if (request.Hybrid && request.QueryText is not null)
        {
            // Expand query with domain synonyms for BM25 vocabulary bridging
            var expandedQueryText = _synonymExpander.Expand(request.QueryText);

            // Scale vector candidate pool with namespace size for better hybrid recall
            int candidateK = snapshot.Count >= 5000
                ? Math.Max(request.K * 12, 80)
                : Math.Max(request.K * 6, 30);
            var vectorResults = _vectorSearch.Search(
                request.Query, snapshot, candidateK, request.MinScore,
                request.Category, request.IncludeStates, false, hnswIndex);
            int hybridK = request.K * diversityMultiplier;
            var hybridResults = _hybridSearch.HybridSearch(
                vectorResults, expandedQueryText, request.Namespace, hybridK,
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
                        vectorResults, prfQuery, request.Namespace, hybridK,
                        request.IncludeStates, request.Category,
                        request.Rerank, request.RrfK, _bm25, _reranker, Get, request.Query, snapshot.Count);
                    // Use PRF results if they improve top score
                    if (prfResults.Count > 0 && prfResults[0].Score > hybridResults[0].Score)
                        return ApplyDiversity(ApplyCategoryBoost(prfResults, request.QueryText), request, snapshot);
                }
            }

            return ApplyDiversity(ApplyCategoryBoost(hybridResults, request.QueryText), request, snapshot);
        }

        // Vector-only search with auto-escalation to hybrid when confidence is low
        int vectorK = request.K * diversityMultiplier;
        var vectorOnlyResults = _vectorSearch.Search(
            request.Query, snapshot, vectorK, request.MinScore,
            request.Category, request.IncludeStates, request.SummaryFirst, hnswIndex);

        // Auto-escalate: if top vector result is low confidence and we have query text,
        // retry as hybrid search to let BM25 rescue keyword-dependent queries
        if (request.QueryText is not null &&
            vectorOnlyResults.Count > 0 &&
            vectorOnlyResults[0].Score < 0.50f &&
            !request.SummaryFirst)
        {
            int candidateK = snapshot.Count >= 5000
                ? Math.Max(request.K * 10, 60)
                : Math.Max(request.K * 5, 25);
            var broadVectorResults = _vectorSearch.Search(
                request.Query, snapshot, candidateK, request.MinScore,
                request.Category, request.IncludeStates, false, hnswIndex);
            var expandedQueryText = _synonymExpander.Expand(request.QueryText);
            var escalatedResults = ApplyCategoryBoost(_hybridSearch.HybridSearch(
                broadVectorResults, expandedQueryText, request.Namespace, request.K * diversityMultiplier,
                request.IncludeStates, request.Category,
                false, request.RrfK, _bm25, _reranker, Get, request.Query, snapshot.Count), request.QueryText);
            return ApplyDiversity(escalatedResults, request, snapshot);
        }

        return ApplyDiversity(vectorOnlyResults, request, snapshot);
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
        float[] query, string ns, int k = 10, float minScore = 0.3f,
        string? queryText = null, bool hybrid = false, bool rerank = false)
        => Search(new SearchRequest
        {
            Query = query, Namespace = ns, K = k, MinScore = minScore,
            IncludeStates = AllStates,
            QueryText = queryText ?? string.Empty, Hybrid = hybrid, Rerank = rerank
        });

    /// <summary>
    /// Search across multiple namespaces and merge results using Reciprocal Rank Fusion.
    /// Returns results annotated with their source namespace.
    /// </summary>
    public IReadOnlyList<Models.CrossSearchResult> SearchMultiple(
        float[] query, IReadOnlyList<string> namespaces, string? queryText = null,
        int k = 5, float minScore = 0f, string? category = null,
        HashSet<string>? includeStates = null, bool hybrid = false,
        bool rerank = false, int rrfK = 60, bool summaryFirst = false,
        bool diversity = false, float diversityLambda = 0.5f)
    {
        if (namespaces.Count == 0)
            return Array.Empty<Models.CrossSearchResult>();

        // Search each namespace independently. When diversity is requested we route
        // through the SearchRequest path to pick up cluster-aware MMR reranking;
        // otherwise we keep the fast hybrid/vector path for backward compat.
        var allRanked = new Dictionary<string, (Models.CrossSearchResult Result, float RrfScore)>();

        foreach (var ns in namespaces)
        {
            IReadOnlyList<CognitiveSearchResult> nsResults;
            if (diversity)
            {
                nsResults = Search(new SearchRequest
                {
                    Query = query,
                    Namespace = ns,
                    QueryText = queryText,
                    K = k,
                    MinScore = minScore,
                    Category = category,
                    IncludeStates = includeStates,
                    Hybrid = hybrid && queryText is not null,
                    Rerank = rerank,
                    RrfK = rrfK,
                    SummaryFirst = summaryFirst,
                    Diversity = true,
                    DiversityLambda = diversityLambda,
                });
            }
            else if (hybrid && queryText is not null)
            {
                nsResults = HybridSearch(query, queryText, ns, k, minScore, category, includeStates, rerank, rrfK);
            }
            else
            {
                nsResults = Search(query, ns, k, minScore, category, includeStates, summaryFirst);
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
        var nsLock = NsLock(ns);
        nsLock.EnterReadLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is null)
                return Array.Empty<(string, string, float)>();

            nsEntries.TryGetValue(entryId, out var target);
            return _duplicateDetector.FindDuplicatesForEntry(entryId, target, nsEntries, threshold);
        }
        finally { nsLock.ExitReadLock(); }
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
        var nsLock = NsLock(ns);
        nsLock.EnterReadLock();
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
        finally { nsLock.ExitReadLock(); }

        // Sort by norm ascending for early-exit optimization
        candidates.Sort((a, b) => a.Norm.CompareTo(b.Norm));
        return _duplicateDetector.FindDuplicates(candidates, threshold, maxResults);
    }

    // ── Access Tracking ──

    /// <summary>Record an access (increments count and updates timestamp). Resolves id→ns lock-free, then per-ns write.</summary>
    public void RecordAccess(string id)
    {
        if (!_store.TryResolveOrLoad(id, out var ns))
            return;

        var nsLock = NsLock(ns);
        nsLock.EnterWriteLock();
        try
        {
            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is not null && nsEntries.TryGetValue(id, out var tuple))
            {
                tuple.Entry.AccessCount++;
                tuple.Entry.LastAccessedAt = DateTimeOffset.UtcNow;
                _store.ScheduleEntryUpsert(ns, tuple.Entry);
            }
        }
        finally { nsLock.ExitWriteLock(); }
    }

    /// <summary>Record an access hit within a known namespace. Per-ns write lock.</summary>
    public void RecordAccess(string id, string ns)
    {
        var nsLock = NsLock(ns);
        nsLock.EnterWriteLock();
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
        finally { nsLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Boost activation energy for an entry (spreading activation). Per-ns write on the provided ns;
    /// falls back to id→ns resolve if not found there. Returns true if entry was found and updated.
    /// </summary>
    public bool BoostActivationEnergy(string id, string ns, float delta)
    {
        // Fast path: try the caller's supplied ns first
        var nsLock = NsLock(ns);
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is not null && nsEntries.TryGetValue(id, out var tuple))
            {
                tuple.Entry.ActivationEnergy += delta;
                _store.ScheduleEntryUpsert(ns, tuple.Entry);
                return true;
            }
        }
        finally { nsLock.ExitWriteLock(); }

        // Fallback: resolve id→ns lock-free, then take the resolved ns's write lock
        if (!_store.TryResolveOrLoad(id, out var resolvedNs) || resolvedNs == ns)
            return false;

        var resolvedLock = NsLock(resolvedNs);
        resolvedLock.EnterWriteLock();
        try
        {
            var resolvedEntries = _store.GetNamespace(resolvedNs);
            if (resolvedEntries is not null && resolvedEntries.TryGetValue(id, out var resolvedTuple))
            {
                resolvedTuple.Entry.ActivationEnergy += delta;
                _store.ScheduleEntryUpsert(resolvedNs, resolvedTuple.Entry);
                return true;
            }
            return false;
        }
        finally { resolvedLock.ExitWriteLock(); }
    }

    // ── Lifecycle State Management ──

    /// <summary>Update an entry's lifecycle state. Resolves id→ns lock-free, then per-ns write. Quantizes on STM→LTM, dequantizes on →STM.</summary>
    public bool SetLifecycleState(string id, string state)
    {
        if (!_store.TryResolveOrLoad(id, out var ns))
            return false;

        var nsLock = NsLock(ns);
        nsLock.EnterWriteLock();
        try
        {
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
        finally { nsLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Update lifecycle state for multiple entries. Groups by resolved namespace so each ns is
    /// locked once for its sub-batch; entries in different namespaces run under different locks.
    /// </summary>
    public int SetLifecycleStateBatch(IEnumerable<string> ids, string state)
    {
        // Resolve all ids first (lock-free), then group by resolved ns so we take each ns's
        // write lock exactly once.
        var byNs = new Dictionary<string, List<string>>();
        foreach (var id in ids)
        {
            if (!_store.TryResolveOrLoad(id, out var ns))
                continue;
            if (!byNs.TryGetValue(ns, out var list))
                byNs[ns] = list = new List<string>();
            list.Add(id);
        }

        int updated = 0;
        foreach (var (ns, idList) in byNs)
        {
            var nsLock = NsLock(ns);
            nsLock.EnterWriteLock();
            try
            {
                var nsEntries = _store.GetNamespace(ns);
                if (nsEntries is null) continue;

                foreach (var id in idList)
                {
                    if (nsEntries.TryGetValue(id, out var tuple))
                    {
                        var previousState = tuple.Entry.LifecycleState;
                        tuple.Entry.LifecycleState = state;
                        UpdateQuantization(nsEntries, id, tuple, previousState, state);
                        _store.ScheduleEntryUpsert(ns, tuple.Entry);
                        updated++;
                    }
                }
            }
            finally { nsLock.ExitWriteLock(); }
        }

        return updated;
    }

    /// <summary>Update activation energy and lifecycle state atomically. Resolves id→ns lock-free, then per-ns write.</summary>
    public bool SetActivationEnergyAndState(string id, float activationEnergy, string? newState = null)
    {
        if (!_store.TryResolveOrLoad(id, out var ns))
            return false;

        var nsLock = NsLock(ns);
        nsLock.EnterWriteLock();
        try
        {
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
        finally { nsLock.ExitWriteLock(); }
    }

    // ── Bulk Reads ──

    /// <summary>Get all entries in a namespace. Per-namespace read lock.</summary>
    public IReadOnlyList<CognitiveEntry> GetAllInNamespace(string ns)
    {
        var nsLock = NsLock(ns);
        nsLock.EnterReadLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is null)
                return Array.Empty<CognitiveEntry>();
            return nsEntries.Values.Select(t => t.Entry).ToList();
        }
        finally { nsLock.ExitReadLock(); }
    }

    /// <summary>Delete all entries in a namespace and remove it from in-memory state. Does NOT cascade to graph edges or clusters — callers must handle that.</summary>
    public int DeleteAllInNamespace(string ns)
    {
        var nsLock = NsLock(ns);
        nsLock.EnterWriteLock();
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
            // Schedule per-entry persistence deletes for incremental backends (e.g. SQLite).
            // BM25 and HNSW cleanup is handled in bulk by RemoveNamespace below — no per-entry
            // removal needed here.
            foreach (var id in nsEntries.Keys.ToList())
                _store.ScheduleEntryDelete(ns, id);

            // Removes namespace from _namespaces, _idToNamespace, _loadedNamespaces, BM25,
            // _hnswIndices, and deletes the persisted HNSW snapshot in one O(1) bulk step.
            _store.RemoveNamespace(ns);
            return count;
        }
        finally { nsLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Get all entries across all namespaces. Lock-free: snapshots ConcurrentDictionary entries.
    /// Diagnostic-grade consistency — may observe in-flight writes to any single ns but never
    /// returns a torn entry record.
    /// </summary>
    public IReadOnlyList<CognitiveEntry> GetAll()
    {
        _store.LoadAll();
        return _store.AllNamespaces
            .SelectMany(kv => kv.Value.Values.Select(t => t.Entry))
            .ToList();
    }

    /// <summary>
    /// Get count of entries by lifecycle state. Single-ns path takes that ns's read lock;
    /// cross-ns path is lock-free (diagnostic-grade consistency via ConcurrentDictionary).
    /// </summary>
    public (int stm, int ltm, int archived) GetStateCounts(string? ns = null)
    {
        if (ns is null || ns == "*")
        {
            _store.LoadAll();
            var entries = _store.AllNamespaces.SelectMany(kv => kv.Value.Values.Select(t => t.Entry));
            return CountStates(entries);
        }
        else
        {
            var nsLock = NsLock(ns);
            nsLock.EnterReadLock();
            try
            {
                _store.EnsureLoaded(ns);
                var entries = _store.GetNamespace(ns) is { } nsEntries
                    ? nsEntries.Values.Select(t => t.Entry)
                    : Enumerable.Empty<CognitiveEntry>();
                return CountStates(entries);
            }
            finally { nsLock.ExitReadLock(); }
        }

        static (int stm, int ltm, int archived) CountStates(IEnumerable<CognitiveEntry> entries)
        {
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
    }

    /// <summary>Re-embed all entries in a namespace. Per-namespace write lock.</summary>
    public (int Updated, int Skipped) RebuildEmbeddings(string ns, IEmbeddingService embedding)
    {
        var nsLock = NsLock(ns);
        nsLock.EnterWriteLock();
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
            {
                _store.ScheduleSave(ns);
                // Invalidate the stale HNSW index so it is rebuilt lazily on the next search.
                // The old topology references pre-re-embedding vectors and would return wrong candidates.
                _store.InvalidateHnswIndex(ns);
            }

            return (updated, skipped);
        }
        finally { nsLock.ExitWriteLock(); }
    }

    public void Dispose()
    {
        foreach (var kv in _nsLocks)
            kv.Value.Dispose();
        _nsLocks.Clear();
    }

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

    /// <summary>Apply cluster-aware MMR diversity reranking when requested.</summary>
    private static IReadOnlyList<CognitiveSearchResult> ApplyDiversity(
        IReadOnlyList<CognitiveSearchResult> results,
        SearchRequest request,
        IReadOnlyCollection<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> snapshot)
    {
        if (!request.Diversity || results.Count <= 1)
            return results.Count > request.K ? results.Take(request.K).ToList() : results;

        // Build vector lookup from snapshot for MMR inter-result similarity
        var vectorLookup = new Dictionary<string, float[]>(snapshot.Count);
        foreach (var (entry, _, _) in snapshot)
            vectorLookup[entry.Id] = entry.Vector;

        return DiversityReranker.Rerank(
            results, request.Query, id => vectorLookup.GetValueOrDefault(id),
            request.K, request.DiversityLambda);
    }

    private static void UpdateQuantization(
        ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> entries,
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
