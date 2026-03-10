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

        var retrieved = index.Get("idx-1", "test");
        Assert.NotNull(retrieved);
        Assert.Equal("hello", retrieved.Text);
    }

    [Fact]
    public void IntegrationWithCognitiveIndex_SearchWorks()
    {
        using var index = new CognitiveIndex(_provider);

        index.Upsert(new CognitiveEntry("s1", new[] { 1f, 0f }, "test", "alpha"));
        index.Upsert(new CognitiveEntry("s2", new[] { 0f, 1f }, "test", "beta"));

        var results = index.Search(new[] { 1f, 0f }, "test", k: 1);
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

        var entry = index2.Get("p1", "persist");
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
        Assert.Equal(2, loaded.StorageVersion);
    }

    // ── Schema Migration ──

    [Fact]
    public void FreshDatabase_MigratesToCurrentVersion()
    {
        // The provider constructor runs InitializeSchema which should migrate to v2
        using var conn = new SqliteConnection($"Data Source={_testDbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();

        // Verify schema version is set
        cmd.CommandText = "SELECT version FROM schema_version LIMIT 1";
        var version = Convert.ToInt32(cmd.ExecuteScalar()!);
        Assert.Equal(2, version);

        // Verify lifecycle_state column exists
        cmd.CommandText = "PRAGMA table_info(entries)";
        var columns = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        Assert.Contains("lifecycle_state", columns);
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

            // Open with current provider — should trigger v1→v2 migration
            using var provider = new SqliteStorageProvider(v1DbPath, debounceMs: 10);

            // Verify version upgraded
            using var conn2 = new SqliteConnection($"Data Source={v1DbPath}");
            conn2.Open();
            using var cmd2 = conn2.CreateCommand();
            cmd2.CommandText = "SELECT version FROM schema_version LIMIT 1";
            Assert.Equal(2, Convert.ToInt32(cmd2.ExecuteScalar()!));

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
}
