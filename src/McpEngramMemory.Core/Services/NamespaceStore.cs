using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Core.Services;

/// <summary>
/// Manages namespace-partitioned storage of cognitive entries with lazy loading from disk.
/// NOT thread-safe — callers must manage their own locking (CognitiveIndex holds the lock).
///
/// Extracted from CognitiveIndex to separate namespace management concerns from search logic.
/// </summary>
internal sealed class NamespaceStore
{
    /// <summary>Minimum namespace size to activate HNSW indexing.</summary>
    private const int HnswThreshold = 200;

    private readonly Dictionary<string, Dictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>> _namespaces = new();
    private readonly HashSet<string> _loadedNamespaces = new();
    private readonly Dictionary<string, string> _idToNamespace = new();
    private readonly Dictionary<string, HnswIndex> _hnswIndices = new();
    private readonly IStorageProvider _persistence;
    private readonly BM25Index _bm25;

    public NamespaceStore(IStorageProvider persistence, BM25Index bm25)
    {
        _persistence = persistence;
        _bm25 = bm25;
    }

    /// <summary>Get the entry dictionary for a namespace (may be null if namespace doesn't exist).</summary>
    public Dictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>? GetNamespace(string ns)
    {
        return _namespaces.TryGetValue(ns, out var entries) ? entries : null;
    }

    /// <summary>Get or create the entry dictionary for a namespace.</summary>
    public Dictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> GetOrCreateNamespace(string ns)
    {
        if (!_namespaces.TryGetValue(ns, out var entries))
        {
            entries = new();
            _namespaces[ns] = entries;
        }
        return entries;
    }

    /// <summary>Remove a namespace entirely from in-memory state (entries, locator, BM25, HNSW, loaded tracking).</summary>
    public void RemoveNamespace(string ns)
    {
        if (_namespaces.TryGetValue(ns, out var entries))
        {
            foreach (var id in entries.Keys)
                _idToNamespace.Remove(id);
            _namespaces.Remove(ns);
        }
        _loadedNamespaces.Remove(ns);
        _bm25.ClearNamespace(ns);
        _hnswIndices.Remove(ns);
        _persistence.DeleteHnswSnapshot(ns);
    }

    /// <summary>All namespace dictionaries (for cross-namespace operations).</summary>
    public IEnumerable<KeyValuePair<string, Dictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>>> AllNamespaces
        => _namespaces;

    /// <summary>Total entries across all loaded namespaces.</summary>
    public int TotalCount => _namespaces.Values.Sum(ns => ns.Count);

    /// <summary>Get all known namespace names (loaded + persisted).</summary>
    public IReadOnlyList<string> GetNamespaceNames()
    {
        var persisted = _persistence.GetPersistedNamespaces();
        var inMemory = _namespaces.Keys;
        return persisted.Union(inMemory).Distinct().ToList();
    }

    /// <summary>Ensure a namespace is loaded from disk. Safe to call multiple times (idempotent).</summary>
    public void EnsureLoaded(string ns)
    {
        if (_loadedNamespaces.Contains(ns))
            return;

        var data = _persistence.LoadNamespace(ns);
        if (!_namespaces.ContainsKey(ns))
            _namespaces[ns] = new();

        LoadEntries(ns, data.Entries);

        if (!_bm25.HasNamespace(ns))
            _bm25.RebuildNamespace(ns, data.Entries);

        // Try to restore HNSW from persisted snapshot (avoids O(N log N) rebuild)
        if (data.Entries.Count >= HnswThreshold && !_hnswIndices.ContainsKey(ns))
            TryRestoreHnsw(ns);

        _loadedNamespaces.Add(ns);
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

    /// <summary>Resolve which namespace an entry belongs to. O(1) lookup.</summary>
    public bool TryResolveNamespace(string entryId, out string ns)
        => _idToNamespace.TryGetValue(entryId, out ns!);

    /// <summary>Resolve namespace via locator, falling back to LoadAll if not found.</summary>
    public bool TryResolveOrLoad(string entryId, out string ns)
    {
        if (TryResolveNamespace(entryId, out ns))
            return true;
        LoadAll();
        return TryResolveNamespace(entryId, out ns);
    }

    /// <summary>Track an entry's namespace in the locator.</summary>
    public void TrackEntry(string entryId, string ns)
        => _idToNamespace[entryId] = ns;

    /// <summary>Remove an entry from the locator.</summary>
    public void UntrackEntry(string entryId)
        => _idToNamespace.Remove(entryId);

    /// <summary>Check if BM25 index exists for a namespace.</summary>
    public bool HasBM25Namespace(string ns) => _bm25.HasNamespace(ns);

    /// <summary>Rebuild BM25 index for a namespace.</summary>
    public void RebuildBM25Namespace(string ns, List<CognitiveEntry> entries)
        => _bm25.RebuildNamespace(ns, entries);

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
        foreach (var entry in entries)
        {
            float norm = VectorMath.Norm(entry.Vector);
            var quantized = entry.LifecycleState is "ltm" or "archived"
                ? VectorQuantizer.Quantize(entry.Vector)
                : null;
            _namespaces[ns][entry.Id] = (entry, norm, quantized);
            _idToNamespace[entry.Id] = ns;
        }
    }
}
