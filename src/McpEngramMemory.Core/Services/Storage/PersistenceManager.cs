using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpEngramMemory.Core.Models;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Storage;

/// <summary>
/// JSON file-based persistence per namespace with debounced async writes.
/// Uses Base64 encoding for float[] vectors to reduce disk footprint by ~60%.
/// </summary>
public sealed class PersistenceManager : IStorageProvider
{
    /// <summary>Current storage format version. Increment when making breaking changes to the JSON schema.</summary>
    public const int CurrentStorageVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new FloatArrayBase64Converter() }
    };

    private readonly string _basePath;
    private readonly object _timerLock = new();
    private readonly TimeSpan _debounceDelay;
    private readonly ILogger<PersistenceManager>? _logger;
    private bool _disposed;

    // Pending namespace saves (keyed by namespace name)
    private readonly Dictionary<string, (Timer Timer, Func<NamespaceData> DataProvider)> _pendingNsSaves = new();

    // Pending global edge save (separate from namespace saves to avoid dummy-data overwrite)
    private Timer? _pendingEdgeTimer;
    private Func<List<GraphEdge>>? _pendingEdgeProvider;

    // Pending cluster save
    private Timer? _pendingClusterTimer;
    private Func<List<SemanticCluster>>? _pendingClusterProvider;

    // Pending collapse history save
    private Timer? _pendingCollapseHistoryTimer;
    private Func<List<CollapseRecord>>? _pendingCollapseHistoryProvider;

    // Pending decay config save
    private Timer? _pendingDecayConfigTimer;
    private Func<Dictionary<string, DecayConfig>>? _pendingDecayConfigProvider;

    /// <summary>JSON backend does not support incremental writes — always uses full namespace snapshots.</summary>
    public bool SupportsIncrementalWrites => false;

    /// <inheritdoc />
    public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) { }

    /// <inheritdoc />
    public void ScheduleDeleteEntry(string ns, string entryId) { }

    public PersistenceManager(string? basePath = null, int debounceMs = 500, ILogger<PersistenceManager>? logger = null)
    {
        _basePath = basePath ?? Path.Combine(AppContext.BaseDirectory, "data");
        _debounceDelay = TimeSpan.FromMilliseconds(debounceMs);
        _logger = logger;
        Directory.CreateDirectory(_basePath);
    }

    /// <summary>
    /// Load namespace data from disk. Returns empty data if file does not exist or is corrupted.
    /// Validates checksum if a companion .sha256 file exists.
    /// </summary>
    public NamespaceData LoadNamespace(string ns)
    {
        var path = GetNamespacePath(ns);
        if (!File.Exists(path))
            return new NamespaceData { StorageVersion = CurrentStorageVersion };

        try
        {
            var json = File.ReadAllText(path);
            if (!VerifyChecksum(path, json))
            {
                _logger?.LogWarning("Checksum mismatch for namespace '{Namespace}', data may be corrupted", ns);
            }
            var data = JsonSerializer.Deserialize<NamespaceData>(json, JsonOptions) ?? new NamespaceData();

            // Forward-compatibility guard: reject files from newer versions
            if (data.StorageVersion > CurrentStorageVersion)
            {
                _logger?.LogError(
                    "Namespace '{Namespace}' has storage version {FileVersion} but this server supports up to version {CurrentVersion}. " +
                    "Upgrade the server or use the version that created this data.",
                    ns, data.StorageVersion, CurrentStorageVersion);
                return new NamespaceData { StorageVersion = CurrentStorageVersion };
            }

            // Run migrations for older versions
            if (data.StorageVersion < CurrentStorageVersion)
            {
                data = RunMigrations(data, ns);
            }

            return data;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Corrupted JSON in namespace '{Namespace}', returning empty data", ns);
            return new NamespaceData { StorageVersion = CurrentStorageVersion };
        }
    }

    /// <summary>
    /// Run sequential migrations from the data's version to CurrentStorageVersion.
    /// Follows the same pattern as SqliteStorageProvider.RunMigrations.
    /// </summary>
    private NamespaceData RunMigrations(NamespaceData data, string ns)
    {
        int fromVersion = data.StorageVersion;

        // v1 → v2: No structural changes needed — v1 data is compatible.
        // Future migrations add cases here:
        // if (data.StorageVersion < 3) MigrateToV3(data);

        data.StorageVersion = CurrentStorageVersion;
        _logger?.LogInformation("Namespace '{Namespace}' migrated from storage v{From} to v{To}", ns, fromVersion, CurrentStorageVersion);

        // Persist the migrated data immediately
        SaveNamespaceSync(ns, data);
        return data;
    }

    /// <summary>
    /// Load global (cross-namespace) edges from disk. Returns empty list if file is corrupted.
    /// </summary>
    public List<GraphEdge> LoadGlobalEdges()
        => LoadGlobalFile<List<GraphEdge>>(Path.Combine(_basePath, "_edges.json"), "edges") ?? new();

    /// <summary>
    /// Load clusters from disk. Returns empty list if file is corrupted.
    /// </summary>
    public List<SemanticCluster> LoadClusters()
        => LoadGlobalFile<List<SemanticCluster>>(Path.Combine(_basePath, "_clusters.json"), "clusters") ?? new();

    /// <summary>
    /// Load collapse history from disk.
    /// </summary>
    public List<CollapseRecord> LoadCollapseHistory()
        => LoadGlobalFile<List<CollapseRecord>>(Path.Combine(_basePath, "_collapse_history.json"), "collapse history") ?? new();

    /// <summary>
    /// Load per-namespace decay configs from disk.
    /// </summary>
    public Dictionary<string, DecayConfig> LoadDecayConfigs()
    {
        var list = LoadGlobalFile<List<DecayConfig>>(Path.Combine(_basePath, "_decay_configs.json"), "decay configs");
        return list?.ToDictionary(c => c.Ns) ?? new();
    }

    private T? LoadGlobalFile<T>(string path, string label) where T : class
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            if (!VerifyChecksum(path, json))
                _logger?.LogWarning("Checksum mismatch for {Label} file, data may be corrupted", label);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Corrupted JSON in {Label} file, returning empty data", label);
            return null;
        }
    }

    /// <summary>
    /// Schedule a debounced save of namespace data.
    /// </summary>
    public void ScheduleSave(string ns, Func<NamespaceData> dataProvider)
    {
        lock (_timerLock)
        {
            if (_disposed) return;

            if (_pendingNsSaves.TryGetValue(ns, out var existing))
                existing.Timer.Dispose();

            var timer = new Timer(_ =>
            {
                Func<NamespaceData>? provider = null;
                lock (_timerLock)
                {
                    if (_pendingNsSaves.TryGetValue(ns, out var entry))
                    {
                        provider = entry.DataProvider;
                        entry.Timer.Dispose();
                        _pendingNsSaves.Remove(ns);
                    }
                }
                if (provider is not null)
                    WriteNamespace(ns, provider);
            }, null, _debounceDelay, Timeout.InfiniteTimeSpan);

            _pendingNsSaves[ns] = (timer, dataProvider);
        }
    }

    /// <summary>
    /// Schedule a debounced save of global edges.
    /// Data provider should return a pre-captured snapshot (no lock re-entry).
    /// </summary>
    public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider)
    {
        lock (_timerLock)
        {
            if (_disposed) return;

            _pendingEdgeTimer?.Dispose();
            _pendingEdgeProvider = dataProvider;

            _pendingEdgeTimer = new Timer(_ =>
            {
                Func<List<GraphEdge>>? provider;
                lock (_timerLock)
                {
                    provider = _pendingEdgeProvider;
                    _pendingEdgeProvider = null;
                    _pendingEdgeTimer?.Dispose();
                    _pendingEdgeTimer = null;
                }
                if (provider is not null)
                    WriteGlobalEdges(provider);
            }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Schedule a debounced save of clusters.
    /// Data provider should return a pre-captured snapshot (no lock re-entry).
    /// </summary>
    public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider)
    {
        lock (_timerLock)
        {
            if (_disposed) return;

            _pendingClusterTimer?.Dispose();
            _pendingClusterProvider = dataProvider;

            _pendingClusterTimer = new Timer(_ =>
            {
                Func<List<SemanticCluster>>? provider;
                lock (_timerLock)
                {
                    provider = _pendingClusterProvider;
                    _pendingClusterProvider = null;
                    _pendingClusterTimer?.Dispose();
                    _pendingClusterTimer = null;
                }
                if (provider is not null)
                    WriteClusters(provider);
            }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Schedule a debounced save of collapse history.
    /// </summary>
    public void ScheduleSaveCollapseHistory(Func<List<CollapseRecord>> dataProvider)
    {
        lock (_timerLock)
        {
            if (_disposed) return;

            _pendingCollapseHistoryTimer?.Dispose();
            _pendingCollapseHistoryProvider = dataProvider;

            _pendingCollapseHistoryTimer = new Timer(_ =>
            {
                Func<List<CollapseRecord>>? provider;
                lock (_timerLock)
                {
                    provider = _pendingCollapseHistoryProvider;
                    _pendingCollapseHistoryProvider = null;
                    _pendingCollapseHistoryTimer?.Dispose();
                    _pendingCollapseHistoryTimer = null;
                }
                if (provider is not null)
                    WriteCollapseHistory(provider);
            }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Schedule a debounced save of decay configs.
    /// </summary>
    public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider)
    {
        lock (_timerLock)
        {
            if (_disposed) return;

            _pendingDecayConfigTimer?.Dispose();
            _pendingDecayConfigProvider = dataProvider;

            _pendingDecayConfigTimer = new Timer(_ =>
            {
                Func<Dictionary<string, DecayConfig>>? provider;
                lock (_timerLock)
                {
                    provider = _pendingDecayConfigProvider;
                    _pendingDecayConfigProvider = null;
                    _pendingDecayConfigTimer?.Dispose();
                    _pendingDecayConfigTimer = null;
                }
                if (provider is not null)
                    WriteDecayConfigs(provider);
            }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Synchronously save namespace data.
    /// </summary>
    public void SaveNamespaceSync(string ns, NamespaceData data)
    {
        data.StorageVersion = CurrentStorageVersion;
        var path = GetNamespacePath(ns);
        var json = JsonSerializer.Serialize(data, JsonOptions);
        AtomicWriteAllText(path, json);
    }

    /// <summary>
    /// Get all namespace names from existing files on disk.
    /// </summary>
    public IReadOnlyList<string> GetPersistedNamespaces()
    {
        if (!Directory.Exists(_basePath))
            return Array.Empty<string>();

        return Directory.GetFiles(_basePath, "*.json")
            .Where(f => !f.EndsWith(".hnsw.json", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != null && !n.StartsWith("_") && !n.StartsWith("__"))
            .Select(n => n!)
            .ToList();
    }

    /// <summary>
    /// Flush all pending saves immediately and synchronously.
    /// </summary>
    public void Flush()
    {
        List<(string Ns, Func<NamespaceData> Provider)> pendingNs;
        Func<List<GraphEdge>>? edgeProvider;
        Func<List<SemanticCluster>>? clusterProvider;
        Func<List<CollapseRecord>>? collapseHistoryProvider;
        Func<Dictionary<string, DecayConfig>>? decayConfigProvider;

        lock (_timerLock)
        {
            pendingNs = _pendingNsSaves
                .Select(kv => (kv.Key, kv.Value.DataProvider))
                .ToList();
            foreach (var (_, (timer, _)) in _pendingNsSaves)
                timer.Dispose();
            _pendingNsSaves.Clear();

            edgeProvider = _pendingEdgeProvider;
            _pendingEdgeProvider = null;
            _pendingEdgeTimer?.Dispose();
            _pendingEdgeTimer = null;

            clusterProvider = _pendingClusterProvider;
            _pendingClusterProvider = null;
            _pendingClusterTimer?.Dispose();
            _pendingClusterTimer = null;

            collapseHistoryProvider = _pendingCollapseHistoryProvider;
            _pendingCollapseHistoryProvider = null;
            _pendingCollapseHistoryTimer?.Dispose();
            _pendingCollapseHistoryTimer = null;

            decayConfigProvider = _pendingDecayConfigProvider;
            _pendingDecayConfigProvider = null;
            _pendingDecayConfigTimer?.Dispose();
            _pendingDecayConfigTimer = null;
        }

        foreach (var (ns, provider) in pendingNs)
            WriteNamespace(ns, provider);

        if (edgeProvider is not null)
            WriteGlobalEdges(edgeProvider);

        if (clusterProvider is not null)
            WriteClusters(clusterProvider);

        if (collapseHistoryProvider is not null)
            WriteCollapseHistory(collapseHistoryProvider);

        if (decayConfigProvider is not null)
            WriteDecayConfigs(decayConfigProvider);
    }

    /// <summary>Load persisted HNSW graph snapshot for a namespace.</summary>
    public HnswSnapshot? LoadHnswSnapshot(string ns)
    {
        var path = GetHnswPath(ns);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            if (!VerifyChecksum(path, json))
                _logger?.LogWarning("Checksum mismatch for HNSW snapshot '{Namespace}'", ns);
            return JsonSerializer.Deserialize<HnswSnapshot>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Corrupted HNSW snapshot for namespace '{Namespace}', will rebuild", ns);
            return null;
        }
    }

    /// <summary>Save an HNSW graph snapshot for a namespace.</summary>
    public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot)
    {
        var path = GetHnswPath(ns);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        AtomicWriteAllText(path, json);
    }

    /// <summary>Delete persisted HNSW snapshot for a namespace.</summary>
    public void DeleteHnswSnapshot(string ns)
    {
        var path = GetHnswPath(ns);
        if (File.Exists(path)) File.Delete(path);
        var checksumPath = path + ".sha256";
        if (File.Exists(checksumPath)) File.Delete(checksumPath);
    }

    /// <summary>Delete all entries in a namespace by removing its JSON and checksum files from disk.</summary>
    public Task DeleteNamespaceAsync(string ns)
    {
        var path = GetNamespacePath(ns);
        if (File.Exists(path)) File.Delete(path);
        var checksumPath = path + ".sha256";
        if (File.Exists(checksumPath)) File.Delete(checksumPath);
        DeleteHnswSnapshot(ns);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_timerLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Flush();
    }

    private string GetNamespacePath(string ns)
    {
        // Sanitize namespace for filename safety
        var safe = string.Join("_", ns.Split(Path.GetInvalidFileNameChars()));
        safe = safe.Replace("..", "_"); // Prevent path traversal
        var path = Path.Combine(_basePath, $"{safe}.json");

        // Guard against path traversal
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(_basePath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Invalid namespace: '{ns}'");

        return path;
    }

    private string GetHnswPath(string ns)
    {
        var safe = string.Join("_", ns.Split(Path.GetInvalidFileNameChars()));
        safe = safe.Replace("..", "_");
        var path = Path.Combine(_basePath, $"{safe}.hnsw.json");

        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(_basePath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Invalid namespace: '{ns}'");

        return path;
    }

    private void WriteNamespace(string ns, Func<NamespaceData> provider)
    {
        try
        {
            var data = provider();
            SaveNamespaceSync(ns, data);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save namespace '{Namespace}'", ns);
        }
    }

    private void WriteGlobalEdges(Func<List<GraphEdge>> provider)
    {
        try
        {
            var edges = provider();
            var json = JsonSerializer.Serialize(edges, JsonOptions);
            var path = Path.Combine(_basePath, "_edges.json");
            AtomicWriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save global edges");
        }
    }

    private void WriteClusters(Func<List<SemanticCluster>> provider)
    {
        try
        {
            var clusters = provider();
            var json = JsonSerializer.Serialize(clusters, JsonOptions);
            var path = Path.Combine(_basePath, "_clusters.json");
            AtomicWriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save clusters");
        }
    }

    private void WriteCollapseHistory(Func<List<CollapseRecord>> provider)
    {
        try
        {
            var records = provider();
            var json = JsonSerializer.Serialize(records, JsonOptions);
            var path = Path.Combine(_basePath, "_collapse_history.json");
            AtomicWriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save collapse history");
        }
    }

    private void WriteDecayConfigs(Func<Dictionary<string, DecayConfig>> provider)
    {
        try
        {
            var configs = provider();
            var list = configs.Values.ToList();
            var json = JsonSerializer.Serialize(list, JsonOptions);
            var path = Path.Combine(_basePath, "_decay_configs.json");
            AtomicWriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save decay configs");
        }
    }

    /// <summary>Write to a temp file then rename for crash-safe atomic writes. Also writes a SHA-256 checksum companion file.</summary>
    private static void AtomicWriteAllText(string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var tmpPath = path + ".tmp";
        File.WriteAllBytes(tmpPath, bytes);
        File.Move(tmpPath, path, overwrite: true);

        // Write checksum companion file (reuse already-encoded bytes)
        var hash = SHA256.HashData(bytes);
        var checksumPath = path + ".sha256";
        File.WriteAllText(checksumPath, Convert.ToHexString(hash));
    }

    /// <summary>Verify file content against its companion .sha256 checksum file. Returns true if no checksum file exists (legacy data).</summary>
    private bool VerifyChecksum(string path, string content)
    {
        var checksumPath = path + ".sha256";
        if (!File.Exists(checksumPath))
            return true; // No checksum = legacy data, pass through

        try
        {
            var expected = File.ReadAllText(checksumPath).Trim();
            var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error reading checksum file for '{Path}'", path);
            return true; // Don't block on checksum read errors
        }
    }
}
