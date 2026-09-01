using System.Diagnostics;
using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class PersistenceManagerTests : IDisposable
{
    private readonly string _testDataPath;

    public PersistenceManagerTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"persist_test_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    [Fact]
    public void UpsertCollapseRecordSync_FailureCommitsNothingAndDoesNotPoisonTheStore()
    {
        using var persistence = new PersistenceManager(_testDataPath);

        // Break the write path before anything commits: the history slot's file name occupied
        // by a directory makes both the strict read-back and the atomic replace fail
        // deterministically.
        var historyPath = Path.Combine(_testDataPath, "_collapse_history.json");
        Directory.CreateDirectory(historyPath);

        var failedRecord = new CollapseRecord(
            "collapse:failed", "cluster-f", "summary-f", "ns",
            new List<string> { "m1" }, new Dictionary<string, string>(), tenantId: "");
        Assert.False(persistence.UpsertCollapseRecordSync(failedRecord));

        // False must mean NOTHING was committed: once the path is writable again the store is
        // simply empty — no half-written file, no phantom record.
        Directory.Delete(historyPath);
        Assert.Empty(persistence.LoadCollapseHistory());

        // The failure has not poisoned anything: later record-level upserts land — and, being
        // read-modify-writes, the second JOINS the record already on disk instead of erasing
        // it with a snapshot of one caller's world.
        var first = new CollapseRecord(
            "collapse:first", "cluster-a", "summary-a", "ns",
            new List<string> { "m2" }, new Dictionary<string, string>(), tenantId: "");
        Assert.True(persistence.UpsertCollapseRecordSync(first));

        var second = new CollapseRecord(
            "collapse:second", "cluster-b", "summary-b", "ns",
            new List<string> { "m3" }, new Dictionary<string, string>(), tenantId: "");
        Assert.True(persistence.UpsertCollapseRecordSync(second));

        var final = persistence.LoadCollapseHistory();
        Assert.Equal(2, final.Count);
        Assert.Contains(final, r => r.CollapseId == "collapse:first");
        Assert.Contains(final, r => r.CollapseId == "collapse:second");

        // And the removal twin honors the same read-modify-write discipline.
        Assert.True(persistence.DeleteCollapseRecordSync("collapse:first"));
        Assert.Equal("collapse:second", Assert.Single(persistence.LoadCollapseHistory()).CollapseId);

        // Deleting an absent record commits nothing and reports agreement — the documented
        // no-op semantics, not a rewrite that can spuriously fail.
        Assert.True(persistence.DeleteCollapseRecordSync("collapse:never-existed"));
        Assert.Single(persistence.LoadCollapseHistory());

        // The history file is a self-checksummed envelope committed in ONE atomic replace —
        // no companion file survives a sync commit (a stale one would spuriously refuse good
        // data after a crash between two writes, which is why the checksum moved inside).
        var historyFile = Path.Combine(_testDataPath, "_collapse_history.json");
        Assert.False(File.Exists(historyFile + ".sha256"));
        Assert.Contains("\"checksum\":", File.ReadAllText(historyFile));
        Assert.True(persistence.TryReadCollapseRecord("collapse:second", out var reread));
        Assert.NotNull(reread);
    }

    [Fact]
    public void LoadNamespace_EmptyFile_ReturnsEmpty()
    {
        var persistence = new PersistenceManager(_testDataPath);
        var data = persistence.LoadNamespace("test");
        Assert.Empty(data.Entries);
        persistence.Dispose();
    }

    [Fact]
    public void SaveAndLoad_RoundTrip()
    {
        var persistence = new PersistenceManager(_testDataPath);
        var data = new NamespaceData
        {
            Entries = new List<CognitiveEntry>
            {
                new CognitiveEntry("a", new[] { 1f, 0f }, "test", "hello")
            }
        };
        persistence.SaveNamespaceSync("test", data);

        var loaded = persistence.LoadNamespace("test");
        Assert.Single(loaded.Entries);
        Assert.Equal("a", loaded.Entries[0].Id);
        Assert.Equal("hello", loaded.Entries[0].Text);
        Assert.Equal(new[] { 1f, 0f }, loaded.Entries[0].Vector);
        persistence.Dispose();
    }

    [Fact]
    public void GetPersistedNamespaces_ReturnsExistingFiles()
    {
        var persistence = new PersistenceManager(_testDataPath);
        persistence.SaveNamespaceSync("work", new NamespaceData());
        persistence.SaveNamespaceSync("personal", new NamespaceData());

        var namespaces = persistence.GetPersistedNamespaces();
        Assert.Contains("work", namespaces);
        Assert.Contains("personal", namespaces);
        persistence.Dispose();
    }

    [Fact]
    public void GetPersistedNamespaces_ExcludesEdgesFile()
    {
        var persistence = new PersistenceManager(_testDataPath);
        // Manually write an _edges.json file
        File.WriteAllText(Path.Combine(_testDataPath, "_edges.json"), "[]");
        persistence.SaveNamespaceSync("test", new NamespaceData());

        var namespaces = persistence.GetPersistedNamespaces();
        Assert.DoesNotContain("_edges", namespaces);
        Assert.Contains("test", namespaces);
        persistence.Dispose();
    }

    [Fact]
    public void DebouncedSave_EventuallyWrites()
    {
        var persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        var data = new NamespaceData
        {
            Entries = new List<CognitiveEntry>
            {
                new CognitiveEntry("a", new[] { 1f, 0f }, "test")
            }
        };
        persistence.ScheduleSave("test", () => data);

        // Poll for the debounced write to land instead of a fixed sleep. The 50 ms debounce
        // timer runs on a background thread; a saturated CI ThreadPool can delay its callback
        // well past a fixed 200 ms window, leaving the file unwritten and Assert.Single failing
        // on an empty collection. We still exercise the debounce path (no Flush) — only the
        // false-negative bound is relaxed; the healthy path lands in tens of ms.
        NamespaceData loaded = persistence.LoadNamespace("test");
        var sw = Stopwatch.StartNew();
        while (loaded.Entries.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(25);
            loaded = persistence.LoadNamespace("test");
        }

        Assert.Single(loaded.Entries);
        persistence.Dispose();
    }

    [Fact]
    public void Flush_WritesImmediately()
    {
        var persistence = new PersistenceManager(_testDataPath, debounceMs: 10000);
        persistence.ScheduleSave("test", () => new NamespaceData
        {
            Entries = new List<CognitiveEntry>
            {
                new CognitiveEntry("a", new[] { 1f }, "test")
            }
        });

        // Flush forces immediate write
        persistence.Flush();

        // Load from a new instance to prove it's on disk
        var persistence2 = new PersistenceManager(_testDataPath);
        var loaded = persistence2.LoadNamespace("test");
        Assert.Single(loaded.Entries);
        persistence.Dispose();
        persistence2.Dispose();
    }

    // Issue 13: Corrupted JSON should not crash the server
    [Fact]
    public void LoadNamespace_CorruptedJson_ReturnsEmpty()
    {
        var persistence = new PersistenceManager(_testDataPath);
        // Write corrupt JSON to the namespace file
        File.WriteAllText(Path.Combine(_testDataPath, "test.json"), "{{not valid json!!");

        var data = persistence.LoadNamespace("test");
        Assert.Empty(data.Entries);
        persistence.Dispose();
    }

    [Fact]
    public void LoadGlobalEdges_CorruptedJson_ReturnsEmpty()
    {
        var persistence = new PersistenceManager(_testDataPath);
        File.WriteAllText(Path.Combine(_testDataPath, "_edges.json"), "corrupt");

        var edges = persistence.LoadGlobalEdges();
        Assert.Empty(edges);
        persistence.Dispose();
    }

    [Fact]
    public void LoadClusters_CorruptedJson_ReturnsEmpty()
    {
        var persistence = new PersistenceManager(_testDataPath);
        File.WriteAllText(Path.Combine(_testDataPath, "_clusters.json"), "corrupt");

        var clusters = persistence.LoadClusters();
        Assert.Empty(clusters);
        persistence.Dispose();
    }

    // --- Storage version validation tests ---

    private static readonly JsonSerializerOptions RawJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Write a NamespaceData directly to JSON bypassing PersistenceManager's version stamping.</summary>
    private void WriteRawJson(string ns, NamespaceData data)
    {
        Directory.CreateDirectory(_testDataPath);
        var json = JsonSerializer.Serialize(data, RawJsonOptions);
        File.WriteAllText(Path.Combine(_testDataPath, $"{ns}.json"), json);
    }

    [Fact]
    public void LoadNamespace_EmptyDir_ReturnsCurrentVersion()
    {
        var persistence = new PersistenceManager(_testDataPath);
        var data = persistence.LoadNamespace("nonexistent");
        Assert.Equal(PersistenceManager.CurrentStorageVersion, data.StorageVersion);
        Assert.Empty(data.Entries);
        persistence.Dispose();
    }

    [Fact]
    public void SaveAndLoad_StampsCurrentVersion()
    {
        var persistence = new PersistenceManager(_testDataPath);
        var data = new NamespaceData
        {
            StorageVersion = 0, // intentionally wrong
            Entries = new List<CognitiveEntry>
            {
                new CognitiveEntry("a", new[] { 1f }, "test", "hello")
            }
        };
        persistence.SaveNamespaceSync("test", data);

        // Read raw JSON to verify version was stamped on disk
        var rawJson = File.ReadAllText(Path.Combine(_testDataPath, "test.json"));
        Assert.Contains($"\"storageVersion\": {PersistenceManager.CurrentStorageVersion}", rawJson);

        // Also verify via round-trip load
        var loaded = persistence.LoadNamespace("test");
        Assert.Equal(PersistenceManager.CurrentStorageVersion, loaded.StorageVersion);
        persistence.Dispose();
    }

    [Fact]
    public void LoadNamespace_OlderVersion_MigratesToCurrent()
    {
        // Write a v1 file directly (bypassing SaveNamespaceSync which stamps v2)
        WriteRawJson("old", new NamespaceData
        {
            StorageVersion = 1,
            Entries = new List<CognitiveEntry>
            {
                new CognitiveEntry("a", new[] { 1f, 2f }, "old", "preserved")
            }
        });

        var persistence = new PersistenceManager(_testDataPath);
        var data = persistence.LoadNamespace("old");

        Assert.Equal(PersistenceManager.CurrentStorageVersion, data.StorageVersion);
        Assert.Single(data.Entries);
        Assert.Equal("preserved", data.Entries[0].Text);
        persistence.Dispose();
    }

    [Fact]
    public void LoadNamespace_NewerVersion_ReturnsEmptyWithCurrentVersion()
    {
        // Simulate a file from a future server version
        WriteRawJson("future", new NamespaceData
        {
            StorageVersion = 999,
            Entries = new List<CognitiveEntry>
            {
                new CognitiveEntry("a", new[] { 1f }, "future", "should be rejected")
            }
        });

        var persistence = new PersistenceManager(_testDataPath);
        var data = persistence.LoadNamespace("future");

        // Should reject and return empty
        Assert.Equal(PersistenceManager.CurrentStorageVersion, data.StorageVersion);
        Assert.Empty(data.Entries);
        persistence.Dispose();
    }

    [Fact]
    public void LoadNamespace_V1File_PersistsMigration()
    {
        WriteRawJson("migrate", new NamespaceData
        {
            StorageVersion = 1,
            Entries = new List<CognitiveEntry>
            {
                new CognitiveEntry("x", new[] { 3f }, "migrate", "data")
            }
        });

        var persistence = new PersistenceManager(_testDataPath);
        _ = persistence.LoadNamespace("migrate"); // triggers migration + re-save

        // Read raw file again — migration should have persisted v2 to disk
        var rawJson = File.ReadAllText(Path.Combine(_testDataPath, "migrate.json"));
        Assert.Contains($"\"storageVersion\": {PersistenceManager.CurrentStorageVersion}", rawJson);
        persistence.Dispose();
    }

    // --- HNSW snapshot persistence tests ---

    [Fact]
    public void LoadHnswSnapshot_NoFile_ReturnsNull()
    {
        var persistence = new PersistenceManager(_testDataPath);
        var snapshot = persistence.LoadHnswSnapshot("test");
        Assert.Null(snapshot);
        persistence.Dispose();
    }

    [Fact]
    public void SaveAndLoadHnswSnapshot_RoundTrip()
    {
        var index = new HnswIndex();
        index.Add("a", new float[] { 1, 0, 0 });
        index.Add("b", new float[] { 0, 1, 0 });

        var persistence = new PersistenceManager(_testDataPath);
        var snapshot = index.CreateSnapshot();
        persistence.SaveHnswSnapshotSync("test", snapshot);

        var loaded = persistence.LoadHnswSnapshot("test");
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.NodeIds.Count);
        Assert.Contains("a", loaded.NodeIds);
        Assert.Contains("b", loaded.NodeIds);
        Assert.Equal(snapshot.M, loaded.M);
        Assert.Equal(snapshot.EfConstruction, loaded.EfConstruction);
        persistence.Dispose();
    }

    [Fact]
    public void DeleteHnswSnapshot_RemovesFile()
    {
        var persistence = new PersistenceManager(_testDataPath);
        var snapshot = new HnswSnapshot
        {
            M = 16, EfConstruction = 200,
            NodeIds = new List<string> { "a" },
            NodeLevels = new List<int> { 0 },
            Connections = new List<List<List<int>>> { new() { new() } }
        };
        persistence.SaveHnswSnapshotSync("test", snapshot);

        Assert.NotNull(persistence.LoadHnswSnapshot("test"));
        persistence.DeleteHnswSnapshot("test");
        Assert.Null(persistence.LoadHnswSnapshot("test"));
        persistence.Dispose();
    }

    [Fact]
    public void GetPersistedNamespaces_ExcludesHnswFiles()
    {
        var persistence = new PersistenceManager(_testDataPath);
        persistence.SaveNamespaceSync("test", new NamespaceData());
        persistence.SaveHnswSnapshotSync("test", new HnswSnapshot { NodeIds = new() { "a" } });

        var namespaces = persistence.GetPersistedNamespaces();
        Assert.Contains("test", namespaces);
        // Should not include "test.hnsw" as a namespace
        Assert.DoesNotContain(namespaces, ns => ns.Contains("hnsw"));
        persistence.Dispose();
    }

    [Fact]
    public async Task DeleteNamespaceAsync_AlsoDeletesHnswSnapshot()
    {
        var persistence = new PersistenceManager(_testDataPath);
        persistence.SaveNamespaceSync("test", new NamespaceData());
        persistence.SaveHnswSnapshotSync("test", new HnswSnapshot { NodeIds = new() { "a" } });

        await persistence.DeleteNamespaceAsync("test");

        Assert.Null(persistence.LoadHnswSnapshot("test"));
        persistence.Dispose();
    }
}
