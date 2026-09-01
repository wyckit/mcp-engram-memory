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

    private const int CurrentSchemaVersion = 3;

    private readonly record struct EntryStorageKey(string TenantId, string EntryId);

    private readonly string _connectionString;
    private readonly object _timerLock = new();
    private readonly TimeSpan _debounceDelay;
    private readonly ILogger<SqliteStorageProvider>? _logger;
    private bool _disposed;

    private readonly Dictionary<string, (Timer Timer, Func<NamespaceData> DataProvider)> _pendingNsSaves = new();

    // Incremental write tracking: per-namespace pending upserts and deletes
    private readonly Dictionary<string, Dictionary<EntryStorageKey, CognitiveEntry>> _pendingEntryUpserts = new();
    private readonly Dictionary<string, HashSet<EntryStorageKey>> _pendingEntryDeletes = new();
    private readonly Dictionary<string, Timer> _incrementalTimers = new();

    private Timer? _pendingEdgeTimer;
    private Func<List<GraphEdge>>? _pendingEdgeProvider;
    private Timer? _pendingClusterTimer;
    private Func<List<SemanticCluster>>? _pendingClusterProvider;
    private Timer? _pendingDecayConfigTimer;
    private Func<Dictionary<string, DecayConfig>>? _pendingDecayConfigProvider;

    public SqliteStorageProvider(string? dbPath = null, int debounceMs = 500, ILogger<SqliteStorageProvider>? logger = null)
    {
        // Frozen ABSOLUTE at construction: a relative path resolved per connection would move
        // the backing store (and the gate identity) whenever CurrentDirectory changes.
        dbPath = Path.GetFullPath(dbPath ?? Path.Combine(AppContext.BaseDirectory, "data", "memory.db"));
        var dir = Path.GetDirectoryName(dbPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={dbPath}";
        _dbFullPath = dbPath;
        _debounceDelay = TimeSpan.FromMilliseconds(debounceMs);
        _logger = logger;

        InitializeSchema();
    }

    private readonly string _dbFullPath;

    /// <summary>The store's durable identity: the database file path canonicalized by
    /// <see cref="StoreIdentityUtil.CanonicalPath"/> — resolved, prefix-stripped,
    /// separator-trimmed, case-folded on Windows — so alias spellings of one file share one
    /// identity. See <see cref="IStorageProvider.StoreIdentity"/>.</summary>
    public string StoreIdentity => StoreIdentityUtil.CanonicalPath(_dbFullPath);

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
            if (fromVersion < 3)
                MigrateToV3(conn, transaction);

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

    /// <summary>
    /// v2→v3: Make tenant identity part of the physical entry key. SQLite cannot alter a
    /// primary key in place, so rebuild the table transactionally and place all existing rows
    /// in the legacy empty-string tenant.
    /// </summary>
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

    private static string ComputeChecksum(string data)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash);
    }

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

    /// <summary>
    /// Every namespace with at least one persisted row. A returned list means the query ran: an
    /// empty one is a store with no namespaces, never a store that could not be read.
    ///
    /// This throws where the other read paths log and degrade, and the asymmetry is deliberate. A
    /// failed <see cref="LoadNamespace"/> yields one unreadable namespace, and the caller can still
    /// tell that namespace apart from the rest. A failed listing yields an empty set that is
    /// indistinguishable from an empty database, and every downstream caller reads it as fact: the
    /// full-load sweep would mark itself complete over it, leaving persisted entries invisible for
    /// the life of the process. Invisible entries are not merely missing — an unlisted twin makes a
    /// duplicated id look unique, so the tenant-wide duplicate test that topology fails closed on
    /// passes instead.
    /// </summary>
    /// <exception cref="NamespaceEnumerationException">The listing query failed.</exception>
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
            // Logged here with the full backend detail and rethrown without it: the wrapper's
            // message reaches callers, and a SqliteException's does not.
            _logger?.LogWarning(ex, "Error listing namespaces from SQLite");
            throw new NamespaceEnumerationException(ex);
        }
    }

    public List<GraphEdge> LoadGlobalEdges() => LoadGlobalData<List<GraphEdge>>("edges") ?? new();
    public List<SemanticCluster> LoadClusters() => LoadGlobalData<List<SemanticCluster>>("clusters") ?? new();
    public List<CollapseRecord> LoadCollapseHistory()
    {
        // LENIENT boot load still validates RAW rows so an explicit tenant null or an unknown
        // future field cannot be normalized away and later laundered by a record RMW.
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
        // Key by the (tenant, ns) partition so two tenants can each hold a config for the same
        // namespace without colliding. Legacy configs (tenant "") key on the bare ns as before.
        // A store poisoned before partition-component validation existed must still boot, so the
        // shared builder resolves a colliding pair rather than throwing on a duplicate key.
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
            cmd.CommandText = "DELETE FROM global_data WHERE key = @key";
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
                // CAPTURE AND WRITE both inside the flush gate: capturing outside let this
                // callback hold an older snapshot at the gate while a newer flush committed,
                // then write the older one over it -- commit order inverting capture order.
                // A failed write is RETAINED (newer-wins) rather than dropped with a log line.
                lock (_flushGate)
                {
                    Func<NamespaceData>? provider = null;
                    lock (_timerLock)
                    {
                        if (_pendingNsSaves.TryGetValue(ns, out var entry))
                        {
                            provider = entry.DataProvider;
                            entry.Timer.Dispose();
                            _pendingNsSaves.Remove(ns);
                            // SUBSUMPTION at the point of commitment — see TryFlushCore's
                            // part 1: this full save materializes the live state, which
                            // already contains every change the pending increments for this
                            // namespace describe. Left queued, a frozen increment firing
                            // AFTER this commit would overwrite fresher rows with stale
                            // values; dropped here, a FAILED full save still re-subsumes
                            // them through its retained (re-materializing) retry.
                            _pendingEntryUpserts.Remove(ns);
                            _pendingEntryDeletes.Remove(ns);
                        }
                    }
                    if (provider is not null && !WriteNamespace(ns, provider))
                        lock (_timerLock)
                        {
                            if (_disposed)
                                _logger?.LogError("Pending write for namespace '{Namespace}' dropped: provider disposed during a failed timer write", ns);
                            else if (!_pendingNsSaves.ContainsKey(ns))
                                ScheduleSave(ns, provider);
                        }
                }
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
            var key = new EntryStorageKey(entry.TenantId, entry.Id);
            upserts[key] = entry;

            // Cancel any pending delete for this entry
            if (_pendingEntryDeletes.TryGetValue(ns, out var deletes))
                deletes.Remove(key);

            ScheduleIncrementalFlush(ns);
        }
    }

    /// <summary>Schedule a debounced delete of a single entry.</summary>
    public void ScheduleDeleteEntry(string ns, string entryId)
        => ScheduleDeleteEntry(ns, entryId, string.Empty);

    /// <summary>Schedule a debounced delete of one tenant-scoped entry.</summary>
    public void ScheduleDeleteEntry(string ns, string entryId, string tenantId)
    {
        lock (_timerLock)
        {
            if (_disposed) return;

            var key = new EntryStorageKey(NormalizeTenantId(tenantId), entryId);

            if (!_pendingEntryDeletes.TryGetValue(ns, out var deletes))
            {
                deletes = new();
                _pendingEntryDeletes[ns] = deletes;
            }
            deletes.Add(key);

            // Cancel any pending upsert for this entry
            if (_pendingEntryUpserts.TryGetValue(ns, out var upserts))
                upserts.Remove(key);

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
            // The WHOLE capture-and-write runs under the flush gate: a TryFlush interleaving
            // between this callback's capture and its commit could otherwise retain a batch
            // this write supersedes -- or this write could land, stale, over state the flush
            // just vouched for. A failed write feeds the same retention the flush uses instead
            // of being dropped with a log line.
            lock (_flushGate)
            {
                Dictionary<EntryStorageKey, CognitiveEntry>? upserts = null;
                HashSet<EntryStorageKey>? deletes = null;

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

                if (!WriteIncrementalChanges(ns, upserts, deletes))
                    RetainIncrementalBatch(ns, upserts, deletes);
            }
        }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        _incrementalTimers[ns] = selfRef;
    }

    /// <summary>Write batched incremental changes in a single transaction.</summary>
    private bool WriteIncrementalChanges(string ns,
        Dictionary<EntryStorageKey, CognitiveEntry>? upserts, HashSet<EntryStorageKey>? deletes)
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

    public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider)
    {
        lock (_timerLock)
        {
            if (_disposed) return;
            _pendingEdgeTimer?.Dispose();
            _pendingEdgeProvider = dataProvider;
            _pendingEdgeTimer = new Timer(_ =>
            {
                lock (_flushGate)
                {
                    Func<List<GraphEdge>>? provider;
                    lock (_timerLock)
                    {
                        if (HasPendingEntryLevelWork())
                        {
                            _pendingEdgeTimer?.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
                            return;
                        }
                        provider = _pendingEdgeProvider;
                        _pendingEdgeProvider = null;
                        _pendingEdgeTimer?.Dispose();
                        _pendingEdgeTimer = null;

                        // THE CHECK AND THE COMMIT ARE ONE ATOM. Deciding under this lock and
                        // then releasing it to write left a window in which entry-level work
                        // could be queued and then overtaken by this graph-level commit —
                        // after a crash, durable topology naming rows that never became
                        // durable. The lock is held across the write deliberately: a brief
                        // stall on scheduling is cheaper than a durability inversion. Mirrors
                        // the JSON provider's timer paths.

                        if (provider is not null && !WriteGlobalData("edges", provider))
                        {
                            if (_disposed)
                                _logger?.LogError("Pending global-edges write dropped: provider disposed during a failed timer write");
                            else if (_pendingEdgeProvider is null)
                                ScheduleSaveGlobalEdges(provider);
                        }
                    }
                }
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
                lock (_flushGate)
                {
                    Func<List<SemanticCluster>>? provider;
                    lock (_timerLock)
                    {
                        if (HasPendingEntryLevelWork())
                        {
                            _pendingClusterTimer?.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
                            return;
                        }
                        provider = _pendingClusterProvider;
                        _pendingClusterProvider = null;
                        _pendingClusterTimer?.Dispose();
                        _pendingClusterTimer = null;

                        // THE CHECK AND THE COMMIT ARE ONE ATOM. Deciding under this lock and
                        // then releasing it to write left a window in which entry-level work
                        // could be queued and then overtaken by this graph-level commit —
                        // after a crash, durable topology naming rows that never became
                        // durable. The lock is held across the write deliberately: a brief
                        // stall on scheduling is cheaper than a durability inversion. Mirrors
                        // the JSON provider's timer paths.

                        if (provider is not null && !WriteGlobalData("clusters", provider))
                        {
                            if (_disposed)
                                _logger?.LogError("Pending clusters write dropped: provider disposed during a failed timer write");
                            else if (_pendingClusterProvider is null)
                                ScheduleSaveClusters(provider);
                        }
                    }
                }
            }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }


    // CROSS-QUEUE CAUSAL ORDER on the timer path — the graph-level callbacks (edges,
    // clusters, decay) call this under _timerLock and DEFER while any entry-level work is
    // pending or retained: a cluster or edge save references entries by id, and committing
    // it while an entry write sits failed-and-retained, then crashing, reloads durable
    // topology naming entries that never became durable — the exact window TryFlushCore's
    // part 2 refuses, re-opened 500ms later by its own retention without this check.
    // Sustained entry traffic defers graph saves rather than dropping them; a TryFlush
    // always drains both levels in order.
    private bool HasPendingEntryLevelWork()
    {
        if (_pendingNsSaves.Count > 0) return true;
        foreach (var u in _pendingEntryUpserts.Values)
            if (u.Count > 0) return true;
        foreach (var d in _pendingEntryDeletes.Values)
            if (d.Count > 0) return true;
        return false;
    }

    /// <summary>Record-level synchronous collapse-history writes — see <see cref="IStorageProvider"/>.</summary>
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

    /// <summary>
    /// The generation-compared delete — compare and removal one atom inside the same backend
    /// transaction as the unconditional read-modify-writes. See
    /// <see cref="IStorageProvider.DeleteCollapseRecordSync(string, long)"/>.
    /// </summary>
    public CollapseRecordCas DeleteCollapseRecordSync(string collapseId, long onlyIfGeneration)
    {
        var outcome = CollapseRecordCas.StoreFailed;
        // MutateCollapseHistorySync reports "the store now agrees with the mutate outcome" —
        // true for a committed change AND for a no-op refusal (AlreadyAbsent/GenerationMoved,
        // where the mutate callback returns false and nothing is written). False means the
        // store failed to read or commit, which overrides whatever the callback decided.
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

    /// <summary>
    /// The generation-compared upsert — compare and write one atom inside the same backend
    /// transaction. See <see cref="IStorageProvider.UpsertCollapseRecordSync(CollapseRecord, long?)"/>.
    /// </summary>
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
            // NULL expected = must be absent; a NUMBER (0 included -- legacy records carry a
            // real generation 0) must match a RESIDENT record. See the interface contract.
            if (current is null && onlyIfGeneration is not null) { outcome = CollapseRecordCas.AlreadyAbsent; return false; }
            if (current is not null && (onlyIfGeneration is null || current.Generation != onlyIfGeneration.Value)) { outcome = CollapseRecordCas.GenerationMoved; return false; }
            records.RemoveAll(r => r.CollapseId == record.CollapseId);
            records.Add(record);
            outcome = CollapseRecordCas.Applied;
            return true;
        });
        return storeAgrees ? outcome : CollapseRecordCas.StoreFailed;
    }

    /// <summary>
    /// Strict single-record read — a failed or checksum-refuted read REFUSES (false) rather
    /// than reporting the record absent, because <see cref="LoadCollapseHistory"/> deliberately
    /// degrades to an empty list and a verification caller must be able to tell "gone" from
    /// "unreadable".
    /// </summary>
    public bool TryReadCollapseRecord(string collapseId, out CollapseRecord? record)
    {
        record = null;
        if (!TryReadCollapseHistory(out var records))
            return false;
        record = records.FirstOrDefault(r => r.CollapseId == collapseId);
        return true;
    }

    /// <summary>Strict full-set read — see <see cref="IStorageProvider.TryReadCollapseHistory"/>.
    /// Same checksum discipline as the single-record read.</summary>
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

    // Read-modify-write inside ONE BACKEND TRANSACTION on one connection — BEGIN IMMEDIATE, so
    // SQLite itself serializes the whole read-modify-write against every other writer of this
    // database: another connection, another provider instance under a differently-spelled
    // connection string, another OS process. A process-local gate keyed by the connection
    // string could not say that — two equivalent spellings got two gates, and two processes
    // shared none — and the two-connection read-then-write it guarded let both readers see the
    // old set and last-write-wins erase a record. The read is STRICT (throws are refusals,
    // never treated as an empty set — committing over an unreadable set would erase records
    // this caller never saw), the write is direct rather than WriteGlobalData (which swallows
    // backend errors by design while a write-ahead receipt caller has to know), and the report
    // is commit-precise: only a failure BEFORE Commit reports false — the transaction then
    // rolled back and the store is unchanged; a post-commit teardown exception cannot flip a
    // commit the store already accepted into a rollback signal. A lock-contention timeout
    // (SQLITE_BUSY) surfaces as an honest false the same way: nothing was committed.
    private bool MutateCollapseHistorySync(Func<List<CollapseRecord>, bool> mutate)
    {
        lock (_timerLock)
        {
            if (_disposed) return false;
        }

        bool committed = false;
        try
        {
            using var conn = OpenConnection();
            // Not deferred: takes SQLite's write lock at BEGIN, before the read, so the set
            // this transaction reads is the set its commit replaces.
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
                    // Tampered-but-valid JSON under a stale checksum must be REFUSED, not
                    // deserialized and then normalized (re-checksummed) by this commit.
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

            // A mutate that changed nothing (deleting an absent record, refusing a
            // generation compare) commits nothing and reports agreement: rewriting
            // identical content would turn "the store already agrees" into a fallible
            // write for nothing.
            if (!mutate(records))
                return true;

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

        return true;
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
                lock (_flushGate)
                {
                    Func<Dictionary<string, DecayConfig>>? provider;
                    lock (_timerLock)
                    {
                        if (HasPendingEntryLevelWork())
                        {
                            _pendingDecayConfigTimer?.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
                            return;
                        }
                        provider = _pendingDecayConfigProvider;
                        _pendingDecayConfigProvider = null;
                        _pendingDecayConfigTimer?.Dispose();
                        _pendingDecayConfigTimer = null;

                        // THE CHECK AND THE COMMIT ARE ONE ATOM. Deciding under this lock and
                        // then releasing it to write left a window in which entry-level work
                        // could be queued and then overtaken by this graph-level commit —
                        // after a crash, durable topology naming rows that never became
                        // durable. The lock is held across the write deliberately: a brief
                        // stall on scheduling is cheaper than a durability inversion. Mirrors
                        // the JSON provider's timer paths.

                        if (provider is not null)
                        {
                            // The provider runs INSIDE WriteGlobalData's try: hoisted out, a
                            // throwing provider was an unhandled exception on a Timer thread —
                            // a process crash — and the pending save was gone either way.
                            if (!WriteGlobalData("decay_configs", () => provider().Values.ToList()))
                            {
                                if (_disposed)
                                    _logger?.LogError("Pending decay-config write dropped: provider disposed during a failed timer write");
                                else if (_pendingDecayConfigProvider is null)
                                    ScheduleSaveDecayConfigs(provider);
                            }
                        }
                    }
                }
            }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        }
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
            // Delete existing entries for this namespace
            using var deleteCmd = conn.CreateCommand();
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = "DELETE FROM entries WHERE ns = @ns";
            deleteCmd.Parameters.AddWithValue("@ns", ns);
            deleteCmd.ExecuteNonQuery();

            // Insert all entries
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

    private bool WriteGlobalData<T>(string key, Func<T> provider)
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
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save global data '{Key}' to SQLite", key);
            return false;
        }
    }

    /// <summary>Delete all entries in a namespace from the SQLite database.</summary>
    public async Task DeleteNamespaceAsync(string ns)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        await pragma.ExecuteNonQueryAsync();
        using var tx = await conn.BeginTransactionAsync();
        try
        {
            using var cmdEntries = conn.CreateCommand();
            cmdEntries.Transaction = (SqliteTransaction)tx;
            cmdEntries.CommandText = "DELETE FROM entries WHERE ns = @ns";
            cmdEntries.Parameters.AddWithValue("@ns", ns);
            await cmdEntries.ExecuteNonQueryAsync();

            using var cmdHnsw = conn.CreateCommand();
            cmdHnsw.Transaction = (SqliteTransaction)tx;
            cmdHnsw.CommandText = "DELETE FROM global_data WHERE key = @key";
            cmdHnsw.Parameters.AddWithValue("@key", $"hnsw_{ns}");
            await cmdHnsw.ExecuteNonQueryAsync();

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>Delete exactly one tenant + namespace partition.</summary>
    public async Task DeleteNamespaceAsync(string ns, string tenantId)
    {
        tenantId = string.IsNullOrWhiteSpace(tenantId) ? string.Empty : tenantId.Trim();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();
        try
        {
            using (var cmdEntries = conn.CreateCommand())
            {
                cmdEntries.Transaction = (SqliteTransaction)tx;
                cmdEntries.CommandText = "DELETE FROM entries WHERE tenant_id = @tenant AND ns = @ns";
                cmdEntries.Parameters.AddWithValue("@tenant", tenantId);
                cmdEntries.Parameters.AddWithValue("@ns", ns);
                await cmdEntries.ExecuteNonQueryAsync();
            }

            using (var cmdHnsw = conn.CreateCommand())
            {
                cmdHnsw.Transaction = (SqliteTransaction)tx;
                cmdHnsw.CommandText = "DELETE FROM global_data WHERE key = @key";
                cmdHnsw.Parameters.AddWithValue("@key",
                    $"hnsw_{NamespaceStore.PartitionKey(tenantId, ns)}");
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

    // ── Flush + Dispose ──

    public void Flush() => TryFlush();

    // Serializes whole flushes: two concurrent TryFlush calls could otherwise interleave so
    // that the LATER capture commits first and the earlier capture's FAILED batch is then
    // retained and re-flushed over it — a stale resurrection. One flush at a time makes
    // capture order commit order.
    private readonly object _flushGate = new();

    /// <summary>Flush and REPORT — see <see cref="IStorageProvider.TryFlush"/>: true only when
    /// every pending write committed; failed batches are retained and re-scheduled.</summary>
    public bool TryFlush()
    {
        lock (_flushGate)
            return TryFlushCore(refuseWhenDisposed: true);
    }

    private bool TryFlushCore(bool refuseWhenDisposed)
    {
        // A PUBLIC flush after Dispose refuses rather than vouching: the pending slots were
        // drained by Dispose's own final flush, and a true here would claim durability from
        // a provider that no longer accepts work. Dispose's own flush passes false — it set
        // the flag itself. Mirrors PersistenceManager.TryFlushCore.
        if (refuseWhenDisposed)
        {
            lock (_timerLock)
            {
                if (_disposed) return false;
            }
        }

        // A graph slot refilled between our materialization and this drain is deliberately
        // LEFT for its own timer — resurrecting an older snapshot over it would invert capture
        // order. But it is still pending work this flush did not commit, and the contract is
        // "true only when all of them committed", so the vouch must be withheld. The comment
        // below already claimed this; nothing enforced it.
        bool supersededDuringDrain = false;
        List<(string Ns, Func<NamespaceData> Provider)> pendingNs;
        List<(string Ns, Dictionary<EntryStorageKey, CognitiveEntry>? Upserts, HashSet<EntryStorageKey>? Deletes)> pendingIncremental;
        Func<List<GraphEdge>>? edgeProvider;
        Func<List<SemanticCluster>>? clusterProvider;
        Func<Dictionary<string, DecayConfig>>? decayConfigProvider;

        // CROSS-QUEUE CAUSAL ORDER, part 3 — the graph-level payloads are MATERIALIZED
        // BEFORE the entry queues are drained. Entry changes apply to memory and schedule
        // their writes in one atom, so everything a payload materialized HERE can reference
        // was scheduled before the drain below and commits with this flush's entry phase —
        // where a payload materialized at write time (after the drain) could name entries
        // whose writes were scheduled post-drain and remain pending: committed topology
        // naming uncommitted entries across a crash. A provider REPLACED between this read
        // and the drain stays pending (its newer payload commits on its own schedule) and
        // the stale materialization is discarded.
        Func<List<GraphEdge>>? edgeProviderRef;
        Func<List<SemanticCluster>>? clusterProviderRef;
        Func<Dictionary<string, DecayConfig>>? decayProviderRef;
        lock (_timerLock)
        {
            edgeProviderRef = _pendingEdgeProvider;
            clusterProviderRef = _pendingClusterProvider;
            decayProviderRef = _pendingDecayConfigProvider;
        }
        List<GraphEdge>? edgeData = null;
        List<SemanticCluster>? clusterData = null;
        List<DecayConfig>? decayData = null;
        bool edgeMaterialized = false, clusterMaterialized = false, decayMaterialized = false;
        try { if (edgeProviderRef is not null) { edgeData = edgeProviderRef(); edgeMaterialized = true; } } catch (Exception ex) { _logger?.LogError(ex, "Edge payload materialization failed; the pending save is retained"); }
        try { if (clusterProviderRef is not null) { clusterData = clusterProviderRef(); clusterMaterialized = true; } } catch (Exception ex) { _logger?.LogError(ex, "Cluster payload materialization failed; the pending save is retained"); }
        try { if (decayProviderRef is not null) { decayData = decayProviderRef().Values.ToList(); decayMaterialized = true; } } catch (Exception ex) { _logger?.LogError(ex, "Decay-config payload materialization failed; the pending save is retained"); }

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
                Dictionary<EntryStorageKey, CognitiveEntry>? upserts = null;
                HashSet<EntryStorageKey>? deletes = null;

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

            // Drain a graph slot only while it still holds the provider whose payload was
            // materialized above; a newer schedule keeps its slot (and its own timer) and
            // this flush does not vouch for it.
            if (ReferenceEquals(_pendingEdgeProvider, edgeProviderRef))
            {
                edgeProvider = _pendingEdgeProvider;
                _pendingEdgeProvider = null;
                _pendingEdgeTimer?.Dispose();
                _pendingEdgeTimer = null;
            }
            // Something newer is queued there and this flush is not committing
            // it — record that so the report stays honest.
            else { edgeProvider = null; supersededDuringDrain |= _pendingEdgeProvider is not null; }

            if (ReferenceEquals(_pendingClusterProvider, clusterProviderRef))
            {
                clusterProvider = _pendingClusterProvider;
                _pendingClusterProvider = null;
                _pendingClusterTimer?.Dispose();
                _pendingClusterTimer = null;
            }
            // Something newer is queued there and this flush is not committing
            // it — record that so the report stays honest.
            else { clusterProvider = null; supersededDuringDrain |= _pendingClusterProvider is not null; }

            if (ReferenceEquals(_pendingDecayConfigProvider, decayProviderRef))
            {
                decayConfigProvider = _pendingDecayConfigProvider;
                _pendingDecayConfigProvider = null;
                _pendingDecayConfigTimer?.Dispose();
                _pendingDecayConfigTimer = null;
            }
            // Something newer is queued there and this flush is not committing
            // it — record that so the report stays honest.
            else { decayConfigProvider = null; supersededDuringDrain |= _pendingDecayConfigProvider is not null; }
        }

        bool allCommitted = !supersededDuringDrain;

        // CROSS-QUEUE CAUSAL ORDER, part 1 — incremental batches and full-namespace saves
        // for the SAME namespace never both commit: a batch whose namespace also has a
        // pending full save is SUBSUMED and skipped outright — not written, not retained.
        // The full save's provider MATERIALIZES the live state when invoked
        // (NamespaceStore.ScheduleSave builds its snapshot at invoke time — the contract
        // this relies on), and every change a captured batch describes was applied to
        // memory before it was scheduled, so the materialization contains it. Writing the
        // batch anyway would be harmless now but fatal on FAILURE: a retained frozen batch
        // firing after the full save committed would overwrite fresher rows with stale
        // values — and if the full save itself fails, its retained retry re-materializes
        // and still subsumes the dropped batch. Batches with no pending full save commit
        // first, before any full save of OTHER namespaces, keeping entry-level work ahead
        // of the graph-level writes below.
        var fullSaveNs = new HashSet<string>();
        foreach (var (ns, _) in pendingNs)
            fullSaveNs.Add(ns);
        var unsubsumedBatchFailures = new HashSet<string>();
        foreach (var (ns, upserts, deletes) in pendingIncremental)
        {
            // The subsumption skip is bypassed on the DISPOSE flush (refuseWhenDisposed is
            // false only there): with no later retry possible, a skipped batch whose full
            // save then fails is simply lost, where writing it first gives the data an
            // independent commit chance — and batch-before-full-save within ONE flush is
            // the safe order (the full save re-materializes and wins).
            if (refuseWhenDisposed && fullSaveNs.Contains(ns))
                continue;
            if (!WriteIncrementalChanges(ns, upserts, deletes))
            {
                // Deferred verdict: a same-ns full save committing below SUBSUMES this
                // failure (its materialization contains everything the batch held), and the
                // flush must not withhold the graph-level saves over data that is in fact
                // durable.
                unsubsumedBatchFailures.Add(ns);
                RetainIncrementalBatch(ns, upserts, deletes);
            }
        }

        foreach (var (ns, provider) in pendingNs)
        {
            if (!WriteNamespace(ns, provider))
            {
                allCommitted = false;
                // Newer-wins retention: the (reentrant) lock makes the emptiness check and the
                // re-schedule one atom, so a snapshot scheduled since the drain supersedes the
                // failed (older) one instead of being clobbered by it. At dispose, the drop is
                // logged explicitly -- nobody is left to retry.
                lock (_timerLock)
                {
                    if (_disposed)
                        _logger?.LogError("Pending write for namespace '{Namespace}' dropped: provider disposed during a failed flush", ns);
                    else if (!_pendingNsSaves.ContainsKey(ns))
                        ScheduleSave(ns, provider);
                }
            }
            else
            {
                unsubsumedBatchFailures.Remove(ns);
            }
        }
        if (unsubsumedBatchFailures.Count > 0)
            allCommitted = false;

        // CROSS-QUEUE CAUSAL ORDER, part 2 — the graph-level saves (edges, clusters, decay)
        // commit only when every ENTRY-level write above committed. A cluster or edge save
        // references entries by id (a cluster's SummaryEntryId in particular), so committing
        // it over a failed-and-retained entry write, then crashing, reloads durable topology
        // naming entries that never became durable. On failure they are RETAINED unattempted
        // through their own re-schedule paths and the flush reports false.
        if (!allCommitted)
        {
            lock (_timerLock)
            {
                if (_disposed)
                {
                    if (edgeProvider is not null) _logger?.LogError("Pending global-edges write dropped: provider disposed during a failed flush");
                    if (clusterProvider is not null) _logger?.LogError("Pending clusters write dropped: provider disposed during a failed flush");
                    if (decayConfigProvider is not null) _logger?.LogError("Pending decay-config write dropped: provider disposed during a failed flush");
                }
                else
                {
                    if (edgeProvider is not null && _pendingEdgeProvider is null) ScheduleSaveGlobalEdges(edgeProvider);
                    if (clusterProvider is not null && _pendingClusterProvider is null) ScheduleSaveClusters(clusterProvider);
                    if (decayConfigProvider is not null && _pendingDecayConfigProvider is null) ScheduleSaveDecayConfigs(decayConfigProvider);
                }
            }
            return false;
        }

        if (edgeProvider is not null && (!edgeMaterialized || !WriteGlobalData("edges", () => edgeData!)))
        {
            allCommitted = false;
            lock (_timerLock)
            {
                if (_disposed)
                    _logger?.LogError("Pending global-edges write dropped: provider disposed during a failed flush");
                else if (_pendingEdgeProvider is null)
                    ScheduleSaveGlobalEdges(edgeProvider);
            }
        }

        if (clusterProvider is not null && (!clusterMaterialized || !WriteGlobalData("clusters", () => clusterData!)))
        {
            allCommitted = false;
            lock (_timerLock)
            {
                if (_disposed)
                    _logger?.LogError("Pending clusters write dropped: provider disposed during a failed flush");
                else if (_pendingClusterProvider is null)
                    ScheduleSaveClusters(clusterProvider);
            }
        }

        if (decayConfigProvider is not null)
        {
            // The materialized payload commits (see part 3 above); a materialization
            // failure is a failed write — retained like one.
            if (!decayMaterialized || !WriteGlobalData("decay_configs", () => decayData!))
            {
                allCommitted = false;
                lock (_timerLock)
                {
                    if (_disposed)
                        _logger?.LogError("Pending decay-config write dropped: provider disposed during a failed flush");
                    else if (_pendingDecayConfigProvider is null)
                        ScheduleSaveDecayConfigs(decayConfigProvider);
                }
            }
        }

        // FINAL LINEARIZATION CHECKPOINT. Schedule calls publish under _timerLock, so at this
        // instant every write queued before the checkpoint is either committed above or visible
        // here. In particular, an entry queued while a blocked backend write was in progress may
        // not hide behind a successful report; it remains pending and makes this flush return
        // false. Work scheduled after this lock is released is ordered after the checkpoint.
        lock (_timerLock)
        {
            if (HasPendingEntryLevelWork()
                || _pendingEdgeProvider is not null
                || _pendingClusterProvider is not null
                || _pendingDecayConfigProvider is not null)
            {
                allCommitted = false;
            }
        }
        return allCommitted;
    }

    // Put a failed incremental batch BACK into the pending queues (newer pending entries win —
    // they were written after the failed batch was captured) and re-arm the debounce, so a
    // failed flush retains the writes for a later attempt instead of dropping them.
    private void RetainIncrementalBatch(string ns,
        Dictionary<EntryStorageKey, CognitiveEntry>? upserts, HashSet<EntryStorageKey>? deletes)
    {
        lock (_timerLock)
        {
            if (_disposed)
            {
                _logger?.LogError("Pending incremental batch for namespace '{Namespace}' dropped: provider disposed during a failed flush", ns);
                return;
            }

            if (upserts is not null)
            {
                if (!_pendingEntryUpserts.TryGetValue(ns, out var u))
                    _pendingEntryUpserts[ns] = u = new();
                foreach (var (key, entry) in upserts)
                    if (!u.ContainsKey(key) && !(_pendingEntryDeletes.TryGetValue(ns, out var dd) && dd.Contains(key)))
                        u[key] = entry;
            }
            if (deletes is not null)
            {
                if (!_pendingEntryDeletes.TryGetValue(ns, out var d))
                    _pendingEntryDeletes[ns] = d = new();
                foreach (var key in deletes)
                    if (!(_pendingEntryUpserts.TryGetValue(ns, out var uu) && uu.ContainsKey(key)))
                        d.Add(key);
            }
            ScheduleIncrementalFlush(ns);
        }
    }

    public void Dispose()
    {
        lock (_timerLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // The disposing thread's own final flush — bypasses the post-dispose refusal it
        // itself armed; see TryFlushCore.
        lock (_flushGate)
            TryFlushCore(refuseWhenDisposed: false);
        CheckpointWal();
    }

    /// <summary>
    /// Fold the write-ahead log back into the main database and truncate it, so a
    /// shutdown leaves a self-contained file rather than a database plus a multi-megabyte
    /// <c>-wal</c> sidecar.
    ///
    /// Why this is needed at all: SQLite checkpoints and removes the WAL when the *last*
    /// connection closes, and this provider opens a connection per operation — so in
    /// principle that happens constantly. In practice <c>Microsoft.Data.Sqlite</c> pools
    /// connections by default, so the native handle stays open and the "last close" never
    /// arrives. Measured: after 4,000 scheduled upserts the WAL sits at ~4 MB and is still
    /// ~4 MB after <see cref="Dispose"/> without this call.
    ///
    /// Note this is about tidiness on shutdown, not durability or unbounded growth. During
    /// normal operation SQLite's automatic checkpoint (every 1,000 pages) holds the WAL to a
    /// steady ~4 MB no matter how many writes follow — measured flat from 1,000 through
    /// 4,000 writes — so no periodic checkpoint policy is warranted. Committed data is never
    /// at risk either way: an un-checkpointed WAL is replayed when the database is next
    /// opened. That is also why failure here is logged rather than thrown.
    /// </summary>
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
            // Never let shutdown fail over housekeeping. The WAL is replayed on next open.
            _logger?.LogWarning(ex,
                "WAL checkpoint on shutdown failed; the write-ahead log will be replayed when the database is next opened.");
        }
    }
}
