using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// Deterministic regression controls for the round-12 review findings: the archive CAS must
/// refuse a lifecycle ABA, an inert later CLAIM must not block the real owner's undo, the
/// per-collapse in-flight gate must span every scanner over one store, and an already-existing
/// cluster under a collapse's id must be identity-checked before adoption. (The occupancy-pin
/// control lives with the cascade tests; the storage-transaction control lives with the SQLite
/// provider tests.)
/// </summary>
public class Round12RegressionTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly ClusterManager _clusters;
    private readonly LifecycleEngine _lifecycle;
    private readonly AccretionScanner _scanner;

    public Round12RegressionTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"round12_test_{Guid.NewGuid():N}");
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

    private string SeedAndDetect()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));
        return _scanner.ScanNamespace("test", tenantId: "").NewCollapses[0].CollapseId;
    }

    /// <summary>
    /// Finding 1: the plan records the member's lifecycle WITNESS, not just its state, and the
    /// archive CAS compares both. A member cycled <c>ltm → stm → ltm</c> between the plan and
    /// the archive stands in the planned state with a moved witness — the exact ABA a
    /// state-only compare absorbed, archiving an occupation the plan never examined.
    /// </summary>
    [Fact]
    public void ExecuteCollapse_LifecycleAbaBetweenPlanAndArchive_RefusesThatMember()
    {
        var collapseId = SeedAndDetect();

        _scanner.OnBeforeArchiveCas = () =>
        {
            _scanner.OnBeforeArchiveCas = null;
            // Same state as planned, different witness: away and back again.
            Assert.DoesNotContain("Error:", _lifecycle.PromoteMemory("a", "stm", "test", tenantId: ""));
            Assert.DoesNotContain("Error:", _lifecycle.PromoteMemory("a", "ltm", "test", tenantId: ""));
        };

        var first = _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");

        Assert.StartsWith("Error:", first);
        Assert.Contains("a: Error: Entry 'a' changed concurrently and was not archived", first);
        // The cycled member was left alone; the untouched members were archived from their
        // exact planned witnesses.
        Assert.Equal("ltm", _index.Get("a", "test", tenantId: "")!.LifecycleState);
        Assert.Equal("archived", _index.Get("b", "test", tenantId: "")!.LifecycleState);
        Assert.Equal("archived", _index.Get("c", "test", tenantId: "")!.LifecycleState);
        Assert.Equal("archived", _index.Get("d", "test", tenantId: "")!.LifecycleState);

        // A retry re-plans the refused member from its fresh witness and completes.
        var retry = _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");
        Assert.StartsWith("Collapsed 4", retry);
        Assert.Equal("archived", _index.Get("a", "test", tenantId: "")!.LifecycleState);
    }

    /// <summary>
    /// Finding 2: a later record's CLAIM is not a later record's ARCHIVE. A claims-ahead
    /// receipt persisted for an archive that never fired is inert — the reserved revision was
    /// never installed anywhere — and the real owner's undo must restore its member rather
    /// than defer to the phantom. Genuine later work needs no claim-set deference at all: it
    /// moved the member's witness, and the owner's restore CAS refuses by itself.
    /// </summary>
    [Fact]
    public void UndoCollapse_InertLaterClaim_DoesNotBlockTheOwnersRestore()
    {
        var collapseId = SeedAndDetect();
        var executed = _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");
        Assert.StartsWith("Collapsed 4", executed);
        var owner = Assert.Single(_scanner.GetCollapseHistory("test", tenantId: ""));

        // A strictly-later record claiming "a" with a reservation whose archive never fired.
        var inertClaim = _index.ReserveLifecycleRevision().Value;
        var later = new CollapseRecord(
            "collapse:test:zzlater", "accretion:collapse:test:zzlater:0000nonce", "summary:zzlater",
            "test", new List<string> { "a" },
            new Dictionary<string, string> { ["a"] = "ltm" },
            owner.CollapsedAt.AddMinutes(1), tenantId: "",
            appliedLifecycleRevisions: new Dictionary<string, long> { ["a"] = inertClaim },
            expectedLifecycleRevisions: new Dictionary<string, long> { ["a"] = 0 });
        Assert.True(_persistence.UpsertCollapseRecordSync(later));

        // A fresh scanner over the same store sees both records — the shape a restart (or a
        // second component) produces. Its undo of the OWNER must restore every member the
        // owner archived, "a" included: the later claim matches no installed witness.
        var restarted = new AccretionScanner(_index, _persistence);
        var undo = restarted.UndoCollapse(collapseId, _lifecycle, _clusters, tenantId: "");
        Assert.StartsWith("Reversed", undo);
        Assert.Equal("ltm", _index.Get("a", "test", tenantId: "")!.LifecycleState);
        Assert.Equal("ltm", _index.Get("b", "test", tenantId: "")!.LifecycleState);
        Assert.Equal("ltm", _index.Get("c", "test", tenantId: "")!.LifecycleState);
        Assert.Equal("ltm", _index.Get("d", "test", tenantId: "")!.LifecycleState);
    }

    /// <summary>
    /// Finding 3: the per-collapse in-flight gate is keyed by STORE, not by scanner instance.
    /// A second scanner over the same index and store, asked to undo while the first is
    /// mid-execution — after the claims persisted, before the archives — must be refused; a
    /// scanner-local gate let it restore nothing, DELETE the durable record, and leave the
    /// first scanner's archives standing with no receipt anywhere.
    /// </summary>
    [Fact]
    public void UndoCollapse_SecondScannerOverSameStore_MidExecution_IsRefused()
    {
        var collapseId = SeedAndDetect();
        var second = new AccretionScanner(_index, _persistence);

        string? interleavedUndo = null;
        _scanner.OnBeforeArchiveCas = () =>
        {
            _scanner.OnBeforeArchiveCas = null;
            interleavedUndo = second.UndoCollapse(collapseId, _lifecycle, _clusters, tenantId: "");
        };

        var executed = _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");

        Assert.StartsWith("Collapsed 4", executed);
        Assert.NotNull(interleavedUndo);
        Assert.StartsWith("Error:", interleavedUndo);
        Assert.Contains("already being executed or undone", interleavedUndo);
        // The execution's record survived the interleaving attempt, archives receipted.
        var record = Assert.Single(_persistence.LoadCollapseHistory());
        Assert.Equal(collapseId, record.CollapseId);
        Assert.Equal(4, record.AppliedLifecycleRevisions!.Count);
    }

    /// <summary>
    /// Delegates everything to a real provider, injecting the three storage faults the
    /// round-12 adversarial pass proved load-bearing: a strict read that fails (existence
    /// unknown), a generation-compared delete that refuses after the record advanced, and an
    /// unconditional delete that fails.
    /// </summary>
    private sealed class FaultInjectingProvider : IStorageProvider
    {
        private readonly IStorageProvider _inner;
        public int FailStrictReads { get; set; }
        public int RefuseConditionalDeletesAdvancingRecord { get; set; }
        public int FailUnconditionalDeletes { get; set; }
        public int FailConditionalDeletes { get; set; }
        /// <summary>1-based index of the conditional-upsert call at which a racing undoer's
        /// terminal delete lands FIRST (the executor's calls run intent=1, claims=2, commit=3).</summary>
        public int UndoerDeleteBeforeConditionalUpsert { get; set; }
        /// <summary>1-based index of the conditional-upsert call that reports StoreFailed.</summary>
        public int StoreFailConditionalUpsert { get; set; }
        public int FailTryFlushes { get; set; }
        private int _conditionalUpsertCalls;
        public FaultInjectingProvider(IStorageProvider inner) => _inner = inner;

        public bool TryReadCollapseRecord(string collapseId, out CollapseRecord? record)
        {
            if (FailStrictReads > 0)
            {
                FailStrictReads--;
                record = null;
                return false;
            }
            return _inner.TryReadCollapseRecord(collapseId, out record);
        }

        public CollapseRecordCas DeleteCollapseRecordSync(string collapseId, long onlyIfGeneration)
        {
            if (FailConditionalDeletes > 0)
            {
                FailConditionalDeletes--;
                return CollapseRecordCas.StoreFailed;
            }
            if (RefuseConditionalDeletesAdvancingRecord > 0)
            {
                RefuseConditionalDeletesAdvancingRecord--;
                // Simulate a concurrent executor persisting between this undo's read and its
                // delete: the durable record advances a generation, and the compare refuses.
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

        public bool DeleteCollapseRecordSync(string collapseId)
        {
            if (FailUnconditionalDeletes > 0)
            {
                FailUnconditionalDeletes--;
                return false;
            }
            return _inner.DeleteCollapseRecordSync(collapseId);
        }

        public CollapseRecordCas UpsertCollapseRecordSync(CollapseRecord record, long? onlyIfGeneration)
        {
            _conditionalUpsertCalls++;
            if (StoreFailConditionalUpsert == _conditionalUpsertCalls)
                return CollapseRecordCas.StoreFailed;
            if (UndoerDeleteBeforeConditionalUpsert == _conditionalUpsertCalls && onlyIfGeneration is not null)
            {
                // The racing undoer's terminal delete lands at the generation the executor is
                // about to commit against — the exact interleaving a read-based verify missed.
                _inner.DeleteCollapseRecordSync(record.CollapseId, onlyIfGeneration.Value);
            }
            return _inner.UpsertCollapseRecordSync(record, onlyIfGeneration);
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

        public bool TryReadCollapseHistory(out List<CollapseRecord> records) => _inner.TryReadCollapseHistory(out records);
        public bool UpsertCollapseRecordSync(CollapseRecord record) => _inner.UpsertCollapseRecordSync(record);
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
    /// Adversarial finding: an UNVERIFIABLE store is not an ABSENT record. A durable-phase
    /// commit that cannot reach the store must roll back this attempt's archives (existence
    /// unknown — fail toward safety) but keep the receipt, in memory and on disk: erasing it
    /// made the next retry find no prior, mint a fresh lineage, and OVERWRITE the durable
    /// record — destroying earlier attempts' archive claims for good.
    /// </summary>
    [Fact]
    public void ExecuteCollapse_CommitStoreFails_RollsBackButPreservesReceiptAndLineage()
    {
        // Call 4 is the durable phase commit (intent=1, created-instance=2, claims=3).
        var faulty = new FaultInjectingProvider(_persistence) { StoreFailConditionalUpsert = 4 };
        var scanner = new AccretionScanner(_index, faulty);

        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));
        var collapseId = scanner.ScanNamespace("test", tenantId: "").NewCollapses[0].CollapseId;

        var result = scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");
        Assert.StartsWith("Error:", result);
        Assert.Contains("could not COMMIT", result);

        // Archives rolled back — existence of the record was unknown, so nothing may stand.
        foreach (var id in new[] { "a", "b", "c", "d" })
            Assert.Equal("ltm", _index.Get(id, "test", tenantId: "")!.LifecycleState);

        // The receipt survived in BOTH stores, lineage intact.
        var inMemory = Assert.Single(scanner.GetCollapseHistory("test", tenantId: ""));
        Assert.True(_persistence.TryReadCollapseRecord(collapseId, out var onDisk));
        Assert.NotNull(onDisk);
        Assert.Equal(inMemory.ClusterId, onDisk!.ClusterId);

        // The retry re-plans from fresh witnesses and completes under the SAME lineage.
        var retry = scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");
        Assert.StartsWith("Collapsed 4", retry);
        Assert.Equal(inMemory.ClusterId,
            Assert.Single(scanner.GetCollapseHistory("test", tenantId: "")).ClusterId);
    }

    /// <summary>
    /// Adversarial finding: the undo's terminal record delete must be a generation compare —
    /// a record that advanced between the undo's read and its delete carries claims the undo
    /// never processed. On refusal the undo re-reads the fresh record, re-runs the reversal
    /// against it, and only then retires it.
    /// </summary>
    [Fact]
    public void UndoCollapse_RecordAdvancesUnderTheDelete_ReReadsAndCompletes()
    {
        var faulty = new FaultInjectingProvider(_persistence) { RefuseConditionalDeletesAdvancingRecord = 1 };
        var scanner = new AccretionScanner(_index, faulty);

        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));
        var collapseId = scanner.ScanNamespace("test", tenantId: "").NewCollapses[0].CollapseId;
        Assert.StartsWith("Collapsed 4", scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));

        var undo = scanner.UndoCollapse(collapseId, _lifecycle, _clusters, tenantId: "");

        Assert.StartsWith("Reversed", undo);
        foreach (var id in new[] { "a", "b", "c", "d" })
            Assert.Equal("ltm", _index.Get(id, "test", tenantId: "")!.LifecycleState);
        Assert.True(_persistence.TryReadCollapseRecord(collapseId, out var remaining));
        Assert.Null(remaining);
    }

    /// <summary>
    /// Adversarial finding: the undo's restores ride the DEBOUNCED entry-write stream while
    /// its record delete commits synchronously — so the record must not retire until the
    /// restores are durable. A crash image taken right after "Reversed" must show the members
    /// restored and the record gone, never archived-with-no-record.
    /// </summary>
    [Fact]
    public void UndoCollapse_CrashImageAfterReversal_ShowsMembersRestored()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));
        var collapseId = _scanner.ScanNamespace("test", tenantId: "").NewCollapses[0].CollapseId;
        Assert.StartsWith("Collapsed 4", _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));

        Assert.StartsWith("Reversed", _scanner.UndoCollapse(collapseId, _lifecycle, _clusters, tenantId: ""));

        // The 60-second debounce means nothing else has flushed; whatever is durable NOW is
        // what a crash at this instant leaves behind.
        var imagePath = Path.Combine(Path.GetTempPath(), $"round12_image_{Guid.NewGuid():N}");
        try
        {
            CopyDirectory(_testDataPath, imagePath);
            using var imagePersistence = new PersistenceManager(imagePath, debounceMs: 50);
            using var imageIndex = new CognitiveIndex(imagePersistence);
            var imageScanner = new AccretionScanner(imageIndex, imagePersistence);

            foreach (var id in new[] { "a", "b", "c", "d" })
                Assert.Equal("ltm", imageIndex.Get(id, "test", tenantId: "")!.LifecycleState);
            Assert.Empty(imageScanner.GetCollapseHistory("test", tenantId: ""));
        }
        finally
        {
            if (Directory.Exists(imagePath))
                Directory.Delete(imagePath, true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    /// <summary>
    /// Adversarial finding: the all-screened branch must not discard record-retraction
    /// failures — a dismissal consulting only memory would retire the proposal while a
    /// durable record survives. On failure the record stays visible (blocking dismissal);
    /// once the store recovers, the retraction completes and dismissal proceeds.
    /// </summary>
    [Fact]
    public void ExecuteCollapse_AllScreenedRetractFailure_KeepsRecordAndBlocksDismissal()
    {
        var faulty = new FaultInjectingProvider(_persistence) { FailConditionalDeletes = 1 };
        var scanner = new AccretionScanner(_index, faulty);

        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));
        var collapseId = scanner.ScanNamespace("test", tenantId: "").NewCollapses[0].CollapseId;
        foreach (var id in new[] { "a", "b", "c", "d" })
            _index.Upsert(new CognitiveEntry(id, new[] { 0f, 1f, 0f }, "other", lifecycleState: "ltm"));

        var first = scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");
        Assert.StartsWith("Error:", first);
        Assert.Contains("could not be retracted", first);
        Assert.Single(scanner.GetCollapseHistory("test", tenantId: ""));

        var dismiss = scanner.DismissCollapse(collapseId, tenantId: "", clusters: _clusters);
        Assert.StartsWith("Error:", dismiss);
        Assert.Contains("Undo it before dismissing", dismiss);

        // Store recovered: the retry retracts cleanly, and the proposal becomes dismissable.
        var retry = scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");
        Assert.Contains("admitted no members", retry);
        Assert.Empty(scanner.GetCollapseHistory("test", tenantId: ""));
        Assert.StartsWith("Dismissed", scanner.DismissCollapse(collapseId, tenantId: "", clusters: _clusters));
    }

    /// <summary>
    /// Finding 4: the already-exists branch verifies the resident cluster's IDENTITY before
    /// adopting it. A same-id cluster in another namespace — the id is public knowledge once
    /// the record is durable — must refuse the retry rather than split the collapse across
    /// namespaces: StoreSummary would write the summary where the resident lives while the
    /// record, and therefore the undo, names the collapse's own namespace, orphaning it.
    /// </summary>
    [Fact]
    public void ExecuteCollapse_ClusterIdOccupiedByForeignNamespace_RefusesAdoption()
    {
        var collapseId = SeedAndDetect();

        // First attempt fails partway (one member gone), leaving a durable record that names
        // the minted cluster id — from here on the id is knowable outside this lineage.
        _index.Delete("d");
        var first = _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");
        Assert.StartsWith("Error:", first);
        var record = Assert.Single(_scanner.GetCollapseHistory("test", tenantId: ""));
        var clusterId = record.ClusterId;

        // A foreign actor tears our cluster down and recreates the id in ANOTHER namespace.
        // ("d" is a member too: a dangling id is unambiguous, so creation admitted it.)
        Assert.DoesNotContain("Error:",
            _clusters.UpdateCluster(clusterId, addIds: null,
                removeIds: new List<string> { "a", "b", "c", "d" }, label: null, tenantId: ""));
        Assert.Equal(EmptyClusterRemoval.Removed, _clusters.RemoveClusterIfEmpty(clusterId, tenantId: ""));
        _index.Upsert(new CognitiveEntry("x", new[] { 0f, 1f, 0f }, "other", lifecycleState: "ltm"));
        Assert.DoesNotContain("Error:",
            _clusters.CreateCluster(clusterId, "other", new List<string> { "x" }, "foreign", tenantId: ""));

        // Retry (member restored). The resident under our id is not ours: refuse, adopt
        // nothing, write nothing into the foreign namespace.
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));
        var retry = _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");
        Assert.StartsWith("Error:", retry);
        Assert.Contains("occupied by a different cluster incarnation", retry);
        Assert.Null(_index.Get($"summary:{clusterId}", "other", tenantId: ""));

        // With the squatter gone the SAME lineage id completes — a retry reuses its record's
        // cluster id rather than minting a new incarnation.
        Assert.DoesNotContain("Error:",
            _clusters.UpdateCluster(clusterId, addIds: null,
                removeIds: new List<string> { "x" }, label: null, tenantId: ""));
        Assert.Equal(EmptyClusterRemoval.Removed, _clusters.RemoveClusterIfEmpty(clusterId, tenantId: ""));
        var completed = _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");
        Assert.StartsWith("Collapsed 4", completed);
        Assert.Equal(clusterId, Assert.Single(_scanner.GetCollapseHistory("test", tenantId: "")).ClusterId);
    }
}
