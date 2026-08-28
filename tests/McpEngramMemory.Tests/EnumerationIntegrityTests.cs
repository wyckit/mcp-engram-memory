using System.Collections.Concurrent;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// Two properties of the full-load sweep that no test of a healthy provider can reach, because both
/// only appear when the enumeration behind it misbehaves.
///
/// The first is that a listing failure used to be spelled the same way as an empty store. Every
/// provider caught its error and returned an empty list, <c>LoadAll</c> marked that empty sweep
/// complete, and the completion was cached — so one transient failure made the store look
/// permanently empty. The security consequence is worse than the availability one: an unlisted
/// persisted twin makes a duplicated id look unique, so the ACL-blind, tenant-wide duplicate test
/// that topology fails closed on passes instead. That is the same fail-open outcome as a lost
/// candidate placement, reached from the persistence side.
///
/// The second is the cost of a cold burst. Completion is published only after the sweep, so before
/// this every cold caller ran its own directory walk or <c>SELECT DISTINCT</c>; "once per process"
/// was true only of a store that was never opened concurrently.
///
/// The counting half is driven through hand-rolled in-memory providers rather than a temp
/// directory: the enumeration counter and the injected failure have to live inside the provider,
/// and neither is reachable by pointing a real backend at a disk.
///
/// The JSON provider — the default one — gets its own on-disk half, because there the failure is a
/// property of a real directory and cannot be stubbed honestly. Its listing failure wears the same
/// shape as an empty store twice over: a missing path is both what a never-created store looks like
/// and what a moved, unmounted or ACL-revoked one looks like, and only a real directory taken away
/// from a live provider tells those apart. Each of those tests takes a per-run GUID directory and
/// deletes it.
/// </summary>
public class EnumerationIntegrityTests
{
    // A real tenant, not the legacy "" partition: the candidate index is the structure whose
    // completeness the sweep is responsible for, and an identified tenant exercises it alone.
    private const string Tenant = "t1";

    private static CognitiveEntry Entry(string id, string ns)
        => new(id, [1f, 0f], ns, tenantId: Tenant);

    private static void AssertSameSet(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(
            expected.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            actual.OrderBy(s => s, StringComparer.Ordinal).ToList());

    private static NamespaceStore StoreOver(IStorageProvider provider)
        => new(provider, new BM25Index());

    // ── Failure must not be recorded as emptiness ──

    /// <summary>
    /// The reviewer's reproduction. A provider that fails once and then recovers used to leave the
    /// store empty forever: the failed sweep returned an empty list, <c>LoadAll</c> published a
    /// completion for it, and every later call was answered from that completion without ever going
    /// back to the provider.
    /// </summary>
    [Fact]
    public void EnumerationFailsThenRecovers_TheNextSweepSeesEveryPersistedNamespace()
    {
        using var provider = new FlakyEnumerationProvider(failuresBeforeSuccess: 1);
        provider.Seed("ns-a", [Entry("twin", "ns-a")]);
        provider.Seed("ns-b", [Entry("twin", "ns-b")]);
        var store = StoreOver(provider);

        // Refused, not silently empty. The distinction is the whole finding: an empty result is
        // indistinguishable from a store with nothing in it, and the caller cannot tell which it got.
        Assert.Throws<NamespaceEnumerationException>(() => store.LoadAll());

        store.LoadAll();
        AssertSameSet(["ns-a", "ns-b"], store.GetCandidateNamespaces("twin", Tenant));
    }

    /// <summary>
    /// The ledger behind the recovery above: a failed sweep publishes nothing, so the next caller
    /// goes back to the provider, and a sweep that genuinely enumerated publishes once and is not
    /// paid for again. Asserting on the enumeration counter rather than on the visible namespaces is
    /// what separates "retried" from "happened to be resident already".
    /// </summary>
    [Fact]
    public void FailedSweep_PublishesNoCompletion_AndASucceedingOneIsCached()
    {
        using var provider = new FlakyEnumerationProvider(failuresBeforeSuccess: 1);
        provider.Seed("ns-a", [Entry("only", "ns-a")]);
        var store = StoreOver(provider);

        Assert.Throws<NamespaceEnumerationException>(() => store.LoadAll());
        Assert.Equal(1, provider.Enumerations);

        // Not cached: this call has to re-enumerate rather than trust a completion recorded over an
        // error. A store that answered from the failed sweep would leave this at 1.
        store.LoadAll();
        Assert.Equal(2, provider.Enumerations);
        Assert.Equal(new[] { "ns-a" }, store.GetCandidateNamespaces("only", Tenant));

        // Now cached: the retry did not turn every later call into an enumeration.
        store.LoadAll();
        Assert.Equal(2, provider.Enumerations);
    }

    /// <summary>
    /// The same failure stated as its security consequence rather than as an availability one. While
    /// the enumeration is failing, the tenant-wide duplicate test must refuse to answer — answering
    /// "1" over a store it could not list is the fail-open outcome, because the caller then treats
    /// an ambiguous bare id as attributable and lets topology mutate a node shared with a twin it
    /// never saw. After recovery the twin is visible and the id counts as ambiguous.
    /// </summary>
    [Fact]
    public void UnlistablePersistedTwin_IsNeverCountedAsAUniqueId()
    {
        using var provider = new FlakyEnumerationProvider(failuresBeforeSuccess: 1);
        provider.Seed("ns-a", [Entry("twin", "ns-a")]);
        provider.Seed("ns-b", [Entry("twin", "ns-b")]);
        using var index = new CognitiveIndex(provider);

        // Neither 1 nor 0 — an id whose namespaces cannot be established is not attributable, and
        // the count is exactly the predicate topology fails closed on.
        Assert.Throws<NamespaceEnumerationException>(
            () => { index.CountNamespacesContaining("twin", tenantId: Tenant); });

        Assert.Equal(2, index.CountNamespacesContaining("twin", tenantId: Tenant));
        // Ambiguous, so the bare-id get refuses rather than picking a twin.
        Assert.Null(index.GetForTenant("twin", tenantId: Tenant));
    }

    // ── Emptiness must stay ordinary ──

    /// <summary>
    /// Fail-closed on a failed listing must not turn an ordinary empty store into an error, and must
    /// not stop it being cached: an empty sweep genuinely enumerated, so it publishes completion like
    /// any other.
    /// </summary>
    [Fact]
    public void GenuinelyEmptyStore_SweepsCleanlyAndIsCached()
    {
        using var provider = new FlakyEnumerationProvider(failuresBeforeSuccess: 0);
        var store = StoreOver(provider);

        store.LoadAll();
        Assert.Equal(1, provider.Enumerations);
        Assert.Empty(store.GetCandidateNamespaces("absent", Tenant));

        store.LoadAll();
        Assert.Equal(1, provider.Enumerations);
    }

    /// <summary>
    /// The same distinction at the JSON provider's own boundary, on real disk: a data directory with
    /// no namespace files lists empty rather than throwing, and one with a namespace file lists it.
    /// Without the second half the first proves nothing — a provider that always returned empty
    /// would pass it.
    /// </summary>
    [Fact]
    public void JsonProvider_EmptyDataDirectoryListsEmpty_AndAPopulatedOneListsItsNamespaces()
    {
        using var dir = new TempDataDirectory();
        using var provider = new PersistenceManager(dir.Root, debounceMs: 50);

        Assert.Empty(provider.GetPersistedNamespaces());

        provider.SaveNamespaceSync("real", new NamespaceData { Entries = [Entry("e1", "real")] });
        Assert.Equal(new[] { "real" }, provider.GetPersistedNamespaces());
    }

    /// <summary>
    /// The other side of the decision the JSON provider has to make, and the reason its
    /// <c>Directory.Exists</c> pre-check existed at all: a genuinely fresh store has no data
    /// directory yet and must still boot. The constructor creates it, which is what makes the
    /// first-run enumeration an ordinary successful one that finds nothing rather than a refusal —
    /// and, in the same stroke, what makes a later absence unambiguously the unavailable case.
    /// </summary>
    [Fact]
    public void JsonProvider_FirstRunWithNoDataDirectoryYet_EnumeratesSuccessfullyEmpty()
    {
        using var dir = new TempDataDirectory(create: false);
        Assert.False(Directory.Exists(dir.Root));

        using var provider = new PersistenceManager(dir.Root, debounceMs: 50);

        Assert.True(Directory.Exists(dir.Root));
        Assert.Empty(provider.GetPersistedNamespaces());
    }

    // ── An unavailable directory is not an empty one ──

    /// <summary>
    /// The reviewer's reproduction, against the default provider. A data directory that goes away
    /// under a running process — moved aside here, an unmounted share or a revoked ACL in the field
    /// — used to be answered by the <c>Directory.Exists</c> pre-check as an empty store: the exact
    /// fail-open the database providers were fixed for, reached by a different shape. Absence has to
    /// refuse while the directory is unavailable, and the recovered lookup has to see the namespace
    /// again, because a completion published over the first answer would hide it for the life of the
    /// process.
    /// </summary>
    [Fact]
    public void JsonProvider_DataDirectoryTakenAway_RefusesToEnumerate_AndTheRestoredLookupSeesIt()
    {
        using var dir = new TempDataDirectory();
        using var provider = new PersistenceManager(dir.Root, debounceMs: 50);
        provider.SaveNamespaceSync("ns-a", new NamespaceData { Entries = [Entry("twin", "ns-a")] });

        dir.MakeUnavailable();
        Assert.Throws<NamespaceEnumerationException>(() => provider.GetPersistedNamespaces());

        dir.Restore();
        Assert.Equal(new[] { "ns-a" }, provider.GetPersistedNamespaces());
    }

    /// <summary>
    /// The same outage stated as its security consequence rather than its availability one. Two of
    /// the tenant's namespaces hold the same bare id, so the id is unattributable and topology must
    /// fail closed on it. An enumeration that reported an unavailable directory as empty would leave
    /// the persisted twin unseen and the count at 1 — an attributable id, free to be mutated through
    /// the graph node it silently shares with the twin. Refusing during the outage and counting 2
    /// after it is the only pair of answers that is never fail-open.
    /// </summary>
    [Fact]
    public void JsonProvider_PersistedTwinHiddenByAnOutage_IsNeverCountedAsAUniqueId()
    {
        using var dir = new TempDataDirectory();
        using var provider = new PersistenceManager(dir.Root, debounceMs: 50);
        provider.SaveNamespaceSync("ns-a", new NamespaceData { Entries = [Entry("twin", "ns-a")] });
        provider.SaveNamespaceSync("ns-b", new NamespaceData { Entries = [Entry("twin", "ns-b")] });
        using var index = new CognitiveIndex(provider);

        dir.MakeUnavailable();

        // Neither 1 nor 0 — an id whose namespaces cannot be established is not attributable, and
        // the count is exactly the predicate topology fails closed on.
        Assert.Throws<NamespaceEnumerationException>(
            () => { index.CountNamespacesContaining("twin", tenantId: Tenant); });

        dir.Restore();
        Assert.Equal(2, index.CountNamespacesContaining("twin", tenantId: Tenant));
        // Ambiguous, so the bare-id get refuses rather than picking a twin.
        Assert.Null(index.GetForTenant("twin", tenantId: Tenant));
    }

    /// <summary>
    /// The completion ledger of <see cref="EnumerationIntegrityTests.FailedSweep_PublishesNoCompletion_AndASucceedingOneIsCached"/>,
    /// carried to the default provider. Nothing is resident before the first sweep, so seeing both
    /// namespaces after the recovery is itself the proof that the failed sweep published nothing and
    /// the next caller went back to disk; a completion recorded over the failure would have left the
    /// twin permanently invisible.
    ///
    /// The last step is the converse, and needs no counter to state it: a sweep that genuinely
    /// enumerated is answered from the completion, so taking the directory away again is not even
    /// noticed. A store that re-listed on every call would throw there instead.
    /// </summary>
    [Fact]
    public void JsonProvider_FailedSweep_PublishesNoCompletion_AndASucceedingOneIsCached()
    {
        using var dir = new TempDataDirectory();
        using var provider = new PersistenceManager(dir.Root, debounceMs: 50);
        provider.SaveNamespaceSync("ns-a", new NamespaceData { Entries = [Entry("twin", "ns-a")] });
        provider.SaveNamespaceSync("ns-b", new NamespaceData { Entries = [Entry("twin", "ns-b")] });
        var store = StoreOver(provider);

        dir.MakeUnavailable();
        Assert.Throws<NamespaceEnumerationException>(() => store.LoadAll());
        // The refusal came before any namespace was materialized, so the store is untouched rather
        // than half-swept — there is no partial view for the next caller to mistake for a complete one.
        Assert.Empty(store.GetCandidateNamespaces("twin", Tenant));

        dir.Restore();
        store.LoadAll();
        AssertSameSet(["ns-a", "ns-b"], store.GetCandidateNamespaces("twin", Tenant));

        dir.MakeUnavailable();
        store.LoadAll();
        AssertSameSet(["ns-a", "ns-b"], store.GetCandidateNamespaces("twin", Tenant));
    }

    // ── One enumeration for a cold burst ──

    /// <summary>
    /// Concurrent cold callers must coalesce onto one sweep. The gate is what makes the assertion
    /// meaningful: it holds the first enumeration open, so every other caller is guaranteed to arrive
    /// while the sweep is still unpublished — the exact state in which an un-single-flighted
    /// implementation runs its own directory walk. Without the gate the leader could finish first and
    /// the others would find the completion already published, which is indistinguishable from
    /// single-flighting.
    ///
    /// The assertion is the enumeration counter, never elapsed time. The wait before releasing is a
    /// window, not a measurement: it gives a broken implementation as long as it needs to run the
    /// enumerations this test says it must not run.
    ///
    /// Dedicated threads (LongRunning) rather than pooled ones, for the reason the retirement storm
    /// in <see cref="CandidateIndexConcurrencyTests"/> gives: every caller parks — first on the
    /// barrier, then inside the sweep — and a thread pool that has to grow to cover them injects
    /// threads on a hill-climbing delay.
    /// </summary>
    [Fact]
    public async Task ConcurrentColdCallers_ShareASingleEnumeration()
    {
        const int callers = 4;

        using var provider = new GatedEnumerationProvider(expectedConcurrentEnumerations: callers);
        provider.Seed("ns-a", [Entry("needle", "ns-a")]);
        provider.Seed("ns-b", [Entry("needle", "ns-b")]);
        var store = StoreOver(provider);

        using var start = new Barrier(callers);
        var seen = new IReadOnlyList<string>[callers];
        var workers = new Task[callers];

        for (int i = 0; i < callers; i++)
        {
            int slot = i;
            workers[slot] = Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                store.LoadAll();
                seen[slot] = store.GetCandidateNamespaces("needle", Tenant);
            }, TaskCreationOptions.LongRunning);
        }

        Assert.True(provider.WaitForFirstEnumeration(TimeSpan.FromSeconds(30)),
            "no caller ever reached the enumeration, so the test never got into the state it exists to check");

        // Deliberately ignored: reaching the expected concurrency is the failure this test reports
        // through the counter below, and timing out is the pass. Waiting here only widens the window
        // in which a redundant sweep could have been observed.
        _ = provider.WaitForExpectedConcurrency(TimeSpan.FromSeconds(2));
        provider.ReleaseEnumerations();

        // Awaited rather than blocked on: this test parks every caller inside the provider gate, so
        // a blocking wait here is the deadlock shape the analyzer refuses. Awaiting also surfaces a
        // worker's own exception as itself instead of flattening it into a timed-out bool.
        try
        {
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            Assert.Fail("the cold callers did not finish; the sweep gate is likely deadlocked");
        }

        Assert.Equal(1, provider.Enumerations);

        // Completeness is what the single enumeration is traded against, so assert it in the same
        // test: every caller — leader and waiters alike — must come out of LoadAll seeing the whole
        // store, not just whatever the leader had materialized when it woke them.
        foreach (var result in seen)
            AssertSameSet(["ns-a", "ns-b"], result);
    }

    // ── Disk fixture ──

    /// <summary>
    /// A per-run data directory for the JSON provider, plus the outage the reviewer reproduced.
    ///
    /// The directory is MOVED aside rather than deleted, and that is the point of the fixture: the
    /// entries have to still exist on the other side of the outage, because the finding is about a
    /// persisted twin that a failed listing hides, not about one that was destroyed. Moving also
    /// reproduces the real event faithfully — an unmounted share or a revoked ACL leaves the data
    /// intact and only the listing impossible.
    ///
    /// Dispose removes both locations unconditionally, so an assertion that fails mid-outage still
    /// cleans up the moved-away copy.
    /// </summary>
    private sealed class TempDataDirectory : IDisposable
    {
        private readonly string _away;

        public string Root { get; }

        public TempDataDirectory(bool create = true)
        {
            Root = Path.Combine(Path.GetTempPath(), $"engram_enum_{Guid.NewGuid():N}");
            _away = Root + "_away";
            if (create)
                Directory.CreateDirectory(Root);
        }

        public void MakeUnavailable() => Directory.Move(Root, _away);

        public void Restore() => Directory.Move(_away, Root);

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
            if (Directory.Exists(_away))
                Directory.Delete(_away, recursive: true);
        }
    }

    // ── Providers ──

    /// <summary>
    /// Shared in-memory backing for the providers below. Writes are accepted and discarded: every
    /// assertion here is about what the store learned from a sweep, and a provider that re-snapshots
    /// on write would add nothing but noise.
    /// </summary>
    private abstract class InMemoryStorageProvider : IStorageProvider
    {
        private readonly ConcurrentDictionary<string, NamespaceData> _data = new();
        private int _enumerations;

        /// <summary>How many times the store went back to the provider to list namespaces.</summary>
        public int Enumerations => Volatile.Read(ref _enumerations);

        public void Seed(string ns, List<CognitiveEntry> entries)
            => _data[ns] = new NamespaceData { Entries = entries };

        protected void CountEnumeration() => Interlocked.Increment(ref _enumerations);

        protected IReadOnlyList<string> PersistedNamespaces() => _data.Keys.ToList();

        public abstract IReadOnlyList<string> GetPersistedNamespaces();

        public NamespaceData LoadNamespace(string ns)
            => _data.TryGetValue(ns, out var data) ? data : new NamespaceData();

        // Incremental, so nothing asks for a full namespace snapshot per write.
        public bool SupportsIncrementalWrites => true;
        public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) { }
        public void ScheduleDeleteEntry(string ns, string entryId) { }
        public void ScheduleSave(string ns, Func<NamespaceData> dataProvider) { }
        public void SaveNamespaceSync(string ns, NamespaceData data) => _data[ns] = data;

        public List<GraphEdge> LoadGlobalEdges() => new();
        public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider) { }
        public List<SemanticCluster> LoadClusters() => new();
        public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider) { }
        public List<CollapseRecord> LoadCollapseHistory() => new();
        public void ScheduleSaveCollapseHistory(Func<List<CollapseRecord>> dataProvider) { }
        public Dictionary<string, DecayConfig> LoadDecayConfigs() => new();
        public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider) { }
        public HnswSnapshot? LoadHnswSnapshot(string ns) => null;
        public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot) { }
        public void DeleteHnswSnapshot(string ns) { }
        public Task DeleteNamespaceAsync(string ns) { _data.TryRemove(ns, out _); return Task.CompletedTask; }
        public void Flush() { }
        public virtual void Dispose() { }
    }

    /// <summary>
    /// Fails its first <c>failuresBeforeSuccess</c> enumerations the way a real backend does — a
    /// dropped connection, a directory that momentarily cannot be read — and succeeds after. Failures
    /// still count as enumerations, because "did the store go back to the provider" is what the
    /// caching assertions are about.
    /// </summary>
    private sealed class FlakyEnumerationProvider : InMemoryStorageProvider
    {
        private int _remainingFailures;

        public FlakyEnumerationProvider(int failuresBeforeSuccess)
            => _remainingFailures = failuresBeforeSuccess;

        public override IReadOnlyList<string> GetPersistedNamespaces()
        {
            CountEnumeration();
            if (Interlocked.Decrement(ref _remainingFailures) >= 0)
                throw new NamespaceEnumerationException(new IOException("injected listing failure"));
            return PersistedNamespaces();
        }
    }

    /// <summary>
    /// Holds every enumeration open until the test releases it, and reports how many callers reached
    /// one. That is what lets the single-flight assertion be about a count rather than about who
    /// happened to be scheduled first.
    /// </summary>
    private sealed class GatedEnumerationProvider : InMemoryStorageProvider
    {
        private readonly ManualResetEventSlim _firstEntered = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private readonly CountdownEvent _arrivals;

        public GatedEnumerationProvider(int expectedConcurrentEnumerations)
            => _arrivals = new CountdownEvent(expectedConcurrentEnumerations);

        public override IReadOnlyList<string> GetPersistedNamespaces()
        {
            CountEnumeration();
            _firstEntered.Set();

            // Signalling past zero would mean more enumerations than the test expects; that is
            // exactly the defect being hunted, and the counter reports it, so swallow the throw
            // rather than replacing a clear assertion failure with an exception from a stub.
            try { _arrivals.Signal(); }
            catch (InvalidOperationException) { }

            _release.Wait();
            return PersistedNamespaces();
        }

        public bool WaitForFirstEnumeration(TimeSpan timeout) => _firstEntered.Wait(timeout);

        public bool WaitForExpectedConcurrency(TimeSpan timeout) => _arrivals.Wait(timeout);

        public void ReleaseEnumerations() => _release.Set();

        public override void Dispose()
        {
            // Release before disposing so a caller still parked in the gate cannot be left waiting on
            // a disposed event if an assertion failed before the test reached its own release.
            _release.Set();
            _release.Dispose();
            _firstEntered.Dispose();
            _arrivals.Dispose();
        }
    }
}
