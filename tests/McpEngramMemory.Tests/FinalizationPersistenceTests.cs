using System.Security.Cryptography;
using System.Text;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace McpEngramMemory.Tests;

/// <summary>
/// Final persistence controls: a flush has an observable queue checkpoint, and receipt JSON is
/// judged before constructors can normalize or discard evidence needed for a fail-closed RMW.
/// </summary>
public sealed class FinalizationPersistenceTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("sqlite")]
    public async Task TryFlush_EntryQueuedDuringBlockedWrite_CannotReturnTrue(string backend)
    {
        var root = Path.Combine(Path.GetTempPath(), $"final-flush-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using IStorageProvider provider = backend == "json"
                ? new PersistenceManager(root, debounceMs: 60_000)
                : new SqliteStorageProvider(Path.Combine(root, "memory.db"), debounceMs: 60_000);
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);

            provider.ScheduleSave("first", () =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(20)));
                return new NamespaceData();
            });

            var flush = Task.Run(provider.TryFlush);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(20)));

            // This is fully queued before the blocked first write is released. It was not in
            // the flush's initial drain and therefore must be visible at the final checkpoint.
            provider.ScheduleSave("second", () => new NamespaceData());
            release.Set();

            Assert.False(await flush.WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.True(provider.TryFlush());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void JsonStrictRawReceiptReads_KeepMissingLegacyTenant_ButRefuseLossyShapes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"final-raw-json-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "_collapse_history.json");
            using var provider = new PersistenceManager(root, debounceMs: 60_000);

            File.WriteAllText(path, ReceiptJson("legacy"));
            Assert.True(provider.TryReadCollapseHistory(out var legacy));
            Assert.Equal(string.Empty, Assert.Single(legacy).TenantId);

            File.WriteAllText(path, ReceiptJson("null-tenant", "\"tenantId\":null,"));
            Assert.False(provider.TryReadCollapseHistory(out var explicitNull));
            Assert.Empty(explicitNull);

            File.WriteAllText(path, ReceiptJson("bad-tenant", "\"tenantId\":\"bad\\u000Atenant\","));
            Assert.False(provider.TryReadCollapseHistory(out _));

            File.WriteAllText(path, ReceiptJson("bad-ns").Replace("\"ns\":\"test\"", "\"ns\":\"bad\\u001Fns\""));
            Assert.False(provider.TryReadCollapseHistory(out _));

            File.WriteAllText(path, ReceiptJson("long-ns").Replace(
                "\"ns\":\"test\"", $"\"ns\":\"{new string('n', CognitiveEntry.MaxNamespaceLength + 1)}\""));
            Assert.False(provider.TryReadCollapseHistory(out _));

            var duplicateMapKey = ReceiptJson("duplicate-map")
                .Replace("\"memberIds\":[]", "\"memberIds\":[\"a\"]")
                .Replace("\"previousStates\":{}", "\"previousStates\":{\"a\":\"stm\",\"a\":\"ltm\"}");
            File.WriteAllText(path, duplicateMapKey);
            Assert.False(provider.TryReadCollapseHistory(out _));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void JsonStrictReceiptSet_RefusesDuplicateIds()
    {
        var root = Path.Combine(Path.GetTempPath(), $"final-duplicate-json-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var row = ReceiptJson("duplicate");
            var duplicated = $"[{row[1..^1]},{row[1..^1]}]";
            File.WriteAllText(Path.Combine(root, "_collapse_history.json"), duplicated);
            using var provider = new PersistenceManager(root, debounceMs: 60_000);

            Assert.False(provider.TryReadCollapseHistory(out var records));
            Assert.Empty(records);
            Assert.Single(provider.LoadCollapseHistory()); // lenient boot keeps the first only
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void JsonUnknownReceiptField_IsNotLaunderedByReadModifyWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), $"final-unknown-json-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "_collapse_history.json");
            var raw = ReceiptJson("future", extraField: ",\"futureWitness\":{\"opaque\":1}");
            File.WriteAllText(path, raw);
            using var provider = new PersistenceManager(root, debounceMs: 60_000);
            var replacement = ValidRecord("replacement");

            Assert.False(provider.TryReadCollapseHistory(out _));
            Assert.Empty(provider.LoadCollapseHistory());
            Assert.False(provider.UpsertCollapseRecordSync(replacement));
            Assert.Equal(CollapseRecordCas.StoreFailed,
                provider.UpsertCollapseRecordSync(replacement, onlyIfGeneration: null));
            Assert.Equal(raw, File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("sqlite")]
    public void MalformedSuppliedReceipt_AllUpsertsRefuseAndPreserveResident(string backend)
    {
        var root = Path.Combine(Path.GetTempPath(), $"final-supplied-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using IStorageProvider provider = backend == "json"
                ? new PersistenceManager(root, debounceMs: 60_000)
                : new SqliteStorageProvider(Path.Combine(root, "memory.db"), debounceMs: 60_000);
            var resident = ValidRecord("resident");
            Assert.True(provider.UpsertCollapseRecordSync(resident));

            // The read constructor intentionally permits poisoned stored shapes so lenient boot
            // can isolate rows. Provider write boundaries must still reject such an object.
            var malformed = new CollapseRecord(
                resident.CollapseId, "cluster:bad", "summary:bad", "bad\nnamespace",
                new List<string>(), new Dictionary<string, string>(), DateTimeOffset.UtcNow,
                tenantId: "", generation: resident.Generation + 1);

            Assert.False(provider.UpsertCollapseRecordSync(malformed));
            Assert.Equal(CollapseRecordCas.StoreFailed,
                provider.UpsertCollapseRecordSync(malformed, resident.Generation));
            Assert.True(provider.TryReadCollapseRecord(resident.CollapseId, out var stillResident));
            Assert.Equal(resident.ClusterId, stillResident!.ClusterId);
            Assert.Equal(resident.Ns, stillResident.Ns);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SuppliedReceipt_NegativeGenerationBlankOptionalWitnessesAndBlankMapKeys_Refuse()
    {
        var root = Path.Combine(Path.GetTempPath(), $"final-object-shapes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var provider = new PersistenceManager(root, debounceMs: 60_000);
            var malformed = new[]
            {
                new CollapseRecord("negative", "k", "s", "test", new List<string>(),
                    new Dictionary<string, string>(), DateTimeOffset.UtcNow, generation: -1),
                new CollapseRecord("blank-stamp", "k", "s", "test", new List<string>(),
                    new Dictionary<string, string>(), DateTimeOffset.UtcNow, clusterStamp: " "),
                new CollapseRecord("blank-instance", "k", "s", "test", new List<string>(),
                    new Dictionary<string, string>(), DateTimeOffset.UtcNow, clusterInstance: "\t"),
                new CollapseRecord("blank-map", "k", "s", "test", new List<string> { "a" },
                    new Dictionary<string, string> { ["a"] = "stm" }, DateTimeOffset.UtcNow,
                    appliedLifecycleRevisions: new Dictionary<string, long> { [""] = 1 })
            };

            foreach (var record in malformed)
            {
                Assert.False(provider.UpsertCollapseRecordSync(record));
                Assert.Equal(CollapseRecordCas.StoreFailed,
                    provider.UpsertCollapseRecordSync(record, onlyIfGeneration: null));
            }
            Assert.True(provider.TryReadCollapseHistory(out var records));
            Assert.Empty(records);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SqliteStrictAndRmwPaths_RefuseUnknownAndDuplicateRawReceipts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"final-raw-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "memory.db");
        try
        {
            using var provider = new SqliteStorageProvider(dbPath, debounceMs: 60_000);
            var unknown = ReceiptJson("future", extraField: ",\"futureWitness\":true");
            WriteSqliteReceiptJson(dbPath, unknown);

            Assert.False(provider.TryReadCollapseHistory(out _));
            Assert.Empty(provider.LoadCollapseHistory());
            Assert.False(provider.UpsertCollapseRecordSync(ValidRecord("new")));
            Assert.Equal(unknown, ReadSqliteReceiptJson(dbPath));

            var row = ReceiptJson("same");
            WriteSqliteReceiptJson(dbPath, $"[{row[1..^1]},{row[1..^1]}]");
            Assert.False(provider.TryReadCollapseHistory(out var duplicateSet));
            Assert.Empty(duplicateSet);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SqlServerPersistenceFinalizationControls_WhenIntegrationBackendIsAvailable()
    {
        var connectionString = Environment.GetEnvironmentVariable("ENGRAM_TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var schema = $"engram_final_{Guid.NewGuid():N}"[..28];
        try
        {
            using var provider = new SqlServerStorageProvider(
                connectionString, schema: schema, debounceMs: 60_000);

            // Omitted tenant is the legacy partition; explicit null is not.
            WriteSqlServerReceiptJson(connectionString, schema, ReceiptJson("legacy"));
            Assert.True(provider.TryReadCollapseHistory(out var legacy));
            Assert.Equal(string.Empty, Assert.Single(legacy).TenantId);
            WriteSqlServerReceiptJson(connectionString, schema,
                ReceiptJson("null-tenant", "\"tenantId\":null,"));
            Assert.False(provider.TryReadCollapseHistory(out _));

            // Duplicate ids refuse as a set, and unknown fields refuse every strict/RMW path.
            var row = ReceiptJson("same");
            WriteSqlServerReceiptJson(connectionString, schema, $"[{row[1..^1]},{row[1..^1]}]");
            Assert.False(provider.TryReadCollapseHistory(out _));

            var unknown = ReceiptJson("future", extraField: ",\"futureWitness\":true");
            WriteSqlServerReceiptJson(connectionString, schema, unknown);
            Assert.False(provider.TryReadCollapseHistory(out _));
            Assert.Empty(provider.LoadCollapseHistory());
            Assert.False(provider.UpsertCollapseRecordSync(ValidRecord("new")));
            Assert.Equal(unknown, ReadSqlServerReceiptJson(connectionString, schema));

            // Restore a valid resident, then prove both supplied-record upserts reject a bad
            // partition shape without replacing it.
            WriteSqlServerReceiptJson(connectionString, schema, ReceiptJson("resident"));
            var malformed = new CollapseRecord(
                "resident", "cluster:bad", "summary:bad", "bad\nnamespace",
                new List<string>(), new Dictionary<string, string>(), DateTimeOffset.UtcNow,
                tenantId: "", generation: 2);
            Assert.False(provider.UpsertCollapseRecordSync(malformed));
            Assert.Equal(CollapseRecordCas.StoreFailed,
                provider.UpsertCollapseRecordSync(malformed, onlyIfGeneration: 1));
            Assert.True(provider.TryReadCollapseRecord("resident", out var stillResident));
            Assert.Equal("cluster:resident", stillResident!.ClusterId);

            // Same blocked-write interleaving as the two always-on providers.
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            provider.ScheduleSave("first", () =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(20)));
                return new NamespaceData();
            });
            var flush = Task.Run(provider.TryFlush);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(20)));
            provider.ScheduleSave("second", () => new NamespaceData());
            release.Set();
            Assert.False(await flush.WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.True(provider.TryFlush());
        }
        finally
        {
            DropSqlServerSchema(connectionString, schema);
        }
    }

    private static CollapseRecord ValidRecord(string id) => new(
        id, $"cluster:{id}", $"summary:{id}", "test",
        new List<string>(), new Dictionary<string, string>(), tenantId: "", generation: 1);

    private static string ReceiptJson(
        string id,
        string tenantField = "",
        string extraField = "")
        => $"[{{\"collapseId\":\"{id}\",\"clusterId\":\"cluster:{id}\",\"summaryEntryId\":\"summary:{id}\"," +
           $"\"ns\":\"test\",{tenantField}\"memberIds\":[],\"previousStates\":{{}}," +
           $"\"collapsedAt\":\"2026-09-01T12:00:00+00:00\",\"generation\":1{extraField}}}]";

    private static void WriteSqliteReceiptJson(string dbPath, string json)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO global_data (key, json_data, checksum)
            VALUES ('collapse_history', @json, @checksum)
            """;
        cmd.Parameters.AddWithValue("@json", json);
        cmd.Parameters.AddWithValue("@checksum",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
        cmd.ExecuteNonQuery();
    }

    private static string ReadSqliteReceiptJson(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json_data FROM global_data WHERE key = 'collapse_history'";
        return (string)cmd.ExecuteScalar()!;
    }

    private static void WriteSqlServerReceiptJson(string connectionString, string schema, string json)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            MERGE [{schema}].global_data AS target
            USING (SELECT CAST('collapse_history' AS NVARCHAR(200)) AS [key]) AS source
            ON target.[key] = source.[key]
            WHEN MATCHED THEN UPDATE SET json_data = @json, checksum = @checksum
            WHEN NOT MATCHED THEN INSERT ([key], json_data, checksum)
                VALUES ('collapse_history', @json, @checksum);
            """;
        cmd.Parameters.AddWithValue("@json", json);
        cmd.Parameters.AddWithValue("@checksum",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
        cmd.ExecuteNonQuery();
    }

    private static string ReadSqlServerReceiptJson(string connectionString, string schema)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT json_data FROM [{schema}].global_data WHERE [key] = 'collapse_history'";
        return (string)cmd.ExecuteScalar()!;
    }

    private static void DropSqlServerSchema(string connectionString, string schema)
    {
        try
        {
            using var conn = new SqlConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                IF OBJECT_ID(N'[{schema}].entries', N'U') IS NOT NULL DROP TABLE [{schema}].entries;
                IF OBJECT_ID(N'[{schema}].global_data', N'U') IS NOT NULL DROP TABLE [{schema}].global_data;
                IF OBJECT_ID(N'[{schema}].schema_version', N'U') IS NOT NULL DROP TABLE [{schema}].schema_version;
                IF EXISTS (SELECT 1 FROM sys.schemas WHERE name = '{schema}') EXEC('DROP SCHEMA [{schema}]');
                """;
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Best-effort cleanup for an optional integration backend.
        }
    }
}
