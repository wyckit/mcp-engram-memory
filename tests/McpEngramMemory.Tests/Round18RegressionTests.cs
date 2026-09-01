using System.Text.Json;
using Microsoft.Data.Sqlite;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Core.Services.Storage.Migration;

namespace McpEngramMemory.Tests;

/// <summary>
/// Deterministic regression controls for the round-18 review findings: the summary-instance
/// handoff deletes the prior side before the record advances; re-embedding and tenant
/// migration preserve summary ownership; a full save subsumes same-namespace incremental
/// work on flush and timer paths alike; every remaining loaded collection quarantines
/// null-shaped rows (entries collection itself, cluster rows, edge rows) while STRICT
/// receipt reads refuse them; and the membership witness is genuine set equality.
/// </summary>
public class Round18RegressionTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly ClusterManager _clusters;
    private readonly LifecycleEngine _lifecycle;
    private readonly AccretionScanner _scanner;

    public Round18RegressionTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"round18_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 60_000);
        _index = new CognitiveIndex(_persistence);
        _clusters = new ClusterManager(_index, _persistence);
        _lifecycle = new LifecycleEngine(_index);
        _scanner = new AccretionScanner(_index, _persistence);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 3;
        public float[] Embed(string text) => new[] { 0.9f, 0.1f, 0f };
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
    /// Finding 1: the instance handoff preserves BOTH sides. The prior attempt's summary is
    /// deleted under the record's still-current authority BEFORE the record advances, so at
    /// no durable point does a record name an instance while a summary it cannot address
    /// survives. The crash-window half (a process death between the advance and the retry's
    /// own store) is not deterministically constructible without a seam; this control pins
    /// the reachable end-to-end property — after a same-lineage teardown and partial retry,
    /// the resident summary is always one the CURRENT record owns, and a full undo leaves
    /// nothing behind.
    /// </summary>
    [Fact]
    public void RetryRecreation_LeavesOnlyARecordOwnedSummary()
    {
        var collapseId = SeedAndDetect();
        _index.Delete("d");
        Assert.StartsWith("Error:", _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));
        var record = Assert.Single(_scanner.GetCollapseHistory("test", tenantId: ""));
        Assert.NotNull(_index.Get(record.SummaryEntryId, "test", tenantId: ""));

        // The cluster is torn down (summary left behind) and the retry takes the
        // fresh-create path: handoff delete, record advance, its own summary store.
        Assert.DoesNotContain("Error:", _clusters.UpdateCluster(record.ClusterId, addIds: null,
            removeIds: new List<string> { "a", "b", "c", "d" }, label: null, tenantId: ""));
        Assert.Equal(EmptyClusterRemoval.Removed, _clusters.RemoveClusterIfEmpty(record.ClusterId, tenantId: ""));
        Assert.StartsWith("Error:", _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));

        // The retry's own summary stands, owned by the ADVANCED record...
        Assert.NotNull(_index.Get(record.SummaryEntryId, "test", tenantId: ""));

        // ...and a full undo addresses it: no summary of this lineage survives.
        Assert.StartsWith("Reversed", _scanner.UndoCollapse(collapseId, _lifecycle, _clusters, tenantId: ""));
        Assert.Null(_index.Get(record.SummaryEntryId, "test", tenantId: ""));
    }

    /// <summary>
    /// Finding 2: re-embedding preserves summary OWNERSHIP. A rebuilt summary keeps its
    /// stamp and instance, so the ownership read screens keep serving it.
    /// </summary>
    [Fact]
    public void RebuildEmbeddings_PreservesSummaryOwnership()
    {
        _index.Upsert(new CognitiveEntry("x", new[] { 0f, 1f, 0f }, "test", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error:", _clusters.CreateCluster("k", "test", new List<string> { "x" }, "mine", tenantId: ""));
        Assert.DoesNotContain("Error:", _clusters.StoreSummary("k", "the summary", new[] { 0f, 1f, 0f }, tenantId: ""));
        Assert.NotNull(_clusters.GetCluster("k", tenantId: "")!.SummaryEntry);

        _index.RebuildEmbeddings("test", new StubEmbedding(), tenantId: "");

        var served = _clusters.GetCluster("k", tenantId: "")!.SummaryEntry;
        Assert.NotNull(served);
        Assert.Equal("the summary", served!.Text);
        Assert.True(Assert.Single(_clusters.ListClusters("test", tenantId: "")).HasSummary);
    }

    /// <summary>
    /// Finding 9: tenant migration preserves summary OWNERSHIP — the moved entry keeps its
    /// stamp and instance.
    /// </summary>
    [Fact]
    public void TenantMigration_PreservesSummaryOwnership()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"round18_mig_{Guid.NewGuid():N}.db");
        try
        {
            using var storage = new SqliteStorageProvider(dbPath, debounceMs: 60_000);
            var summary = new CognitiveEntry("summary:k", new[] { 1f, 0f }, "acme::work",
                text: "owned summary", category: "cluster-summary", lifecycleState: "ltm")
            {
                IsSummaryNode = true,
                SourceClusterId = "k",
                SourceClusterStamp = "stamp-S",
                SourceClusterInstance = "instance-I"
            };
            summary.Revision = 7;
            summary.LifecycleRevision = 9;
            storage.SaveNamespaceSync("acme::work", new NamespaceData { Entries = new List<CognitiveEntry> { summary } });
            // Graph-level rows that name the prefixed namespace must move with the entries.
            storage.ScheduleSaveClusters(() => new List<SemanticCluster>
            {
                new("k", "cluster", "acme::work", new List<string> { "m" }, null, "summary:k", null)
                {
                    CreationStamp = "stamp-S",
                    InstanceId = "instance-I"
                }
            });
            Assert.True(storage.TryFlush());
            Assert.True(storage.UpsertCollapseRecordSync(new CollapseRecord(
                "collapse:1", "k", "summary:k", "acme::work",
                new List<string> { "m" }, new Dictionary<string, string>(),
                clusterStamp: "stamp-S", clusterInstance: "instance-I")));

            var manifest = new TenantPrefixMigrationTool(storage).Migrate();

            var moved = Assert.Single(storage.LoadNamespace("work").Entries);
            Assert.Equal("acme", moved.TenantId);
            Assert.Equal("stamp-S", moved.SourceClusterStamp);
            Assert.Equal("instance-I", moved.SourceClusterInstance);
            // The WITNESSES survive the clone — receipts staged against them must keep matching.
            Assert.Equal(7, moved.Revision);
            Assert.Equal(9, moved.LifecycleRevision);
            // The cluster row and the collapse receipt moved with their entries.
            var movedCluster = Assert.Single(storage.LoadClusters());
            Assert.Equal("work", movedCluster.Ns);
            Assert.Equal("acme", movedCluster.TenantId);
            Assert.Equal("stamp-S", movedCluster.CreationStamp);
            Assert.Equal("instance-I", movedCluster.InstanceId);
            Assert.True(storage.TryReadCollapseHistory(out var receipts));
            var movedReceipt = Assert.Single(receipts);
            Assert.Equal("work", movedReceipt.Ns);
            Assert.Equal("acme", movedReceipt.TenantId);
            Assert.Equal("instance-I", movedReceipt.ClusterInstance);
            Assert.Empty(manifest.WarningList);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Finding 3, timer path: a full save committing from its debounce timer SUBSUMES the
    /// namespace's pending incremental work — a frozen increment firing after it can no
    /// longer overwrite fresher rows with stale values.
    /// </summary>
    [Fact]
    public async Task SqliteTimers_FullSaveSubsumesPendingIncrements()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"round18_sub_{Guid.NewGuid():N}.db");
        try
        {
            using var provider = new SqliteStorageProvider(dbPath, debounceMs: 30);
            var v1 = new CognitiveEntry("e", new[] { 1f, 0f }, "sub", text: "v1", lifecycleState: "ltm");
            var v2 = new CognitiveEntry("e", new[] { 1f, 0f }, "sub", text: "v2", lifecycleState: "ltm");
            provider.ScheduleUpsertEntry("sub", v1);
            provider.ScheduleSave("sub", () => new NamespaceData { Entries = new List<CognitiveEntry> { v2 } });

            await Task.Delay(900);

            Assert.Equal("v2", Assert.Single(provider.LoadNamespace("sub").Entries).Text);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Finding 4: an entries COLLECTION that bound JSON null is quarantined like its rows —
    /// the namespace loads empty instead of bricking every load that touches it.
    /// </summary>
    [Fact]
    public void NullEntriesCollection_LoadsEmptyInsteadOfBricking()
    {
        File.WriteAllText(Path.Combine(_testDataPath, "nullcoll.json"),
            "{\"storageVersion\":2,\"entries\":null}");

        using var persistence2 = new PersistenceManager(_testDataPath, debounceMs: 60_000);
        using var index2 = new CognitiveIndex(persistence2);

        Assert.Empty(index2.GetAllInNamespace("nullcoll", tenantId: ""));
    }

    /// <summary>
    /// Finding 5: the membership witness is genuine SET equality. A duplicate-bearing
    /// witness list that matches on count must refuse, not bind a summary to a set it does
    /// not describe.
    /// </summary>
    [Fact]
    public void MembershipWitness_DuplicateFalseEquality_Refuses()
    {
        _index.Upsert(new CognitiveEntry("x", new[] { 0f, 1f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("y", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error:", _clusters.CreateCluster("k", "test", new List<string> { "x", "y" }, "mine", tenantId: ""));

        var refused = _clusters.StoreSummary("k", "summary of x alone, twice", new[] { 0f, 1f, 0f }, tenantId: "",
            onlyIfStamp: null, onlyIfMembers: new List<string> { "x", "x" });

        Assert.StartsWith("Error:", refused);
        Assert.Contains("membership changed", refused);
        Assert.Null(_index.Get("summary:k", "test", tenantId: ""));
    }

    /// <summary>
    /// Finding 6: null-shaped cluster rows are quarantined on load instead of bricking
    /// every cluster operation.
    /// </summary>
    [Fact]
    public void NullClusterRows_AreQuarantinedOnLoad()
    {
        var good = JsonSerializer.Serialize(new SemanticCluster("k", "test", new List<string> { "x" }, "label"));
        var nullId = good.Replace("\"clusterId\":\"k\"", "\"clusterId\":null");
        Assert.NotEqual(good, nullId);
        File.WriteAllText(Path.Combine(_testDataPath, "_clusters.json"), $"[null,{good},{nullId}]");

        using var persistence2 = new PersistenceManager(_testDataPath, debounceMs: 60_000);
        using var index2 = new CognitiveIndex(persistence2);
        var clusters2 = new ClusterManager(index2, persistence2);

        var listing = Assert.Single(clusters2.ListClusters("test", tenantId: ""));
        Assert.Equal("k", listing.ClusterId);
    }

    /// <summary>
    /// Finding 7: null-shaped edge rows are quarantined on load instead of bricking every
    /// graph operation.
    /// </summary>
    [Fact]
    public void NullEdgeRows_AreQuarantinedOnLoad()
    {
        var good = JsonSerializer.Serialize(new GraphEdge("a", "b", "related"));
        var nullSource = good.Replace("\"sourceId\":\"a\"", "\"sourceId\":null");
        var nullRelation = good.Replace("\"relation\":\"related\"", "\"relation\":null");
        var wildWeight = good.Replace("\"weight\":1", "\"weight\":5.5");
        Assert.NotEqual(good, nullSource);
        Assert.NotEqual(good, nullRelation);
        Assert.NotEqual(good, wildWeight);
        File.WriteAllText(Path.Combine(_testDataPath, "_edges.json"),
            $"[null,{good},{nullSource},{nullRelation},{wildWeight}]");

        // The read path clamps weights — a stored out-of-range value must not poison scoring.
        foreach (var e in new PersistenceManager(_testDataPath, debounceMs: 60_000).LoadGlobalEdges())
            if (e is not null) Assert.InRange(e.Weight, 0f, 1f);

        using var persistence2 = new PersistenceManager(_testDataPath, debounceMs: 60_000);
        using var index2 = new CognitiveIndex(persistence2);
        index2.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "g", lifecycleState: "ltm"));
        index2.Upsert(new CognitiveEntry("b", new[] { 0f, 1f, 0f }, "g", lifecycleState: "ltm"));
        var graph = new KnowledgeGraph(persistence2, index2);

        var neighbors = graph.GetNeighbors("a", relation: null, direction: "both", tenantId: "");
        Assert.NotNull(neighbors);
    }

    /// <summary>
    /// Finding 8: STRICT receipt reads refuse malformed rows (a null list element here) —
    /// they must never launder them into generation compares — while the lenient boot load
    /// degrades by dropping them.
    /// </summary>
    [Fact]
    public void MalformedReceiptRows_StrictReadRefuses_LenientLoadDrops()
    {
        File.WriteAllText(Path.Combine(_testDataPath, "_collapse_history.json"), "[null]");

        using var persistence2 = new PersistenceManager(_testDataPath, debounceMs: 60_000);

        Assert.False(persistence2.TryReadCollapseHistory(out var strict));
        Assert.Empty(strict);
        Assert.Empty(persistence2.LoadCollapseHistory());
    }
}
