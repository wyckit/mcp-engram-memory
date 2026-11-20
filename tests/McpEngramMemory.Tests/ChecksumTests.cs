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
    public void Load_WithCorruptedChecksum_DropsCorruptedData()
    {
        // Save valid data
        var entry = new CognitiveEntry("c3", new[] { 1f, 0f }, "corrupt", "corrupted checksum test");
        _persistence.SaveNamespaceSync("corrupt", new NamespaceData { Entries = [entry] });

        // Corrupt the checksum file
        var checksumPath = Path.Combine(_testDataPath, "corrupt.json.sha256");
        File.WriteAllText(checksumPath, "0000000000000000000000000000000000000000000000000000000000000000");

        // Aligned with SQLite and SQL Server: checksum-mismatched payloads are dropped rather than returned
        var loaded = _persistence.LoadNamespace("corrupt");
        Assert.Empty(loaded.Entries);
    }

    [Fact]
    public void LoadGlobalFilesAndHnsw_WithCorruptedChecksum_DropCorruptedPayloads()
    {
        _persistence.ScheduleSaveGlobalEdges(() => [new GraphEdge("a", "b", "cross_reference")]);
        _persistence.ScheduleSaveClusters(() => [new SemanticCluster("cl1", "test", ["a"], "lbl")]);
        _persistence.SaveHnswSnapshotSync("test", new HnswSnapshot { NodeIds = ["a"] });
        Assert.True(_persistence.TryFlush());

        string badHash = new string('0', 64);
        File.WriteAllText(Path.Combine(_testDataPath, "_edges.json.sha256"), badHash);
        File.WriteAllText(Path.Combine(_testDataPath, "_clusters.json.sha256"), badHash);
        File.WriteAllText(Path.Combine(_testDataPath, "test.hnsw.json.sha256"), badHash);

        Assert.Empty(_persistence.LoadGlobalEdges());
        Assert.Empty(_persistence.LoadClusters());
        Assert.Null(_persistence.LoadHnswSnapshot("test"));
    }

    [Fact]
    public void AtomicChecksumWrite_LeavesNoTempFilesBehind()
    {
        var entry = new CognitiveEntry("atomic-1", new[] { 1f, 0f }, "atomicns", "atomic checksum test");
        _persistence.SaveNamespaceSync("atomicns", new NamespaceData { Entries = [entry] });

        var leftoverTmps = Directory.GetFiles(_testDataPath, "*.tmp");
        Assert.Empty(leftoverTmps);
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
        Assert.Equal(PersistenceManager.CurrentStorageVersion, loaded.StorageVersion);
    }

    [Fact]
    public void InterruptedUpdate_BetweenPayloadAndChecksum_PreservesPreviouslyCommittedRecord()
    {
        var r1 = new CognitiveEntry("r1", new[] { 1f, 0f }, "torn_ns", "committed before interruption");
        var r2 = new CognitiveEntry("r2", new[] { 0f, 1f }, "torn_ns", "uncommitted during interruption");
        var r3 = new CognitiveEntry("r3", new[] { 0.5f, 0.5f }, "torn_ns", "committed after recovery");

        // 1. Commit record 1 cleanly.
        _persistence.SaveNamespaceSync("torn_ns", new NamespaceData { Entries = [r1] });
        var nsPath = Path.Combine(_testDataPath, "torn_ns.json");
        var checksumPath = nsPath + ".sha256";
        var r1Checksum = File.ReadAllText(checksumPath).Trim();

        // 2. Simulate an interruption strictly between replacing the JSON payload and replacing .sha256.
        _persistence.OnAfterPayloadBeforeChecksumWrite = _ =>
            throw new IOException("Simulated crash between payload and checksum replace");
        try
        {
            Assert.Throws<IOException>(() =>
                _persistence.SaveNamespaceSync("torn_ns", new NamespaceData { Entries = [r1, r2] }));
        }
        finally
        {
            _persistence.OnAfterPayloadBeforeChecksumWrite = null;
        }

        // Verify the torn on-disk state: new valid payload + previous checksum.
        Assert.Contains("\"r2\"", File.ReadAllText(nsPath));
        Assert.Equal(r1Checksum, File.ReadAllText(checksumPath).Trim());

        // 3. Restart on a fresh PersistenceManager: previously committed record r1 MUST remain readable.
        using (var restarted1 = new PersistenceManager(_testDataPath, debounceMs: 10))
        {
            var recovered = restarted1.LoadNamespace("torn_ns");
            var recoveredEntry = Assert.Single(recovered.Entries);
            Assert.Equal("r1", recoveredEntry.Id);
            Assert.Equal("committed before interruption", recoveredEntry.Text);
        }

        // 4. A second restart without writes still recovers r1 cleanly.
        using (var restarted2 = new PersistenceManager(_testDataPath, debounceMs: 10))
        {
            var recovered = restarted2.LoadNamespace("torn_ns");
            Assert.Equal("r1", Assert.Single(recovered.Entries).Id);

            // 5. Subsequent commit heals the primary + checksum pair.
            restarted2.SaveNamespaceSync("torn_ns", new NamespaceData { Entries = [r1, r3] });
        }

        using (var restarted3 = new PersistenceManager(_testDataPath, debounceMs: 10))
        {
            var final = restarted3.LoadNamespace("torn_ns");
            Assert.Equal(2, final.Entries.Count);
            Assert.Contains(final.Entries, e => e.Id == "r1");
            Assert.Contains(final.Entries, e => e.Id == "r3");
        }
    }

    [Fact]
    public void InterruptedUpdate_BlackBoxPreviousChecksumSwap_RecoversPriorSnapshotAndReadableWhenChecksumMatches()
    {
        var r1 = new CognitiveEntry("r1", new[] { 1f, 0f }, "swap_ns", "first committed");
        var r2 = new CognitiveEntry("r2", new[] { 0f, 1f }, "swap_ns", "second committed");

        _persistence.SaveNamespaceSync("swap_ns", new NamespaceData { Entries = [r1] });
        var checksumPath = Path.Combine(_testDataPath, "swap_ns.json.sha256");
        var r1Checksum = File.ReadAllText(checksumPath);

        _persistence.SaveNamespaceSync("swap_ns", new NamespaceData { Entries = [r1, r2] });
        var r2Checksum = File.ReadAllText(checksumPath);

        // Simulate stop between payload and checksum: valid new payload paired with previous checksum.
        File.WriteAllText(checksumPath, r1Checksum);
        using (var torn = new PersistenceManager(_testDataPath, debounceMs: 10))
        {
            var loaded = torn.LoadNamespace("swap_ns");
            var entry = Assert.Single(loaded.Entries);
            Assert.Equal("r1", entry.Id);
            Assert.Equal("first committed", entry.Text);
        }

        // With matching checksum restored, both records are readable.
        File.WriteAllText(checksumPath, r2Checksum);
        using (var healed = new PersistenceManager(_testDataPath, debounceMs: 10))
        {
            var loaded = healed.LoadNamespace("swap_ns");
            Assert.Equal(2, loaded.Entries.Count);
            Assert.Contains(loaded.Entries, e => e.Id == "r1");
            Assert.Contains(loaded.Entries, e => e.Id == "r2");
        }
    }

    [Fact]
    public void InterruptedUpdate_GlobalFiles_PreservePreviouslyCommittedState()
    {
        // Commit v1 of all global control-plane files.
        _persistence.ScheduleSaveGlobalEdges(() => [new GraphEdge("a1", "b1", "cross_reference")]);
        _persistence.ScheduleSaveClusters(() => [new SemanticCluster("cl1", "ns", ["a1"], "v1 cluster")]);
        _persistence.ScheduleSaveDecayConfigs(() => new Dictionary<string, DecayConfig>
        {
            ["ns"] = new("ns", decayRate: 0.25f)
        });
        Assert.True(_persistence.TryFlush());

        // Simulate crash between payload write and checksum write on v2 updates.
        _persistence.OnAfterPayloadBeforeChecksumWrite = _ =>
            throw new IOException("Simulated crash between global payload and checksum");
        try
        {
            _persistence.ScheduleSaveGlobalEdges(() => [new GraphEdge("a2", "b2", "cross_reference")]);
            _persistence.ScheduleSaveClusters(() => [new SemanticCluster("cl2", "ns", ["a2"], "v2 cluster")]);
            _persistence.ScheduleSaveDecayConfigs(() => new Dictionary<string, DecayConfig>
            {
                ["ns"] = new("ns", decayRate: 0.99f)
            });
            Assert.False(_persistence.TryFlush());
        }
        finally
        {
            _persistence.OnAfterPayloadBeforeChecksumWrite = null;
        }

        // Restart recovers v1 of edges, clusters, and decay configs rather than returning empty.
        using var restarted = new PersistenceManager(_testDataPath, debounceMs: 10);
        Assert.Equal("a1", Assert.Single(restarted.LoadGlobalEdges()).SourceId);
        Assert.Equal("cl1", Assert.Single(restarted.LoadClusters()).ClusterId);
        Assert.Equal(0.25f, restarted.LoadDecayConfigs()["ns"].DecayRate);
    }

    [Fact]
    public void CorruptedPrimaryAndCorruptedBackup_FailsClosed()
    {
        var r1 = new CognitiveEntry("r1", new[] { 1f, 0f }, "both_bad", "v1");
        var r2 = new CognitiveEntry("r2", new[] { 0f, 1f }, "both_bad", "v2");
        _persistence.SaveNamespaceSync("both_bad", new NamespaceData { Entries = [r1] });
        _persistence.SaveNamespaceSync("both_bad", new NamespaceData { Entries = [r1, r2] });

        var nsPath = Path.Combine(_testDataPath, "both_bad.json");
        Assert.True(File.Exists(nsPath + ".bak"));

        File.WriteAllText(nsPath + ".sha256", new string('0', 64));
        File.WriteAllText(nsPath + ".bak", new string('f', 64) + "\n{\"tampered\":true}");

        using var restarted = new PersistenceManager(_testDataPath, debounceMs: 10);
        Assert.Empty(restarted.LoadNamespace("both_bad").Entries);
    }

    [Fact]
    public async Task DeleteNamespace_AndTenantDelete_NeverResurrectDeletedDataFromBackup()
    {
        var a1 = new CognitiveEntry("e1", new[] { 1f, 0f }, "shared", "tenant-a-secret", tenantId: "a");
        var b1 = new CognitiveEntry("e1", new[] { 0f, 1f }, "shared", "tenant-b-v1", tenantId: "b");
        var b2 = new CognitiveEntry("e2", new[] { 0f, 1f }, "shared", "tenant-b-v2", tenantId: "b");

        _persistence.SaveNamespaceSync("shared", new NamespaceData { Entries = [a1, b1] });
        _persistence.SaveNamespaceSync("shared", new NamespaceData { Entries = [a1, b1, b2] });

        // Delete tenant "a": backup must be refreshed so tenant "a" is gone from both primary and .bak.
        await _persistence.DeleteNamespaceAsync("shared", "a");

        // Simulate a later torn primary write on shared.json.
        var nsPath = Path.Combine(_testDataPath, "shared.json");
        File.WriteAllText(nsPath + ".sha256", new string('0', 64));

        using (var restarted = new PersistenceManager(_testDataPath, debounceMs: 10))
        {
            var recovered = restarted.LoadNamespace("shared").Entries;
            Assert.Equal(2, recovered.Count);
            Assert.DoesNotContain(recovered, e => e.TenantId == "a");
            Assert.All(recovered, e => Assert.Equal("b", e.TenantId));
        }

        // Full delete removes primary, checksum, and backup completely.
        await _persistence.DeleteNamespaceAsync("shared");
        Assert.False(File.Exists(nsPath));
        Assert.False(File.Exists(nsPath + ".sha256"));
        Assert.False(File.Exists(nsPath + ".bak"));
        Assert.Empty(_persistence.LoadNamespace("shared").Entries);
    }
}
