using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// Deterministic regression controls for the round-15 review findings: the all-screened retry
/// deletes the prior attempt's summary; summary creation is incarnation-atomic at both the
/// cluster and the entry; strict reads are consulted on cache misses; the JSON global timer
/// callbacks cannot deadlock a flush; and tenant ids are normalized at the scanner boundary.
/// </summary>
public class Round15RegressionTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly ClusterManager _clusters;
    private readonly LifecycleEngine _lifecycle;
    private readonly AccretionScanner _scanner;

    public Round15RegressionTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"round15_test_{Guid.NewGuid():N}");
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
    /// Finding 1: the all-screened retry deletes the PRIOR attempt's summary — stamped — before
    /// retiring the record. A partial first attempt stores the summary; the old branch removed
    /// the cluster, released the claims, deleted the record, and left the summary searchable
    /// forever with nothing naming it.
    /// </summary>
    [Fact]
    public void ExecuteCollapse_AllScreenedRetry_DeletesThePriorSummary()
    {
        var collapseId = SeedAndDetect();

        // Partial attempt: summary stored, a/b/c archived, record durable.
        _index.Delete("d");
        Assert.StartsWith("Error:", _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));
        var record = Assert.Single(_scanner.GetCollapseHistory("test", tenantId: ""));
        Assert.NotNull(_index.Get(record.SummaryEntryId, "test", tenantId: ""));

        // Members evicted by a public caller; twins screen re-admission.
        Assert.DoesNotContain("Error:",
            _clusters.UpdateCluster(record.ClusterId, addIds: null,
                removeIds: new List<string> { "a", "b", "c", "d" }, label: null, tenantId: ""));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));
        foreach (var id in new[] { "a", "b", "c", "d" })
            _index.Upsert(new CognitiveEntry(id, new[] { 0f, 1f, 0f }, "other", lifecycleState: "ltm"));

        var retry = _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");

        Assert.Contains("admitted no members", retry);
        Assert.Empty(_scanner.GetCollapseHistory("test", tenantId: ""));
        // The claims were released AND the summary went with the record.
        foreach (var id in new[] { "a", "b", "c" })
            Assert.Equal("ltm", _index.Get(id, "test", tenantId: "")!.LifecycleState);
        Assert.Null(_index.Get(record.SummaryEntryId, "test", tenantId: ""));
    }

    /// <summary>
    /// Finding 2a: the collapse's summary store is INCARNATION-CONDITIONED. A replacement
    /// cluster that took the recorded id refuses the stamped store outright — this collapse's
    /// summary can never be handed to a cluster it does not own.
    /// </summary>
    [Fact]
    public void StoreSummary_StampMismatch_RefusesWithoutWriting()
    {
        var collapseId = SeedAndDetect();
        _index.Delete("d");
        Assert.StartsWith("Error:", _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));
        var record = Assert.Single(_scanner.GetCollapseHistory("test", tenantId: ""));

        // Replace the incarnation under the same id, same namespace.
        Assert.DoesNotContain("Error:",
            _clusters.UpdateCluster(record.ClusterId, addIds: null,
                removeIds: new List<string> { "a", "b", "c", "d" }, label: null, tenantId: ""));
        // The prior attempt's summary must not shadow the replacement scenario.
        _index.DeleteIfSummaryOf(record.SummaryEntryId, "test", "", record.ClusterId, onlyIfStamp: record.ClusterStamp);
        Assert.Equal(EmptyClusterRemoval.Removed, _clusters.RemoveClusterIfEmpty(record.ClusterId, tenantId: ""));
        _index.Upsert(new CognitiveEntry("x", new[] { 0f, 1f, 0f }, "test", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error:",
            _clusters.CreateCluster(record.ClusterId, "test", new List<string> { "x" }, "forged", tenantId: ""));

        var stampedStore = _clusters.StoreSummary(record.ClusterId, "stale summary", new[] { 0.5f, 0.5f, 0f },
            tenantId: "", onlyIfStamp: record.ClusterStamp);

        Assert.StartsWith("Error:", stampedStore);
        Assert.Contains("different incarnation", stampedStore);
        Assert.Null(_index.Get(record.SummaryEntryId, "test", tenantId: ""));
    }

    /// <summary>
    /// Finding 2b: the summary ENTRY write is incarnation-atomic. A delayed writer from a
    /// replaced incarnation cannot overwrite the successor's summary (and then reap the
    /// wreckage into a dangling pointer): the conditional upsert refuses with the successor's
    /// summary untouched.
    /// </summary>
    [Fact]
    public void UpsertSummary_DifferentIncarnationResident_RefusesAndSparesSuccessor()
    {
        _index.Upsert(new CognitiveEntry("x", new[] { 0f, 1f, 0f }, "test", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error:",
            _clusters.CreateCluster("k", "test", new List<string> { "x" }, "successor", tenantId: ""));
        Assert.DoesNotContain("Error:",
            _clusters.StoreSummary("k", "successor summary", new[] { 0f, 1f, 0f }, tenantId: ""));
        var successor = _index.Get("summary:k", "test", tenantId: "");
        Assert.NotNull(successor);

        // The delayed old writer's entry, stamped with a DEAD incarnation.
        var stale = new CognitiveEntry("summary:k", new[] { 1f, 0f, 0f }, "test",
            text: "stale summary", category: "cluster-summary", lifecycleState: "ltm", tenantId: "")
        {
            IsSummaryNode = true,
            SourceClusterId = "k",
            SourceClusterStamp = "dead-incarnation-stamp"
        };

        Assert.False(_index.UpsertSummaryIfIncarnation(stale));
        Assert.Equal("successor summary", _index.Get("summary:k", "test", tenantId: "")!.Text);
    }

    /// <summary>
    /// Finding 3: a cache miss is not absence. A recovery scanner whose one-shot history load
    /// ran while the store was empty must still see — and undo — a record another scanner
    /// persisted afterwards: the miss consults the store strictly instead of answering
    /// "No collapse record found" from the frozen cache.
    /// </summary>
    [Fact]
    public void UndoCollapse_RecordPersistedAfterCacheLoad_IsFoundStrictly()
    {
        // Scanner 2's history cache freezes EMPTY, before anything exists.
        var scanner2 = new AccretionScanner(_index, _persistence);
        Assert.Null(scanner2.GetCollapseRecordNs("collapse:not-yet", tenantId: ""));

        // Scanner 1 then executes a collapse to completion.
        var collapseId = SeedAndDetect();
        Assert.StartsWith("Collapsed 4", _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));

        // The recovery scanner resolves the namespace AND undoes through strict reads.
        Assert.Equal("test", scanner2.GetCollapseRecordNs(collapseId, tenantId: ""));
        var undo = scanner2.UndoCollapse(collapseId, _lifecycle, _clusters, tenantId: "");
        Assert.StartsWith("Reversed", undo);
        foreach (var id in new[] { "a", "b", "c", "d" })
            Assert.Equal("ltm", _index.Get(id, "test", tenantId: "")!.LifecycleState);
    }

    /// <summary>
    /// Finding 4: the JSON global debounce callbacks track themselves in-flight only INSIDE
    /// the flush gate, so a flush holding the gate can never wait on a callback that is
    /// waiting on the gate. This stress drives edge/cluster/decay timers against concurrent
    /// flushes; before the fix it deadlocked.
    /// </summary>
    [Fact]
    public void GlobalTimerCallbacks_ConcurrentWithFlushes_DoNotDeadlock()
    {
        using var persistence = new PersistenceManager(
            Path.Combine(Path.GetTempPath(), $"round15_flush_{Guid.NewGuid():N}"), debounceMs: 10);
        try
        {
            for (int i = 0; i < 30; i++)
            {
                persistence.ScheduleSaveGlobalEdges(() => new List<GraphEdge>());
                persistence.ScheduleSaveClusters(() => new List<SemanticCluster>());
                persistence.ScheduleSaveDecayConfigs(() => new Dictionary<string, DecayConfig>());
                Assert.True(persistence.TryFlush());
                Thread.Sleep(5);
            }
        }
        finally
        {
            var dir = ((IStorageProvider)persistence).StoreIdentity;
            persistence.Dispose();
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Finding 5: tenant ids are normalized at every public scanner boundary. A scan supplied
    /// with a padded tenant creates proposals under the normalized tenant, and every later
    /// call — padded or not — addresses the same state.
    /// </summary>
    [Fact]
    public void ScannerBoundaries_NormalizeTenantIds()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm", tenantId: "acme"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm", tenantId: "acme"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm", tenantId: "acme"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm", tenantId: "acme"));

        var scan = _scanner.ScanNamespace("test", tenantId: " acme ");
        var collapseId = Assert.Single(scan.NewCollapses).CollapseId;

        Assert.Single(_scanner.GetPendingCollapses("test", tenantId: "acme"));
        Assert.Equal("test", _scanner.GetPendingCollapseNs(collapseId, tenantId: " acme "));

        var executed = _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: " acme ");
        Assert.StartsWith("Collapsed 4", executed);
        Assert.Single(_scanner.GetCollapseHistory("test", tenantId: " acme "));

        var undo = _scanner.UndoCollapse(collapseId, _lifecycle, _clusters, tenantId: "acme");
        Assert.StartsWith("Reversed", undo);
    }

    /// <summary>
    /// Refuter finding: a Flush issued during a sustained stream of collapse-history RMWs must
    /// complete. Before the fix the RMWs tracked themselves in-flight OUTSIDE the flush gate,
    /// so new writes kept starting while the flush held the gate waiting for quiescence — the
    /// count never reached zero and the flush starved with every debounced save queued behind
    /// the gate it held.
    /// </summary>
    [Fact]
    public async Task Flush_UnderSustainedCollapseRmwStream_CompletesInsteadOfStarving()
    {
        using var persistence = new PersistenceManager(
            Path.Combine(Path.GetTempPath(), $"round15_starve_{Guid.NewGuid():N}"), debounceMs: 60_000);
        using var stop = new CancellationTokenSource();
        var mutators = Enumerable.Range(0, 3).Select(t => Task.Run(() =>
        {
            int i = 0;
            while (!stop.IsCancellationRequested)
            {
                var id = $"collapse:starve:{t}:{i++}";
                var record = new CollapseRecord(id, "k", "summary:k", "test",
                    new List<string>(), new Dictionary<string, string>(), tenantId: "", generation: 1);
                if (persistence.UpsertCollapseRecordSync(record, onlyIfGeneration: null) == CollapseRecordCas.Applied)
                    persistence.DeleteCollapseRecordSync(id, onlyIfGeneration: 1);
            }
        })).ToArray();

        await Task.Delay(300);
        var flush = Task.Run(() => persistence.TryFlush());
        var done = await Task.WhenAny(flush, Task.Delay(TimeSpan.FromSeconds(20)));
        stop.Cancel();
        await Task.WhenAll(mutators);

        Assert.Same(flush, done);
        Assert.True(await flush);
    }

    /// <summary>
    /// Refuter finding: the strict miss-path read in <c>GetCollapseRecordNs</c> answers from
    /// the STORE and installs nothing. Before the fix it warmed the cache, and a read racing a
    /// concurrent undo's retirement window resurrected the just-deleted record — wedging the
    /// next dismissal behind a phantom "partially executed attempt".
    /// </summary>
    [Fact]
    public void GetCollapseRecordNs_DoesNotResurrectARetiredRecord()
    {
        // Scanner 2's history cache freezes EMPTY before anything exists.
        var scanner2 = new AccretionScanner(_index, _persistence);
        Assert.Null(scanner2.GetCollapseRecordNs("collapse:none", tenantId: ""));

        var collapseId = SeedAndDetect();
        _index.Delete("d");
        Assert.StartsWith("Error:", _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));
        Assert.Equal("test", scanner2.GetCollapseRecordNs(collapseId, tenantId: ""));

        // The record is retired out from under scanner 2 — another stack's undo committing.
        Assert.True(_persistence.TryReadCollapseRecord(collapseId, out var durable));
        Assert.Equal(CollapseRecordCas.Applied,
            persistenceDelete(collapseId, durable!.Generation));

        // A warmed cache would still answer "test" here; the store answers.
        Assert.Null(scanner2.GetCollapseRecordNs(collapseId, tenantId: ""));

        CollapseRecordCas persistenceDelete(string id, long generation)
            => _persistence.DeleteCollapseRecordSync(id, generation);
    }

    /// <summary>
    /// Refuter finding: read constructors normalize without VALIDATING. A tenant id stored
    /// before validation tightened (over-long here) must still deserialize on every model the
    /// loaders construct — a throwing constructor made one poisoned row render the whole store
    /// unloadable, cluster and lifecycle operations included.
    /// </summary>
    [Fact]
    public void PoisonedTenantRows_StillDeserializeOnEveryReadConstructor()
    {
        var poisoned = new string('t', 80); // over the tenant length cap — Tenancy.Normalize throws on it

        static string Poison(string json, string tenant)
        {
            var replaced = json
                .Replace("\"tenantId\":\"t\"", $"\"tenantId\":\"{tenant}\"")
                .Replace("\"TenantId\":\"t\"", $"\"TenantId\":\"{tenant}\"");
            Assert.NotEqual(json, replaced);
            return replaced;
        }

        var cluster = JsonSerializer.Deserialize<SemanticCluster>(Poison(
            JsonSerializer.Serialize(new SemanticCluster("k", "test", new List<string> { "x" }, "label", tenantId: "t")), poisoned));
        Assert.Equal(poisoned, cluster!.TenantId);

        var record = JsonSerializer.Deserialize<CollapseRecord>(Poison(
            JsonSerializer.Serialize(new CollapseRecord("c1", "k", "summary:k", "test",
                new List<string>(), new Dictionary<string, string>(), tenantId: "t")), poisoned));
        Assert.Equal(poisoned, record!.TenantId);

        var config = JsonSerializer.Deserialize<DecayConfig>(Poison(
            JsonSerializer.Serialize(new DecayConfig("test", tenantId: "t")), poisoned));
        Assert.Equal(poisoned, config!.TenantId);

        var edge = JsonSerializer.Deserialize<GraphEdge>(Poison(
            JsonSerializer.Serialize(new GraphEdge("a", "b", "related", tenantId: "t")), poisoned));
        Assert.Equal(poisoned, edge!.TenantId);
    }

    /// <summary>
    /// Refuter finding: the CURRENT incarnation's summary store takes over a dead
    /// predecessor's surviving summary in ONE atom — the public call succeeds with no manual
    /// slot-clearing, and no instant exists with the slot empty (the delete-then-retry shape
    /// destroyed the only summary when a crash or quota throw landed between the two calls).
    /// </summary>
    [Fact]
    public void StoreSummary_StalePredecessorSummary_IsTakenOverByTheCurrentIncarnation()
    {
        _index.Upsert(new CognitiveEntry("x", new[] { 0f, 1f, 0f }, "test", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error:", _clusters.CreateCluster("k", "test", new List<string> { "x" }, "first", tenantId: ""));
        Assert.DoesNotContain("Error:", _clusters.StoreSummary("k", "first summary", new[] { 0f, 1f, 0f }, tenantId: ""));
        var predecessor = _index.Get("summary:k", "test", tenantId: "");
        Assert.NotNull(predecessor);

        // The incarnation dies; its summary survives — the crash window the takeover exists for.
        Assert.DoesNotContain("Error:", _clusters.UpdateCluster("k", addIds: null,
            removeIds: new List<string> { "x" }, label: null, tenantId: ""));
        Assert.Equal(EmptyClusterRemoval.Removed, _clusters.RemoveClusterIfEmpty("k", tenantId: ""));
        Assert.NotNull(_index.Get("summary:k", "test", tenantId: ""));

        _index.Upsert(new CognitiveEntry("y", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error:", _clusters.CreateCluster("k", "test", new List<string> { "y" }, "second", tenantId: ""));
        Assert.DoesNotContain("Error:", _clusters.StoreSummary("k", "second summary", new[] { 1f, 0f, 0f }, tenantId: ""));

        var successor = _index.Get("summary:k", "test", tenantId: "");
        Assert.Equal("second summary", successor!.Text);
        Assert.NotEqual(predecessor!.SourceClusterStamp, successor.SourceClusterStamp);
    }

    /// <summary>
    /// Refuter finding: a refused summary store leaves NO half-applied edit. The
    /// SummaryEntryId published before the entry CAS is restored on refusal, and the cluster
    /// listing derives its summary bit from the RESOLVED entry — a pointer at a manually
    /// stored non-summary squatter reports no summary and persists none.
    /// </summary>
    [Fact]
    public void StoreSummary_RefusedBySquatter_RestoresThePointerAndTheListingStaysHonest()
    {
        _index.Upsert(new CognitiveEntry("x", new[] { 0f, 1f, 0f }, "test", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error:", _clusters.CreateCluster("k", "test", new List<string> { "x" }, "mine", tenantId: ""));
        // A non-summary entry a caller manually placed under the summary's id.
        _index.Upsert(new CognitiveEntry("summary:k", new[] { 1f, 0f, 0f }, "test",
            text: "squatter", lifecycleState: "ltm"));

        var refused = _clusters.StoreSummary("k", "real summary", new[] { 0f, 1f, 0f }, tenantId: "");

        Assert.StartsWith("Error:", refused);
        Assert.Equal("squatter", _index.Get("summary:k", "test", tenantId: "")!.Text);
        var listing = Assert.Single(_clusters.ListClusters("test", tenantId: ""));
        Assert.False(listing.HasSummary);
        // The restored pointer is what persists: no cluster on disk names the phantom summary.
        Assert.True(_persistence.TryFlush());
        var clustersJson = File.ReadAllText(Path.Combine(_testDataPath, "_clusters.json"));
        Assert.DoesNotContain("summary:k", clustersJson);
    }
}
