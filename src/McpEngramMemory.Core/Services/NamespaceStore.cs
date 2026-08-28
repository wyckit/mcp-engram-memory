using System.Collections.Concurrent;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services;

/// <summary>
/// Identifies a storage partition by tenant + namespace. The legacy single-tenant partition is
/// <c>Tenant == ""</c>. Value equality (record struct) makes it a safe dictionary key.
/// </summary>
internal readonly record struct NsKey(string Tenant, string Ns);

/// <summary>
/// Manages tenant + namespace-partitioned storage of cognitive entries with lazy loading from disk.
/// Partitions are keyed by <see cref="NsKey"/> = (tenant, ns); the legacy tenant is <c>""</c>, so
/// for every pre-tenant (no-tenant) caller the partition key is exactly the namespace and behavior
/// is byte-for-byte identical to the single-tenant design.
///
/// Infrastructure is thread-safe via ConcurrentDictionary. Per-partition entry dictionaries are
/// also ConcurrentDictionary, allowing CognitiveIndex to use ReadLock for read paths and WriteLock
/// only for mutations.
///
/// Tenant isolation: the reverse id locator (<see cref="_idToNamespace"/>) and the total-count
/// atomic track the LEGACY tenant only. This is deliberate — the global, tenant-less id operations
/// (Get(id)/Delete(id) and the id→ns resolvers) must resolve strictly within the legacy tenant so
/// a caller can never probe or reach another tenant's entry by id alone. Tenant partitions are
/// reached only through the explicit tenant-scoped APIs.
///
/// The candidate index (<see cref="_idCandidates"/>) is a second, independent structure and covers
/// every tenant precisely because it is tenant-QUALIFIED: a lookup names the tenant it wants, so
/// tracking another tenant's placements cannot widen what a tenant-less caller reaches. The two are
/// not interchangeable — the locator answers "the one namespace" (tenant-blind, last writer wins),
/// the candidate index answers "every namespace of THIS tenant", which is the only form in which
/// unique-versus-ambiguous is decidable.
/// </summary>
internal sealed class NamespaceStore
{
    /// <summary>Minimum partition size to activate HNSW indexing.</summary>
    private const int HnswThreshold = 200;

    // The separator used to compose a tenant-scoped partition key lives on Tenancy, together with
    // Tenancy.ValidatePartitionComponent — the guard that makes "never appears in a namespace or
    // tenant id" an enforced fact instead of an assumption. It is enforced where untrusted strings
    // become key components (Tenancy.Normalize, the CognitiveEntry write ctor, and the read/delete/
    // config paths that never construct an entry), so composition below can be injective.

    private readonly ConcurrentDictionary<NsKey, ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>> _namespaces = new();
    // Loaded tracking + load locks are keyed by NAMESPACE (the persistence unit): one LoadNamespace
    // call materializes every tenant's rows for that ns and buckets them into the right partition.
    private readonly ConcurrentDictionary<string, bool> _loadedNamespaces = new();
    // Completion tracking for LoadAll. _namespaceSetGeneration is bumped whenever a namespace is
    // un-loaded — the only in-process event that can make "every persisted namespace has been
    // materialized" stop being true — and _loadedGeneration records the generation the last sweep
    // that ran to completion started under. Monotonic, so a completion recorded against an older
    // generation can never be mistaken for a current one; -1 because generation 0 is the initial,
    // never-swept state.
    private long _namespaceSetGeneration;
    private long _loadedGeneration = -1;
    // LEGACY-ONLY reverse index: id → ns. Only legacy-tenant ("") entries are tracked here so the
    // global id-based operations resolve strictly within the legacy tenant.
    private readonly ConcurrentDictionary<string, string> _idToNamespace = new();
    // Candidate index: (tenant, id) → every namespace of that tenant currently holding the id.
    // Set-valued because (tenant, ns, id) is the real identity — one id legitimately occurs in
    // several namespaces of one tenant, and a bare-id resolver that saw only one of them would
    // call an ambiguous id unique and then act on an arbitrary twin. Keyed by value tuple so
    // equality is structural and ordinal, and valued by a ConcurrentDictionary used as a set
    // because the BCL has no concurrent set, so a lookup never blocks a placement or vice versa.
    private readonly ConcurrentDictionary<(string Tenant, string Id), ConcurrentDictionary<string, byte>> _idCandidates = new();
    // Publishing a bucket and retiring an emptied one are the one pair that must not interleave: a
    // retirement decides on emptiness and then unpublishes that instance, and a placement written
    // into the same instance in between is left live but unreachable. That is not merely a lost
    // entry — an id occupying two namespaces then reads back as occupying one, so the tenant-wide
    // duplicate test that topology fails closed on passes instead. The two are therefore serialized
    // per (tenant, id) by these stripes, while lookups stay lock-free and unaffected. Striping
    // rather than a lock object per key because a per-key lock would itself need the
    // publish/retire protocol it exists to provide. 64 is deliberately modest: the critical section
    // is two dictionary operations, so a hash collision between unrelated ids costs almost nothing,
    // and the array is allocated once per store.
    private const int CandidateStripeCount = 64;
    private readonly object[] _candidateStripes =
        Enumerable.Range(0, CandidateStripeCount).Select(_ => new object()).ToArray();
    // BM25/HNSW sub-indexes are keyed by the composed partition-key STRING (see PartitionKey).
    private readonly ConcurrentDictionary<string, HnswIndex> _hnswIndices = new();
    private readonly ConcurrentDictionary<string, object> _loadLocks = new();
    private readonly IStorageProvider _persistence;
    private readonly BM25Index _bm25;

    // Atomic total-entry count for the LEGACY tenant across all namespaces — maintained by
    // TrackEntry / UntrackEntry / RemoveNamespace / LoadEntries so memory-limits checks can run
    // without a cross-namespace lock. Approximate under concurrent mutations but reliably
    // incremented once per distinct legacy id insertion. Tenant partitions are excluded by design
    // (they are unreachable via the global id/count paths).
    private long _totalCountApprox;

    public NamespaceStore(IStorageProvider persistence, BM25Index bm25)
    {
        _persistence = persistence;
        _bm25 = bm25;
    }

    /// <summary>Compose the partition-key string for BM25/HNSW sub-indexes. Legacy tenant → the namespace itself.</summary>
    /// <exception cref="ArgumentException">
    /// Either component carries a control character, which would make the composition non-injective
    /// and let one caller forge another tenant's key.
    /// </exception>
    public static string PartitionKey(string tenant, string ns)
    {
        // Composition is injective only while neither component carries the separator, so validating
        // here — the one place a key is ever composed — is what actually holds the invariant up. Every
        // other entry point (decay configs, diffusion bases, index invalidation) reaches a partition
        // through this method, so one check covers them all rather than each site remembering.
        //
        // This throws rather than asserting on purpose: a failing Debug.Assert calls
        // Environment.FailFast, which would turn a containment bug into a killed process (and a dead
        // test host) instead of a catchable error, and would vanish entirely in Release — exactly
        // where forging matters. The cost is two vectorized scans of a short string, well below the
        // allocation on the next line.
        Tenancy.ValidatePartitionComponent(tenant, nameof(tenant));
        Tenancy.ValidatePartitionComponent(ns, nameof(ns));
        return ComposeKeyUnchecked(tenant, ns);
    }

    /// <summary>
    /// The key format itself, with no assertion. Reserved for the recovery paths that must be able
    /// to key an ALREADY-poisoned component — the whole point of those paths is to survive data the
    /// assert exists to keep out, so they cannot route through <see cref="PartitionKey(string,string)"/>.
    /// </summary>
    private static string ComposeKeyUnchecked(string tenant, string ns)
        => tenant.Length == 0 ? ns : string.Concat(tenant, Tenancy.PartitionSeparator.ToString(), ns);

    /// <summary>Compose the partition-key string for a <see cref="NsKey"/>.</summary>
    public static string PartitionKey(NsKey key) => PartitionKey(key.Tenant, key.Ns);

    /// <summary>
    /// Build the partition-keyed decay-config map every storage provider hands to the lifecycle
    /// engine. Shared here because the collision it has to survive is a property of the key format,
    /// not of any one backend: a store written before <see cref="Tenancy.ValidatePartitionComponent"/>
    /// existed can hold two rows that compose to the same key, and the obvious
    /// <c>ToDictionary</c> throws on that — turning a historical bad write into a host that cannot
    /// boot, with manual database repair as the only way out. Keeping one row deterministically
    /// (tenant-scoped ahead of legacy, then stored order) degrades a poisoned pair to a logged
    /// warning while leaving every well-formed store byte-for-byte unchanged.
    /// </summary>
    internal static Dictionary<string, DecayConfig> DecayConfigsByPartition(
        List<DecayConfig>? configs, ILogger? logger)
    {
        var map = new Dictionary<string, DecayConfig>();
        if (configs is null)
            return map;

        // OrderBy is a stable sort, so within each group the stored order decides. That makes the
        // survivor arbitrary but reproducible across boots, which is what keeps decay behaviour
        // from flipping between restarts.
        foreach (var config in configs.OrderBy(c => c.TenantId.Length == 0 ? 1 : 0))
        {
            // ComposeKeyUnchecked, not PartitionKey: the rows being keyed here are exactly the ones
            // that may already carry a separator, so the assert would abort the boot this method
            // exists to rescue.
            if (map.TryAdd(ComposeKeyUnchecked(config.TenantId, config.Ns), config))
                continue;

            logger?.LogWarning(
                "Duplicate decay-config partition key for tenant '{TenantId}' namespace '{Namespace}' — " +
                "keeping the first row in tenant-scoped-first order and discarding the rest. This store " +
                "was written before partition-component validation and should be repaired.",
                EscapeControlChars(config.TenantId), EscapeControlChars(config.Ns));
        }
        return map;
    }

    /// <summary>
    /// Render a partition component safely for a log line. A poisoned store is exactly the case
    /// where these strings may hold newlines, so echoing them raw would let a bad write forge log
    /// records. Never used on a success path.
    /// </summary>
    private static string EscapeControlChars(string value)
        => value.Any(char.IsControl)
            ? string.Concat(value.Select(c => char.IsControl(c) ? $"\\u{(int)c:X4}" : c.ToString()))
            : value;

    /// <summary>Get the entry dictionary for the legacy-tenant partition of a namespace (null if absent).</summary>
    public ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>? GetNamespace(string ns)
        => GetNamespace(new NsKey(string.Empty, ns));

    /// <summary>Get the entry dictionary for a tenant partition (null if it doesn't exist).</summary>
    public ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>? GetNamespace(NsKey key)
        => _namespaces.TryGetValue(key, out var entries) ? entries : null;

    /// <summary>Get or create the entry dictionary for the legacy-tenant partition of a namespace.</summary>
    public ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> GetOrCreateNamespace(string ns)
        => GetOrCreateNamespace(new NsKey(string.Empty, ns));

    /// <summary>Get or create the entry dictionary for a tenant partition.</summary>
    public ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> GetOrCreateNamespace(NsKey key)
        => _namespaces.GetOrAdd(key, _ => new ConcurrentDictionary<string, (CognitiveEntry, float, QuantizedVector?)>());

    /// <summary>
    /// Remove the LEGACY-tenant partition of a namespace entirely from in-memory state (entries,
    /// locator, BM25, HNSW, loaded tracking). Only removes locator entries that still point at this
    /// ns — an orphaned id that was later upserted into a different ns keeps its updated locator +
    /// count entry. Tenant partitions of the same ns are untouched (they are managed independently).
    /// The namespace is validated by the <see cref="NsKey"/> overload this delegates to, which runs
    /// before any state is touched.
    /// </summary>
    public void RemoveNamespace(string ns)
    {
        var key = new NsKey(string.Empty, ns);
        RemoveNamespace(key);
        _loadedNamespaces.TryRemove(ns, out _);

        // Un-load first, invalidate second, and never the other way round. LoadAll reads the
        // generation before its sweep, so bumping after the un-load guarantees that any completion
        // published by a sweep which ran with this namespace already gone carries a generation the
        // next caller will reject. Bumping first would let such a sweep read the NEW generation,
        // find the namespace still marked loaded, and then publish a completion that our removal
        // falsifies a moment later — a namespace persisted but permanently unmaterialized, which is
        // the stale MISS the candidate index must never serve.
        //
        // The NsKey overload deliberately does not bump: it empties a partition but leaves the
        // namespace loaded, so "every persisted namespace has been materialized" still holds.
        Interlocked.Increment(ref _namespaceSetGeneration);
    }

    /// <summary>
    /// Remove exactly one tenant + namespace partition from in-memory state, its candidate-index
    /// placements and its search indexes. Other tenant partitions with the same namespace are left
    /// loaded and untouched. Persistence rows are deliberately not deleted here; the caller must
    /// schedule deletes for the removed entry ids so incremental providers can target the full
    /// (tenant, ns, id) key.
    /// </summary>
    public void RemoveNamespace(NsKey key)
    {
        // A delete reaches storage without ever constructing a CognitiveEntry, so the entry write
        // ctor's guard does not cover it. Validate before anything is removed: a namespace carrying
        // the separator would compose to another tenant's partition key and clear that tenant's
        // BM25/HNSW indexes and persisted snapshot.
        Tenancy.ValidatePartitionComponent(key.Tenant, nameof(key));
        Tenancy.ValidatePartitionComponent(key.Ns, nameof(key));

        if (_namespaces.TryRemove(key, out var entries))
        {
            int removed = 0;
            foreach (var id in entries.Keys)
            {
                // The candidate index is retracted for EVERY tenant, not just the legacy one: it
                // is keyed by the full (tenant, ns, id) placement, so a tenant partition skipped
                // here would keep naming a namespace whose entries no longer exist — and a
                // candidate that outlives its partition is exactly the stale resolution this
                // index exists to prevent.
                UntrackCandidate(id, key.Ns, key.Tenant);

                if (key.Tenant.Length != 0)
                    continue;

                // Use the KeyValuePair overload of TryRemove so we only delete a locator entry
                // when it currently points at THIS namespace. Guards against a rare but real
                // scenario: id X was upserted to ns=A (orphan), then re-upserted to ns=B (locator
                // now points at B). If we unconditionally TryRemove(X), we'd blow away B's
                // locator AND decrement the total count while B's entries dict still has X —
                // driving TotalCount negative.
                if (_idToNamespace.TryRemove(new KeyValuePair<string, string>(id, key.Ns)))
                    removed++;
            }
            if (removed > 0)
                Interlocked.Add(ref _totalCountApprox, -removed);
        }
        string pk = PartitionKey(key);
        _bm25.ClearNamespace(pk);
        _hnswIndices.TryRemove(pk, out _);
        _persistence.DeleteHnswSnapshot(pk);
    }

    /// <summary>All partition entry dictionaries (for cross-partition diagnostic operations).</summary>
    public IEnumerable<ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>> AllNamespaces
        => _namespaces.Values;

    /// <summary>Total legacy-tenant entries across all namespaces. O(1) atomic read — safe without a lock.</summary>
    public int TotalCount => (int)Interlocked.Read(ref _totalCountApprox);

    /// <summary>Get all known namespace names (loaded + persisted), tenant-independent.</summary>
    public IReadOnlyList<string> GetNamespaceNames()
    {
        var persisted = _persistence.GetPersistedNamespaces();
        var inMemory = _namespaces.Keys.Select(k => k.Ns);
        return persisted.Union(inMemory).Distinct().ToList();
    }

    /// <summary>
    /// Get namespace names that currently contain entries for one tenant. Callers must load all
    /// persisted namespaces first when they require complete discovery. Empty legacy partitions
    /// created as a side effect of loading another tenant are excluded.
    /// </summary>
    public IReadOnlyList<string> GetNamespaceNames(string tenantId)
        => _namespaces
            .Where(kv => kv.Key.Tenant == tenantId && !kv.Value.IsEmpty)
            .Select(kv => kv.Key.Ns)
            .Distinct()
            .ToList();

    /// <summary>
    /// Distinct tenant ids across all loaded partitions (includes the legacy tenant "" when legacy
    /// data is present). Callers that need complete discovery must <see cref="LoadAll"/> first.
    /// </summary>
    public IReadOnlyList<string> GetAllTenants()
        => _namespaces.Keys.Select(k => k.Tenant).Distinct().ToList();

    /// <summary>Snapshot the entry dictionaries belonging to exactly one tenant.</summary>
    public IEnumerable<ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>>
        GetTenantNamespaces(string tenantId)
        => _namespaces
            .Where(kv => kv.Key.Tenant == tenantId)
            .Select(kv => kv.Value);

    /// <summary>
    /// Ensure a namespace is loaded from disk (all tenants). Thread-safe via per-namespace load lock
    /// with double-check pattern. Multiple namespaces can be loaded concurrently. Loaded rows are
    /// bucketed into their (tenant, ns) partitions by <see cref="CognitiveEntry.TenantId"/>.
    /// </summary>
    public void EnsureLoaded(string ns)
    {
        if (_loadedNamespaces.ContainsKey(ns))
            return;

        // Per-namespace lock prevents concurrent double-loading of the same namespace
        lock (_loadLocks.GetOrAdd(ns, _ => new object()))
        {
            if (_loadedNamespaces.ContainsKey(ns))
                return; // Another thread loaded while we waited

            var data = _persistence.LoadNamespace(ns);
            // Ensure the legacy partition exists even when empty, matching prior behavior where
            // GetNamespace(ns) returned a (possibly empty) dictionary after EnsureLoaded.
            _namespaces.GetOrAdd(new NsKey(string.Empty, ns), _ => new ConcurrentDictionary<string, (CognitiveEntry, float, QuantizedVector?)>());

            LoadEntries(ns, data.Entries);

            // Build BM25 + restore HNSW per (tenant, ns) partition so a search never mixes tenants
            // at the candidate stage. For the legacy tenant this is identical to the single-tenant
            // path (partition key == ns, one group).
            foreach (var group in data.Entries.GroupBy(e => e.TenantId))
            {
                string pk = PartitionKey(group.Key, ns);
                var groupList = group as IList<CognitiveEntry> ?? group.ToList();

                if (!_bm25.HasNamespace(pk))
                    _bm25.RebuildNamespace(pk, groupList);

                // Try to restore HNSW from persisted snapshot (avoids O(N log N) rebuild)
                if (groupList.Count >= HnswThreshold && !_hnswIndices.ContainsKey(pk))
                    TryRestoreHnsw(pk, new NsKey(group.Key, ns));
            }

            _loadedNamespaces.TryAdd(ns, true);
        }
    }

    /// <summary>
    /// Materialize every persisted namespace, then remember that it was done.
    ///
    /// The enumeration is the expensive half and it is what the cache removes: a directory listing
    /// for the JSON provider, a <c>SELECT DISTINCT ns</c> for the database ones, paid on every call,
    /// while the <see cref="EnsureLoaded"/> that follows is a dictionary hit for everything already
    /// resident. Every bare-id resolution reaches this method (see
    /// <see cref="GetCandidateNamespaces"/>), so leaving the enumeration uncached puts one storage
    /// round trip on a lookup that otherwise touches only memory — the end-to-end scaling the
    /// candidate index was supposed to remove.
    ///
    /// Caching is exactly as complete as re-sweeping, for this process. A namespace persisted BY
    /// this process was materialized before it could be written — the write paths call
    /// <see cref="EnsureLoaded"/> under the partition write lock before creating the partition — so
    /// it is already resident and already in the candidate index without any sweep. The one
    /// in-process event that falsifies the claim is un-loading a namespace, and
    /// <see cref="RemoveNamespace(string)"/> reports that by bumping the generation. A namespace
    /// another PROCESS creates is not discovered, but that boundary already existed: EnsureLoaded
    /// never re-reads a namespace it has loaded, so another process's rows in any known namespace
    /// were already invisible, and one server process per data directory is the documented
    /// supported topology.
    ///
    /// Concurrency: the generation is read BEFORE the sweep and republished after it, so an un-load
    /// landing mid-sweep leaves an already-stale generation recorded and the next caller sweeps
    /// again. Two threads arriving cold may both sweep; EnsureLoaded is idempotent, so that costs
    /// one redundant enumeration and nothing else.
    /// </summary>
    public void LoadAll()
    {
        long generation = Interlocked.Read(ref _namespaceSetGeneration);
        if (Interlocked.Read(ref _loadedGeneration) == generation)
            return;

        foreach (var ns in _persistence.GetPersistedNamespaces())
            EnsureLoaded(ns);

        // Exchange rather than a plain store: it fences the sweep ahead of the publication, so no
        // other thread can observe the completion before the namespaces it vouches for are loaded.
        Interlocked.Exchange(ref _loadedGeneration, generation);
    }

    /// <summary>
    /// Snapshot current namespace data (ALL tenant partitions of the namespace) and schedule a
    /// debounced write to disk. The full snapshot spans every tenant because the persistence unit
    /// is the namespace; each entry carries its own <see cref="CognitiveEntry.TenantId"/>.
    /// </summary>
    public void ScheduleSave(string ns)
    {
        var data = new NamespaceData();
        var list = new List<CognitiveEntry>();
        foreach (var kv in _namespaces)
        {
            if (kv.Key.Ns != ns) continue;
            list.AddRange(kv.Value.Values.Select(t => t.Entry));
        }
        data.Entries = list;

        _persistence.ScheduleSave(ns, () => data);

        // Persist HNSW snapshots alongside namespace data (one per tenant partition)
        ScheduleHnswSave(ns);
    }

    /// <summary>Schedule an incremental upsert (SQLite/SQL Server) or full snapshot (JSON) for a single entry.</summary>
    public void ScheduleEntryUpsert(string ns, CognitiveEntry entry)
    {
        if (_persistence.SupportsIncrementalWrites)
            _persistence.ScheduleUpsertEntry(ns, entry);
        else
            ScheduleSave(ns);
    }

    /// <summary>
    /// Schedule a tenant-scoped incremental delete (SQLite/SQL Server) or full snapshot (JSON) for a
    /// single entry. The tenant is threaded to the provider so an incremental delete targets the
    /// full (tenant, ns, id) key and never removes a co-keyed row in another tenant.
    /// </summary>
    public void ScheduleEntryDelete(string ns, string entryId, string tenantId)
    {
        if (_persistence.SupportsIncrementalWrites)
            _persistence.ScheduleDeleteEntry(ns, entryId, tenantId);
        else
            ScheduleSave(ns);
    }

    /// <summary>Index an entry in BM25 under its (tenant, ns) partition for keyword search.</summary>
    public void IndexBM25(CognitiveEntry entry)
        => _bm25.Index(entry, PartitionKey(entry.TenantId, entry.Ns));

    /// <summary>Remove an entry from the BM25 keyword index for the given partition key.</summary>
    public void RemoveBM25(string id, string partitionKey) => _bm25.Remove(id, partitionKey);

    // ── Id Locators: the LEGACY-only reverse index (entryId → one ns) and the tenant-qualified
    //    candidate index ((tenant, entryId) → every ns holding it). Both are maintained by the
    //    same Track/Untrack pair so they cannot drift apart. ──

    /// <summary>Resolve a legacy-tenant namespace via locator, falling back to LoadAll if not found.</summary>
    public bool TryResolveOrLoad(string entryId, out string ns)
    {
        if (_idToNamespace.TryGetValue(entryId, out ns!))
            return true;
        LoadAll();
        return _idToNamespace.TryGetValue(entryId, out ns!);
    }

    /// <summary>
    /// Record that (<paramref name="tenantId"/>, <paramref name="ns"/>) holds
    /// <paramref name="entryId"/>, in the candidate index for every tenant and additionally in the
    /// LEGACY locator + total count. Tenant entries (non-empty tenantId) are intentionally NOT put
    /// in the locator, keeping the global id/count paths legacy-scoped.
    /// Increments TotalCount atomically only when the id was not already tracked (so upsert-of-existing
    /// doesn't drift the count).
    /// </summary>
    public void TrackEntry(string entryId, string ns, string tenantId)
    {
        // Upsert never removes the id from a namespace it previously occupied, so the candidate
        // set grows rather than moves — which is what makes it agree with a full scan, where both
        // twins would have been found. The locator below moves instead, and that difference is
        // exactly why it cannot answer the ambiguity question.
        TrackCandidate(entryId, ns, tenantId);

        if (tenantId.Length != 0)
            return; // tenant partitions are excluded from the global locator/count by design

        if (_idToNamespace.TryAdd(entryId, ns))
            Interlocked.Increment(ref _totalCountApprox);
        else
            _idToNamespace[entryId] = ns;
    }

    /// <summary>
    /// Retract one (<paramref name="tenantId"/>, <paramref name="ns"/>, <paramref name="entryId"/>)
    /// placement from the candidate index, and for the legacy tenant also from the locator +
    /// TotalCount. The namespace and tenant are required rather than looked up: the candidate index
    /// is set-valued, so retracting the right placement needs the full triple, and a delete in one
    /// namespace must not evict a surviving twin in another.
    /// </summary>
    public void UntrackEntry(string entryId, string ns, string tenantId)
    {
        UntrackCandidate(entryId, ns, tenantId);

        if (tenantId.Length != 0)
            return;

        // Deliberately unconditional on ns, unlike RemoveNamespace above: this is the pre-existing
        // legacy-locator contract and the delete paths that call it already resolved through the
        // locator or are deleting the only legacy placement they know of.
        if (_idToNamespace.TryRemove(entryId, out _))
            Interlocked.Decrement(ref _totalCountApprox);
    }

    /// <summary>
    /// Every namespace of <paramref name="tenantId"/> that currently holds
    /// <paramref name="entryId"/>. Constant-time in the number of namespaces the tenant owns —
    /// this is the structure that replaces walking them all.
    ///
    /// Exact only over partitions that have been materialized in this process, so a caller that
    /// needs completeness must have loaded the persisted namespaces first (see
    /// <see cref="LoadAll"/>). Returning a stale-but-superset answer would be tolerable — every
    /// caller re-reads the partition to confirm — but a stale MISS would not, which is why every
    /// path that can move an id between partitions maintains this index.
    ///
    /// The lookup itself takes no lock and still cannot miss a placement that is live for the whole
    /// call: a bucket is unpublished only while empty and only under its stripe, so the instance
    /// read here cannot have been retired while it still held the placement, and no bucket
    /// published afterwards can hold one that already existed when this read began.
    /// </summary>
    public IReadOnlyList<string> GetCandidateNamespaces(string entryId, string tenantId)
    {
        if (!_idCandidates.TryGetValue((tenantId, entryId), out var bucket))
            return Array.Empty<string>();

        // Materialize rather than hand back the live key collection: the caller iterates it while
        // probing partitions, and a concurrent write to this bucket would otherwise mutate the
        // sequence mid-walk.
        return bucket.Keys.ToList();
    }

    /// <summary>
    /// The stripe that serializes publication and retirement for one candidate key. Hashing the
    /// whole key rather than the id alone keeps two tenants that legitimately share an id from
    /// colliding on the same stripe more often than chance.
    /// </summary>
    private object CandidateStripe((string Tenant, string Id) key)
        => _candidateStripes[key.GetHashCode() & (CandidateStripeCount - 1)];

    /// <summary>
    /// Add one placement to the candidate index, under this key's stripe so it cannot interleave
    /// with a retirement of the same key. Placements for other keys, and every lookup, run
    /// unblocked; a concurrent <see cref="UntrackCandidate"/> for the same id in a DIFFERENT
    /// namespace genuinely races, because those two run under two different per-partition write
    /// locks and nothing above this serializes them.
    /// </summary>
    private void TrackCandidate(string entryId, string ns, string tenantId)
    {
        var key = (tenantId, entryId);
        lock (CandidateStripe(key))
        {
            // Fetching the bucket and writing into it is one atomic step against a retirement, so
            // the instance written here is still the published one when the stripe is released. No
            // post-write re-check can substitute for that: a retirement evaluates emptiness BEFORE
            // the write and unpublishes AFTER it, so the re-check passes and the placement is lost
            // anyway. That ordering is what the previous lock-free version could not close.
            _idCandidates.GetOrAdd(key, _ => new ConcurrentDictionary<string, byte>())[ns] = 0;
        }
    }

    /// <summary>
    /// Remove one placement from the candidate index, retiring the bucket once it is empty so a
    /// store that churns ids does not retain a dictionary per id ever seen. Retirement is the whole
    /// reason the stripe exists: emptiness has to still hold at the moment the instance is
    /// unpublished, and only mutual exclusion with <see cref="TrackCandidate"/> makes it hold.
    /// </summary>
    private void UntrackCandidate(string entryId, string ns, string tenantId)
    {
        var key = (tenantId, entryId);
        lock (CandidateStripe(key))
        {
            if (!_idCandidates.TryGetValue(key, out var bucket))
                return;

            bucket.TryRemove(ns, out _);
            if (!bucket.IsEmpty)
                return;

            // Plain TryRemove, not the value-matching overload used in RemoveNamespace. Under the
            // stripe no other thread can have published a different bucket for this key, so there
            // is nothing left for a value match to protect — and it never protected the case that
            // mattered, since the observed instance and the instance a racing placement just wrote
            // into are the same object.
            _idCandidates.TryRemove(key, out _);
        }
    }

    // ── HNSW Index Management (keyed by composed partition key) ──

    /// <summary>Get the HNSW index for a partition, or null if not built.</summary>
    public HnswIndex? GetHnswIndex(string partitionKey)
        => _hnswIndices.TryGetValue(partitionKey, out var idx) ? idx : null;

    /// <summary>Add an entry to the per-partition HNSW index, building the index if the partition is large enough.</summary>
    public void AddToHnsw(NsKey key, string id, float[] vector)
    {
        string pk = PartitionKey(key);
        var nsEntries = GetNamespace(key);
        int count = nsEntries?.Count ?? 0;

        if (!_hnswIndices.TryGetValue(pk, out var idx))
        {
            if (count < HnswThreshold)
                return; // Not large enough yet

            // Build HNSW from all existing entries in the partition
            idx = new HnswIndex();
            if (nsEntries is not null)
            {
                foreach (var (entry, _, _) in nsEntries.Values)
                    idx.Add(entry.Id, entry.Vector);
            }
            _hnswIndices[pk] = idx;
            return; // The new entry is already in nsEntries if called after dict update
        }

        idx.Add(id, vector);

        if (idx.NeedsRebuild)
            _hnswIndices[pk] = idx.Rebuild();
    }

    /// <summary>Remove an entry from the per-partition HNSW index.</summary>
    public void RemoveFromHnsw(string partitionKey, string id)
    {
        if (_hnswIndices.TryGetValue(partitionKey, out var idx))
        {
            idx.Remove(id);
            if (idx.NeedsRebuild)
                _hnswIndices[partitionKey] = idx.Rebuild();
        }
    }

    /// <summary>
    /// Invalidate the in-memory HNSW index for a partition and delete its persisted snapshot.
    /// Call this after bulk re-embedding so the stale index is rebuilt lazily on the next search.
    /// </summary>
    public void InvalidateHnswIndex(string partitionKey)
    {
        ValidatePartitionKey(partitionKey, nameof(partitionKey));
        _hnswIndices.TryRemove(partitionKey, out _);
        _persistence.DeleteHnswSnapshot(partitionKey);
    }

    /// <summary>
    /// Validate an ALREADY-COMPOSED partition key at an entry point that receives one rather than
    /// its two components. A well-formed key is either a bare namespace (legacy tenant) or exactly
    /// <c>tenant + separator + ns</c>, so at most one separator is legal and every other control
    /// character is a component that skipped its boundary guard. This cannot distinguish a legacy
    /// namespace that smuggled in one separator from a genuine tenant key — that ambiguity is
    /// inherent to the flattened representation and is closed where components are validated on the
    /// way in, not here.
    /// </summary>
    private static void ValidatePartitionKey(string partitionKey, string paramName)
    {
        int sep = partitionKey.IndexOf(Tenancy.PartitionSeparator);
        if (sep < 0)
        {
            Tenancy.ValidatePartitionComponent(partitionKey, paramName);
            return;
        }
        Tenancy.ValidatePartitionComponent(partitionKey[..sep], paramName);
        Tenancy.ValidatePartitionComponent(partitionKey[(sep + 1)..], paramName);
    }

    /// <summary>Try to restore a partition's HNSW index from a persisted snapshot. Falls back to lazy rebuild if snapshot is stale.</summary>
    private void TryRestoreHnsw(string partitionKey, NsKey key)
    {
        var snapshot = _persistence.LoadHnswSnapshot(partitionKey);
        if (snapshot == null) return;

        var nsEntries = GetNamespace(key);
        if (nsEntries == null) return;

        var restored = Retrieval.HnswIndex.RestoreFromSnapshot(snapshot, id =>
        {
            if (nsEntries.TryGetValue(id, out var tuple))
                return tuple.Entry.Vector;
            return null;
        });

        if (restored != null)
            _hnswIndices[partitionKey] = restored;
        // else: snapshot was stale, HNSW will be lazily rebuilt on first AddToHnsw call
    }

    /// <summary>Persist current HNSW index snapshots for every tenant partition of a namespace.</summary>
    private void ScheduleHnswSave(string ns)
    {
        foreach (var key in _namespaces.Keys)
        {
            if (key.Ns != ns) continue;
            string pk = PartitionKey(key);
            if (_hnswIndices.TryGetValue(pk, out var idx))
            {
                var snapshot = idx.CreateSnapshot();
                _persistence.SaveHnswSnapshotSync(pk, snapshot);
            }
        }
    }

    private void LoadEntries(string ns, List<CognitiveEntry> entries)
    {
        int added = 0;
        foreach (var entry in entries)
        {
            var key = new NsKey(entry.TenantId, entry.Ns);
            var nsDict = _namespaces.GetOrAdd(key, _ => new ConcurrentDictionary<string, (CognitiveEntry, float, QuantizedVector?)>());
            float norm = VectorMath.Norm(entry.Vector);
            var quantized = entry.LifecycleState is "ltm" or "archived"
                ? VectorQuantizer.Quantize(entry.Vector)
                : null;
            nsDict[entry.Id] = (entry, norm, quantized);

            // Track the placement the row actually landed in (entry.Ns, which keyed nsDict above),
            // not the namespace being loaded: a row whose Ns disagrees with its file lives in the
            // partition its own Ns names, and that is the partition a candidate lookup must send
            // the caller to. This is the path that makes the index complete after LoadAll — without
            // it, an id in a namespace untouched this process would resolve as a miss.
            TrackCandidate(entry.Id, entry.Ns, entry.TenantId);

            // Only legacy-tenant entries populate the global locator + count.
            if (entry.TenantId.Length == 0)
            {
                if (_idToNamespace.TryAdd(entry.Id, ns))
                    added++;
                else
                    _idToNamespace[entry.Id] = ns;
            }
        }
        if (added > 0)
            Interlocked.Add(ref _totalCountApprox, added);
    }
}
