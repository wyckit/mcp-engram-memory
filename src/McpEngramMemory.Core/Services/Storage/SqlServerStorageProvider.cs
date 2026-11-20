using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using McpEngramMemory.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Storage;

/// <summary>
/// Microsoft SQL Server-backed storage provider. Shares debounced-write, backoff,
/// anti-starvation, and flush coordination via <see cref="DebouncedStorageCoordinator{TEntryKey}"/>,
/// and batches multi-row writes through <see cref="SqlBulkCopy"/> + set-based temp-table
/// MERGE/DELETE statements to avoid N+1 network round-trips.
/// </summary>
public sealed class SqlServerStorageProvider : IStorageProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new FloatArrayBase64Converter() }
    };

    private const int CurrentSchemaVersion = 3;
    private static readonly Regex SchemaNameRegex = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    // SQL Server clustered index / PRIMARY KEY (tenant_id, ns, id) has a 900-byte limit.
    // Each NVARCHAR UTF-16 code unit takes 2 bytes, so combined length must not exceed 450.
    internal const int MaxPrimaryKeyTotalChars = 450;
    internal const int MaxTenantIdColumnChars = 64;

    /// <summary>
    /// Row threshold at which multi-row upserts/deletes switch from single parameterized
    /// commands to <see cref="SqlBulkCopy"/> into a session-local staging table + single
    /// set-based SQL MERGE/DELETE.
    /// </summary>
    internal const int DefaultBulkBatchThreshold = 5;

    internal readonly record struct EntryStorageKey(string TenantId, string EntryId);

    private readonly string _connectionString;
    private readonly string _schema;
    private readonly string _schemaQuoted;
    private readonly ILogger<SqlServerStorageProvider>? _logger;
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

    internal int BulkBatchThreshold { get; set; } = DefaultBulkBatchThreshold;

    internal Action<string>? OnBeforeGlobalWrite { get; set; }
    internal Action? OnBeforeCollapseCommit { get; set; }

    public SqlServerStorageProvider(
        string connectionString,
        string? schema = null,
        int debounceMs = 500,
        ILogger<SqlServerStorageProvider>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is required.", nameof(connectionString));

        schema ??= "dbo";
        if (!SchemaNameRegex.IsMatch(schema))
            throw new ArgumentException(
                $"Invalid schema name '{schema}'. Must match {SchemaNameRegex}.", nameof(schema));

        _connectionString = connectionString;
        _schema = schema;
        _schemaQuoted = $"[{schema}]";
        _logger = logger;

        string identity;
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var dataSource = builder.DataSource.Trim();
            if (dataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
                dataSource = dataSource.Substring(4);
            var hostPart = dataSource;
            var portPart = string.Empty;
            int comma = dataSource.IndexOf(',');
            if (comma >= 0)
            {
                hostPart = dataSource.Substring(0, comma);
                portPart = dataSource.Substring(comma);
            }
            int slash = hostPart.IndexOf('\\');
            var instancePart = string.Empty;
            if (slash >= 0)
            {
                instancePart = hostPart.Substring(slash);
                hostPart = hostPart.Substring(0, slash);
            }
            hostPart = hostPart.Trim().ToUpperInvariant() switch
            {
                "." or "(LOCAL)" or "LOCALHOST" or "127.0.0.1" or "::1" or "[::1]" => "LOCALHOST",
                var other => other,
            };
            identity = $"{hostPart}{instancePart}{portPart}/{builder.InitialCatalog}".ToUpperInvariant();
        }
        catch (Exception)
        {
            identity = connectionString;
        }
        StoreIdentity = $"mssql:{identity}/{schema.ToUpperInvariant()}";

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

    public string StoreIdentity { get; }

    private void InitializeSchema()
    {
        const int maxAttempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                InitializeSchemaCore();
                return;
            }
            catch (SqlException ex) when (ex.Number == 1205 && attempt < maxAttempts)
            {
                // Transient SQL Server DDL deadlock victim (Error 1205) when concurrent processes
                // or test schemas initialize in the same database simultaneously.
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private void InitializeSchemaCore()
    {
        using var conn = OpenConnection();

        using (var ensureSchema = conn.CreateCommand())
        {
            ensureSchema.CommandText = $"""
                IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = @schema)
                    EXEC('CREATE SCHEMA {_schemaQuoted}');
                """;
            ensureSchema.Parameters.AddWithValue("@schema", _schema);
            ensureSchema.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                IF OBJECT_ID(N'{_schema}.schema_version', N'U') IS NULL
                    CREATE TABLE {_schemaQuoted}.schema_version (
                        version INT NOT NULL
                    );

                IF OBJECT_ID(N'{_schema}.entries', N'U') IS NULL
                    CREATE TABLE {_schemaQuoted}.entries (
                        tenant_id       NVARCHAR(64)  NOT NULL CONSTRAINT DF_engram_entries_tenant DEFAULT(''),
                        id              NVARCHAR(450) NOT NULL,
                        ns              NVARCHAR(450) NOT NULL,
                        json_data       NVARCHAR(MAX) NOT NULL,
                        checksum        CHAR(64)      NOT NULL,
                        lifecycle_state NVARCHAR(32)  NOT NULL CONSTRAINT DF_engram_entries_lifecycle DEFAULT('stm'),
                        CONSTRAINT PK_engram_entries PRIMARY KEY (tenant_id, ns, id)
                    );

                IF OBJECT_ID(N'{_schema}.global_data', N'U') IS NULL
                    CREATE TABLE {_schemaQuoted}.global_data (
                        [key]     NVARCHAR(450) NOT NULL CONSTRAINT PK_engram_global_data PRIMARY KEY,
                        json_data NVARCHAR(MAX) NOT NULL,
                        checksum  CHAR(64)      NOT NULL
                    );

                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_entries_ns_state'
                               AND object_id = OBJECT_ID(N'{_schema}.entries'))
                    CREATE INDEX idx_entries_ns_state ON {_schemaQuoted}.entries(ns, lifecycle_state);
                """;
            cmd.ExecuteNonQuery();
        }

        int currentVersion = GetSchemaVersion(conn);
        if (currentVersion < CurrentSchemaVersion)
            RunMigrations(conn, currentVersion);
    }

    private void RunMigrations(SqlConnection conn, int fromVersion)
    {
        using var transaction = conn.BeginTransaction();
        try
        {
            if (fromVersion < 3)
                MigrateToV3(conn, transaction);

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = fromVersion == 0
                    ? $"INSERT INTO {_schemaQuoted}.schema_version (version) VALUES (@v)"
                    : $"UPDATE {_schemaQuoted}.schema_version SET version = @v";
                cmd.Parameters.AddWithValue("@v", CurrentSchemaVersion);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
            _logger?.LogInformation("SQL Server schema migrated from v{From} to v{To} in schema [{Schema}]",
                fromVersion, CurrentSchemaVersion, _schema);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private void MigrateToV3(SqlConnection conn, SqlTransaction transaction)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            IF NOT EXISTS (SELECT 1 FROM sys.columns
                           WHERE object_id = OBJECT_ID(N'{_schema}.entries') AND name = 'tenant_id')
                ALTER TABLE {_schemaQuoted}.entries
                    ADD tenant_id NVARCHAR(64) NOT NULL
                        CONSTRAINT DF_engram_entries_tenant DEFAULT('');

            IF NOT EXISTS (
                SELECT 1
                FROM sys.key_constraints kc
                JOIN sys.index_columns ic
                    ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id AND ic.key_ordinal = 1
                JOIN sys.columns c
                    ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE kc.name = 'PK_engram_entries'
                  AND kc.parent_object_id = OBJECT_ID(N'{_schema}.entries')
                  AND c.name = 'tenant_id')
            BEGIN
                IF EXISTS (SELECT 1 FROM sys.key_constraints
                           WHERE name = 'PK_engram_entries'
                             AND parent_object_id = OBJECT_ID(N'{_schema}.entries'))
                    ALTER TABLE {_schemaQuoted}.entries DROP CONSTRAINT PK_engram_entries;

                ALTER TABLE {_schemaQuoted}.entries
                    ADD CONSTRAINT PK_engram_entries PRIMARY KEY (tenant_id, ns, id);
            END

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_entries_tenant_ns_state'
                           AND object_id = OBJECT_ID(N'{_schema}.entries'))
                CREATE INDEX idx_entries_tenant_ns_state
                    ON {_schemaQuoted}.entries(tenant_id, ns, lifecycle_state);
            """;
        cmd.ExecuteNonQuery();
    }

    private int GetSchemaVersion(SqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {_schemaQuoted}.schema_version";
        var count = Convert.ToInt32(cmd.ExecuteScalar()!);
        if (count == 0)
            return 0;

        cmd.CommandText = $"SELECT TOP 1 version FROM {_schemaQuoted}.schema_version";
        return Convert.ToInt32(cmd.ExecuteScalar()!);
    }

    private SqlConnection OpenConnection()
    {
        var conn = new SqlConnection(_connectionString);
        conn.Open();
        return conn;
    }

    internal static void ValidateEntryKeyLengths(string tenantId, string ns, string id)
    {
        if (tenantId.Length > MaxTenantIdColumnChars)
            throw new ArgumentException(
                $"SQL Server entry tenant_id length ({tenantId.Length}) exceeds the column limit of {MaxTenantIdColumnChars} characters.",
                nameof(tenantId));

        int totalChars = tenantId.Length + ns.Length + id.Length;
        if (totalChars > MaxPrimaryKeyTotalChars)
            throw new ArgumentException(
                $"SQL Server primary key (tenant_id, ns, id) combined length is {totalChars} characters " +
                $"({totalChars * 2} bytes), which exceeds SQL Server's 900-byte ({MaxPrimaryKeyTotalChars}-char) " +
                "clustered index limit (Error 1946). Shorten entry id, namespace, or tenantId.",
                nameof(id));
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
            cmd.CommandText = $"SELECT json_data, checksum FROM {_schemaQuoted}.entries WHERE ns = @ns";
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
            _logger?.LogWarning(ex, "Error loading namespace '{Namespace}' from SQL Server", ns);
            return new NamespaceData();
        }
    }

    public IReadOnlyList<string> GetPersistedNamespaces()
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT DISTINCT ns FROM {_schemaQuoted}.entries " +
                              "WHERE ns NOT LIKE '\\_%' ESCAPE '\\' AND ns NOT LIKE '\\_\\_%' ESCAPE '\\'";

            var namespaces = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                namespaces.Add(reader.GetString(0));
            return namespaces;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error listing namespaces from SQL Server");
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
            cmd.CommandText = $"SELECT json_data, checksum FROM {_schemaQuoted}.global_data WHERE [key] = @key";
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
            _logger?.LogWarning(ex, "Error loading collapse history from SQL Server");
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
            cmd.CommandText = $"SELECT json_data, checksum FROM {_schemaQuoted}.global_data WHERE [key] = @key";
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
            _logger?.LogWarning(ex, "Error loading global data '{Key}' from SQL Server", key);
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
            cmd.CommandText = BuildGlobalUpsertSql();
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
                cmd.CommandText = $"DELETE FROM {_schemaQuoted}.global_data WHERE [key] = @key";
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
    {
        ValidateEntryKeyLengths(entry.TenantId, ns, entry.Id);
        _coordinator.ScheduleUpsertEntry(ns, entry);
    }

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
            cmd.CommandText = $"SELECT json_data, checksum FROM {_schemaQuoted}.global_data WHERE [key] = @key";
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
                readCmd.CommandText =
                    $"SELECT json_data, checksum FROM {_schemaQuoted}.global_data WITH (UPDLOCK, HOLDLOCK) WHERE [key] = @key";
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
                reader.Close();
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
            cmd.CommandText = BuildGlobalUpsertSql();
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

    // ── Write implementations (with SqlBulkCopy set-based batching) ──

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
            _logger?.LogError(ex, "Failed to save namespace '{Namespace}' to SQL Server", ns);
            return false;
        }
    }

    private void WriteNamespaceData(string ns, NamespaceData data)
    {
        foreach (var entry in data.Entries)
            ValidateEntryKeyLengths(entry.TenantId, ns, entry.Id);

        using var conn = OpenConnection();
        using var transaction = conn.BeginTransaction();
        try
        {
            using (var deleteCmd = conn.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = $"DELETE FROM {_schemaQuoted}.entries WHERE ns = @ns";
                deleteCmd.Parameters.AddWithValue("@ns", ns);
                deleteCmd.ExecuteNonQuery();
            }

            if (data.Entries.Count >= BulkBatchThreshold)
            {
                BulkInsertEntries(conn, transaction, ns, data.Entries);
            }
            else if (data.Entries.Count > 0)
            {
                InsertEntriesRowByRow(conn, transaction, ns, data.Entries);
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
            if (upserts is not null)
            {
                foreach (var entry in upserts.Values)
                    ValidateEntryKeyLengths(entry.TenantId, ns, entry.Id);
            }

            using var conn = OpenConnection();
            using var transaction = conn.BeginTransaction();
            try
            {
                if (deletes is not null && deletes.Count > 0)
                {
                    if (deletes.Count >= BulkBatchThreshold)
                        BulkDeleteEntries(conn, transaction, ns, deletes);
                    else
                        DeleteEntriesRowByRow(conn, transaction, ns, deletes);
                }

                if (upserts is not null && upserts.Count > 0)
                {
                    if (upserts.Count >= BulkBatchThreshold)
                        BulkMergeEntries(conn, transaction, ns, upserts.Values);
                    else
                        UpsertEntriesRowByRow(conn, transaction, ns, upserts.Values);
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

    internal static DataTable BuildUpsertBatchTable(string ns, IEnumerable<CognitiveEntry> entries)
    {
        var table = new DataTable();
        table.Columns.Add("tenant_id", typeof(string));
        table.Columns.Add("ns", typeof(string));
        table.Columns.Add("id", typeof(string));
        table.Columns.Add("json_data", typeof(string));
        table.Columns.Add("checksum", typeof(string));
        table.Columns.Add("lifecycle_state", typeof(string));

        foreach (var entry in entries)
        {
            ValidateEntryKeyLengths(entry.TenantId, ns, entry.Id);
            var json = JsonSerializer.Serialize(entry, JsonOptions);
            table.Rows.Add(
                entry.TenantId,
                ns,
                entry.Id,
                json,
                ComputeChecksum(json),
                entry.LifecycleState);
        }

        return table;
    }

    internal static DataTable BuildDeleteBatchTable(string ns, IEnumerable<EntryStorageKey> deletes)
    {
        var table = new DataTable();
        table.Columns.Add("tenant_id", typeof(string));
        table.Columns.Add("ns", typeof(string));
        table.Columns.Add("id", typeof(string));

        foreach (var key in deletes)
            table.Rows.Add(key.TenantId, ns, key.EntryId);

        return table;
    }

    internal static string BuildBulkMergeSql(string schemaQuoted) => $"""
        MERGE {schemaQuoted}.entries WITH (HOLDLOCK) AS target
        USING #engram_upsert_stage AS source
        ON target.tenant_id = source.tenant_id AND target.ns = source.ns AND target.id = source.id
        WHEN MATCHED THEN
            UPDATE SET json_data = source.json_data, checksum = source.checksum, lifecycle_state = source.lifecycle_state
        WHEN NOT MATCHED THEN
            INSERT (id, ns, tenant_id, json_data, checksum, lifecycle_state)
            VALUES (source.id, source.ns, source.tenant_id, source.json_data, source.checksum, source.lifecycle_state);
        DROP TABLE #engram_upsert_stage;
        """;

    internal static string BuildBulkDeleteSql(string schemaQuoted) => $"""
        DELETE target
        FROM {schemaQuoted}.entries AS target
        INNER JOIN #engram_delete_stage AS source
            ON target.tenant_id = source.tenant_id AND target.ns = source.ns AND target.id = source.id;
        DROP TABLE #engram_delete_stage;
        """;

    private void BulkInsertEntries(SqlConnection conn, SqlTransaction transaction, string ns, IEnumerable<CognitiveEntry> entries)
    {
        using var table = BuildUpsertBatchTable(ns, entries);
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = $"{_schemaQuoted}.entries",
            BatchSize = 1000
        };
        bulk.ColumnMappings.Add("tenant_id", "tenant_id");
        bulk.ColumnMappings.Add("ns", "ns");
        bulk.ColumnMappings.Add("id", "id");
        bulk.ColumnMappings.Add("json_data", "json_data");
        bulk.ColumnMappings.Add("checksum", "checksum");
        bulk.ColumnMappings.Add("lifecycle_state", "lifecycle_state");
        bulk.WriteToServer(table);
    }

    private void BulkMergeEntries(SqlConnection conn, SqlTransaction transaction, string ns, IEnumerable<CognitiveEntry> entries)
    {
        using (var createStage = conn.CreateCommand())
        {
            createStage.Transaction = transaction;
            createStage.CommandText = """
                CREATE TABLE #engram_upsert_stage (
                    tenant_id       NVARCHAR(64)  COLLATE DATABASE_DEFAULT NOT NULL,
                    ns              NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL,
                    id              NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL,
                    json_data       NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NOT NULL,
                    checksum        CHAR(64)      COLLATE DATABASE_DEFAULT NOT NULL,
                    lifecycle_state NVARCHAR(32)  COLLATE DATABASE_DEFAULT NOT NULL
                );
                """;
            createStage.ExecuteNonQuery();
        }

        using var table = BuildUpsertBatchTable(ns, entries);
        using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = "#engram_upsert_stage",
            BatchSize = 1000
        })
        {
            bulk.ColumnMappings.Add("tenant_id", "tenant_id");
            bulk.ColumnMappings.Add("ns", "ns");
            bulk.ColumnMappings.Add("id", "id");
            bulk.ColumnMappings.Add("json_data", "json_data");
            bulk.ColumnMappings.Add("checksum", "checksum");
            bulk.ColumnMappings.Add("lifecycle_state", "lifecycle_state");
            bulk.WriteToServer(table);
        }

        using var mergeCmd = conn.CreateCommand();
        mergeCmd.Transaction = transaction;
        mergeCmd.CommandText = BuildBulkMergeSql(_schemaQuoted);
        mergeCmd.ExecuteNonQuery();
    }

    private void BulkDeleteEntries(SqlConnection conn, SqlTransaction transaction, string ns, IEnumerable<EntryStorageKey> deletes)
    {
        using (var createStage = conn.CreateCommand())
        {
            createStage.Transaction = transaction;
            createStage.CommandText = """
                CREATE TABLE #engram_delete_stage (
                    tenant_id NVARCHAR(64)  COLLATE DATABASE_DEFAULT NOT NULL,
                    ns        NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL,
                    id        NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL
                );
                """;
            createStage.ExecuteNonQuery();
        }

        using var table = BuildDeleteBatchTable(ns, deletes);
        using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = "#engram_delete_stage",
            BatchSize = 1000
        })
        {
            bulk.ColumnMappings.Add("tenant_id", "tenant_id");
            bulk.ColumnMappings.Add("ns", "ns");
            bulk.ColumnMappings.Add("id", "id");
            bulk.WriteToServer(table);
        }

        using var deleteCmd = conn.CreateCommand();
        deleteCmd.Transaction = transaction;
        deleteCmd.CommandText = BuildBulkDeleteSql(_schemaQuoted);
        deleteCmd.ExecuteNonQuery();
    }

    private void InsertEntriesRowByRow(SqlConnection conn, SqlTransaction transaction, string ns, IEnumerable<CognitiveEntry> entries)
    {
        using var insertCmd = conn.CreateCommand();
        insertCmd.Transaction = transaction;
        insertCmd.CommandText = $"""
            INSERT INTO {_schemaQuoted}.entries (id, ns, tenant_id, json_data, checksum, lifecycle_state)
            VALUES (@id, @ns, @tenant, @json, @checksum, @state)
            """;
        var idParam = insertCmd.Parameters.Add("@id", SqlDbType.NVarChar, 450);
        var nsParam = insertCmd.Parameters.Add("@ns", SqlDbType.NVarChar, 450);
        var tenantParam = insertCmd.Parameters.Add("@tenant", SqlDbType.NVarChar, 64);
        var jsonParam = insertCmd.Parameters.Add("@json", SqlDbType.NVarChar, -1);
        var checksumParam = insertCmd.Parameters.Add("@checksum", SqlDbType.Char, 64);
        var stateParam = insertCmd.Parameters.Add("@state", SqlDbType.NVarChar, 32);

        foreach (var entry in entries)
        {
            var json = JsonSerializer.Serialize(entry, JsonOptions);
            idParam.Value = entry.Id;
            nsParam.Value = ns;
            tenantParam.Value = entry.TenantId;
            jsonParam.Value = json;
            checksumParam.Value = ComputeChecksum(json);
            stateParam.Value = entry.LifecycleState;
            insertCmd.ExecuteNonQuery();
        }
    }

    private void UpsertEntriesRowByRow(SqlConnection conn, SqlTransaction transaction, string ns, IEnumerable<CognitiveEntry> entries)
    {
        using var upsertCmd = conn.CreateCommand();
        upsertCmd.Transaction = transaction;
        upsertCmd.CommandText = BuildEntryUpsertSql();
        var idParam = upsertCmd.Parameters.Add("@id", SqlDbType.NVarChar, 450);
        var nsParam = upsertCmd.Parameters.Add("@ns", SqlDbType.NVarChar, 450);
        var tenantParam = upsertCmd.Parameters.Add("@tenant", SqlDbType.NVarChar, 64);
        var jsonParam = upsertCmd.Parameters.Add("@json", SqlDbType.NVarChar, -1);
        var checksumParam = upsertCmd.Parameters.Add("@checksum", SqlDbType.Char, 64);
        var stateParam = upsertCmd.Parameters.Add("@state", SqlDbType.NVarChar, 32);

        foreach (var entry in entries)
        {
            var json = JsonSerializer.Serialize(entry, JsonOptions);
            idParam.Value = entry.Id;
            nsParam.Value = ns;
            tenantParam.Value = entry.TenantId;
            jsonParam.Value = json;
            checksumParam.Value = ComputeChecksum(json);
            stateParam.Value = entry.LifecycleState;
            upsertCmd.ExecuteNonQuery();
        }
    }

    private void DeleteEntriesRowByRow(SqlConnection conn, SqlTransaction transaction, string ns, IEnumerable<EntryStorageKey> deletes)
    {
        using var deleteCmd = conn.CreateCommand();
        deleteCmd.Transaction = transaction;
        deleteCmd.CommandText = $"DELETE FROM {_schemaQuoted}.entries WHERE tenant_id = @tenant AND ns = @ns AND id = @id";
        var delTenantParam = deleteCmd.Parameters.Add("@tenant", SqlDbType.NVarChar, 64);
        var delNsParam = deleteCmd.Parameters.Add("@ns", SqlDbType.NVarChar, 450);
        var delIdParam = deleteCmd.Parameters.Add("@id", SqlDbType.NVarChar, 450);
        delNsParam.Value = ns;
        foreach (var key in deletes)
        {
            delTenantParam.Value = key.TenantId;
            delIdParam.Value = key.EntryId;
            deleteCmd.ExecuteNonQuery();
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
            cmd.CommandText = BuildGlobalUpsertSql();
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@json", json);
            cmd.Parameters.AddWithValue("@checksum", checksum);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save global data '{Key}' to SQL Server", key);
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

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            using var tx = (SqlTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                using (var cmdEntries = conn.CreateCommand())
                {
                    cmdEntries.Transaction = tx;
                    cmdEntries.CommandText = $"DELETE FROM {_schemaQuoted}.entries WHERE ns = @ns";
                    cmdEntries.Parameters.AddWithValue("@ns", ns);
                    await cmdEntries.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                using (var cmdHnsw = conn.CreateCommand())
                {
                    cmdHnsw.Transaction = tx;
                    cmdHnsw.CommandText = $"DELETE FROM {_schemaQuoted}.global_data WHERE [key] = @key";
                    cmdHnsw.Parameters.AddWithValue("@key", $"hnsw_{ns}");
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

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            using var tx = (SqlTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                using (var cmdEntries = conn.CreateCommand())
                {
                    cmdEntries.Transaction = tx;
                    cmdEntries.CommandText =
                        $"DELETE FROM {_schemaQuoted}.entries WHERE tenant_id = @tenant AND ns = @ns";
                    cmdEntries.Parameters.AddWithValue("@tenant", tenantId);
                    cmdEntries.Parameters.AddWithValue("@ns", ns);
                    await cmdEntries.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                using (var cmdHnsw = conn.CreateCommand())
                {
                    cmdHnsw.Transaction = tx;
                    cmdHnsw.CommandText = $"DELETE FROM {_schemaQuoted}.global_data WHERE [key] = @key";
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

    // ── SQL builders ──

    private string BuildEntryUpsertSql() => $"""
        MERGE {_schemaQuoted}.entries WITH (HOLDLOCK) AS target
        USING (SELECT @tenant AS tenant_id, @ns AS ns, @id AS id) AS source
        ON target.tenant_id = source.tenant_id AND target.ns = source.ns AND target.id = source.id
        WHEN MATCHED THEN
            UPDATE SET json_data = @json, checksum = @checksum, lifecycle_state = @state
        WHEN NOT MATCHED THEN
            INSERT (id, ns, tenant_id, json_data, checksum, lifecycle_state)
            VALUES (@id, @ns, @tenant, @json, @checksum, @state);
        """;

    private string BuildGlobalUpsertSql() => $"""
        MERGE {_schemaQuoted}.global_data WITH (HOLDLOCK) AS target
        USING (SELECT @key AS [key]) AS source
        ON target.[key] = source.[key]
        WHEN MATCHED THEN
            UPDATE SET json_data = @json, checksum = @checksum
        WHEN NOT MATCHED THEN
            INSERT ([key], json_data, checksum) VALUES (@key, @json, @checksum);
        """;

    // ── Flush + Dispose ──

    public void Flush() => TryFlush();

    public bool TryFlush() => _coordinator.TryFlush();

    public void Dispose() => _coordinator.Dispose();
}
