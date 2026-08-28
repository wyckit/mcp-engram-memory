using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Data.Sqlite;

namespace McpEngramMemory.Tests;

public class SqliteStorageProviderTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly SqliteStorageProvider _provider;

    public SqliteStorageProviderTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"sqlite_test_{Guid.NewGuid():N}", "memory.db");
        _provider = new SqliteStorageProvider(_testDbPath, debounceMs: 10);
    }

    public void Dispose()
    {
        _provider.Dispose();
        // Clear SQLite connection pool to release file locks before cleanup
        SqliteConnection.ClearAllPools();
        var dir = Path.GetDirectoryName(_testDbPath);
        if (dir is not null && Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    /// <summary>
    /// Disposing the provider must fold the write-ahead log back into the database and
    /// truncate it, leaving a self-contained file rather than a multi-megabyte `-wal`
    /// sidecar.
    ///
    /// This does not happen for free. SQLite removes the WAL when the *last* connection
    /// closes, and this provider opens one per operation — but Microsoft.Data.Sqlite pools
    /// connections by default, so the native handle stays open and that last close never
    /// arrives. Without the explicit `wal_checkpoint(TRUNCATE)` in Dispose, the WAL measured
    /// ~4 MB after 4,000 upserts and stayed ~4 MB after disposal.
    /// </summary>
    [Fact]
    public void Dispose_CheckpointsAndTruncatesWal()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sqlite_wal_{Guid.NewGuid():N}", "memory.db");
        var walPath = dbPath + "-wal";
        try
        {
            var provider = new SqliteStorageProvider(dbPath, debounceMs: 10);

            // Order matters. The pin connection is opened BEFORE the writes, not after.
            //
            // SQLite removes the WAL when the last connection closes, and this provider opens
            // one per operation — so with no other connection held, whether a -wal file still
            // exists after the writes depends on pool timing. Opening the pin afterwards does
            // not help either: if the WAL was already reclaimed, merely connecting does not
            // recreate it, which is how this failed a second time. Holding a connection open
            // across the writes means the WAL cannot be removed at all.
            //
            // That determinism is what gives the post-dispose assertion meaning: with no WAL
            // to reclaim, it would pass whether or not the checkpoint ever ran.
            //
            // Two things are needed to make "held open" actually mean what this test assumes.
            //
            // Pooling=False: Microsoft.Data.Sqlite pools by connection string, and this pin string
            // would otherwise be byte-identical to the provider's, making the pin a loan from the
            // same pool rather than a handle of its own. ClearAllPools() is process-wide static
            // state that five other test classes call, and with maxParallelThreads=4 one of them
            // firing mid-test could reclaim it.
            //
            // An open READ TRANSACTION: this is the part the original test was missing, and the
            // actual cause of the flake. An idle connection stops SQLite *deleting* the WAL; it
            // does not stop it being *checkpointed and truncated*, because an idle connection is
            // not a reader. Measured at the moment of failure: journal_mode=wal, memory.db 397 KB,
            // memory.db-wal 0 B — the frames had already been copied into the main database and
            // the log reset. A reader holding a snapshot from before the writes cannot have the
            // checkpointer advance past it, so the frames must stay in the log.
            using var pin = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            pin.Open();

            // Raw BEGIN DEFERRED rather than SqliteConnection.BeginTransaction(): that API defaults
            // to IsolationLevel.Serializable, which issues BEGIN IMMEDIATE and takes a RESERVED
            // write lock — the provider's own writes below then fail with "database is locked".
            // A deferred transaction whose first statement is a SELECT holds only a read lock,
            // which in WAL mode does not block writers but does pin the snapshot.
            using (var begin = pin.CreateCommand())
            {
                begin.CommandText = "BEGIN DEFERRED;";
                begin.ExecuteNonQuery();
            }
            using (var snapshot = pin.CreateCommand())
            {
                // Deferred takes no lock until a statement runs, so actually read something.
                snapshot.CommandText = "SELECT COUNT(*) FROM entries;";
                snapshot.ExecuteScalar();
            }

            // Enough rows to push the WAL past SQLite's 1,000-page auto-checkpoint so there
            // is something substantial left to reclaim.
            var entries = new List<CognitiveEntry>();
            for (int i = 0; i < 400; i++)
                entries.Add(new CognitiveEntry($"e{i}", new float[64], "walns", $"entry body {i}"));
            provider.SaveNamespaceSync("walns", new NamespaceData { Entries = entries });

            // Failure here would mean the reader snapshot above did not hold the log, so report
            // what SQLite actually left on disk rather than just that a bool was false.
            static string Listing(string path) => string.Join(", ",
                Directory.GetFiles(Path.GetDirectoryName(path)!)
                    .Select(f => $"{Path.GetFileName(f)}({new FileInfo(f).Length}B)"));

            Assert.True(File.Exists(walPath),
                $"expected a -wal file while a reader snapshot is held open. dir=[{Listing(dbPath)}]");
            var walBeforeDispose = new FileInfo(walPath).Length;
            Assert.True(walBeforeDispose > 0,
                $"expected a non-empty WAL before dispose — the log was checkpointed despite an "
                + $"open reader. dir=[{Listing(dbPath)}]");

            // Release the reader and the pin so the checkpoint can truncate:
            // wal_checkpoint(TRUNCATE) needs no other readers.
            using (var end = pin.CreateCommand())
            {
                end.CommandText = "ROLLBACK;";
                end.ExecuteNonQuery();
            }
            pin.Close();

            provider.Dispose();

            var walAfterDispose = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
            Assert.True(walAfterDispose == 0,
                $"WAL should be truncated on dispose, but was {walAfterDispose} bytes (was {walBeforeDispose} before).");

            // The data must survive the checkpoint - it moved into the main db, not away.
            var reopened = new SqliteStorageProvider(dbPath, debounceMs: 10);
            Assert.Equal(400, reopened.LoadNamespace("walns").Entries.Count);
            reopened.Dispose();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var dir = Path.GetDirectoryName(dbPath);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoadNamespace_Empty_ReturnsEmptyData()
    {
        var data = _provider.LoadNamespace("nonexistent");
        Assert.Empty(data.Entries);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var entry = new CognitiveEntry("test-1", new[] { 1f, 2f, 3f }, "myns", "hello world");
        var data = new NamespaceData { Entries = new List<CognitiveEntry> { entry } };

        _provider.SaveNamespaceSync("myns", data);
        var loaded = _provider.LoadNamespace("myns");

        Assert.Single(loaded.Entries);
        Assert.Equal("test-1", loaded.Entries[0].Id);
        Assert.Equal("hello world", loaded.Entries[0].Text);
        Assert.Equal(new[] { 1f, 2f, 3f }, loaded.Entries[0].Vector);
    }

    [Fact]
    public void SaveNamespaceSync_Overwrites()
    {
        var entry1 = new CognitiveEntry("a", new[] { 1f, 0f }, "ns", "first");
        _provider.SaveNamespaceSync("ns", new NamespaceData { Entries = [entry1] });

        var entry2 = new CognitiveEntry("b", new[] { 0f, 1f }, "ns", "second");
        _provider.SaveNamespaceSync("ns", new NamespaceData { Entries = [entry2] });

        var loaded = _provider.LoadNamespace("ns");
        Assert.Single(loaded.Entries);
        Assert.Equal("b", loaded.Entries[0].Id);
    }

    [Fact]
    public void GetPersistedNamespaces_ListsNamespaces()
    {
        _provider.SaveNamespaceSync("alpha", new NamespaceData { Entries = [new CognitiveEntry("a", new[] { 1f }, "alpha")] });
        _provider.SaveNamespaceSync("beta", new NamespaceData { Entries = [new CognitiveEntry("b", new[] { 1f }, "beta")] });

        var namespaces = _provider.GetPersistedNamespaces();
        Assert.Contains("alpha", namespaces);
        Assert.Contains("beta", namespaces);
    }

    [Fact]
    public void GetPersistedNamespaces_ExcludesUnderscorePrefix()
    {
        _provider.SaveNamespaceSync("_system", new NamespaceData { Entries = [new CognitiveEntry("s", new[] { 1f }, "_system")] });
        _provider.SaveNamespaceSync("normal", new NamespaceData { Entries = [new CognitiveEntry("n", new[] { 1f }, "normal")] });

        var namespaces = _provider.GetPersistedNamespaces();
        Assert.Contains("normal", namespaces);
        Assert.DoesNotContain("_system", namespaces);
    }

    [Fact]
    public void DebouncedSave_FlushesOnDispose()
    {
        var entry = new CognitiveEntry("d1", new[] { 1f, 2f }, "debounce-ns", "debounced");
        var data = new NamespaceData { Entries = [entry] };
        _provider.ScheduleSave("debounce-ns", () => data);

        // Flush forces pending writes
        _provider.Flush();

        var loaded = _provider.LoadNamespace("debounce-ns");
        Assert.Single(loaded.Entries);
        Assert.Equal("d1", loaded.Entries[0].Id);
    }

    [Fact]
    public void GlobalEdges_SaveAndLoad()
    {
        var edges = new List<GraphEdge>
        {
            new("a", "b", "cross_reference"),
            new("b", "c", "depends_on")
        };

        _provider.ScheduleSaveGlobalEdges(() => edges);
        _provider.Flush();

        var loaded = _provider.LoadGlobalEdges();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("a", loaded[0].SourceId);
        Assert.Equal("depends_on", loaded[1].Relation);
    }

    [Fact]
    public void Clusters_SaveAndLoad()
    {
        var clusters = new List<SemanticCluster>
        {
            new("c1", "test", new List<string> { "m1", "m2" }, "test cluster")
        };

        _provider.ScheduleSaveClusters(() => clusters);
        _provider.Flush();

        var loaded = _provider.LoadClusters();
        Assert.Single(loaded);
        Assert.Equal("c1", loaded[0].ClusterId);
    }

    [Fact]
    public void CollapseHistory_SaveAndLoad()
    {
        var records = new List<CollapseRecord>
        {
            new("collapse-1", "c1", "summary-1", "test",
                new List<string> { "orig-1", "orig-2" },
                new Dictionary<string, string> { ["orig-1"] = "ltm", ["orig-2"] = "ltm" },
                DateTimeOffset.UtcNow)
        };

        _provider.ScheduleSaveCollapseHistory(() => records);
        _provider.Flush();

        var loaded = _provider.LoadCollapseHistory();
        Assert.Single(loaded);
        Assert.Equal("c1", loaded[0].ClusterId);
    }

    [Fact]
    public void DecayConfigs_SaveAndLoad()
    {
        var configs = new Dictionary<string, DecayConfig>
        {
            ["test"] = new("test", decayRate: 0.5f)
        };

        _provider.ScheduleSaveDecayConfigs(() => configs);
        _provider.Flush();

        var loaded = _provider.LoadDecayConfigs();
        Assert.Single(loaded);
        Assert.Equal(0.5f, loaded["test"].DecayRate);
    }

    [Fact]
    public void IntegrationWithCognitiveIndex_BasicOperations()
    {
        using var index = new CognitiveIndex(_provider);

        var entry = new CognitiveEntry("idx-1", new[] { 1f, 0f }, "test", "hello");
        index.Upsert(entry);

        var retrieved = index.Get("idx-1", "test", tenantId: "");
        Assert.NotNull(retrieved);
        Assert.Equal("hello", retrieved.Text);
    }

    [Fact]
    public void IntegrationWithCognitiveIndex_SearchWorks()
    {
        using var index = new CognitiveIndex(_provider);

        index.Upsert(new CognitiveEntry("s1", new[] { 1f, 0f }, "test", "alpha"));
        index.Upsert(new CognitiveEntry("s2", new[] { 0f, 1f }, "test", "beta"));

        var results = index.Search(new[] { 1f, 0f }, "test", tenantId: "", k: 1);
        Assert.Single(results);
        Assert.Equal("s1", results[0].Id);
    }

    [Fact]
    public void IntegrationWithCognitiveIndex_PersistsAcrossInstances()
    {
        // Save with first instance
        using (var index = new CognitiveIndex(_provider))
        {
            index.Upsert(new CognitiveEntry("p1", new[] { 1f, 0f }, "persist", "persisted entry"));
            _provider.Flush();
        }

        // Load with new provider pointing to same DB
        using var provider2 = new SqliteStorageProvider(_testDbPath, debounceMs: 10);
        using var index2 = new CognitiveIndex(provider2);

        var entry = index2.Get("p1", "persist", tenantId: "");
        Assert.NotNull(entry);
        Assert.Equal("persisted entry", entry.Text);
    }

    [Fact]
    public void MultipleEntries_PreservesAll()
    {
        var entries = Enumerable.Range(1, 20).Select(i =>
            new CognitiveEntry($"multi-{i}", new[] { (float)i, 0f }, "multi", $"entry {i}")).ToList();

        _provider.SaveNamespaceSync("multi", new NamespaceData { Entries = entries });
        var loaded = _provider.LoadNamespace("multi");

        Assert.Equal(20, loaded.Entries.Count);
    }

    [Fact]
    public void StorageVersion_IsSet()
    {
        var entry = new CognitiveEntry("v1", new[] { 1f }, "ver", "versioned");
        _provider.SaveNamespaceSync("ver", new NamespaceData { Entries = [entry] });

        var loaded = _provider.LoadNamespace("ver");
        Assert.Equal(3, loaded.StorageVersion);
    }

    // ── Schema Migration ──

    [Fact]
    public void FreshDatabase_MigratesToCurrentVersion()
    {
        // The provider constructor runs InitializeSchema which should migrate to v3.
        using var conn = new SqliteConnection($"Data Source={_testDbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Verify schema version is set
        cmd.CommandText = "SELECT version FROM schema_version LIMIT 1";
        var version = Convert.ToInt32(cmd.ExecuteScalar()!);
        Assert.Equal(3, version);

        // Verify lifecycle_state column exists
        cmd.CommandText = "PRAGMA table_info(entries)";
        var columns = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        Assert.Contains("lifecycle_state", columns);
        Assert.Contains("tenant_id", columns);
    }

    [Fact]
    public void MigrateV1ToV2_BackfillsLifecycleState()
    {
        // Create a v1 database manually
        var v1DbPath = Path.Combine(Path.GetTempPath(), $"sqlite_v1_test_{Guid.NewGuid():N}", "memory.db");
        var v1Dir = Path.GetDirectoryName(v1DbPath)!;
        Directory.CreateDirectory(v1Dir);

        try
        {
            // Set up a v1 schema manually (no lifecycle_state column)
            using (var conn = new SqliteConnection($"Data Source={v1DbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    PRAGMA journal_mode=WAL;
                    CREATE TABLE schema_version (version INTEGER NOT NULL);
                    INSERT INTO schema_version (version) VALUES (1);
                    CREATE TABLE entries (
                        id TEXT NOT NULL, ns TEXT NOT NULL,
                        json_data TEXT NOT NULL, checksum TEXT NOT NULL,
                        PRIMARY KEY (ns, id)
                    );
                    CREATE TABLE global_data (
                        key TEXT PRIMARY KEY, json_data TEXT NOT NULL, checksum TEXT NOT NULL
                    );
                    CREATE INDEX idx_entries_ns ON entries(ns);
                    """;
                cmd.ExecuteNonQuery();

                // Build a proper v1 entry: use the real serializer so checksum is valid
                var entry = new CognitiveEntry("m1", new[] { 1f }, "test", "hello", lifecycleState: "ltm");
                var json = System.Text.Json.JsonSerializer.Serialize(entry, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    Converters = { new McpEngramMemory.Core.Models.FloatArrayBase64Converter() }
                });
                var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(json)));

                cmd.CommandText = "INSERT INTO entries (id, ns, json_data, checksum) VALUES ('m1', 'test', @json, @checksum)";
                cmd.Parameters.AddWithValue("@json", json);
                cmd.Parameters.AddWithValue("@checksum", checksum);
                cmd.ExecuteNonQuery();
            }

            // Open with current provider — should trigger the full v1→v3 migration chain.
            using var provider = new SqliteStorageProvider(v1DbPath, debounceMs: 10);

            // Verify version upgraded
            using var conn2 = new SqliteConnection($"Data Source={v1DbPath}");
            conn2.Open();
            using var cmd2 = conn2.CreateCommand();
            cmd2.CommandText = "SELECT version FROM schema_version LIMIT 1";
            Assert.Equal(3, Convert.ToInt32(cmd2.ExecuteScalar()!));

            // Verify lifecycle_state was backfilled from JSON
            cmd2.CommandText = "SELECT lifecycle_state FROM entries WHERE id = 'm1'";
            Assert.Equal("ltm", cmd2.ExecuteScalar()!.ToString());

            // Verify data is still loadable
            var data = provider.LoadNamespace("test");
            Assert.Single(data.Entries);
            Assert.Equal("ltm", data.Entries[0].LifecycleState);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(v1Dir))
                Directory.Delete(v1Dir, true);
        }
    }

    [Fact]
    public void WriteAndLoad_LifecycleStateColumnPopulated()
    {
        var entry = new CognitiveEntry("ls1", new[] { 1f, 2f }, "ns", "test", lifecycleState: "ltm");
        _provider.SaveNamespaceSync("ns", new NamespaceData { Entries = [entry] });

        // Verify the column was populated directly
        using var conn = new SqliteConnection($"Data Source={_testDbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT lifecycle_state FROM entries WHERE id = 'ls1'";
        Assert.Equal("ltm", cmd.ExecuteScalar()!.ToString());
    }

    [Fact]
    public void IncrementalUpsert_LifecycleStateColumnPopulated()
    {
        var entry = new CognitiveEntry("inc1", new[] { 1f, 2f }, "ns", "incremental", lifecycleState: "archived");
        _provider.ScheduleUpsertEntry("ns", entry);
        _provider.Flush();

        using var conn = new SqliteConnection($"Data Source={_testDbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT lifecycle_state FROM entries WHERE id = 'inc1'";
        Assert.Equal("archived", cmd.ExecuteScalar()!.ToString());
    }

    [Fact]
    public void IncrementalWrites_SameNamespaceAndIdAcrossTenants_RoundTripAndDeleteIndependently()
    {
        const string ns = "tenant-shared";
        const string id = "same-id";

        _provider.ScheduleUpsertEntry(ns,
            new CognitiveEntry(id, new[] { 1f }, ns, "tenant a", tenantId: "tenant-a"));
        _provider.ScheduleUpsertEntry(ns,
            new CognitiveEntry(id, new[] { 2f }, ns, "tenant b", tenantId: "tenant-b"));
        _provider.Flush();

        var initiallyLoaded = _provider.LoadNamespace(ns).Entries;
        Assert.Equal(2, initiallyLoaded.Count);
        Assert.Contains(initiallyLoaded, entry => entry.TenantId == "tenant-a" && entry.Text == "tenant a");
        Assert.Contains(initiallyLoaded, entry => entry.TenantId == "tenant-b" && entry.Text == "tenant b");

        using (var reopened = new SqliteStorageProvider(_testDbPath, debounceMs: 10))
        {
            var reloaded = reopened.LoadNamespace(ns).Entries;
            Assert.Equal(2, reloaded.Count);
            Assert.Contains(reloaded, entry => entry.TenantId == "tenant-a" && entry.Id == id);
            Assert.Contains(reloaded, entry => entry.TenantId == "tenant-b" && entry.Id == id);

            reopened.ScheduleDeleteEntry(ns, id, "tenant-a");
            reopened.Flush();
        }

        var afterTenantDelete = _provider.LoadNamespace(ns).Entries;
        var survivor = Assert.Single(afterTenantDelete);
        Assert.Equal("tenant-b", survivor.TenantId);
        Assert.Equal(id, survivor.Id);
        Assert.Equal("tenant b", survivor.Text);
    }

    [Fact]
    public void MigrateV2ToV3_PreservesExistingRowsInLegacyTenant()
    {
        var v2DbPath = Path.Combine(Path.GetTempPath(), $"sqlite_v2_test_{Guid.NewGuid():N}", "memory.db");
        var v2Dir = Path.GetDirectoryName(v2DbPath)!;
        Directory.CreateDirectory(v2Dir);

        try
        {
            var entry = new CognitiveEntry("legacy", new[] { 1f }, "migration", "legacy row", lifecycleState: "ltm");
            var json = System.Text.Json.JsonSerializer.Serialize(entry, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Converters = { new FloatArrayBase64Converter() }
            });
            var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(json)));

            using (var conn = new SqliteConnection($"Data Source={v2DbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE schema_version (version INTEGER NOT NULL);
                    INSERT INTO schema_version (version) VALUES (2);
                    CREATE TABLE entries (
                        id TEXT NOT NULL,
                        ns TEXT NOT NULL,
                        json_data TEXT NOT NULL,
                        checksum TEXT NOT NULL,
                        lifecycle_state TEXT DEFAULT 'stm',
                        PRIMARY KEY (ns, id)
                    );
                    CREATE TABLE global_data (
                        key TEXT PRIMARY KEY,
                        json_data TEXT NOT NULL,
                        checksum TEXT NOT NULL
                    );
                    CREATE INDEX idx_entries_ns ON entries(ns);
                    CREATE INDEX idx_entries_ns_state ON entries(ns, lifecycle_state);
                    INSERT INTO entries (id, ns, json_data, checksum, lifecycle_state)
                    VALUES ('legacy', 'migration', @json, @checksum, 'ltm');
                    """;
                cmd.Parameters.AddWithValue("@json", json);
                cmd.Parameters.AddWithValue("@checksum", checksum);
                cmd.ExecuteNonQuery();
            }

            using var provider = new SqliteStorageProvider(v2DbPath, debounceMs: 10);
            var migrated = Assert.Single(provider.LoadNamespace("migration").Entries);
            Assert.Equal(string.Empty, migrated.TenantId);
            Assert.Equal("legacy row", migrated.Text);

            using var verify = new SqliteConnection($"Data Source={v2DbPath}");
            verify.Open();
            using var verifyCmd = verify.CreateCommand();
            verifyCmd.CommandText = "SELECT tenant_id FROM entries WHERE ns = 'migration' AND id = 'legacy'";
            Assert.Equal(string.Empty, verifyCmd.ExecuteScalar()!.ToString());
            verifyCmd.CommandText = "SELECT version FROM schema_version LIMIT 1";
            Assert.Equal(3, Convert.ToInt32(verifyCmd.ExecuteScalar()!));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(v2Dir))
                Directory.Delete(v2Dir, true);
        }
    }
}
