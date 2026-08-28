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
    // LEGACY-ONLY reverse index: id → ns. Only legacy-tenant ("") entries are tracked here so the
    // global id-based operations resolve strictly within the legacy tenant.
    private readonly ConcurrentDictionary<string, string> _idToNamespace = new();
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
    }

    /// <summary>
    /// Remove exactly one tenant + namespace partition from in-memory state and its search
    /// indexes. Other tenant partitions with the same namespace are left loaded and untouched.
    /// Persistence rows are deliberately not deleted here; the caller must schedule deletes for
    /// the removed entry ids so incremental providers can target the full (tenant, ns, id) key.
    /// </summary>
    public void RemoveNamespace(NsKey key)
    {
        // A delete reaches storage without ever constructing a CognitiveEntry, so the entry write
        // ctor's guard does not cover it. Validate before anything is removed: a namespace carrying
        // the separator would compose to another tenant's partition key and clear that tenant's
        // BM25/HNSW indexes and persisted snapshot.
        Tenancy.ValidatePartitionComponent(key.Tenant, nameof(key));
        Tenancy.ValidatePartitionComponent(key.Ns, nameof(key));

        if (_namespaces.TryRemove(key, out var entries) && key.Tenant.Length == 0)
        {
            int removed = 0;
            // Use the KeyValuePair overload of TryRemove so we only delete a locator entry
            // when it currently points at THIS namespace. Guards against a rare but real
            // scenario: id X was upserted to ns=A (orphan), then re-upserted to ns=B (locator
            // now points at B). If we unconditionally TryRemove(X), we'd blow away B's
            // locator AND decrement the total count while B's entries dict still has X —
            // driving TotalCount negative.
            foreach (var id in entries.Keys)
            {
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

    /// <summary>Load all persisted namespaces from disk.</summary>
    public void LoadAll()
    {
        foreach (var ns in _persistence.GetPersistedNamespaces())
            EnsureLoaded(ns);
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

    // ── Id Locator (reverse index: entryId → namespace) — LEGACY tenant only ──

    /// <summary>Resolve a legacy-tenant namespace via locator, falling back to LoadAll if not found.</summary>
    public bool TryResolveOrLoad(string entryId, out string ns)
    {
        if (_idToNamespace.TryGetValue(entryId, out ns!))
            return true;
        LoadAll();
        return _idToNamespace.TryGetValue(entryId, out ns!);
    }

    /// <summary>
    /// Track an entry's namespace in the LEGACY locator + total count. Tenant entries (non-empty
    /// tenantId) are intentionally NOT tracked here, keeping the global id/count paths legacy-scoped.
    /// Increments TotalCount atomically only when the id was not already tracked (so upsert-of-existing
    /// doesn't drift the count).
    /// </summary>
    public void TrackEntry(string entryId, string ns, string tenantId)
    {
        if (tenantId.Length != 0)
            return; // tenant partitions are excluded from the global locator/count by design

        if (_idToNamespace.TryAdd(entryId, ns))
            Interlocked.Increment(ref _totalCountApprox);
        else
            _idToNamespace[entryId] = ns;
    }

    /// <summary>Remove a (legacy) entry from the locator. Decrements TotalCount atomically if the id was tracked.</summary>
    public void UntrackEntry(string entryId)
    {
        if (_idToNamespace.TryRemove(entryId, out _))
            Interlocked.Decrement(ref _totalCountApprox);
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
