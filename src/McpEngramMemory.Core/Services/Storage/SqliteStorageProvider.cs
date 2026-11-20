using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpEngramMemory.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Storage;

/// <summary>
/// SQLite-backed storage provider with transactional writes, crash safety,
/// and per-entry granularity. Shares debounced-write, backoff, anti-starvation,
/// and flush coordination with JSON and SQL Server via <see cref="DebouncedStorageCoordinator{TEntryKey}"/>.
/// </summary>
public sealed class SqliteStorageProvider : IStorageProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new FloatArrayBase64Converter() }
    };

    private const int CurrentSchemaVersion = 3;

    private readonly record struct EntryStorageKey(string TenantId, string EntryId);

    private readonly string _connectionString;
    private readonly string _dbFullPath;
    private readonly ILogger<SqliteStorageProvider>? _logger;
    private readonly DebouncedStorageCoordinator<EntryStorageKey> _coordinator;

    // Retained directly on the provider pointing at the coordinator's live locks/queues
    // for deterministic concurrency-test inspection.
    private readonly object _timerLock;
#pragma warning disable CS0414 // Reflected on by StorageTimerOwnershipTests; updated via coordinator disposal callback.
    private bool _disposed;
#pragma warning restore CS0414
    private readonly Dictionary<string, Dictionary<EntryStorageKey, CognitiveEntry>> _pendingEntryUpserts;
    private readonly Dictionary<string, HashSet<EntryStorageKey>> _pendingEntryDeletes;

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

    internal Action<string>? OnBeforeGlobalWrite { get; set; }
    internal Action? OnBeforeCollapseCommit { get; set; }

    public SqliteStorageProvider(string? dbPath = null, int debounceMs = 500, ILogger<SqliteStorageProvider>? logger = null)
    {
        dbPath = Path.GetFullPath(dbPath ?? Path.Combine(AppContext.BaseDirectory, "data", "memory.db"));
        var dir = Path.GetDirectoryName(dbPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        _dbFullPath = dbPath;
        _logger = logger;

        InitializeSchema();

        _coordinator = new DebouncedStorageCoordinator<EntryStorageKey>(
            TimeSpan.FromMilliseconds(debounceMs),
            supportsIncrementalWrites: true,
            logger: _logger,
            makeKey: static (t, id) => new EntryStorageKey(t, id),
            getTenantId: static k => k.TenantId,
            writeNamespace: WriteNamespace,
            writeIncremental: WriteIncrementalChanges,
            writeEdges: edges => WriteGlobalData("edges", edges),
            writeClusters: clusters => WriteGlobalData("clusters", clusters),
            writeDecayConfigs: configs => WriteGlobalData("decay_configs", configs.Values.ToList()),
            writeHnsw: TrySaveHnswSnapshotSync,
            onDisposedUnderLock: () => _disposed = true);
        _timerLock = _coordinator.TimerLock;
        _pendingEntryUpserts = _coordinator.PendingEntryUpserts;
        _pendingEntryDeletes = _coordinator.PendingEntryDeletes;
    }

    public string StoreIdentity => StoreIdentityUtil.CanonicalPath(_dbFullPath);

    private void InitializeSchema()
    {
        using var conn = OpenConnection();

        using var walCmd = conn.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL;";
        walCmd.ExecuteNonQuery();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS entries (
                id TEXT NOT NULL,
                ns TEXT NOT NULL,
                json_data TEXT NOT NULL,
                checksum TEXT NOT NULL,
                PRIMARY KEY (ns, id)
            );

            CREATE TABLE IF NOT EXISTS global_data (
                key TEXT PRIMARY KEY,
                json_data TEXT NOT NULL,
                checksum TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_entries_ns ON entries(ns);
            """;
        cmd.ExecuteNonQuery();

        int currentVersion = GetSchemaVersion(conn);
        if (currentVersion < CurrentSchemaVersion)
            RunMigrations(conn, currentVersion);
    }

    private static int GetSchemaVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_version";
        var count = (long)cmd.ExecuteScalar()!;
        if (count == 0)
            return 0;

        cmd.CommandText = "SELECT version FROM schema_version LIMIT 1";
        return Convert.ToInt32(cmd.ExecuteScalar()!);
    }

    private void RunMigrations(SqliteConnection conn, int fromVersion)
    {
        using var transaction = conn.BeginTransaction();
        try
        {
            if (fromVersion < 2)
                MigrateToV2(conn, transaction);
            if (fromVersion < 3)
                MigrateToV3(conn, transaction);

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = fromVersion == 0
                ? "INSERT INTO schema_version (version) VALUES (@v)"
                : "UPDATE schema_version SET version = @v";
            cmd.Parameters.AddWithValue("@v", CurrentSchemaVersion);
            cmd.ExecuteNonQuery();

            transaction.Commit();
            _logger?.LogInformation("Schema migrated from v{From} to v{To}", fromVersion, CurrentSchemaVersion);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void MigrateToV2(SqliteConnection conn, SqliteTransaction transaction)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;

        try
        {
            cmd.CommandText = "ALTER TABLE entries ADD COLUMN lifecycle_state TEXT DEFAULT 'stm'";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column"))
        {
            // Column already exists
        }

        cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_entries_ns_state ON entries(ns, lifecycle_state)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            UPDATE entries
            SET lifecycle_state = json_extract(json_data, '$.lifecycleState')
            WHERE json_extract(json_data, '$.lifecycleState') IS NOT NULL
              AND json_extract(json_data, '$.lifecycleState') != lifecycle_state
            """;
        cmd.ExecuteNonQuery();
    }

    private static void MigrateToV3(SqliteConnection conn, SqliteTransaction transaction)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            CREATE TABLE entries_v3 (
                tenant_id TEXT NOT NULL DEFAULT '',
                id TEXT NOT NULL,
                ns TEXT NOT NULL,
                json_data TEXT NOT NULL,
                checksum TEXT NOT NULL,
                lifecycle_state TEXT NOT NULL DEFAULT 'stm',
                PRIMARY KEY (tenant_id, ns, id)
            );

            INSERT INTO entries_v3 (tenant_id, id, ns, json_data, checksum, lifecycle_state)
            SELECT '', id, ns, json_data, checksum, COALESCE(lifecycle_state, 'stm')
            FROM entries;

            DROP TABLE entries;
            ALTER TABLE entries_v3 RENAME TO entries;

            CREATE INDEX idx_entries_ns ON entries(ns);
            CREATE INDEX idx_entries_tenant_ns ON entries(tenant_id, ns);
            CREATE INDEX idx_entries_tenant_ns_state
                ON entries(tenant_id, ns, lifecycle_state);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        await pragma.ExecuteNonQueryAsync().ConfigureAwait(false);
        return conn;
    }

    private static string ComputeChecksum(string data)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));

    private static string NormalizeTenantId(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? string.Empty : tenantId.Trim();

    private bool VerifyChecksum(string data, string expectedChecksum, string context)
    {
        var actual = ComputeChecksum(data);
        if (string.Equals(actual, expectedChecksum, StringComparison.OrdinalIgnoreCase))
            return true;

        _logger?.LogWarning("Checksum mismatch for {Context}: expected {Expected}, got {Actual}",
            context, expectedChecksum, actual);
        return false;
    }

    // ── Load methods ──

    public NamespaceData LoadNamespace(string ns)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json_data, checksum FROM entries WHERE ns = @ns";
            cmd.Parameters.AddWithValue("@ns", ns);

            var entries = new List<CognitiveEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var checksum = reader.GetString(1);

                if (!VerifyChecksum(json, checksum, $"entry in namespace '{ns}'"))
                    continue;

                var entry = JsonSerializer.Deserialize<CognitiveEntry>(json, JsonOptions);
                if (entry is not null)
                    entries.Add(entry);
            }

            return new NamespaceData { StorageVersion = CurrentSchemaVersion, Entries = entries };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error loading namespace '{Namespace}' from SQLite", ns);
            return new NamespaceData();
        }
    }

    public IReadOnlyList<string> GetPersistedNamespaces()
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT ns FROM entries WHERE ns NOT LIKE '\\_%' ESCAPE '\\' AND ns NOT LIKE '\\_\\_%' ESCAPE '\\'";

            var namespaces = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                namespaces.Add(reader.GetString(0));
            return namespaces;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error listing namespaces from SQLite");
            throw new NamespaceEnumerationException(ex);
        }
    }

    public List<GraphEdge> LoadGlobalEdges() => LoadGlobalData<List<GraphEdge>>("edges") ?? new();
    public List<SemanticCluster> LoadClusters() => LoadGlobalData<List<SemanticCluster>>("clusters") ?? new();

    public List<CollapseRecord> LoadCollapseHistory()
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json_data, checksum FROM global_data WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", "collapse_history");
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return new();
            var json = reader.GetString(0);
            var checksum = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (checksum is not null && !VerifyChecksum(json, checksum, "collapse_history"))
                return new();
            var set = CollapseRecordShape.DeserializeLenient(json, JsonOptions, out var dropped);
            if (dropped > 0)
                _logger?.LogWarning(
                    "Collapse-history boot load dropped {Dropped} malformed, duplicate, or forward-unknown " +
                    "record row(s); this store holds data this version cannot safely act on and should be repaired.",
                    dropped);
            return set;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error loading collapse history from SQLite");
            return new();
        }
    }

    public Dictionary<string, DecayConfig> LoadDecayConfigs()
    {
        var list = LoadGlobalData<List<DecayConfig>>("decay_configs");
        return NamespaceStore.DecayConfigsByPartition(list, _logger);
    }

    private T? LoadGlobalData<T>(string key) where T : class
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json_data, checksum FROM global_data WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            var json = reader.GetString(0);
            var checksum = reader.GetString(1);

            if (!VerifyChecksum(json, checksum, $"global data '{key}'"))
                return null;

            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error loading global data '{Key}' from SQLite", key);
            return null;
        }
    }

    // ── HNSW snapshot persistence ──

    public HnswSnapshot? LoadHnswSnapshot(string ns)
        => LoadGlobalData<HnswSnapshot>($"hnsw_{ns}");

    public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot)
        => _ = TrySaveHnswSnapshotSync(ns, snapshot);

    private bool TrySaveHnswSnapshotSync(string ns, HnswSnapshot snapshot)
    {
        try
        {
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var checksum = ComputeChecksum(json);
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO global_data (key, json_data, checksum)
                VALUES (@key, @json, @checksum)
                """;
            cmd.Parameters.AddWithValue("@key", $"hnsw_{ns}");
            cmd.Parameters.AddWithValue("@json", json);
            cmd.Parameters.AddWithValue("@checksum", checksum);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save HNSW snapshot for namespace '{Namespace}'", ns);
            return false;
        }
    }

    public void ScheduleSaveHnswSnapshot(string ns, Func<HnswSnapshot?> snapshotProvider)
        => _coordinator.ScheduleHnswSave(ns, snapshotProvider);

    public void DeleteHnswSnapshot(string ns)
    {
        _coordinator.FlushGate.Wait();
        try
        {
            _coordinator.CancelHnswSave(ns);
            try
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM global_data WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", $"hnsw_{ns}");
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to delete HNSW snapshot for namespace '{Namespace}'", ns);
            }
        }
        finally
        {
            _coordinator.FlushGate.Release();
        }
    }

    // ── Debounced save scheduling (delegated to DebouncedStorageCoordinator) ──

    public bool SupportsIncrementalWrites => true;

    public void ScheduleSave(string ns, Func<NamespaceData> dataProvider)
        => _coordinator.ScheduleSave(ns, dataProvider);

    public void SaveNamespaceSync(string ns, NamespaceData data)
    {
        WriteNamespaceData(ns, data);
        _coordinator.ResetNamespaceFailure(ns);
    }

    public void ScheduleUpsertEntry(string ns, CognitiveEntry entry)
        => _coordinator.ScheduleUpsertEntry(ns, entry);

    public void ScheduleDeleteEntry(string ns, string entryId)
        => _coordinator.ScheduleDeleteEntry(ns, entryId, string.Empty);

    public void ScheduleDeleteEntry(string ns, string entryId, string tenantId)
        => _coordinator.ScheduleDeleteEntry(ns, entryId, tenantId);

    public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider)
        => _coordinator.ScheduleSaveGlobalEdges(dataProvider);

    public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider)
        => _coordinator.ScheduleSaveClusters(dataProvider);

    public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider)
        => _coordinator.ScheduleSaveDecayConfigs(dataProvider);

    // ── Collapse history synchronous CAS RMW ──

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
        records = new List<CollapseRecord>();
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json_data, checksum FROM global_data WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", "collapse_history");
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var json = reader.GetString(0);
                var checksum = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (checksum is not null && !VerifyChecksum(json, checksum, "collapse_history"))
                {
                    _logger?.LogWarning("Strict collapse-record read refused: content does not match its stored checksum");
                    return false;
                }
                if (!CollapseRecordShape.TryDeserializeStrict(
                        json, JsonOptions, out records, out var defect))
                {
                    _logger?.LogWarning("Strict collapse-record read refused: {Defect}", defect);
                    records = new List<CollapseRecord>();
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Strict collapse-record read failed; existence reported as unknown");
            return false;
        }
    }

    private bool MutateCollapseHistorySync(Func<List<CollapseRecord>, bool> mutate)
    {
        if (!_coordinator.TryBeginTrackedExternalWrite())
            return false;

        bool committed = false;
        try
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();

            List<CollapseRecord> records;
            try
            {
                using var readCmd = conn.CreateCommand();
                readCmd.Transaction = tx;
                readCmd.CommandText = "SELECT json_data, checksum FROM global_data WHERE key = @key";
                readCmd.Parameters.AddWithValue("@key", "collapse_history");
                using var reader = readCmd.ExecuteReader();
                if (reader.Read())
                {
                    var json = reader.GetString(0);
                    var storedChecksum = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (storedChecksum is not null && !VerifyChecksum(json, storedChecksum, "collapse_history"))
                    {
                        _logger?.LogError("Collapse-history record write refused: content does not match its stored checksum");
                        return false;
                    }
                    if (!CollapseRecordShape.TryDeserializeStrict(
                            json, JsonOptions, out records, out var defect))
                    {
                        _logger?.LogError("Collapse-history record write refused: {Defect}", defect);
                        return false;
                    }
                }
                else
                {
                    records = new List<CollapseRecord>();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Collapse-history record write refused: the current set could not be read");
                return false;
            }

            if (!mutate(records))
                return true;

            OnBeforeCollapseCommit?.Invoke();

            var serialized = JsonSerializer.Serialize(records, JsonOptions);
            var checksum = ComputeChecksum(serialized);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO global_data (key, json_data, checksum)
                VALUES (@key, @json, @checksum)
                """;
            cmd.Parameters.AddWithValue("@key", "collapse_history");
            cmd.Parameters.AddWithValue("@json", serialized);
            cmd.Parameters.AddWithValue("@checksum", checksum);
            cmd.ExecuteNonQuery();
            tx.Commit();
            committed = true;
        }
        catch (Exception ex)
        {
            if (!committed)
            {
                _logger?.LogError(ex, "Synchronous collapse-history save failed; transaction rolled back");
                return false;
            }
            _logger?.LogWarning(ex, "Post-commit teardown failed after collapse-history save; the commit stands");
        }
        finally
        {
            _coordinator.EndTrackedWrite();
        }

        return true;
    }

    // ── Write implementations ──

    private bool WriteNamespace(string ns, Func<NamespaceData> provider)
    {
        try
        {
            var data = provider();
            WriteNamespaceData(ns, data);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save namespace '{Namespace}' to SQLite", ns);
            return false;
        }
    }

    private void WriteNamespaceData(string ns, NamespaceData data)
    {
        using var conn = OpenConnection();
        using var transaction = conn.BeginTransaction();
        try
        {
            using var deleteCmd = conn.CreateCommand();
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = "DELETE FROM entries WHERE ns = @ns";
            deleteCmd.Parameters.AddWithValue("@ns", ns);
            deleteCmd.ExecuteNonQuery();

            using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = "INSERT INTO entries (tenant_id, id, ns, json_data, checksum, lifecycle_state) VALUES (@tenant, @id, @ns, @json, @checksum, @state)";
            var tenantParam = insertCmd.Parameters.Add("@tenant", SqliteType.Text);
            var idParam = insertCmd.Parameters.Add("@id", SqliteType.Text);
            var nsParam = insertCmd.Parameters.Add("@ns", SqliteType.Text);
            var jsonParam = insertCmd.Parameters.Add("@json", SqliteType.Text);
            var checksumParam = insertCmd.Parameters.Add("@checksum", SqliteType.Text);
            var stateParam = insertCmd.Parameters.Add("@state", SqliteType.Text);
            insertCmd.Prepare();

            foreach (var entry in data.Entries)
            {
                var json = JsonSerializer.Serialize(entry, JsonOptions);
                tenantParam.Value = entry.TenantId;
                idParam.Value = entry.Id;
                nsParam.Value = ns;
                jsonParam.Value = json;
                checksumParam.Value = ComputeChecksum(json);
                stateParam.Value = entry.LifecycleState;
                insertCmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private bool WriteIncrementalChanges(
        string ns,
        Dictionary<EntryStorageKey, CognitiveEntry>? upserts,
        HashSet<EntryStorageKey>? deletes)
    {
        if ((upserts is null || upserts.Count == 0) && (deletes is null || deletes.Count == 0))
            return true;

        try
        {
            using var conn = OpenConnection();
            using var transaction = conn.BeginTransaction();
            try
            {
                if (deletes is not null && deletes.Count > 0)
                {
                    using var deleteCmd = conn.CreateCommand();
                    deleteCmd.Transaction = transaction;
                    deleteCmd.CommandText = "DELETE FROM entries WHERE tenant_id = @tenant AND ns = @ns AND id = @id";
                    var delTenantParam = deleteCmd.Parameters.Add("@tenant", SqliteType.Text);
                    var delNsParam = deleteCmd.Parameters.Add("@ns", SqliteType.Text);
                    var delIdParam = deleteCmd.Parameters.Add("@id", SqliteType.Text);
                    deleteCmd.Prepare();

                    delNsParam.Value = ns;
                    foreach (var key in deletes)
                    {
                        delTenantParam.Value = key.TenantId;
                        delIdParam.Value = key.EntryId;
                        deleteCmd.ExecuteNonQuery();
                    }
                }

                if (upserts is not null && upserts.Count > 0)
                {
                    using var upsertCmd = conn.CreateCommand();
                    upsertCmd.Transaction = transaction;
                    upsertCmd.CommandText = "INSERT OR REPLACE INTO entries (tenant_id, id, ns, json_data, checksum, lifecycle_state) VALUES (@tenant, @id, @ns, @json, @checksum, @state)";
                    var tenantParam = upsertCmd.Parameters.Add("@tenant", SqliteType.Text);
                    var idParam = upsertCmd.Parameters.Add("@id", SqliteType.Text);
                    var nsParam = upsertCmd.Parameters.Add("@ns", SqliteType.Text);
                    var jsonParam = upsertCmd.Parameters.Add("@json", SqliteType.Text);
                    var checksumParam = upsertCmd.Parameters.Add("@checksum", SqliteType.Text);
                    var stateParam = upsertCmd.Parameters.Add("@state", SqliteType.Text);
                    upsertCmd.Prepare();

                    foreach (var entry in upserts.Values)
                    {
                        var json = JsonSerializer.Serialize(entry, JsonOptions);
                        tenantParam.Value = entry.TenantId;
                        idParam.Value = entry.Id;
                        nsParam.Value = ns;
                        jsonParam.Value = json;
                        checksumParam.Value = ComputeChecksum(json);
                        stateParam.Value = entry.LifecycleState;
                        upsertCmd.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to write incremental changes for namespace '{Namespace}'", ns);
            return false;
        }
    }

    private bool WriteGlobalData<T>(string key, T data)
    {
        try
        {
            OnBeforeGlobalWrite?.Invoke(key);
            var json = JsonSerializer.Serialize(data, JsonOptions);
            var checksum = ComputeChecksum(json);

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO global_data (key, json_data, checksum)
                VALUES (@key, @json, @checksum)
                """;
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@json", json);
            cmd.Parameters.AddWithValue("@checksum", checksum);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save global data '{Key}' to SQLite", key);
            return false;
        }
    }

    // ── Namespace deletion ──

    public async Task DeleteNamespaceAsync(string ns)
    {
        await _coordinator.FlushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _coordinator.CancelAllPendingForNamespaceUnlockedByGate(ns);

            using var conn = await OpenConnectionAsync().ConfigureAwait(false);
            using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                using var cmdEntries = conn.CreateCommand();
                cmdEntries.Transaction = (SqliteTransaction)tx;
                cmdEntries.CommandText = "DELETE FROM entries WHERE ns = @ns";
                cmdEntries.Parameters.AddWithValue("@ns", ns);
                await cmdEntries.ExecuteNonQueryAsync().ConfigureAwait(false);

                using var cmdHnsw = conn.CreateCommand();
                cmdHnsw.Transaction = (SqliteTransaction)tx;
                cmdHnsw.CommandText = "DELETE FROM global_data WHERE key = @key";
                cmdHnsw.Parameters.AddWithValue("@key", $"hnsw_{ns}");
                await cmdHnsw.ExecuteNonQueryAsync().ConfigureAwait(false);

                await tx.CommitAsync().ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _coordinator.FlushGate.Release();
        }
    }

    public async Task DeleteNamespaceAsync(string ns, string tenantId)
    {
        tenantId = NormalizeTenantId(tenantId);
        await _coordinator.FlushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _coordinator.CancelTenantPendingForNamespaceUnlockedByGate(ns, tenantId, out var pendingNsProvider);

            if (pendingNsProvider is not null
                && StorageSnapshot.TryCapture(pendingNsProvider, _logger, out var pendingData)
                && pendingData is not null)
            {
                var otherTenantUpserts = new Dictionary<EntryStorageKey, CognitiveEntry>();
                foreach (var entry in pendingData.Entries)
                {
                    if (entry.TenantId != tenantId)
                        otherTenantUpserts[new EntryStorageKey(entry.TenantId, entry.Id)] = entry;
                }
                if (otherTenantUpserts.Count > 0)
                    WriteIncrementalChanges(ns, otherTenantUpserts, deletes: null);
            }

            using var conn = await OpenConnectionAsync().ConfigureAwait(false);
            using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                using (var cmdEntries = conn.CreateCommand())
                {
                    cmdEntries.Transaction = (SqliteTransaction)tx;
                    cmdEntries.CommandText = "DELETE FROM entries WHERE tenant_id = @tenant AND ns = @ns";
                    cmdEntries.Parameters.AddWithValue("@tenant", tenantId);
                    cmdEntries.Parameters.AddWithValue("@ns", ns);
                    await cmdEntries.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                using (var cmdHnsw = conn.CreateCommand())
                {
                    cmdHnsw.Transaction = (SqliteTransaction)tx;
                    cmdHnsw.CommandText = "DELETE FROM global_data WHERE key = @key";
                    cmdHnsw.Parameters.AddWithValue("@key",
                        $"hnsw_{NamespaceStore.PartitionKey(tenantId, ns)}");
                    await cmdHnsw.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                await tx.CommitAsync().ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _coordinator.FlushGate.Release();
        }
    }

    // ── Flush + Dispose ──

    public void Flush() => TryFlush();

    public bool TryFlush() => _coordinator.TryFlush();

    public void Dispose()
    {
        _coordinator.Dispose();
        CheckpointWal();
    }

    private void CheckpointWal()
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "WAL checkpoint on shutdown failed; the write-ahead log will be replayed when the database is next opened.");
        }
    }
}
