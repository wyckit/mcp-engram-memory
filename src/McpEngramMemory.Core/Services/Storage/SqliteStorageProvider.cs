using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpEngramMemory.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Storage;

/// <summary>
/// SQLite-backed storage provider with transactional writes, crash safety,
/// and per-entry granularity. Implements the same debounced write pattern
/// as PersistenceManager for consistency.
/// </summary>
public sealed class SqliteStorageProvider : IStorageProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new FloatArrayBase64Converter() }
    };

    private const int CurrentSchemaVersion = 2;

    private readonly string _connectionString;
    private readonly object _timerLock = new();
    private readonly TimeSpan _debounceDelay;
    private readonly ILogger<SqliteStorageProvider>? _logger;
    private bool _disposed;

    private readonly Dictionary<string, (Timer Timer, Func<NamespaceData> DataProvider)> _pendingNsSaves = new();

    // Incremental write tracking: per-namespace pending upserts and deletes
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

    public SqliteStorageProvider(string? dbPath = null, int debounceMs = 500, ILogger<SqliteStorageProvider>? logger = null)
    {
        dbPath ??= Path.Combine(AppContext.BaseDirectory, "data", "memory.db");
        var dir = Path.GetDirectoryName(dbPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={dbPath}";
        _debounceDelay = TimeSpan.FromMilliseconds(debounceMs);
        _logger = logger;

        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var conn = OpenConnection();

        // Set WAL journal mode once per database (persists across connections)
        using var walCmd = conn.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL;";
        walCmd.ExecuteNonQuery();

        // Create base tables (v1 schema) — idempotent via IF NOT EXISTS
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

        // Read current version and run any pending migrations
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
            return 0; // Fresh database

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

            // Upsert version row
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

    /// <summary>v1→v2: Add lifecycle_state column for server-side filtering without JSON deserialization.</summary>
    private static void MigrateToV2(SqliteConnection conn, SqliteTransaction transaction)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;

        // Add column (no-op on fresh DBs where column doesn't need to exist yet,
        // but ALTER TABLE is idempotent-safe via try/catch for "duplicate column" errors)
        try
        {
            cmd.CommandText = "ALTER TABLE entries ADD COLUMN lifecycle_state TEXT DEFAULT 'stm'";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column"))
        {
            // Column already exists (e.g., re-run after partial migration) — safe to ignore
        }

        cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_entries_ns_state ON entries(ns, lifecycle_state)";
        cmd.ExecuteNonQuery();

        // Backfill lifecycle_state from JSON for existing entries.
        // ALTER TABLE ADD COLUMN with DEFAULT sets existing rows to 'stm',
        // so correct rows whose JSON actually has 'ltm' or 'archived'.
        cmd.CommandText = """
            UPDATE entries
            SET lifecycle_state = json_extract(json_data, '$.lifecycleState')
            WHERE json_extract(json_data, '$.lifecycleState') IS NOT NULL
              AND json_extract(json_data, '$.lifecycleState') != lifecycle_state
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
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
            cmd.CommandText = "SELECT json_data, checksum FROM entries WHERE ns = @ns";
            cmd.Parameters.AddWithValue("@ns", ns);

            var entries = new List<CognitiveEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var checksum = reader.GetString(1);

                if (!VerifyChecksum(json, checksum, $"entry in namespace '{ns}'"))
                    continue; // Skip corrupted entries

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

    /// <summary>SQLite supports per-entry incremental writes via INSERT OR REPLACE.</summary>
    public bool SupportsIncrementalWrites => true;

    /// <summary>Schedule a debounced upsert of a single entry.</summary>
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

            // Cancel any pending delete for this entry
            if (_pendingEntryDeletes.TryGetValue(ns, out var deletes))
                deletes.Remove(entry.Id);

            ScheduleIncrementalFlush(ns);
        }
    }

    /// <summary>Schedule a debounced delete of a single entry.</summary>
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

            // Cancel any pending upsert for this entry
            if (_pendingEntryUpserts.TryGetValue(ns, out var upserts))
                upserts.Remove(entryId);

            ScheduleIncrementalFlush(ns);
        }
    }

    /// <summary>Schedule or reset a debounce timer for incremental writes on a namespace. Must be called under _timerLock.</summary>
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
                // Only self-remove if we are still the current timer (avoid disposing a replacement)
                if (_incrementalTimers.TryGetValue(ns, out var current) && ReferenceEquals(current, selfRef))
                    _incrementalTimers.Remove(ns);
            }

            WriteIncrementalChanges(ns, upserts, deletes);
        }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        _incrementalTimers[ns] = selfRef;
    }

    /// <summary>Write batched incremental changes in a single transaction.</summary>
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
                    deleteCmd.CommandText = "DELETE FROM entries WHERE ns = @ns AND id = @id";
                    var delNsParam = deleteCmd.Parameters.Add("@ns", SqliteType.Text);
                    var delIdParam = deleteCmd.Parameters.Add("@id", SqliteType.Text);
                    deleteCmd.Prepare();

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
                    upsertCmd.CommandText = "INSERT OR REPLACE INTO entries (id, ns, json_data, checksum, lifecycle_state) VALUES (@id, @ns, @json, @checksum, @state)";
                    var idParam = upsertCmd.Parameters.Add("@id", SqliteType.Text);
                    var nsParam = upsertCmd.Parameters.Add("@ns", SqliteType.Text);
                    var jsonParam = upsertCmd.Parameters.Add("@json", SqliteType.Text);
                    var checksumParam = upsertCmd.Parameters.Add("@checksum", SqliteType.Text);
                    var stateParam = upsertCmd.Parameters.Add("@state", SqliteType.Text);
                    upsertCmd.Prepare();

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
            _logger?.LogError(ex, "Failed to save namespace '{Namespace}' to SQLite", ns);
        }
    }

    private void WriteNamespaceData(string ns, NamespaceData data)
    {
        using var conn = OpenConnection();
        using var transaction = conn.BeginTransaction();
        try
        {
            // Delete existing entries for this namespace
            using var deleteCmd = conn.CreateCommand();
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = "DELETE FROM entries WHERE ns = @ns";
            deleteCmd.Parameters.AddWithValue("@ns", ns);
            deleteCmd.ExecuteNonQuery();

            // Insert all entries
            using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = "INSERT INTO entries (id, ns, json_data, checksum, lifecycle_state) VALUES (@id, @ns, @json, @checksum, @state)";
            var idParam = insertCmd.Parameters.Add("@id", SqliteType.Text);
            var nsParam = insertCmd.Parameters.Add("@ns", SqliteType.Text);
            var jsonParam = insertCmd.Parameters.Add("@json", SqliteType.Text);
            var checksumParam = insertCmd.Parameters.Add("@checksum", SqliteType.Text);
            var stateParam = insertCmd.Parameters.Add("@state", SqliteType.Text);
            insertCmd.Prepare();

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
            cmd.CommandText = """
                INSERT OR REPLACE INTO global_data (key, json_data, checksum)
                VALUES (@key, @json, @checksum)
                """;
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@json", json);
            cmd.Parameters.AddWithValue("@checksum", checksum);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save global data '{Key}' to SQLite", key);
        }
    }

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

            // Collect pending incremental writes
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
