using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Models.Governance;
using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Models.Learning;
using McpEngramMemory.Core.Models.Provenance;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Constitution;
using McpEngramMemory.Core.Services.Governance;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Knowledge;
using McpEngramMemory.Core.Services.Learning;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Provenance;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpEngramMemory.Tests;

/// <summary>
/// Regression tests for the 2.1.x concurrency, HNSW, lifecycle, accretion, and
/// governed-learning hardening fixes.
/// </summary>
public sealed class ConcurrencyLifecycleFixesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"concurrency_lifecycle_{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }

    private string SubDir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 1. KnowledgeGraph & ClusterManager reader concurrency + reverse index
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task KnowledgeGraph_LoadedReadPaths_DoNotSerializeOnUpgradeableReadLock()
    {
        using var persistence = new PersistenceManager(SubDir("kg_readers"));
        using var index = new CognitiveIndex(persistence);
        var graph = new KnowledgeGraph(persistence, index);

        index.Upsert(new CognitiveEntry("a", [1f, 0f], "ns", "entry a", tenantId: "t1"));
        index.Upsert(new CognitiveEntry("b", [0f, 1f], "ns", "entry b", tenantId: "t1"));
        graph.AddEdge(new GraphEdge("a", "b", "similar_to", 0.9f, tenantId: "t1"));

        // Warm the one-shot lazy load so _loaded is true.
        Assert.Equal(1, graph.EdgeCount);

        // Hold the single ReaderWriterLockSlim upgradeable read lock on a helper thread.
        // Any method that still called EnterUpgradeableReadLock() on the loaded fast path
        // would block forever until the helper releases; EnterReadLock() runs concurrently.
        var rwLock = GetPrivateField<ReaderWriterLockSlim>(graph, "_lock");
        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var holder = Task.Run(() =>
        {
            rwLock.EnterUpgradeableReadLock();
            try
            {
                held.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
            finally { rwLock.ExitUpgradeableReadLock(); }
        });

        Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var readTask = Task.Run(() =>
            {
                Assert.Equal(1, graph.EdgeCount);
                Assert.Single(graph.GetNeighbors("a", relation: null, direction: "both", tenantId: "t1").Neighbors);
                Assert.NotEmpty(graph.Traverse("a", tenantId: "t1").Edges);
                Assert.Single(graph.GetStoredEdgesForEntry("a", tenantId: "t1"));
                Assert.Single(graph.GetAllEdges());
                Assert.Single(graph.GetStoredEdges(tenantId: "t1"));
            });

            await readTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ClusterManager_LoadedReadPaths_DoNotSerializeOnUpgradeableReadLock()
    {
        using var persistence = new PersistenceManager(SubDir("cm_readers"));
        using var index = new CognitiveIndex(persistence);
        var clusters = new ClusterManager(index, persistence);

        index.Upsert(new CognitiveEntry("e1", [1f, 0f], "ns", "entry 1", tenantId: "t1"));
        index.Upsert(new CognitiveEntry("e2", [0.9f, 0.1f], "ns", "entry 2", tenantId: "t1"));
        Assert.DoesNotContain("Error:", clusters.CreateCluster("c1", "ns", ["e1", "e2"], "label", tenantId: "t1"));

        // Warm one-shot load so _loaded is true.
        Assert.Equal(1, clusters.ClusterCount);

        var rwLock = GetPrivateField<ReaderWriterLockSlim>(clusters, "_lock");
        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var holder = Task.Run(() =>
        {
            rwLock.EnterUpgradeableReadLock();
            try
            {
                held.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
            finally { rwLock.ExitUpgradeableReadLock(); }
        });

        Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var readTask = Task.Run(() =>
            {
                Assert.Equal(1, clusters.ClusterCount);
                Assert.NotNull(clusters.GetCluster("c1", tenantId: "t1"));
                Assert.Single(clusters.ListClusters("ns", tenantId: "t1"));
                Assert.Single(clusters.GetClusterMembershipsForEntry("e1", tenantId: "t1"));
                Assert.Equal("ns", clusters.GetClusterNamespace("c1", tenantId: "t1"));
                Assert.True(clusters.TryGetClusterIdentity("c1", tenantId: "t1", out var stamp, out var instance));
                Assert.False(string.IsNullOrEmpty(stamp));
                Assert.False(string.IsNullOrEmpty(instance));
            });

            await readTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void ClusterManager_ReverseIndex_StaysConsistentAcrossCreateUpdateEvictTransferAndReload()
    {
        var dir = SubDir("cm_reverse_idx");
        using (var persistence = new PersistenceManager(dir, debounceMs: 50))
        using (var index = new CognitiveIndex(persistence))
        {
            var clusters = new ClusterManager(index, persistence);

            // Seed two tenants sharing identical entry IDs to verify tenant-partitioned reverse lookup.
            index.Upsert(new CognitiveEntry("shared", [1f, 0f], "ns1", "t1 shared", tenantId: "t1"));
            index.Upsert(new CognitiveEntry("e2", [0.9f, 0.1f], "ns1", "t1 e2", tenantId: "t1"));
            index.Upsert(new CognitiveEntry("e3", [0.8f, 0.2f], "ns1", "t1 e3", tenantId: "t1"));
            index.Upsert(new CognitiveEntry("shared", [1f, 0f], "ns1", "t2 shared", tenantId: "t2"));
            index.Upsert(new CognitiveEntry("t2-other", [0.9f, 0.1f], "ns1", "t2 other", tenantId: "t2"));

            Assert.DoesNotContain("Error:", clusters.CreateCluster("c1", "ns1", ["shared", "e2"], "first", tenantId: "t1"));
            Assert.DoesNotContain("Error:", clusters.CreateCluster("c2", "ns1", ["shared"], "second", tenantId: "t1"));
            Assert.DoesNotContain("Error:", clusters.CreateCluster("t2-c1", "ns1", ["shared", "t2-other"], "t2", tenantId: "t2"));

            // Reverse index lookup returns tenant-local clusters only.
            var t1Shared = clusters.GetClusterMembershipsForEntry("shared", tenantId: "t1")
                .Select(m => m.ClusterId).OrderBy(x => x).ToList();
            Assert.Equal(["c1", "c2"], t1Shared);
            Assert.Equal(["t2-c1"], clusters.GetClusterMembershipsForEntry("shared", tenantId: "t2").Select(m => m.ClusterId));

            // UpdateCluster add + remove updates reverse index accurately.
            Assert.DoesNotContain("Error:", clusters.UpdateCluster("c1", addIds: ["e3"], removeIds: ["e2"], label: null, tenantId: "t1"));
            Assert.Empty(clusters.GetClusterMembershipsForEntry("e2", tenantId: "t1"));
            Assert.Equal(["c1"], clusters.GetClusterMembershipsForEntry("e3", tenantId: "t1").Select(m => m.ClusterId));

            // RemoveEntryFromAllClusters (eviction) removes membership across all of t1's clusters
            // without touching t2's same-id entry.
            clusters.RemoveEntryFromAllClusters("shared", tenantId: "t1");
            Assert.Empty(clusters.GetClusterMembershipsForEntry("shared", tenantId: "t1"));
            Assert.Empty(clusters.GetCluster("c2", tenantId: "t1")!.Members);
            Assert.Equal(["t2-c1"], clusters.GetClusterMembershipsForEntry("shared", tenantId: "t2").Select(m => m.ClusterId));

            // TransferMembership migrates e3 -> shared2 and keeps reverse index coherent.
            index.Upsert(new CognitiveEntry("shared2", [0.7f, 0.3f], "ns1", "t1 shared2", tenantId: "t1"));
            Assert.Equal(1, clusters.TransferMembership("e3", "shared2", tenantId: "t1"));
            Assert.Empty(clusters.GetClusterMembershipsForEntry("e3", tenantId: "t1"));
            Assert.Equal(["c1"], clusters.GetClusterMembershipsForEntry("shared2", tenantId: "t1").Select(m => m.ClusterId));

            Assert.True(persistence.TryFlush());
        }

        // Reload into a brand-new ClusterManager and verify reverse index is rebuilt on lazy load.
        using (var persistence2 = new PersistenceManager(dir, debounceMs: 50))
        using (var index2 = new CognitiveIndex(persistence2))
        {
            var reloaded = new ClusterManager(index2, persistence2);
            Assert.Equal(["c1"], reloaded.GetClusterMembershipsForEntry("shared2", tenantId: "t1").Select(m => m.ClusterId));
            Assert.Empty(reloaded.GetClusterMembershipsForEntry("e3", tenantId: "t1"));
            Assert.Equal(["t2-c1"], reloaded.GetClusterMembershipsForEntry("shared", tenantId: "t2").Select(m => m.ClusterId));
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 2. HNSW tombstone traversal, snapshot staleness, O(C) search, persistence
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HnswIndex_TombstoneBridgeNode_DoesNotDisconnectGreedyOrLayerSearch()
    {
        // Construct a deterministic 2-layer graph where node 1 ("bridge") is the ONLY bridge
        // connecting entry point 0 ("entry") to target 2 ("target") on both layer 1 and layer 0,
        // and node 1 is tombstoned (deleted).
        var vectors = new Dictionary<string, float[]>
        {
            ["entry"] = [1f, 0f, 0f],
            ["bridge"] = [0.7071f, 0.7071f, 0f],
            ["target"] = [0f, 1f, 0f]
        };

        var snapshot = new HnswSnapshot
        {
            M = 16,
            EfConstruction = 100,
            EntryPoint = 0,
            MaxLevel = 1,
            NodeIds = ["entry", "bridge", "target"],
            NodeLevels = [1, 1, 0],
            Deleted = [1],
            // Connections[node][layer]:
            // Node 0: layer 0 -> [1], layer 1 -> [1]
            // Node 1 (deleted bridge): layer 0 -> [0, 2], layer 1 -> [0]
            // Node 2 (target): layer 0 -> [1]
            Connections =
            [
                [[1], [1]],
                [[0, 2], [0]],
                [[1]]
            ]
        };

        var restored = HnswIndex.RestoreFromSnapshot(snapshot, id => vectors.GetValueOrDefault(id));
        Assert.NotNull(restored);
        Assert.Equal(2, restored.Count);
        Assert.False(restored.Remove("bridge")); // deleted node must NOT be mapped in _idToIndex

        var results = restored.Search([0f, 1f, 0f], k: 2);
        Assert.DoesNotContain(results, r => r.Id == "bridge");
        Assert.Contains(results, r => r.Id == "target");
        Assert.Equal("target", results[0].Id);
        Assert.True(results[0].Score > 0.99f);
    }

    [Fact]
    public void HnswIndex_RestoreFromSnapshot_SkipsDeletedIdMapping_AndRejectsMissingLiveEntries()
    {
        var index = new HnswIndex();
        var vectors = new Dictionary<string, float[]>
        {
            ["a"] = [1f, 0f, 0f],
            ["b"] = [0f, 1f, 0f],
            ["c"] = [0f, 0f, 1f],
            ["d-new"] = [0.6f, 0.8f, 0f]
        };

        index.Add("a", vectors["a"]);
        index.Add("b", vectors["b"]);
        index.Add("c", vectors["c"]);
        Assert.True(index.Remove("b"));

        var snapshot = index.CreateSnapshot();

        // 2-arg restore must NOT map tombstoned "b" into _idToIndex.
        var restored = HnswIndex.RestoreFromSnapshot(snapshot, id => vectors.GetValueOrDefault(id));
        Assert.NotNull(restored);
        Assert.Equal(2, restored.Count);
        Assert.False(restored.Remove("b"));
        Assert.True(restored.Remove("a"));
        Assert.True(restored.Remove("c"));

        // 3-arg restore succeeds when all expected live IDs are present in the snapshot.
        var valid = HnswIndex.RestoreFromSnapshot(snapshot, id => vectors.GetValueOrDefault(id), ["a", "c"]);
        Assert.NotNull(valid);
        Assert.Equal(2, valid.Count);

        // 3-arg restore returns null when a live entry with a non-zero vector is missing from the snapshot.
        var stale = HnswIndex.RestoreFromSnapshot(snapshot, id => vectors.GetValueOrDefault(id), ["a", "c", "d-new"]);
        Assert.Null(stale);
    }

    [Fact]
    public void CognitiveIndex_HnswSearch_SkipsMismatchedDimensions_WithoutThrowing()
    {
        using var persistence = new PersistenceManager(SubDir("hnsw_dim_guard"));
        using var index = new CognitiveIndex(persistence);

        // Populate >= HnswThreshold (200) entries of dimension 4 so HNSW activates.
        int count = NamespaceStore.HnswThreshold + 10;
        var batch = new List<CognitiveEntry>(count);
        for (int i = 0; i < count; i++)
        {
            float angle = i * 0.03f;
            batch.Add(new CognitiveEntry($"e{i}", [MathF.Cos(angle), MathF.Sin(angle), 0.1f, 0f], "dims", $"text {i}", tenantId: "t1"));
        }
        index.UpsertBatch(batch);
        Assert.True(index.HasHnswIndex("dims", "t1"));

        // Add an 8-dim entry into the same namespace after HNSW is built.
        index.Upsert(new CognitiveEntry("wide", [1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f], "dims", "wide vector", tenantId: "t1"));

        // Querying with 4-dim succeeds and skips the 8-dim entry safely.
        var r4 = index.Search([1f, 0f, 0.1f, 0f], "dims", tenantId: "t1", k: 5, minScore: 0f);
        Assert.NotEmpty(r4);
        Assert.DoesNotContain(r4, r => r.Id == "wide");

        // Querying with an 8-dim vector does NOT throw ArgumentOutOfRangeException.
        var r8 = index.Search([1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f], "dims", tenantId: "t1", k: 5, minScore: 0f);
        Assert.All(r8, r => Assert.Equal("wide", r.Id));
    }

    [Fact]
    public void CognitiveIndex_UpsertBatchSavesHnswOnce_AndNonVectorMutationsSkipHnswSaves()
    {
        using var inner = new PersistenceManager(SubDir("hnsw_persist_cost"), debounceMs: 50);
        using var provider = new CountingStorageProvider(inner);
        using var index = new CognitiveIndex(provider);

        int count = NamespaceStore.HnswThreshold + 10;
        var batch = new List<CognitiveEntry>(count);
        for (int i = 0; i < count; i++)
            batch.Add(new CognitiveEntry($"e{i}", [MathF.Cos(i * 0.03f), MathF.Sin(i * 0.03f), 0.2f], "ns", $"entry {i}", tenantId: "t1"));

        index.UpsertBatch(batch);

        // UpsertBatch must persist HNSW ONCE after the batch completes, never N times.
        Assert.Equal(1, provider.HnswSaves);
        provider.ResetCounts();

        // Non-vector mutations must NEVER synchronously rewrite HNSW snapshots.
        index.RecordAccess("e0", "ns", tenantId: "t1");
        Assert.True(index.BoostActivationEnergy("e1", "ns", 1.5f, tenantId: "t1"));
        Assert.True(index.SetLifecycleState("e2", "ltm", "ns", tenantId: "t1"));
        Assert.Equal(3, index.SetLifecycleStateBatch(["e3", "e4", "e5"], "ltm", "ns", tenantId: "t1"));

        Assert.Equal(0, provider.HnswSaves);
    }

    [Fact]
    public void CognitiveIndex_SqliteRestart_LazilyBuildsHnswOnFirstSearch_AndDetectsStaleJsonSnapshot()
    {
        // Part A: SQLite restart (no persisted HNSW snapshot) builds HNSW lazily on first Search.
        int count = NamespaceStore.HnswThreshold + 10;
        var dbPath = Path.Combine(SubDir("sqlite_lazy_hnsw"), "memory.db");
        using (var sqlite = new SqliteStorageProvider(dbPath, debounceMs: 50))
        using (var index = new CognitiveIndex(sqlite))
        {
            var batch = new List<CognitiveEntry>(count);
            for (int i = 0; i < count; i++)
            {
                float a = i * 0.03f;
                batch.Add(new CognitiveEntry($"e{i}", [MathF.Cos(a), MathF.Sin(a), 0f], "big", $"item {i}", tenantId: "t1"));
            }
            index.UpsertBatch(batch);
            Assert.True(sqlite.TryFlush());
        }

        using (var sqlite2 = new SqliteStorageProvider(dbPath, debounceMs: 50))
        using (var index2 = new CognitiveIndex(sqlite2))
        {
            // Loading entries alone does not yet have an HNSW index on SQLite.
            Assert.Equal(count, index2.CountInNamespace("big", "t1"));
            Assert.False(index2.HasHnswIndex("big", "t1"));

            // First Search builds HNSW lazily under read lock without deadlock and returns exact top hit.
            var results = index2.Search([1f, 0f, 0f], "big", tenantId: "t1", k: 5, minScore: 0f);
            Assert.True(index2.HasHnswIndex("big", "t1"));
            Assert.NotEmpty(results);
            Assert.Equal("e0", results[0].Id);
        }

        // Part B: Stale JSON HNSW snapshot missing a newer live entry triggers clean rebuild.
        var jsonDir = SubDir("json_stale_hnsw");
        using (var pm = new PersistenceManager(jsonDir, debounceMs: 50))
        using (var index = new CognitiveIndex(pm))
        {
            var batch = new List<CognitiveEntry>(count);
            for (int i = 0; i < count; i++)
            {
                float a = i * 0.03f;
                batch.Add(new CognitiveEntry($"e{i}", [MathF.Cos(a), MathF.Sin(a), 0f], "stale-ns", $"item {i}"));
            }
            index.UpsertBatch(batch);
            Assert.True(pm.TryFlush());

            // Inject a new live entry directly into the persisted namespace JSON without updating
            // the persisted HNSW snapshot (simulating a crash between entry flush and HNSW save).
            // Place it in the neighborhood of e0 (cos ~0.97) with a distinct z-component so it
            // connects cleanly in HNSW yet is uniquely distinguishable at minScore >= 0.99.
            var data = pm.LoadNamespace("stale-ns");
            float[] brandNewVec = [0.97f, 0.01f, 0.2429f];
            data.Entries.Add(new CognitiveEntry("brand-new", brandNewVec, "stale-ns", "newly persisted entry"));
            pm.SaveNamespaceSync("stale-ns", data);
        }

        using (var pm2 = new PersistenceManager(jsonDir, debounceMs: 50))
        using (var index2 = new CognitiveIndex(pm2))
        {
            // Loading the namespace must REJECT the stale HNSW snapshot because "brand-new" is missing.
            Assert.Equal(count + 1, index2.CountInNamespace("stale-ns", ""));
            Assert.False(index2.HasHnswIndex("stale-ns", ""));

            // First Search lazily rebuilds HNSW from all live entries including "brand-new".
            var results = index2.Search([0.97f, 0.01f, 0.2429f], "stale-ns", tenantId: "", k: 3, minScore: 0.99f);
            Assert.True(index2.HasHnswIndex("stale-ns", ""));
            Assert.NotEmpty(results);
            Assert.Equal("brand-new", results[0].Id);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 3. CognitiveIndex MaxTotalCount, Dispose, RebuildEmbeddings, SearchMultiple
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CognitiveIndex_MaxTotalCount_EnforcedAcrossAllTenants()
    {
        var dir = SubDir("max_total_multitenant");
        var limits = new MemoryLimitsConfig { MaxTotalCount = 5 };

        using (var persistence = new PersistenceManager(dir, debounceMs: 50))
        using (var index = new CognitiveIndex(persistence, limits))
        {
            // Fill 3 slots in tenant "acme" and 2 slots in tenant "beta" -> total 5.
            for (int i = 0; i < 3; i++)
                index.Upsert(new CognitiveEntry($"a{i}", [1f, 0f], "ns", $"acme {i}", tenantId: "acme"));
            for (int i = 0; i < 2; i++)
                index.Upsert(new CognitiveEntry($"b{i}", [1f, 0f], "ns", $"beta {i}", tenantId: "beta"));

            // Named tenant, another named tenant, and legacy tenant are all blocked at MaxTotalCount=5.
            Assert.Throws<InvalidOperationException>(() =>
                index.Upsert(new CognitiveEntry("b2", [1f, 0f], "ns", "overflow", tenantId: "beta")));
            Assert.Throws<InvalidOperationException>(() =>
                index.Upsert(new CognitiveEntry("g0", [1f, 0f], "ns", "overflow", tenantId: "gamma")));
            Assert.Throws<InvalidOperationException>(() =>
                index.Upsert(new CognitiveEntry("l0", [1f, 0f], "ns", "overflow", tenantId: "")));

            // Updating an existing entry still succeeds because it does not consume a new slot.
            index.Upsert(new CognitiveEntry("a0", [0f, 1f], "ns", "updated acme 0", tenantId: "acme"));

            // UpsertBatch stops at MaxTotalCount across tenants and admits 0 entries when full.
            int accepted = index.UpsertBatch([
                new CognitiveEntry("x1", [1f, 0f], "ns", "x1", tenantId: "gamma"),
                new CognitiveEntry("x2", [1f, 0f], "ns", "x2", tenantId: "gamma")
            ]);
            Assert.Equal(0, accepted);
            Assert.Equal(0, index.CountInNamespace("ns", "gamma"));

            // Deleting one entry in "acme" frees a slot for "gamma".
            Assert.True(index.Delete("a2", "ns", tenantId: "acme"));
            index.Upsert(new CognitiveEntry("g0", [1f, 0f], "ns", "now fits", tenantId: "gamma"));
            Assert.Equal(1, index.CountInNamespace("ns", "gamma"));
            Assert.True(persistence.TryFlush());
        }

        // Reload into a fresh index and verify MaxTotalCount is still enforced after cold restart.
        using (var persistence2 = new PersistenceManager(dir, debounceMs: 50))
        using (var index2 = new CognitiveIndex(persistence2, limits))
        {
            Assert.Throws<InvalidOperationException>(() =>
                index2.Upsert(new CognitiveEntry("extra", [1f, 0f], "ns", "overflow after reload", tenantId: "delta")));
        }
    }

    [Fact]
    public async Task CognitiveIndex_Dispose_LeavesContendedLocksPublished_RemovesCleanLocks()
    {
        using var persistence = new PersistenceManager(SubDir("dispose_contended"));
        var index = new CognitiveIndex(persistence);

        index.Upsert(new CognitiveEntry("e1", [1f, 0f], "idle-ns", "idle", tenantId: "t1"));
        index.Upsert(new CognitiveEntry("e2", [1f, 0f], "held-ns", "held", tenantId: "t1"));
        Assert.Equal(2, index.NamespaceLockCount);

        var baseline = index.OccupancyRevisionFor("held-ns", "t1");
        using var pinHeld = new ManualResetEventSlim(false);
        using var waiterStarted = new ManualResetEventSlim(false);
        using var releasePin = new ManualResetEventSlim(false);

        // ReaderWriterLockSlim.Dispose throws SynchronizationLockException when a lock has active
        // waiters (holder + queued waiter), matching real shutdown contention.
        var holder = Task.Run(() =>
        {
            using var pin = index.TryPinOccupancy("held-ns", "t1", baseline);
            Assert.NotNull(pin);
            pinHeld.Set();
            releasePin.Wait(TimeSpan.FromSeconds(10));
        });

        Assert.True(pinHeld.Wait(TimeSpan.FromSeconds(5)));

        // Queue a writer behind the held read lock so WaitingWriteCount > 0 on "held-ns".
        var waiter = Task.Run(() =>
        {
            waiterStarted.Set();
            try { index.Upsert(new CognitiveEntry("e3", [0f, 1f], "held-ns", "queued", tenantId: "t1")); }
            catch (ObjectDisposedException) { }
        });

        Assert.True(waiterStarted.Wait(TimeSpan.FromSeconds(5)));
        var heldLock = GetPrivateField<ConcurrentDictionary<string, ReaderWriterLockSlim>>(index, "_nsLocks")[
            NamespaceStore.PartitionKey("t1", "held-ns")];
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (heldLock.WaitingWriteCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(5);
        Assert.True(heldLock.WaitingWriteCount > 0, "Expected queued writer on held-ns lock.");

        try
        {
            index.Dispose();

            Assert.Equal(1, index.DisposalContendedLockCount);
            // The uncontended lock was disposed and removed; the contended lock stayed published.
            Assert.Equal(1, index.NamespaceLockCount);
        }
        finally
        {
            releasePin.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
            await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CognitiveIndex_RebuildEmbeddings_ContextualPrefix_NonBlocking_AndRevisionGuarded()
    {
        // Part A: Contextual prefix overload vs default compatibility overload.
        using (var persistence = new PersistenceManager(SubDir("rebuild_prefix")))
        using (var index = new CognitiveIndex(persistence))
        {
            index.Upsert(new CognitiveEntry("e1", [1f, 0f], "work", "architecture decision", category: "decision", tenantId: "t1"));
            index.Upsert(new CognitiveEntry("e2", [0f, 1f], "work", "general note", category: null, tenantId: "t1"));

            var recording = new RecordingEmbeddingService();
            var (updated1, skipped1) = index.RebuildEmbeddings("work", recording, tenantId: "t1", useContextualPrefix: true);
            Assert.Equal(2, updated1);
            Assert.Equal(0, skipped1);
            Assert.Contains("[decision] architecture decision", recording.RecordedTexts);
            Assert.Contains("[work] general note", recording.RecordedTexts);

            recording.RecordedTexts.Clear();
            var (updated2, _) = index.RebuildEmbeddings("work", recording, tenantId: "t1");
            Assert.Equal(2, updated2);
            Assert.Contains("architecture decision", recording.RecordedTexts);
            Assert.Contains("general note", recording.RecordedTexts);
        }

        // Part B: Non-blocking embedding computation + Revision CAS guard against concurrent writes/deletes.
        using (var persistence = new PersistenceManager(SubDir("rebuild_nonblocking")))
        using (var index = new CognitiveIndex(persistence))
        {
            index.Upsert(new CognitiveEntry("e1", [1f, 0f, 0f], "ns", "first", tenantId: "t1"));
            index.Upsert(new CognitiveEntry("e2", [0f, 1f, 0f], "ns", "second", tenantId: "t1"));
            index.Upsert(new CognitiveEntry("e3", [0f, 0f, 1f], "ns", "third", tenantId: "t1"));

            using var enteredEmbed = new ManualResetEventSlim(false);
            using var releaseEmbed = new ManualResetEventSlim(false);
            var blocking = new BlockingEmbeddingService(enteredEmbed, releaseEmbed);

            var rebuildTask = Task.Run(() => index.RebuildEmbeddings("ns", blocking, tenantId: "t1"));

            Assert.True(enteredEmbed.Wait(TimeSpan.FromSeconds(5)), "RebuildEmbeddings never invoked Embed.");

            // While Embed is blocked, concurrent reads, upserts, and deletes on the SAME partition
            // must complete immediately without blocking on the partition write lock.
            var concurrentOps = Task.Run(() =>
            {
                var read = index.Get("e1", "ns", tenantId: "t1");
                Assert.NotNull(read);
                Assert.NotEmpty(index.Search([1f, 0f, 0f], "ns", tenantId: "t1", k: 3, minScore: 0f));

                // Mutate e1 (bumps Revision) and delete e2 while RebuildEmbeddings is in Phase 2.
                index.Upsert(new CognitiveEntry("e1", [0.5f, 0.5f, 0.5f, 0.5f], "ns", "newer text", tenantId: "t1"));
                Assert.True(index.Delete("e2", "ns", tenantId: "t1"));
            });

            await concurrentOps.WaitAsync(TimeSpan.FromSeconds(5));

            releaseEmbed.Set();
            var (updated, skipped) = await rebuildTask.WaitAsync(TimeSpan.FromSeconds(5));

            // Only untouched e3 should be swapped; concurrently updated e1 and deleted e2 must be skipped.
            Assert.Equal(1, updated);
            Assert.Equal(2, skipped);

            var e1 = index.Get("e1", "ns", tenantId: "t1")!;
            Assert.Equal("newer text", e1.Text);
            Assert.Equal(4, e1.Vector.Length); // kept concurrent writer's vector, not rebuilt 6-dim vector
            Assert.Null(index.Get("e2", "ns", tenantId: "t1")); // not resurrected
            Assert.Equal(6, index.Get("e3", "ns", tenantId: "t1")!.Vector.Length); // rebuilt cleanly
        }
    }

    [Fact]
    public void CognitiveIndex_SearchMultiple_HonorsSummaryFirstAndRerank_AcrossHybridAndVectorPaths()
    {
        using var persistence = new PersistenceManager(SubDir("search_multiple_flags"));
        using var index = new CognitiveIndex(persistence);

        // Two namespaces: ns-a has higher-scoring regular entries, ns-b has a summary node.
        index.Upsert(new CognitiveEntry("reg-1", [1f, 0f, 0f], "ns-a", "ordinary regular note one", tenantId: "t1"));
        index.Upsert(new CognitiveEntry("reg-2", [0.99f, 0.14f, 0f], "ns-a", "ordinary regular note two", tenantId: "t1"));
        var summary = new CognitiveEntry("sum-1", [0.85f, 0.52f, 0f], "ns-b", "cluster summary overview", tenantId: "t1")
        {
            IsSummaryNode = true,
            SourceClusterId = "c-sum"
        };
        index.Upsert(summary);

        // summaryFirst=true puts the summary first across BOTH vector-only and hybrid SearchMultiple.
        var vecSummaryFirst = index.SearchMultiple(
            [1f, 0f, 0f], ["ns-a", "ns-b"], queryText: "overview", tenantId: "t1",
            k: 2, minScore: 0f, hybrid: false, summaryFirst: true);
        Assert.Equal("sum-1", vecSummaryFirst[0].Id);

        var hybSummaryFirst = index.SearchMultiple(
            [1f, 0f, 0f], ["ns-a", "ns-b"], queryText: "ordinary regular note", tenantId: "t1",
            k: 2, minScore: 0f, hybrid: true, summaryFirst: true);
        Assert.Equal("sum-1", hybSummaryFirst[0].Id);

        // rerank=true on vector-only path (vectors above 0.50 auto-escalation threshold so hybrid
        // escalation does NOT fire) promotes the exact lexical match over a slightly closer vector.
        index.Upsert(new CognitiveEntry("vec-only", [1f, 0f, 0f], "ns-r", "completely unrelated bananas", tenantId: "t1"));
        index.Upsert(new CognitiveEntry("kw-match", [0.98f, 0.19f, 0f], "ns-r", "quantum entanglement protocol", tenantId: "t1"));

        var withoutRerank = index.SearchMultiple(
            [1f, 0f, 0f], ["ns-r"], queryText: "quantum entanglement protocol", tenantId: "t1",
            k: 1, minScore: 0f, hybrid: false, rerank: false);
        Assert.Equal("vec-only", withoutRerank[0].Id);

        var withRerank = index.SearchMultiple(
            [1f, 0f, 0f], ["ns-r"], queryText: "quantum entanglement protocol", tenantId: "t1",
            k: 1, minScore: 0f, hybrid: false, rerank: true);
        Assert.Equal("kw-match", withRerank[0].Id);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 4. LifecycleEngine, Decay/Consolidation/Accretion services, DBSCAN
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void LifecycleEngine_RunDecayCycle_SkipsSystemNamespaces_AndSuppressesIdleAndArchivedWrites()
    {
        var clock = new SimulatedTimeProvider();
        using var inner = new PersistenceManager(SubDir("decay_write_storm"), debounceMs: 50);
        using var provider = new CountingStorageProvider(inner);
        using var index = new CognitiveIndex(provider) { Clock = clock };
        var lifecycle = new LifecycleEngine(index, provider);

        var now = clock.GetUtcNow();
        // System namespace (_-prefixed) should be skipped completely.
        index.Upsert(new CognitiveEntry("sys1", [1f, 0f], "_system_meta", "system record", lifecycleState: "stm")
        {
            CreatedAt = now.AddHours(-10), LastAccessedAt = now.AddHours(-10), ActivationEnergy = 5f
        });
        // Normal namespace with STM, LTM, and already-archived entries.
        index.Upsert(new CognitiveEntry("stm1", [1f, 0f], "work", "stm entry", lifecycleState: "stm")
        {
            CreatedAt = now.AddHours(-10), LastAccessedAt = now.AddHours(-10), ActivationEnergy = 5f
        });
        index.Upsert(new CognitiveEntry("ltm1", [1f, 0f], "work", "ltm entry", lifecycleState: "ltm")
        {
            CreatedAt = now, LastAccessedAt = now, AccessCount = 2, ActivationEnergy = 2.0f
        });
        index.Upsert(new CognitiveEntry("arc1", [1f, 0f], "work", "archived entry", lifecycleState: "archived")
        {
            CreatedAt = now.AddDays(-30), LastAccessedAt = now.AddDays(-30), ActivationEnergy = -10f
        });

        provider.ResetCounts();

        // Cycle 1: stm1 transitions stm->ltm (energy computed under stmMultiplier=3.0);
        // ltm1 (delta 0) & arc1 (already archived) must NOT write.
        var res1 = lifecycle.RunDecayCycle("*", tenantId: "");
        Assert.Equal(1, res1.TotalNamespaces);
        Assert.Equal(3, res1.ProcessedCount);
        Assert.Equal(1, res1.StmToLtm);
        Assert.Equal("stm", index.Get("sys1", "_system_meta", tenantId: "")!.LifecycleState);
        Assert.Equal(1, provider.EntryWrites + provider.FullSaves); // only stm1 changed

        // Cycle 2: stm1 is now LTM, so its energy settles under ltmMultiplier=1.0 (1 write);
        // ltm1 and arc1 still do NOT write.
        provider.ResetCounts();
        var res2 = lifecycle.RunDecayCycle("*", tenantId: "");
        Assert.Equal(3, res2.ProcessedCount);
        Assert.Equal(1, provider.EntryWrites + provider.FullSaves);

        // Cycle 3 immediately after (0 time elapsed): nothing changed meaningfully -> ZERO writes!
        provider.ResetCounts();
        var res3 = lifecycle.RunDecayCycle("*", tenantId: "");
        Assert.Equal(3, res3.ProcessedCount);
        Assert.Equal(0, provider.EntryWrites + provider.FullSaves);
    }

    [Fact]
    public void LifecycleEngine_ScheduleSaveConfigs_ClonesSnapshotAndRunsOutsideConfigLock()
    {
        using var inner = new PersistenceManager(SubDir("decay_config_clone"), debounceMs: 50);
        Dictionary<string, DecayConfig>? capturedSnapshot = null;
        bool concurrentReadSucceeded = false;

        LifecycleEngine? lifecycle = null;
        using var provider = new SyncConfigSaveProvider(inner, dataProvider =>
        {
            // Verify _configLock is NOT held when ScheduleSaveDecayConfigs is called by reading
            // from another thread synchronously inside the save callback (would deadlock if held).
            DecayConfig? readResult = null;
            using var done = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                readResult = lifecycle!.GetDecayConfig("work", tenantId: "t1");
                done.Set();
            });
            thread.Start();
            concurrentReadSucceeded = done.Wait(TimeSpan.FromSeconds(5)) && readResult is not null;
            capturedSnapshot = dataProvider();
        });

        using var index = new CognitiveIndex(provider);
        lifecycle = new LifecycleEngine(index, provider);

        var config = lifecycle.SetDecayConfig("work", decayRate: 0.25f, reinforcementWeight: 1.5f,
            stmThreshold: null, archiveThreshold: null, useSpectralDecay: null,
            subdiffusiveExponent: null, tenantId: "t1");

        Assert.True(concurrentReadSucceeded, "ScheduleSaveDecayConfigs ran while holding _configLock.");
        Assert.NotNull(capturedSnapshot);

        // Mutating the live DecayConfig after save must NOT mutate the cloned snapshot instance.
        config.DecayRate = 99.0f;
        var pk = NamespaceStore.PartitionKey("t1", "work");
        Assert.NotSame(config, capturedSnapshot![pk]);
        Assert.Equal(0.25f, capturedSnapshot[pk].DecayRate);
        Assert.Equal(1.5f, capturedSnapshot[pk].ReinforcementWeight);
    }

    [Fact]
    public async Task DecayAndConsolidationBackgroundServices_CollectMultiTenantErrors_AndRespectCancellation()
    {
        // Part A: DecayBackgroundService preserves partial errors from ALL failing tenants.
        var clock1 = new SimulatedTimeProvider();
        using (var inner = new PersistenceManager(SubDir("multi_tenant_decay_err"), debounceMs: 50))
        using (var failing = new FailingEnumStorageProvider(inner, failDecayConfigs: true))
        using (var index = new CognitiveIndex(failing) { Clock = clock1 })
        {
            index.Upsert(new CognitiveEntry("e1", [1f, 0f], "ns", "a", tenantId: "tenant-alpha"));
            index.Upsert(new CognitiveEntry("e2", [1f, 0f], "ns", "b", tenantId: "tenant-beta"));

            var lifecycle = new LifecycleEngine(index, failing);
            var tracker = new BackgroundWorkerStatusTracker();
            using var decaySvc = new DecayBackgroundService(lifecycle, NullLogger<DecayBackgroundService>.Instance, tracker);
            try
            {
                await decaySvc.StartAsync(CancellationToken.None);
                await clock1.RunNextCycleAsync();
            }
            finally
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await decaySvc.StopAsync(stopCts.Token);
            }

            var status = tracker.GetSnapshot().Decay;
            Assert.Equal(1, status.CyclesCompleted);
            Assert.NotNull(status.LastErrorMessage);
            Assert.Contains("tenant-alpha", status.LastErrorMessage);
            Assert.Contains("tenant-beta", status.LastErrorMessage);
        }

        // Part B: ConsolidationBackgroundService preserves partial errors from ALL failing tenants.
        var clock2 = new SimulatedTimeProvider();
        using (var inner = new PersistenceManager(SubDir("multi_tenant_cons_err"), debounceMs: 50))
        using (var failing = new FailingEnumStorageProvider(inner, failDecayConfigs: true))
        using (var index = new CognitiveIndex(failing) { Clock = clock2 })
        {
            index.Upsert(new CognitiveEntry("e1", [1f, 0f], "ns", "a", tenantId: "tenant-alpha"));
            index.Upsert(new CognitiveEntry("e2", [1f, 0f], "ns", "b", tenantId: "tenant-beta"));

            var graph = new KnowledgeGraph(failing, index);
            var diffusion = new MemoryDiffusionKernel(index, graph);

            var lifecycle = new LifecycleEngine(index, failing, diffusion);
            var tracker = new BackgroundWorkerStatusTracker();
            using var consSvc = new ConsolidationBackgroundService(lifecycle, statusTracker: tracker);
            try
            {
                await consSvc.StartAsync(CancellationToken.None);
                await clock2.RunNextCycleAsync();
            }
            finally
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await consSvc.StopAsync(stopCts.Token);
            }

            var status = tracker.GetSnapshot().Consolidation;
            Assert.Equal(1, status.CyclesCompleted);
            Assert.NotNull(status.LastErrorMessage);
            Assert.Contains("tenant-alpha", status.LastErrorMessage);
            Assert.Contains("tenant-beta", status.LastErrorMessage);
        }

        // Part C: Cooperative cancellation throws OperationCanceledException instead of recording false failures.
        using (var inner = new PersistenceManager(SubDir("lifecycle_cancel"), debounceMs: 50))
        using (var index = new CognitiveIndex(inner))
        {
            index.Upsert(new CognitiveEntry("e1", [1f, 0f], "ns", "a"));
            var graph = new KnowledgeGraph(inner, index);
            var diffusion = new MemoryDiffusionKernel(index, graph);
            var lifecycle = new LifecycleEngine(index, inner, diffusion);

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                lifecycle.RunDecayCycleInternal("*", "", canceled.Token));
            Assert.Throws<OperationCanceledException>(() =>
                lifecycle.RunConsolidationPassInternal("*", "", canceled.Token));
        }
    }

    [Fact]
    public async Task AccretionBackgroundService_SkipsSystemNamespaces_AndSkipsUnchangedPartitions()
    {
        var clock = new SimulatedTimeProvider();
        using var persistence = new PersistenceManager(SubDir("accretion_rev_skip"), debounceMs: 50);
        using var index = new CognitiveIndex(persistence) { Clock = clock };
        using var scanner = new AccretionScanner(index, persistence);
        var clusters = new ClusterManager(index, persistence);
        var embedding = new HashEmbeddingService(dimensions: 4);
        var tracker = new BackgroundWorkerStatusTracker();

        // System namespace (_internal) with 4 identical LTM vectors must NEVER be scanned.
        for (int i = 0; i < 4; i++)
            index.Upsert(new CognitiveEntry($"sys{i}", [1f, 0f, 0f, 0f], "_internal", $"sys {i}", lifecycleState: "ltm"));

        // Normal namespace ("work") with 4 identical LTM vectors.
        for (int i = 0; i < 4; i++)
            index.Upsert(new CognitiveEntry($"w{i}", [1f, 0f, 0f, 0f], "work", $"work {i}", lifecycleState: "ltm"));

        using var service = new AccretionBackgroundService(
            scanner, index, clusters, embedding, NullLogger<AccretionBackgroundService>.Instance, tracker);
        try
        {
            await service.StartAsync(CancellationToken.None);

            // Cycle 1: scans "work" (4 LTM entries), skips "_internal", creates 1 auto-summary.
            await clock.RunNextCycleAsync();
            Assert.Empty(scanner.GetPendingCollapses("_internal", tenantId: ""));
            Assert.Empty(clusters.ListClusters("_internal", tenantId: ""));
            Assert.Single(clusters.ListClusters("work", tenantId: ""));
            Assert.Equal(4, tracker.GetSnapshot().Accretion.TotalEntriesProcessed);

            // Cycles 2 & 3 with no changes: unchanged "work" partition is skipped (0 entries processed).
            await clock.RunNextCycleAsync();
            await clock.RunNextCycleAsync();
            Assert.Equal(4, tracker.GetSnapshot().Accretion.TotalEntriesProcessed);

            // Occupancy change (new entry) triggers a rescan on the next tick.
            index.Upsert(new CognitiveEntry("w4", [1f, 0f, 0f, 0f], "work", "work 4", lifecycleState: "ltm"));
            await clock.RunNextCycleAsync();
            Assert.Equal(9, tracker.GetSnapshot().Accretion.TotalEntriesProcessed);

            // Next idle tick skips again.
            await clock.RunNextCycleAsync();
            Assert.Equal(9, tracker.GetSnapshot().Accretion.TotalEntriesProcessed);

            // Lifecycle transition (STM -> LTM, no occupancy bump) also triggers a rescan.
            index.Upsert(new CognitiveEntry("w5", [1f, 0f, 0f, 0f], "work", "work 5", lifecycleState: "stm"));
            await clock.RunNextCycleAsync(); // scans with w5 still STM (5 LTM entries -> total 14)
            long afterStmInsert = tracker.GetSnapshot().Accretion.TotalEntriesProcessed;
            Assert.True(index.SetLifecycleState("w5", "ltm", "work", tenantId: ""));
            await clock.RunNextCycleAsync();
            Assert.True(tracker.GetSnapshot().Accretion.TotalEntriesProcessed > afterStmInsert);
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(stopCts.Token);
        }
    }

    [Fact]
    public void AccretionScanner_DbscanBoundedQueue_AndNamespaceRemovedCleansPendingAndDismissed()
    {
        // Part A: Dense DBSCAN with two clusters (200 and 50 points) + 10 isolated points.
        var entries = new List<CognitiveEntry>(260);
        for (int i = 0; i < 200; i++)
            entries.Add(new CognitiveEntry($"c1-{i}", [1f, 0.001f * (i % 5), 0f], "dbscan", $"c1 {i}", lifecycleState: "ltm"));
        for (int i = 0; i < 50; i++)
            entries.Add(new CognitiveEntry($"c2-{i}", [0f, 1f, 0.001f * (i % 5)], "dbscan", $"c2 {i}", lifecycleState: "ltm"));
        // Add 10 isolated noise points mutually separated by cosine distance > 0.3 >> epsilon (0.02).
        float[][] noiseVectors =
        [
            [-1f, 0f, 0f],
            [0f, -1f, 0f],
            [0f, 0f, 1f],
            [0f, 0f, -1f],
            [0.577f, -0.577f, 0.577f],
            [-0.577f, 0.577f, 0.577f],
            [-0.577f, -0.577f, 0.577f],
            [0.577f, -0.577f, -0.577f],
            [-0.577f, 0.577f, -0.577f],
            [-0.577f, -0.577f, -0.577f]
        ];
        for (int i = 0; i < noiseVectors.Length; i++)
            entries.Add(new CognitiveEntry($"n-{i}", noiseVectors[i], "dbscan", $"noise {i}", lifecycleState: "ltm"));

        // Pick noise points mutually > epsilon apart; the two dense clusters have cosine distance < 0.01.
        var detected = AccretionScanner.Dbscan(entries, epsilon: 0.02f, minPoints: 3);
        var clusterSizes = detected.Select(c => c.Count).OrderByDescending(c => c).ToList();
        Assert.Equal([200, 50], clusterSizes);

        // Part B: NamespaceRemoved cleans up both _pendingCollapses and _dismissedEntryIds for the deleted partition.
        using var persistence = new PersistenceManager(SubDir("accretion_cleanup"), debounceMs: 50);
        using var index = new CognitiveIndex(persistence);
        using var scanner = new AccretionScanner(index, persistence);
        var clusters = new ClusterManager(index, persistence);

        for (int i = 0; i < 4; i++)
        {
            index.Upsert(new CognitiveEntry($"e{i}", [1f, 0f, 0f], "ephemeral", $"ephem {i}", lifecycleState: "ltm"));
            index.Upsert(new CognitiveEntry($"d{i}", [0f, 1f, 0f], "ephemeral", $"dismiss {i}", lifecycleState: "ltm"));
            index.Upsert(new CognitiveEntry($"k{i}", [1f, 0f, 0f], "keep", $"keep {i}", lifecycleState: "ltm"));
        }

        var ephemScan = scanner.ScanNamespace("ephemeral", tenantId: "");
        Assert.Equal(2, ephemScan.ClustersDetected);
        Assert.Equal(1, scanner.ScanNamespace("keep", tenantId: "").ClustersDetected);

        // Dismiss one of the two collapses in "ephemeral", leaving one pending + 4 dismissed.
        var toDismiss = ephemScan.NewCollapses[0].CollapseId;
        Assert.DoesNotContain("Error:", scanner.DismissCollapse(toDismiss, tenantId: "", clusters));
        Assert.Equal(4, scanner.DismissedEntryCount);
        Assert.Equal(2, scanner.PendingCount); // 1 in ephemeral + 1 in keep

        // Delete "ephemeral" -> both its pending collapse AND its dismissed entries are reclaimed,
        // while "keep"'s pending collapse stays intact.
        index.DeleteAllInNamespace("ephemeral", tenantId: "");
        Assert.Equal(0, scanner.DismissedEntryCount);
        Assert.Empty(scanner.GetPendingCollapses("ephemeral", tenantId: ""));
        Assert.Single(scanner.GetPendingCollapses("keep", tenantId: ""));
        Assert.Equal(1, scanner.PendingCount);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 5. GovernedLearningCoordinator premature Commit/Allow audit fix
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GovernedLearningCoordinator_DeniedAndQuarantinedProposals_NeverEmitCommitAuditRecord()
    {
        // Case 1: Deterministic verifier fails -> Denied, ZERO Commit audit records.
        {
            var (coordinator, audit, store, request) = CreateGovernedHarness(
                verifierStatus: VerificationStatus.Failed,
                constitutionRule: new AuditEnvelopeConstitutionRule());

            var result = await coordinator.ExecuteAsync(request);

            Assert.Equal(PromotionOutcome.Denied, result.Promotion.Outcome);
            Assert.Null(result.Commit);
            var records = await audit.ReadAllAsync();
            Assert.DoesNotContain(records, r => r.Phase == ConstitutionPhase.Commit);
            Assert.Contains(records, r => r.Phase == ConstitutionPhase.Precondition);
            Assert.Null((await store.ReadAsync("tenant-a", "project", "claim-1")).ActiveVersion);
        }

        // Case 2: Permission broadening -> Denied, ZERO Commit audit records.
        {
            var (coordinator, audit, _, request) = CreateGovernedHarness(
                verifierStatus: VerificationStatus.Passed,
                constitutionRule: new AuditEnvelopeConstitutionRule(),
                requestedCapabilities: [KnowledgeCapability.Read, KnowledgeCapability.Train]);

            var result = await coordinator.ExecuteAsync(request);

            Assert.Equal(PromotionOutcome.Denied, result.Promotion.Outcome);
            Assert.Null(result.Commit);
            Assert.DoesNotContain(await audit.ReadAllAsync(), r => r.Phase == ConstitutionPhase.Commit);
        }

        // Case 3: Constitution quarantines the operation -> Quarantined, ZERO Commit audit records.
        {
            var (coordinator, audit, _, request) = CreateGovernedHarness(
                verifierStatus: VerificationStatus.Passed,
                evaluator: new FixedOutcomeEvaluator(ConstitutionOutcome.Quarantine));

            var result = await coordinator.ExecuteAsync(request);

            Assert.Equal(PromotionOutcome.Quarantined, result.Promotion.Outcome);
            Assert.Null(result.Commit);
            var records = await audit.ReadAllAsync();
            Assert.DoesNotContain(records, r => r.Phase == ConstitutionPhase.Commit);
            Assert.Contains(records, r => r.Phase == ConstitutionPhase.Precondition && r.Outcome == ConstitutionOutcome.Quarantine);
        }

        // Case 4: Valid proposal passes Precondition, then authorizes Commit/Allow and commits atomically.
        {
            var (coordinator, audit, store, request) = CreateGovernedHarness(
                verifierStatus: VerificationStatus.Passed,
                constitutionRule: new AuditEnvelopeConstitutionRule());

            var result = await coordinator.ExecuteAsync(request);

            Assert.Equal(PromotionOutcome.Promoted, result.Promotion.Outcome);
            Assert.NotNull(result.Commit);
            Assert.Equal(GovernedCommitOutcome.Committed, result.Commit!.Outcome);

            var records = await audit.ReadAllAsync();
            var commitAudit = Assert.Single(records, r => r.Phase == ConstitutionPhase.Commit);
            Assert.Equal(ConstitutionOutcome.Allow, commitAudit.Outcome);

            var snapshot = await store.ReadAsync("tenant-a", "project", "claim-1");
            Assert.NotNull(snapshot.ActiveVersion);
            Assert.Single(snapshot.Audit);
            Assert.Equal(commitAudit.EventId, snapshot.Audit[0].EventId);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Test Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T)field!.GetValue(target)!;
    }

    private static (
        GovernedLearningCoordinator Coordinator,
        InMemoryConstitutionAuditStore Audit,
        InMemoryGovernedKnowledgeStore Store,
        GovernedLearningRequest Request)
        CreateGovernedHarness(
            VerificationStatus verifierStatus,
            IConstitutionRule? constitutionRule = null,
            IConstitutionEvaluator? evaluator = null,
            HashSet<KnowledgeCapability>? requestedCapabilities = null)
    {
        var now = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var provider = new InMemoryConstitutionProvider();
        var constitutionHash = provider.Current.EffectiveVersionHash;
        var audit = new InMemoryConstitutionAuditStore();
        var kernel = new ConstitutionKernel(
            provider,
            evaluator ?? new DeterministicConstitutionEvaluator([constitutionRule ?? new AuditEnvelopeConstitutionRule()]),
            audit);
        var store = new InMemoryGovernedKnowledgeStore();

        var sourceRef = new ArtifactRef("tenant-a", "project", ArtifactKind.Evidence, "src-1", "v1");
        var sourceOp = new OperationArtifactReference("tenant-a", "project", "Evidence", "src-1", "v1");
        var permissions = new PermissionEnvelope([new CapabilityGrant(ArtifactCapability.Read, ["alice"])]);
        var inherited = new HashSet<KnowledgeCapability> { KnowledgeCapability.Read };
        var requested = requestedCapabilities ?? new HashSet<KnowledgeCapability> { KnowledgeCapability.Read };

        var generator = new StaticDraftGenerator(new KnowledgeProposalDraft(
            "Verified claim",
            KnowledgeHypothesisType.Generalization,
            [sourceOp],
            Array.Empty<OperationArtifactReference>(),
            0.2,
            Array.Empty<string>(),
            new KnowledgeValidityInterval(now, null),
            requested));

        var coordinator = new GovernedLearningCoordinator(
            new TeacherRuntime(generator),
            new VerifierPlanner(),
            new KnowledgePromotionEvaluator(),
            kernel,
            store);

        var teacherRequest = new TeacherRequest(
            "prop-1",
            "tenant-a",
            "Promote claim-1",
            [sourceOp],
            new GenerationIdentity("test-teacher", "1", "prompt", "1", "view-1"),
            constitutionHash,
            inherited,
            now);

        var verifier = new FixedVerifier(verifierStatus);
        var evidenceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("src-1"))).ToLowerInvariant();
        var versions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KnowledgeProposal.EvidenceKey(sourceOp)] = "v1"
        };

        var request = new GovernedLearningRequest(
            teacherRequest,
            "alice",
            new LearningExecutionBudget(1, 2, DateTimeOffset.UtcNow.AddMinutes(5)),
            [verifier],
            proposal =>
            {
                var target = new ArtifactRef("tenant-a", "project", ArtifactKind.Knowledge, "claim-1", "v1");
                var comp = new CalibratedComponent(0.8m, "test", "1", now);
                var profile = new EpistemicProfile(comp, comp, comp, comp, comp, comp);
                var evidence = new EvidenceReference(sourceRef, evidenceHash, now, "src-1", permissions);
                var version = KnowledgeCanonicalizer.PublishVersion(new KnowledgeVersionDefinition(
                    target, proposal.Claim, KnowledgeMaturity.Supported, KnowledgeStatus.Active,
                    new BitemporalValidity(now, now, now, verifiedAt: now),
                    profile, [evidence], Array.Empty<EvidenceReference>(), permissions, proposal.ConstitutionVersionHash));
                return new GovernedLearningMaterialization(
                    version,
                    receipt => ProvenanceCanonicalizer.Publish(
                        "prov-1", target, [sourceRef], ProvenanceRelation.DerivedFrom,
                        "alice", "test", "1",
                        [new ArtifactRef("tenant-a", "project", ArtifactKind.Verification, "det", "1")],
                        receipt.Decision.ConstitutionVersionHashes[^1],
                        receipt.AuditRecord.EventId, permissions, now),
                    new Dictionary<ArtifactRef, PermissionEnvelope> { [sourceRef] = permissions },
                    ExpectedActiveVersionHash: null);
            },
            () => new CommitAuthorityState(provider.Current.EffectiveVersionHash, versions));

        return (coordinator, audit, store, request);
    }

    private sealed class RecordingEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 4;
        public List<string> RecordedTexts { get; } = new();

        public float[] Embed(string text)
        {
            RecordedTexts.Add(text);
            return [1f, 0f, 0f, 0f];
        }
    }

    private sealed class BlockingEmbeddingService(ManualResetEventSlim entered, ManualResetEventSlim release)
        : IEmbeddingService
    {
        public int Dimensions => 6;

        public float[] Embed(string text)
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return [1f, 0f, 0f, 0f, 0f, 0f];
        }
    }

    private sealed class StaticDraftGenerator(KnowledgeProposalDraft draft) : IKnowledgeProposalGenerator
    {
        public ValueTask<KnowledgeProposalDraft> GenerateAsync(TeacherRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(draft);
    }

    private sealed class FixedVerifier(VerificationStatus status) : ILearningVerifier
    {
        public VerifierIdentity Identity { get; } = new("fixed-det", "1", VerifierKind.Deterministic);

        public ValueTask<(VerificationStatus Status, IReadOnlyList<VerificationFinding> Findings)> VerifyAsync(
            KnowledgeProposal proposal, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<VerificationFinding> findings = status == VerificationStatus.Passed
                ? Array.Empty<VerificationFinding>()
                : [new VerificationFinding("fixed-failure", "Deterministic check failed.")];
            return ValueTask.FromResult((status, findings));
        }
    }

    private sealed class FixedOutcomeEvaluator(ConstitutionOutcome outcome) : IConstitutionEvaluator
    {
        public ValueTask<ConstitutionDecision> EvaluateAsync(
            OperationEnvelope operation,
            ConstitutionBundle constitution,
            ConstitutionPhase phase,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ConstitutionFinding> findings = outcome == ConstitutionOutcome.Allow
                ? Array.Empty<ConstitutionFinding>()
                : [new ConstitutionFinding("test.fixed-outcome", "fixed-outcome", outcome, $"Forced {outcome}.")];
            return ValueTask.FromResult(new ConstitutionDecision(
                operation.OperationId, phase, outcome, findings, constitution.VersionHashes));
        }
    }

    private class CountingStorageProvider(IStorageProvider inner) : IStorageProvider
    {
        public int HnswSaves;
        public int EntryWrites;
        public int FullSaves;

        public void ResetCounts()
        {
            Interlocked.Exchange(ref HnswSaves, 0);
            Interlocked.Exchange(ref EntryWrites, 0);
            Interlocked.Exchange(ref FullSaves, 0);
        }

        public string StoreIdentity => inner.StoreIdentity;
        public bool SupportsIncrementalWrites => inner.SupportsIncrementalWrites;
        public virtual NamespaceData LoadNamespace(string ns) => inner.LoadNamespace(ns);
        public virtual IReadOnlyList<string> GetPersistedNamespaces() => inner.GetPersistedNamespaces();
        public void ScheduleSave(string ns, Func<NamespaceData> dataProvider)
        {
            Interlocked.Increment(ref FullSaves);
            inner.ScheduleSave(ns, dataProvider);
        }
        public void SaveNamespaceSync(string ns, NamespaceData data) => inner.SaveNamespaceSync(ns, data);
        public void ScheduleUpsertEntry(string ns, CognitiveEntry entry)
        {
            Interlocked.Increment(ref EntryWrites);
            inner.ScheduleUpsertEntry(ns, entry);
        }
        public void ScheduleDeleteEntry(string ns, string id) => inner.ScheduleDeleteEntry(ns, id);
        public List<GraphEdge> LoadGlobalEdges() => inner.LoadGlobalEdges();
        public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider) => inner.ScheduleSaveGlobalEdges(dataProvider);
        public List<SemanticCluster> LoadClusters() => inner.LoadClusters();
        public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider) => inner.ScheduleSaveClusters(dataProvider);
        public List<CollapseRecord> LoadCollapseHistory() => inner.LoadCollapseHistory();
        public bool UpsertCollapseRecordSync(CollapseRecord record) => inner.UpsertCollapseRecordSync(record);
        public bool DeleteCollapseRecordSync(string id) => inner.DeleteCollapseRecordSync(id);
        public CollapseRecordCas UpsertCollapseRecordSync(CollapseRecord record, long? generation) =>
            inner.UpsertCollapseRecordSync(record, generation);
        public CollapseRecordCas DeleteCollapseRecordSync(string id, long generation) =>
            inner.DeleteCollapseRecordSync(id, generation);
        public bool TryReadCollapseRecord(string id, out CollapseRecord? record) => inner.TryReadCollapseRecord(id, out record);
        public bool TryReadCollapseHistory(out List<CollapseRecord> records) => inner.TryReadCollapseHistory(out records);
        public virtual Dictionary<string, DecayConfig> LoadDecayConfigs() => inner.LoadDecayConfigs();
        public virtual void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider) =>
            inner.ScheduleSaveDecayConfigs(dataProvider);
        public HnswSnapshot? LoadHnswSnapshot(string ns) => inner.LoadHnswSnapshot(ns);
        public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot)
        {
            Interlocked.Increment(ref HnswSaves);
            inner.SaveHnswSnapshotSync(ns, snapshot);
        }
        public void DeleteHnswSnapshot(string ns) => inner.DeleteHnswSnapshot(ns);
        public Task DeleteNamespaceAsync(string ns) => inner.DeleteNamespaceAsync(ns);
        public void Flush() => inner.Flush();
        public bool TryFlush() => inner.TryFlush();
        public void Dispose() => inner.Dispose();
    }

    private sealed class SyncConfigSaveProvider(
        IStorageProvider inner,
        Action<Func<Dictionary<string, DecayConfig>>> onScheduleSave) : CountingStorageProvider(inner)
    {
        public override void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider)
        {
            onScheduleSave(dataProvider);
            base.ScheduleSaveDecayConfigs(dataProvider);
        }
    }

    private sealed class FailingEnumStorageProvider(IStorageProvider inner, bool failDecayConfigs)
        : CountingStorageProvider(inner)
    {
        public override Dictionary<string, DecayConfig> LoadDecayConfigs()
        {
            if (failDecayConfigs)
                throw new IOException("Simulated decay config load failure.");
            return base.LoadDecayConfigs();
        }
    }
}
