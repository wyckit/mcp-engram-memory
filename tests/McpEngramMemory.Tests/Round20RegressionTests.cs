using System.Text.Json;
using Microsoft.Data.Sqlite;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Core.Services.Storage.Migration;

namespace McpEngramMemory.Tests;

/// <summary>
/// Deterministic regression controls for the round-20 reconciliation: the receipt-structure
/// validation accepts the PROTOCOL'S OWN record shapes (intent records carry no states;
/// partial claims cover a subset) while still refusing foreign-key maps; and graph rows
/// reverse by exact per-row provenance, including across a PrefixSplit whose placement-level
/// records cannot attribute tenants.
/// </summary>
public class Round20RegressionTests : IDisposable
{
    private readonly string _testDataPath;

    public Round20RegressionTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"round20_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    /// <summary>
    /// The member-map invariant matches the protocol: an INTENT record has members and no
    /// states yet, and a partial claims record covers a subset — both must pass the strict
    /// read (requiring count equality refused the protocol's every second write and wedged
    /// collapse execution outright). A map naming a member the record never claimed still
    /// refuses.
    /// </summary>
    [Fact]
    public void StrictReceiptValidation_AcceptsProtocolShapes_RefusesForeignKeys()
    {
        var intentShaped = new CollapseRecord("c1", "k1", "summary:k1", "test",
            new List<string> { "a", "b" }, new Dictionary<string, string>());
        var partialClaims = new CollapseRecord("c2", "k2", "summary:k2", "test",
            new List<string> { "a", "b", "c" }, new Dictionary<string, string> { ["a"] = "ltm" });
        File.WriteAllText(Path.Combine(_testDataPath, "_collapse_history.json"),
            JsonSerializer.Serialize(new List<CollapseRecord> { intentShaped, partialClaims }));

        using (var pm = new PersistenceManager(_testDataPath, debounceMs: 60_000))
        {
            Assert.True(pm.TryReadCollapseHistory(out var records));
            Assert.Equal(2, records.Count);
        }

        var foreignKey = new CollapseRecord("c3", "k3", "summary:k3", "test",
            new List<string> { "a" }, new Dictionary<string, string> { ["not-a-member"] = "ltm" });
        File.WriteAllText(Path.Combine(_testDataPath, "_collapse_history.json"),
            JsonSerializer.Serialize(new List<CollapseRecord> { foreignKey }));

        using (var pm = new PersistenceManager(_testDataPath, debounceMs: 60_000))
        {
            Assert.False(pm.TryReadCollapseHistory(out var refused));
            Assert.Empty(refused);
        }
    }

    /// <summary>
    /// Graph rows reverse by EXACT per-row provenance. A PrefixSplit re-stamps every tenant
    /// at the source, so placement-level records cannot attribute a row's original tenant —
    /// the per-row manifest can, and both the cluster and the receipt return to the exact
    /// (Ns, TenantId) they carried, while a never-migrated destination resident stays put.
    /// </summary>
    [Fact]
    public void Reverse_PlaysBackPerRowProvenance_AcrossAPrefixSplit()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"round20_prov_{Guid.NewGuid():N}.db");
        try
        {
            using var storage = new SqliteStorageProvider(dbPath, debounceMs: 60_000);
            storage.ScheduleSaveClusters(() => new List<SemanticCluster>
            {
                new("K", "resident", "notes", new List<string> { "m" }, null, null, null),
                new("C", "migrated", "acme::notes", new List<string> { "n" }, null, null, null)
            });
            Assert.True(storage.TryFlush());
            Assert.True(storage.UpsertCollapseRecordSync(new CollapseRecord(
                "collapse:1", "C", "summary:C", "acme::notes",
                new List<string> { "n" }, new Dictionary<string, string>(),
                clusterStamp: "S", clusterInstance: "I")));
            var srcEntry = new CognitiveEntry("n", new[] { 1f, 0f }, "acme::notes", text: "n", lifecycleState: "ltm");
            storage.SaveNamespaceSync("acme::notes", new NamespaceData { Entries = new List<CognitiveEntry> { srcEntry } });
            storage.SaveNamespaceSync("notes", new NamespaceData { Entries = new List<CognitiveEntry>() });

            var tool = new TenantPrefixMigrationTool(storage);
            var manifest = tool.Migrate();
            Assert.NotNull(manifest.GraphRowMoves);
            Assert.Equal(2, manifest.GraphRowMoves!.Count);

            // Forward moved both rows to the placement...
            Assert.True(storage.TryReadCollapseHistory(out var midway));
            Assert.Equal("notes", Assert.Single(midway).Ns);

            tool.Reverse(manifest);

            var clusters = storage.LoadClusters();
            var k = Assert.Single(clusters, c => c.ClusterId == "K");
            Assert.Equal("notes", k.Ns);
            var c = Assert.Single(clusters, x => x.ClusterId == "C");
            Assert.Equal("acme::notes", c.Ns);
            Assert.Equal("", c.TenantId);
            Assert.True(storage.TryReadCollapseHistory(out var reversed));
            var receipt = Assert.Single(reversed);
            Assert.Equal("acme::notes", receipt.Ns);
            Assert.Equal("", receipt.TenantId);
            Assert.Equal("I", receipt.ClusterInstance);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
    /// <summary>
    /// Refuter finding (P1): an EMPTY GraphRowMoves list means "the forward pass provably
    /// moved no graph rows" — the reverse must move none. Conflating empty with null
    /// re-armed the placement-level sweep and re-tenanted rows the pass never touched.
    /// </summary>
    [Fact]
    public void Reverse_EmptyGraphRowProvenance_MovesNoGraphRows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"round20_empty_{Guid.NewGuid():N}.db");
        try
        {
            using var storage = new SqliteStorageProvider(dbPath, debounceMs: 60_000);
            var legacy = new CognitiveEntry("y", new[] { 0f, 1f }, "work", text: "ls", lifecycleState: "ltm");
            storage.SaveNamespaceSync("work", new NamespaceData { Entries = new List<CognitiveEntry> { legacy } });

            var tool = new TenantPrefixMigrationTool(storage);
            var manifest = tool.Migrate(defaultTenantId: "t1");
            Assert.NotNull(manifest.GraphRowMoves);
            Assert.Empty(manifest.GraphRowMoves!);

            // Post-migration, normal operation creates a cluster at (work, t1)...
            storage.ScheduleSaveClusters(() => new List<SemanticCluster>
            {
                new("K", "post", "work", new List<string> { "y" }, null, null, "t1")
            });
            Assert.True(storage.TryFlush());

            tool.Reverse(manifest);

            // ...and Reverse must not sweep it into the legacy tenant.
            var k = Assert.Single(storage.LoadClusters());
            Assert.Equal("t1", k.TenantId);
            Assert.Equal("work", k.Ns);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Refuter finding (P2): the cross-field rule. A receipt holding an INSTALLED claim with
    /// no recorded previous state would let the undo retire the receipt while silently never
    /// restoring that member — the strict read refuses it, while the protocol's paired
    /// claims shape still passes.
    /// </summary>
    [Fact]
    public void StrictReceiptValidation_RefusesAppliedClaimWithoutState()
    {
        var paired = new CollapseRecord("c1", "k1", "summary:k1", "test",
            new List<string> { "a" }, new Dictionary<string, string> { ["a"] = "ltm" },
            appliedLifecycleRevisions: new Dictionary<string, long> { ["a"] = 7 });
        File.WriteAllText(Path.Combine(_testDataPath, "_collapse_history.json"),
            JsonSerializer.Serialize(new List<CollapseRecord> { paired }));
        using (var pm = new PersistenceManager(_testDataPath, debounceMs: 60_000))
            Assert.True(pm.TryReadCollapseHistory(out _));

        var orphanClaim = new CollapseRecord("c2", "k2", "summary:k2", "test",
            new List<string> { "a", "b" }, new Dictionary<string, string> { ["a"] = "ltm" },
            appliedLifecycleRevisions: new Dictionary<string, long> { ["a"] = 7, ["b"] = 9 });
        File.WriteAllText(Path.Combine(_testDataPath, "_collapse_history.json"),
            JsonSerializer.Serialize(new List<CollapseRecord> { orphanClaim }));
        using (var pm = new PersistenceManager(_testDataPath, debounceMs: 60_000))
        {
            Assert.False(pm.TryReadCollapseHistory(out var refused));
            Assert.Empty(refused);
        }
    }

    /// <summary>
    /// Refuter finding (P3): the reverse's OWN manifest carries per-row graph provenance, so
    /// a reverse-of-reverse plays the rows forward again exactly instead of skipping them.
    /// </summary>
    [Fact]
    public void ReverseOfReverse_PlaysGraphRowsForwardAgain()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"round20_rr_{Guid.NewGuid():N}.db");
        try
        {
            using var storage = new SqliteStorageProvider(dbPath, debounceMs: 60_000);
            storage.ScheduleSaveClusters(() => new List<SemanticCluster>
            {
                new("C", "migrated", "acme::notes", new List<string> { "n" }, null, null, null)
            });
            Assert.True(storage.TryFlush());
            var srcEntry = new CognitiveEntry("n", new[] { 1f, 0f }, "acme::notes", text: "n", lifecycleState: "ltm");
            storage.SaveNamespaceSync("acme::notes", new NamespaceData { Entries = new List<CognitiveEntry> { srcEntry } });

            var tool = new TenantPrefixMigrationTool(storage);
            var m1 = tool.Migrate();
            var m2 = tool.Reverse(m1);
            Assert.NotNull(m2.GraphRowMoves);
            Assert.Single(m2.GraphRowMoves!);

            tool.Reverse(m2);

            var c = Assert.Single(storage.LoadClusters());
            Assert.Equal("notes", c.Ns);
            Assert.Equal("acme", c.TenantId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
