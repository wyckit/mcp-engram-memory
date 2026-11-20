using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Constitution;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Governance.Persistence;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace McpEngramMemory.Tests;

/// <summary>
/// Targeted regression tests covering Group A non-breaking 2.1.x–2.2.x hardening:
/// storage coordinator anti-starvation, debounced HNSW persistence, SQL Server batch builders,
/// KnowledgeGraph/ClusterManager disposal, purge_debates sharing cleanup, background worker
/// profile gating &amp; lazy diffusion warmup, read-only audit batching, DuplicateDetector bounds,
/// ONNX thread configuration, spectral_recall filtering &amp; access tracking, hybrid summaryFirst,
/// get_memory optional ns, ToolError/IsError standardization, profile typo warnings, raw-query
/// reranking, vendored D3, debate permission-laundering prevention, and side-effect-free write checks.
/// </summary>
public sealed class GroupAFixesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"engram-groupa-{Guid.NewGuid():N}");

    public GroupAFixesTests()
    {
        Directory.CreateDirectory(_root);
    }

    private string SubDir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 1. Storage Coordinator: Anti-Starvation, Debounced HNSW, Bulk SQL, Dispose
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void PersistenceManager_AntiStarvation_FlushesGlobalSaveUnderContinuousNamespaceWrites()
    {
        var clock = new ControlledTimeProvider();
        var dir = SubDir("json_antistarve");
        using var pm = new PersistenceManager(dir, debounceMs: 50)
        {
            TimerProvider = clock,
            MaxGlobalDeferrals = 2
        };

        var edge = new GraphEdge("a", "b", "similar_to", 0.9f);
        var entry = new CognitiveEntry("a", [1f, 0f, 0f], "busy", "text");

        // Schedule an edge save (creates timer 0) and a pending namespace save (creates timer 1).
        // Under steady writes where the namespace save stays pending, the edge timer previously
        // deferred forever; with MaxGlobalDeferrals = 2, the 3rd callback must proactively drain
        // the pending namespace save and flush edges to disk.
        pm.ScheduleSaveGlobalEdges(() => [edge]);
        var edgeTimer = clock.Latest;

        pm.ScheduleSave("busy", () => new NamespaceData { Entries = [entry] });
        edgeTimer.DeliverQueuedCallback(); // deferral 1
        Assert.Empty(pm.LoadGlobalEdges());

        edgeTimer.DeliverQueuedCallback(); // deferral 2
        Assert.Empty(pm.LoadGlobalEdges());

        edgeTimer.DeliverQueuedCallback(); // cap reached -> proactive drain + global write!
        var loaded = pm.LoadGlobalEdges();
        Assert.Single(loaded);
        Assert.Equal("a", loaded[0].SourceId);
        Assert.Equal("b", loaded[0].TargetId);
        Assert.Single(pm.LoadNamespace("busy").Entries);
    }

    [Fact]
    public void SqliteStorageProvider_AntiStarvation_DrainsEntriesBeforeGlobalSaveUnderContinuousUpserts()
    {
        var clock = new ControlledTimeProvider();
        var dbPath = Path.Combine(SubDir("sqlite_antistarve"), "memory.db");
        using var sqlite = new SqliteStorageProvider(dbPath, debounceMs: 50)
        {
            TimerProvider = clock,
            MaxGlobalDeferrals = 2
        };

        var edge = new GraphEdge("e1", "e2", "cross_reference", 0.8f);
        sqlite.ScheduleSaveGlobalEdges(() => [edge]);
        var edgeTimer = clock.Latest;

        sqlite.ScheduleUpsertEntry("work", new CognitiveEntry("e1", [1f, 0f, 0f], "work", "v1"));
        edgeTimer.DeliverQueuedCallback(); // deferral 1
        Assert.Empty(sqlite.LoadGlobalEdges());

        edgeTimer.DeliverQueuedCallback(); // deferral 2
        Assert.Empty(sqlite.LoadGlobalEdges());

        edgeTimer.DeliverQueuedCallback(); // cap reached -> drains entry upserts first, then writes edges!
        var edges = sqlite.LoadGlobalEdges();
        Assert.Single(edges);
        var entries = sqlite.LoadNamespace("work").Entries;
        Assert.Single(entries);
        Assert.Equal("e1", entries[0].Id);
    }

    [Fact]
    public void PersistenceManager_DebouncedHnswSnapshot_CoalescesWritesAndCancelsOnDelete()
    {
        var clock = new ControlledTimeProvider();
        var dir = SubDir("json_hnsw_debounce");
        using var pm = new PersistenceManager(dir, debounceMs: 100) { TimerProvider = clock };

        int snapshotBuilds = 0;
        var hnsw = new HnswIndex();
        for (int i = 0; i < 5; i++)
            hnsw.Add($"id-{i}", [MathF.Cos(i), MathF.Sin(i), 0f]);

        // Rapidly schedule 10 HNSW saves within a single debounce window -> must coalesce into 1 write.
        for (int i = 0; i < 10; i++)
        {
            pm.ScheduleSaveHnswSnapshot("debounced-ns", () =>
            {
                Interlocked.Increment(ref snapshotBuilds);
                return hnsw.CreateSnapshot();
            });
        }

        Assert.Equal(0, Volatile.Read(ref snapshotBuilds)); // Deferred, not synchronous!
        Assert.True(pm.TryFlush());
        Assert.Equal(1, Volatile.Read(ref snapshotBuilds));
        Assert.NotNull(pm.LoadHnswSnapshot("debounced-ns"));

        // Schedule another HNSW save and immediately delete it -> pending save must be cancelled.
        pm.ScheduleSaveHnswSnapshot("debounced-ns", () =>
        {
            Interlocked.Increment(ref snapshotBuilds);
            return hnsw.CreateSnapshot();
        });
        pm.DeleteHnswSnapshot("debounced-ns");
        Assert.True(pm.TryFlush());
        Assert.Equal(1, Volatile.Read(ref snapshotBuilds));
        Assert.Null(pm.LoadHnswSnapshot("debounced-ns"));
    }

    [Fact]
    public void SqlServerStorageProvider_BulkBatchHelpers_ProduceExpectedTablesAndSetBasedSql()
    {
        var entries = new[]
        {
            new CognitiveEntry("id-1", [1f, 0f], "ns1", "first", "cat", lifecycleState: "ltm", tenantId: "t1"),
            new CognitiveEntry("id-2", [0f, 1f], "ns1", "second", null, lifecycleState: "stm", tenantId: "t1")
        };

        using var upsertTable = SqlServerStorageProvider.BuildUpsertBatchTable("ns1", entries);
        Assert.Equal(2, upsertTable.Rows.Count);
        Assert.Equal(["tenant_id", "ns", "id", "json_data", "checksum", "lifecycle_state"],
            upsertTable.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName).ToArray());
        Assert.Equal("t1", upsertTable.Rows[0]["tenant_id"]);
        Assert.Equal("ns1", upsertTable.Rows[0]["ns"]);
        Assert.Equal("id-1", upsertTable.Rows[0]["id"]);
        Assert.Equal("ltm", upsertTable.Rows[0]["lifecycle_state"]);
        Assert.Equal(64, ((string)upsertTable.Rows[0]["checksum"]).Length);

        var deletes = new[]
        {
            new SqlServerStorageProvider.EntryStorageKey("t1", "id-1"),
            new SqlServerStorageProvider.EntryStorageKey("t2", "id-2")
        };
        using var deleteTable = SqlServerStorageProvider.BuildDeleteBatchTable("ns1", deletes);
        Assert.Equal(2, deleteTable.Rows.Count);
        Assert.Equal(["tenant_id", "ns", "id"],
            deleteTable.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName).ToArray());

        string mergeSql = SqlServerStorageProvider.BuildBulkMergeSql("[dbo]");
        Assert.Contains("MERGE [dbo].entries WITH (HOLDLOCK) AS target", mergeSql);
        Assert.Contains("#engram_upsert_stage", mergeSql);

        string deleteSql = SqlServerStorageProvider.BuildBulkDeleteSql("[dbo]");
        Assert.Contains("DELETE target", deleteSql);
        Assert.Contains("#engram_delete_stage", deleteSql);
    }

    [Fact]
    public void KnowledgeGraph_And_ClusterManager_Dispose_FlushesSafelyInBothTeardownOrders()
    {
        // Order A: dispose graph & clusters BEFORE persistence.
        var dirA = SubDir("dispose_order_a");
        using (var pm = new PersistenceManager(dirA, debounceMs: 60_000))
        using (var index = new CognitiveIndex(pm))
        {
            var graph = new KnowledgeGraph(pm, index);
            var clusters = new ClusterManager(index, pm);
            index.Upsert(new CognitiveEntry("a", [1f, 0f, 0f], "ns", "entry a"));
            index.Upsert(new CognitiveEntry("b", [0.9f, 0.1f, 0f], "ns", "entry b"));
            graph.AddEdge(new GraphEdge("a", "b", "similar_to", 0.9f));
            clusters.CreateCluster("c1", "ns", ["a", "b"], "pair", tenantId: "");

            graph.Dispose();
            graph.Dispose(); // idempotent
            clusters.Dispose();
            clusters.Dispose(); // idempotent
        }

        using (var pmReload = new PersistenceManager(dirA, debounceMs: 50))
        {
            Assert.Single(pmReload.LoadGlobalEdges());
            Assert.Single(pmReload.LoadClusters());
        }

        // Order B: dispose persistence BEFORE graph & clusters (must NOT throw ObjectDisposedException).
        var dirB = SubDir("dispose_order_b");
        var pmB = new PersistenceManager(dirB, debounceMs: 60_000);
        var indexB = new CognitiveIndex(pmB);
        var graphB = new KnowledgeGraph(pmB, indexB);
        var clustersB = new ClusterManager(indexB, pmB);
        indexB.Upsert(new CognitiveEntry("x", [1f, 0f, 0f], "ns", "entry x"));
        graphB.AddEdge(new GraphEdge("x", "x2", "elaborates", 0.5f));
        clustersB.CreateCluster("c2", "ns", ["x"], "solo", tenantId: "");

        pmB.Dispose();
        indexB.Dispose();
        graphB.Dispose();
        clustersB.Dispose();
    }

    [Fact]
    public async Task PurgeDebates_Execute_CleansSystemSharingRows_AndDryRunDoesNotClaimEmptyNamespace()
    {
        var dir = SubDir("purge_debates_sharing");
        using var pm = new PersistenceManager(dir, debounceMs: 50);
        using var index = new CognitiveIndex(pm);
        using var graph = new KnowledgeGraph(pm, index);
        using var clusters = new ClusterManager(index, pm);
        var embedding = new HashEmbeddingService();
        var registry = new NamespaceRegistry(index, embedding);

        // 1. Alice creates a stale debate namespace with ownership in _system_sharing.
        var alicePrincipal = new PrincipalContext("tenant-1", "alice");
        var staleNs = "active-debate-old-1";
        Assert.True(registry.TryAcquireWriteAccess("alice", staleNs, alicePrincipal.TenantId));
        var staleEntry = new CognitiveEntry("d1", embedding.Embed("old debate"), staleNs, "old debate", tenantId: alicePrincipal.TenantId)
        {
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-48)
        };
        index.Upsert(staleEntry);

        // Confirm _system_sharing record exists before purge.
        Assert.NotNull(index.Get(NamespaceRegistry.PermissionEntryId(staleNs, alicePrincipal.TenantId),
            NamespaceRegistry.SystemNamespace, tenantId: ""));

        var aliceAdmin = new AdminTools(index, graph, clusters, pm, registry, alicePrincipal);
        var dryRes = Assert.IsType<PurgeDebatesResult>(await aliceAdmin.PurgeDebates(maxAgeHours: 24, dryRun: true));
        Assert.Equal(1, dryRes.NamespacesAffected);
        // Dry-run must NOT delete the sharing row.
        Assert.NotNull(index.Get(NamespaceRegistry.PermissionEntryId(staleNs, alicePrincipal.TenantId),
            NamespaceRegistry.SystemNamespace, tenantId: ""));

        var execRes = Assert.IsType<PurgeDebatesResult>(await aliceAdmin.PurgeDebates(maxAgeHours: 24, dryRun: false));
        Assert.Equal(1, execRes.NamespacesAffected);
        Assert.Equal(1, execRes.TotalEntriesRemoved);
        // After real purge empties the debate namespace, its _system_sharing permission entry is cleaned up.
        Assert.Null(index.Get(NamespaceRegistry.PermissionEntryId(staleNs, alicePrincipal.TenantId),
            NamespaceRegistry.SystemNamespace, tenantId: ""));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 2. Runtime & Background Performance
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BackgroundWorkerRegistration_GatesByProfile_AndHonorsOverrides()
    {
        var minimal = BackgroundWorkerRegistration.ResolveOptions("minimal", _ => null);
        Assert.Equal(2, minimal.EnabledWorkers.Count);
        Assert.Contains(BackgroundWorkerRegistration.WorkerEmbeddingWarmup, minimal.EnabledWorkers);
        Assert.Contains(BackgroundWorkerRegistration.WorkerDecay, minimal.EnabledWorkers);
        Assert.DoesNotContain(BackgroundWorkerRegistration.WorkerDiffusionWarmup, minimal.EnabledWorkers);
        Assert.False(minimal.DiffusionWarmupEagerLoad);

        var standard = BackgroundWorkerRegistration.ResolveOptions("standard", _ => null);
        Assert.Equal(6, standard.EnabledWorkers.Count);
        Assert.False(standard.DiffusionWarmupEagerLoad);

        var env = new Dictionary<string, string?>
        {
            ["MEMORY_BACKGROUND_WORKERS"] = "decay, unknown_worker",
            ["MEMORY_ENABLE_AUTOLINK"] = "true",
            ["MEMORY_DECAY_INTERVAL_MINUTES"] = "5",
            ["MEMORY_DIFFUSION_WARMUP_EAGER_LOAD"] = "1",
            ["MEMORY_AUTOLINK_INTERVAL_HOURS"] = "-2"
        };
        var custom = BackgroundWorkerRegistration.ResolveOptions("minimal", k => env.GetValueOrDefault(k));
        Assert.Equal(2, custom.EnabledWorkers.Count);
        Assert.Contains(BackgroundWorkerRegistration.WorkerDecay, custom.EnabledWorkers);
        Assert.Contains(BackgroundWorkerRegistration.WorkerAutoLink, custom.EnabledWorkers);
        Assert.Equal(TimeSpan.FromMinutes(5), custom.DecayInterval);
        Assert.True(custom.DiffusionWarmupEagerLoad);
        Assert.Equal(AutoLinkBackgroundService.DefaultInterval, custom.AutoLinkInterval);
        Assert.Equal(2, custom.Warnings.Count); // unknown_worker + negative interval

        var none = BackgroundWorkerRegistration.ResolveOptions("full",
            k => k == "MEMORY_BACKGROUND_WORKERS" ? "none" : null);
        Assert.Empty(none.EnabledWorkers);
    }

    [Fact]
    public void DiffusionKernelWarmupService_WarmOnlyLoadedPartitions_SkipsDiskLoadAll()
    {
        var dir = SubDir("diffusion_lazy_warmup");
        using (var pm1 = new PersistenceManager(dir, debounceMs: 50))
        using (var index1 = new CognitiveIndex(pm1))
        {
            index1.Upsert(new CognitiveEntry("u1", [1f, 0f, 0f], "cold-ns", "persisted on disk"));
            Assert.True(pm1.TryFlush());
        }

        using var pm2 = new PersistenceManager(dir, debounceMs: 50);
        using var index2 = new CognitiveIndex(pm2);
        using var graph2 = new KnowledgeGraph(pm2, index2);
        var kernel = new MemoryDiffusionKernel(index2, graph2);

        var warmup = new DiffusionKernelWarmupService(kernel, index2)
        {
            WarmOnlyLoadedPartitions = true
        };

        // Nothing is loaded yet — warmup must NOT trigger LoadAll() to materialize cold-ns.
        Assert.Empty(index2.GetLoadedPartitions());
        warmup.WarmAllQualifyingPartitions(CancellationToken.None);
        Assert.Empty(index2.GetLoadedPartitions());
    }

    [Fact]
    public async Task FileConstitutionAuditStore_BatchesReadOnlyAllows_AndImmediatelyFlushesWritesAndDenies()
    {
        var govDir = SubDir("audit_batching");
        using var audit = new FileConstitutionAuditStore(govDir, readOnlyFlushInterval: TimeSpan.FromSeconds(30), maxBufferedRecords: 64);
        var provider = new InMemoryConstitutionProvider();
        var evaluator = new DeterministicConstitutionEvaluator([new AuditEnvelopeConstitutionRule()]);
        var kernel = new ConstitutionKernel(provider, evaluator, audit);

        string validDigest = new('a', 64);

        // 10 read-only Allow evaluations (Precondition + Postcondition) must buffer with 0 fsyncs.
        for (int i = 0; i < 5; i++)
        {
            var readOp = new OperationEnvelope(
                $"mcp:r{i}:get_memory", CognitiveOperationKind.ReadMemory, "", "default",
                "Read", [], null, validDigest, DateTimeOffset.UtcNow, new Dictionary<string, string>());
            Assert.Equal(ConstitutionOutcome.Allow,
                (await kernel.EvaluateAndAuditAsync(readOp, ConstitutionPhase.Precondition)).Outcome);
            Assert.Equal(ConstitutionOutcome.Allow,
                (await kernel.EvaluateAndAuditAsync(readOp, ConstitutionPhase.Postcondition)).Outcome);
        }

        Assert.Equal(0, audit.FsyncCount);
        Assert.Equal(10, audit.PendingBufferedCount);

        // A single WriteMemory evaluation immediately flushes all 10 buffered reads + itself in 1 fsync.
        var writeOp = new OperationEnvelope(
            "mcp:w1:store_memory", CognitiveOperationKind.WriteMemory, "", "default",
            "Write", [], null, validDigest, DateTimeOffset.UtcNow, new Dictionary<string, string>());
        Assert.Equal(ConstitutionOutcome.Allow,
            (await kernel.EvaluateAndAuditAsync(writeOp, ConstitutionPhase.Precondition)).Outcome);

        Assert.Equal(1, audit.FsyncCount);
        Assert.Equal(0, audit.PendingBufferedCount);

        // A Deny on a ReadMemory operation must ALSO fsync immediately (never buffered).
        var denyRead = new OperationEnvelope(
            "mcp:r-deny:get_memory", CognitiveOperationKind.ReadMemory, "", "default",
            "ReadDeny", [], null, "not-a-valid-hex-digest", DateTimeOffset.UtcNow, new Dictionary<string, string>());
        Assert.Equal(ConstitutionOutcome.Deny,
            (await kernel.EvaluateAndAuditAsync(denyRead, ConstitutionPhase.Precondition)).Outcome);
        Assert.Equal(2, audit.FsyncCount);
        Assert.Equal(0, audit.PendingBufferedCount);

        // Verify all 12 records persisted with dense monotone sequence numbers 1..12.
        var records = await audit.ReadAllAsync();
        Assert.Equal(12, records.Count);
        Assert.Equal(Enumerable.Range(1, 12).Select(n => (long)n), records.Select(r => r.Sequence));

        // Add one more buffered read and verify DisposeAsync flushes it cleanly.
        var tailRead = new OperationEnvelope(
            "mcp:r-tail:recall", CognitiveOperationKind.Retrieve, "", "default",
            "Recall", [], null, validDigest, DateTimeOffset.UtcNow, new Dictionary<string, string>());
        await kernel.EvaluateAndAuditAsync(tailRead, ConstitutionPhase.Precondition);
        Assert.Equal(1, audit.PendingBufferedCount);
        await audit.DisposeAsync();

        var reopened = await new FileConstitutionAuditStore(govDir).ReadAllAsync();
        Assert.Equal(13, reopened.Count);
        Assert.Equal(Enumerable.Range(1, 13).Select(n => (long)n), reopened.Select(r => r.Sequence));
    }

    [Fact]
    public void DuplicateDetector_RespectsMaxEntries_MaxPairComparisons_AndCancellation()
    {
        var detector = new DuplicateDetector();
        var candidates = new List<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>();
        for (int i = 0; i < 20; i++)
        {
            // Pairs (0,1), (2,3), ..., (18,19) are identical duplicates, distinct across pairs.
            float angle = (i / 2) * 0.3f;
            float[] vec = [MathF.Cos(angle), MathF.Sin(angle), 0f];
            candidates.Add((new CognitiveEntry($"e{i}", vec, "ns", $"text {i}"), VectorMath.Norm(vec), null));
        }

        // Uncapped finds all 10 pairs.
        var all = detector.FindDuplicates(candidates, threshold: 0.99f, maxResults: 100,
            maxEntries: 0, maxPairComparisons: 0, CancellationToken.None);
        Assert.Equal(10, all.Count);

        // maxEntries = 6 slices to first 6 candidates -> only 3 duplicate pairs.
        var entryCapped = detector.FindDuplicates(candidates, threshold: 0.99f, maxResults: 100,
            maxEntries: 6, maxPairComparisons: 0, CancellationToken.None);
        Assert.Equal(3, entryCapped.Count);

        // maxPairComparisons = 20 stops after anchor 0 (19 comparisons) + anchor 1 (18 comparisons) -> 1 pair.
        var pairCapped = detector.FindDuplicates(candidates, threshold: 0.99f, maxResults: 100,
            maxEntries: 0, maxPairComparisons: 20, CancellationToken.None);
        Assert.True(pairCapped.Count < all.Count);

        // Pre-cancelled token throws OperationCanceledException.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            detector.FindDuplicates(candidates, threshold: 0.99f, maxResults: 100,
                maxEntries: 0, maxPairComparisons: 0, cts.Token));
    }

    [Fact]
    public void OnnxEmbeddingService_ConfiguresThreadsAndRejectsNonPositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OnnxEmbeddingService(modelDir: null, maximumTokens: 512, intraOpNumThreads: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OnnxEmbeddingService(modelDir: null, maximumTokens: 512, intraOpNumThreads: 1, interOpNumThreads: -1));

        using var svc = new OnnxEmbeddingService(modelDir: null, maximumTokens: 512, intraOpNumThreads: 2, interOpNumThreads: 1);
        Assert.Equal(2, svc.IntraOpNumThreads);
        Assert.Equal(1, svc.InterOpNumThreads);
        var vec = svc.Embed("hello world");
        Assert.Equal(svc.Dimensions, vec.Length);
    }

    [Fact]
    public void SpectralRecall_FiltersOutArchivedSurfacedEntries_AndRecordsAccess()
    {
        var dir = SubDir("spectral_archive_access");
        using var pm = new PersistenceManager(dir, debounceMs: 50);
        using var index = new CognitiveIndex(pm);
        using var graph = new KnowledgeGraph(pm, index);
        var embedding = new HashEmbeddingService();
        var registry = new NamespaceRegistry(index, embedding);
        var access = new NamespaceAccess(registry, new PrincipalContext("", AgentIdentity.DefaultAgentId));
        var kernel = new MemoryDiffusionKernel(index, graph);
        var reranker = new SpectralRetrievalReranker(kernel);
        var tools = new SpectralRetrievalTools(index, embedding, reranker, access);

        // Build a qualifying graph (>= 32 nodes, >= 8 positive-relation edges) where an archived
        // entry is strongly linked to an active anchor node so broad diffusion spreads mass to it.
        const string ns = "spec";
        var anchorVec = embedding.Embed("distributed consensus raft protocol");
        index.Upsert(new CognitiveEntry("anchor", anchorVec, ns, "distributed consensus raft protocol", lifecycleState: "ltm"));
        // Give archived entry an orthogonal vector so upstream vector Search won't match it directly,
        // forcing any appearance to come purely via spectral redistribution.
        float[] ortho = new float[embedding.Dimensions];
        ortho[0] = 1f;
        index.Upsert(new CognitiveEntry("archived-neighbor", ortho, ns, "retired old note", lifecycleState: "archived"));
        graph.AddEdge(new GraphEdge("anchor", "archived-neighbor", "similar_to", 1.0f));

        for (int i = 0; i < 34; i++)
        {
            var id = $"n{i}";
            var v = embedding.Embed($"topic {i} distributed consensus");
            index.Upsert(new CognitiveEntry(id, v, ns, $"topic {i} distributed consensus", lifecycleState: "ltm"));
            if (i < 12)
                graph.AddEdge(new GraphEdge("anchor", id, "similar_to", 0.9f));
        }

        var results = tools.SpectralRecall("distributed consensus raft protocol", ns, mode: "broad", k: 10);
        Assert.NotEmpty(results);
        Assert.DoesNotContain(results, r => r.Id == "archived-neighbor");
        Assert.All(results, r => Assert.Contains(r.LifecycleState, new[] { "stm", "ltm" }));

        // Access tracking: every returned entry must have AccessCount >= 1.
        foreach (var r in results)
        {
            var entry = index.Get(r.Id, ns, tenantId: "");
            Assert.NotNull(entry);
            Assert.True(entry.AccessCount >= 1, $"Expected AccessCount >= 1 for spectrally recalled entry '{r.Id}'.");
        }
    }

    [Fact]
    public void CoreMemoryTools_SearchMemory_HybridSummaryFirst_RanksSummaryAhead()
    {
        var dir = SubDir("hybrid_summary_first");
        using var pm = new PersistenceManager(dir, debounceMs: 50);
        using var index = new CognitiveIndex(pm);
        using var graph = new KnowledgeGraph(pm, index);
        using var clusters = new ClusterManager(index, pm);
        var embedding = new HashEmbeddingService();
        var metrics = new MetricsCollector();
        var registry = new NamespaceRegistry(index, embedding);
        var tools = new CoreMemoryTools(
            index, new PhysicsEngine(), embedding, metrics, graph, new QueryExpander(),
            new SpreadingActivationService(index, graph, clusters), clusters, registry,
            new PrincipalContext("", AgentIdentity.DefaultAgentId));

        var v = embedding.Embed("kubernetes ingress controller tls config");
        index.Upsert(new CognitiveEntry("raw-1", v, "infra", "kubernetes ingress controller tls config", lifecycleState: "ltm"));
        index.Upsert(new CognitiveEntry("raw-2", v, "infra", "kubernetes ingress controller tls config details", lifecycleState: "ltm"));
        var summary = new CognitiveEntry("sum-1", v, "infra", "summary of kubernetes ingress controller tls config", lifecycleState: "ltm")
        {
            IsSummaryNode = true
        };
        index.Upsert(summary);

        var resultObj = tools.SearchMemory(
            ns: "infra",
            text: "kubernetes ingress controller tls config",
            k: 3,
            hybrid: true,
            summaryFirst: true);
        var results = Assert.IsAssignableFrom<IReadOnlyList<CognitiveSearchResult>>(resultObj);
        Assert.NotEmpty(results);
        Assert.Equal("sum-1", results[0].Id);
        Assert.True(results[0].IsSummaryNode);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 3. MCP Ergonomics & Polish
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AdminTools_GetMemory_OptionalNs_DisambiguatesAndNeverFallsBack()
    {
        var dir = SubDir("get_memory_ns");
        using var pm = new PersistenceManager(dir, debounceMs: 50);
        using var index = new CognitiveIndex(pm);
        using var graph = new KnowledgeGraph(pm, index);
        using var clusters = new ClusterManager(index, pm);
        var embedding = new HashEmbeddingService();
        var registry = new NamespaceRegistry(index, embedding);
        var principal = new PrincipalContext("t1", "alice");

        Assert.True(registry.TryAcquireWriteAccess("alice", "ns-a", "t1"));
        Assert.True(registry.TryAcquireWriteAccess("alice", "ns-b", "t1"));
        Assert.True(registry.TryAcquireWriteAccess("bob", "ns-secret", "t1"));

        index.Upsert(new CognitiveEntry("dup", embedding.Embed("alpha"), "ns-a", "alpha text", tenantId: "t1"));
        index.Upsert(new CognitiveEntry("dup", embedding.Embed("beta"), "ns-b", "beta text", tenantId: "t1"));
        index.Upsert(new CognitiveEntry("secret-only", embedding.Embed("s"), "ns-secret", "secret", tenantId: "t1"));

        var admin = new AdminTools(index, graph, clusters, pm, registry, principal);

        // Bare id is ambiguous -> miss.
        Assert.Equal("Entry 'dup' not found.", admin.GetMemory("dup"));

        // Explicit ns disambiguates cleanly.
        var resA = Assert.IsType<GetMemoryResult>(admin.GetMemory("dup", ns: "ns-a"));
        Assert.Equal("alpha text", resA.Text);
        Assert.Equal("ns-a", resA.Entry.Namespace);

        var resB = Assert.IsType<GetMemoryResult>(admin.GetMemory("dup", ns: "ns-b"));
        Assert.Equal("beta text", resB.Text);
        Assert.Equal("ns-b", resB.Entry.Namespace);

        // Wrong explicit ns MUST NOT fall back to ns-a or ns-b.
        Assert.Equal("Entry 'dup' not found.", admin.GetMemory("dup", ns: "ns-nonexistent"));

        // Unreadable namespace returns the identical miss reply (no existence oracle).
        Assert.Equal("Entry 'secret-only' not found.", admin.GetMemory("secret-only", ns: "ns-secret"));
    }

    [Fact]
    public void ConstitutionMcpFilter_NormalizeToolErrorResult_SetsIsErrorConsistently()
    {
        static CallToolResult TextResult(string text) => new()
        {
            Content = [new TextContentBlock { Text = text }]
        };

        // Bare "Error: ..." string -> IsError = true.
        var errString = ConstitutionMcpFilter.NormalizeToolErrorResult(TextResult("Error: bad input"));
        Assert.True(errString.IsError);

        // ToolError JSON -> IsError = true.
        var toolErr = ConstitutionMcpFilter.NormalizeToolErrorResult(
            TextResult(JsonSerializer.Serialize(ToolError.Create("boom"))));
        Assert.True(toolErr.IsError);

        // ShareResult error_* -> IsError = true.
        var shareErr = ConstitutionMcpFilter.NormalizeToolErrorResult(
            TextResult(JsonSerializer.Serialize(new ShareResult("error_derived_restricted", "ns", "charlie", "read"))));
        Assert.True(shareErr.IsError);

        // Normal GetMemoryResult whose text body happens to start with "Error:" -> NOT flagged.
        var normalMem = ConstitutionMcpFilter.NormalizeToolErrorResult(
            TextResult("""{"entry":{"id":"e1","text":"Error: benign log line","ns":"work"},"text":"Error: benign log line"}"""));
        Assert.NotEqual(true, normalMem.IsError);

        // Normal JSON array -> NOT flagged.
        var normalArr = ConstitutionMcpFilter.NormalizeToolErrorResult(TextResult("""[{"id":"1"}]"""));
        Assert.NotEqual(true, normalArr.IsError);
    }

    [Fact]
    public void ToolProfileRegistration_WarnsOnTypoAndFallsBackToMinimal()
    {
        Assert.Equal("minimal", ToolProfileRegistration.ResolveProfile(null, out var wNull));
        Assert.Null(wNull);

        Assert.Equal("minimal", ToolProfileRegistration.ResolveProfile("  ", out var wBlank));
        Assert.Null(wBlank);

        Assert.Equal("standard", ToolProfileRegistration.ResolveProfile(" STANDARD ", out var wStd));
        Assert.Null(wStd);

        Assert.Equal("full", ToolProfileRegistration.ResolveProfile("full", out var wFull));
        Assert.Null(wFull);

        Assert.Equal("minimal", ToolProfileRegistration.ResolveProfile("standrad", out var wTypo));
        Assert.NotNull(wTypo);
        Assert.Contains("standrad", wTypo);
        Assert.Contains("minimal", wTypo);
        Assert.Contains("standard", wTypo);
        Assert.Contains("full", wTypo);
    }

    [Fact]
    public void HybridSearchEngine_PassesRawQueryTextToReranker_NotSynonymExpandedText()
    {
        var bm25 = new BM25Index();
        var engine = new HybridSearchEngine();
        var entry = new CognitiveEntry("e1", [1f, 0f, 0f], "ns", "database connection pool timeout");
        bm25.Index(entry, "ns");

        var vectorResults = new[]
        {
            new CognitiveSearchResult("e1", entry.Text, 0.90f, "ltm", 1f, null, null, false, null, 0)
        };

        var capturingReranker = new CapturingReranker();
        _ = engine.HybridSearch(
            vectorResults,
            queryText: "db pool timeout database connection rdbms sql",
            ns: "ns",
            k: 1,
            includeStates: new HashSet<string> { "stm", "ltm" },
            category: null,
            rerank: true,
            rrfK: 60,
            bm25: bm25,
            reranker: capturingReranker,
            getEntry: (_, _) => entry,
            queryVector: [1f, 0f, 0f],
            entryCount: 1,
            bm25IncludeIds: null,
            rerankQueryText: "db pool timeout");

        Assert.Equal("db pool timeout", capturingReranker.LastQueryText);
    }

    private sealed class CapturingReranker : IReranker
    {
        public string? LastQueryText { get; private set; }

        public IReadOnlyList<CognitiveSearchResult> Rerank(string queryText, IReadOnlyList<CognitiveSearchResult> candidates)
        {
            LastQueryText = queryText;
            return candidates;
        }
    }

    [Fact]
    public void MemoryGraphHtml_VendorsPinnedD3WithNoUnpinnedCdn()
    {
        // Locate repo root from test assembly output directory.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "McpEngramMemory.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var htmlPath = Path.Combine(dir.FullName, "visualization", "memory-graph.html");
        var d3Path = Path.Combine(dir.FullName, "visualization", "d3.v7.9.0.min.js");
        Assert.True(File.Exists(htmlPath));
        Assert.True(File.Exists(d3Path));

        var html = File.ReadAllText(htmlPath);
        Assert.DoesNotContain("cdn.jsdelivr.net", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("./d3.v7.9.0.min.js", html, StringComparison.Ordinal);

        var actualHash = Convert.ToBase64String(SHA384.HashData(File.ReadAllBytes(d3Path)));
        Assert.Equal("CjloA8y00+1SDAUkjs099PVfnY2KmDC2BZnws9kh8D/lX1s46w6EPhpXdqMfjK6i", actualHash);
    }

    [Fact]
    public void DebateTools_PreventsPermissionLaundering_AcrossShareAndResolveDebate()
    {
        var dir = SubDir("debate_laundering");
        using var pm = new PersistenceManager(dir, debounceMs: 50);
        using var index = new CognitiveIndex(pm);
        using var graph = new KnowledgeGraph(pm, index);
        var embedding = new HashEmbeddingService();
        var registry = new NamespaceRegistry(index, embedding);
        var sessions = new DebateSessionManager();
        var metrics = new MetricsCollector();

        const string tenant = "t-sec";
        // Alice owns secret-expert and grants Bob READ-ONLY access (Charlie has NO access).
        Assert.True(registry.TryAcquireWriteAccess("alice", "secret-expert", tenant));
        index.Upsert(new CognitiveEntry("s1", embedding.Embed("classified architecture design"), "secret-expert",
            "classified architecture design", tenantId: tenant));
        var grantBob = registry.Share("secret-expert", "alice", "bob", "read", tenant);
        Assert.Equal("shared", grantBob.Status);

        // Bob also owns a namespace already shared with Charlie.
        Assert.True(registry.TryAcquireWriteAccess("bob", "bob-shared", tenant));
        Assert.Equal("shared", registry.Share("bob-shared", "bob", "charlie", "read", tenant).Status);

        var bobAccess = new NamespaceAccess(registry, new PrincipalContext(tenant, "bob"));
        var bobDebate = new DebateTools(index, graph, embedding, sessions, metrics, bobAccess);

        // Bob consults Alice's secret-expert in a debate session.
        var consult = Assert.IsType<ConsultPanelResult>(
            bobDebate.ConsultExpertPanel("architecture design", ["secret-expert"], "launder-1", minScore: 0f));
        Assert.Equal("active-debate-launder-1", consult.DebateNamespace);

        // 1. Bob CANNOT share active-debate-launder-1 with Charlie (Charlie cannot read secret-expert).
        var shareDebate = registry.Share("active-debate-launder-1", "bob", "charlie", "read", tenant);
        Assert.Equal("error_derived_restricted", shareDebate.Status);

        // 2. Bob CANNOT resolve the debate into bob-shared (already shared with Charlie).
        var badResolve = bobDebate.ResolveDebate("launder-1", winningNode: 1,
            consensusSummary: "leaked consensus", targetNamespace: "bob-shared");
        var badResolveMsg = Assert.IsType<string>(badResolve);
        Assert.StartsWith("Error:", badResolveMsg);

        // 3. Bob CAN resolve into a private namespace bob-private, AND the derived restriction propagates.
        var goodResolve = Assert.IsType<ResolveDebateResult>(
            bobDebate.ResolveDebate("launder-1", winningNode: 1,
                consensusSummary: "private consensus", targetNamespace: "bob-private"));
        Assert.Equal("bob-private", goodResolve.ConsensusNamespace);

        // Bob STILL cannot later share bob-private with Charlie!
        var shareTargetDenied = registry.Share("bob-private", "bob", "charlie", "read", tenant);
        Assert.Equal("error_derived_restricted", shareTargetDenied.Status);

        // 4. Once Alice explicitly grants Charlie read access to secret-expert, sharing bob-private succeeds.
        Assert.Equal("shared", registry.Share("secret-expert", "alice", "charlie", "read", tenant).Status);
        var shareTargetAllowed = registry.Share("bob-private", "bob", "charlie", "read", tenant);
        Assert.Equal("shared", shareTargetAllowed.Status);
    }

    [Fact]
    public void Tools_PreValidationAndNonMutatingCalls_DoNotClaimUnregisteredEmptyNamespaces()
    {
        var dir = SubDir("no_prevalidation_claim");
        using var pm = new PersistenceManager(dir, debounceMs: 50);
        using var index = new CognitiveIndex(pm);
        using var graph = new KnowledgeGraph(pm, index);
        using var clusters = new ClusterManager(index, pm);
        var embedding = new HashEmbeddingService();
        var metrics = new MetricsCollector();
        var registry = new NamespaceRegistry(index, embedding);
        var lifecycle = new LifecycleEngine(index, pm);
        var scanner = new AccretionScanner(index, pm);
        var diffusion = new MemoryDiffusionKernel(index, graph);
        var spectral = new SpectralRetrievalReranker(diffusion);
        var dispatcher = new ExpertDispatcher(index, embedding);
        var sessions = new DebateSessionManager();

        const string tenant = "t-claim";
        var bobPrincipal = new PrincipalContext(tenant, "bob");
        var bobAccess = new NamespaceAccess(registry, bobPrincipal);
        var coreTools = new CoreMemoryTools(
            index, new PhysicsEngine(), embedding, metrics, graph, new QueryExpander(),
            new SpreadingActivationService(index, graph, clusters), clusters, registry, bobPrincipal);
        var compositeTools = new CompositeTools(
            index, embedding, graph, lifecycle, dispatcher, metrics, spectral, bobAccess);
        var clusterTools = new ClusterTools(clusters, embedding, bobAccess);
        var debateTools = new DebateTools(index, graph, embedding, sessions, metrics, bobAccess);
        var lifecycleTools = new LifecycleTools(lifecycle, embedding, index, bobAccess);
        var intelTools = new IntelligenceTools(index, graph, embedding, scanner, clusters, lifecycle, bobAccess);
        var diffusionTools = new MemoryDiffusionTools(diffusion, bobAccess);

        // Trigger every failing/non-mutating path as Bob against distinct unclaimed empty namespaces.
        Assert.StartsWith("Error:", coreTools.StoreMemory("id1", "ns-store-bad", text: null, vector: null));
        Assert.IsType<ToolError>(coreTools.StoreBatch("ns-batch-bad", [new BatchEntry { Id = "", Text = "" }]));
        Assert.StartsWith("Error:", Assert.IsType<string>(compositeTools.Remember("id1", "ns-rem-bad", text: "")));
        Assert.IsType<ToolError>(clusterTools.CreateCluster("c1", "ns-cluster-bad", memberIds: ""));
        Assert.StartsWith("Error:", Assert.IsType<string>(debateTools.MapDebateGraph("no-such-session", [new DebateEdge(1, 2, "similar_to", 0.5f)])));
        Assert.StartsWith("Error:", Assert.IsType<string>(debateTools.ResolveDebate("no-such-session-2", 1, "sum", "ns-resolve-bad")));
        _ = lifecycleTools.DeepRecall("ns-deep-empty", text: "query");
        Assert.StartsWith("Error:", intelTools.MergeMemories("missing1", "missing2", "ns-merge-bad"));
        _ = diffusionTools.InvalidateDiffusion("ns-diff-empty");

        // NONE of those unclaimed empty namespaces should have been claimed by Bob in _system_sharing,
        // so Alice can still claim and write to every single one of them!
        string[] untouchedNamespaces =
        [
            "ns-store-bad", "ns-batch-bad", "ns-rem-bad", "ns-cluster-bad",
            "active-debate-no-such-session", "active-debate-no-such-session-2",
            "ns-resolve-bad", "ns-deep-empty", "ns-merge-bad", "ns-diff-empty"
        ];
        foreach (var ns in untouchedNamespaces)
        {
            Assert.Null(index.Get(NamespaceRegistry.PermissionEntryId(ns, tenant),
                NamespaceRegistry.SystemNamespace, tenantId: ""));
            Assert.True(registry.TryAcquireWriteAccess("alice", ns, tenant),
                $"Expected Alice to still be able to claim untouched empty namespace '{ns}'.");
        }
    }

    [Fact]
    public void DebateTools_BlocksTransitiveMultiHopPermissionLaundering()
    {
        var dir = SubDir("debate_transitive_laundering");
        using var pm = new PersistenceManager(dir, debounceMs: 50);
        using var index = new CognitiveIndex(pm);
        using var graph = new KnowledgeGraph(pm, index);
        var embedding = new HashEmbeddingService();
        var registry = new NamespaceRegistry(index, embedding);
        var sessions = new DebateSessionManager();
        var metrics = new MetricsCollector();

        const string tenant = "t-trans";
        Assert.True(registry.TryAcquireWriteAccess("alice", "secret-expert", tenant));
        index.Upsert(new CognitiveEntry("s1", embedding.Embed("classified token rotation key"), "secret-expert",
            "classified token rotation key", tenantId: tenant));
        Assert.Equal("shared", registry.Share("secret-expert", "alice", "bob", "read", tenant).Status);

        var bobAccess = new NamespaceAccess(registry, new PrincipalContext(tenant, "bob"));
        var bobDebate = new DebateTools(index, graph, embedding, sessions, metrics, bobAccess);

        // Hop 1: Bob consults Alice's secret-expert -> active-debate-hop1 (owned by Bob, derived from secret-expert).
        var hop1 = Assert.IsType<ConsultPanelResult>(
            bobDebate.ConsultExpertPanel("token rotation key", ["secret-expert"], "hop1", minScore: 0f));
        Assert.Equal("active-debate-hop1", hop1.DebateNamespace);

        // Hop 2a: Bob tries to launder by consulting his OWN active-debate-hop1 into active-debate-hop2.
        var hop2 = Assert.IsType<ConsultPanelResult>(
            bobDebate.ConsultExpertPanel("token rotation key", ["active-debate-hop1"], "hop2", minScore: 0f));
        Assert.Equal("active-debate-hop2", hop2.DebateNamespace);
        Assert.Equal("error_derived_restricted",
            registry.Share("active-debate-hop2", "bob", "charlie", "read", tenant).Status);

        // Hop 2b: Bob resolves hop1 into bob-private (owned by Bob), then consults bob-private in hop3.
        Assert.IsType<ResolveDebateResult>(
            bobDebate.ResolveDebate("hop1", winningNode: 1, consensusSummary: "derived summary", targetNamespace: "bob-private"));
        var hop3 = Assert.IsType<ConsultPanelResult>(
            bobDebate.ConsultExpertPanel("derived summary", ["bob-private"], "hop3", minScore: 0f));
        Assert.Equal("active-debate-hop3", hop3.DebateNamespace);
        Assert.Equal("error_derived_restricted",
            registry.Share("active-debate-hop3", "bob", "charlie", "read", tenant).Status);

        // Also cannot resolve hop3 into a namespace already shared with Charlie.
        Assert.True(registry.TryAcquireWriteAccess("bob", "bob-public", tenant));
        Assert.Equal("shared", registry.Share("bob-public", "bob", "charlie", "read", tenant).Status);
        var launderResolve = Assert.IsType<string>(
            bobDebate.ResolveDebate("hop3", winningNode: 1, consensusSummary: "leaked", targetNamespace: "bob-public"));
        Assert.StartsWith("Error:", launderResolve);
    }

    [Fact]
    public void DebouncedStorageCoordinator_AntiStarvation_BacksOffOnEntryFailure_AndCommitsUnderConcurrentArrivals()
    {
        var clock = new ControlledTimeProvider();
        bool failEntries = true;
        int entryAttempts = 0;
        int edgeWrites = 0;
        DebouncedStorageCoordinator<DefaultEntryStorageKey>? coord = null;
        coord = new DebouncedStorageCoordinator<DefaultEntryStorageKey>(
            debounceDelay: TimeSpan.FromMilliseconds(100),
            supportsIncrementalWrites: false,
            logger: null,
            makeKey: static (t, id) => new DefaultEntryStorageKey(t, id),
            getTenantId: static k => k.TenantId,
            writeNamespace: (ns, provider) =>
            {
                entryAttempts++;
                if (failEntries) return false;
                // Simulate steady concurrent entry traffic arriving DURING disk/DB I/O.
                if (entryAttempts == 2)
                    coord!.ScheduleSave("concurrent-ns", () => new NamespaceData());
                return true;
            },
            writeIncremental: null,
            writeEdges: _ => { edgeWrites++; return true; },
            writeClusters: _ => true,
            writeDecayConfigs: _ => true,
            writeHnsw: (_, _) => true)
        {
            TimerProvider = clock,
            MaxGlobalDeferrals = 1
        };

        using (coord)
        {
            coord.ScheduleSaveGlobalEdges(() => [new GraphEdge("a", "b", "similar_to", 1f)]);
            var edgeTimer = clock.Latest;
            coord.ScheduleSave("busy", () => new NamespaceData());

            // 1. Defer once (ConsecutiveDeferrals 0 -> 1).
            edgeTimer.DeliverQueuedCallback();
            Assert.Equal(0, entryAttempts);
            Assert.Equal(0, edgeWrites);

            // 2. Threshold reached -> proactive drain fails -> global timer MUST back off (> 100ms),
            //    not spin at 100ms debounce delay.
            edgeTimer.DeliverQueuedCallback();
            Assert.Equal(1, entryAttempts);
            Assert.Equal(0, edgeWrites);
            var backedOffEdgeTimer = clock.Latest;
            Assert.True(backedOffEdgeTimer.DueTime > TimeSpan.FromMilliseconds(100),
                $"Expected exponential backoff > 100ms, got {backedOffEdgeTimer.DueTime}.");

            // 3. Recovery: entry writes succeed, AND a brand-new concurrent entry arrives mid-drain.
            //    Because global snapshot was captured BEFORE the drain, edges MUST commit without starving!
            failEntries = false;
            backedOffEdgeTimer.DeliverQueuedCallback(); // deferral 1 (reset after failure)
            Assert.Equal(0, edgeWrites);
            backedOffEdgeTimer.DeliverQueuedCallback(); // proactive drain succeeds -> edges commit despite concurrent-ns!
            Assert.Equal(2, entryAttempts);
            Assert.Equal(1, edgeWrites);
        }
    }

    [Fact]
    public async Task FileConstitutionAuditStore_MultiInstanceContention_AndTimerRecoveryAfterCancelledFlush()
    {
        var govDir = SubDir("audit_multi_instance");
        await using var storeA = new FileConstitutionAuditStore(govDir, readOnlyFlushInterval: TimeSpan.FromMilliseconds(40), maxBufferedRecords: 8);
        await using var storeB = new FileConstitutionAuditStore(govDir, readOnlyFlushInterval: TimeSpan.FromMilliseconds(40), maxBufferedRecords: 8);

        var provider = new InMemoryConstitutionProvider();
        var evaluator = new DeterministicConstitutionEvaluator([new AuditEnvelopeConstitutionRule()]);
        var kernelA = new ConstitutionKernel(provider, evaluator, storeA);
        var kernelB = new ConstitutionKernel(provider, evaluator, storeB);
        string digest = new('b', 64);

        // Interleave buffered reads and immediate writes across two independent store instances
        // pointing at the same audit.journal.
        var taskA = Task.Run(async () =>
        {
            for (int i = 0; i < 12; i++)
            {
                var kind = i % 4 == 0 ? CognitiveOperationKind.WriteMemory : CognitiveOperationKind.ReadMemory;
                var op = new OperationEnvelope($"mcp:A{i}:op", kind, "", "agent-a", "A", [], null, digest, DateTimeOffset.UtcNow, new Dictionary<string, string>());
                await kernelA.EvaluateAndAuditAsync(op, ConstitutionPhase.Precondition);
            }
        });
        var taskB = Task.Run(async () =>
        {
            for (int i = 0; i < 12; i++)
            {
                var kind = i % 3 == 0 ? CognitiveOperationKind.WriteMemory : CognitiveOperationKind.Retrieve;
                var op = new OperationEnvelope($"mcp:B{i}:op", kind, "", "agent-b", "B", [], null, digest, DateTimeOffset.UtcNow, new Dictionary<string, string>());
                await kernelB.EvaluateAndAuditAsync(op, ConstitutionPhase.Precondition);
            }
        });

        await Task.WhenAll(taskA, taskB);
        await storeA.FlushAsync();
        await storeB.FlushAsync();

        var all = await storeA.ReadAllAsync();
        Assert.Equal(24, all.Count);
        Assert.Equal(Enumerable.Range(1, 24).Select(n => (long)n), all.Select(r => r.Sequence));

        // Verify a FlushAsync that cancels AFTER CancelFlushTimer() (while waiting on the cross-process
        // journal lock) re-arms the timer instead of stranding buffered records indefinitely.
        var tailOp = new OperationEnvelope("mcp:tail:get_memory", CognitiveOperationKind.ReadMemory, "", "agent-a", "Tail", [], null, digest, DateTimeOffset.UtcNow, new Dictionary<string, string>());
        await kernelA.EvaluateAndAuditAsync(tailOp, ConstitutionPhase.Precondition);
        Assert.Equal(1, storeA.PendingBufferedCount);

        var lockPath = Path.Combine(govDir, "audit.journal.lock");
        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using var cancelWhileLocked = new CancellationTokenSource(TimeSpan.FromMilliseconds(35));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => storeA.FlushAsync(cancelWhileLocked.Token).AsTask());
        }
        Assert.Equal(1, storeA.PendingBufferedCount);

        // Wait briefly for the re-armed 40ms timer to flush the record automatically after lock release.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (storeA.PendingBufferedCount > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.Equal(0, storeA.PendingBufferedCount);
        Assert.Equal(25, (await storeB.ReadAllAsync()).Count);
    }

    [Fact]
    public void BackgroundWorkerRegistration_RejectsOverflowingIntervals_WithWarning()
    {
        var env = new Dictionary<string, string?>
        {
            ["MEMORY_DECAY_INTERVAL_MINUTES"] = "1e300",
            ["MEMORY_ACCRETION_INTERVAL_MINUTES"] = "9999999999",
            ["MEMORY_CONSOLIDATION_DELAY_MINUTES"] = "999999999"
        };

        var opts = BackgroundWorkerRegistration.ResolveOptions("full", k => env.GetValueOrDefault(k));
        Assert.Equal(DecayBackgroundService.DefaultInterval, opts.DecayInterval);
        Assert.Equal(AccretionBackgroundService.DefaultInterval, opts.AccretionInterval);
        Assert.Equal(ConsolidationBackgroundService.DefaultStartupDelay, opts.ConsolidationDelay);
        Assert.Equal(3, opts.Warnings.Count);
    }

    [Fact]
    public void SqliteStorageProvider_DebouncedHnswSnapshot_CoalescesAndSynchronizesDelete()
    {
        var clock = new ControlledTimeProvider();
        var dbPath = Path.Combine(SubDir("sqlite_hnsw_debounce"), "memory.db");
        using var sqlite = new SqliteStorageProvider(dbPath, debounceMs: 100) { TimerProvider = clock };

        int builds = 0;
        var hnsw = new HnswIndex();
        for (int i = 0; i < 5; i++)
            hnsw.Add($"e-{i}", [MathF.Cos(i), MathF.Sin(i), 0.1f]);

        for (int i = 0; i < 5; i++)
        {
            sqlite.ScheduleSaveHnswSnapshot("big-ns", () =>
            {
                Interlocked.Increment(ref builds);
                return hnsw.CreateSnapshot();
            });
        }

        Assert.Equal(0, Volatile.Read(ref builds));
        Assert.True(sqlite.TryFlush());
        Assert.Equal(1, Volatile.Read(ref builds));
        var snapshot = sqlite.LoadHnswSnapshot("big-ns");
        Assert.NotNull(snapshot);
        Assert.Equal(5, snapshot!.NodeIds.Count);

        sqlite.DeleteHnswSnapshot("big-ns");
        Assert.Null(sqlite.LoadHnswSnapshot("big-ns"));
    }

    [Fact]
    public void DebateTools_ResolveDebate_BlocksLaunderingFromForeignOwnedDebateNamespace()
    {
        var dir = SubDir("debate_foreign_owner_resolve");
        using var pm = new PersistenceManager(dir, debounceMs: 50);
        using var index = new CognitiveIndex(pm);
        using var graph = new KnowledgeGraph(pm, index);
        var embedding = new HashEmbeddingService();
        var registry = new NamespaceRegistry(index, embedding);
        var sessions = new DebateSessionManager();
        var metrics = new MetricsCollector();

        const string tenant = "t-foreign-debate";
        // Alice owns alice-secret and runs a debate over her own namespace -> active-debate-alice-1
        // has Owner = "alice" and empty DerivedSources.
        Assert.True(registry.TryAcquireWriteAccess("alice", "alice-secret", tenant));
        index.Upsert(new CognitiveEntry("a1", embedding.Embed("proprietary key material"), "alice-secret",
            "proprietary key material", tenantId: tenant));

        var aliceAccess = new NamespaceAccess(registry, new PrincipalContext(tenant, "alice"));
        var aliceDebate = new DebateTools(index, graph, embedding, sessions, metrics, aliceAccess);
        var consult = Assert.IsType<ConsultPanelResult>(
            aliceDebate.ConsultExpertPanel("key material", ["alice-secret"], "alice-1", minScore: 0f));
        Assert.Equal("active-debate-alice-1", consult.DebateNamespace);

        // Alice shares the active debate (write) with Bob, NOT with Charlie.
        Assert.Equal("shared", registry.Share("active-debate-alice-1", "alice", "bob", "write", tenant).Status);

        // Bob owns bob-shared (readable by Charlie) and wants to resolve Alice's debate.
        Assert.True(registry.TryAcquireWriteAccess("bob", "bob-shared", tenant));
        Assert.Equal("shared", registry.Share("bob-shared", "bob", "charlie", "read", tenant).Status);

        var bobAccess = new NamespaceAccess(registry, new PrincipalContext(tenant, "bob"));
        var bobDebate = new DebateTools(index, graph, embedding, sessions, metrics, bobAccess);

        // 1. Bob CANNOT resolve Alice's foreign-owned debate directly into bob-shared (Charlie lacks access).
        var directLeak = Assert.IsType<string>(
            bobDebate.ResolveDebate("alice-1", winningNode: 1, consensusSummary: "leaked", targetNamespace: "bob-shared"));
        Assert.StartsWith("Error:", directLeak);

        // 2. Bob CAN resolve into a private namespace, and active-debate-alice-1 is recorded as a
        //    derived source so Bob still cannot subsequently share it with Charlie.
        var privateResolve = Assert.IsType<ResolveDebateResult>(
            bobDebate.ResolveDebate("alice-1", winningNode: 1, consensusSummary: "private", targetNamespace: "bob-private"));
        Assert.Equal("bob-private", privateResolve.ConsensusNamespace);
        Assert.Equal("error_derived_restricted",
            registry.Share("bob-private", "bob", "charlie", "read", tenant).Status);

        // 3. Once Alice explicitly grants Charlie read access to active-debate-alice-1, sharing succeeds.
        Assert.Equal("shared", registry.Share("active-debate-alice-1", "alice", "charlie", "read", tenant).Status);
        Assert.Equal("shared", registry.Share("bob-private", "bob", "charlie", "read", tenant).Status);
    }

    [Fact]
    public void ConstitutionMcpFilter_NormalizeToolErrorResult_HandlesPascalCaseStatus()
    {
        var pascalErr = new CallToolResult
        {
            Content = [new TextContentBlock { Text = """{"Status":"error","Error":"model unavailable"}""" }]
        };
        Assert.True(ConstitutionMcpFilter.NormalizeToolErrorResult(pascalErr).IsError);
    }
}
