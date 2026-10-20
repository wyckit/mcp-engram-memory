using System.Diagnostics;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public sealed class LifecyclePersistenceCostTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"decay_cost_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
    }

    [Fact]
    public void FourDecayPasses_PersistAllUpdatesWithoutRewritingUnchangedHnsw()
    {
        using (var provider = new CountingProvider(new PersistenceManager(_path)))
        using (var index = new CognitiveIndex(provider))
        {
            for (int i = 0; i < 500; i++)
                index.Upsert(Entry($"e{i}", "t1"));
            index.Upsert(Entry("e0", "t2"));
            Assert.True(provider.HnswSaves > 0, "the fixture must build and persist HNSW");
            provider.HnswSaves = 0;

            var lifecycle = new LifecycleEngine(index, provider);
            var watch = Stopwatch.StartNew();
            for (int pass = 0; pass < 4; pass++)
            {
                var result = lifecycle.RunDecayCycle("ns", "t1");
                Assert.Equal(500, result.ProcessedCount);
                Assert.Empty(result.FailedNamespaces ?? []);
            }
            output.WriteLine($"Four 500-entry decay passes: {watch.Elapsed.TotalMilliseconds:F1} ms; " +
                $"{provider.HnswSaves} synchronous HNSW saves.");
            Assert.Equal(0, provider.HnswSaves);
            Assert.True(provider.TryFlush());
        }

        using var restoredProvider = new PersistenceManager(_path);
        using var restored = new CognitiveIndex(restoredProvider);
        var entries = restored.GetAllInNamespace("ns", "t1");
        Assert.Equal(500, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal("ltm", entry.LifecycleState);
            Assert.InRange(entry.ActivationEnergy, 0f, 1f);
        });
        var twin = Assert.Single(restored.GetAllInNamespace("ns", "t2"));
        Assert.Equal("stm", twin.LifecycleState);
        Assert.Equal(9f, twin.ActivationEnergy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnergyAndStateUpdates_PersistWhileVectorChangesStillSaveHnsw(bool legacy)
    {
        string tenant = legacy ? "" : "t1";
        using (var provider = new CountingProvider(new PersistenceManager(_path)))
        using (var index = new CognitiveIndex(provider))
        {
            for (int i = 0; i < 200; i++) index.Upsert(Entry($"e{i}", tenant));
            Assert.True(provider.HnswSaves > 0);
            provider.HnswSaves = 0;

            Assert.True(legacy
                ? index.SetActivationEnergyAndState("e0", 42f, "archived")
                : index.SetActivationEnergyAndState("e0", 42f, "archived", "ns", tenant));
            Assert.True(legacy
                ? index.SetActivationEnergyAndState("e1", 7f)
                : index.SetActivationEnergyAndState("e1", 7f, null, "ns", tenant));
            Assert.Equal(0, provider.HnswSaves);

            var changed = new CognitiveEntry("e2", [0f, 1f], "ns", tenantId: tenant);
            index.Upsert(changed);
            Assert.True(provider.HnswSaves > 0);
            int saves = provider.HnswSaves;
            Assert.True(index.Delete("e3", "ns", tenant));
            Assert.True(provider.HnswSaves > saves);
            Assert.True(provider.TryFlush());
        }

        using var restoredProvider = new PersistenceManager(_path);
        using var restored = new CognitiveIndex(restoredProvider);
        var entries = restored.GetAllInNamespace("ns", tenant).ToDictionary(e => e.Id);
        Assert.Equal(199, entries.Count);
        Assert.Equal(42f, entries["e0"].ActivationEnergy);
        Assert.Equal("archived", entries["e0"].LifecycleState);
        Assert.Equal(7f, entries["e1"].ActivationEnergy);
        Assert.Equal("stm", entries["e1"].LifecycleState);
        Assert.Equal(new[] { 0f, 1f }, entries["e2"].Vector);
        Assert.DoesNotContain("e3", entries.Keys);
        var snapshot = Assert.IsType<HnswSnapshot>(
            restoredProvider.LoadHnswSnapshot(NamespaceStore.PartitionKey(tenant, "ns")));
        Assert.Contains("e2", snapshot.NodeIds);
        Assert.Contains(snapshot.NodeIds.IndexOf("e3"), snapshot.Deleted);
    }

    private static CognitiveEntry Entry(string id, string tenant) =>
        new(id, [1f, 0f], "ns", tenantId: tenant)
        {
            ActivationEnergy = 9f,
            LastAccessedAt = DateTimeOffset.UtcNow.AddHours(-1)
        };

    // Delegate actual JSON durability; count only the synchronous derived-index work.
    private sealed class CountingProvider(IStorageProvider inner) : IStorageProvider
    {
        public int HnswSaves;
        public string StoreIdentity => inner.StoreIdentity;
        public bool SupportsIncrementalWrites => inner.SupportsIncrementalWrites;
        public NamespaceData LoadNamespace(string ns) => inner.LoadNamespace(ns);
        public IReadOnlyList<string> GetPersistedNamespaces() => inner.GetPersistedNamespaces();
        public void ScheduleSave(string ns, Func<NamespaceData> dataProvider) => inner.ScheduleSave(ns, dataProvider);
        public void SaveNamespaceSync(string ns, NamespaceData data) => inner.SaveNamespaceSync(ns, data);
        public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) => inner.ScheduleUpsertEntry(ns, entry);
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
        public Dictionary<string, DecayConfig> LoadDecayConfigs() => inner.LoadDecayConfigs();
        public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider) =>
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
}
