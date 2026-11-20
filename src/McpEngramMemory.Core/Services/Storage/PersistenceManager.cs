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

    private const string EncodedNamespacePrefix = "ns_enc_";
    private const string EncodedNamespaceHashPrefix = "ns_enc_h_";
    private const string TenantHnswPrefix = "hnsw_t_";
    private const string EncodedHnswPrefix = "hnsw_enc_";
    private const int MaxReversibleHexUtf8Bytes = 96;
    private static readonly HashSet<char> InvalidFileNameChars = new(Path.GetInvalidFileNameChars());
    private static readonly HashSet<string> ReservedControlPlaneStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "_edges", "_clusters", "_collapse_history", "_decay_configs"
    };
    private static readonly HashSet<string> WindowsReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly string _basePath;
    private readonly string _basePathWithSeparator;
    private readonly ILogger<PersistenceManager>? _logger;
    private readonly DebouncedStorageCoordinator<DefaultEntryStorageKey> _coordinator;
    // Retained directly on the provider instance pointing at the coordinator's scheduling lock
    // and disposal flag for deterministic concurrency-test synchronization.
    private readonly object _timerLock;
#pragma warning disable CS0414 // Reflected on by StorageTimerOwnershipTests; updated via coordinator disposal callback.
    private bool _disposed;
#pragma warning restore CS0414

    // Internal clock & anti-starvation seams for deterministic regression tests.
    internal TimeProvider TimerProvider
    {
        get => _coordinator.TimerProvider;
        init => _coordinator.TimerProvider = value;
    }

    internal int MaxGlobalDeferrals
    {
        get => _coordinator.MaxGlobalDeferrals;
        set => _coordinator.MaxGlobalDeferrals = value;
    }

    // Internal test seam for verifying _timerLock is not held during global file writes.
    internal Action<string>? OnBeforeGlobalWrite { get; set; }

    /// <summary>JSON backend does not support incremental writes — always uses full namespace snapshots.</summary>
    public bool SupportsIncrementalWrites => false;

    /// <inheritdoc />
    public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) { }

    /// <inheritdoc />
    public void ScheduleDeleteEntry(string ns, string entryId) { }

    public PersistenceManager(string? basePath = null, int debounceMs = 500, ILogger<PersistenceManager>? logger = null)
    {
        // Frozen ABSOLUTE at construction: a relative path resolved per operation would move
        // the backing store (and the gate identity) whenever CurrentDirectory changes.
        _basePath = Path.GetFullPath(basePath ?? Path.Combine(AppContext.BaseDirectory, "data"));
        _basePathWithSeparator = _basePath.EndsWith(Path.DirectorySeparatorChar)
            || _basePath.EndsWith(Path.AltDirectorySeparatorChar)
            ? _basePath
            : _basePath + Path.DirectorySeparatorChar;
        _logger = logger;

        // Load-bearing for GetPersistedNamespaces, not merely a convenience for the first write:
        // it is what makes "the data directory is not there" an anomaly rather than an ordinary
        // first run, so that method can refuse instead of reporting an empty store.
        Directory.CreateDirectory(_basePath);

        _coordinator = new DebouncedStorageCoordinator<DefaultEntryStorageKey>(
            TimeSpan.FromMilliseconds(debounceMs),
            supportsIncrementalWrites: false,
            logger: _logger,
            makeKey: static (t, id) => new DefaultEntryStorageKey(t, id),
            getTenantId: static k => k.TenantId,
            writeNamespace: WriteNamespace,
            writeIncremental: null,
            writeEdges: WriteGlobalEdges,
            writeClusters: WriteClusters,
            writeDecayConfigs: WriteDecayConfigs,
            writeHnsw: TrySaveHnswSnapshotSync,
            onDisposedUnderLock: () => _disposed = true);
        _timerLock = _coordinator.TimerLock;

        // LAST, after everything that can throw: a constructor that failed after acquiring
        // would leak a refcount no Dispose will ever release.
        _collapseHistoryGate = AcquireCollapseHistoryGate(StoreIdentity);
    }

    /// <summary>The store's durable identity: the data directory canonicalized by <see cref="StoreIdentityUtil.CanonicalPath"/>.</summary>
    public string StoreIdentity => StoreIdentityUtil.CanonicalPath(_basePath);

    /// <summary>
    /// Load namespace data from disk. Returns empty data if file does not exist, is corrupted,
    /// or fails checksum verification (matching SQLite and SQL Server backends).
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
                _logger?.LogWarning("Checksum mismatch for namespace '{Namespace}'; dropping corrupted payload", ns);
                return new NamespaceData { StorageVersion = CurrentStorageVersion };
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
                data = RunMigrations(data, ns);

            return data;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Corrupted JSON in namespace '{Namespace}', returning empty data", ns);
            return new NamespaceData { StorageVersion = CurrentStorageVersion };
        }
    }

    private NamespaceData RunMigrations(NamespaceData data, string ns)
    {
        int fromVersion = data.StorageVersion;
        data.StorageVersion = CurrentStorageVersion;
        _logger?.LogInformation("Namespace '{Namespace}' migrated from storage v{From} to v{To}", ns, fromVersion, CurrentStorageVersion);
        SaveNamespaceSync(ns, data);
        return data;
    }

    public List<GraphEdge> LoadGlobalEdges()
        => LoadGlobalFile<List<GraphEdge>>(Path.Combine(_basePath, "_edges.json"), "edges") ?? new();

    public List<SemanticCluster> LoadClusters()
        => LoadGlobalFile<List<SemanticCluster>>(Path.Combine(_basePath, "_clusters.json"), "clusters") ?? new();

    /// <summary>
    /// Load collapse history from disk. Envelope-aware and lenient at boot so receipts are not
    /// silently orphaned on load; strict reads (<see cref="TryReadCollapseHistory"/> /
    /// <see cref="TryReadCollapseRecord"/>) and mutations always fail closed on checksum mismatch.
    /// </summary>
    public List<CollapseRecord> LoadCollapseHistory()
    {
        var path = Path.Combine(_basePath, "_collapse_history.json");
        if (!File.Exists(path))
            return new();

        try
        {
            var content = File.ReadAllText(path);
            var trimmed = content.TrimStart();
            if (trimmed.StartsWith('['))
                return DeserializeCollapseHistoryLenient(content);

            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("records", out var recordsEl))
            {
                _logger?.LogWarning("Collapse-history file has an unrecognized shape; returning empty data");
                return new();
            }
            if (doc.RootElement.TryGetProperty("checksum", out var checksumEl))
            {
                var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recordsEl.GetRawText())));
                if (!string.Equals(actual, checksumEl.GetString(), StringComparison.OrdinalIgnoreCase))
                    _logger?.LogWarning("Checksum mismatch for collapse history file, data may be corrupted");
            }
            return DeserializeCollapseHistoryLenient(recordsEl.GetRawText());
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Corrupted JSON in collapse history file, returning empty data");
            return new();
        }
    }

    private List<CollapseRecord> DeserializeCollapseHistoryLenient(string json)
    {
        var set = CollapseRecordShape.DeserializeLenient(json, JsonOptions, out var dropped);
        if (dropped > 0)
            _logger?.LogWarning(
                "Collapse-history boot load dropped {Dropped} malformed, duplicate, or forward-unknown " +
                "record row(s); this store holds data this version cannot safely act on and should be repaired.",
                dropped);
        return set;
    }

    public Dictionary<string, DecayConfig> LoadDecayConfigs()
    {
        var list = LoadGlobalFile<List<DecayConfig>>(Path.Combine(_basePath, "_decay_configs.json"), "decay configs");
        return NamespaceStore.DecayConfigsByPartition(list, _logger);
    }

    private T? LoadGlobalFile<T>(string path, string label) where T : class
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            if (!VerifyChecksum(path, json))
            {
                _logger?.LogWarning("Checksum mismatch for {Label} file; dropping corrupted payload", label);
                return null;
            }
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Corrupted JSON in {Label} file, returning empty data", label);
            return null;
        }
    }

    // ── Debounced Scheduling (delegated to DebouncedStorageCoordinator) ──

    public void ScheduleSave(string ns, Func<NamespaceData> dataProvider)
        => _coordinator.ScheduleSave(ns, dataProvider);

    public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider)
        => _coordinator.ScheduleSaveGlobalEdges(dataProvider);

    public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider)
        => _coordinator.ScheduleSaveClusters(dataProvider);

    public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider)
        => _coordinator.ScheduleSaveDecayConfigs(dataProvider);

    public void ScheduleSaveHnswSnapshot(string ns, Func<HnswSnapshot?> snapshotProvider)
        => _coordinator.ScheduleHnswSave(ns, snapshotProvider);

    // ── Collapse history synchronous CAS RMW ──

    private static readonly object s_collapseHistoryGateTableLock = new();
    private static readonly Dictionary<string, (object Gate, int Holders)> s_collapseHistoryGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _collapseHistoryGate;

    private static object AcquireCollapseHistoryGate(string storeKey)
    {
        lock (s_collapseHistoryGateTableLock)
        {
            if (s_collapseHistoryGates.TryGetValue(storeKey, out var slot))
            {
                s_collapseHistoryGates[storeKey] = (slot.Gate, slot.Holders + 1);
                return slot.Gate;
            }
            var gate = new object();
            s_collapseHistoryGates[storeKey] = (gate, 1);
            return gate;
        }
    }

    private static void ReleaseCollapseHistoryGate(string storeKey)
    {
        lock (s_collapseHistoryGateTableLock)
        {
            if (!s_collapseHistoryGates.TryGetValue(storeKey, out var slot))
                return;
            if (slot.Holders <= 1) s_collapseHistoryGates.Remove(storeKey);
            else s_collapseHistoryGates[storeKey] = (slot.Gate, slot.Holders - 1);
        }
    }

    private T WithInterprocessHistoryLock<T>(T failValue, Func<T> body)
    {
        var lockPath = Path.Combine(_basePath, "_collapse_history.lock");
        const int attempts = 120;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            FileStream? stream = null;
            try
            {
                stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(25);
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(25);
                continue;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Interprocess collapse-history lock could not be opened");
                return failValue;
            }

            try { return body(); }
            finally { stream.Dispose(); }
        }

        _logger?.LogError("Interprocess collapse-history lock could not be acquired within the timeout; operation refused");
        return failValue;
    }

    public bool UpsertCollapseRecordSync(CollapseRecord record)
    {
        if (CollapseRecordShape.Describe(record) is { } defect)
        {
            _logger?.LogError("Collapse-history upsert refused: {Defect}", defect);
            return false;
        }
        return MutateCollapseHistorySync(records =>
        {
            records.RemoveAll(r => r.CollapseId == record.CollapseId);
            records.Add(record);
            return true;
        });
    }

    public bool DeleteCollapseRecordSync(string collapseId)
        => MutateCollapseHistorySync(records => records.RemoveAll(r => r.CollapseId == collapseId) > 0);

    public CollapseRecordCas DeleteCollapseRecordSync(string collapseId, long onlyIfGeneration)
    {
        var outcome = CollapseRecordCas.StoreFailed;
        bool storeAgrees = MutateCollapseHistorySync(records =>
        {
            var current = records.FirstOrDefault(r => r.CollapseId == collapseId);
            if (current is null) { outcome = CollapseRecordCas.AlreadyAbsent; return false; }
            if (current.Generation != onlyIfGeneration) { outcome = CollapseRecordCas.GenerationMoved; return false; }
            records.RemoveAll(r => r.CollapseId == collapseId);
            outcome = CollapseRecordCas.Applied;
            return true;
        });
        return storeAgrees ? outcome : CollapseRecordCas.StoreFailed;
    }

    public bool TryReadCollapseRecord(string collapseId, out CollapseRecord? record)
    {
        record = null;
        if (!TryReadCollapseHistory(out var records))
            return false;
        record = records.FirstOrDefault(r => r.CollapseId == collapseId);
        return true;
    }

    public bool TryReadCollapseHistory(out List<CollapseRecord> records)
    {
        List<CollapseRecord> found = new();
        bool ok;
        lock (_collapseHistoryGate)
        {
            ok = WithInterprocessHistoryLock(false, () =>
            {
                var path = Path.Combine(_basePath, "_collapse_history.json");
                return TryReadHistorySet(path, out found);
            });
        }
        records = found;
        return ok;
    }

    private bool MutateCollapseHistorySync(Func<List<CollapseRecord>, bool> mutate)
    {
        if (!_coordinator.TryBeginTrackedExternalWrite())
            return false;

        bool committed = false;
        try
        {
            lock (_collapseHistoryGate)
            {
                committed = WithInterprocessHistoryLock(false, () =>
                {
                    var path = Path.Combine(_basePath, "_collapse_history.json");
                    if (!TryReadHistorySet(path, out var records))
                        return false;

                    return !mutate(records) || TryCommitCollapseHistory(records, path);
                });
            }
        }
        finally
        {
            _coordinator.EndTrackedWrite();
        }
        return committed;
    }

    private bool TryReadHistorySet(string path, out List<CollapseRecord> records)
    {
        records = new List<CollapseRecord>();
        try
        {
            if (!File.Exists(path)) return true;
            var content = File.ReadAllText(path);
            var trimmed = content.TrimStart();

            if (trimmed.StartsWith('['))
            {
                if (!VerifyChecksum(path, content))
                {
                    _logger?.LogError("Collapse-history read refused: pre-envelope content does not match its checksum companion");
                    return false;
                }
                if (!CollapseRecordShape.TryDeserializeStrict(
                        content, JsonOptions, out records, out var defect))
                {
                    _logger?.LogError("Collapse-history read refused: {Defect}", defect);
                    records = new List<CollapseRecord>();
                    return false;
                }
                return true;
            }

            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("checksum", out var checksumEl)
                || !doc.RootElement.TryGetProperty("records", out var recordsEl))
            {
                _logger?.LogError("Collapse-history read refused: unrecognized envelope shape");
                return false;
            }

            var recordsRaw = recordsEl.GetRawText();
            var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recordsRaw)));
            if (!string.Equals(actual, checksumEl.GetString(), StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogError("Collapse-history read refused: records do not match the envelope checksum");
                return false;
            }

            if (!CollapseRecordShape.TryDeserializeStrict(
                    recordsRaw, JsonOptions, out records, out var envelopeDefect))
            {
                _logger?.LogError("Collapse-history read refused: {Defect}", envelopeDefect);
                records = new List<CollapseRecord>();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Collapse-history read refused: the current set could not be read");
            return false;
        }
    }

    public CollapseRecordCas UpsertCollapseRecordSync(CollapseRecord record, long? onlyIfGeneration)
    {
        if (CollapseRecordShape.Describe(record) is { } defect)
        {
            _logger?.LogError("Conditional collapse-history upsert refused: {Defect}", defect);
            return CollapseRecordCas.StoreFailed;
        }
        var outcome = CollapseRecordCas.StoreFailed;
        bool storeAgrees = MutateCollapseHistorySync(records =>
        {
            var current = records.FirstOrDefault(r => r.CollapseId == record.CollapseId);
            if (current is null && onlyIfGeneration is not null) { outcome = CollapseRecordCas.AlreadyAbsent; return false; }
            if (current is not null && (onlyIfGeneration is null || current.Generation != onlyIfGeneration.Value)) { outcome = CollapseRecordCas.GenerationMoved; return false; }
            records.RemoveAll(r => r.CollapseId == record.CollapseId);
            records.Add(record);
            outcome = CollapseRecordCas.Applied;
            return true;
        });
        return storeAgrees ? outcome : CollapseRecordCas.StoreFailed;
    }

    private bool TryCommitCollapseHistory(List<CollapseRecord> records, string path)
    {
        string json;
        try
        {
            var recordsJson = JsonSerializer.Serialize(records, JsonOptions);
            var recordsChecksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recordsJson)));
            json = $"{{\"checksum\":\"{recordsChecksum}\",\"records\":{recordsJson}}}";
        }
        catch (Exception ex) { _logger?.LogError(ex, "Collapse-history serialize failed"); return false; }

        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteDurableTempFile(tmp, Encoding.UTF8.GetBytes(json));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Collapse-history temp write failed");
            try { File.Delete(tmp); } catch { /* best effort */ }
            return false;
        }

        try
        {
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Collapse-history commit failed; previous file left intact");
            try { File.Delete(tmp); } catch { /* best effort */ }
            return false;
        }

        try { File.Delete(path + ".sha256"); }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Stale collapse-history checksum companion could not be removed; the commit stands");
        }

        return true;
    }

    // ── Sync saves, enumeration, flush, delete, dispose ──

    public void SaveNamespaceSync(string ns, NamespaceData data)
    {
        data.StorageVersion = CurrentStorageVersion;
        var path = GetNamespacePath(ns);
        var json = JsonSerializer.Serialize(data, JsonOptions);
        AtomicWriteAllText(path, json);
        _coordinator.ResetNamespaceFailure(ns);
    }

    public IReadOnlyList<string> GetPersistedNamespaces()
    {
        try
        {
            var results = new List<string>();
            foreach (var file in Directory.GetFiles(_basePath, "*.json"))
            {
                if (file.EndsWith(".hnsw.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                var stem = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(stem))
                    continue;

                string? decoded = DecodeNamespaceStem(stem, file);
                if (string.IsNullOrEmpty(decoded) || decoded.StartsWith('_'))
                    continue;
                results.Add(decoded);
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error listing namespace files under '{BasePath}'", _basePath);
            throw new NamespaceEnumerationException(ex);
        }
    }

    private string? DecodeNamespaceStem(string stem, string filePath)
    {
        if (stem.StartsWith(EncodedNamespaceHashPrefix, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("entries", out var entriesEl)
                    && entriesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entryEl in entriesEl.EnumerateArray())
                    {
                        if (entryEl.ValueKind == JsonValueKind.Object
                            && entryEl.TryGetProperty("ns", out var nsEl)
                            && nsEl.ValueKind == JsonValueKind.String)
                        {
                            var name = nsEl.GetString();
                            if (!string.IsNullOrEmpty(name))
                                return name;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Could not resolve hashed namespace file '{Stem}'", stem);
            }
            return null;
        }

        if (stem.StartsWith(EncodedNamespacePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var hex = stem.Substring(EncodedNamespacePrefix.Length);
            if (hex.Length > 0 && hex.Length % 2 == 0)
            {
                try { return Encoding.UTF8.GetString(Convert.FromHexString(hex)); }
                catch (FormatException) { return null; }
            }
            return null;
        }

        return stem;
    }

    public void Flush() => TryFlush();

    public bool TryFlush() => _coordinator.TryFlush();

    public HnswSnapshot? LoadHnswSnapshot(string ns)
    {
        var path = GetHnswPath(ns);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            if (!VerifyChecksum(path, json))
            {
                _logger?.LogWarning("Checksum mismatch for HNSW snapshot '{Namespace}'; dropping and rebuilding", ns);
                return null;
            }
            return JsonSerializer.Deserialize<HnswSnapshot>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Corrupted HNSW snapshot for namespace '{Namespace}', will rebuild", ns);
            return null;
        }
    }

    public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot)
    {
        var path = GetHnswPath(ns);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        AtomicWriteAllText(path, json);
    }

    private bool TrySaveHnswSnapshotSync(string ns, HnswSnapshot snapshot)
    {
        try
        {
            SaveHnswSnapshotSync(ns, snapshot);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save HNSW snapshot for '{Namespace}'", ns);
            return false;
        }
    }

    public void DeleteHnswSnapshot(string ns)
    {
        _coordinator.FlushGate.Wait();
        try
        {
            DeleteHnswSnapshotUnlockedByGate(ns);
        }
        finally
        {
            _coordinator.FlushGate.Release();
        }
    }

    private void DeleteHnswSnapshotUnlockedByGate(string ns)
    {
        _coordinator.CancelHnswSave(ns);
        var path = GetHnswPath(ns);
        if (File.Exists(path)) File.Delete(path);
        var checksumPath = path + ".sha256";
        if (File.Exists(checksumPath)) File.Delete(checksumPath);
    }

    public Task DeleteNamespaceAsync(string ns)
    {
        _coordinator.FlushGate.Wait();
        try
        {
            DeleteNamespaceCore(ns);
        }
        finally
        {
            _coordinator.FlushGate.Release();
        }
        return Task.CompletedTask;
    }

    private void DeleteNamespaceCore(string ns)
    {
        _coordinator.CancelAllPendingForNamespaceUnlockedByGate(ns);

        var path = GetNamespacePath(ns);
        if (File.Exists(path)) File.Delete(path);
        var checksumPath = path + ".sha256";
        if (File.Exists(checksumPath)) File.Delete(checksumPath);
        DeleteHnswSnapshotUnlockedByGate(ns);
    }

    public Task DeleteNamespaceAsync(string ns, string tenantId)
    {
        tenantId = string.IsNullOrWhiteSpace(tenantId) ? string.Empty : tenantId.Trim();
        _coordinator.FlushGate.Wait();
        try
        {
            _coordinator.CancelTenantPendingForNamespaceUnlockedByGate(ns, tenantId, out var pendingProvider);

            var diskData = LoadNamespace(ns);
            var remaining = diskData.Entries.Where(entry => entry.TenantId != tenantId).ToList();

            if (pendingProvider is not null
                && StorageSnapshot.TryCapture(pendingProvider, _logger, out var pendingData)
                && pendingData is not null)
            {
                var pendingOtherTenants = pendingData.Entries
                    .Where(entry => entry.TenantId != tenantId)
                    .ToList();
                if (pendingOtherTenants.Count > 0)
                {
                    var pendingTenants = new HashSet<string>(
                        pendingOtherTenants.Select(e => e.TenantId), StringComparer.Ordinal);
                    remaining.RemoveAll(e => pendingTenants.Contains(e.TenantId));
                    remaining.AddRange(pendingOtherTenants);
                }
            }

            if (remaining.Count == 0)
            {
                DeleteHnswSnapshotUnlockedByGate(NamespaceStore.PartitionKey(tenantId, ns));
                DeleteNamespaceCore(ns);
                return Task.CompletedTask;
            }

            diskData.Entries = remaining;
            SaveNamespaceSync(ns, diskData);
            DeleteHnswSnapshotUnlockedByGate(NamespaceStore.PartitionKey(tenantId, ns));
        }
        finally
        {
            _coordinator.FlushGate.Release();
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        ReleaseCollapseHistoryGate(StoreIdentity);
    }

    // ── Path resolution & security ──

    private static bool IsWindowsReservedDeviceName(string value)
    {
        int dot = value.IndexOf('.');
        var head = dot >= 0 ? value.Substring(0, dot) : value;
        return WindowsReservedDeviceNames.Contains(head);
    }

    private static bool IsUnsafeFilenameComponent(string value)
    {
        if (string.IsNullOrEmpty(value))
            return true;
        if (value == "." || value.EndsWith('.') || value.Contains("..", StringComparison.Ordinal))
            return true;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsControl(c)
                || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
                || InvalidFileNameChars.Contains(c))
            {
                return true;
            }
        }
        return IsWindowsReservedDeviceName(value);
    }

    private static bool RequiresNamespaceEncoding(string ns)
    {
        return IsUnsafeFilenameComponent(ns)
            || ns.StartsWith(EncodedNamespacePrefix, StringComparison.OrdinalIgnoreCase)
            || ns.EndsWith(".hnsw", StringComparison.OrdinalIgnoreCase)
            || ReservedControlPlaneStems.Contains(ns);
    }

    private void EnsureWithinBasePath(string path, string ns)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!Path.GetFullPath(path).StartsWith(_basePathWithSeparator, comparison))
            throw new ArgumentException($"Invalid namespace: '{ns}'");
    }

    internal string GetNamespacePath(string ns)
    {
        if (string.IsNullOrEmpty(ns))
            throw new ArgumentException("Namespace must not be empty.", nameof(ns));

        string stem;
        if (!RequiresNamespaceEncoding(ns))
        {
            stem = ns;
        }
        else
        {
            var utf8 = Encoding.UTF8.GetBytes(ns);
            stem = utf8.Length <= MaxReversibleHexUtf8Bytes
                ? EncodedNamespacePrefix + Convert.ToHexString(utf8).ToLowerInvariant()
                : EncodedNamespaceHashPrefix + Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant();
        }

        var path = Path.Combine(_basePath, $"{stem}.json");
        EnsureWithinBasePath(path, ns);
        return path;
    }

    internal string GetHnswPath(string ns)
    {
        if (string.IsNullOrEmpty(ns))
            throw new ArgumentException("Namespace must not be empty.", nameof(ns));

        string stem;
        if (ns.IndexOf(Tenancy.PartitionSeparator) >= 0)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ns))).ToLowerInvariant();
            stem = TenantHnswPrefix + hash;
        }
        else if (IsUnsafeFilenameComponent(ns)
                 || ns.StartsWith(TenantHnswPrefix, StringComparison.OrdinalIgnoreCase)
                 || ns.StartsWith(EncodedHnswPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ns))).ToLowerInvariant();
            stem = EncodedHnswPrefix + hash;
        }
        else
        {
            stem = ns;
        }

        var path = Path.Combine(_basePath, $"{stem}.hnsw.json");
        EnsureWithinBasePath(path, ns);
        return path;
    }

    // ── Backend writers & atomic I/O ──

    private bool WriteNamespace(string ns, Func<NamespaceData> provider)
    {
        try
        {
            var data = provider();
            SaveNamespaceSync(ns, data);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save namespace '{Namespace}'", ns);
            return false;
        }
    }

    private bool WriteGlobalEdges(List<GraphEdge> edges)
    {
        try
        {
            OnBeforeGlobalWrite?.Invoke("edges");
            var json = JsonSerializer.Serialize(edges, JsonOptions);
            AtomicWriteAllText(Path.Combine(_basePath, "_edges.json"), json);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save global edges");
            return false;
        }
    }

    private bool WriteClusters(List<SemanticCluster> clusters)
    {
        try
        {
            OnBeforeGlobalWrite?.Invoke("clusters");
            var json = JsonSerializer.Serialize(clusters, JsonOptions);
            AtomicWriteAllText(Path.Combine(_basePath, "_clusters.json"), json);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save clusters");
            return false;
        }
    }

    private bool WriteDecayConfigs(Dictionary<string, DecayConfig> configs)
    {
        try
        {
            OnBeforeGlobalWrite?.Invoke("decay_configs");
            var json = JsonSerializer.Serialize(configs.Values.ToList(), JsonOptions);
            AtomicWriteAllText(Path.Combine(_basePath, "_decay_configs.json"), json);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save decay configs");
            return false;
        }
    }

    /// <summary>
    /// Write payload and companion .sha256 atomically using unique flushed temp files and atomic moves
    /// so a crash or concurrent reader never observes a truncated payload or half-written checksum file.
    /// </summary>
    private static void AtomicWriteAllText(string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        AtomicWriteBytes(path, bytes);

        var hashBytes = Encoding.UTF8.GetBytes(Convert.ToHexString(SHA256.HashData(bytes)));
        AtomicWriteBytes(path + ".sha256", hashBytes);
    }

    private static void AtomicWriteBytes(string path, byte[] bytes)
    {
        var tmpPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteDurableTempFile(tmpPath, bytes);
            File.Move(tmpPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best effort */ }
            throw;
        }
    }

    private static void WriteDurableTempFile(string tmpPath, byte[] bytes)
    {
        using var stream = new FileStream(
            tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            16 * 1024, FileOptions.None);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

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
            _logger?.LogWarning(ex, "Checksum file for '{Path}' exists but could not be read; verification fails closed", path);
            return false;
        }
    }
}
