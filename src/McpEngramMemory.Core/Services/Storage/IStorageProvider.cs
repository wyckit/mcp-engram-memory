using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Storage;

/// <summary>
/// Abstraction for data persistence. Implementations handle loading, saving,
/// and debounced writes for namespace data, graph edges, and clusters.
/// </summary>
public interface IStorageProvider : IDisposable
{
    NamespaceData LoadNamespace(string ns);
    IReadOnlyList<string> GetPersistedNamespaces();
    void ScheduleSave(string ns, Func<NamespaceData> dataProvider);
    void SaveNamespaceSync(string ns, NamespaceData data);

    /// <summary>Whether this provider supports per-entry incremental writes (vs full namespace snapshots).</summary>
    bool SupportsIncrementalWrites { get; }

    /// <summary>Schedule a debounced upsert of a single entry. Only called when SupportsIncrementalWrites is true.</summary>
    void ScheduleUpsertEntry(string ns, CognitiveEntry entry);

    /// <summary>Schedule a debounced delete of a single entry. Only called when SupportsIncrementalWrites is true.</summary>
    void ScheduleDeleteEntry(string ns, string entryId);

    List<GraphEdge> LoadGlobalEdges();
    void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider);

    List<SemanticCluster> LoadClusters();
    void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider);

    List<CollapseRecord> LoadCollapseHistory();
    void ScheduleSaveCollapseHistory(Func<List<CollapseRecord>> dataProvider);

    Dictionary<string, DecayConfig> LoadDecayConfigs();
    void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider);

    /// <summary>Load persisted HNSW graph snapshot for a namespace. Returns null if none exists.</summary>
    HnswSnapshot? LoadHnswSnapshot(string ns);

    /// <summary>Save an HNSW graph snapshot for a namespace.</summary>
    void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot);

    /// <summary>Delete persisted HNSW snapshot for a namespace.</summary>
    void DeleteHnswSnapshot(string ns);

    /// <summary>Delete all entries in a namespace from the backing store.</summary>
    Task DeleteNamespaceAsync(string ns);

    void Flush();
}
