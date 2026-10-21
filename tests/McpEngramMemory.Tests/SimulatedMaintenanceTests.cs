using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpEngramMemory.Tests;

public sealed class SimulatedMaintenanceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"simulated_maintenance_{Guid.NewGuid():N}");
    private readonly SimulatedTimeProvider _clock = new();
    private IStorageProvider? _store;
    private CognitiveIndex? _index;
    private static readonly string[] Tenants = ["tenant-a", "tenant-b", ""];
    private const string Ns = "same-project";

    private CognitiveIndex Create(bool sqlite)
    {
        Directory.CreateDirectory(_path);
        _store = sqlite ? new SqliteStorageProvider(Path.Combine(_path, "memory.db")) : new PersistenceManager(_path);
        return _index = new CognitiveIndex(_store) { Clock = _clock };
    }

    private void Seed(CognitiveIndex index, string tenant, string id, string state, float energy = 0)
        => index.Upsert(new CognitiveEntry(id, [1f, 0f, 0f], Ns, $"{tenant}:{id}", lifecycleState: state, tenantId: tenant)
        {
            CreatedAt = _clock.GetUtcNow(), LastAccessedAt = _clock.GetUtcNow(),
            AccessCount = 1, ActivationEnergy = energy
        });

    private static async Task Stop(BackgroundService service)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StopAsync(deadline.Token);
    }

    [Theory]
    [InlineData("decay")]
    [InlineData("consolidation")]
    [InlineData("auto-link")]
    [InlineData("accretion")]
    public async Task CancellationBeforeFirstDeadline_RemovesTimerWithoutRunningMaintenance(string worker)
    {
        var index = Create(false);
        var graph = new KnowledgeGraph(_store!, index);
        var lifecycle = new LifecycleEngine(index, _store);
        var tracker = new BackgroundWorkerStatusTracker();
        using BackgroundService service = worker switch
        {
            "decay" => new DecayBackgroundService(lifecycle, NullLogger<DecayBackgroundService>.Instance, tracker),
            "consolidation" => new ConsolidationBackgroundService(lifecycle, statusTracker: tracker),
            "auto-link" => new AutoLinkBackgroundService(new AutoLinkScanner(index, graph, new DuplicateDetector()), index, lifecycle, statusTracker: tracker),
            _ => new AccretionBackgroundService(new AccretionScanner(index), index, new ClusterManager(index, _store!),
                new FixedEmbedding(), NullLogger<AccretionBackgroundService>.Instance, tracker)
        };
        try
        {
            await service.StartAsync(CancellationToken.None);
            await _clock.WaitForScheduledAsync();
            var status = tracker.GetSnapshot();
            Assert.All(new[] { status.Decay, status.Consolidation, status.AutoLink, status.Accretion },
                s => Assert.Equal(0, s.CyclesCompleted));
        }
        finally { await Stop(service); }
        Assert.Equal(0, _clock.PendingCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenDaysOfDecay_UsesSimulatedAgeAndPersistsTenantSpecificTransitions(bool sqlite)
    {
        var index = Create(sqlite);
        foreach (var tenant in Tenants)
        {
            Seed(index, tenant, "same-id", "ltm");
            Seed(index, tenant, "refresh", "ltm");
        }
        var lifecycle = new LifecycleEngine(index, _store);
        // Only tenant-a uses default aging. Identical IDs in the other partitions
        // must honor their own zero-decay configuration throughout all 672 cycles.
        foreach (var tenant in Tenants.Skip(1))
            lifecycle.SetDecayConfig(Ns, 0, 1, null, null, false, null, tenant);
        var tracker = new BackgroundWorkerStatusTracker();
        using var service = new DecayBackgroundService(lifecycle, NullLogger<DecayBackgroundService>.Instance, tracker);
        var start = _clock.GetUtcNow();
        try
        {
            await service.StartAsync(CancellationToken.None);
            for (int cycle = 1; cycle <= 7 * 24 * 4; cycle++)
            {
                var now = await _clock.RunNextCycleAsync();
                Assert.Equal(start.AddMinutes(cycle * 15), now);
                var status = tracker.GetSnapshot().Decay;
                Assert.Equal(cycle, status.CyclesCompleted);
                Assert.Equal(now.UtcDateTime, status.LastRunUtc);
                Assert.Null(status.LastErrorMessage);
                if (cycle % 96 == 0)
                {
                    index.RecordAccess("refresh", Ns, "tenant-a");
                    Assert.Equal(now, index.Get("refresh", Ns, "tenant-a")!.LastAccessedAt);
                }
                Assert.Equal("ltm", index.Get("refresh", Ns, "tenant-a")!.LifecycleState);
                if (cycle == 240) Assert.Equal("ltm", index.Get("same-id", Ns, "tenant-a")!.LifecycleState);
                if (cycle == 241) Assert.Equal("archived", index.Get("same-id", Ns, "tenant-a")!.LifecycleState);
            }
        }
        finally { await Stop(service); }
        Assert.Equal(0, _clock.PendingCount);
        Assert.Equal(4032, tracker.GetSnapshot().Decay.TotalEntriesProcessed);
        Assert.True(_store!.TryFlush());
        index.Dispose();
        _index = new CognitiveIndex(_store);
        foreach (var tenant in Tenants)
        {
            var entry = _index.Get("same-id", Ns, tenant)!;
            Assert.Equal(tenant + ":same-id", entry.Text);
            Assert.Equal(tenant == "tenant-a" ? "archived" : "ltm", entry.LifecycleState);
            Assert.Equal(1, entry.AccessCount);
            Assert.Equal(start, entry.LastAccessedAt);
            var refreshed = _index.Get("refresh", Ns, tenant)!;
            Assert.Equal("ltm", refreshed.LifecycleState);
            Assert.Equal(tenant == "tenant-a" ? 8 : 1, refreshed.AccessCount);
            Assert.Equal(tenant == "tenant-a" ? start.AddDays(7) : start, refreshed.LastAccessedAt);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FourteenDailyConsolidations_ChangeRealClustersWithoutCrossingTenantOptOuts(bool sqlite)
    {
        var index = Create(sqlite);
        var graph = new KnowledgeGraph(_store!, index);
        var lifecycle = new LifecycleEngine(index, _store, new MemoryDiffusionKernel(index, graph));
        foreach (var tenant in Tenants)
        {
            for (int i = 0; i < 32; i++) Seed(index, tenant, $"member-{i}", "stm", 5);
            for (int i = 0; i < 32; i++)
                for (int j = i + 1; j < 32; j++)
                    graph.AddEdge(new GraphEdge($"member-{i}", $"member-{j}", "similar_to", 1, tenantId: tenant));
        }
        lifecycle.SetDecayConfig(Ns, null, null, null, null, null, null, "tenant-b").EnableConsolidation = false;
        var tracker = new BackgroundWorkerStatusTracker();
        using var service = new ConsolidationBackgroundService(lifecycle, statusTracker: tracker);
        var start = _clock.GetUtcNow();
        try
        {
            await service.StartAsync(CancellationToken.None);
            for (int day = 0; day < 14; day++)
            {
                var now = await _clock.RunNextCycleAsync();
                Assert.Equal(start.AddMinutes(10).AddDays(day), now);
                var status = tracker.GetSnapshot().Consolidation;
                Assert.Equal(day + 1, status.CyclesCompleted);
                Assert.Equal((day + 1) * 64, status.TotalEntriesProcessed);
                Assert.Null(status.LastErrorMessage);
                foreach (var tenant in Tenants)
                    Assert.All(index.GetAllInNamespace(Ns, tenant), entry =>
                    {
                        Assert.Equal(tenant + ":" + entry.Id, entry.Text);
                        Assert.Equal(tenant == "tenant-b" ? "stm" : day == 0 ? "ltm" : "archived", entry.LifecycleState);
                    });
                if (day == 0)
                    foreach (var tenant in new[] { "tenant-a", "" })
                        foreach (var entry in index.GetAllInNamespace(Ns, tenant))
                            index.SetActivationEnergyAndState(entry.Id, -10, null, Ns, tenant);
            }
        }
        finally { await Stop(service); }
        Assert.Equal(0, _clock.PendingCount);
        Assert.True(_store!.TryFlush());
        using var reopened = new CognitiveIndex(_store);
        Assert.All(reopened.GetAllInNamespace(Ns, "tenant-b"), e => Assert.Equal("stm", e.LifecycleState));
        Assert.All(reopened.GetAllInNamespace(Ns, "tenant-a"), e => Assert.Equal("archived", e.LifecycleState));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SevenDaysOfAutoLink_RespectTenantConfigurationAndDoNotAccumulateDuplicateEdges(bool sqlite)
    {
        var index = Create(sqlite);
        var graph = new KnowledgeGraph(_store!, index);
        var lifecycle = new LifecycleEngine(index, _store);
        foreach (var tenant in Tenants)
        {
            Seed(index, tenant, "left", "ltm");
            Seed(index, tenant, "right", "ltm");
        }
        lifecycle.SetDecayConfig(Ns, null, null, null, null, null, null, "tenant-b").EnableAutoLink = false;
        var tracker = new BackgroundWorkerStatusTracker();
        using var service = new AutoLinkBackgroundService(new AutoLinkScanner(index, graph, new DuplicateDetector()), index, lifecycle, statusTracker: tracker);
        var start = _clock.GetUtcNow();
        try
        {
            await service.StartAsync(CancellationToken.None);
            for (int cycle = 0; cycle < 28; cycle++)
            {
                Assert.Equal(start.AddMinutes(15).AddHours(cycle * 6), await _clock.RunNextCycleAsync());
                Assert.Equal(cycle + 1, tracker.GetSnapshot().AutoLink.CyclesCompleted);
                Assert.Null(tracker.GetSnapshot().AutoLink.LastErrorMessage);
                Assert.Empty(graph.GetStoredEdges("tenant-b"));
                foreach (var tenant in new[] { "tenant-a", "" })
                {
                    var edge = Assert.Single(graph.GetStoredEdges(tenant));
                    Assert.Equal(tenant, edge.TenantId);
                    Assert.Contains(edge.SourceId, new[] { "left", "right" });
                    Assert.Contains(edge.TargetId, new[] { "left", "right" });
                }
            }
        }
        finally { await Stop(service); }
        Assert.Equal(0, _clock.PendingCount);
        Assert.True(tracker.GetSnapshot().AutoLink.TotalEntriesProcessed > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoDaysOfAccretion_CreatePartitionLocalSummariesWithoutRepeatedCollapse(bool sqlite)
    {
        var index = Create(sqlite);
        var clusters = new ClusterManager(index, _store!);
        foreach (var tenant in Tenants)
            for (int i = 0; i < 4; i++) Seed(index, tenant, $"entry-{i}", "ltm");
        var tracker = new BackgroundWorkerStatusTracker();
        using var service = new AccretionBackgroundService(new AccretionScanner(index, _store), index, clusters,
            new FixedEmbedding(), NullLogger<AccretionBackgroundService>.Instance, tracker);
        var start = _clock.GetUtcNow();
        try
        {
            await service.StartAsync(CancellationToken.None);
            for (int cycle = 1; cycle <= 96; cycle++)
            {
                Assert.Equal(start.AddMinutes(cycle * 30), await _clock.RunNextCycleAsync());
                var status = tracker.GetSnapshot().Accretion;
                Assert.Equal(cycle, status.CyclesCompleted);
                Assert.Null(status.LastErrorMessage);
                foreach (var tenant in Tenants)
                {
                    var entries = index.GetAllInNamespace(Ns, tenant);
                    var summary = Assert.Single(entries, e => e.IsSummaryNode);
                    foreach (var foreign in Tenants.Where(t => t.Length > 0 && t != tenant))
                        Assert.DoesNotContain(foreign + ":entry-", summary.Text);
                    Assert.Equal(4, entries.Count(e => !e.IsSummaryNode));
                    Assert.All(entries.Where(e => !e.IsSummaryNode), e => Assert.Equal(tenant + ":" + e.Id, e.Text));
                }
            }
        }
        finally { await Stop(service); }
        Assert.Equal(0, _clock.PendingCount);
        Assert.True(tracker.GetSnapshot().Accretion.TotalEntriesProcessed >= 12);
    }

    private sealed class FixedEmbedding : IEmbeddingService
    {
        public int Dimensions => 3;
        public float[] Embed(string text) => [1f, 0f, 0f];
    }

    public void Dispose()
    {
        _index?.Dispose();
        _store?.Dispose();
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_path, "memory.db")}");
        SqliteConnection.ClearPool(connection);
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }
}
