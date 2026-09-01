using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// Deterministic regression controls for the round-14 review findings: the in-flight gate is
/// keyed by the durable store alone (two independently loaded stacks over one store contend);
/// an all-screened retry releases a prior's claims before retracting the record; generation 0
/// is a real (legacy) generation and NULL is the absence token; undo strictly re-reads before
/// its first side effect; checksum verification fails closed on an unreadable companion; and a
/// failed debounced timer write is retained so TryFlush cannot vouch over a hole.
/// </summary>
public class Round14RegressionTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly ClusterManager _clusters;
    private readonly LifecycleEngine _lifecycle;
    private readonly AccretionScanner _scanner;

    public Round14RegressionTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"round14_test_{Guid.NewGuid():N}");
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

    private string SeedAndDetect(AccretionScanner scanner)
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));
        return scanner.ScanNamespace("test", tenantId: "").NewCollapses[0].CollapseId;
    }

    /// <summary>
    /// Finding 1: the in-flight gate keys on the DURABLE STORE alone. A second, independently
    /// loaded stack over the same directory — its own PersistenceManager, CognitiveIndex,
    /// ClusterManager, scanner: the exact shape whose in-memory state the first stack's
    /// witness CAS cannot fence — is refused mid-execution instead of undoing and deleting the
    /// receipt out from under the executor.
    /// </summary>
    [Fact]
    public void UndoFromIndependentStack_OverSameStore_MidExecution_IsRefused()
    {
        var collapseId = SeedAndDetect(_scanner);

        // The second stack addresses the store through an ALIAS SPELLING — a trailing
        // separator GetFullPath legitimately preserves — so this also pins the identity
        // canonicalization: an alias that split the store key would bypass the gate. (Stack 2
        // performs no entry writes; a second full-snapshot writing stack over one store is
        // outside the storage design's single-writing-stack assumption.)
        using var persistence2 = new PersistenceManager(
            _testDataPath + Path.DirectorySeparatorChar, debounceMs: 60_000);
        Assert.Equal(((IStorageProvider)_persistence).StoreIdentity,
            ((IStorageProvider)persistence2).StoreIdentity);
        using var index2 = new CognitiveIndex(persistence2);
        var clusters2 = new ClusterManager(index2, persistence2);
        var lifecycle2 = new LifecycleEngine(index2);
        var scanner2 = new AccretionScanner(index2, persistence2);

        string? interleavedUndo = null;
        _scanner.OnBeforeArchiveCas = () =>
        {
            _scanner.OnBeforeArchiveCas = null;
            interleavedUndo = scanner2.UndoCollapse(collapseId, lifecycle2, clusters2, tenantId: "");
        };

        var executed = _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");

        Assert.StartsWith("Collapsed 4", executed);
        Assert.NotNull(interleavedUndo);
        Assert.StartsWith("Error:", interleavedUndo);
        Assert.Contains("already being executed or undone", interleavedUndo);
        Assert.True(_persistence.TryReadCollapseRecord(collapseId, out var record));
        Assert.NotNull(record);
    }

    /// <summary>
    /// Finding 2: an all-screened retry must RELEASE a prior attempt's claims before
    /// retracting the record. Repro: a partial collapse archives members; a public
    /// UpdateCluster evicts the memberships; twins screen re-admission; the retry judges every
    /// member unadmitted. The old branch deleted the record and stranded the archived members
    /// forever — now they are restored by the release pass before the record retires.
    /// </summary>
    [Fact]
    public void ExecuteCollapse_AllScreenedRetryWithAppliedClaims_RestoresBeforeRetracting()
    {
        var collapseId = SeedAndDetect(_scanner);

        // Partial collapse: "d" is gone, so a, b, c are archived under claims and the attempt
        // errors with the record durable.
        _index.Delete("d");
        Assert.StartsWith("Error:", _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));
        foreach (var id in new[] { "a", "b", "c" })
            Assert.Equal("archived", _index.Get(id, "test", tenantId: "")!.LifecycleState);
        var record = Assert.Single(_scanner.GetCollapseHistory("test", tenantId: ""));

        // Any caller evicts the memberships (the undo is NOT the only evictor)...
        Assert.DoesNotContain("Error:",
            _clusters.UpdateCluster(record.ClusterId, addIds: null,
                removeIds: new List<string> { "a", "b", "c", "d" }, label: null, tenantId: ""));
        // ...and twins appear so re-admission screens every member out.
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));
        foreach (var id in new[] { "a", "b", "c", "d" })
            _index.Upsert(new CognitiveEntry(id, new[] { 0f, 1f, 0f }, "other", lifecycleState: "ltm"));

        var retry = _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");

        Assert.Contains("admitted no members", retry);
        // The record retired — but only AFTER its claims were released: the archived members
        // are restored, not stranded.
        Assert.Empty(_scanner.GetCollapseHistory("test", tenantId: ""));
        foreach (var id in new[] { "a", "b", "c" })
            Assert.Equal("ltm", _index.Get(id, "test", tenantId: "")!.LifecycleState);
    }

    /// <summary>
    /// Finding 3: generation 0 is a REAL generation (legacy records deserialize at 0) and NULL
    /// is the absence token. An expected-absent conditional upsert must refuse a resident
    /// legacy record instead of overwriting it; CASing against generation 0 must address
    /// exactly that resident.
    /// </summary>
    [Fact]
    public void ConditionalUpsert_LegacyGenerationZero_IsNotAbsence()
    {
        var legacy = new CollapseRecord(
            "collapse:legacy", "cluster-l", "summary-l", "test",
            new List<string> { "m" }, new Dictionary<string, string>(), tenantId: "",
            generation: 0);
        Assert.True(_persistence.UpsertCollapseRecordSync(legacy));

        var intruder = new CollapseRecord(
            "collapse:legacy", "cluster-x", "summary-x", "test",
            new List<string> { "m2" }, new Dictionary<string, string>(), tenantId: "",
            generation: 1);

        // Expected-absent refuses the resident legacy record...
        Assert.Equal(CollapseRecordCas.GenerationMoved,
            _persistence.UpsertCollapseRecordSync(intruder, onlyIfGeneration: null));
        Assert.True(_persistence.TryReadCollapseRecord("collapse:legacy", out var resident));
        Assert.Equal("cluster-l", resident!.ClusterId);

        // ...while a CAS against generation 0 addresses exactly that resident.
        Assert.Equal(CollapseRecordCas.Applied,
            _persistence.UpsertCollapseRecordSync(intruder, onlyIfGeneration: 0));
        Assert.True(_persistence.TryReadCollapseRecord("collapse:legacy", out var replaced));
        Assert.Equal("cluster-x", replaced!.ClusterId);
    }

    /// <summary>
    /// Finding 4: undo strictly re-reads its record before the FIRST side effect. A tampered
    /// envelope (valid JSON, stale internal checksum) is served by the LENIENT boot load, but
    /// the undo must refuse on the strict read rather than restore members to attacker-chosen
    /// states and only then notice at the terminal delete.
    /// </summary>
    [Fact]
    public void UndoCollapse_TamperedRecord_IsRefusedBeforeAnySideEffect()
    {
        var collapseId = SeedAndDetect(_scanner);
        Assert.StartsWith("Collapsed 4", _scanner.ExecuteCollapse(
            collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: ""));
        _persistence.Flush();

        // Tamper the durable record: previous states now say "stm" (attacker-chosen), and the
        // envelope checksum is left stale.
        var historyPath = Path.Combine(_testDataPath, "_collapse_history.json");
        File.WriteAllText(historyPath, File.ReadAllText(historyPath).Replace("\"ltm\"", "\"stm\""));

        // A fresh stack boots off the LENIENT load — which serves the tampered record...
        using var persistence2 = new PersistenceManager(_testDataPath, debounceMs: 60_000);
        using var index2 = new CognitiveIndex(persistence2);
        var clusters2 = new ClusterManager(index2, persistence2);
        var lifecycle2 = new LifecycleEngine(index2);
        var scanner2 = new AccretionScanner(index2, persistence2);
        Assert.Single(scanner2.GetCollapseHistory("test", tenantId: ""));

        // ...but the undo's strict re-read refuses before anything is touched.
        var undo = scanner2.UndoCollapse(collapseId, lifecycle2, clusters2, tenantId: "");
        Assert.StartsWith("Error:", undo);
        Assert.Contains("strictly read", undo);
        foreach (var id in new[] { "a", "b", "c", "d" })
            Assert.Equal("archived", index2.Get(id, "test", tenantId: "")!.LifecycleState);
    }

    /// <summary>
    /// Finding 5: a checksum companion that EXISTS but cannot be read fails CLOSED. Holding it
    /// open exclusively must make the strict raw-array read refuse, not pass.
    /// </summary>
    [Fact]
    public void StrictRead_UnreadableChecksumCompanion_FailsClosed()
    {
        var historyPath = Path.Combine(_testDataPath, "_collapse_history.json");
        var record = new CollapseRecord(
            "collapse:legacy", "cluster-l", "summary-l", "test",
            new List<string> { "m" }, new Dictionary<string, string>(), tenantId: "");
        var arrayJson = JsonSerializer.Serialize(new List<CollapseRecord> { record },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(historyPath, arrayJson);
        var companion = historyPath + ".sha256";
        File.WriteAllText(companion, Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(arrayJson))));

        // Readable companion, matching: passes.
        Assert.True(_persistence.TryReadCollapseRecord("collapse:legacy", out var readable));
        Assert.NotNull(readable);

        // Companion held exclusively open: evidence exists and is unavailable — refuse.
        using (new FileStream(companion, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(_persistence.TryReadCollapseRecord("collapse:legacy", out _));
        }
    }

    /// <summary>
    /// Finding 6: a failed debounced TIMER write is retained, so a later TryFlush reports the
    /// hole instead of vouching over it. The namespace file is squatted by a directory, the
    /// timer fires and fails, and TryFlush must return false until the store heals.
    /// </summary>
    [Fact]
    public void TimerWriteFailure_IsRetained_SoTryFlushCannotVouchOverIt()
    {
        using var persistence = new PersistenceManager(
            Path.Combine(Path.GetTempPath(), $"round14_timer_{Guid.NewGuid():N}"), debounceMs: 30);
        try
        {
            // Squat the namespace file path so every write deterministically fails.
            var nsPath = Path.Combine(persistence.StoreIdentity, "squat.json");
            Directory.CreateDirectory(nsPath);

            persistence.ScheduleSave("squat", () => new NamespaceData
            {
                Entries = new List<CognitiveEntry> { new("e", new[] { 1f }, "squat") }
            });

            // Let the debounce fire and fail; the snapshot must be RETAINED, so the flush
            // reports the hole.
            Thread.Sleep(200);
            Assert.False(persistence.TryFlush());

            // Store heals: the retained snapshot commits and the flush vouches truthfully.
            Directory.Delete(nsPath);
            Assert.True(persistence.TryFlush());
            Assert.Single(persistence.LoadNamespace("squat").Entries);
        }
        finally
        {
            var dir = persistence.StoreIdentity;
            persistence.Dispose();
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}
