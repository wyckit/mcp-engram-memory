using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
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

        // Wait for debounce
        Thread.Sleep(200);

        var loaded = persistence.LoadNamespace("test");
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
}
