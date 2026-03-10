using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class ChecksumTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;

    public ChecksumTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"checksum_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 10);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    [Fact]
    public void Save_CreatesChecksumFile()
    {
        var entry = new CognitiveEntry("c1", new[] { 1f, 0f }, "checkns", "test");
        _persistence.SaveNamespaceSync("checkns", new NamespaceData { Entries = [entry] });

        var checksumPath = Path.Combine(_testDataPath, "checkns.json.sha256");
        Assert.True(File.Exists(checksumPath), "Checksum file should be created alongside data file");

        var checksum = File.ReadAllText(checksumPath).Trim();
        Assert.Equal(64, checksum.Length); // SHA-256 hex string is 64 chars
    }

    [Fact]
    public void Load_WithValidChecksum_Succeeds()
    {
        var entry = new CognitiveEntry("c2", new[] { 1f, 2f }, "valid", "valid data");
        _persistence.SaveNamespaceSync("valid", new NamespaceData { Entries = [entry] });

        var loaded = _persistence.LoadNamespace("valid");
        Assert.Single(loaded.Entries);
        Assert.Equal("c2", loaded.Entries[0].Id);
    }

    [Fact]
    public void Load_WithCorruptedChecksum_StillLoads()
    {
        // Save valid data
        var entry = new CognitiveEntry("c3", new[] { 1f, 0f }, "corrupt", "corrupted checksum test");
        _persistence.SaveNamespaceSync("corrupt", new NamespaceData { Entries = [entry] });

        // Corrupt the checksum file
        var checksumPath = Path.Combine(_testDataPath, "corrupt.json.sha256");
        File.WriteAllText(checksumPath, "0000000000000000000000000000000000000000000000000000000000000000");

        // Should still load (with a warning log, but not fail)
        var loaded = _persistence.LoadNamespace("corrupt");
        Assert.Single(loaded.Entries);
        Assert.Equal("c3", loaded.Entries[0].Id);
    }

    [Fact]
    public void Load_WithNoChecksum_Succeeds()
    {
        // Save valid data
        var entry = new CognitiveEntry("c4", new[] { 1f, 0f }, "legacy", "legacy data");
        _persistence.SaveNamespaceSync("legacy", new NamespaceData { Entries = [entry] });

        // Delete the checksum file to simulate legacy data
        var checksumPath = Path.Combine(_testDataPath, "legacy.json.sha256");
        if (File.Exists(checksumPath))
            File.Delete(checksumPath);

        // Should load fine (no checksum = legacy, pass through)
        var loaded = _persistence.LoadNamespace("legacy");
        Assert.Single(loaded.Entries);
    }

    [Fact]
    public void GlobalEdges_HaveChecksums()
    {
        var edges = new List<GraphEdge> { new("a", "b", "cross_reference") };
        _persistence.ScheduleSaveGlobalEdges(() => edges);
        _persistence.Flush();

        var checksumPath = Path.Combine(_testDataPath, "_edges.json.sha256");
        Assert.True(File.Exists(checksumPath));
    }

    [Fact]
    public void Clusters_HaveChecksums()
    {
        var clusters = new List<SemanticCluster>
        {
            new("cl1", "test", new List<string> { "a" }, "test cluster")
        };
        _persistence.ScheduleSaveClusters(() => clusters);
        _persistence.Flush();

        var checksumPath = Path.Combine(_testDataPath, "_clusters.json.sha256");
        Assert.True(File.Exists(checksumPath));
    }

    [Fact]
    public void StorageVersion_IsPersistedInNamespaceData()
    {
        var entry = new CognitiveEntry("v1", new[] { 1f }, "vertest", "versioned");
        _persistence.SaveNamespaceSync("vertest", new NamespaceData { Entries = [entry] });

        var loaded = _persistence.LoadNamespace("vertest");
        Assert.Equal(1, loaded.StorageVersion);
    }
}
