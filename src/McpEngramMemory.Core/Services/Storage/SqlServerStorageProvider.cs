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

    private const int CurrentSchemaVersion = 3;
    private static readonly Regex SchemaNameRegex = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly record struct EntryStorageKey(string TenantId, string EntryId);

    private readonly string _connectionString;
    private readonly string _schema;
    private readonly string _schemaQuoted;
    private readonly object _timerLock = new();
    private readonly TimeSpan _debounceDelay;
    private readonly ILogger<SqlServerStorageProvider>? _logger;
    private bool _disposed;

    private readonly Dictionary<string, (Timer Timer, Func<NamespaceData> DataProvider)> _pendingNsSaves = new();

    private readonly Dictionary<string, Dictionary<EntryStorageKey, CognitiveEntry>> _pendingEntryUpserts = new();
    private readonly Dictionary<string, HashSet<EntryStorageKey>> _pendingEntryDeletes = new();
    private readonly Dictionary<string, Timer> _incrementalTimers = new();

    private Timer? _pendingEdgeTimer;
    private Func<List<GraphEdge>>? _pendingEdgeProvider;
    private Timer? _pendingClusterTimer;
    private Func<List<SemanticCluster>>? _pendingClusterProvider;
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

        // Canonicalized store identity: the server, database and schema this provider actually
        // writes — the same for any two instances whose connection strings are equivalent
        // spellings, with the common local-host aliases (".", "(local)", "localhost",
        // "127.0.0.1", "::1") and the "tcp:" protocol prefix canonicalized so they cannot
        // split the in-process gate. Best-effort beyond that (a DNS alias is invisible here);
        // the durable record layer stays serialized by the backend transaction regardless. A
        // builder parse failure falls back to the raw string, which degrades to per-spelling
        // identity rather than failing construction.
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
            // Folded to upper BEFORE the alias switch so casing ("(Local)") cannot slip past,
            // and including the bracketed IPv6 loopback spelling connection strings use.
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
    }

    /// <summary>See <see cref="IStorageProvider.StoreIdentity"/>.</summary>
    public string StoreIdentity { get; }

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
            RunMigrations(conn, currentVersion);
    }

    /// <summary>
    /// Applies all pending forward migrations inside a single transaction, then records the
    /// new schema version. The whole thing is version-gated (only runs when the stored version
    /// is behind), so re-constructing the provider against an already-migrated database is a
    /// no-op — i.e. migration is idempotent. Any failure rolls back atomically, leaving the
    /// database at its previous version.
    /// </summary>
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

    /// <summary>
    /// v2→v3: first-class tenant isolation. Adds a <c>tenant_id NVARCHAR(64) NOT NULL DEFAULT ''</c>
    /// column (existing rows collapse to the legacy empty-string tenant) and re-roots the primary
    /// key from (ns, id) to (tenant_id, ns, id). Each step is individually guarded so a partial
    /// re-run is safe; the enclosing transaction makes the whole migration atomic. The reverse of
    /// this migration is scripted in <c>scripts/migrations/sqlserver_v3_tenant_id.down.sql</c>.
    /// </summary>
    private void MigrateToV3(SqlConnection conn, SqlTransaction transaction)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            -- 1. Add the tenant column, defaulting existing rows to the legacy '' tenant.
            IF NOT EXISTS (SELECT 1 FROM sys.columns
                           WHERE object_id = OBJECT_ID(N'{_schema}.entries') AND name = 'tenant_id')
                ALTER TABLE {_schemaQuoted}.entries
                    ADD tenant_id NVARCHAR(64) NOT NULL
                        CONSTRAINT DF_engram_entries_tenant DEFAULT('');

            -- 2. Re-root the primary key onto (tenant_id, ns, id).
            IF EXISTS (SELECT 1 FROM sys.key_constraints
                       WHERE name = 'PK_engram_entries'
                         AND parent_object_id = OBJECT_ID(N'{_schema}.entries'))
                ALTER TABLE {_schemaQuoted}.entries DROP CONSTRAINT PK_engram_entries;

            IF NOT EXISTS (SELECT 1 FROM sys.key_constraints
                           WHERE name = 'PK_engram_entries'
                             AND parent_object_id = OBJECT_ID(N'{_schema}.entries'))
                ALTER TABLE {_schemaQuoted}.entries
                    ADD CONSTRAINT PK_engram_entries PRIMARY KEY (tenant_id, ns, id);

            -- 3. Tenant-aware covering index for the T2-05 tenant-scoped queries.
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
    /// passes instead. A transient failover, which is routine on this backend, is exactly the case
    /// that must not be recorded as an empty store.
    /// </summary>
    /// <exception cref="NamespaceEnumerationException">The listing query failed.</exception>
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
            // Logged here with the full backend detail and rethrown without it: the wrapper's
            // message reaches callers, and a SqlException's — which can name the server and the
            // schema — does not.
            _logger?.LogWarning(ex, "Error listing namespaces from SQL Server");
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
            var key = new EntryStorageKey(entry.TenantId, entry.Id);
            upserts[key] = entry;

            if (_pendingEntryDeletes.TryGetValue(ns, out var deletes))
                deletes.Remove(key);

            ScheduleIncrementalFlush(ns);
        }
    }

    public void ScheduleDeleteEntry(string ns, string entryId)
        => ScheduleDeleteEntry(ns, entryId, "");

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

            if (_pendingEntryUpserts.TryGetValue(ns, out var upserts))
                upserts.Remove(key);

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
                    if (_incrementalTimers.TryGetValue(ns, out var current) && ReferenceEquals(current, selfRef))
                        _incrementalTimers.Remove(ns);
                }

                if (!WriteIncrementalChanges(ns, upserts, deletes))
                    RetainIncrementalBatch(ns, upserts, deletes);
            }
        }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        _incrementalTimers[ns] = selfRef;
    }

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
                    // Delete on the full tenant-rooted key so a delete in one tenant can never
                    // remove another tenant's row that shares (ns, id) under the v3 PK.
                    deleteCmd.CommandText = $"DELETE FROM {_schemaQuoted}.entries WHERE tenant_id = @tenant AND ns = @ns AND id = @id";
                    var delTenantParam = deleteCmd.Parameters.Add("@tenant", System.Data.SqlDbType.NVarChar, 64);
                    var delNsParam = deleteCmd.Parameters.Add("@ns", System.Data.SqlDbType.NVarChar, 450);
                    var delIdParam = deleteCmd.Parameters.Add("@id", System.Data.SqlDbType.NVarChar, 450);
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
                    upsertCmd.CommandText = BuildEntryUpsertSql();
                    var idParam = upsertCmd.Parameters.Add("@id", System.Data.SqlDbType.NVarChar, 450);
                    var nsParam = upsertCmd.Parameters.Add("@ns", System.Data.SqlDbType.NVarChar, 450);
                    var tenantParam = upsertCmd.Parameters.Add("@tenant", System.Data.SqlDbType.NVarChar, 64);
                    var jsonParam = upsertCmd.Parameters.Add("@json", System.Data.SqlDbType.NVarChar, -1);
                    var checksumParam = upsertCmd.Parameters.Add("@checksum", System.Data.SqlDbType.Char, 64);
                    var stateParam = upsertCmd.Parameters.Add("@state", System.Data.SqlDbType.NVarChar, 32);

                    foreach (var entry in upserts.Values)
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
    /// <see cref="IStorageProvider.DeleteCollapseRecordSync(string, long)"/> and the SQLite
    /// twin for the outcome plumbing.
    /// </summary>
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
    /// Strict single-record read — a failed read REFUSES (false) rather than reporting the
    /// record absent, because <see cref="LoadCollapseHistory"/> deliberately degrades to an
    /// empty list and a verification caller must be able to tell "gone" from "unreadable".
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

    // Read-modify-write inside ONE BACKEND TRANSACTION on one connection, with the read taking
    // UPDLOCK + HOLDLOCK on the history row (a key-range lock when the row does not exist yet),
    // so SQL SERVER serializes the whole read-modify-write against every other writer of this
    // history — another connection, another provider instance under an equivalent-but-
    // differently-spelled connection string, another OS process. A process-local lock keyed by
    // the raw connection string could not say that, and the two-connection read-then-write it
    // guarded let two writers both read the old set and last-write-wins erase a record. Strict
    // read (an unreadable set refuses the call rather than masquerading as empty), direct write
    // (WriteGlobalData swallows errors by design), commit-precise reporting: only a failure
    // BEFORE Commit is a refusal — the transaction then rolled back and the store is unchanged
    // (a deadlock-victim exception lands here too, as an honest false); a post-commit teardown
    // exception cannot flip a commit the store already accepted.
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
                reader.Close();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Collapse-history record write refused: the current set could not be read");
                return false;
            }

            // No-op mutate: commit nothing, report agreement — see the SQLite twin.
            if (!mutate(records))
                return true;

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
            _logger?.LogError(ex, "Failed to save namespace '{Namespace}' to SQL Server", ns);
            return false;
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
                INSERT INTO {_schemaQuoted}.entries (id, ns, tenant_id, json_data, checksum, lifecycle_state)
                VALUES (@id, @ns, @tenant, @json, @checksum, @state)
                """;
            var idParam = insertCmd.Parameters.Add("@id", System.Data.SqlDbType.NVarChar, 450);
            var nsParam = insertCmd.Parameters.Add("@ns", System.Data.SqlDbType.NVarChar, 450);
            var tenantParam = insertCmd.Parameters.Add("@tenant", System.Data.SqlDbType.NVarChar, 64);
            var jsonParam = insertCmd.Parameters.Add("@json", System.Data.SqlDbType.NVarChar, -1);
            var checksumParam = insertCmd.Parameters.Add("@checksum", System.Data.SqlDbType.Char, 64);
            var stateParam = insertCmd.Parameters.Add("@state", System.Data.SqlDbType.NVarChar, 32);

            foreach (var entry in data.Entries)
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

    /// <summary>Delete exactly one tenant + namespace partition.</summary>
    public async Task DeleteNamespaceAsync(string ns, string tenantId)
    {
        tenantId = string.IsNullOrWhiteSpace(tenantId) ? string.Empty : tenantId.Trim();
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            using (var cmdEntries = conn.CreateCommand())
            {
                cmdEntries.Transaction = tx;
                cmdEntries.CommandText =
                    $"DELETE FROM {_schemaQuoted}.entries WHERE tenant_id = @tenant AND ns = @ns";
                cmdEntries.Parameters.AddWithValue("@tenant", tenantId);
                cmdEntries.Parameters.AddWithValue("@ns", ns);
                await cmdEntries.ExecuteNonQueryAsync();
            }

            using (var cmdHnsw = conn.CreateCommand())
            {
                cmdHnsw.Transaction = tx;
                cmdHnsw.CommandText = $"DELETE FROM {_schemaQuoted}.global_data WHERE [key] = @key";
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

        // FINAL LINEARIZATION CHECKPOINT — see the SQLite twin. Every schedule publishes under
        // _timerLock, so no entry queued during this flush's backend writes can be hidden by a
        // successful report. Work scheduled after the checkpoint is ordered after the flush.
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

    // Put a failed incremental batch BACK into the pending queues (newer pending entries win)
    // and re-arm the debounce — see the SQLite twin.
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
    }
}
