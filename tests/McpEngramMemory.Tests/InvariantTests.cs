using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Data.Sqlite;

namespace McpEngramMemory.Tests;

/// <summary>
/// Structural invariant tests that verify properties which must always hold
/// regardless of operation order. Covers both JSON and SQLite backends.
/// </summary>
public class InvariantTests : IDisposable
{
    private readonly string _jsonPath;
    private readonly string _sqlitePath;
    private readonly PersistenceManager _jsonPersistence;
    private readonly SqliteStorageProvider _sqlitePersistence;
    private readonly CognitiveIndex _jsonIndex;
    private readonly CognitiveIndex _sqliteIndex;

    public InvariantTests()
    {
        _jsonPath = Path.Combine(Path.GetTempPath(), $"invariant_json_{Guid.NewGuid():N}");
        _sqlitePath = Path.Combine(Path.GetTempPath(), $"invariant_sqlite_{Guid.NewGuid():N}", "memory.db");
        _jsonPersistence = new PersistenceManager(_jsonPath, debounceMs: 10);
        _sqlitePersistence = new SqliteStorageProvider(_sqlitePath, debounceMs: 10);
        _jsonIndex = new CognitiveIndex(_jsonPersistence);
        _sqliteIndex = new CognitiveIndex(_sqlitePersistence);
    }

    public void Dispose()
    {
        _jsonIndex.Dispose();
        _sqliteIndex.Dispose();
        _jsonPersistence.Dispose();
        _sqlitePersistence.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_jsonPath))
            Directory.Delete(_jsonPath, true);
        var sqliteDir = Path.GetDirectoryName(_sqlitePath);
        if (sqliteDir is not null && Directory.Exists(sqliteDir))
            Directory.Delete(sqliteDir, true);
    }

    private static CognitiveEntry MakeEntry(string id, string ns = "test",
        string? text = null, string? category = null, string lifecycleState = "stm",
        float v1 = 1f, float v2 = 0f)
    {
        return new CognitiveEntry(id, new[] { v1, v2 }, ns, text ?? $"text-{id}",
            category, lifecycleState: lifecycleState);
    }

    // ── Upsert-Get-Delete Consistency ──

    [Theory]
    [InlineData("json")]
    [InlineData("sqlite")]
    public void UpsertGetDelete_Consistency(string backend)
    {
        var index = backend == "sqlite" ? _sqliteIndex : _jsonIndex;

        // Upsert → Get returns entry
        var entry = MakeEntry("c1");
        index.Upsert(entry);
        Assert.NotNull(index.Get("c1"));
        Assert.NotNull(index.Get("c1", "test"));
        Assert.Equal(1, index.Count);

        // Delete → Get returns null
        Assert.True(index.Delete("c1"));
        Assert.Null(index.Get("c1"));
        Assert.Null(index.Get("c1", "test"));
        Assert.Equal(0, index.Count);

        // Delete again → returns false
        Assert.False(index.Delete("c1"));
    }

    // ── Count matches GetAll ──

    [Theory]
    [InlineData("json")]
    [InlineData("sqlite")]
    public void Count_MatchesGetAll(string backend)
    {
        var index = backend == "sqlite" ? _sqliteIndex : _jsonIndex;

        for (int i = 0; i < 10; i++)
            index.Upsert(MakeEntry($"cnt-{i}"));

        Assert.Equal(index.Count, index.GetAll().Count);
        Assert.Equal(10, index.CountInNamespace("test"));

        index.Delete("cnt-3");
        index.Delete("cnt-7");

        Assert.Equal(index.Count, index.GetAll().Count);
        Assert.Equal(8, index.CountInNamespace("test"));
    }

    // ── Namespace Isolation ──

    [Theory]
    [InlineData("json")]
    [InlineData("sqlite")]
    public void NamespaceIsolation_SearchDoesNotCrossNamespaces(string backend)
    {
        var index = backend == "sqlite" ? _sqliteIndex : _jsonIndex;

        index.Upsert(MakeEntry("a1", ns: "alpha", v1: 1f, v2: 0f));
        index.Upsert(MakeEntry("b1", ns: "beta", v1: 1f, v2: 0f));

        var alphaResults = index.Search(new[] { 1f, 0f }, "alpha", k: 10);
        var betaResults = index.Search(new[] { 1f, 0f }, "beta", k: 10);

        Assert.Single(alphaResults);
        Assert.Equal("a1", alphaResults[0].Id);
        Assert.Single(betaResults);
        Assert.Equal("b1", betaResults[0].Id);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("sqlite")]
    public void NamespaceIsolation_SameIdInDifferentNamespaces_CoexistIndependently(string backend)
    {
        var index = backend == "sqlite" ? _sqliteIndex : _jsonIndex;

        index.Upsert(MakeEntry("shared-id", ns: "ns1"));
        // Upsert same ID in different namespace
        index.Upsert(MakeEntry("shared-id", ns: "ns2"));

        // Both should exist independently
        Assert.NotNull(index.Get("shared-id", "ns1"));
        Assert.NotNull(index.Get("shared-id", "ns2"));
        Assert.Equal(1, index.CountInNamespace("ns1"));
        Assert.Equal(1, index.CountInNamespace("ns2"));
    }

    // ── Cross-Namespace Get ──

    [Theory]
    [InlineData("json")]
    [InlineData("sqlite")]
    public void CrossNamespaceGet_FindsEntryInAnyNamespace(string backend)
    {
        var index = backend == "sqlite" ? _sqliteIndex : _jsonIndex;

        index.Upsert(MakeEntry("find-me", ns: "hidden"));

        // Get without namespace should still find it
        var found = index.Get("find-me");
        Assert.NotNull(found);
        Assert.Equal("hidden", found.Ns);
    }

    // ── Lifecycle State Validity ──

    [Theory]
    [InlineData("json")]
    [InlineData("sqlite")]
    public void LifecycleState_TransitionsPreserveEntry(string backend)
    {
        var index = backend == "sqlite" ? _sqliteIndex : _jsonIndex;

        var entry = MakeEntry("lc1", text: "lifecycle test", category: "test-cat");
        index.Upsert(entry);

        // STM → LTM
        Assert.True(index.SetLifecycleState("lc1", "ltm"));
        var after = index.Get("lc1")!;
        Assert.Equal("ltm", after.LifecycleState);
        Assert.Equal("lifecycle test", after.Text);
        Assert.Equal("test-cat", after.Category);

        // LTM → archived
        Assert.True(index.SetLifecycleState("lc1", "archived"));
        Assert.Equal("archived", index.Get("lc1")!.LifecycleState);

        // archived → STM
        Assert.True(index.SetLifecycleState("lc1", "stm"));
        Assert.Equal("stm", index.Get("lc1")!.LifecycleState);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("sqlite")]
    public void SetLifecycleStateBatch_MatchesIndividualCalls(string backend)
    {
        var index = backend == "sqlite" ? _sqliteIndex : _jsonIndex;

        index.Upsert(MakeEntry("b1"));
        index.Upsert(MakeEntry("b2"));
        index.Upsert(MakeEntry("b3"));

        int updated = index.SetLifecycleStateBatch(new[] { "b1", "b2", "b3" }, "ltm");
        Assert.Equal(3, updated);

        Assert.Equal("ltm", index.Get("b1")!.LifecycleState);
        Assert.Equal("ltm", index.Get("b2")!.LifecycleState);
        Assert.Equal("ltm", index.Get("b3")!.LifecycleState);
    }

    // ── Delete + Re-insert ──

    [Theory]
    [InlineData("json")]
    [InlineData("sqlite")]
    public void DeleteAndReinsert_EntryIsFindable(string backend)
    {
        var index = backend == "sqlite" ? _sqliteIndex : _jsonIndex;

        index.Upsert(MakeEntry("reins", text: "original"));
        index.Delete("reins");
        Assert.Null(index.Get("reins"));

        index.Upsert(MakeEntry("reins", text: "reinserted"));
        var found = index.Get("reins")!;
        Assert.Equal("reinserted", found.Text);

        // Searchable too
        var results = index.Search(new[] { 1f, 0f }, "test", k: 1);
        Assert.Single(results);
        Assert.Equal("reins", results[0].Id);
    }

    // ── Access Tracking ──

    [Theory]
    [InlineData("json")]
    [InlineData("sqlite")]
    public void RecordAccess_IncrementsCountAndTimestamp(string backend)
    {
        var index = backend == "sqlite" ? _sqliteIndex : _jsonIndex;

        index.Upsert(MakeEntry("acc1"));
        var before = index.Get("acc1")!;
        int initialCount = before.AccessCount;
        var initialTime = before.LastAccessedAt;

        index.RecordAccess("acc1");

        var after = index.Get("acc1")!;
        Assert.Equal(initialCount + 1, after.AccessCount);
        Assert.True(after.LastAccessedAt >= initialTime);
    }

    // ── SQLite Full Round-Trip Fidelity ──

    [Fact]
    public void SqliteRoundTrip_AllFieldsSurvive()
    {
        var metadata = new Dictionary<string, string> { ["key1"] = "value1", ["key2"] = "value2" };
        var entry = new CognitiveEntry("rt1", new[] { 0.5f, -0.3f, 1.0f }, "round-trip",
            "full round trip test", "test-category", metadata, "ltm");
        entry.ActivationEnergy = 42.5f;
        entry.IsSummaryNode = true;
        entry.SourceClusterId = "cluster-abc";

        _sqliteIndex.Upsert(entry);
        _sqlitePersistence.Flush();

        // Load via a fresh provider pointing to same DB
        using var provider2 = new SqliteStorageProvider(_sqlitePath, debounceMs: 10);
        using var index2 = new CognitiveIndex(provider2);

        var loaded = index2.Get("rt1", "round-trip")!;
        Assert.NotNull(loaded);
        Assert.Equal("rt1", loaded.Id);
        Assert.Equal(new[] { 0.5f, -0.3f, 1.0f }, loaded.Vector);
        Assert.Equal("round-trip", loaded.Ns);
        Assert.Equal("full round trip test", loaded.Text);
        Assert.Equal("test-category", loaded.Category);
        Assert.Equal("ltm", loaded.LifecycleState);
        Assert.Equal(42.5f, loaded.ActivationEnergy);
        Assert.True(loaded.IsSummaryNode);
        Assert.Equal("cluster-abc", loaded.SourceClusterId);
        Assert.Equal("value1", loaded.Metadata["key1"]);
        Assert.Equal("value2", loaded.Metadata["key2"]);
        Assert.Equal(entry.CreatedAt.ToUnixTimeMilliseconds(), loaded.CreatedAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void JsonRoundTrip_AllFieldsSurvive()
    {
        var metadata = new Dictionary<string, string> { ["key1"] = "value1" };
        var entry = new CognitiveEntry("rt2", new[] { 0.5f, -0.3f }, "round-trip",
            "json round trip", "cat", metadata, "archived");
        entry.ActivationEnergy = 10f;
        entry.IsSummaryNode = true;
        entry.SourceClusterId = "c1";

        _jsonIndex.Upsert(entry);
        _jsonPersistence.Flush();

        // Load via fresh persistence
        using var persistence2 = new PersistenceManager(_jsonPath, debounceMs: 10);
        using var index2 = new CognitiveIndex(persistence2);

        var loaded = index2.Get("rt2", "round-trip")!;
        Assert.NotNull(loaded);
        Assert.Equal("rt2", loaded.Id);
        Assert.Equal(new[] { 0.5f, -0.3f }, loaded.Vector);
        Assert.Equal("json round trip", loaded.Text);
        Assert.Equal("archived", loaded.LifecycleState);
        Assert.Equal(10f, loaded.ActivationEnergy);
        Assert.True(loaded.IsSummaryNode);
        Assert.Equal("c1", loaded.SourceClusterId);
        Assert.Equal("value1", loaded.Metadata["key1"]);
    }

    // ── Incremental Write Correctness (SQLite-specific) ──

    [Fact]
    public void IncrementalWrites_UpsertAndDelete_ProducesCorrectState()
    {
        // Upsert 3 entries
        _sqliteIndex.Upsert(MakeEntry("iw1"));
        _sqliteIndex.Upsert(MakeEntry("iw2"));
        _sqliteIndex.Upsert(MakeEntry("iw3"));
        _sqlitePersistence.Flush();

        // Delete one, update one
        _sqliteIndex.Delete("iw2");
        _sqliteIndex.SetLifecycleState("iw1", "ltm");
        _sqlitePersistence.Flush();

        // Verify via fresh provider
        using var provider2 = new SqliteStorageProvider(_sqlitePath, debounceMs: 10);
        using var index2 = new CognitiveIndex(provider2);

        Assert.NotNull(index2.Get("iw1", "test"));
        Assert.Equal("ltm", index2.Get("iw1", "test")!.LifecycleState);
        Assert.Null(index2.Get("iw2", "test"));
        Assert.NotNull(index2.Get("iw3", "test"));
    }

    // ── Backend Equivalence ──

    [Fact]
    public void JsonAndSqlite_ProduceSameResults()
    {
        // Perform identical operations on both backends
        for (int i = 0; i < 5; i++)
        {
            var entry = MakeEntry($"eq-{i}", v1: i + 1f, v2: i * 0.5f);
            _jsonIndex.Upsert(entry);
            _sqliteIndex.Upsert(entry);
        }

        _jsonIndex.SetLifecycleState("eq-2", "ltm");
        _sqliteIndex.SetLifecycleState("eq-2", "ltm");
        _jsonIndex.Delete("eq-4");
        _sqliteIndex.Delete("eq-4");

        // Counts should match
        Assert.Equal(_jsonIndex.Count, _sqliteIndex.Count);

        // All entries should match
        var jsonAll = _jsonIndex.GetAll().OrderBy(e => e.Id).ToList();
        var sqliteAll = _sqliteIndex.GetAll().OrderBy(e => e.Id).ToList();
        Assert.Equal(jsonAll.Count, sqliteAll.Count);
        for (int i = 0; i < jsonAll.Count; i++)
        {
            Assert.Equal(jsonAll[i].Id, sqliteAll[i].Id);
            Assert.Equal(jsonAll[i].LifecycleState, sqliteAll[i].LifecycleState);
            Assert.Equal(jsonAll[i].Text, sqliteAll[i].Text);
        }

        // Search results should match
        var jsonResults = _jsonIndex.Search(new[] { 3f, 1f }, "test", k: 3);
        var sqliteResults = _sqliteIndex.Search(new[] { 3f, 1f }, "test", k: 3);
        Assert.Equal(jsonResults.Count, sqliteResults.Count);
        for (int i = 0; i < jsonResults.Count; i++)
            Assert.Equal(jsonResults[i].Id, sqliteResults[i].Id);
    }

    // ── Schema Version After Migration ──

    [Fact]
    public void SchemaVersion_MatchesExpectedAfterInit()
    {
        using var conn = new SqliteConnection($"Data Source={_sqlitePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version LIMIT 1";
        Assert.Equal(2, Convert.ToInt32(cmd.ExecuteScalar()!));
    }

    // ── State Counts Consistency ──

    [Theory]
    [InlineData("json")]
    [InlineData("sqlite")]
    public void GetStateCounts_MatchesActualStates(string backend)
    {
        var index = backend == "sqlite" ? _sqliteIndex : _jsonIndex;

        index.Upsert(MakeEntry("sc1", lifecycleState: "stm"));
        index.Upsert(MakeEntry("sc2", lifecycleState: "stm"));
        index.Upsert(MakeEntry("sc3", lifecycleState: "stm"));
        index.SetLifecycleState("sc2", "ltm");
        index.SetLifecycleState("sc3", "archived");

        var (stm, ltm, archived) = index.GetStateCounts("test");
        Assert.Equal(1, stm);
        Assert.Equal(1, ltm);
        Assert.Equal(1, archived);

        // Verify total
        Assert.Equal(3, stm + ltm + archived);
        Assert.Equal(index.CountInNamespace("test"), stm + ltm + archived);
    }

    // ── Concurrent Operations Preserve Invariants ──

    [Theory]
    [InlineData("json")]
    [InlineData("sqlite")]
    public async Task ConcurrentOperations_PreserveCountInvariant(string backend)
    {
        var index = backend == "sqlite" ? _sqliteIndex : _jsonIndex;

        // Insert 50 entries concurrently
        var insertTasks = Enumerable.Range(0, 50).Select(i =>
            Task.Run(() => index.Upsert(MakeEntry($"conc-{i}", v1: i + 1f))));
        await Task.WhenAll(insertTasks);

        Assert.Equal(50, index.Count);
        Assert.Equal(50, index.GetAll().Count);

        // Delete even entries concurrently
        var deleteTasks = Enumerable.Range(0, 25).Select(i =>
            Task.Run(() => index.Delete($"conc-{i * 2}")));
        await Task.WhenAll(deleteTasks);

        Assert.Equal(25, index.Count);
        Assert.Equal(25, index.GetAll().Count);
    }
}
