using System.Collections.Concurrent;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Core.Services;

/// <summary>
/// Manages namespace-partitioned storage of cognitive entries with lazy loading from disk.
/// Infrastructure is thread-safe via ConcurrentDictionary. Per-namespace entry dictionaries
/// are also ConcurrentDictionary, allowing CognitiveIndex to use ReadLock for read paths
/// and WriteLock only for mutations.
/// </summary>
internal sealed class NamespaceStore
{
    /// <summary>Minimum namespace size to activate HNSW indexing.</summary>
    private const int HnswThreshold = 200;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>> _namespaces = new();
    private readonly ConcurrentDictionary<string, bool> _loadedNamespaces = new();
    private readonly ConcurrentDictionary<string, string> _idToNamespace = new();
    private readonly ConcurrentDictionary<string, HnswIndex> _hnswIndices = new();
    private readonly ConcurrentDictionary<string, object> _loadLocks = new();
    private readonly IStorageProvider _persistence;
    private readonly BM25Index _bm25;

    // Atomic total-entry count across all namespaces — maintained by TrackEntry /
    // UntrackEntry / RemoveNamespace / LoadEntries so memory-limits checks can run
    // without a cross-namespace lock. Approximate under concurrent mutations but
    // reliably incremented once per distinct id insertion.
    private long _totalCountApprox;

    public NamespaceStore(IStorageProvider persistence, BM25Index bm25)
    {
        _persistence = persistence;
        _bm25 = bm25;
    }

    /// <summary>Get the entry dictionary for a namespace (may be null if namespace doesn't exist).</summary>
    public ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>? GetNamespace(string ns)
    {
        return _namespaces.TryGetValue(ns, out var entries) ? entries : null;
    }

    /// <summary>Get or create the entry dictionary for a namespace.</summary>
    public ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> GetOrCreateNamespace(string ns)
    {
        return _namespaces.GetOrAdd(ns, _ => new ConcurrentDictionary<string, (CognitiveEntry, float, QuantizedVector?)>());
    }

    /// <summary>Remove a namespace entirely from in-memory state (entries, locator, BM25, HNSW, loaded tracking).</summary>
    public void RemoveNamespace(string ns)
    {
        if (_namespaces.TryRemove(ns, out var entries))
        {
            int removed = 0;
            foreach (var id in entries.Keys)
            {
                if (_idToNamespace.TryRemove(id, out _))
                    removed++;
            }
            if (removed > 0)
                Interlocked.Add(ref _totalCountApprox, -removed);
        }
        _loadedNamespaces.TryRemove(ns, out _);
        _bm25.ClearNamespace(ns);
        _hnswIndices.TryRemove(ns, out _);
        _persistence.DeleteHnswSnapshot(ns);
    }

    /// <summary>All namespace dictionaries (for cross-namespace operations).</summary>
    public IEnumerable<KeyValuePair<string, ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>>> AllNamespaces
        => _namespaces;

    /// <summary>Total entries across all loaded namespaces. O(1) atomic read — safe without a lock.</summary>
    public int TotalCount => (int)Interlocked.Read(ref _totalCountApprox);

    /// <summary>Get all known namespace names (loaded + persisted).</summary>
    public IReadOnlyList<string> GetNamespaceNames()
    {
        var persisted = _persistence.GetPersistedNamespaces();
        var inMemory = _namespaces.Keys;
        return persisted.Union(inMemory).Distinct().ToList();
    }

    /// <summary>
    /// Ensure a namespace is loaded from disk. Thread-safe via per-namespace load lock
    /// with double-check pattern. Multiple namespaces can be loaded concurrently.
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
            _namespaces.GetOrAdd(ns, _ => new ConcurrentDictionary<string, (CognitiveEntry, float, QuantizedVector?)>());

            LoadEntries(ns, data.Entries);

            if (!_bm25.HasNamespace(ns))
                _bm25.RebuildNamespace(ns, data.Entries);

            // Try to restore HNSW from persisted snapshot (avoids O(N log N) rebuild)
            if (data.Entries.Count >= HnswThreshold && !_hnswIndices.ContainsKey(ns))
                TryRestoreHnsw(ns);

            _loadedNamespaces.TryAdd(ns, true);
        }
    }

    /// <summary>Load all persisted namespaces from disk.</summary>
    public void LoadAll()
    {
        foreach (var ns in _persistence.GetPersistedNamespaces())
            EnsureLoaded(ns);
    }

    /// <summary>Snapshot current namespace data and schedule a debounced write to disk.</summary>
    public void ScheduleSave(string ns)
    {
        var data = new NamespaceData();
        if (_namespaces.TryGetValue(ns, out var entries))
            data.Entries = entries.Values.Select(t => t.Entry).ToList();

        _persistence.ScheduleSave(ns, () => data);

        // Persist HNSW snapshot alongside namespace data
        ScheduleHnswSave(ns);
    }

    /// <summary>Schedule an incremental upsert (SQLite) or full snapshot (JSON) for a single entry.</summary>
    public void ScheduleEntryUpsert(string ns, CognitiveEntry entry)
    {
        if (_persistence.SupportsIncrementalWrites)
            _persistence.ScheduleUpsertEntry(ns, entry);
        else
            ScheduleSave(ns);
    }

    /// <summary>Schedule an incremental delete (SQLite) or full snapshot (JSON) for a single entry.</summary>
    public void ScheduleEntryDelete(string ns, string entryId)
    {
        if (_persistence.SupportsIncrementalWrites)
            _persistence.ScheduleDeleteEntry(ns, entryId);
        else
            ScheduleSave(ns);
    }

    /// <summary>Index an entry in BM25 for keyword search.</summary>
    public void IndexBM25(CognitiveEntry entry) => _bm25.Index(entry);

    /// <summary>Remove an entry from the BM25 keyword index.</summary>
    public void RemoveBM25(string id, string ns) => _bm25.Remove(id, ns);

    // ── Id Locator (reverse index: entryId → namespace) ──

    /// <summary>Resolve namespace via locator, falling back to LoadAll if not found.</summary>
    public bool TryResolveOrLoad(string entryId, out string ns)
    {
        if (_idToNamespace.TryGetValue(entryId, out ns!))
            return true;
        LoadAll();
        return _idToNamespace.TryGetValue(entryId, out ns!);
    }

    /// <summary>
    /// Track an entry's namespace in the locator. Increments TotalCount atomically only
    /// when the id was not already tracked (so upsert-of-existing doesn't drift the count).
    /// Invariant: an entry id does not move between namespaces — upserting an existing id
    /// to a different ns silently leaves the locator pointing at the new ns (pre-existing
    /// behavior), but the count is not double-incremented.
    /// </summary>
    public void TrackEntry(string entryId, string ns)
    {
        if (_idToNamespace.TryAdd(entryId, ns))
            Interlocked.Increment(ref _totalCountApprox);
        else
            _idToNamespace[entryId] = ns;
    }

    /// <summary>Remove an entry from the locator. Decrements TotalCount atomically if the id was tracked.</summary>
    public void UntrackEntry(string entryId)
    {
        if (_idToNamespace.TryRemove(entryId, out _))
            Interlocked.Decrement(ref _totalCountApprox);
    }

    // ── HNSW Index Management ──

    /// <summary>Get the HNSW index for a namespace, or null if not built.</summary>
    public HnswIndex? GetHnswIndex(string ns)
        => _hnswIndices.TryGetValue(ns, out var idx) ? idx : null;

    /// <summary>Add an entry to the per-namespace HNSW index, building the index if the namespace is large enough.</summary>
    public void AddToHnsw(string ns, string id, float[] vector)
    {
        var nsEntries = GetNamespace(ns);
        int count = nsEntries?.Count ?? 0;

        if (!_hnswIndices.TryGetValue(ns, out var idx))
        {
            if (count < HnswThreshold)
                return; // Not large enough yet

            // Build HNSW from all existing entries
            idx = new HnswIndex();
            if (nsEntries is not null)
            {
                foreach (var (entry, _, _) in nsEntries.Values)
                    idx.Add(entry.Id, entry.Vector);
            }
            _hnswIndices[ns] = idx;
            return; // The new entry is already in nsEntries if called after dict update
        }

        idx.Add(id, vector);

        if (idx.NeedsRebuild)
            _hnswIndices[ns] = idx.Rebuild();
    }

    /// <summary>Remove an entry from the per-namespace HNSW index.</summary>
    public void RemoveFromHnsw(string ns, string id)
    {
        if (_hnswIndices.TryGetValue(ns, out var idx))
        {
            idx.Remove(id);
            if (idx.NeedsRebuild)
                _hnswIndices[ns] = idx.Rebuild();
        }
    }

    /// <summary>
    /// Invalidate the in-memory HNSW index for a namespace and delete its persisted snapshot.
    /// Call this after bulk re-embedding so the stale index is rebuilt lazily on the next search.
    /// </summary>
    public void InvalidateHnswIndex(string ns)
    {
        _hnswIndices.TryRemove(ns, out _);
        _persistence.DeleteHnswSnapshot(ns);
    }

    /// <summary>Try to restore HNSW index from a persisted snapshot. Falls back to lazy rebuild if snapshot is stale.</summary>
    private void TryRestoreHnsw(string ns)
    {
        var snapshot = _persistence.LoadHnswSnapshot(ns);
        if (snapshot == null) return;

        var nsEntries = GetNamespace(ns);
        if (nsEntries == null) return;

        var restored = Retrieval.HnswIndex.RestoreFromSnapshot(snapshot, id =>
        {
            if (nsEntries.TryGetValue(id, out var tuple))
                return tuple.Entry.Vector;
            return null;
        });

        if (restored != null)
            _hnswIndices[ns] = restored;
        // else: snapshot was stale, HNSW will be lazily rebuilt on first AddToHnsw call
    }

    /// <summary>Persist the current HNSW index snapshot for a namespace (if one exists).</summary>
    private void ScheduleHnswSave(string ns)
    {
        if (_hnswIndices.TryGetValue(ns, out var idx))
        {
            var snapshot = idx.CreateSnapshot();
            _persistence.SaveHnswSnapshotSync(ns, snapshot);
        }
    }

    private void LoadEntries(string ns, List<CognitiveEntry> entries)
    {
        var nsDict = _namespaces.GetOrAdd(ns, _ => new ConcurrentDictionary<string, (CognitiveEntry, float, QuantizedVector?)>());
        int added = 0;
        foreach (var entry in entries)
        {
            float norm = VectorMath.Norm(entry.Vector);
            var quantized = entry.LifecycleState is "ltm" or "archived"
                ? VectorQuantizer.Quantize(entry.Vector)
                : null;
            nsDict[entry.Id] = (entry, norm, quantized);
            if (_idToNamespace.TryAdd(entry.Id, ns))
                added++;
            else
                _idToNamespace[entry.Id] = ns;
        }
        if (added > 0)
            Interlocked.Add(ref _totalCountApprox, added);
    }
}
