using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// Deterministic regression controls for the round-13 review findings: the durable phase is a
/// generation-CAS WRITE (a lease), not a read; record retirement fails closed on an unproven
/// flush; cluster cleanup compares a persisted incarnation stamp; the JSON history RMW holds an
/// interprocess lock; strict reads validate checksums; and the zero-admission retraction is
/// generation-compared. (The SQLite checksum control lives with the provider tests; the
/// cluster-membership occupancy-pin control is at the bottom.)
/// </summary>
public class Round13RegressionTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly ClusterManager _clusters;
    private readonly LifecycleEngine _lifecycle;
    private readonly KnowledgeGraph _graph;
    private readonly AccretionScanner _scanner;

    public Round13RegressionTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"round13_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 60_000);
        _index = new CognitiveIndex(_persistence);
        _clusters = new ClusterManager(_index, _persistence);
        _lifecycle = new LifecycleEngine(_index);
        _graph = new KnowledgeGraph(_persistence, _index);
        _scanner = new AccretionScanner(_index, _persistence);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    private string SeedAndDetect(AccretionScanner scanner)
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));
        return scanner.ScanNamespace("test", tenantId: "").NewCollapses[0].CollapseId;
    }

    /// <summary>Same fault-injecting delegate as the round-12 file, for this round's faults.</summary>
    private sealed class FaultInjectingProvider : IStorageProvider
    {
        private readonly IStorageProvider _inner;
        /// <summary>1-based index of the conditional-upsert call before which a racing undoer's
        /// terminal delete lands (executor calls run intent=1, claims=2, commit=3).</summary>
        public int UndoerDeleteBeforeConditionalUpsert { get; set; }
        public int FailTryFlushes { get; set; }
        public int RefuseConditionalDeletesAdvancingRecord { get; set; }
        private int _conditionalUpsertCalls;
        public FaultInjectingProvider(IStorageProvider inner) => _inner = inner;

        public CollapseRecordCas UpsertCollapseRecordSync(CollapseRecord record, long? onlyIfGeneration)
        {
            _conditionalUpsertCalls++;
            if (UndoerDeleteBeforeConditionalUpsert == _conditionalUpsertCalls && onlyIfGeneration is not null)
                _inner.DeleteCollapseRecordSync(record.CollapseId, onlyIfGeneration.Value);
            return _inner.UpsertCollapseRecordSync(record, onlyIfGeneration);
        }

        public CollapseRecordCas DeleteCollapseRecordSync(string collapseId, long onlyIfGeneration)
        {
            if (RefuseConditionalDeletesAdvancingRecord > 0)
            {
                RefuseConditionalDeletesAdvancingRecord--;
                if (_inner.TryReadCollapseRecord(collapseId, out var current) && current is not null)
                {
                    _inner.UpsertCollapseRecordSync(new CollapseRecord(
                        current.CollapseId, current.ClusterId, current.SummaryEntryId, current.Ns,
                        current.MemberIds, current.PreviousStates, current.CollapsedAt, current.TenantId,
                        current.AppliedLifecycleRevisions, current.ExpectedLifecycleRevisions,
                        current.Generation + 1, current.ClusterStamp));
                }
                return CollapseRecordCas.GenerationMoved;
            }
            return _inner.DeleteCollapseRecordSync(collapseId, onlyIfGeneration);
        }

        public bool TryFlush()
        {
            if (FailTryFlushes > 0)
            {
                FailTryFlushes--;
                return false;
            }
            return _inner.TryFlush();
        }

        public bool UpsertCollapseRecordSync(CollapseRecord record) => _inner.UpsertCollapseRecordSync(record);
        public bool DeleteCollapseRecordSync(string collapseId) => _inner.DeleteCollapseRecordSync(collapseId);
        public bool TryReadCollapseRecord(string collapseId, out CollapseRecord? record) => _inner.TryReadCollapseRecord(collapseId, out record);
    public bool TryReadCollapseHistory(out List<CollapseRecord> records) => _inner.TryReadCollapseHistory(out records);
        public NamespaceData LoadNamespace(string ns) => _inner.LoadNamespace(ns);
        public IReadOnlyList<string> GetPersistedNamespaces() => _inner.GetPersistedNamespaces();
        public void ScheduleSave(string ns, Func<NamespaceData> dataProvider) => _inner.ScheduleSave(ns, dataProvider);
        public void SaveNamespaceSync(string ns, NamespaceData data) => _inner.SaveNamespaceSync(ns, data);
        public bool SupportsIncrementalWrites => _inner.SupportsIncrementalWrites;
        public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) => _inner.ScheduleUpsertEntry(ns, entry);
        public void ScheduleDeleteEntry(string ns, string entryId) => _inner.ScheduleDeleteEntry(ns, entryId);
        public List<GraphEdge> LoadGlobalEdges() => _inner.LoadGlobalEdges();
        public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider) => _inner.ScheduleSaveGlobalEdges(dataProvider);
        public List<SemanticCluster> LoadClusters() => _inner.LoadClusters();
        public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider) => _inner.ScheduleSaveClusters(dataProvider);
        public List<CollapseRecord> LoadCollapseHistory() => _inner.LoadCollapseHistory();
        public Dictionary<string, DecayConfig> LoadDecayConfigs() => _inner.LoadDecayConfigs();
        public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider) => _inner.ScheduleSaveDecayConfigs(dataProvider);
        public HnswSnapshot? LoadHnswSnapshot(string ns) => _inner.LoadHnswSnapshot(ns);
        public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot) => _inner.SaveHnswSnapshotSync(ns, snapshot);
        public void DeleteHnswSnapshot(string ns) => _inner.DeleteHnswSnapshot(ns);
        public Task DeleteNamespaceAsync(string ns) => _inner.DeleteNamespaceAsync(ns);
        public void Flush() => _inner.Flush();
        public void Dispose() { }
    }

    /// <summary>
    /// Finding 1: the durable phase is a LEASE, not a read. An undoer whose terminal delete
    /// lands immediately before the executor's commit write makes the commit refuse
    /// (AlreadyAbsent) and the archives roll back — where a read-based verify saw the record
    /// "present", returned "Collapsed 4", and left the entries archived with no receipt after
    /// the undoer's delete landed.
    /// </summary>
    [Fact]
    public void ExecuteCollapse_UndoerDeletesBeforeCommit_RollsBackInsteadOfSucceeding()
    {
        // Write 4 is the durable-phase commit (intent=1, created-instance=2, claims=3).
        var faulty = new FaultInjectingProvider(_persistence) { UndoerDeleteBeforeConditionalUpsert = 4 };
        var scanner = new AccretionScanner(_index, faulty);
        var collapseId = SeedAndDetect(scanner);

        var result = scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");

        Assert.StartsWith("Error:", result);
        Assert.Contains("lost its durable record", result);
        foreach (var id in new[] { "a", "b", "c", "d" })
            Assert.Equal("ltm", _index.Get(id, "test", tenantId: "")!.LifecycleState);
        Assert.Empty(scanner.GetCollapseHistory("test", tenantId: ""));
        Assert.True(_persistence.TryReadCollapseRecord(collapseId, out var onDisk));
        Assert.Null(onDisk);
        // The proposal survives; a clean retry completes.
        Assert.StartsWith("Collapsed 4", scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));
    }

    /// <summary>
    /// Adversarial finding: a persisted claim whose archive CAS has NOT yet fired is ARMED,
    /// not inert. An undoer that retires the record in that window DISARMS the claims (bumps
    /// the members' lifecycle witnesses), so the executor's pending archive CASes refuse —
    /// and an executor CRASH right after its archive loop (simulated by a throwing seam)
    /// strands nothing: members stand unarchived, record retired, reversal complete. Without
    /// the disarm, this exact crash left every member archived on disk with no receipt
    /// anywhere entitled to restore it. The undoer runs from a second scanner whose store key
    /// aliases apart (wrapper providers), the cross-gate actor the in-memory gate cannot see.
    /// </summary>
    [Fact]
    public void UndoDuringClaimsWindow_DisarmsClaims_SoACrashedExecutorStrandsNothing()
    {
        var scanner1 = new AccretionScanner(_index, new FaultInjectingProvider(_persistence));
        var scanner2 = new AccretionScanner(_index, new FaultInjectingProvider(_persistence));
        var collapseId = SeedAndDetect(scanner1);

        string? interleavedUndo = null;
        scanner1.OnBeforeArchiveCas = () =>
        {
            scanner1.OnBeforeArchiveCas = null;
            interleavedUndo = scanner2.UndoCollapse(collapseId, _lifecycle, _clusters, tenantId: "");
        };
        scanner1.OnBeforeDurableCommit = () => throw new InvalidOperationException("simulated crash");

        Assert.Throws<InvalidOperationException>(() => scanner1.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));

        // The undoer saw only armed claims: it disarmed them, cleaned up, and retired the
        // record — a complete reversal.
        Assert.NotNull(interleavedUndo);
        Assert.StartsWith("Reversed", interleavedUndo);
        // The crashed executor's pending archives REFUSED against the bumped witnesses:
        // nothing stands archived, and nothing on disk needs a receipt that no longer exists.
        foreach (var id in new[] { "a", "b", "c", "d" })
            Assert.Equal("ltm", _index.Get(id, "test", tenantId: "")!.LifecycleState);
        Assert.True(_persistence.TryReadCollapseRecord(collapseId, out var onDisk));
        Assert.Null(onDisk);
    }

    /// <summary>
    /// The other half of the lease: once the commit APPLIES, a stale undoer holding the
    /// pre-commit generation gets GenerationMoved from its terminal delete and must re-read —
    /// it can no longer discard the claims the commit fenced.
    /// </summary>
    [Fact]
    public void CommittedExecution_FencesStaleUndoerDeletes()
    {
        var collapseId = SeedAndDetect(_scanner);
        Assert.StartsWith("Collapsed 4", _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));

        var committed = Assert.Single(_scanner.GetCollapseHistory("test", tenantId: ""));
        Assert.Equal(CollapseRecordCas.GenerationMoved,
            _persistence.DeleteCollapseRecordSync(collapseId, committed.Generation - 1));
        Assert.True(_persistence.TryReadCollapseRecord(collapseId, out var still));
        Assert.NotNull(still);
    }

    /// <summary>
    /// Finding 2: record retirement fails CLOSED on an unproven flush. The undo's restores
    /// ride the debounced write stream; if the flush cannot prove them durable, the receipt
    /// must stay — a void Flush over swallowed errors let the record retire while the restored
    /// entries stood archived on disk.
    /// </summary>
    [Fact]
    public void UndoCollapse_FlushFails_KeepsTheRecordAndRetriesCleanly()
    {
        var faulty = new FaultInjectingProvider(_persistence) { FailTryFlushes = 1 };
        var scanner = new AccretionScanner(_index, faulty);
        var collapseId = SeedAndDetect(scanner);
        Assert.StartsWith("Collapsed 4", scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));

        var undo = scanner.UndoCollapse(collapseId, _lifecycle, _clusters, tenantId: "");
        Assert.StartsWith("Error:", undo);
        Assert.Contains("could not all be made durable", undo);
        Assert.Single(scanner.GetCollapseHistory("test", tenantId: ""));
        Assert.True(_persistence.TryReadCollapseRecord(collapseId, out var preserved));
        Assert.NotNull(preserved);

        var retry = scanner.UndoCollapse(collapseId, _lifecycle, _clusters, tenantId: "");
        Assert.StartsWith("Reversed", retry);
        foreach (var id in new[] { "a", "b", "c", "d" })
            Assert.Equal("ltm", _index.Get(id, "test", tenantId: "")!.LifecycleState);
        Assert.Empty(scanner.GetCollapseHistory("test", tenantId: ""));
    }

    /// <summary>
    /// Finding 3: cluster identity is the persisted INCARNATION STAMP, not the public id. A
    /// same-namespace recreation of the recorded cluster id — with its own members and its own
    /// summary — must survive the old record's undo untouched, and the undo must still restore
    /// its own members and retire its record.
    /// </summary>
    [Fact]
    public void UndoCollapse_SameNamespaceForgedCluster_IsSparedEntirely()
    {
        var collapseId = SeedAndDetect(_scanner);

        // Partial execution leaves a durable record naming the cluster id and stamp.
        _index.Delete("d");
        Assert.StartsWith("Error:", _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));
        var record = Assert.Single(_scanner.GetCollapseHistory("test", tenantId: ""));
        var clusterId = record.ClusterId;

        // A forger tears our cluster down and recreates the SAME id in the SAME namespace,
        // with its own member and its own summary.
        Assert.DoesNotContain("Error:",
            _clusters.UpdateCluster(clusterId, addIds: null,
                removeIds: new List<string> { "a", "b", "c", "d" }, label: null, tenantId: ""));
        Assert.Equal(EmptyClusterRemoval.Removed, _clusters.RemoveClusterIfEmpty(clusterId, tenantId: ""));
        _index.Upsert(new CognitiveEntry("x", new[] { 0f, 1f, 0f }, "test", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error:",
            _clusters.CreateCluster(clusterId, "test", new List<string> { "x" }, "forged", tenantId: ""));
        Assert.DoesNotContain("Error:",
            _clusters.StoreSummary(clusterId, "forged summary", new[] { 0f, 1f, 0f }, tenantId: ""));

        var undo = _scanner.UndoCollapse(collapseId, _lifecycle, _clusters, tenantId: "");

        Assert.StartsWith("Reversed", undo);
        // Our members came back; the record retired.
        foreach (var id in new[] { "a", "b", "c" })
            Assert.Equal("ltm", _index.Get(id, "test", tenantId: "")!.LifecycleState);
        Assert.Empty(_scanner.GetCollapseHistory("test", tenantId: ""));
        // The forged incarnation survives whole: cluster, membership, and summary.
        Assert.True(_clusters.TryGetClusterStamp(clusterId, tenantId: "", out var forgedStamp));
        Assert.NotEqual(record.ClusterStamp, forgedStamp);
        Assert.Contains(_clusters.GetClusterMembershipsForEntry("x", tenantId: ""),
            m => m.ClusterId == clusterId);
        Assert.NotNull(_index.Get($"summary:{clusterId}", "test", tenantId: ""));
    }

    /// <summary>
    /// Finding 4: the JSON history RMW holds an OS-level interprocess lock spanning
    /// read-through-replace. While another process (here: another handle with
    /// <see cref="FileShare.None"/>) holds the lock file, every history operation refuses
    /// honestly instead of interleaving; releasing it restores service.
    /// </summary>
    [Fact]
    public void JsonHistoryRmw_WhileInterprocessLockHeld_RefusesInsteadOfInterleaving()
    {
        var record = new CollapseRecord(
            "collapse:ipc", "cluster-i", "summary-i", "test",
            new List<string> { "m" }, new Dictionary<string, string>(), tenantId: "");

        var lockPath = Path.Combine(_testDataPath, "_collapse_history.lock");
        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(_persistence.UpsertCollapseRecordSync(record));
        }

        Assert.True(_persistence.UpsertCollapseRecordSync(record));
        Assert.True(_persistence.TryReadCollapseRecord("collapse:ipc", out var stored));
        Assert.NotNull(stored);
    }

    /// <summary>
    /// Finding 5: strict reads validate the stored checksum. Valid-JSON tampering under a
    /// stale checksum is REFUSED — by the strict read (existence unknown, never absent) and by
    /// the RMW (which would otherwise normalize the tampering into a re-checksummed commit).
    /// Removing the companion is the legacy state and passes, matching the loader's own
    /// convention.
    /// </summary>
    [Fact]
    public void JsonHistoryReads_TamperedContentWithStaleChecksum_IsRefused()
    {
        var record = new CollapseRecord(
            "collapse:sum", "cluster-s", "summary-s", "test",
            new List<string> { "m" }, new Dictionary<string, string>(), tenantId: "");
        Assert.True(_persistence.UpsertCollapseRecordSync(record));

        var historyPath = Path.Combine(_testDataPath, "_collapse_history.json");
        var tampered = File.ReadAllText(historyPath).Replace("cluster-s", "cluster-TAMPERED");
        Assert.NotEqual(tampered, File.ReadAllText(historyPath));
        File.WriteAllText(historyPath, tampered);

        Assert.False(_persistence.TryReadCollapseRecord("collapse:sum", out _));
        var another = new CollapseRecord(
            "collapse:other", "cluster-o", "summary-o", "test",
            new List<string> { "m2" }, new Dictionary<string, string>(), tenantId: "");
        Assert.False(_persistence.UpsertCollapseRecordSync(another));

        // Legacy state: a pre-envelope raw-ARRAY file (no internal checksum) passes through,
        // by the boot loader's own convention for data written before checksums existed.
        File.WriteAllText(historyPath, "[]");
        File.Delete(historyPath + ".sha256");
        Assert.True(_persistence.TryReadCollapseRecord("collapse:sum", out var legacyRead));
        Assert.Null(legacyRead);
    }

    /// <summary>
    /// Finding 6: the zero-admission retraction is generation-compared. A record that
    /// advanced between the branch's judgment and its delete is NOT erased; the error is
    /// retryable and the next attempt retracts cleanly against the fresh generation.
    /// </summary>
    [Fact]
    public void ExecuteCollapse_AllScreenedRetraction_RefusesWhenRecordAdvanced()
    {
        var faulty = new FaultInjectingProvider(_persistence) { RefuseConditionalDeletesAdvancingRecord = 1 };
        var scanner = new AccretionScanner(_index, faulty);
        var collapseId = SeedAndDetect(scanner);
        foreach (var id in new[] { "a", "b", "c", "d" })
            _index.Upsert(new CognitiveEntry(id, new[] { 0f, 1f, 0f }, "other", lifecycleState: "ltm"));

        var first = scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");
        Assert.StartsWith("Error:", first);
        Assert.Contains("advanced concurrently", first);
        // The advanced record still stands, on disk and in memory.
        Assert.True(_persistence.TryReadCollapseRecord(collapseId, out var advanced));
        Assert.NotNull(advanced);
        Assert.Single(scanner.GetCollapseHistory("test", tenantId: ""));

        var retry = scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");
        Assert.Contains("admitted no members", retry);
        Assert.True(_persistence.TryReadCollapseRecord(collapseId, out var retracted));
        Assert.Null(retracted);
    }

    /// <summary>
    /// P2: the cluster-membership twin of the graph occupancy-pin control. A same-slot
    /// replacement injected at the last reachable instant — the pre-pin seam of the cluster
    /// primitive — makes the eviction refuse BEFORE anything is removed: the id reports
    /// unsettled and the replacement's inherited membership survives.
    /// </summary>
    [Fact]
    public void CascadeAll_ReplacementDuringClusterSweep_MembershipSurvives()
    {
        _index.Upsert(new CognitiveEntry("swept", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("anchor", new[] { 0.9f, 0.1f, 0f }, "test", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error:",
            _clusters.CreateCluster("pin-cluster", "test", new List<string> { "swept", "anchor" }, null, tenantId: ""));

        int seamFired = 0;
        _clusters.OnBeforeOccupancyPin = () =>
        {
            if (Interlocked.Increment(ref seamFired) == 1)
            {
                _clusters.OnBeforeOccupancyPin = null;
                _index.Upsert(new CognitiveEntry("swept", new[] { 0.5f, 0.5f, 0f }, "test",
                    "replacement occupation", tenantId: ""));
            }
        };

        var outcome = TopologyCascade.CascadeAll(
            _index, _graph, _clusters, new[] { "swept" }, "", apply: true, watchNs: "test");

        Assert.True(seamFired >= 1, "the cluster pre-pin seam never fired");
        Assert.Equal(1, outcome.IdsUnsettled);
        Assert.Contains(_clusters.GetClusterMembershipsForEntry("swept", tenantId: ""),
            m => m.ClusterId == "pin-cluster");
    }
}
