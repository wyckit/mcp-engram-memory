using System.Text.Json;
using Microsoft.Data.Sqlite;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Core.Services.Storage.Migration;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

/// <summary>
/// Deterministic regression controls for the round-19 review findings: the summary handoff
/// is durable before the record advances; merge refuses machinery-owned and receipt-held
/// entries and binds its topology transfers to the authorized namespace; migration dedupes
/// re-run folds, keeps sources on any graph-row failure, and reverses by placement; strict
/// receipt reads refuse nested poison; and the cluster-member and edge-endpoint quarantines
/// close their gaps.
/// </summary>
public class Round19RegressionTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly LifecycleEngine _lifecycle;
    private readonly AccretionScanner _scanner;
    private readonly IntelligenceTools _tools;

    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 3;
        public float[] Embed(string text) => new[] { 0.9f, 0.1f, 0f };
    }

    public Round19RegressionTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"round19_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 60_000);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
        _lifecycle = new LifecycleEngine(_index);
        _scanner = new AccretionScanner(_index, _persistence);
        var access = new NamespaceAccess(new NamespaceRegistry(_index, new StubEmbedding()), AgentIdentity.Default);
        _tools = new IntelligenceTools(_index, _graph, new StubEmbedding(), _scanner, _clusters, _lifecycle, access);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    private string SeedAndDetect(string tenantId = "")
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm", tenantId: tenantId));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm", tenantId: tenantId));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm", tenantId: tenantId));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm", tenantId: tenantId));
        return _scanner.ScanNamespace("test", tenantId: tenantId).NewCollapses[0].CollapseId;
    }

    /// <summary>
    /// Finding 1: the handoff delete is proved DURABLE before the record advances. After a
    /// teardown and a partial retry, a crash-simulating reload must see the prior summary
    /// gone from disk — the old shape left it durable while the durable record already
    /// named the new instance, unaddressable by every cleanup forever.
    /// </summary>
    [Fact]
    public void HandoffDelete_IsDurableBeforeTheRecordAdvances()
    {
        var collapseId = SeedAndDetect();
        _index.Delete("d");
        Assert.StartsWith("Error:", _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));
        var record = Assert.Single(_scanner.GetCollapseHistory("test", tenantId: ""));
        Assert.True(_persistence.TryFlush()); // the prior summary is durable on disk

        Assert.DoesNotContain("Error:", _clusters.UpdateCluster(record.ClusterId, addIds: null,
            removeIds: new List<string> { "a", "b", "c", "d" }, label: null, tenantId: ""));
        Assert.Equal(EmptyClusterRemoval.Removed, _clusters.RemoveClusterIfEmpty(record.ClusterId, tenantId: ""));
        Assert.StartsWith("Error:", _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));

        // Crash-simulating reload: a fresh provider over the same directory reads only what
        // is durable. The record has advanced past the prior instance, so the prior summary
        // must not be durable anywhere.
        using var persistence2 = new PersistenceManager(_testDataPath, debounceMs: 60_000);
        Assert.True(persistence2.TryReadCollapseRecord(collapseId, out var durable));
        var priorSummaryOnDisk = persistence2.LoadNamespace("test").Entries
            .FirstOrDefault(e => e.Id == record.SummaryEntryId && e.SourceClusterInstance == record.ClusterInstance);
        Assert.Null(priorSummaryOnDisk);
    }

    /// <summary>
    /// Finding 3: merge refuses machinery-owned and receipt-held entries outright.
    /// </summary>
    [Fact]
    public void MergeMemories_RefusesSummariesAndArchivedEntries()
    {
        _index.Upsert(new CognitiveEntry("plain", new[] { 1f, 0f, 0f }, "m", text: "plain", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("summaryish", new[] { 0f, 1f, 0f }, "m", text: "s", lifecycleState: "ltm")
        {
            IsSummaryNode = true,
            SourceClusterId = "k"
        });
        _index.Upsert(new CognitiveEntry("archived", new[] { 0f, 0f, 1f }, "m", text: "a", lifecycleState: "archived"));

        var refusedSummary = _tools.MergeMemories("plain", "summaryish", "m");
        Assert.StartsWith("Error:", refusedSummary);
        Assert.Contains("machinery-owned", refusedSummary);

        var refusedArchived = _tools.MergeMemories("plain", "archived", "m");
        Assert.StartsWith("Error:", refusedArchived);
        Assert.Contains("collapse receipts", refusedArchived);
    }

    /// <summary>
    /// Finding 2 (edges): the namespace-bound transfer refuses — all or nothing — when any
    /// incident endpoint resolves outside the authorized namespace.
    /// </summary>
    [Fact]
    public void TransferEdges_NamespaceBound_RefusesForeignEndpoints()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "ns1", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f, 0f }, "ns1", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("far", new[] { 0f, 0f, 1f }, "ns2", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error", _graph.AddEdge(new GraphEdge("a", "far", "related", 1.0f, null, tenantId: "")));

        Assert.Equal(0, _graph.TransferEdges("a", "b", tenantId: "", onlyIfWithinNs: "ns1"));
        // The foreign-endpoint edge is untouched.
        Assert.Single(_graph.GetNeighbors("a", relation: null, direction: "both", tenantId: "").Neighbors);
    }

    /// <summary>
    /// Finding 2 (clusters): the namespace-bound membership transfer re-homes only clusters
    /// IN the authorized namespace; a foreign namespace's cluster keeps its member.
    /// </summary>
    [Fact]
    public void TransferMembership_NamespaceBound_OnlyRewiresAuthorizedClusters()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "ns1", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f, 0f }, "ns1", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error:", _clusters.CreateCluster("c1", "ns1", new List<string> { "a" }, null, tenantId: ""));
        Assert.DoesNotContain("Error:", _clusters.CreateCluster("c2", "ns2", new List<string> { "a" }, null, tenantId: ""));

        Assert.Equal(1, _clusters.TransferMembership("a", "b", tenantId: "", onlyClustersInNs: "ns1"));

        Assert.Contains(_clusters.GetClusterMembershipsForEntry("b", tenantId: ""), m => m.ClusterId == "c1");
        Assert.Contains(_clusters.GetClusterMembershipsForEntry("a", tenantId: ""), m => m.ClusterId == "c2");
    }

    /// <summary>
    /// Finding 4: a re-run's fold dedupes by (tenant, id) — a destination already holding
    /// copies from an aborted pass ends with ONE row per entry, not duplicate-id rows.
    /// </summary>
    [Fact]
    public void Migration_RerunFold_DedupesByTenantAndId()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"round19_fold_{Guid.NewGuid():N}.db");
        try
        {
            using var storage = new SqliteStorageProvider(dbPath, debounceMs: 60_000);
            var e1 = new CognitiveEntry("e1", new[] { 1f, 0f }, "acme::work", text: "one", lifecycleState: "ltm");
            storage.SaveNamespaceSync("acme::work", new NamespaceData { Entries = new List<CognitiveEntry> { e1 } });
            // The destination already holds a copy, as after an aborted earlier pass.
            var copy = new CognitiveEntry("e1", new[] { 1f, 0f }, "work", text: "one", lifecycleState: "ltm", tenantId: "acme");
            storage.SaveNamespaceSync("work", new NamespaceData { Entries = new List<CognitiveEntry> { copy } });

            new TenantPrefixMigrationTool(storage).Migrate();

            Assert.Single(storage.LoadNamespace("work").Entries);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Finding 5: when the graph-row move cannot complete (unreadable history here), the
    /// SOURCE namespaces are kept so a re-run can converge — deleting them made the receipts
    /// permanently unmovable.
    /// </summary>
    [Fact]
    public void Migration_UnreadableHistory_KeepsSources()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"round19_keep_{Guid.NewGuid():N}.db");
        try
        {
            using var storage = new SqliteStorageProvider(dbPath, debounceMs: 60_000);
            var e1 = new CognitiveEntry("e1", new[] { 1f, 0f }, "acme::work", text: "one", lifecycleState: "ltm");
            storage.SaveNamespaceSync("acme::work", new NamespaceData { Entries = new List<CognitiveEntry> { e1 } });
            // A checksum-refuted history row makes the strict read refuse.
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO global_data (key, json_data, checksum) VALUES ('collapse_history', '[]', 'bogus')";
                cmd.ExecuteNonQuery();
            }

            var manifest = new TenantPrefixMigrationTool(storage).Migrate();

            Assert.Contains(manifest.WarningList, w => w.Contains("could not be strictly read"));
            Assert.Contains("acme::work", storage.GetPersistedNamespaces());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Finding 6: Reverse moves graph rows by PLACEMENT — a cluster that lived at the
    /// destination before the migration (or was created there since) is never swept into a
    /// source it never came from, while the migrated cluster moves back.
    /// </summary>
    [Fact]
    public void Reverse_SparesNeverMigratedDestinationRows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"round19_rev_{Guid.NewGuid():N}.db");
        try
        {
            using var storage = new SqliteStorageProvider(dbPath, debounceMs: 60_000);
            // Pre-existing destination content: cluster K at 'notes', legacy tenant.
            storage.ScheduleSaveClusters(() => new List<SemanticCluster>
            {
                new("K", "resident", "notes", new List<string> { "m" }, null, null, null),
                new("C", "migrated", "acme::notes", new List<string> { "n" }, null, null, null)
            });
            Assert.True(storage.TryFlush());
            var srcEntry = new CognitiveEntry("n", new[] { 1f, 0f }, "acme::notes", text: "n", lifecycleState: "ltm");
            storage.SaveNamespaceSync("acme::notes", new NamespaceData { Entries = new List<CognitiveEntry> { srcEntry } });
            storage.SaveNamespaceSync("notes", new NamespaceData { Entries = new List<CognitiveEntry>() });

            var tool = new TenantPrefixMigrationTool(storage);
            var manifest = tool.Migrate();
            tool.Reverse(manifest);

            var clusters = storage.LoadClusters();
            var k = Assert.Single(clusters, c => c.ClusterId == "K");
            Assert.Equal("notes", k.Ns);
            Assert.Equal("", k.TenantId);
            var c = Assert.Single(clusters, x => x.ClusterId == "C");
            Assert.Equal("acme::notes", c.Ns);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Finding 8: nested poison — a null member id inside an otherwise well-formed receipt —
    /// refuses the strict read and is dropped by the lenient load.
    /// </summary>
    [Fact]
    public void NestedPoisonReceipts_StrictRefuses_LenientDrops()
    {
        var record = new CollapseRecord("c1", "k", "summary:k", "test",
            new List<string> { "m" }, new Dictionary<string, string>());
        var json = JsonSerializer.Serialize(new List<CollapseRecord> { record });
        var poisoned = json.Replace("\"memberIds\":[\"m\"]", "\"memberIds\":[null]");
        Assert.NotEqual(json, poisoned);
        File.WriteAllText(Path.Combine(_testDataPath, "_collapse_history.json"), poisoned);

        using var persistence2 = new PersistenceManager(_testDataPath, debounceMs: 60_000);
        Assert.False(persistence2.TryReadCollapseHistory(out var strict));
        Assert.Empty(strict);
        Assert.Empty(persistence2.LoadCollapseHistory());
    }

    /// <summary>
    /// Finding 10: a null member id inside an otherwise well-formed CLUSTER row is stripped
    /// on load instead of throwing out of every membership walk.
    /// </summary>
    [Fact]
    public void NullClusterMemberIds_AreStrippedOnLoad()
    {
        var good = JsonSerializer.Serialize(new SemanticCluster("k", "test", new List<string> { "x" }, "label"));
        var poisoned = good.Replace("\"memberIds\":[\"x\"]", "\"memberIds\":[null,\"x\"]");
        Assert.NotEqual(good, poisoned);
        File.WriteAllText(Path.Combine(_testDataPath, "_clusters.json"), $"[{poisoned}]");

        using var persistence2 = new PersistenceManager(_testDataPath, debounceMs: 60_000);
        using var index2 = new CognitiveIndex(persistence2);
        var clusters2 = new ClusterManager(index2, persistence2);

        var listing = Assert.Single(clusters2.ListClusters("test", tenantId: ""));
        Assert.Equal("k", listing.ClusterId);
    }

    /// <summary>
    /// Finding 11: whitespace edge endpoints are quarantined like null ones — a row that no
    /// API could ever address must not load (and re-persist) forever.
    /// </summary>
    [Fact]
    public void WhitespaceEdgeEndpoints_AreQuarantinedOnLoad()
    {
        var good = JsonSerializer.Serialize(new GraphEdge("a", "b", "related"));
        var blankSource = good.Replace("\"sourceId\":\"a\"", "\"sourceId\":\" \"");
        Assert.NotEqual(good, blankSource);
        File.WriteAllText(Path.Combine(_testDataPath, "_edges.json"), $"[{good},{blankSource}]");

        using var persistence2 = new PersistenceManager(_testDataPath, debounceMs: 60_000);
        using var index2 = new CognitiveIndex(persistence2);
        index2.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "g", lifecycleState: "ltm"));
        index2.Upsert(new CognitiveEntry("b", new[] { 0f, 1f, 0f }, "g", lifecycleState: "ltm"));
        var graph2 = new KnowledgeGraph(persistence2, index2);

        Assert.Single(graph2.GetNeighbors("b", relation: null, direction: "both", tenantId: "").Neighbors);
    }
    /// <summary>
    /// Refuter finding (P1): DefaultAssigned stamps ONLY legacy rows. An already-tenanted
    /// row in a bare namespace — folded in by a prior partial pass, or written under column
    /// tenancy — keeps its tenant instead of being flattened onto the default.
    /// </summary>
    [Fact]
    public void Migration_DefaultAssigned_LeavesTenantedRowsAlone()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"round19_da_{Guid.NewGuid():N}.db");
        try
        {
            using var storage = new SqliteStorageProvider(dbPath, debounceMs: 60_000);
            var tenanted = new CognitiveEntry("x", new[] { 1f, 0f }, "work", text: "as", lifecycleState: "ltm", tenantId: "acme");
            var legacy = new CognitiveEntry("y", new[] { 0f, 1f }, "work", text: "ls", lifecycleState: "ltm");
            storage.SaveNamespaceSync("work", new NamespaceData { Entries = new List<CognitiveEntry> { tenanted, legacy } });

            var manifest = new TenantPrefixMigrationTool(storage).Migrate(defaultTenantId: "t");

            var rows = storage.LoadNamespace("work").Entries;
            Assert.Equal("acme", Assert.Single(rows, r => r.Id == "x").TenantId);
            Assert.Equal("t", Assert.Single(rows, r => r.Id == "y").TenantId);
            Assert.Single(manifest.Records); // only the legacy row was recorded as moved
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Refuter finding (P2): the fold's collision rule is identity-compared. A resident
    /// destination row with a DIFFERENT occupation (a newer direct write, not an aborted
    /// re-run copy) survives the fold; the stale source copy is discarded with a warning.
    /// </summary>
    [Fact]
    public void Migration_Fold_KeepsDifferingResidentRow()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"round19_res_{Guid.NewGuid():N}.db");
        try
        {
            using var storage = new SqliteStorageProvider(dbPath, debounceMs: 60_000);
            var stale = new CognitiveEntry("x", new[] { 1f, 0f }, "acme::work", text: "stale", lifecycleState: "ltm");
            stale.Revision = 3;
            storage.SaveNamespaceSync("acme::work", new NamespaceData { Entries = new List<CognitiveEntry> { stale } });
            var newer = new CognitiveEntry("x", new[] { 1f, 0f }, "work", text: "newer", lifecycleState: "ltm", tenantId: "acme");
            newer.Revision = 9;
            storage.SaveNamespaceSync("work", new NamespaceData { Entries = new List<CognitiveEntry> { newer } });

            var manifest = new TenantPrefixMigrationTool(storage).Migrate();

            Assert.Equal("newer", Assert.Single(storage.LoadNamespace("work").Entries).Text);
            Assert.Contains(manifest.WarningList, w => w.Contains("different occupation"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
