using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Core.Services.Storage.Migration;
using Microsoft.Data.SqlClient;

namespace McpEngramMemory.Tests;

/// <summary>
/// SQL Server-backed tests for <see cref="TenantPrefixMigrationTool"/>. Gated on
/// <c>ENGRAM_TEST_SQLSERVER_CONNECTION</c> (same convention as <see cref="SqlServerStorageProviderTests"/>)
/// — skipped (reported passed, per that file's documented convention) when unset.
///
/// This backend has the real tenant-partitioned <c>(tenant_id, ns, id)</c> primary key (v3 schema),
/// so it is the only backend that can safely fold two different prefixed source namespaces sharing
/// both a destination path AND an entry id into the same physical bucket — the scenario that is a
/// deliberate boundary/limitation on the SQLite backend (see
/// TenantPrefixMigrationToolTests.Migrate_SameIdAcrossTenantsAtSameDestination_SqliteBackendBoundary).
/// </summary>
public class TenantPrefixMigrationToolSqlServerTests : IDisposable
{
    private const string ConnectionEnvVar = "ENGRAM_TEST_SQLSERVER_CONNECTION";

    private readonly string? _connectionString;
    private readonly string _schema;
    private readonly SqlServerStorageProvider? _provider;
    private readonly TenantPrefixMigrationTool? _tool;
    private readonly bool _enabled;

    public TenantPrefixMigrationToolSqlServerTests()
    {
        _connectionString = Environment.GetEnvironmentVariable(ConnectionEnvVar);
        _enabled = !string.IsNullOrWhiteSpace(_connectionString);
        _schema = $"engram_tpm_test_{Guid.NewGuid():N}".Substring(0, 32);

        if (_enabled)
        {
            _provider = new SqlServerStorageProvider(_connectionString!, schema: _schema, debounceMs: 10);
            _tool = new TenantPrefixMigrationTool(_provider);
        }
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

    private bool IsEnabled() => _enabled;

    private static CognitiveEntry Entry(string id, string ns, string? text = null)
        => new(id, new[] { 1f, 0f }, ns, text: text);

    [Fact]
    public void Migrate_SameIdAcrossTenantsAtSameDestination_TenantPartitionedPrimaryKeyKeepsThemDisjoint()
    {
        if (!IsEnabled()) return;

        _provider!.SaveNamespaceSync("tenant-a::work", new NamespaceData { Entries = [Entry("shared", "tenant-a::work", "alpha")] });
        _provider.SaveNamespaceSync("tenant-b::work", new NamespaceData { Entries = [Entry("shared", "tenant-b::work", "bravo")] });

        var manifest = _tool!.Migrate();

        Assert.True(manifest.RowCountParityOk);
        var loaded = _provider.LoadNamespace("work");
        Assert.Equal(2, loaded.Entries.Count);

        var a = loaded.Entries.Single(e => e.TenantId == "tenant-a");
        var b = loaded.Entries.Single(e => e.TenantId == "tenant-b");
        Assert.Equal("alpha", a.Text);
        Assert.Equal("bravo", b.Text);
        Assert.Equal("shared", a.Id);
        Assert.Equal("shared", b.Id);
    }

    [Fact]
    public void Migrate_ThenReverse_SameIdAcrossTenants_RoundTrips()
    {
        if (!IsEnabled()) return;

        _provider!.SaveNamespaceSync("tenant-a::work", new NamespaceData { Entries = [Entry("shared", "tenant-a::work", "alpha")] });
        _provider.SaveNamespaceSync("tenant-b::work", new NamespaceData { Entries = [Entry("shared", "tenant-b::work", "bravo")] });

        var totalBefore = _provider.GetPersistedNamespaces().Sum(ns => _provider.LoadNamespace(ns).Entries.Count);

        var forward = _tool!.Migrate();
        var reverse = _tool.Reverse(forward);

        Assert.True(forward.RowCountParityOk);
        Assert.True(reverse.RowCountParityOk);

        var totalAfter = _provider.GetPersistedNamespaces().Sum(ns => _provider.LoadNamespace(ns).Entries.Count);
        Assert.Equal(totalBefore, totalAfter);

        var a = _provider.LoadNamespace("tenant-a::work");
        Assert.Single(a.Entries);
        Assert.Equal("alpha", a.Entries[0].Text);
        Assert.Equal("", a.Entries[0].TenantId);

        var b = _provider.LoadNamespace("tenant-b::work");
        Assert.Single(b.Entries);
        Assert.Equal("bravo", b.Entries[0].Text);
        Assert.Equal("", b.Entries[0].TenantId);
    }
}
