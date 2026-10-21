using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;
using Microsoft.Data.Sqlite;

namespace McpEngramMemory.Tests;

public sealed class MaintenanceSnapshotConcurrencyTests
{
    private static Task Run(Action action) => Task.Factory.StartNew(action,
        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BlockedSnapshot_AllowsScopedRecallWriteAndDecay_ThenPersistsWithoutCanaryMutation(bool sqlite, bool failCapture)
    {
        const string ns = "expert_helix", tenant = "tenant-a";
        var path = Path.Combine(Path.GetTempPath(), $"maintenance_overlap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        var timers = new ControlledTimeProvider();
        try
        {
            using IStorageProvider store = sqlite
                ? new SqliteStorageProvider(Path.Combine(path, "memory.db")) { TimerProvider = timers }
                : new PersistenceManager(path) { TimerProvider = timers };
            var clock = new SimulatedTimeProvider();
            using var index = new CognitiveIndex(store) { Clock = clock };
            var graph = new KnowledgeGraph(store, index);
            var embedding = new FixedEmbedding();
            var registry = new NamespaceRegistry(index, embedding);
            registry.EnsureOwnership(ns, "owner", tenant);
            registry.EnsureOwnership("private", "other-owner", tenant);
            var access = new NamespaceAccess(registry, new PrincipalContext(tenant, "owner"));
            var lifecycle = new LifecycleEngine(index, store);
            var tools = new CompositeTools(index, embedding, graph, lifecycle,
                new ExpertDispatcher(index, embedding), new MetricsCollector(),
                new SpectralRetrievalReranker(new MemoryDiffusionKernel(index, graph)), access);
            index.Upsert(new CognitiveEntry("answer", [1f, 0f, 0f], ns,
                "Helix production deployment owner is Ada.", lifecycleState: "ltm", tenantId: tenant)
                { LastAccessedAt = clock.GetUtcNow(), AccessCount = 100 });
            index.Upsert(new CognitiveEntry("cold", [0f, 1f, 0f], ns,
                "Library entry about birds.", lifecycleState: "ltm", tenantId: tenant)
                { LastAccessedAt = clock.GetUtcNow().AddDays(-7), AccessCount = 1 });
            foreach (var (owner, scope, marker) in new[]
            {
                (tenant, "private", "PRIVATE_CANARY"),
                ("tenant-b", ns, "FOREIGN_CANARY"),
                ("", ns, "LEGACY_CANARY")
            })
                index.Upsert(new CognitiveEntry("answer", [1f, 0f, 0f], scope,
                    $"Helix production deployment owner is {marker}.", lifecycleState: "archived", tenantId: owner));
            Assert.True(store.TryFlush());

            using var captured = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int calls = 0;
            store.ScheduleSaveGlobalEdges(() =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    captured.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Work did not release snapshot");
                    if (failCapture) throw new IOException("Injected snapshot failure");
                }
                return graph.GetStoredEdges(tenant).ToList();
            });
            var callback = Run(timers.Latest.DeliverQueuedCallback);
            Task? foreground = null;
            try
            {
                Assert.True(captured.Wait(TimeSpan.FromSeconds(5)));
                foreground = Run(() =>
                {
                    // Completion is required while the persistence capture is blocked,
                    // so this proves overlap instead of hoping the scheduler creates it.
                    foreach (var requested in new string?[] { ns, null })
                    {
                        var result = Assert.IsType<RecallResult>(tools.Recall("Who owns Helix production deployment?", requested));
                        Assert.Equal("Helix production deployment owner is Ada.", Assert.Single(result.Results).Text);
                    }
                    Assert.Empty(Assert.IsType<RecallResult>(tools.Recall("Who owns Helix production deployment?", "private")).Results);
                    index.Upsert(new CognitiveEntry("writer", [0f, 1f, 0f], ns,
                        "Committed writer version 1.", lifecycleState: "ltm", tenantId: tenant));
                    var decay = lifecycle.RunDecayCycle(ns, tenant);
                    Assert.Empty(decay.FailedNamespaces ?? []);
                    Assert.Contains("cold", decay.LtmToArchivedIds);
                });
                await foreground.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(callback.IsCompleted);
            }
            finally
            {
                release.Set();
                await Task.WhenAll(foreground ?? Task.CompletedTask, callback).WaitAsync(TimeSpan.FromSeconds(20));
            }
            Assert.True(store.TryFlush());
            index.Dispose();
            using var reopened = new CognitiveIndex(store);
            Assert.Equal("Committed writer version 1.", reopened.Get("writer", ns, tenant)!.Text);
            Assert.Equal("archived", reopened.Get("cold", ns, tenant)!.LifecycleState);
            foreach (var (owner, scope, marker) in new[]
            {
                (tenant, "private", "PRIVATE_CANARY"), ("tenant-b", ns, "FOREIGN_CANARY"), ("", ns, "LEGACY_CANARY")
            })
            {
                var canary = reopened.Get("answer", scope, owner)!;
                Assert.Contains(marker, canary.Text);
                Assert.Equal("archived", canary.LifecycleState);
                Assert.Equal(1, canary.AccessCount);
            }
        }
        finally
        {
            using var connection = new SqliteConnection($"Data Source={Path.Combine(path, "memory.db")}");
            SqliteConnection.ClearPool(connection);
            Directory.Delete(path, true);
        }
    }

    private sealed class FixedEmbedding : IEmbeddingService
    {
        public int Dimensions => 3;
        public float[] Embed(string text) => [1f, 0f, 0f];
    }
}
