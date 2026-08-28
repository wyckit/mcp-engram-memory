using System.Collections.Concurrent;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Evaluation;
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
    // 0 = live, 1 = disposed. Int rather than bool so Interlocked.Exchange can provide
    // an atomic "once and only once" transition across concurrent Dispose callers.
    private int _disposedFlag;
    private readonly BM25Index _bm25 = new();
    private readonly TokenReranker _reranker = new();
    private readonly VectorSearchEngine _vectorSearch = new();
    private readonly HybridSearchEngine _hybridSearch;
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

    public CognitiveIndex(IStorageProvider persistence, MemoryLimitsConfig? limits = null, MetricsCollector? metrics = null)
    {
        _store = new NamespaceStore(persistence, _bm25);
        _limits = limits ?? new MemoryLimitsConfig();
        _hybridSearch = new HybridSearchEngine(metrics);
    }

    /// <summary>
    /// Get or lazily create the ReaderWriterLockSlim for a namespace.
    /// Throws <see cref="ObjectDisposedException"/> if Dispose has run (or begins to run
    /// during this call), including the TOCTOU window between the pre-check and
    /// GetOrAdd. A just-created-then-orphaned lock is disposed inline so nothing leaks.
    ///
    /// Hot path (ns already in the dict): one TryGetValue, zero allocations.
    /// Cold path (first use of a ns): one RWLS allocation, race-safe publication.
    /// </summary>
    private ReaderWriterLockSlim NsLock(string ns)
    {
        if (Volatile.Read(ref _disposedFlag) != 0)
            throw new ObjectDisposedException(nameof(CognitiveIndex));

        // Fast path — the lock is already published. Avoids a RWLS allocation
        // on every single-ns operation after the first.
        if (_nsLocks.TryGetValue(ns, out var existing))
            return existing;

        // Cold path — create-then-publish so we can reclaim our own lock if Dispose
        // races with us between the pre-check and publication.
        var created = new ReaderWriterLockSlim();
        var published = _nsLocks.GetOrAdd(ns, created);

        if (Volatile.Read(ref _disposedFlag) != 0)
        {
            // Dispose ran between our pre-check and GetOrAdd. If WE are the one who
            // just published (Dispose already iterated before we added), yank it out
            // and dispose it so it doesn't leak.
            if (ReferenceEquals(published, created) &&
                ((ICollection<KeyValuePair<string, ReaderWriterLockSlim>>)_nsLocks)
                    .Remove(new KeyValuePair<string, ReaderWriterLockSlim>(ns, created)))
            {
                created.Dispose();
            }
            throw new ObjectDisposedException(nameof(CognitiveIndex));
        }

        // Another thread won the race and published first — discard our unused instance.
        if (!ReferenceEquals(published, created))
            created.Dispose();

        return published;
    }

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
        => CountInNamespace(ns, string.Empty);

    /// <summary>Count entries in a specific tenant + namespace partition. Per-partition read lock.</summary>
    public int CountInNamespace(string ns, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterReadLock();
        try
        {
            _store.EnsureLoaded(ns);
            return _store.GetNamespace(key)?.Count ?? 0;
        }
        finally { nsLock.ExitReadLock(); }
    }

    /// <summary>
    /// Get all known namespace names. Lock-free: reads from a ConcurrentDictionary snapshot
    /// and the persisted-namespace list from storage.
    /// </summary>
    public IReadOnlyList<string> GetNamespaces()
        => _store.GetNamespaceNames();

    /// <summary>Get namespaces containing entries for exactly one tenant.</summary>
    public IReadOnlyList<string> GetNamespaces(string tenantId)
    {
        _store.LoadAll();
        return _store.GetNamespaceNames(Tenancy.Normalize(tenantId));
    }

    /// <summary>
    /// All distinct tenant ids in the store (includes the legacy tenant "" when legacy data exists).
    /// Loads every persisted namespace first so background maintenance can cover every tenant.
    /// </summary>
    public IReadOnlyList<string> GetAllTenants()
    {
        _store.LoadAll();
        return _store.GetAllTenants();
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

        // Partition by (tenant, ns). For the legacy tenant ("") the partition key is exactly the
        // namespace, so this locks/keys identically to the pre-tenant path.
        var nskey = new NsKey(entry.TenantId, entry.Ns);
        string pk = NamespaceStore.PartitionKey(nskey);
        var nsLock = NsLock(pk);
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(entry.Ns);
            var nsEntries = _store.GetOrCreateNamespace(nskey);

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
            _store.TrackEntry(entry.Id, entry.Ns, entry.TenantId);
            _store.IndexBM25(entry);
            _store.AddToHnsw(nskey, entry.Id, entry.Vector);
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
        // Group by (tenant, ns) so each partition takes one write lock. For the legacy tenant this
        // is identical to grouping by namespace.
        foreach (var nsGroup in prepared.GroupBy(p => new NsKey(p.Entry.TenantId, p.Entry.Ns)))
        {
            if (totalLimitHit) break;

            var nskey = nsGroup.Key;
            string pk = NamespaceStore.PartitionKey(nskey);
            var nsLock = NsLock(pk);
            nsLock.EnterWriteLock();
            try
            {
                _store.EnsureLoaded(nskey.Ns);
                var nsEntries = _store.GetOrCreateNamespace(nskey);

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
                    _store.TrackEntry(entry.Id, entry.Ns, entry.TenantId);
                    _store.IndexBM25(entry);
                    _store.AddToHnsw(nskey, entry.Id, entry.Vector);
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

    /// <summary>
    /// Get an entry by ID within a specific namespace and tenant. Per-partition read lock.
    /// <paramref name="tenantId"/> is required — pass "" to resolve within the legacy partition.
    /// An id that exists only under a different tenant returns null — cross-tenant id-probing
    /// is impossible.
    /// </summary>
    public CognitiveEntry? Get(string id, string ns, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        string pk = NamespaceStore.PartitionKey(key);
        var nsLock = NsLock(pk);
        nsLock.EnterReadLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is not null && nsEntries.TryGetValue(id, out var tuple))
                return tuple.Entry;
            return null;
        }
        finally { nsLock.ExitReadLock(); }
    }

    /// <summary>
    /// Get an entry by bare id within one tenant. Returns null when no match exists or when the id
    /// is ambiguous across multiple namespaces in that tenant. This scan is intentionally named
    /// (rather than overloaded) so it cannot be confused with
    /// <see cref="Get(string, string, string)"/>.
    /// </summary>
    public CognitiveEntry? GetForTenant(string id, string tenantId)
        => ScanForTenant(id, tenantId, namespaceSnapshot: null).Match;

    /// <summary>
    /// How many of the tenant's namespaces contain <paramref name="id"/>, saturating at 2.
    /// This is the single expression of the ambiguity rule: a bare id is not an identity, so every
    /// caller that reaches an entry by id alone only ever needs to distinguish none (0) from
    /// exactly-one (1) from ambiguous (2). Counting past the second hit would cost partition
    /// lookups that no caller can observe.
    ///
    /// Pass <paramref name="namespaceSnapshot"/> when guarding a sweep of many ids. Omitting it
    /// re-lists the tenant's namespaces per call, and that listing loads every persisted namespace
    /// — which turns a cascade over one namespace's entries into one full store reload per entry.
    /// </summary>
    public int CountNamespacesContaining(
        string id, string tenantId, IReadOnlyList<string>? namespaceSnapshot = null)
        => ScanForTenant(id, tenantId, namespaceSnapshot).Found;

    /// <summary>
    /// Shared scan behind <see cref="GetForTenant"/>, <see cref="DeleteForTenant"/> and
    /// <see cref="CountNamespacesContaining"/>, so the three can never disagree about what counts
    /// as ambiguous. Returns no match the moment a second namespace claims the id — an ambiguous
    /// id resolves to nothing rather than to an arbitrary winner.
    /// </summary>
    private (CognitiveEntry? Match, string? Ns, int Found) ScanForTenant(
        string id, string tenantId, IReadOnlyList<string>? namespaceSnapshot)
    {
        CognitiveEntry? match = null;
        string? matchNs = null;
        int found = 0;

        foreach (var ns in namespaceSnapshot ?? GetNamespaces(tenantId))
        {
            var candidate = Get(id, ns, tenantId: tenantId);
            if (candidate is null)
                continue;
            if (++found == 2)
                return (null, null, 2);
            match = candidate;
            matchNs = ns;
        }

        return (match, matchNs, found);
    }

    /// <summary>
    /// Delete an entry by ID, searching all namespaces within the LEGACY tenant. Resolves id→ns
    /// lock-free then takes that ns's write lock. Only ever reaches legacy-tenant entries — a global
    /// id-delete can never remove another tenant's entry. Use <see cref="Delete(string, string, string)"/>
    /// for tenant-scoped deletes.
    /// </summary>
    public bool Delete(string id)
    {
        if (!_store.TryResolveOrLoad(id, out var ns))
            return false;

        string? deletedFromNs = null;
        // Legacy tenant: partition key == ns.
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
                _store.ScheduleEntryDelete(ns, id, string.Empty);
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

    /// <summary>
    /// Delete an entry scoped to a specific (tenant, ns) partition. Returns false when the entry is
    /// not present in exactly that partition — including when the id exists only under a different
    /// tenant or a different namespace. Pass "" to target the legacy partition.
    /// </summary>
    public bool Delete(string id, string ns, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        string pk = NamespaceStore.PartitionKey(key);

        string? deletedFromNs = null;
        var nsLock = NsLock(pk);
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is not null && nsEntries.TryRemove(id, out _))
            {
                if (key.Tenant.Length == 0)
                    _store.UntrackEntry(id);
                _store.RemoveBM25(id, pk);
                _store.RemoveFromHnsw(pk, id);
                _store.ScheduleEntryDelete(ns, id, key.Tenant);
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

    /// <summary>
    /// Delete an entry by bare id within one tenant. Refuses ambiguous ids that occur in more than
    /// one namespace, preventing a tenant-scoped caller from deleting an arbitrary match.
    /// </summary>
    public bool DeleteForTenant(string id, string tenantId)
    {
        var matchNamespace = ScanForTenant(id, tenantId, namespaceSnapshot: null).Ns;
        return matchNamespace is not null && Delete(id, matchNamespace, tenantId: tenantId);
    }

    // ── Search ──

    /// <summary>Unified search entry point supporting vector-only, hybrid, and deep recall modes.</summary>
    public IReadOnlyList<CognitiveSearchResult> Search(SearchRequest request)
    {
        if (request.Query is null || request.Query.Length == 0)
            throw new ArgumentException("Query vector must not be null or empty.", nameof(request));
        if (request.K <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "K must be positive.");

        // The namespace becomes half of a composite partition key below. A namespace carrying the
        // separator — or any other control character — could compose a key that reads as a
        // different (tenant, ns) pair, so it is rejected here rather than allowed to forge one.
        // This is a read path that never constructs an entry, so nothing else validates it.
        Tenancy.ValidatePartitionComponent(request.Namespace, nameof(request));

        // Scope the entire search to the (tenant, ns) partition. For the legacy tenant ("") the
        // partition key is the namespace, so candidate generation, BM25 lookup and getEntry behave
        // exactly as before. Tenant candidate sets never mix, so RRF/rerank run unchanged on an
        // already-isolated pool.
        var nskey = new NsKey(Tenancy.Normalize(request.TenantId), request.Namespace);
        string pk = NamespaceStore.PartitionKey(nskey);

        // Hold the per-namespace read lock across the ENTIRE search — the candidate snapshot, the
        // vector search, and every BM25/hybrid/PRF/auto-escalation call below. BM25Index.Search reads
        // shared inner NamespaceIndex state (plain Dictionary/HashSet) that concurrent writers mutate
        // under this same lock's write side. Releasing the lock right after the snapshot (as this method
        // did previously) left every BM25 call unlocked, so a background decay/consolidation/accretion
        // pass or a concurrent remember could mutate those collections mid-enumeration — risking a
        // "Collection was modified" throw or torn scores. This is a read lock, so concurrent searches
        // still run fully in parallel; only writers to this namespace wait.
        var nsLock = NsLock(pk);
        nsLock.EnterReadLock();
        try
        {
            _store.EnsureLoaded(request.Namespace);
            var nsEntries = _store.GetNamespace(nskey);
            if (nsEntries is null || nsEntries.Count == 0)
                return Array.Empty<CognitiveSearchResult>();
            IReadOnlyCollection<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> snapshot =
                nsEntries.Values.ToList();
            HnswIndex? hnswIndex = _store.GetHnswIndex(pk);

            // Entry resolver for BM25-only ("keyword rescue") candidates. Resolves per-candidate
            // straight from nsEntries rather than allocating a second whole-namespace map up front:
            // the read lock is held for the entire method, so this partition dictionary is stable,
            // and it is already scoped to this (tenant, ns) partition so it can never surface another
            // tenant's entry. Reading nsEntries here instead of calling Get() also avoids re-entering
            // this non-recursive read lock.
            Func<string, string, CognitiveEntry?> getEntry = (eid, _) =>
                nsEntries.TryGetValue(eid, out var tuple) ? tuple.Entry : null;

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
                    vectorResults, expandedQueryText, pk, hybridK,
                    request.IncludeStates, request.Category,
                    request.Rerank, request.RrfK, _bm25, _reranker, getEntry, request.Query, snapshot.Count);

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
                            vectorResults, prfQuery, pk, hybridK,
                            request.IncludeStates, request.Category,
                            request.Rerank, request.RrfK, _bm25, _reranker, getEntry, request.Query, snapshot.Count);
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
                    broadVectorResults, expandedQueryText, pk, request.K * diversityMultiplier,
                    request.IncludeStates, request.Category,
                    false, request.RrfK, _bm25, _reranker, getEntry, request.Query, snapshot.Count), request.QueryText);
                return ApplyDiversity(escalatedResults, request, snapshot);
            }

            return ApplyDiversity(vectorOnlyResults, request, snapshot);
        }
        finally { nsLock.ExitReadLock(); }
    }

    /// <summary>
    /// Namespace-scoped k-nearest-neighbor search with two-stage Int8 screening pipeline.
    /// <paramref name="tenantId"/> is required and sits directly after the namespace so the
    /// tenant-qualified identity reads first; pass "" for the legacy partition.
    /// </summary>
    public IReadOnlyList<CognitiveSearchResult> Search(
        float[] query, string ns, string tenantId, int k = 5, float minScore = 0f,
        string? category = null, HashSet<string>? includeStates = null, bool summaryFirst = false)
        => Search(new SearchRequest
        {
            Query = query, Namespace = ns, K = k, MinScore = minScore,
            Category = category, IncludeStates = includeStates, SummaryFirst = summaryFirst,
            TenantId = tenantId
        });

    /// <summary>
    /// Hybrid search combining vector + BM25 via Reciprocal Rank Fusion. The required
    /// <paramref name="tenantId"/> scopes the search to a tenant partition ("" = legacy
    /// tenant, i.e. identical to the pre-tenant behavior). RRF fusion parameters are unchanged.
    /// </summary>
    public IReadOnlyList<CognitiveSearchResult> HybridSearch(
        float[] query, string queryText, string ns, string tenantId, int k = 5, float minScore = 0f,
        string? category = null, HashSet<string>? includeStates = null,
        bool rerank = false, int rrfK = 60)
        => Search(new SearchRequest
        {
            Query = query, QueryText = queryText, Namespace = ns, K = k, MinScore = minScore,
            Category = category, IncludeStates = includeStates, Hybrid = true, Rerank = rerank,
            RrfK = rrfK, TenantId = tenantId
        });

    /// <summary>Apply token-level reranking to existing search results.</summary>
    public IReadOnlyList<CognitiveSearchResult> Rerank(
        string queryText, IReadOnlyList<CognitiveSearchResult> results)
        => _reranker.Rerank(queryText, results);

    /// <summary>Search ALL states including archived (for deep_recall). Pass tenantId "" for the legacy partition.</summary>
    public IReadOnlyList<CognitiveSearchResult> SearchAllStates(
        float[] query, string ns, string tenantId, int k = 10, float minScore = 0.3f,
        string? queryText = null, bool hybrid = false, bool rerank = false)
        => Search(new SearchRequest
        {
            Query = query, Namespace = ns, K = k, MinScore = minScore,
            IncludeStates = AllStates, TenantId = tenantId,
            QueryText = queryText ?? string.Empty, Hybrid = hybrid, Rerank = rerank
        });

    /// <summary>
    /// Search across multiple namespaces and merge results using Reciprocal Rank Fusion.
    /// Returns results annotated with their source namespace.
    /// <paramref name="queryText"/> is required-but-nullable rather than defaulted: tenantId may
    /// not jump over a string slot (an old positional query text would silently bind as the
    /// tenant), so callers without text pass <c>queryText: null</c> explicitly.
    /// <paramref name="tenantId"/> is required; pass "" for the legacy partition.
    /// </summary>
    public IReadOnlyList<Models.CrossSearchResult> SearchMultiple(
        float[] query, IReadOnlyList<string> namespaces, string? queryText, string tenantId,
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
                    TenantId = tenantId,
                });
            }
            else if (hybrid && queryText is not null)
            {
                nsResults = HybridSearch(query, queryText, ns, tenantId: tenantId, k, minScore, category, includeStates, rerank, rrfK);
            }
            else
            {
                // Route through the request path so the tenant scope is applied; for the legacy
                // tenant this constructs exactly the same request as Search(query, ns, ...).
                nsResults = Search(new SearchRequest
                {
                    Query = query, Namespace = ns, K = k, MinScore = minScore,
                    Category = category, IncludeStates = includeStates,
                    SummaryFirst = summaryFirst, TenantId = tenantId
                });
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

    /// <summary>Find near-duplicates for a single entry within its namespace (O(N) scan). Pass tenantId "" for the legacy partition.</summary>
    public IReadOnlyList<(string IdA, string IdB, float Similarity)> FindDuplicatesForEntry(
        string ns, string entryId, string tenantId, float threshold = 0.95f)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterReadLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is null)
                return Array.Empty<(string, string, float)>();

            nsEntries.TryGetValue(entryId, out var target);
            return _duplicateDetector.FindDuplicatesForEntry(entryId, target, nsEntries, threshold);
        }
        finally { nsLock.ExitReadLock(); }
    }

    /// <summary>Find near-duplicate entries within a namespace by pairwise cosine similarity. Pass tenantId "" for the legacy partition.</summary>
    public IReadOnlyList<(string IdA, string IdB, float Similarity)> FindDuplicates(
        string ns, string tenantId, float threshold = 0.95f, string? category = null,
        HashSet<string>? includeStates = null, int maxResults = 100)
    {
        if (threshold < 0f || threshold > 1f)
            throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be between 0 and 1.");

        includeStates ??= new HashSet<string> { "stm", "ltm" };

        List<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates;
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterReadLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
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
        => RecordAccess(id, ns, string.Empty);

    /// <summary>Record an access hit within an exact tenant + namespace partition.</summary>
    public void RecordAccess(string id, string ns, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
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
        => BoostActivationEnergy(id, ns, delta, string.Empty, allowNamespaceFallback: true);

    /// <summary>Boost activation energy within an exact tenant + namespace partition.</summary>
    public bool BoostActivationEnergy(string id, string ns, float delta, string tenantId)
        => BoostActivationEnergy(id, ns, delta, tenantId, allowNamespaceFallback: false);

    private bool BoostActivationEnergy(
        string id, string ns, float delta, string tenantId, bool allowNamespaceFallback)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        // Fast path: try the caller's supplied ns first
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is not null && nsEntries.TryGetValue(id, out var tuple))
            {
                tuple.Entry.ActivationEnergy += delta;
                _store.ScheduleEntryUpsert(ns, tuple.Entry);
                return true;
            }
        }
        finally { nsLock.ExitWriteLock(); }

        if (!allowNamespaceFallback)
            return false;

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

    /// <summary>Update lifecycle state within an exact tenant + namespace partition.</summary>
    public bool SetLifecycleState(string id, string state, string ns, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is null || !nsEntries.TryGetValue(id, out var tuple))
                return false;

            var previousState = tuple.Entry.LifecycleState;
            tuple.Entry.LifecycleState = state;
            UpdateQuantization(nsEntries, id, tuple, previousState, state);
            _store.ScheduleEntryUpsert(ns, tuple.Entry);
            return true;
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

    /// <summary>Update lifecycle state for ids in an exact tenant + namespace partition.</summary>
    public int SetLifecycleStateBatch(
        IEnumerable<string> ids, string state, string ns, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is null)
                return 0;

            int updated = 0;
            foreach (var id in ids)
            {
                if (!nsEntries.TryGetValue(id, out var tuple))
                    continue;

                var previousState = tuple.Entry.LifecycleState;
                tuple.Entry.LifecycleState = state;
                UpdateQuantization(nsEntries, id, tuple, previousState, state);
                _store.ScheduleEntryUpsert(ns, tuple.Entry);
                updated++;
            }
            return updated;
        }
        finally { nsLock.ExitWriteLock(); }
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

    /// <summary>
    /// Update activation energy and optional lifecycle state within an exact tenant + namespace
    /// partition.
    /// </summary>
    public bool SetActivationEnergyAndState(
        string id, float activationEnergy, string? newState, string ns, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is null || !nsEntries.TryGetValue(id, out var tuple))
                return false;

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
        finally { nsLock.ExitWriteLock(); }
    }

    // ── Bulk Reads ──

    /// <summary>Get all entries in a namespace. Per-namespace read lock.</summary>
    public IReadOnlyList<CognitiveEntry> GetAllInNamespace(string ns)
        => GetAllInNamespace(ns, string.Empty);

    /// <summary>Get all entries in a tenant + namespace partition. Per-partition read lock.</summary>
    public IReadOnlyList<CognitiveEntry> GetAllInNamespace(string ns, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterReadLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is null)
                return Array.Empty<CognitiveEntry>();
            return nsEntries.Values.Select(t => t.Entry).ToList();
        }
        finally { nsLock.ExitReadLock(); }
    }

    /// <summary>Delete all entries in a namespace and remove it from in-memory state. Does NOT cascade to graph edges or clusters — callers must handle that.</summary>
    public int DeleteAllInNamespace(string ns)
        => DeleteAllInNamespace(ns, string.Empty);

    /// <summary>
    /// Delete every entry in exactly one tenant + namespace partition. Other tenants with the
    /// same namespace or ids are untouched. Does NOT cascade to graph edges or clusters.
    /// </summary>
    public int DeleteAllInNamespace(string ns, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is null || nsEntries.Count == 0)
            {
                _store.RemoveNamespace(key);
                return 0;
            }

            var ids = nsEntries.Keys.ToList();
            // Remove first so snapshot-based providers observe the post-delete state when their
            // save is scheduled. This clears only the selected partition's BM25/HNSW indexes.
            _store.RemoveNamespace(key);
            foreach (var id in ids)
                _store.ScheduleEntryDelete(ns, id, key.Tenant);

            return ids.Count;
        }
        finally { nsLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Get all entries across all namespaces. Lock-free: snapshots ConcurrentDictionary entries.
    /// Diagnostic-grade consistency — may observe in-flight writes to any single ns but never
    /// returns a torn entry record.
    /// </summary>
    public IReadOnlyList<CognitiveEntry> GetAll()
        => GetAllForTenant(string.Empty);

    /// <summary>Get all entries belonging to exactly one tenant.</summary>
    public IReadOnlyList<CognitiveEntry> GetAllForTenant(string tenantId)
    {
        _store.LoadAll();
        return _store.GetTenantNamespaces(Tenancy.Normalize(tenantId))
            .SelectMany(d => d.Values.Select(t => t.Entry))
            .ToList();
    }

    /// <summary>
    /// Explicit system-level diagnostic snapshot across every tenant. Principal-bound tools and
    /// maintenance paths must use <see cref="GetAllForTenant"/> instead.
    /// </summary>
    public IReadOnlyList<CognitiveEntry> GetAllAcrossTenants()
    {
        _store.LoadAll();
        return _store.AllNamespaces
            .SelectMany(d => d.Values.Select(t => t.Entry))
            .ToList();
    }

    /// <summary>
    /// Get count of entries by lifecycle state. Single-ns path takes that ns's read lock;
    /// cross-ns path is lock-free (diagnostic-grade consistency via ConcurrentDictionary).
    /// </summary>
    public (int stm, int ltm, int archived) GetStateCounts(string? ns = null)
        => GetStateCounts(ns, string.Empty);

    /// <summary>
    /// Get lifecycle-state counts within one tenant. A null or "*" namespace aggregates only
    /// that tenant's partitions; a concrete namespace reads exactly that tenant + namespace.
    /// </summary>
    public (int stm, int ltm, int archived) GetStateCounts(string? ns, string tenantId)
    {
        string tenant = Tenancy.Normalize(tenantId);
        if (ns is null || ns == "*")
        {
            _store.LoadAll();
            var entries = _store.GetTenantNamespaces(tenant)
                .SelectMany(d => d.Values.Select(t => t.Entry));
            return CountStates(entries);
        }
        else
        {
            var key = new NsKey(tenant, ns);
            var nsLock = NsLock(NamespaceStore.PartitionKey(key));
            nsLock.EnterReadLock();
            try
            {
                _store.EnsureLoaded(ns);
                var entries = _store.GetNamespace(key) is { } nsEntries
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

    /// <summary>Re-embed all entries in a namespace. Per-namespace write lock. Pass tenantId "" for the legacy partition.</summary>
    public (int Updated, int Skipped) RebuildEmbeddings(string ns, IEmbeddingService embedding, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        string pk = NamespaceStore.PartitionKey(key);
        var nsLock = NsLock(pk);
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
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
                    oldEntry.ActivationEnergy, oldEntry.IsSummaryNode, oldEntry.SourceClusterId,
                    oldEntry.Keywords, oldEntry.TenantId);

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
                _store.InvalidateHnswIndex(pk);
            }

            return (updated, skipped);
        }
        finally { nsLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Release per-namespace locks. Per the standard <see cref="IDisposable"/> contract,
    /// the caller is responsible for ensuring no in-flight operations are still
    /// executing on this index when Dispose is called — a thread holding a per-ns
    /// lock during Dispose would cause <see cref="ReaderWriterLockSlim.Dispose"/> to
    /// throw <see cref="SynchronizationLockException"/>. Dispose is safe against
    /// concurrent Dispose callers (once-and-only-once) and against racing NsLock
    /// callers (they throw <see cref="ObjectDisposedException"/> before touching a
    /// torn-down lock).
    /// </summary>
    public void Dispose()
    {
        // Atomic once-and-only-once transition via Interlocked.Exchange. Concurrent
        // Dispose callers never both reach the teardown — only the first to flip 0→1
        // proceeds; the rest see a non-zero prior value and return immediately.
        // The flag flips BEFORE teardown so any racing NsLock(ns) call sees it and
        // throws ObjectDisposedException up front.
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;

        // Per-lock best-effort. ReaderWriterLockSlim.Dispose throws if a lock is still held or has
        // waiters, and the hosted server can genuinely reach here in that state: the maintenance
        // passes take no CancellationToken, so a pass that outlives the host's shutdown timeout
        // keeps its namespace lock while the container tears the object graph down.
        //
        // Letting that escape is what costs something. ServiceProvider disposes singletons in
        // reverse creation order and does not catch, so a throw here abandons every disposable
        // created earlier — including PersistenceManager, whose Dispose is the Flush that writes
        // pending debounced entries. An exception at this point silently drops memories.
        //
        // Weighed against that, the lock object is not worth defending: skipping its Dispose leaks
        // internal handles the process is about to release anyway. Losing the flush loses data.
        int skipped = 0;
        foreach (var kv in _nsLocks)
        {
            try { kv.Value.Dispose(); }
            catch (SynchronizationLockException) { skipped++; }
        }
        _nsLocks.Clear();
        DisposalContendedLockCount = skipped;
    }

    /// <summary>
    /// How many per-namespace locks were still in use when <see cref="Dispose"/> ran, and so were
    /// left undisposed. Non-zero means shutdown raced an in-flight operation — the disposal itself
    /// still completed. Exposed rather than logged because this type takes no logger; a host that
    /// cares can read it after disposing, and the disposal tests assert on it.
    /// </summary>
    public int DisposalContendedLockCount { get; private set; }

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
