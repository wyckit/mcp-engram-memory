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
    private const string MigrationManifestStem = "_engram-migration";
    private const int MaxReversibleHexUtf8Bytes = 96;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly HashSet<char> InvalidFileNameChars = new(Path.GetInvalidFileNameChars());
    private static readonly HashSet<char> WindowsInvalidFileNameChars = BuildWindowsInvalidFileNameChars();
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

    private static HashSet<char> BuildWindowsInvalidFileNameChars()
    {
        var set = new HashSet<char> { '"', '<', '>', '|', ':', '*', '?', '\\', '/' };
        for (int c = 0; c < 32; c++)
            set.Add((char)c);
        return set;
    }

    private readonly string _basePath;
    private readonly string _basePathWithSeparator;
    private readonly ILogger<PersistenceManager>? _logger;
    private readonly DebouncedStorageCoordinator<DefaultEntryStorageKey> _coordinator;
    private readonly object _legacyMigrationLock = new();
    private volatile bool _legacyMigrationIncomplete;
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

    // Internal test seams for deterministic durability & migration regression tests.
    internal Action<string>? OnBeforeGlobalWrite { get; set; }
    internal Action<string>? OnAfterPayloadBeforeChecksumWrite { get; set; }
    internal Action<string, string>? OnAfterLegacyMigrationTargetCommit { get; set; }

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

        // Migrate any unambiguous 2.0.x/2.1.x legacy namespace filenames (e.g., ns_enc_notes.json)
        // to their canonical encoded filenames before serving reads or enumerations.
        MigrateLegacyNamespaceFiles();

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
    /// Load namespace data from disk. Recovers the last verified backup snapshot if the primary
    /// payload/checksum pair was torn by an interrupted write; returns empty data only if the file
    /// does not exist or neither primary nor backup verifies cleanly (matching SQLite/SQL Server).
    /// </summary>
    public NamespaceData LoadNamespace(string ns)
    {
        var path = GetNamespacePath(ns);
        if (!File.Exists(path))
        {
            if (_legacyMigrationIncomplete || HasPotentialLegacyFileFor(ns))
                MigrateLegacyNamespaceFiles();
            if (!File.Exists(path))
                return new NamespaceData { StorageVersion = CurrentStorageVersion };
        }

        var loaded = LoadVerifiedPayload<NamespaceData>(path, $"namespace '{ns}'", out bool fromPrimary);
        if (loaded is null)
            return new NamespaceData { StorageVersion = CurrentStorageVersion };

        // Forward-compatibility guard: reject files from newer versions without falling back.
        if (loaded.StorageVersion > CurrentStorageVersion)
        {
            _logger?.LogError(
                "Namespace '{Namespace}' has storage version {FileVersion} but this server supports up to version {CurrentVersion}. " +
                "Upgrade the server or use the version that created this data.",
                ns, loaded.StorageVersion, CurrentStorageVersion);
            return new NamespaceData { StorageVersion = CurrentStorageVersion };
        }

        // Run migrations for older versions only when loaded from the primary file.
        if (fromPrimary && loaded.StorageVersion < CurrentStorageVersion)
            loaded = RunMigrations(loaded, ns);

        return loaded;
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

        return LoadVerifiedPayload<T>(path, $"{label} file", out _);
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
        if (_legacyMigrationIncomplete && Directory.Exists(_basePath))
            MigrateLegacyNamespaceFiles();

        try
        {
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
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
                if (seen.Add(decoded))
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
                if (TryInspectVerifiedNamespaceFile(filePath, out var data, out var ownerNs, out bool ambiguous)
                    && !ambiguous
                    && !string.IsNullOrEmpty(ownerNs)
                    && SamePath(GetNamespacePath(ownerNs), filePath))
                {
                    return ownerNs;
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
            if (TryDecodeCanonicalHexNamespace(hex, filePath, out var decoded))
                return decoded;
            return null;
        }

        return stem;
    }

    private bool TryDecodeCanonicalHexNamespace(string hex, string filePath, out string? decoded)
    {
        decoded = null;
        if (hex.Length == 0 || hex.Length % 2 != 0 || hex.Length > MaxReversibleHexUtf8Bytes * 2)
            return false;

        try
        {
            var candidate = StrictUtf8.GetString(Convert.FromHexString(hex));
            if (string.IsNullOrEmpty(candidate) || !RequiresNamespaceEncoding(candidate))
                return false;

            // Round-trip check ensures legacy ns_enc_<validhex>.json files are never misclassified
            // as encoded namespaces for unrelated names.
            if (!SamePath(GetNamespacePath(candidate), filePath))
                return false;

            decoded = candidate;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException or ArgumentException)
        {
            return false;
        }
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
            var rawBytes = File.ReadAllBytes(path);
            if (!VerifyChecksumBytes(path, rawBytes))
            {
                _logger?.LogWarning("Checksum mismatch for HNSW snapshot '{Namespace}'; dropping and rebuilding", ns);
                return null;
            }
            return JsonSerializer.Deserialize<HnswSnapshot>(DecodeUtf8(rawBytes), JsonOptions);
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
        AtomicWriteAllText(path, json, keepBackup: false);
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
        var backupPath = path + ".bak";
        if (File.Exists(backupPath)) File.Delete(backupPath);
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
        // Remove backup first so a crash mid-delete can never resurrect a deleted namespace.
        var backupPath = path + ".bak";
        if (File.Exists(backupPath)) File.Delete(backupPath);
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
            // Refresh backup immediately after committing the filtered snapshot so deleted
            // tenant entries are never retained on disk or resurrected by a later torn write.
            BackupVerifiedPrimaryIfPresent(GetNamespacePath(ns));
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

    private bool IsWithinBasePath(string path)
        => Path.GetFullPath(path).StartsWith(_basePathWithSeparator, PathComparison);

    private static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), PathComparison);

    private void EnsureWithinBasePath(string path, string ns)
    {
        if (!IsWithinBasePath(path))
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

    // ── Legacy namespace filename migration (2.0.x / 2.1.0 -> hardened canonical filenames) ──

    private sealed record LegacyMigrationCandidate(
        string SourcePath,
        string OwnerNs,
        string TargetPath,
        NamespaceData Data);

    /// <summary>
    /// Safely migrates verified legacy namespace files (such as <c>ns_enc_notes.json</c> or
    /// 2.0/2.1 sanitized filenames) to their canonical encoded filenames. Verifies ownership
    /// from entry payloads, preserves originals on any ambiguity or target conflict, and can
    /// safely resume after an interruption between committing the target and removing the source.
    /// </summary>
    internal void MigrateLegacyNamespaceFiles()
    {
        lock (_legacyMigrationLock)
        {
            if (!Directory.Exists(_basePath))
                return;

            string[] files;
            try
            {
                files = Directory.GetFiles(_basePath, "*.json");
                Array.Sort(files, StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _legacyMigrationIncomplete = true;
                _logger?.LogWarning(ex, "Could not scan '{BasePath}' for legacy namespace filename migration", _basePath);
                return;
            }

            bool incomplete = false;
            var candidates = new List<LegacyMigrationCandidate>();

            foreach (var filePath in files)
            {
                var fileName = Path.GetFileName(filePath);
                string stem;

                if (fileName.EndsWith(".hnsw.json", StringComparison.OrdinalIgnoreCase))
                {
                    // Fast header peek avoids parsing multi-MB HNSW caches while still catching
                    // rare legacy namespaces literally named "*.hnsw".
                    if (!LooksLikeNamespaceDataHeader(filePath))
                        continue;
                    stem = fileName.Substring(0, fileName.Length - ".json".Length);
                }
                else
                {
                    stem = Path.GetFileNameWithoutExtension(filePath);
                    if (string.IsNullOrEmpty(stem)
                        || ReservedControlPlaneStems.Contains(stem)
                        || string.Equals(stem, MigrationManifestStem, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // A 2.0/2.1 legacy filename can only differ from the hardened canonical filename
                    // if the stem requires encoding itself or contains '_' from legacy sanitization.
                    if (!RequiresNamespaceEncoding(stem) && !stem.Contains('_'))
                        continue;
                }

                try
                {
                    if (!TryInspectVerifiedNamespaceFile(filePath, out var data, out var ownerNs, out bool ambiguous))
                    {
                        // Unreadable, corrupted, non-NamespaceData, or future-version file: preserve untouched.
                        continue;
                    }

                    if (ambiguous || data is null || string.IsNullOrEmpty(ownerNs))
                    {
                        if (RequiresNamespaceEncoding(stem) && !IsAlreadyCanonicalEncodedStem(stem, filePath))
                        {
                            _logger?.LogWarning(
                                "Preserving legacy namespace file '{File}' untouched: ownership could not be unambiguously verified from entries.",
                                fileName);
                        }
                        continue;
                    }

                    if (ReservedControlPlaneStems.Contains(ownerNs))
                        continue;

                    var targetPath = GetNamespacePath(ownerNs);
                    if (SamePath(filePath, targetPath))
                        continue;

                    if (!IsValidLegacyStemForNamespace(ownerNs, stem))
                    {
                        _logger?.LogWarning(
                            "Preserving namespace file '{File}' untouched: entries belong to '{Namespace}' which does not match legacy filename sanitization.",
                            fileName, ownerNs);
                        continue;
                    }

                    candidates.Add(new LegacyMigrationCandidate(filePath, ownerNs, targetPath, data));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    incomplete = true;
                    _logger?.LogWarning(ex, "Transient error inspecting legacy namespace file '{File}'; will retry", fileName);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Preserving legacy namespace file '{File}' untouched after inspection error", fileName);
                }
            }

            // Detect cross-file ambiguity before writing anything: if two legacy files claim the
            // same canonical target, preserve all of them untouched.
            var groupedByTarget = new Dictionary<string, List<LegacyMigrationCandidate>>(PathComparer);
            foreach (var candidate in candidates)
            {
                if (!groupedByTarget.TryGetValue(candidate.TargetPath, out var group))
                {
                    group = new List<LegacyMigrationCandidate>();
                    groupedByTarget[candidate.TargetPath] = group;
                }
                group.Add(candidate);
            }

            foreach (var (_, group) in groupedByTarget)
            {
                if (group.Count > 1)
                {
                    _logger?.LogWarning(
                        "Preserving {Count} ambiguous legacy namespace files claiming namespace '{Namespace}': {Files}",
                        group.Count,
                        group[0].OwnerNs,
                        string.Join(", ", group.Select(c => Path.GetFileName(c.SourcePath))));
                    continue;
                }

                var candidate = group[0];
                if (!TryMigrateSingleLegacyFile(candidate))
                    incomplete = true;
            }

            _legacyMigrationIncomplete = incomplete;
        }
    }

    private bool TryMigrateSingleLegacyFile(LegacyMigrationCandidate candidate)
    {
        try
        {
            var normalizedData = new NamespaceData
            {
                StorageVersion = CurrentStorageVersion,
                Entries = candidate.Data.Entries
            };
            var canonicalJson = JsonSerializer.Serialize(normalizedData, JsonOptions);

            if (File.Exists(candidate.TargetPath))
            {
                // Resumability check: if a prior run committed the canonical target and stopped
                // before removing the legacy source, both files contain the exact same entries.
                if (!TryInspectVerifiedNamespaceFile(
                        candidate.TargetPath, out var existingTarget, out var targetOwnerNs, out bool targetAmbiguous)
                    || targetAmbiguous
                    || existingTarget is null
                    || !string.Equals(targetOwnerNs, candidate.OwnerNs, StringComparison.Ordinal))
                {
                    _logger?.LogWarning(
                        "Preserving legacy namespace file '{Source}' untouched: canonical target '{Target}' already exists and could not be verified as an identical migration.",
                        Path.GetFileName(candidate.SourcePath), Path.GetFileName(candidate.TargetPath));
                    return true;
                }

                var existingNormalizedJson = JsonSerializer.Serialize(new NamespaceData
                {
                    StorageVersion = CurrentStorageVersion,
                    Entries = existingTarget.Entries
                }, JsonOptions);

                if (!string.Equals(canonicalJson, existingNormalizedJson, StringComparison.Ordinal))
                {
                    _logger?.LogWarning(
                        "Preserving legacy namespace file '{Source}' untouched: canonical target '{Target}' already exists with different content.",
                        Path.GetFileName(candidate.SourcePath), Path.GetFileName(candidate.TargetPath));
                    return true;
                }

                // If the previous run stopped between writing target payload and its .sha256,
                // complete the checksum commit before removing the legacy original.
                var targetChecksumPath = candidate.TargetPath + ".sha256";
                if (!File.Exists(targetChecksumPath)
                    || !VerifyChecksumBytes(candidate.TargetPath, File.ReadAllBytes(candidate.TargetPath)))
                {
                    AtomicWriteAllText(candidate.TargetPath, canonicalJson, keepBackup: true);
                }
            }
            else
            {
                AtomicWriteAllText(candidate.TargetPath, canonicalJson, keepBackup: true);

                // Verify the newly committed canonical file + checksum before touching the original.
                var committedBytes = File.ReadAllBytes(candidate.TargetPath);
                if (!File.Exists(candidate.TargetPath + ".sha256")
                    || !VerifyChecksumBytes(candidate.TargetPath, committedBytes))
                {
                    _logger?.LogWarning(
                        "Legacy migration verification failed for '{Source}'; original file preserved.",
                        Path.GetFileName(candidate.SourcePath));
                    return false;
                }

                OnAfterLegacyMigrationTargetCommit?.Invoke(candidate.SourcePath, candidate.TargetPath);
            }

            // Target is durably committed and verified; remove legacy source files.
            // Delete backup first, then primary, then checksum so partial deletion stays safe.
            TryDeleteFileStrict(candidate.SourcePath + ".bak");
            TryDeleteFileStrict(candidate.SourcePath);
            TryDeleteFileStrict(candidate.SourcePath + ".sha256");

            _logger?.LogInformation(
                "Migrated legacy namespace file '{Source}' to '{Target}' for namespace '{Namespace}'.",
                Path.GetFileName(candidate.SourcePath),
                Path.GetFileName(candidate.TargetPath),
                candidate.OwnerNs);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Legacy namespace migration interrupted for '{Source}'; original preserved for safe resume.",
                Path.GetFileName(candidate.SourcePath));
            return false;
        }
    }

    private static void TryDeleteFileStrict(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private bool HasPotentialLegacyFileFor(string ns)
    {
        if (string.IsNullOrEmpty(ns) || !RequiresNamespaceEncoding(ns))
            return false;

        var canonicalPath = GetNamespacePath(ns);
        foreach (var candidateStem in EnumerateLegacyCandidateStems(ns))
        {
            if (string.IsNullOrEmpty(candidateStem)
                || ReservedControlPlaneStems.Contains(candidateStem)
                || string.Equals(candidateStem, MigrationManifestStem, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (candidateStem.IndexOfAny(['/', '\\', '\0']) >= 0
                || candidateStem == "."
                || candidateStem.Contains("..", StringComparison.Ordinal))
            {
                continue;
            }

            string candidatePath;
            try
            {
                candidatePath = Path.Combine(_basePath, $"{candidateStem}.json");
                if (!IsWithinBasePath(candidatePath) || SamePath(candidatePath, canonicalPath))
                    continue;
            }
            catch
            {
                continue;
            }

            if (File.Exists(candidatePath))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateLegacyCandidateStems(string ns)
    {
        yield return ns;
        yield return LegacySanitizeLinux(ns);
        yield return LegacySanitizeWindows(ns);
        yield return LegacySanitizeCurrentOs(ns);
    }

    private static bool IsValidLegacyStemForNamespace(string ns, string stem)
    {
        return string.Equals(ns, stem, PathComparison)
            || string.Equals(LegacySanitizeLinux(ns), stem, PathComparison)
            || string.Equals(LegacySanitizeWindows(ns), stem, PathComparison)
            || string.Equals(LegacySanitizeCurrentOs(ns), stem, PathComparison);
    }

    private static string LegacySanitizeLinux(string ns)
        => ns.Replace('\0', '_').Replace('/', '_').Replace("..", "_");

    private static string LegacySanitizeWindows(string ns)
    {
        var sb = new StringBuilder(ns.Length);
        for (int i = 0; i < ns.Length; i++)
        {
            char c = ns[i];
            sb.Append(WindowsInvalidFileNameChars.Contains(c) ? '_' : c);
        }
        return sb.ToString().Replace("..", "_");
    }

    private static string LegacySanitizeCurrentOs(string ns)
        => string.Join("_", ns.Split(Path.GetInvalidFileNameChars())).Replace("..", "_");

    private bool IsAlreadyCanonicalEncodedStem(string stem, string filePath)
    {
        if (stem.StartsWith(EncodedNamespaceHashPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        if (stem.StartsWith(EncodedNamespacePrefix, StringComparison.OrdinalIgnoreCase))
            return TryDecodeCanonicalHexNamespace(stem.Substring(EncodedNamespacePrefix.Length), filePath, out _);
        return false;
    }

    private static bool LooksLikeNamespaceDataHeader(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> buffer = stackalloc byte[256];
            int read = stream.Read(buffer);
            if (read <= 0)
                return false;
            var snippet = Encoding.UTF8.GetString(buffer.Slice(0, read));
            if (snippet.Contains("\"nodeIds\"", StringComparison.Ordinal)
                || snippet.Contains("\"efConstruction\"", StringComparison.Ordinal))
            {
                return false;
            }
            return snippet.Contains("\"entries\"", StringComparison.Ordinal)
                || snippet.Contains("\"storageVersion\"", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private bool TryInspectVerifiedNamespaceFile(
        string filePath,
        out NamespaceData? data,
        out string? ownerNs,
        out bool ambiguous)
    {
        data = null;
        ownerNs = null;
        ambiguous = false;

        if (!TryReadVerifiedJson(filePath, Path.GetFileName(filePath), out var json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("entries", out var entriesEl)
                || entriesEl.ValueKind != JsonValueKind.Array
                || doc.RootElement.TryGetProperty("nodeIds", out _))
            {
                return false;
            }

            var parsed = JsonSerializer.Deserialize<NamespaceData>(json, JsonOptions);
            if (parsed is null || parsed.StorageVersion > CurrentStorageVersion)
                return false;

            if (parsed.Entries.Count == 0)
            {
                data = parsed;
                ambiguous = true;
                return true;
            }

            string? singleOwner = null;
            foreach (var entry in parsed.Entries)
            {
                if (string.IsNullOrEmpty(entry.Ns))
                {
                    data = parsed;
                    ambiguous = true;
                    return true;
                }

                if (singleOwner is null)
                {
                    singleOwner = entry.Ns;
                }
                else if (!string.Equals(singleOwner, entry.Ns, StringComparison.Ordinal))
                {
                    data = parsed;
                    ambiguous = true;
                    return true;
                }
            }

            data = parsed;
            ownerNs = singleOwner;
            ambiguous = false;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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
    /// Snapshot the previously verified primary payload into a single atomic self-checksummed
    /// <c>.bak</c> file before replacing the primary payload and companion <c>.sha256</c>.
    /// If a crash interrupts execution between payload replacement and checksum replacement,
    /// readers recover the last verified snapshot from <c>.bak</c> instead of losing the namespace.
    /// </summary>
    private void AtomicWriteAllText(string path, string content, bool keepBackup = true)
    {
        if (keepBackup)
            BackupVerifiedPrimaryIfPresent(path);

        var bytes = Encoding.UTF8.GetBytes(content);
        AtomicWriteBytes(path, bytes);

        OnAfterPayloadBeforeChecksumWrite?.Invoke(path);

        var hashBytes = Encoding.UTF8.GetBytes(Convert.ToHexString(SHA256.HashData(bytes)));
        AtomicWriteBytes(path + ".sha256", hashBytes);
    }

    private static void BackupVerifiedPrimaryIfPresent(string path)
    {
        if (!File.Exists(path))
            return;

        byte[] prevBytes;
        try
        {
            prevBytes = File.ReadAllBytes(path);
        }
        catch
        {
            return;
        }

        if (prevBytes.Length == 0)
            return;

        var prevHash = Convert.ToHexString(SHA256.HashData(prevBytes));
        var checksumPath = path + ".sha256";
        if (File.Exists(checksumPath))
        {
            string expected;
            try
            {
                expected = File.ReadAllText(checksumPath).Trim();
            }
            catch
            {
                return;
            }

            // Never overwrite an existing verified backup with a torn/mismatched primary.
            if (!string.Equals(prevHash, expected, StringComparison.OrdinalIgnoreCase))
                return;
        }
        else
        {
            // Legacy file without .sha256: only back up if it is parseable JSON.
            try
            {
                using var _ = JsonDocument.Parse(prevBytes);
            }
            catch (JsonException)
            {
                return;
            }
        }

        // Single atomic self-verifying backup file: "<64-char SHA256 hex>\n<raw UTF-8 JSON bytes>".
        var header = Encoding.ASCII.GetBytes(prevHash + "\n");
        var backupBytes = new byte[header.Length + prevBytes.Length];
        Buffer.BlockCopy(header, 0, backupBytes, 0, header.Length);
        Buffer.BlockCopy(prevBytes, 0, backupBytes, header.Length, prevBytes.Length);
        AtomicWriteBytes(path + ".bak", backupBytes);
    }

    private T? LoadVerifiedPayload<T>(string path, string label, out bool fromPrimary) where T : class
    {
        fromPrimary = false;
        try
        {
            var rawBytes = File.ReadAllBytes(path);
            if (VerifyChecksumBytes(path, rawBytes))
            {
                var parsed = JsonSerializer.Deserialize<T>(DecodeUtf8(rawBytes), JsonOptions);
                fromPrimary = true;
                return parsed;
            }

            if (TryDeserializeVerifiedBackup<T>(path, label, out var recovered))
            {
                _logger?.LogWarning(
                    "Checksum mismatch for {Label}; recovered last verified snapshot from backup",
                    label);
                return recovered;
            }

            _logger?.LogWarning("Checksum mismatch for {Label}; dropping corrupted payload", label);
            return null;
        }
        catch (JsonException ex)
        {
            if (TryDeserializeVerifiedBackup<T>(path, label, out var recovered))
            {
                _logger?.LogWarning(
                    ex,
                    "Corrupted JSON in {Label}; recovered last verified snapshot from backup",
                    label);
                return recovered;
            }

            _logger?.LogWarning(ex, "Corrupted JSON in {Label}, returning empty data", label);
            return null;
        }
    }

    private bool TryDeserializeVerifiedBackup<T>(string path, string label, out T? value) where T : class
    {
        value = null;
        if (!TryReadVerifiedBackup(path, out var backupJson))
            return false;

        try
        {
            value = JsonSerializer.Deserialize<T>(backupJson, JsonOptions);
            return value is not null;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Corrupted JSON in backup snapshot for {Label}", label);
            return false;
        }
    }

    private bool TryReadVerifiedJson(string path, string label, out string json)
    {
        json = string.Empty;
        try
        {
            var rawBytes = File.ReadAllBytes(path);
            if (VerifyChecksumBytes(path, rawBytes))
            {
                json = DecodeUtf8(rawBytes);
                return true;
            }

            if (TryReadVerifiedBackup(path, out json))
            {
                _logger?.LogWarning(
                    "Checksum mismatch for {Label}; using last verified snapshot from backup",
                    label);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private bool TryReadVerifiedBackup(string path, out string json)
    {
        json = string.Empty;
        var backupPath = path + ".bak";
        if (!File.Exists(backupPath))
            return false;

        try
        {
            var raw = File.ReadAllBytes(backupPath);
            // Header format: 64 ASCII SHA-256 hex chars + '\n' (65 bytes) followed by payload bytes.
            if (raw.Length <= 65 || raw[64] != (byte)'\n')
            {
                _logger?.LogWarning("Backup snapshot '{Path}' has invalid header; ignoring", backupPath);
                return false;
            }

            var expectedHash = Encoding.ASCII.GetString(raw, 0, 64);
            var payloadSpan = raw.AsSpan(65);
            var actualHash = Convert.ToHexString(SHA256.HashData(payloadSpan));
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogWarning("Backup snapshot '{Path}' failed checksum verification; ignoring", backupPath);
                return false;
            }

            json = DecodeUtf8(payloadSpan);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not read backup snapshot '{Path}'", backupPath);
            return false;
        }
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            bytes = bytes.Slice(3);
        return Encoding.UTF8.GetString(bytes);
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
        => VerifyChecksumBytes(path, Encoding.UTF8.GetBytes(content));

    private bool VerifyChecksumBytes(string path, byte[] rawBytes)
    {
        var checksumPath = path + ".sha256";
        if (!File.Exists(checksumPath))
            return true; // No checksum = legacy data, pass through

        try
        {
            var expected = File.ReadAllText(checksumPath).Trim();
            var actual = Convert.ToHexString(SHA256.HashData(rawBytes));
            return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Checksum file for '{Path}' exists but could not be read; verification fails closed", path);
            return false;
        }
    }
}
