using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Data.SqlClient;

namespace McpEngramMemory.Tests;

/// <summary>
/// Integration tests for <see cref="SqlServerStorageProvider"/>. Gated on the
/// <c>ENGRAM_TEST_SQLSERVER_CONNECTION</c> environment variable — when unset,
/// every test is skipped so CI without a SQL Server stays green.
///
/// Each test uses a unique generated schema so concurrent runs don't collide,
/// and the schema (with all its tables) is dropped on dispose.
/// </summary>
public class SqlServerStorageProviderTests : IDisposable
{
    private const string ConnectionEnvVar = "ENGRAM_TEST_SQLSERVER_CONNECTION";

    private readonly string? _connectionString;
    private readonly string _schema;
    private readonly SqlServerStorageProvider? _provider;
    private readonly bool _enabled;

    public SqlServerStorageProviderTests()
    {
        _connectionString = Environment.GetEnvironmentVariable(ConnectionEnvVar);
        _enabled = !string.IsNullOrWhiteSpace(_connectionString);
        _schema = $"engram_test_{Guid.NewGuid():N}".Substring(0, 32);

        if (_enabled)
            _provider = new SqlServerStorageProvider(_connectionString!, schema: _schema, debounceMs: 10);
    }

    public void Dispose()
    {
        _provider?.Dispose();
        if (_enabled)
            DropTestSchema();
    }

    private void DropTestSchema()
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                IF OBJECT_ID(N'[{_schema}].entries', N'U') IS NOT NULL DROP TABLE [{_schema}].entries;
                IF OBJECT_ID(N'[{_schema}].global_data', N'U') IS NOT NULL DROP TABLE [{_schema}].global_data;
                IF OBJECT_ID(N'[{_schema}].schema_version', N'U') IS NOT NULL DROP TABLE [{_schema}].schema_version;
                IF EXISTS (SELECT 1 FROM sys.schemas WHERE name = '{_schema}') EXEC('DROP SCHEMA [{_schema}]');
                """;
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Best-effort cleanup; ignore if connection or schema is gone
        }
    }

    /// <summary>
    /// Returns true when the SQL Server connection env var is set. xunit 2.5 lacks
    /// a runtime <c>Assert.Skip</c>, so each test calls this and early-returns when
    /// the backend is unavailable. The test is reported as passed (not skipped) —
    /// acceptable here since the gating is intentional CI infra, not a flaky check.
    /// </summary>
    private bool IsEnabled() => _enabled;

    [Fact]
    public void LoadNamespace_Empty_ReturnsEmptyData()
    {
        if (!IsEnabled()) return;
        var data = _provider!.LoadNamespace("nonexistent");
        Assert.Empty(data.Entries);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        if (!IsEnabled()) return;
        var entry = new CognitiveEntry("test-1", new[] { 1f, 2f, 3f }, "myns", "hello world");
        var data = new NamespaceData { Entries = new List<CognitiveEntry> { entry } };

        _provider!.SaveNamespaceSync("myns", data);
        var loaded = _provider.LoadNamespace("myns");

        Assert.Single(loaded.Entries);
        Assert.Equal("test-1", loaded.Entries[0].Id);
        Assert.Equal("hello world", loaded.Entries[0].Text);
        Assert.Equal(new[] { 1f, 2f, 3f }, loaded.Entries[0].Vector);
    }

    [Fact]
    public void SaveNamespaceSync_Overwrites()
    {
        if (!IsEnabled()) return;
        var entry1 = new CognitiveEntry("a", new[] { 1f, 0f }, "ns", "first");
        _provider!.SaveNamespaceSync("ns", new NamespaceData { Entries = [entry1] });

        var entry2 = new CognitiveEntry("b", new[] { 0f, 1f }, "ns", "second");
        _provider.SaveNamespaceSync("ns", new NamespaceData { Entries = [entry2] });

        var loaded = _provider.LoadNamespace("ns");
        Assert.Single(loaded.Entries);
        Assert.Equal("b", loaded.Entries[0].Id);
    }

    [Fact]
    public void GetPersistedNamespaces_ListsNamespaces()
    {
        if (!IsEnabled()) return;
        _provider!.SaveNamespaceSync("alpha", new NamespaceData { Entries = [new CognitiveEntry("a", new[] { 1f }, "alpha")] });
        _provider.SaveNamespaceSync("beta", new NamespaceData { Entries = [new CognitiveEntry("b", new[] { 1f }, "beta")] });

        var namespaces = _provider.GetPersistedNamespaces();
        Assert.Contains("alpha", namespaces);
        Assert.Contains("beta", namespaces);
    }

    [Fact]
    public void GetPersistedNamespaces_ExcludesUnderscorePrefix()
    {
        if (!IsEnabled()) return;
        _provider!.SaveNamespaceSync("_system", new NamespaceData { Entries = [new CognitiveEntry("s", new[] { 1f }, "_system")] });
        _provider.SaveNamespaceSync("normal", new NamespaceData { Entries = [new CognitiveEntry("n", new[] { 1f }, "normal")] });

        var namespaces = _provider.GetPersistedNamespaces();
        Assert.Contains("normal", namespaces);
        Assert.DoesNotContain("_system", namespaces);
    }

    [Fact]
    public void DebouncedSave_FlushesOnDispose()
    {
        if (!IsEnabled()) return;
        var entry = new CognitiveEntry("d1", new[] { 1f, 2f }, "debounce-ns", "debounced");
        var data = new NamespaceData { Entries = [entry] };
        _provider!.ScheduleSave("debounce-ns", () => data);

        _provider.Flush();

        var loaded = _provider.LoadNamespace("debounce-ns");
        Assert.Single(loaded.Entries);
        Assert.Equal("d1", loaded.Entries[0].Id);
    }

    [Fact]
    public void GlobalEdges_SaveAndLoad()
    {
        if (!IsEnabled()) return;
        var edges = new List<GraphEdge>
        {
            new("a", "b", "cross_reference"),
            new("b", "c", "depends_on")
        };

        _provider!.ScheduleSaveGlobalEdges(() => edges);
        _provider.Flush();

        var loaded = _provider.LoadGlobalEdges();
        Assert.Equal(2, loaded.Count);
    }

    [Fact]
    public void Clusters_SaveAndLoad()
    {
        if (!IsEnabled()) return;
        var clusters = new List<SemanticCluster>
        {
            new("c1", "test", new List<string> { "m1", "m2" }, "test cluster")
        };

        _provider!.ScheduleSaveClusters(() => clusters);
        _provider.Flush();

        var loaded = _provider.LoadClusters();
        Assert.Single(loaded);
        Assert.Equal("c1", loaded[0].ClusterId);
    }

    [Fact]
    public void DecayConfigs_SaveAndLoad()
    {
        if (!IsEnabled()) return;
        var configs = new Dictionary<string, DecayConfig>
        {
            ["test"] = new("test", decayRate: 0.5f)
        };

        _provider!.ScheduleSaveDecayConfigs(() => configs);
        _provider.Flush();

        var loaded = _provider.LoadDecayConfigs();
        Assert.Single(loaded);
        Assert.Equal(0.5f, loaded["test"].DecayRate);
    }

    [Fact]
    public void IntegrationWithCognitiveIndex_PersistsAcrossInstances()
    {
        if (!IsEnabled()) return;

        using (var index = new CognitiveIndex(_provider!))
        {
            index.Upsert(new CognitiveEntry("p1", new[] { 1f, 0f }, "persist", "persisted entry"));
            _provider!.Flush();
        }

        using var provider2 = new SqlServerStorageProvider(_connectionString!, schema: _schema, debounceMs: 10);
        using var index2 = new CognitiveIndex(provider2);

        var entry = index2.Get("p1", "persist");
        Assert.NotNull(entry);
        Assert.Equal("persisted entry", entry.Text);
    }

    [Fact]
    public void IncrementalUpsert_LifecycleStateColumnPopulated()
    {
        if (!IsEnabled()) return;
        var entry = new CognitiveEntry("inc1", new[] { 1f, 2f }, "ns", "incremental", lifecycleState: "archived");
        _provider!.ScheduleUpsertEntry("ns", entry);
        _provider.Flush();

        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT lifecycle_state FROM [{_schema}].entries WHERE id = 'inc1'";
        Assert.Equal("archived", cmd.ExecuteScalar()!.ToString());
    }

    [Fact]
    public void IncrementalDelete_RemovesEntry()
    {
        if (!IsEnabled()) return;
        var entry = new CognitiveEntry("d1", new[] { 1f }, "ns", "to-delete");
        _provider!.ScheduleUpsertEntry("ns", entry);
        _provider.Flush();

        _provider.ScheduleDeleteEntry("ns", "d1");
        _provider.Flush();

        var loaded = _provider.LoadNamespace("ns");
        Assert.Empty(loaded.Entries);
    }

    [Fact]
    public async Task DeleteNamespaceAsync_RemovesEntries()
    {
        if (!IsEnabled()) return;
        _provider!.SaveNamespaceSync("doomed",
            new NamespaceData { Entries = [new CognitiveEntry("x", new[] { 1f }, "doomed", "v")] });

        await _provider.DeleteNamespaceAsync("doomed");

        Assert.Empty(_provider.LoadNamespace("doomed").Entries);
    }

    [Fact]
    public void InvalidSchemaName_Throws()
    {
        // No connection required — pure constructor validation
        Assert.Throws<ArgumentException>(() =>
            new SqlServerStorageProvider("Server=localhost;", schema: "bad; DROP TABLE x;--"));
    }

    [Fact]
    public void EmptyConnectionString_Throws()
    {
        Assert.Throws<ArgumentException>(() => new SqlServerStorageProvider(""));
    }
}
