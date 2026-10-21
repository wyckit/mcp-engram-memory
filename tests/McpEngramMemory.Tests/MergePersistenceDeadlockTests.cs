using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;
using Microsoft.Data.Sqlite;

namespace McpEngramMemory.Tests;

public sealed class MergePersistenceDeadlockTests
{
    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 3;
        public float[] Embed(string text) => [1f, 0f, 0f];
    }

    private static Task<T> RunDedicated<T>(Func<T> action) => Task.Factory.StartNew(
        action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Merge_WhileEdgeSnapshotWaitsForGraph_CompletesAndPersistsOnlyItsTenant(bool sqlite)
    {
        const string ns = "merge-deadlock", tenant = "tenant-a";
        var path = Path.Combine(Path.GetTempPath(), $"merge_deadlock_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        var clock = new ControlledTimeProvider();
        try
        {
            using IStorageProvider store = sqlite
                ? new SqliteStorageProvider(Path.Combine(path, "memory.db")) { TimerProvider = clock }
                : new PersistenceManager(path) { TimerProvider = clock };
            using var index = new CognitiveIndex(store);
            var graph = new KnowledgeGraph(store, index);
            var clusters = new ClusterManager(index, store);
            var embedding = new StubEmbedding();
            var registry = new NamespaceRegistry(index, embedding);
            registry.ClaimOwnershipOnWrite(ns, "alice", tenantId: tenant);
            var tools = new IntelligenceTools(index, graph, embedding,
                new AccretionScanner(index, store), clusters, new LifecycleEngine(index),
                new NamespaceAccess(registry, new PrincipalContext(tenant, "alice")));
            foreach (var owner in new[] { "", tenant })
            {
                foreach (var id in new[] { "keep", "archive", "far" })
                    index.Upsert(new CognitiveEntry(id, [1f, 0f, 0f], ns,
                        text: owner + ":" + id, tenantId: owner));
                Assert.DoesNotContain("Error", graph.AddEdge(new GraphEdge(
                    "archive", "far", "depends_on", tenantId: owner)));
                Assert.DoesNotContain("Error", clusters.CreateCluster(
                    "cluster", ns, ["archive", "far"], label: null, tenantId: owner));
            }
            Assert.True(store.TryFlush());
            using var mergeHasGraph = new ManualResetEventSlim();
            using var snapshotStarted = new ManualResetEventSlim();
            Task<List<GraphEdge>>? capture = null;
            int snapshotTimeouts = 0;
            graph.OnValidatedUnderFence = () =>
            {
                mergeHasGraph.Set();
                if (!snapshotStarted.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Snapshot callback did not start");
            };
            store.ScheduleSaveGlobalEdges(() =>
            {
                // Use the graph's real read lock. The bounded proxy lets the old lock
                // inversion unwind and fail an assertion instead of wedging the test host.
                capture = RunDedicated(() => graph.GetStoredEdges("")
                    .Concat(graph.GetStoredEdges(tenant)).ToList());
                snapshotStarted.Set();
                try { return capture.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
                catch (TimeoutException) { Interlocked.Increment(ref snapshotTimeouts); throw; }
            });
            var timer = clock.Latest;
            var merge = RunDedicated(() => tools.MergeMemories("keep", "archive", ns));
            Assert.True(mergeHasGraph.Wait(TimeSpan.FromSeconds(5)));
            var callback = RunDedicated(() => { timer.DeliverQueuedCallback(); return true; });
            await Task.WhenAll(merge, callback).WaitAsync(TimeSpan.FromSeconds(15));
            graph.OnValidatedUnderFence = null;
            Assert.NotNull(capture);
            await capture.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, snapshotTimeouts);
            Assert.StartsWith("Merged", await merge);
            Assert.True(store.TryFlush());
            // Reload persisted state, not just the in-memory merge result. Colliding IDs
            // in the legacy tenant must retain their original lifecycle and topology.
            using var reloaded = new CognitiveIndex(store);
            var durableGraph = new KnowledgeGraph(store, reloaded);
            var durableClusters = new ClusterManager(reloaded, store);
            Assert.Equal("archived", reloaded.Get("archive", ns, tenantId: tenant)!.LifecycleState);
            Assert.Equal("stm", reloaded.Get("archive", ns, tenantId: "")!.LifecycleState);
            Assert.Equal(":keep", reloaded.Get("keep", ns, tenantId: "")!.Text);
            Assert.Contains(durableGraph.GetStoredEdges(tenant), e => e.SourceId == "keep" && e.TargetId == "far");
            Assert.DoesNotContain(durableGraph.GetStoredEdges(tenant), e => e.SourceId == "archive");
            Assert.Contains(durableGraph.GetStoredEdges(""), e => e.SourceId == "archive" && e.TargetId == "far");
            Assert.Contains("cluster", durableClusters.GetClustersForEntry("keep", tenantId: tenant));
            Assert.Contains("cluster", durableClusters.GetClustersForEntry("archive", tenantId: ""));
            Assert.DoesNotContain("cluster", durableClusters.GetClustersForEntry("keep", tenantId: ""));
        }
        finally
        {
            using var connection = new SqliteConnection($"Data Source={Path.Combine(path, "memory.db")}");
            SqliteConnection.ClearPool(connection);
            Directory.Delete(path, recursive: true);
        }
    }
}
