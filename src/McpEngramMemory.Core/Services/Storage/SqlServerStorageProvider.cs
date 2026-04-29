using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using McpEngramMemory.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Storage;

/// <summary>
/// Microsoft SQL Server-backed storage provider. Mirrors SqliteStorageProvider's
/// debounced-write, transactional, and per-entry incremental-write semantics, with
/// a configurable schema (default <c>dbo</c>).
/// </summary>
public sealed class SqlServerStorageProvider : IStorageProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new FloatArrayBase64Converter() }
    };

    private const int CurrentSchemaVersion = 2;
    private static readonly Regex SchemaNameRegex = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly string _connectionString;
    private readonly string _schema;
    private readonly string _schemaQuoted;
    private readonly object _timerLock = new();
    private readonly TimeSpan _debounceDelay;
    private readonly ILogger<SqlServerStorageProvider>? _logger;
    private bool _disposed;

    private readonly Dictionary<string, (Timer Timer, Func<NamespaceData> DataProvider)> _pendingNsSaves = new();

    private readonly Dictionary<string, Dictionary<string, CognitiveEntry>> _pendingEntryUpserts = new();
    private readonly Dictionary<string, HashSet<string>> _pendingEntryDeletes = new();
    private readonly Dictionary<string, Timer> _incrementalTimers = new();

    private Timer? _pendingEdgeTimer;
    private Func<List<GraphEdge>>? _pendingEdgeProvider;
    private Timer? _pendingClusterTimer;
    private Func<List<SemanticCluster>>? _pendingClusterProvider;
    private Timer? _pendingCollapseHistoryTimer;
    private Func<List<CollapseRecord>>? _pendingCollapseHistoryProvider;
    private Timer? _pendingDecayConfigTimer;
    private Func<Dictionary<string, DecayConfig>>? _pendingDecayConfigProvider;

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
        _debounceDelay = TimeSpan.FromMilliseconds(debounceMs);
        _logger = logger;

        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var conn = OpenConnection();

        // Create schema if missing (no-op for dbo).
        using (var ensureSchema = conn.CreateCommand())
        {
            ensureSchema.CommandText = $"""
                IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = @schema)
                    EXEC('CREATE SCHEMA {_schemaQuoted}');
                """;
            ensureSchema.Parameters.AddWithValue("@schema", _schema);
            ensureSchema.ExecuteNonQuery();
        }

        // Base v2 tables — created in one shot since this is a new backend.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                IF OBJECT_ID(N'{_schema}.schema_version', N'U') IS NULL
                    CREATE TABLE {_schemaQuoted}.schema_version (
                        version INT NOT NULL
                    );

                IF OBJECT_ID(N'{_schema}.entries', N'U') IS NULL
                    CREATE TABLE {_schemaQuoted}.entries (
                        id              NVARCHAR(450) NOT NULL,
                        ns              NVARCHAR(450) NOT NULL,
                        json_data       NVARCHAR(MAX) NOT NULL,
                        checksum        CHAR(64)      NOT NULL,
                        lifecycle_state NVARCHAR(32)  NOT NULL CONSTRAINT DF_engram_entries_lifecycle DEFAULT('stm'),
                        CONSTRAINT PK_engram_entries PRIMARY KEY (ns, id)
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
            SetSchemaVersion(conn, currentVersion, CurrentSchemaVersion);
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

    private void SetSchemaVersion(SqlConnection conn, int fromVersion, int toVersion)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = fromVersion == 0
            ? $"INSERT INTO {_schemaQuoted}.schema_version (version) VALUES (@v)"
            : $"UPDATE {_schemaQuoted}.schema_version SET version = @v";
        cmd.Parameters.AddWithValue("@v", toVersion);
        cmd.ExecuteNonQuery();
        _logger?.LogInformation("SQL Server schema initialized at v{To} (was v{From}) in schema [{Schema}]",
            toVersion, fromVersion, _schema);
    }

    private SqlConnection OpenConnection()
    {
        var conn = new SqlConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static string ComputeChecksum(string data)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash);
    }

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
            return Array.Empty<string>();
        }
    }

    public List<GraphEdge> LoadGlobalEdges() => LoadGlobalData<List<GraphEdge>>("edges") ?? new();
    public List<SemanticCluster> LoadClusters() => LoadGlobalData<List<SemanticCluster>>("clusters") ?? new();
    public List<CollapseRecord> LoadCollapseHistory() => LoadGlobalData<List<CollapseRecord>>("collapse_history") ?? new();

    public Dictionary<string, DecayConfig> LoadDecayConfigs()
    {
        var list = LoadGlobalData<List<DecayConfig>>("decay_configs");
        return list?.ToDictionary(c => c.Ns) ?? new();
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
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save HNSW snapshot for namespace '{Namespace}'", ns);
        }
    }

    public void DeleteHnswSnapshot(string ns)
    {
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

    // ── Save methods (debounced) ──

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

    public void SaveNamespaceSync(string ns, NamespaceData data)
    {
        WriteNamespaceData(ns, data);
    }

    // ── Incremental writes ──

    public bool SupportsIncrementalWrites => true;

    public void ScheduleUpsertEntry(string ns, CognitiveEntry entry)
    {
        lock (_timerLock)
        {
            if (_disposed) return;

            if (!_pendingEntryUpserts.TryGetValue(ns, out var upserts))
            {
                upserts = new();
                _pendingEntryUpserts[ns] = upserts;
            }
            upserts[entry.Id] = entry;

            if (_pendingEntryDeletes.TryGetValue(ns, out var deletes))
                deletes.Remove(entry.Id);

            ScheduleIncrementalFlush(ns);
        }
    }

    public void ScheduleDeleteEntry(string ns, string entryId)
    {
        lock (_timerLock)
        {
            if (_disposed) return;

            if (!_pendingEntryDeletes.TryGetValue(ns, out var deletes))
            {
                deletes = new();
                _pendingEntryDeletes[ns] = deletes;
            }
            deletes.Add(entryId);

            if (_pendingEntryUpserts.TryGetValue(ns, out var upserts))
                upserts.Remove(entryId);

            ScheduleIncrementalFlush(ns);
        }
    }

    private void ScheduleIncrementalFlush(string ns)
    {
        if (_incrementalTimers.TryGetValue(ns, out var existing))
            existing.Dispose();

        Timer? selfRef = null;
        selfRef = new Timer(_ =>
        {
            Dictionary<string, CognitiveEntry>? upserts = null;
            HashSet<string>? deletes = null;

            lock (_timerLock)
            {
                if (_pendingEntryUpserts.TryGetValue(ns, out var u) && u.Count > 0)
                {
                    upserts = new(u);
                    u.Clear();
                }
                if (_pendingEntryDeletes.TryGetValue(ns, out var d) && d.Count > 0)
                {
                    deletes = new(d);
                    d.Clear();
                }
                if (_incrementalTimers.TryGetValue(ns, out var current) && ReferenceEquals(current, selfRef))
                    _incrementalTimers.Remove(ns);
            }

            WriteIncrementalChanges(ns, upserts, deletes);
        }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        _incrementalTimers[ns] = selfRef;
    }

    private void WriteIncrementalChanges(string ns,
        Dictionary<string, CognitiveEntry>? upserts, HashSet<string>? deletes)
    {
        if ((upserts is null || upserts.Count == 0) && (deletes is null || deletes.Count == 0))
            return;

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
                    deleteCmd.CommandText = $"DELETE FROM {_schemaQuoted}.entries WHERE ns = @ns AND id = @id";
                    var delNsParam = deleteCmd.Parameters.Add("@ns", System.Data.SqlDbType.NVarChar, 450);
                    var delIdParam = deleteCmd.Parameters.Add("@id", System.Data.SqlDbType.NVarChar, 450);
                    delNsParam.Value = ns;
                    foreach (var id in deletes)
                    {
                        delIdParam.Value = id;
                        deleteCmd.ExecuteNonQuery();
                    }
                }

                if (upserts is not null && upserts.Count > 0)
                {
                    using var upsertCmd = conn.CreateCommand();
                    upsertCmd.Transaction = transaction;
                    upsertCmd.CommandText = BuildEntryUpsertSql();
                    var idParam = upsertCmd.Parameters.Add("@id", System.Data.SqlDbType.NVarChar, 450);
                    var nsParam = upsertCmd.Parameters.Add("@ns", System.Data.SqlDbType.NVarChar, 450);
                    var jsonParam = upsertCmd.Parameters.Add("@json", System.Data.SqlDbType.NVarChar, -1);
                    var checksumParam = upsertCmd.Parameters.Add("@checksum", System.Data.SqlDbType.Char, 64);
                    var stateParam = upsertCmd.Parameters.Add("@state", System.Data.SqlDbType.NVarChar, 32);

                    foreach (var entry in upserts.Values)
                    {
                        var json = JsonSerializer.Serialize(entry, JsonOptions);
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
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to write incremental changes for namespace '{Namespace}'", ns);
        }
    }

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
                    WriteGlobalData("edges", provider);
            }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

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
                    WriteGlobalData("clusters", provider);
            }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

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
                    WriteGlobalData("collapse_history", provider);
            }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

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
                {
                    var configs = provider();
                    var list = configs.Values.ToList();
                    WriteGlobalData("decay_configs", () => list);
                }
            }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    // ── Write implementations ──

    private void WriteNamespace(string ns, Func<NamespaceData> provider)
    {
        try
        {
            var data = provider();
            WriteNamespaceData(ns, data);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save namespace '{Namespace}' to SQL Server", ns);
        }
    }

    private void WriteNamespaceData(string ns, NamespaceData data)
    {
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

            using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = $"""
                INSERT INTO {_schemaQuoted}.entries (id, ns, json_data, checksum, lifecycle_state)
                VALUES (@id, @ns, @json, @checksum, @state)
                """;
            var idParam = insertCmd.Parameters.Add("@id", System.Data.SqlDbType.NVarChar, 450);
            var nsParam = insertCmd.Parameters.Add("@ns", System.Data.SqlDbType.NVarChar, 450);
            var jsonParam = insertCmd.Parameters.Add("@json", System.Data.SqlDbType.NVarChar, -1);
            var checksumParam = insertCmd.Parameters.Add("@checksum", System.Data.SqlDbType.Char, 64);
            var stateParam = insertCmd.Parameters.Add("@state", System.Data.SqlDbType.NVarChar, 32);

            foreach (var entry in data.Entries)
            {
                var json = JsonSerializer.Serialize(entry, JsonOptions);
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

    private void WriteGlobalData<T>(string key, Func<T> provider)
    {
        try
        {
            var data = provider();
            var json = JsonSerializer.Serialize(data, JsonOptions);
            var checksum = ComputeChecksum(json);

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = BuildGlobalUpsertSql();
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@json", json);
            cmd.Parameters.AddWithValue("@checksum", checksum);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save global data '{Key}' to SQL Server", key);
        }
    }

    public async Task DeleteNamespaceAsync(string ns)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            using (var cmdEntries = conn.CreateCommand())
            {
                cmdEntries.Transaction = tx;
                cmdEntries.CommandText = $"DELETE FROM {_schemaQuoted}.entries WHERE ns = @ns";
                cmdEntries.Parameters.AddWithValue("@ns", ns);
                await cmdEntries.ExecuteNonQueryAsync();
            }

            using (var cmdHnsw = conn.CreateCommand())
            {
                cmdHnsw.Transaction = tx;
                cmdHnsw.CommandText = $"DELETE FROM {_schemaQuoted}.global_data WHERE [key] = @key";
                cmdHnsw.Parameters.AddWithValue("@key", $"hnsw_{ns}");
                await cmdHnsw.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    // ── SQL builders ──

    private string BuildEntryUpsertSql() => $"""
        MERGE {_schemaQuoted}.entries WITH (HOLDLOCK) AS target
        USING (SELECT @ns AS ns, @id AS id) AS source
        ON target.ns = source.ns AND target.id = source.id
        WHEN MATCHED THEN
            UPDATE SET json_data = @json, checksum = @checksum, lifecycle_state = @state
        WHEN NOT MATCHED THEN
            INSERT (id, ns, json_data, checksum, lifecycle_state)
            VALUES (@id, @ns, @json, @checksum, @state);
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

    public void Flush()
    {
        List<(string Ns, Func<NamespaceData> Provider)> pendingNs;
        List<(string Ns, Dictionary<string, CognitiveEntry>? Upserts, HashSet<string>? Deletes)> pendingIncremental;
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

            pendingIncremental = new();
            var incrementalNs = new HashSet<string>(_pendingEntryUpserts.Keys);
            foreach (var k in _pendingEntryDeletes.Keys)
                incrementalNs.Add(k);
            foreach (var ns in incrementalNs)
            {
                Dictionary<string, CognitiveEntry>? upserts = null;
                HashSet<string>? deletes = null;

                if (_pendingEntryUpserts.TryGetValue(ns, out var u) && u.Count > 0)
                {
                    upserts = new(u);
                    u.Clear();
                }
                if (_pendingEntryDeletes.TryGetValue(ns, out var d) && d.Count > 0)
                {
                    deletes = new(d);
                    d.Clear();
                }
                if (upserts is not null || deletes is not null)
                    pendingIncremental.Add((ns, upserts, deletes));
            }
            foreach (var (_, timer) in _incrementalTimers)
                timer.Dispose();
            _incrementalTimers.Clear();

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

        foreach (var (ns, upserts, deletes) in pendingIncremental)
            WriteIncrementalChanges(ns, upserts, deletes);

        if (edgeProvider is not null)
            WriteGlobalData("edges", edgeProvider);

        if (clusterProvider is not null)
            WriteGlobalData("clusters", clusterProvider);

        if (collapseHistoryProvider is not null)
            WriteGlobalData("collapse_history", collapseHistoryProvider);

        if (decayConfigProvider is not null)
        {
            var configs = decayConfigProvider();
            WriteGlobalData("decay_configs", () => configs.Values.ToList());
        }
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
}
