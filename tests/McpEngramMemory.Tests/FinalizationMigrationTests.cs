using Microsoft.Data.Sqlite;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Core.Services.Storage.Migration;

namespace McpEngramMemory.Tests;

/// <summary>Closure controls for exact, retryable tenant-prefix migration provenance.</summary>
public sealed class FinalizationMigrationTests
{
    [Fact]
    public void Reverse_SparesReplacementEntryClusterAndReceipt()
    {
        WithStore(storage =>
        {
            var entry = Entry("e", "acme::work");
            entry.Revision = 7;
            entry.LifecycleRevision = 11;
            storage.SaveNamespaceSync("acme::work", new NamespaceData { Entries = [entry] });
            SaveClusters(storage,
                new SemanticCluster("c", "old", "acme::work", ["e"], null, null, "")
                { CreationStamp = "stamp-1", InstanceId = "instance-1" });
            Assert.True(storage.UpsertCollapseRecordSync(Receipt("r", "acme::work", "", 3)));

            var tool = new TenantPrefixMigrationTool(storage);
            var manifest = tool.Migrate();

            var replacement = Entry("e", "work", "acme");
            replacement.Revision = 8;
            replacement.LifecycleRevision = 12;
            storage.SaveNamespaceSync("work", new NamespaceData { Entries = [replacement] });
            SaveClusters(storage,
                new SemanticCluster("c", "replacement", "work", ["e"], null, null, "acme")
                { CreationStamp = "stamp-2", InstanceId = "instance-2" });
            Assert.True(storage.UpsertCollapseRecordSync(Receipt("r", "work", "acme", 4)));

            var reverse = tool.Reverse(manifest);

            var spared = Assert.Single(storage.LoadNamespace("work").Entries);
            Assert.Equal(8, spared.Revision);
            Assert.Empty(storage.LoadNamespace("acme::work").Entries);
            var cluster = Assert.Single(storage.LoadClusters());
            Assert.Equal("work", cluster.Ns);
            Assert.Equal("instance-2", cluster.InstanceId);
            Assert.True(storage.TryReadCollapseHistory(out var receipts));
            var receipt = Assert.Single(receipts);
            Assert.Equal("work", receipt.Ns);
            Assert.Equal(4, receipt.Generation);
            Assert.Empty(reverse.Records);
            Assert.Empty(reverse.GraphRowMoves!);
            Assert.Contains(reverse.WarningList, w => w.Contains("replaced", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void Reverse_SparesNewerOccupationRecreatedAtOriginalSource()
    {
        WithStore(storage =>
        {
            var migrated = Entry("e", "acme::work");
            migrated.Revision = 7;
            migrated.LifecycleRevision = 11;
            storage.SaveNamespaceSync("acme::work",
                new NamespaceData { Entries = [migrated] });
            var tool = new TenantPrefixMigrationTool(storage);
            var manifest = tool.Migrate();

            var sourceReplacement = Entry("e", "acme::work");
            sourceReplacement.Revision = 8;
            sourceReplacement.LifecycleRevision = 12;
            storage.SaveNamespaceSync("acme::work",
                new NamespaceData { Entries = [sourceReplacement] });

            var reverse = tool.Reverse(manifest);

            var source = Assert.Single(storage.LoadNamespace("acme::work").Entries);
            Assert.Equal(8, source.Revision);
            Assert.Equal(12, source.LifecycleRevision);
            var destination = Assert.Single(storage.LoadNamespace("work").Entries);
            Assert.Equal(7, destination.Revision);
            Assert.Equal(11, destination.LifecycleRevision);
            Assert.Empty(reverse.Records);
            Assert.Contains(reverse.WarningList, w =>
                w.Contains("original slot", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reverse_EntryRefusalKeepsWholePlacementTopologyMigrated(
        bool replaceOriginalSource)
    {
        WithStore(storage =>
        {
            var entry = Entry("e", "acme::work");
            entry.Revision = 7;
            entry.LifecycleRevision = 11;
            storage.SaveNamespaceSync("acme::work",
                new NamespaceData { Entries = [entry] });
            SaveClusters(storage,
                new SemanticCluster("c", "cluster", "acme::work", ["e"], null, null, "")
                { CreationStamp = "stamp", InstanceId = "instance" });
            Assert.True(storage.UpsertCollapseRecordSync(
                Receipt("r", "acme::work", "", 3)));
            var tool = new TenantPrefixMigrationTool(storage);
            var manifest = tool.Migrate();

            var replacement = Entry("e", replaceOriginalSource ? "acme::work" : "work",
                replaceOriginalSource ? "" : "acme");
            replacement.Revision = 8;
            replacement.LifecycleRevision = 12;
            storage.SaveNamespaceSync(replacement.Ns,
                new NamespaceData { Entries = [replacement] });

            var reverse = tool.Reverse(manifest);

            if (replaceOriginalSource)
            {
                Assert.Equal(8, Assert.Single(storage.LoadNamespace("acme::work").Entries).Revision);
                Assert.Equal(7, Assert.Single(storage.LoadNamespace("work").Entries).Revision);
            }
            else
            {
                Assert.Empty(storage.LoadNamespace("acme::work").Entries);
                Assert.Equal(8, Assert.Single(storage.LoadNamespace("work").Entries).Revision);
            }
            var cluster = Assert.Single(storage.LoadClusters());
            Assert.Equal("work", cluster.Ns);
            Assert.Equal("acme", cluster.TenantId);
            Assert.True(storage.TryReadCollapseHistory(out var receipts));
            var receipt = Assert.Single(receipts);
            Assert.Equal("work", receipt.Ns);
            Assert.Equal("acme", receipt.TenantId);
            Assert.Empty(reverse.Records);
            Assert.Empty(reverse.GraphRowMoves!);
            Assert.Contains(reverse.WarningList, w =>
                w.Contains("not reversed as a unit", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void ReverseRetry_RecognizesAlreadyReversedEntryAndGraphRows_ThenConverges()
    {
        WithStore(storage =>
        {
            var entry = Entry("e", "acme::work");
            entry.Revision = 7;
            entry.LifecycleRevision = 11;
            storage.SaveNamespaceSync("acme::work",
                new NamespaceData { Entries = [entry] });
            SaveClusters(storage,
                new SemanticCluster("c", "cluster", "acme::work", ["e"], null, null, "")
                { CreationStamp = "stamp", InstanceId = "instance" });
            Assert.True(storage.UpsertCollapseRecordSync(
                Receipt("r", "acme::work", "", 3)));
            var forward = new TenantPrefixMigrationTool(storage).Migrate();

            var faulty = new FaultInjectingProvider(storage) { FailTryFlushes = 1 };
            var tool = new TenantPrefixMigrationTool(faulty);
            var partial = tool.Reverse(forward);
            Assert.Single(partial.Records);
            Assert.Equal("acme::work", Assert.Single(storage.LoadNamespace("acme::work").Entries).Ns);
            Assert.Equal("work", Assert.Single(storage.LoadClusters()).Ns);
            Assert.True(storage.TryReadCollapseHistory(out var partiallyReversedReceipts));
            Assert.Equal("acme::work", Assert.Single(partiallyReversedReceipts).Ns);

            var healed = tool.Reverse(forward);
            Assert.Single(healed.Records);
            Assert.Equal(2, healed.GraphRowMoves!.Count);
            Assert.Equal("acme::work", Assert.Single(storage.LoadClusters()).Ns);
            Assert.True(storage.TryReadCollapseHistory(out var healedReceipts));
            Assert.Equal("acme::work", Assert.Single(healedReceipts).Ns);

            tool.Reverse(healed);
            Assert.Equal("work", Assert.Single(storage.LoadNamespace("work").Entries).Ns);
            Assert.Equal("work", Assert.Single(storage.LoadClusters()).Ns);
            Assert.True(storage.TryReadCollapseHistory(out var replayedReceipts));
            Assert.Equal("work", Assert.Single(replayedReceipts).Ns);
        });
    }

    [Fact]
    public void GraphOnlyReverseOfReverse_DoesNotTakeEntryEarlyReturn()
    {
        WithStore(storage =>
        {
            SaveClusters(storage,
                new SemanticCluster("c", "graph-only", "work", ["e"], null, null, "acme")
                { CreationStamp = "stamp", InstanceId = "instance" });
            var manifest = new TenantMigrationManifest([], 0, 0,
                GraphRowMoves:
                [
                    new MigratedGraphRowRecord("cluster", "c", "acme::work", "", "work",
                        "acme", CreationStamp: "stamp", InstanceId: "instance",
                        HasIdentityWitness: true)
                ]);

            var reverse = new TenantPrefixMigrationTool(storage).Reverse(manifest);
            Assert.Equal("acme::work", Assert.Single(storage.LoadClusters()).Ns);
            Assert.Single(reverse.GraphRowMoves!);

            new TenantPrefixMigrationTool(storage).Reverse(reverse);
            var forwardAgain = Assert.Single(storage.LoadClusters());
            Assert.Equal("work", forwardAgain.Ns);
            Assert.Equal("acme", forwardAgain.TenantId);
        });
    }

    [Fact]
    public void NormalizedSourceCollision_KeepsUnsafeSourcesGraphRowsAndExactAcceptedProvenance()
    {
        WithStore(storage =>
        {
            storage.SaveNamespaceSync("acme::work", new NamespaceData { Entries = [Entry("same", "acme::work")] });
            storage.SaveNamespaceSync(" acme ::work", new NamespaceData { Entries = [Entry("same", " acme ::work")] });
            SaveClusters(storage,
                new SemanticCluster("c1", "one", "acme::work", ["same"], null, null, "")
                { CreationStamp = "s1", InstanceId = "i1" },
                new SemanticCluster("c2", "two", " acme ::work", ["same"], null, null, "")
                { CreationStamp = "s2", InstanceId = "i2" });

            var manifest = new TenantPrefixMigrationTool(storage).Migrate();

            var accepted = Assert.Single(manifest.Records);
            var refusedSource = accepted.OriginalNs == "acme::work" ? " acme ::work" : "acme::work";
            Assert.Single(storage.LoadNamespace("work").Entries);
            Assert.Single(storage.LoadNamespace(refusedSource).Entries);
            Assert.DoesNotContain(storage.GetPersistedNamespaces(), n =>
                n != refusedSource && n != "work" && n.EndsWith("::work", StringComparison.Ordinal));

            var clusters = storage.LoadClusters();
            var acceptedCluster = Assert.Single(clusters, c => c.Ns == "work");
            var refusedCluster = Assert.Single(clusters, c => c.Ns == refusedSource);
            Assert.NotEqual(acceptedCluster.ClusterId, refusedCluster.ClusterId);
            var graphMove = Assert.Single(manifest.GraphRowMoves!);
            Assert.Equal(accepted.OriginalNs, graphMove.FromNs);
        });
    }

    [Fact]
    public void MixedCollisionSource_RetractsAllMovesAndPreservesParity()
    {
        WithStore(storage =>
        {
            var collided = Entry("collided", "acme::work");
            collided.Revision = 1;
            var otherwiseAccepted = Entry("accepted", "acme::work");
            otherwiseAccepted.Revision = 2;
            storage.SaveNamespaceSync("acme::work",
                new NamespaceData { Entries = [collided, otherwiseAccepted] });

            var resident = Entry("collided", "work", "acme");
            resident.Revision = 9;
            storage.SaveNamespaceSync("work", new NamespaceData { Entries = [resident] });
            SaveClusters(storage,
                new SemanticCluster("c", "source", "acme::work", ["collided", "accepted"],
                    null, null, "") { CreationStamp = "stamp", InstanceId = "instance" });

            var manifest = new TenantPrefixMigrationTool(storage).Migrate();

            Assert.True(manifest.RowCountParityOk);
            Assert.Empty(manifest.Records);
            Assert.Empty(manifest.GraphRowMoves!);
            var source = storage.LoadNamespace("acme::work").Entries;
            Assert.Equal(2, source.Count);
            Assert.Contains(source, e => e.Id == "collided");
            Assert.Contains(source, e => e.Id == "accepted");
            var destination = storage.LoadNamespace("work").Entries;
            var onlyResident = Assert.Single(destination);
            Assert.Equal("collided", onlyResident.Id);
            Assert.Equal(9, onlyResident.Revision);
            Assert.Equal(1, storage.GetPersistedNamespaces()
                .Sum(ns => storage.LoadNamespace(ns).Entries.Count(e => e.Id == "accepted")));
            Assert.Equal("acme::work", Assert.Single(storage.LoadClusters()).Ns);
        });
    }

    [Fact]
    public void ReverseClusterFlushFailure_IsNeutralizedAndPublishesNoProvenance()
    {
        WithStore(storage =>
        {
            SaveClusters(storage,
                new SemanticCluster("c", "moved", "work", ["e"], null, null, "acme")
                { CreationStamp = "stamp", InstanceId = "instance" });
            var faulty = new FaultInjectingProvider(storage) { FailTryFlushes = 1 };
            var manifest = new TenantMigrationManifest([], 0, 0,
                GraphRowMoves:
                [
                    new MigratedGraphRowRecord("cluster", "c", "acme::work", "", "work",
                        "acme", CreationStamp: "stamp", InstanceId: "instance",
                        HasIdentityWitness: true)
                ]);

            var failed = new TenantPrefixMigrationTool(faulty).Reverse(manifest);
            Assert.Equal("work", Assert.Single(storage.LoadClusters()).Ns);
            Assert.Empty(failed.GraphRowMoves!);

            var retried = new TenantPrefixMigrationTool(storage).Reverse(manifest);
            Assert.Equal("acme::work", Assert.Single(storage.LoadClusters()).Ns);
            Assert.Single(retried.GraphRowMoves!);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialGraphMoveRetry_CumulatesPriorDurableProvenance(bool recreateTool)
    {
        WithStore(storage =>
        {
            storage.SaveNamespaceSync("acme::work", new NamespaceData { Entries = [Entry("e", "acme::work")] });
            Assert.True(storage.UpsertCollapseRecordSync(Receipt("r1", "acme::work", "", 1)));
            Assert.True(storage.UpsertCollapseRecordSync(Receipt("r2", "acme::work", "", 1)));
            var faulty = new FaultInjectingProvider(storage) { FailConditionalUpsertCall = 2 };
            var tool = new TenantPrefixMigrationTool(faulty);

            var partial = tool.Migrate();
            Assert.Single(partial.GraphRowMoves!);
            Assert.Contains("acme::work", storage.GetPersistedNamespaces());

            if (recreateTool)
                tool = new TenantPrefixMigrationTool(faulty);
            var healed = recreateTool
                ? tool.Migrate(priorAttempt: partial)
                : tool.Migrate();
            Assert.Equal(2, healed.GraphRowMoves!.Count(r => r.Kind == "receipt"));

            tool.Reverse(healed);
            Assert.True(storage.TryReadCollapseHistory(out var receipts));
            Assert.Equal(2, receipts.Count);
            Assert.All(receipts, r =>
            {
                Assert.Equal("acme::work", r.Ns);
                Assert.Equal("", r.TenantId);
            });
        });
    }

    private static CognitiveEntry Entry(string id, string ns, string tenant = "")
        => new(id, [1f, 0f], ns, text: id, tenantId: tenant);

    private static CollapseRecord Receipt(string id, string ns, string tenant, long generation)
        => new(id, "cluster", "summary", ns, ["e"], new Dictionary<string, string>(),
            tenantId: tenant, generation: generation, clusterStamp: "stamp",
            clusterInstance: "instance");

    private static void SaveClusters(SqliteStorageProvider storage, params SemanticCluster[] clusters)
    {
        storage.ScheduleSaveClusters(() => clusters.ToList());
        Assert.True(storage.TryFlush());
    }

    private static void WithStore(Action<SqliteStorageProvider> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"final-migration-{Guid.NewGuid():N}.db");
        try
        {
            using var storage = new SqliteStorageProvider(path, debounceMs: 60_000);
            test(storage);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class FaultInjectingProvider(IStorageProvider inner) : IStorageProvider
    {
        private int _conditionalUpsertCalls;
        public int FailTryFlushes { get; set; }
        public int FailConditionalUpsertCall { get; set; }

        public CollapseRecordCas UpsertCollapseRecordSync(CollapseRecord record, long? onlyIfGeneration)
        {
            _conditionalUpsertCalls++;
            if (_conditionalUpsertCalls == FailConditionalUpsertCall)
                return CollapseRecordCas.StoreFailed;
            return inner.UpsertCollapseRecordSync(record, onlyIfGeneration);
        }

        public bool TryFlush()
        {
            if (FailTryFlushes > 0)
            {
                FailTryFlushes--;
                return false;
            }
            return inner.TryFlush();
        }

        public string StoreIdentity => inner.StoreIdentity;
        public NamespaceData LoadNamespace(string ns) => inner.LoadNamespace(ns);
        public IReadOnlyList<string> GetPersistedNamespaces() => inner.GetPersistedNamespaces();
        public void ScheduleSave(string ns, Func<NamespaceData> dataProvider) => inner.ScheduleSave(ns, dataProvider);
        public void SaveNamespaceSync(string ns, NamespaceData data) => inner.SaveNamespaceSync(ns, data);
        public bool SupportsIncrementalWrites => inner.SupportsIncrementalWrites;
        public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) => inner.ScheduleUpsertEntry(ns, entry);
        public void ScheduleDeleteEntry(string ns, string entryId) => inner.ScheduleDeleteEntry(ns, entryId);
        public List<GraphEdge> LoadGlobalEdges() => inner.LoadGlobalEdges();
        public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider) => inner.ScheduleSaveGlobalEdges(dataProvider);
        public List<SemanticCluster> LoadClusters() => inner.LoadClusters();
        public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider) => inner.ScheduleSaveClusters(dataProvider);
        public List<CollapseRecord> LoadCollapseHistory() => inner.LoadCollapseHistory();
        public bool UpsertCollapseRecordSync(CollapseRecord record) => inner.UpsertCollapseRecordSync(record);
        public bool DeleteCollapseRecordSync(string collapseId) => inner.DeleteCollapseRecordSync(collapseId);
        public CollapseRecordCas DeleteCollapseRecordSync(string collapseId, long onlyIfGeneration) => inner.DeleteCollapseRecordSync(collapseId, onlyIfGeneration);
        public bool TryReadCollapseRecord(string collapseId, out CollapseRecord? record) => inner.TryReadCollapseRecord(collapseId, out record);
        public bool TryReadCollapseHistory(out List<CollapseRecord> records) => inner.TryReadCollapseHistory(out records);
        public Dictionary<string, DecayConfig> LoadDecayConfigs() => inner.LoadDecayConfigs();
        public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider) => inner.ScheduleSaveDecayConfigs(dataProvider);
        public HnswSnapshot? LoadHnswSnapshot(string ns) => inner.LoadHnswSnapshot(ns);
        public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot) => inner.SaveHnswSnapshotSync(ns, snapshot);
        public void DeleteHnswSnapshot(string ns) => inner.DeleteHnswSnapshot(ns);
        public Task DeleteNamespaceAsync(string ns) => inner.DeleteNamespaceAsync(ns);
        public void Flush() => inner.Flush();
        public void Dispose() { }
    }
}
