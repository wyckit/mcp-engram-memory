using System.Collections.Concurrent;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// Two properties of the tenant + id candidate index that the equivalence tests in
/// <see cref="EntryResolutionIndexTests"/> cannot reach, because both are invisible to a
/// single-threaded caller looking at a warm store.
///
/// The first is a lost placement under concurrency. Publishing a candidate bucket and retiring an
/// emptied one used to be lock-free, so a retirement could decide on emptiness, a concurrent
/// placement could be written into that same instance, and the retirement could then unpublish it.
/// The entry stays live and stops being reachable by bare id. That is not a tolerable staleness:
/// every bare-id caller treats "fewer namespaces" as "less ambiguous", so an id occupying two
/// namespaces reads back as occupying one and the tenant-wide duplicate test that topology fails
/// closed on passes instead. The stress test below drives exactly that interleaving and then checks
/// the index against a brute-force scan of what is actually resident.
///
/// The second is the cost of a resolution. The candidate index removed the per-namespace partition
/// probes but every lookup still enumerated the storage provider — a directory listing or a
/// SELECT DISTINCT — so the end-to-end work still scaled with the number of persisted namespaces.
/// An access-pattern test built on the ACL predicate cannot see that, because the predicate is only
/// applied to namespaces that already survived the enumeration. Counting provider enumerations
/// directly is the seam that can.
///
/// Every test here runs against a hand-rolled in-memory provider rather than a temp directory, for
/// two reasons: the enumeration counter has to live somewhere, and the JSON provider re-snapshots a
/// whole namespace per write, which would make a churn loop quadratic and drown the race window in
/// serialization.
/// </summary>
public class CandidateIndexConcurrencyTests : IDisposable
{
    // A real tenant, not the legacy "" partition: TrackEntry short-circuits out of the legacy
    // locator for an identified tenant, so the storm exercises the candidate index and nothing else.
    private const string Tenant = "t1";
    private const string RemoverNs = "retire-source";
    // Adder namespaces are chunked below CognitiveIndex's HNSW threshold (200). A partition that
    // crosses it starts building and rebuilding a graph on every upsert, which is real work on the
    // very path whose timing the race depends on.
    private const int AdderNsChunk = 150;

    private readonly CountingStorageProvider _provider = new();
    private readonly CognitiveIndex _index;

    public CandidateIndexConcurrencyTests() => _index = new CognitiveIndex(_provider);

    public void Dispose()
    {
        _index.Dispose();
        _provider.Dispose();
    }

    // Text is deliberately null. BM25Index.Index returns immediately when both text and keywords are
    // blank, and CognitiveIndex skips keyword enrichment for the same reason, so an upsert is close
    // to nothing but the dictionary write and the candidate publication — which is what makes the
    // publication a large fraction of the path and the race reachable within a test's runtime.
    private static CognitiveEntry Entry(string id, string ns, string tenantId)
        => new(id, [1f, 0f], ns, tenantId: tenantId);

    private static string StormId(int round) => $"race-{round:D6}";

    private static string AdderNs(int worker, int round)
        => $"adder-{worker}-{round / AdderNsChunk:D3}";

    private static void AssertSameSet(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(
            expected.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            actual.OrderBy(s => s, StringComparer.Ordinal).ToList());

    // ── The race ──

    /// <summary>
    /// Drive the retirement/publication interleaving on one (tenant, id) at a time, then require the
    /// index to name every placement that is actually resident.
    ///
    /// Shape, and why it is this shape: each round uses a fresh id, so a lost placement is permanent
    /// rather than repaired by the next iteration — a churn loop that keeps re-upserting the same id
    /// republishes its own bucket and hides the very defect it is trying to provoke. The remover
    /// seeds its placement before the round opens, so when the barrier releases, the bucket holds
    /// exactly one namespace and the delete is guaranteed to empty it and attempt a retirement. The
    /// adders publish into that same bucket at the same instant.
    /// </summary>
    [Fact]
    public void ConcurrentPublishAndRetire_NeverUnpublishesALivePlacement()
    {
        const int rounds = 6000;
        const int adders = 3;

        RunRetirementStorm(adders, rounds);

        // The oracle: everything actually resident, read back through the same public API a caller
        // would use, with no knowledge of how the index is maintained.
        var missing = new List<string>();
        int live = 0;
        foreach (var ns in _index.GetNamespaces(Tenant))
        {
            foreach (var entry in _index.GetAllInNamespace(ns, tenantId: Tenant))
            {
                live++;
                if (!_index.GetNamespacesContaining(entry.Id, tenantId: Tenant).Contains(ns))
                    missing.Add($"{ns}/{entry.Id}");
            }
        }

        // Guard the guard. If the storm had silently done nothing, an index that is permanently
        // empty would agree with an empty scan and this test would pass having proved nothing.
        // The adders never delete and every id is unique to its round, so the count is exact.
        Assert.Equal(rounds * adders, live);

        Assert.True(missing.Count == 0,
            $"{missing.Count} live placement(s) are unreachable by bare id — a retirement " +
            $"unpublished a bucket a concurrent placement had already been written into. " +
            $"First few: {string.Join(", ", missing.Take(5))}");
    }

    /// <summary>
    /// The same storm, stated as the security consequence rather than as an internal disagreement:
    /// while both twins are live, the tenant-wide duplicate test must never report fewer than two
    /// namespaces. Under-reporting here is what lets an ACL-blind topology guard conclude a
    /// duplicated id is unique and proceed to mutate a node shared with a twin the caller cannot see.
    /// </summary>
    [Fact]
    public void DuplicatedId_UnderConcurrentRetirement_NeverCountsFewerThanTwoNamespaces()
    {
        const int rounds = 4000;
        const int adders = 2;

        RunRetirementStorm(adders, rounds);

        var underCounted = new List<string>();
        for (int round = 0; round < rounds; round++)
        {
            var id = StormId(round);
            // Exactly two: both adders hold the id and neither ever deleted it, the remover's
            // placement is gone, and the count saturates at two by contract.
            if (_index.CountNamespacesContaining(id, tenantId: Tenant) != 2)
                underCounted.Add(id);
        }

        Assert.True(underCounted.Count == 0,
            $"{underCounted.Count} duplicated id(s) reported fewer than two candidate namespaces " +
            $"while both entries were live. First few: {string.Join(", ", underCounted.Take(5))}");
    }

    /// <summary>
    /// One remover and <paramref name="adders"/> adders, re-synchronized on every round so the
    /// delete that empties a bucket and the upserts that repopulate it are always contending for the
    /// same key at the same moment.
    ///
    /// Dedicated threads (LongRunning) rather than pooled ones: every worker parks on a barrier each
    /// round, and a thread-pool that has to grow to cover the blocked workers injects threads on a
    /// hill-climbing delay, which would stretch the test out and desynchronize the rounds it depends
    /// on.
    /// </summary>
    private void RunRetirementStorm(int adders, int rounds)
    {
        int workerCount = adders + 1;
        using var start = new ManualResetEventSlim(false);
        using var round = new Barrier(workerCount);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        Exception? failure = null;
        var failureGate = new object();
        void Record(Exception ex) { lock (failureGate) { failure ??= ex; } }

        var workers = new Task[workerCount];
        for (int w = 0; w < workerCount; w++)
        {
            int worker = w;
            workers[w] = Task.Factory.StartNew(() =>
            {
                // Per-worker, deterministically seeded: the adders use it to smear their arrival
                // across the remover's path (see below), and a shared Random would neither be
                // thread-safe nor reproducible.
                var jitter = new Random(1000 + worker);
                try
                {
                    start.Wait();
                    for (int r = 0; r < rounds; r++)
                    {
                        var id = StormId(r);
                        try
                        {
                            // Seeded before the round opens so the bucket holds exactly one
                            // namespace when the barrier releases — the delete below is then
                            // guaranteed to empty it and attempt a retirement.
                            if (worker == 0 && !deadline.IsCancellationRequested)
                                _index.Upsert(Entry(id, RemoverNs, Tenant));
                        }
                        catch (Exception ex) { Record(ex); }

                        round.SignalAndWait();

                        try
                        {
                            // A cancelled deadline skips the work but still signals, so the barrier
                            // stays balanced and no worker is left parked forever.
                            if (deadline.IsCancellationRequested)
                                continue;

                            if (worker == 0)
                            {
                                _index.Delete(id, RemoverNs, tenantId: Tenant);
                            }
                            else
                            {
                                // The window is a few instructions wide and sits a few hundred
                                // nanoseconds into the remover's delete. Releasing every adder at
                                // exactly the barrier samples one relative offset over and over, so
                                // each adder first spins a short random while, sweeping its arrival
                                // across the remover's path. The bound is itself randomized by
                                // powers of two rather than fixed: the cost of one SpinWait
                                // iteration varies by an order of magnitude across CPUs, and a
                                // log-spread bound covers the useful offsets on all of them instead
                                // of only the machine it was tuned on.
                                Thread.SpinWait(jitter.Next(0, 1 << jitter.Next(0, 7)));
                                _index.Upsert(Entry(id, AdderNs(worker, r), Tenant));
                            }
                        }
                        catch (Exception ex) { Record(ex); }
                    }
                }
                catch (Exception ex) { Record(ex); }
                finally
                {
                    // Leaving the barrier on the way out keeps a worker that died early from
                    // parking the rest at a phase that can never complete.
                    try { round.RemoveParticipant(); }
                    catch (InvalidOperationException) { /* barrier already torn down */ }
                }
            }, TaskCreationOptions.LongRunning);
        }

        start.Set();

        Assert.True(Task.WaitAll(workers, TimeSpan.FromSeconds(120)),
            "the storm workers did not finish; the index is likely deadlocked");
        Assert.Null(failure);
        Assert.False(deadline.IsCancellationRequested,
            "the storm ran out of time before completing its rounds, so it proved nothing");
    }

    // ── Normal, single-threaded maintenance of the same structure ──

    /// <summary>
    /// Add, remove and re-add through the empty state. Emptying a bucket completely is the state
    /// that retires it, so re-adding afterwards has to publish a fresh one and lookups have to reach
    /// the fresh one — the sequence a retirement bug can also break without any concurrency at all.
    /// </summary>
    [Fact]
    public void AddRemoveAndReAdd_TracksExactlyTheLivePlacements()
    {
        _index.Upsert(Entry("x", "n1", Tenant));
        _index.Upsert(Entry("x", "n2", Tenant));
        AssertSameSet(["n1", "n2"], _index.GetNamespacesContaining("x", tenantId: Tenant));
        Assert.Equal(2, _index.CountNamespacesContaining("x", tenantId: Tenant));

        Assert.True(_index.Delete("x", "n1", tenantId: Tenant));
        Assert.Equal(new[] { "n2" }, _index.GetNamespacesContaining("x", tenantId: Tenant));
        Assert.Equal(1, _index.CountNamespacesContaining("x", tenantId: Tenant));

        // Bucket now empties and is retired.
        Assert.True(_index.Delete("x", "n2", tenantId: Tenant));
        Assert.Empty(_index.GetNamespacesContaining("x", tenantId: Tenant));
        Assert.Equal(0, _index.CountNamespacesContaining("x", tenantId: Tenant));

        // Republished from nothing, then grown again.
        _index.Upsert(Entry("x", "n3", Tenant));
        Assert.Equal(new[] { "n3" }, _index.GetNamespacesContaining("x", tenantId: Tenant));

        _index.Upsert(Entry("x", "n1", Tenant));
        AssertSameSet(["n1", "n3"], _index.GetNamespacesContaining("x", tenantId: Tenant));
        Assert.Equal(2, _index.CountNamespacesContaining("x", tenantId: Tenant));
    }

    // ── The cost the ACL-predicate benchmark cannot see ──

    /// <summary>
    /// Seed namespaces straight into the provider, so the only way the index can learn about them is
    /// the full load sweep — the same position a freshly started server is in.
    /// </summary>
    private void SeedPersisted(int namespaceCount, string needleNs)
    {
        for (int i = 0; i < namespaceCount; i++)
        {
            var ns = $"ns-{i:D2}";
            var entries = new List<CognitiveEntry> { Entry($"filler-{i}", ns, string.Empty) };
            if (ns == needleNs)
                entries.Add(Entry("needle", ns, string.Empty));
            _provider.Seed(ns, entries);
        }
    }

    [Fact]
    public void BareIdResolution_DoesNotEnumeratePersistedNamespacesOnEveryCall()
    {
        SeedPersisted(namespaceCount: 12, needleNs: "ns-07");

        int before = _provider.Enumerations;
        for (int i = 0; i < 50; i++)
            Assert.Equal(new[] { "ns-07" }, _index.GetNamespacesContaining("needle", tenantId: ""));
        int enumerations = _provider.Enumerations - before;

        // One sweep for the whole process, not one per resolution. The predicate-counting test in
        // EntryResolutionIndexTests cannot observe this: the predicate is only ever applied to the
        // namespaces that survived the enumeration, so it stayed flat while the enumeration behind
        // it scaled with the number of persisted namespaces on every single call.
        Assert.Equal(1, enumerations);

        // Completeness is what the count is being traded against, so assert it in the same test: the
        // needle lives in a namespace this process had never touched, and it is still found.
        Assert.Equal(1, _index.CountNamespacesContaining("needle", tenantId: ""));
        Assert.Equal("ns-07", _index.GetForTenant("needle", tenantId: "")!.Ns);
    }

    [Fact]
    public void NamespaceCreatedAfterTheSweepIsCached_IsStillReachableByBareId()
    {
        SeedPersisted(namespaceCount: 12, needleNs: "ns-07");

        // Warm the cache.
        Assert.Equal(new[] { "ns-07" }, _index.GetNamespacesContaining("needle", tenantId: ""));
        int afterWarm = _provider.Enumerations;

        // The completeness argument the cache rests on: a namespace created by THIS process is
        // materialized by the write path before it can be written to, so it is already in the
        // candidate index and needs no sweep to be discovered. If that were false, the twin below
        // would be invisible and the id would look unique — the same failure as a lost placement,
        // reached by a different route.
        _index.Upsert(Entry("needle", "created-later", string.Empty));

        AssertSameSet(["ns-07", "created-later"], _index.GetNamespacesContaining("needle", tenantId: ""));
        Assert.Equal(2, _index.CountNamespacesContaining("needle", tenantId: ""));
        // Ambiguous now, so the bare-id get refuses rather than picking a twin.
        Assert.Null(_index.GetForTenant("needle", tenantId: ""));

        // And none of that re-enumerated the provider.
        Assert.Equal(afterWarm, _provider.Enumerations);

        // Removing a partition retracts its candidates without needing a sweep either, and the
        // surviving twin becomes unambiguous again.
        Assert.Equal(2, _index.DeleteAllInNamespace("ns-07", tenantId: ""));
        Assert.Equal(new[] { "created-later" }, _index.GetNamespacesContaining("needle", tenantId: ""));
        Assert.Equal("created-later", _index.GetForTenant("needle", tenantId: "")!.Ns);
        Assert.Equal(afterWarm, _provider.Enumerations);
    }

    /// <summary>
    /// The single invalidation point the cache's completeness argument rests on. Un-loading a
    /// namespace is the one in-process event that makes "every persisted namespace has been
    /// materialized" false again, and the namespace stays persisted, so a sweep that did not go back
    /// to the provider would leave it permanently invisible — a bare id living there would resolve
    /// to nothing, and an id living there and elsewhere would look unique.
    ///
    /// Driven against the store directly because CognitiveIndex removes partitions through the NsKey
    /// overload, which deliberately leaves the namespace loaded and so does not invalidate anything.
    /// </summary>
    [Fact]
    public void UnloadingANamespace_InvalidatesTheCachedLoadSweep()
    {
        using var provider = new CountingStorageProvider();
        provider.Seed("gone", [Entry("orphan", "gone", string.Empty)]);
        var store = new NamespaceStore(provider, new BM25Index());

        store.LoadAll();
        Assert.Equal(1, provider.Enumerations);
        Assert.Equal(new[] { "gone" }, store.GetCandidateNamespaces("orphan", string.Empty));

        // Second sweep is free — this is the caching being asserted, not assumed.
        store.LoadAll();
        Assert.Equal(1, provider.Enumerations);

        store.RemoveNamespace("gone");
        Assert.Empty(store.GetCandidateNamespaces("orphan", string.Empty));

        // Still persisted, so the next sweep has to re-enumerate and re-materialize it.
        store.LoadAll();
        Assert.Equal(2, provider.Enumerations);
        Assert.Equal(new[] { "gone" }, store.GetCandidateNamespaces("orphan", string.Empty));
    }

    /// <summary>
    /// In-memory provider that counts <see cref="GetPersistedNamespaces"/> calls — the enumeration a
    /// real backend pays for with a directory listing or a SELECT DISTINCT. Writes are accepted and
    /// discarded: the tests here assert on in-memory index state, and a provider that re-snapshots a
    /// namespace per write would make the churn loops quadratic.
    /// </summary>
    private sealed class CountingStorageProvider : IStorageProvider
    {
        private readonly ConcurrentDictionary<string, NamespaceData> _data = new();
        private int _enumerations;

        public int Enumerations => Volatile.Read(ref _enumerations);

        public void Seed(string ns, List<CognitiveEntry> entries)
            => _data[ns] = new NamespaceData { Entries = entries };

        public IReadOnlyList<string> GetPersistedNamespaces()
        {
            Interlocked.Increment(ref _enumerations);
            return _data.Keys.ToList();
        }

        public NamespaceData LoadNamespace(string ns)
            => _data.TryGetValue(ns, out var data) ? data : new NamespaceData();

        // Incremental, so CognitiveIndex never asks for a full namespace snapshot per write.
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
        public bool UpsertCollapseRecordSync(CollapseRecord record) => true;
        public bool DeleteCollapseRecordSync(string collapseId) => true;
        public CollapseRecordCas UpsertCollapseRecordSync(CollapseRecord record, long? onlyIfGeneration) => CollapseRecordCas.Applied;
        public CollapseRecordCas DeleteCollapseRecordSync(string collapseId, long onlyIfGeneration) => CollapseRecordCas.AlreadyAbsent;
        public bool TryReadCollapseRecord(string collapseId, out CollapseRecord? record) { record = null; return true; }
    public bool TryReadCollapseHistory(out List<CollapseRecord> records) { records = new(); return true; }
        public bool TryFlush() => true;
        public Dictionary<string, DecayConfig> LoadDecayConfigs() => new();
        public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider) { }
        public HnswSnapshot? LoadHnswSnapshot(string ns) => null;
        public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot) { }
        public void DeleteHnswSnapshot(string ns) { }
        public Task DeleteNamespaceAsync(string ns) { _data.TryRemove(ns, out _); return Task.CompletedTask; }
        public void Flush() { }
        public void Dispose() { }
    }
}
