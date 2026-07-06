using System.Collections.Concurrent;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;

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

    // Separator for composing a tenant-scoped partition key string used by the BM25 and HNSW
    // sub-indexes (which are keyed by string). ASCII Unit Separator — never appears in a namespace
    // or tenant id in practice. For the legacy tenant the composed key is just the namespace.
    private const char PartitionSeparator = (char)0x1F;

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
    public static string PartitionKey(string tenant, string ns)
        => tenant.Length == 0 ? ns : string.Concat(tenant, PartitionSeparator.ToString(), ns);

    /// <summary>Compose the partition-key string for a <see cref="NsKey"/>.</summary>
    public static string PartitionKey(NsKey key) => PartitionKey(key.Tenant, key.Ns);

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
    /// </summary>
    public void RemoveNamespace(string ns)
    {
        var key = new NsKey(string.Empty, ns);
        if (_namespaces.TryRemove(key, out var entries))
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
                if (_idToNamespace.TryRemove(new KeyValuePair<string, string>(id, ns)))
                    removed++;
            }
            if (removed > 0)
                Interlocked.Add(ref _totalCountApprox, -removed);
        }
        _loadedNamespaces.TryRemove(ns, out _);
        string pk = PartitionKey(key); // == ns for the legacy tenant
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
        _hnswIndices.TryRemove(partitionKey, out _);
        _persistence.DeleteHnswSnapshot(partitionKey);
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
