using System.Text.Json;
using Microsoft.Data.Sqlite;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// Deterministic regression controls for the round-17 review findings: summary writers and
/// record-driven cleanup fence the PHYSICAL cluster instance rather than the reusable lineage
/// stamp; dismissal lets durable absence override its cache; the membership witness binds a
/// generated summary to the set it describes at store AND publish; tool authorization binds
/// to the mutation; null-shaped stored rows are quarantined instead of bricking loads; the
/// flush commits captured incremental batches before live full saves and withholds dependent
/// graph saves after an entry-level failure; and a disposed SQL provider's TryFlush refuses.
/// </summary>
public class Round17RegressionTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly ClusterManager _clusters;
    private readonly LifecycleEngine _lifecycle;
    private readonly AccretionScanner _scanner;

    public Round17RegressionTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"round17_test_{Guid.NewGuid():N}");
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

    private string SeedAndDetect(string tenantId = "")
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm", tenantId: tenantId));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm", tenantId: tenantId));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm", tenantId: tenantId));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm", tenantId: tenantId));
        return _scanner.ScanNamespace("test", tenantId: tenantId).NewCollapses[0].CollapseId;
    }

    /// <summary>
    /// Finding 1 at the CAS: same lineage stamp is NOT same incarnation. A delayed writer
    /// admitted by a dead cluster object of the lineage carries the reused stamp but a
    /// different instance, and must refuse instead of overwriting the live object's summary.
    /// </summary>
    [Fact]
    public void SummaryCas_SameLineageDifferentInstance_Refuses()
    {
        var live = new CognitiveEntry("summary:k", new[] { 0f, 1f, 0f }, "test",
            text: "live summary", category: "cluster-summary", lifecycleState: "ltm")
        {
            IsSummaryNode = true,
            SourceClusterId = "k",
            SourceClusterStamp = "lineage-S",
            SourceClusterInstance = "instance-2"
        };
        Assert.True(_index.UpsertSummaryIfIncarnation(live));

        var delayed = new CognitiveEntry("summary:k", new[] { 1f, 0f, 0f }, "test",
            text: "stale text from the dead object", category: "cluster-summary", lifecycleState: "ltm")
        {
            IsSummaryNode = true,
            SourceClusterId = "k",
            SourceClusterStamp = "lineage-S",
            SourceClusterInstance = "instance-1"
        };

        Assert.False(_index.UpsertSummaryIfIncarnation(delayed));
        Assert.Equal("live summary", _index.Get("summary:k", "test", tenantId: "")!.Text);
    }

    /// <summary>
    /// Finding 1 end to end: a same-stamp recreation in the CAS-to-publish window leaves the
    /// preempted writer UNPUBLISHED — the publish fence compares the physical instance, which
    /// the recreation cannot reuse — and the recreation's own summary survives the reap.
    /// </summary>
    [Fact]
    public void StoreSummary_SameStampRecreationInPublishWindow_IsNotPublishedOver()
    {
        _index.Upsert(new CognitiveEntry("x", new[] { 0f, 1f, 0f }, "test", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error:", _clusters.CreateCluster("k", "test", new List<string> { "x" }, "first", tenantId: ""));
        Assert.True(_clusters.TryGetClusterStamp("k", "", out var stamp));

        _clusters.OnBeforeSummaryPublish = () =>
        {
            _clusters.OnBeforeSummaryPublish = null;
            // Teardown and SAME-STAMP recreation (a collapse retry's shape), which then
            // publishes its own summary.
            Assert.DoesNotContain("Error:", _clusters.UpdateCluster("k", addIds: null,
                removeIds: new List<string> { "x" }, label: null, tenantId: ""));
            _index.DeleteIfSummaryOf("summary:k", "test", "", "k");
            Assert.Equal(EmptyClusterRemoval.Removed, _clusters.RemoveClusterIfEmpty("k", tenantId: ""));
            _index.Upsert(new CognitiveEntry("y", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
            Assert.DoesNotContain("Error:", _clusters.CreateCluster("k", "test", new List<string> { "y" }, "retry",
                tenantId: "", creationStamp: stamp));
            Assert.DoesNotContain("Error:", _clusters.StoreSummary("k", "retry summary", new[] { 1f, 0f, 0f }, tenantId: ""));
        };

        var preempted = _clusters.StoreSummary("k", "preempted summary", new[] { 0f, 1f, 0f }, tenantId: "");

        Assert.StartsWith("Error:", preempted);
        Assert.Equal("retry summary", _index.Get("summary:k", "test", tenantId: "")!.Text);
        var listing = Assert.Single(_clusters.ListClusters("test", tenantId: ""));
        Assert.True(listing.HasSummary);
    }

    /// <summary>
    /// Finding 2: record-driven cleanup deletes only the summary of the PHYSICAL instance the
    /// record names. A same-stamp recreated cluster's summary — a concurrent retry's live
    /// artifact in production — is spared by the instance condition where the stamp alone
    /// matched and destroyed it.
    /// </summary>
    [Fact]
    public void UndoCollapse_SameStampRecreatedClustersSummary_IsSpared()
    {
        var collapseId = SeedAndDetect();
        _index.Delete("d");
        Assert.StartsWith("Error:", _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));
        var record = Assert.Single(_scanner.GetCollapseHistory("test", tenantId: ""));
        Assert.NotNull(record.ClusterInstance);

        // The attempt's cluster is torn down and recreated under the SAME lineage stamp with
        // a fresh instance, which stores its own summary and is then removed — leaving that
        // recreation's summary orphaned under the reused stamp.
        Assert.DoesNotContain("Error:", _clusters.UpdateCluster(record.ClusterId, addIds: null,
            removeIds: new List<string> { "a", "b", "c", "d" }, label: null, tenantId: ""));
        _index.DeleteIfSummaryOf(record.SummaryEntryId, "test", "", record.ClusterId);
        Assert.Equal(EmptyClusterRemoval.Removed, _clusters.RemoveClusterIfEmpty(record.ClusterId, tenantId: ""));
        _index.Upsert(new CognitiveEntry("y", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error:", _clusters.CreateCluster(record.ClusterId, "test", new List<string> { "y" }, "retry",
            tenantId: "", creationStamp: record.ClusterStamp));
        Assert.DoesNotContain("Error:", _clusters.StoreSummary(record.ClusterId, "the retry's summary",
            new[] { 1f, 0f, 0f }, tenantId: ""));
        Assert.DoesNotContain("Error:", _clusters.UpdateCluster(record.ClusterId, addIds: null,
            removeIds: new List<string> { "y" }, label: null, tenantId: ""));
        Assert.Equal(EmptyClusterRemoval.Removed, _clusters.RemoveClusterIfEmpty(record.ClusterId, tenantId: ""));

        var undo = _scanner.UndoCollapse(collapseId, _lifecycle, _clusters, tenantId: "");

        Assert.StartsWith("Reversed", undo);
        foreach (var id in new[] { "a", "b", "c" })
            Assert.Equal("ltm", _index.Get(id, "test", tenantId: "")!.LifecycleState);
        // The recreation's summary carries the reused stamp but a different instance — spared.
        Assert.Equal("the retry's summary", _index.Get(record.SummaryEntryId, "test", tenantId: "")!.Text);
    }

    /// <summary>
    /// Finding 3: durable absence overrides the cache. A record retired by another stack
    /// leaves this scanner's cache stale-PRESENT; dismissal consults the store under the
    /// in-flight slot and proceeds instead of refusing toward an undo of nothing.
    /// </summary>
    [Fact]
    public void DismissCollapse_DurablyRetiredRecord_Proceeds()
    {
        var collapseId = SeedAndDetect();
        _index.Delete("d");
        Assert.StartsWith("Error:", _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));
        var record = Assert.Single(_scanner.GetCollapseHistory("test", tenantId: ""));

        // Another stack retires the record; this scanner's cache still holds it. The shell
        // is emptied so dismissal's cleanup gate is not the thing under test.
        Assert.DoesNotContain("Error:", _clusters.UpdateCluster(record.ClusterId, addIds: null,
            removeIds: new List<string> { "a", "b", "c", "d" }, label: null, tenantId: ""));
        Assert.True(_persistence.TryReadCollapseRecord(collapseId, out var durable));
        Assert.Equal(CollapseRecordCas.Applied,
            _persistence.DeleteCollapseRecordSync(collapseId, durable!.Generation));

        var dismissed = _scanner.DismissCollapse(collapseId, tenantId: "", _clusters);

        Assert.StartsWith("Dismissed", dismissed);
    }

    /// <summary>
    /// Finding 4: the membership witness. A summary generated from a member set refuses at
    /// the store when the set diverged, and a divergence racing the entry write is caught by
    /// the publish re-verification — the entry is reaped and nothing is published.
    /// </summary>
    [Fact]
    public void StoreSummary_MembershipWitness_RefusesAndReapsOnDivergence()
    {
        _index.Upsert(new CognitiveEntry("x", new[] { 0f, 1f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("y", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error:", _clusters.CreateCluster("k", "test", new List<string> { "x" }, "mine", tenantId: ""));

        // Diverged before the store: refused outright.
        var refused = _clusters.StoreSummary("k", "summary of x and y", new[] { 0.5f, 0.5f, 0f }, tenantId: "",
            onlyIfStamp: null, onlyIfMembers: new List<string> { "x", "y" });
        Assert.StartsWith("Error:", refused);
        Assert.Contains("membership changed", refused);

        // Diverged between the entry write and the publish: reaped, nothing published.
        _clusters.OnBeforeSummaryPublish = () =>
        {
            _clusters.OnBeforeSummaryPublish = null;
            Assert.DoesNotContain("Error:", _clusters.UpdateCluster("k", addIds: new List<string> { "y" },
                removeIds: null, label: null, tenantId: ""));
        };
        var raced = _clusters.StoreSummary("k", "summary of x", new[] { 0f, 1f, 0f }, tenantId: "",
            onlyIfStamp: null, onlyIfMembers: new List<string> { "x" });

        Assert.StartsWith("Error:", raced);
        Assert.Null(_index.Get("summary:k", "test", tenantId: ""));
        var listing = Assert.Single(_clusters.ListClusters("test", tenantId: ""));
        Assert.False(listing.HasSummary);
    }

    /// <summary>
    /// Finding 5: authorization binds to the mutation. The namespace the caller's gate
    /// authorized is re-compared under the mutation's own lock; a mismatch refuses with the
    /// gate's miss shape, and the matching namespace proceeds.
    /// </summary>
    [Fact]
    public void MutationsWithNamespaceBinding_RefuseAForeignNamespace()
    {
        _index.Upsert(new CognitiveEntry("x", new[] { 0f, 1f, 0f }, "test", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error:", _clusters.CreateCluster("k", "test", new List<string> { "x" }, "mine", tenantId: ""));

        Assert.Equal("Error: Cluster 'k' not found.",
            _clusters.UpdateClusterInNs("k", addIds: null, removeIds: null, label: "renamed", tenantId: "", onlyIfNs: "other"));
        Assert.Equal("Error: Cluster 'k' not found.",
            _clusters.StoreSummaryInNs("k", "summary", new[] { 0f, 1f, 0f }, tenantId: "", onlyIfNs: "other"));

        Assert.DoesNotContain("Error:",
            _clusters.UpdateClusterInNs("k", addIds: null, removeIds: null, label: "renamed", tenantId: "", onlyIfNs: "test"));
        Assert.DoesNotContain("Error:",
            _clusters.StoreSummaryInNs("k", "summary", new[] { 0f, 1f, 0f }, tenantId: "", onlyIfNs: "test"));
    }

    /// <summary>
    /// Finding 6: null-shaped decay rows (a null list element, a row whose ns bound JSON
    /// null) are dropped with a warning instead of throwing the whole config load over.
    /// </summary>
    [Fact]
    public void NullShapedDecayRows_AreDroppedNotBricking()
    {
        var rowJson = JsonSerializer.Serialize(new DecayConfig("test", tenantId: "t"));
        Assert.Contains("\"ns\":\"test\"", rowJson);
        var nullNsRow = rowJson.Replace("\"ns\":\"test\"", "\"ns\":null");
        Assert.NotEqual(rowJson, nullNsRow);
        File.WriteAllText(Path.Combine(_testDataPath, "_decay_configs.json"),
            $"[null,{rowJson},{nullNsRow}]");

        using var persistence2 = new PersistenceManager(_testDataPath, debounceMs: 60_000);
        var engine = new LifecycleEngine(_index, persistence2);

        var configs = engine.GetAllDecayConfigs();
        Assert.Single(configs);
        Assert.Equal("t", configs[0].TenantId);
    }

    /// <summary>
    /// Finding 7: null-shaped entry rows (a null element, a row with a null id or vector)
    /// are dropped on load instead of bricking the namespace.
    /// </summary>
    [Fact]
    public void NullShapedEntryRows_AreDroppedOnLoad()
    {
        var good = new CognitiveEntry("good", new[] { 1f, 0f, 0f }, "poisonload", text: "survives", lifecycleState: "ltm");
        _persistence.SaveNamespaceSync("poisonload", new NamespaceData { Entries = new List<CognitiveEntry> { good } });
        var path = Path.Combine(_testDataPath, "poisonload.json");
        var json = File.ReadAllText(path);
        var poisoned = json.Replace("\"entries\": [", "\"entries\": [null,{\"id\":null,\"vector\":[1.0],\"ns\":\"poisonload\"},");
        Assert.NotEqual(json, poisoned);
        File.WriteAllText(path, poisoned);

        using var persistence2 = new PersistenceManager(_testDataPath, debounceMs: 60_000);
        using var index2 = new CognitiveIndex(persistence2);

        var loaded = index2.GetAllInNamespace("poisonload", tenantId: "");
        Assert.Single(loaded);
        Assert.Equal("good", loaded[0].Id);
    }

    /// <summary>
    /// Finding 8 (SQLite shape of the class): captured incremental batches commit BEFORE the
    /// live full-namespace save, so a stale batch row can never overwrite the fresher state
    /// the full save just wrote.
    /// </summary>
    [Fact]
    public void SqliteFlush_CapturedBatchCommitsBeforeLiveFullSave()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"round17_sqlite_{Guid.NewGuid():N}.db");
        try
        {
            using (var provider = new SqliteStorageProvider(dbPath, debounceMs: 60_000))
            {
                var v1 = new CognitiveEntry("e", new[] { 1f, 0f }, "ord", text: "v1", lifecycleState: "ltm");
                var v2 = new CognitiveEntry("e", new[] { 1f, 0f }, "ord", text: "v2", lifecycleState: "ltm");
                provider.ScheduleUpsertEntry("ord", v1);
                provider.ScheduleSave("ord", () => new NamespaceData { Entries = new List<CognitiveEntry> { v2 } });

                Assert.True(provider.TryFlush());
                var data = provider.LoadNamespace("ord");
                Assert.Equal("v2", Assert.Single(data.Entries).Text);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Finding 9 (P2): a disposed SQL provider's TryFlush refuses instead of vouching for a
    /// provider that no longer accepts work.
    /// </summary>
    [Fact]
    public void SqliteTryFlush_AfterDispose_Refuses()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"round17_disposed_{Guid.NewGuid():N}.db");
        try
        {
            var provider = new SqliteStorageProvider(dbPath, debounceMs: 60_000);
            provider.Dispose();
            Assert.False(provider.TryFlush());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 3;
        public float[] Embed(string text) => new[] { 0.9f, 0.1f, 0f };
    }

    /// <summary>
    /// Refuter finding (P2): the durable record's instance must always name a physical
    /// object whose summary the record OWNS. A retry's intent carries the PRIOR attempt's
    /// instance (never its own unproven candidate), so a retry that fails mid-flight leaves
    /// the record still owning the prior attempt's summary — and the undo deletes it,
    /// instead of sparing it into a permanent orphan.
    /// </summary>
    [Fact]
    public void UndoAfterFailedRetry_StillOwnsThePriorAttemptsSummary()
    {
        var collapseId = SeedAndDetect();
        _index.Delete("d");
        Assert.StartsWith("Error:", _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));
        var record = Assert.Single(_scanner.GetCollapseHistory("test", tenantId: ""));
        Assert.NotNull(_index.Get(record.SummaryEntryId, "test", tenantId: ""));

        // A retry that fails again (d is still gone) advances the record; it must not
        // disown the prior attempt's physical instance while doing so.
        Assert.StartsWith("Error:", _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));

        var undo = _scanner.UndoCollapse(collapseId, _lifecycle, _clusters, tenantId: "");

        Assert.StartsWith("Reversed", undo);
        Assert.Null(_index.Get(record.SummaryEntryId, "test", tenantId: ""));
    }

    /// <summary>
    /// Refuter finding (P1): the full-namespace save MATERIALIZES at invoke time. A snapshot
    /// frozen at schedule time committing after newer writes durably resurrected deleted
    /// entries (and erased new ones) with the flush reporting true.
    /// </summary>
    [Fact]
    public void FullSave_MaterializesAtInvokeTime_DeletedEntryStaysDeleted()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"round17_mat_{Guid.NewGuid():N}.db");
        try
        {
            using var provider = new SqliteStorageProvider(dbPath, debounceMs: 60_000);
            using var index2 = new CognitiveIndex(provider);
            index2.Upsert(new CognitiveEntry("e1", new[] { 1f, 0f, 0f }, "mat", text: "one", lifecycleState: "ltm"));
            index2.Upsert(new CognitiveEntry("e2", new[] { 0f, 1f, 0f }, "mat", text: "two", lifecycleState: "ltm"));

            // Schedules the full-namespace save, then a DELETE lands inside the debounce.
            index2.RebuildEmbeddings("mat", new StubEmbedding(), tenantId: "");
            Assert.True(index2.Delete("e1", "mat", tenantId: ""));

            Assert.True(provider.TryFlush());
            var rows = provider.LoadNamespace("mat");
            Assert.Equal("e2", Assert.Single(rows.Entries).Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Refuter finding (P2, liveness half): the timer-path causal deferral (graph saves wait
    /// for entry-level work) DEFERS, never drops — both levels commit once the queues drain.
    /// </summary>
    [Fact]
    public async Task GraphTimerDeferral_EventuallyCommitsAfterEntryWork()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"round17_defer_{Guid.NewGuid():N}.db");
        try
        {
            using var provider = new SqliteStorageProvider(dbPath, debounceMs: 20);
            provider.ScheduleUpsertEntry("dfr", new CognitiveEntry("e", new[] { 1f, 0f }, "dfr", text: "x", lifecycleState: "ltm"));
            provider.ScheduleSaveClusters(() => new List<SemanticCluster>
            {
                new("kc", "dfr", new List<string> { "e" })
            });

            await Task.Delay(800);

            Assert.Single(provider.LoadClusters());
            Assert.Equal("e", Assert.Single(provider.LoadNamespace("dfr").Entries).Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
