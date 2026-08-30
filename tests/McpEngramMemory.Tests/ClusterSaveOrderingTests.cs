using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// WHICH CLUSTER MAP REACHES THE STORE WHEN TWO MUTATORS OVERLAP, and what a storage provider is
/// allowed to do while it is being asked to load.
///
/// THE INVERSION. Debounced cluster saving is last-registration-wins over a FULL-REPLACE blob:
/// every provider disposes the pending timer, overwrites the pending provider, and later rewrites
/// the whole cluster document from whichever registration survived. While capture and registration
/// both happened inside the cluster write lock they were totally ordered, so the survivor was always
/// the newest. Moving the registration outside the lock — necessary, because a synchronous provider
/// would otherwise run a storage round trip inside a critical section every reader contends for —
/// split those two moments apart, and two overlapping mutators could then capture in one order and
/// register in the other. The OLDER map is what reaches storage; the newer mutation stays live in
/// memory, so <c>get_cluster</c> keeps answering correctly and nothing looks wrong until the process
/// restarts and the cluster is simply gone. No error, no log line, no flag.
///
/// The fix is to hand persistence a METHOD GROUP that snapshots when the debounce fires, exactly as
/// <c>KnowledgeGraph.ScheduleSaveEdges</c> does, so registration order stops mattering. These tests
/// state that by putting a second mutator THROUGH the gap of the first, with no threads racing and
/// no timing: the first mutator is suspended between its publish and its registration, on a seam
/// that exists for this and nothing else.
/// </summary>
public sealed class ClusterSaveOrderingTests : IDisposable
{
    private const string Tenant = "acme";
    private const string Ns = "main";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;

    public ClusterSaveOrderingTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"cluster_save_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 600_000);
        _index = new CognitiveIndex(_persistence);

        _index.Upsert(new CognitiveEntry("m1", [1f, 0f], Ns, "m1", tenantId: Tenant));
        _index.Upsert(new CognitiveEntry("m2", [0f, 1f], Ns, "m2", tenantId: Tenant));
        _index.Upsert(new CognitiveEntry("m3", [1f, 1f], Ns, "m3", tenantId: Tenant));
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 1. THE REGISTRATION INVERSION
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A mutator suspended between its publish and its save registration, with a SECOND mutator run
    /// to completion through the gap. The blob that reaches storage must contain both edits.
    ///
    /// <c>StoreSummary</c> is the suspended one deliberately: it publishes and registers exactly
    /// once, with no follow-up centroid pass to quietly re-register a fresher snapshot afterwards
    /// and paper over the inversion. Its stale registration is therefore the last one, which is the
    /// shape that actually loses data.
    ///
    /// Before the fix the persisted blob held only the cluster <c>StoreSummary</c> had captured —
    /// <c>c2</c> was created, published, registered, and then silently overwritten by a map from
    /// before it existed. <c>GetCluster("c2")</c> still answered for the life of the process; the
    /// reload is what exposes it.
    /// </summary>
    [Fact]
    public void AMutatorSuspendedBeforeItsSaveRegistration_DoesNotOverwriteANewerClusterMap()
    {
        var store = new ClusterCapturingStore(_persistence);
        var clusters = new ClusterManager(_index, store);

        Assert.Contains("Created", clusters.CreateCluster("c1", Ns, new[] { "m1" }, "one", Tenant));

        var interloperDone = new ManualResetEventSlim(false);
        Exception? interloperFailure = null;
        int suspensions = 0;
        int suspendingThread = Environment.CurrentManagedThreadId;

        clusters.OnBeforeScheduleSave = () =>
        {
            // Only the FIRST registration on the suspending thread opens the gap. The interloper
            // registers through this same seam and must not recurse into it.
            if (Environment.CurrentManagedThreadId != suspendingThread) return;
            if (Interlocked.Increment(ref suspensions) != 1) return;

            var interloper = new Thread(() =>
            {
                try
                {
                    Assert.Contains("Created",
                        clusters.CreateCluster("c2", Ns, new[] { "m2", "m3" }, "two", Tenant));
                }
                catch (Exception ex) { interloperFailure = ex; }
                finally { interloperDone.Set(); }
            })
            { IsBackground = true, Name = "cluster-interloper" };

            interloper.Start();
            Assert.True(interloperDone.Wait(Budget), "the interloping cluster mutation never finished");
            interloper.Join(Budget);
        };

        Assert.StartsWith("summary:", clusters.StoreSummary("c1", "a summary", [0.5f, 0.5f], Tenant));
        clusters.OnBeforeScheduleSave = null;

        Assert.Null(interloperFailure);
        Assert.Equal(1, suspensions);

        // Run the debounce the shipped providers would have run, deterministically.
        store.Commit();

        var persisted = store.LoadClusters();
        Assert.Contains(persisted, c => c.ClusterId == "c1");
        Assert.Contains(persisted, c => c.ClusterId == "c2");

        // The suspended mutator's own edit survived too — this is about ordering, not about one
        // write beating the other.
        Assert.Equal("summary:c1", persisted.Single(c => c.ClusterId == "c1").SummaryEntryId);

        // End to end: a manager reloading over what was actually written sees both.
        var reloaded = new ClusterManager(_index, store);
        Assert.NotNull(reloaded.GetCluster("c1", Tenant));
        Assert.NotNull(reloaded.GetCluster("c2", Tenant));
    }

    /// <summary>
    /// The same gap, with the interloper editing THE SAME cluster rather than creating another one.
    /// Membership is the payload that actually goes missing in the reported scenario, and a
    /// full-replace blob loses it just as completely as it loses a whole cluster.
    /// </summary>
    [Fact]
    public void AMutatorSuspendedBeforeItsSaveRegistration_DoesNotOverwriteANewerMemberList()
    {
        var store = new ClusterCapturingStore(_persistence);
        var clusters = new ClusterManager(_index, store);

        Assert.Contains("Created", clusters.CreateCluster("c1", Ns, new[] { "m1" }, "one", Tenant));

        var interloperDone = new ManualResetEventSlim(false);
        Exception? interloperFailure = null;
        int suspensions = 0;
        int suspendingThread = Environment.CurrentManagedThreadId;

        clusters.OnBeforeScheduleSave = () =>
        {
            if (Environment.CurrentManagedThreadId != suspendingThread) return;
            if (Interlocked.Increment(ref suspensions) != 1) return;

            var interloper = new Thread(() =>
            {
                try
                {
                    Assert.Contains("Updated",
                        clusters.UpdateCluster("c1", new[] { "m3" }, null, null, Tenant));
                }
                catch (Exception ex) { interloperFailure = ex; }
                finally { interloperDone.Set(); }
            })
            { IsBackground = true, Name = "cluster-interloper" };

            interloper.Start();
            Assert.True(interloperDone.Wait(Budget), "the interloping cluster mutation never finished");
            interloper.Join(Budget);
        };

        Assert.StartsWith("summary:", clusters.StoreSummary("c1", "a summary", [0.5f, 0.5f], Tenant));
        clusters.OnBeforeScheduleSave = null;

        Assert.Null(interloperFailure);
        store.Commit();

        var persisted = store.LoadClusters().Single(c => c.ClusterId == "c1");
        Assert.Equal(new[] { "m1", "m3" }, persisted.MemberIds);
        Assert.Equal("summary:c1", persisted.SummaryEntryId);
    }

    /// <summary>
    /// The control: with nothing overlapping, every cluster mutator still reaches the store. A
    /// deferred provider that snapshotted the wrong thing — or nothing — would satisfy the two tests
    /// above and lose everything.
    /// </summary>
    [Fact]
    public void WithNothingOverlapping_EveryClusterMutatorReachesTheStore()
    {
        var store = new ClusterCapturingStore(_persistence);
        var clusters = new ClusterManager(_index, store);

        Assert.Contains("Created", clusters.CreateCluster("c1", Ns, new[] { "m1", "m2" }, "one", Tenant));
        Assert.Contains("Updated", clusters.UpdateCluster("c1", new[] { "m3" }, null, "renamed", Tenant));
        Assert.StartsWith("summary:", clusters.StoreSummary("c1", "text", [0.5f, 0.5f], Tenant));
        Assert.Equal(1, clusters.TransferMembership("m3", "m2", Tenant));
        clusters.RemoveEntryFromAllClusters("m1", Tenant);

        store.Commit();

        var persisted = store.LoadClusters().Single(c => c.ClusterId == "c1");
        Assert.Equal("renamed", persisted.Label);
        Assert.Equal("summary:c1", persisted.SummaryEntryId);
        Assert.DoesNotContain("m1", persisted.MemberIds);
        Assert.DoesNotContain("m3", persisted.MemberIds);
        Assert.Contains("m2", persisted.MemberIds);

        // What the store holds equals what the manager holds — the property the deferred snapshot
        // exists to keep true regardless of which registration won.
        var live = clusters.GetCluster("c1", Tenant)!;
        Assert.Equal(live.MemberCount, persisted.MemberIds.Count);
    }

    /// <summary>
    /// A centroid computed by an older single-cluster update may not be published onto the member
    /// list installed by a newer update that completed through the save-registration gap.
    /// </summary>
    [Fact]
    public void AnOlderUpdateCannotPublishItsCentroidOntoANewerMemberList()
    {
        var store = new ClusterCapturingStore(_persistence);
        var clusters = new ClusterManager(_index, store);

        Assert.Contains("Created", clusters.CreateCluster("c1", Ns, new[] { "m1", "m2" }, "one", Tenant));

        clusters.OnBeforeScheduleSave = () =>
        {
            clusters.OnBeforeScheduleSave = null;
            Assert.Contains("Updated",
                clusters.UpdateCluster("c1", null, new[] { "m1" }, null, Tenant));
        };

        Assert.Contains("Updated",
            clusters.UpdateCluster("c1", new[] { "m3" }, null, null, Tenant));
        clusters.OnBeforeScheduleSave = null;

        store.Commit();
        var persisted = store.LoadClusters().Single(c => c.ClusterId == "c1");

        Assert.Equal(new[] { "m2", "m3" }, persisted.MemberIds);
        Assert.Equal(new[] { 0.5f, 1f }, persisted.Centroid);
    }

    /// <summary>
    /// The batched centroid path used by cascade eviction has the same generation requirement as a
    /// normal update. A nested edit wins both membership and centroid.
    /// </summary>
    [Fact]
    public void AnOlderCascadeCannotPublishItsCentroidOntoANewerMemberList()
    {
        var store = new ClusterCapturingStore(_persistence);
        var clusters = new ClusterManager(_index, store);

        Assert.Contains("Created",
            clusters.CreateCluster("c1", Ns, new[] { "m1", "m2", "m3" }, "one", Tenant));

        clusters.OnBeforeScheduleSave = () =>
        {
            clusters.OnBeforeScheduleSave = null;
            Assert.Contains("Updated",
                clusters.UpdateCluster("c1", null, new[] { "m2" }, null, Tenant));
        };

        clusters.RemoveEntryFromAllClusters("m1", Tenant);
        clusters.OnBeforeScheduleSave = null;

        store.Commit();
        var persisted = store.LoadClusters().Single(c => c.ClusterId == "c1");

        Assert.Equal(new[] { "m3" }, persisted.MemberIds);
        Assert.Equal(new[] { 1f, 1f }, persisted.Centroid);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // 2. A PROVIDER THAT WRITES TO THE INDEX WHILE IT IS BEING ASKED TO LOAD
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The load-vs-fence rule, exercised for the first time with a provider that actually does the
    /// thing the rule is about.
    ///
    /// Both classes state that <c>LoadClusters</c> / <c>LoadGlobalEdges</c> run arbitrary
    /// caller-supplied code which may reach back into <see cref="CognitiveIndex"/>, and both hoist
    /// the load out of the fenced section for that reason. Nothing in the suite supplied such a
    /// provider — the existing synchronous-provider test is synchronous on the SAVE path only — so
    /// the rule had never been executed even once.
    ///
    /// The entry the provider writes CROSSES the ambiguity boundary (its id already exists in
    /// another namespace of the tenant), which is the only kind of index write that asks for the
    /// fence's exclusive side and therefore the only kind that can collide with a fence holder.
    /// Every mutator on both classes must complete over it, <c>StoreSummary</c> included — the one
    /// this class describes as needing no fence, and which for that very reason has to warm the
    /// cluster map before it takes the structural lock a fence holder waits for.
    /// </summary>
    [Fact]
    public void EveryMutator_OverAProviderThatWritesToTheIndexWhileLoading_Completes()
    {
        // "twin" is resident in one namespace already, so the provider's write-back is a 1 -> 2
        // crossing — the only kind of index write that asks for the fence's exclusive side.
        _index.Upsert(new CognitiveEntry("twin", [1f, 1f], Ns, "twin", tenantId: Tenant));

        // The cluster is already PERSISTED, so StoreSummary can be the very first call on a cold
        // manager and therefore the call that triggers the one-shot load. That is the ordering the
        // fix is about: the one mutator here that takes no fence still has to warm the map before it
        // takes the structural lock every fence holder is waiting for.
        var store = new IndexWritingLoadStore(_persistence, _index, Tenant)
        {
            SeededClusters = { new SemanticCluster("k", "l", Ns, new List<string> { "m1" }, null, null, Tenant) },
        };

        var clusters = new ClusterManager(_index, store);
        Assert.Equal("summary:k", clusters.StoreSummary("k", "text", [0.5f, 0.5f], Tenant));

        Assert.True(store.ClusterLoads > 0, "the provider's LoadClusters was never invoked");
        Assert.True(store.IndexWrites > 0, "the provider never wrote to the index while loading");

        // The rest of the cluster mutators over the same provider.
        Assert.Contains("Updated", clusters.UpdateCluster("k", new[] { "m2" }, null, null, Tenant));
        Assert.Equal(1, clusters.TransferMembership("m2", "m3", Tenant));
        clusters.RemoveEntryFromAllClusters("m3", Tenant);
        Assert.Contains("Created", clusters.CreateCluster("k2", Ns, new[] { "m1" }, "l2", Tenant));

        // And the graph's own load path, over a provider of the same shape.
        var edgeStore = new IndexWritingLoadStore(_persistence, _index, Tenant);
        var graph = new KnowledgeGraph(edgeStore, _index);

        Assert.True(graph.TryAddEdge(new GraphEdge("m1", "m2", "supports", 0.9f, null, Tenant), out _));
        Assert.Equal(1, graph.AddEdges(new[] { new GraphEdge("m1", "m3", "supports", 0.9f, null, Tenant) }));
        Assert.Contains("Removed", graph.RemoveEdges("m1", "m3", "supports", Tenant));
        Assert.Equal(1, graph.RemoveAllEdgesForEntry("m1", Tenant));
        Assert.True(edgeStore.EdgeLoads > 0, "the provider's LoadGlobalEdges was never invoked");
        Assert.True(edgeStore.IndexWrites > 0, "the provider never wrote to the index while loading");
    }
}

/// <summary>
/// A provider that owns the cluster blob so a test can see what a save would actually persist, and
/// reload a manager over it. The debounce is the test's own <see cref="Commit"/> — no timer, no
/// thread, no waiting — which is the shipped behaviour made deterministic.
/// </summary>
file sealed class ClusterCapturingStore : IStorageProvider
{
    private readonly IStorageProvider _inner;
    private List<SemanticCluster> _persisted = new();
    private Func<List<SemanticCluster>>? _pending;

    public ClusterCapturingStore(IStorageProvider inner) => _inner = inner;

    /// <summary>Run the debounce the shipped providers would have run.</summary>
    public void Commit()
    {
        var pending = _pending;
        _pending = null;
        if (pending is not null) _persisted = pending();
    }

    public List<SemanticCluster> LoadClusters() => new(_persisted);

    // Last-write-wins over a full-replace blob, exactly like PersistenceManager and both SQL
    // providers: the pending provider is overwritten, not queued.
    public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider) => _pending = dataProvider;

    public List<GraphEdge> LoadGlobalEdges() => _inner.LoadGlobalEdges();
    public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider) => _inner.ScheduleSaveGlobalEdges(dataProvider);
    public NamespaceData LoadNamespace(string ns) => _inner.LoadNamespace(ns);
    public IReadOnlyList<string> GetPersistedNamespaces() => _inner.GetPersistedNamespaces();
    public void ScheduleSave(string ns, Func<NamespaceData> dataProvider) => _inner.ScheduleSave(ns, dataProvider);
    public void SaveNamespaceSync(string ns, NamespaceData data) => _inner.SaveNamespaceSync(ns, data);
    public bool SupportsIncrementalWrites => _inner.SupportsIncrementalWrites;
    public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) => _inner.ScheduleUpsertEntry(ns, entry);
    public void ScheduleDeleteEntry(string ns, string entryId) => _inner.ScheduleDeleteEntry(ns, entryId);
    public void ScheduleDeleteEntry(string ns, string entryId, string tenantId) => _inner.ScheduleDeleteEntry(ns, entryId, tenantId);
    public List<CollapseRecord> LoadCollapseHistory() => _inner.LoadCollapseHistory();
    public void ScheduleSaveCollapseHistory(Func<List<CollapseRecord>> dataProvider) => _inner.ScheduleSaveCollapseHistory(dataProvider);
    public Dictionary<string, DecayConfig> LoadDecayConfigs() => _inner.LoadDecayConfigs();
    public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider) => _inner.ScheduleSaveDecayConfigs(dataProvider);
    public HnswSnapshot? LoadHnswSnapshot(string ns) => _inner.LoadHnswSnapshot(ns);
    public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot) => _inner.SaveHnswSnapshotSync(ns, snapshot);
    public void DeleteHnswSnapshot(string ns) => _inner.DeleteHnswSnapshot(ns);
    public Task DeleteNamespaceAsync(string ns) => _inner.DeleteNamespaceAsync(ns);
    public Task DeleteNamespaceAsync(string ns, string tenantId) => _inner.DeleteNamespaceAsync(ns, tenantId);
    public void Flush() => _inner.Flush();

    // The inner provider belongs to the fixture, which disposes it.
    public void Dispose() { }
}

/// <summary>
/// The provider both classes' load-vs-fence rules are written against and which no test supplied: it
/// WRITES AN ENTRY INTO <see cref="CognitiveIndex"/> from inside its load methods, and the write it
/// makes is an ambiguity crossing — the one kind that asks for the attribution fence's exclusive
/// side.
///
/// It writes once per load method, not once per call, so a mutator that loads repeatedly does not
/// manufacture a fresh crossing on every acquisition.
/// </summary>
file sealed class IndexWritingLoadStore : IStorageProvider
{
    private readonly IStorageProvider _inner;
    private readonly CognitiveIndex _index;
    private readonly string _tenant;
    private int _writes;

    public IndexWritingLoadStore(IStorageProvider inner, CognitiveIndex index, string tenant)
    {
        _inner = inner;
        _index = index;
        _tenant = tenant;
    }

    /// <summary>Clusters this provider hands back, so a cold manager can be driven from a mutator
    /// that needs an existing cluster.</summary>
    public List<SemanticCluster> SeededClusters { get; } = new();

    public int ClusterLoads { get; private set; }
    public int EdgeLoads { get; private set; }
    public int IndexWrites => _writes;

    private void WriteBackOnce(string ns)
    {
        if (Interlocked.CompareExchange(ref _writes, 1, 0) != 0) return;
        // A 1 -> 2 crossing for "twin": the caller has already placed it in another namespace.
        _index.Upsert(new CognitiveEntry("twin", [1f, 1f], ns, "twin from the provider", tenantId: _tenant));
    }

    public List<SemanticCluster> LoadClusters()
    {
        ClusterLoads++;
        WriteBackOnce("provider-clusters");
        return new List<SemanticCluster>(SeededClusters);
    }

    public List<GraphEdge> LoadGlobalEdges()
    {
        EdgeLoads++;
        WriteBackOnce("provider-edges");
        return _inner.LoadGlobalEdges();
    }

    public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider) => _inner.ScheduleSaveClusters(dataProvider);
    public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider) => _inner.ScheduleSaveGlobalEdges(dataProvider);
    public NamespaceData LoadNamespace(string ns) => _inner.LoadNamespace(ns);
    public IReadOnlyList<string> GetPersistedNamespaces() => _inner.GetPersistedNamespaces();
    public void ScheduleSave(string ns, Func<NamespaceData> dataProvider) => _inner.ScheduleSave(ns, dataProvider);
    public void SaveNamespaceSync(string ns, NamespaceData data) => _inner.SaveNamespaceSync(ns, data);
    public bool SupportsIncrementalWrites => _inner.SupportsIncrementalWrites;
    public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) => _inner.ScheduleUpsertEntry(ns, entry);
    public void ScheduleDeleteEntry(string ns, string entryId) => _inner.ScheduleDeleteEntry(ns, entryId);
    public void ScheduleDeleteEntry(string ns, string entryId, string tenantId) => _inner.ScheduleDeleteEntry(ns, entryId, tenantId);
    public List<CollapseRecord> LoadCollapseHistory() => _inner.LoadCollapseHistory();
    public void ScheduleSaveCollapseHistory(Func<List<CollapseRecord>> dataProvider) => _inner.ScheduleSaveCollapseHistory(dataProvider);
    public Dictionary<string, DecayConfig> LoadDecayConfigs() => _inner.LoadDecayConfigs();
    public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider) => _inner.ScheduleSaveDecayConfigs(dataProvider);
    public HnswSnapshot? LoadHnswSnapshot(string ns) => _inner.LoadHnswSnapshot(ns);
    public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot) => _inner.SaveHnswSnapshotSync(ns, snapshot);
    public void DeleteHnswSnapshot(string ns) => _inner.DeleteHnswSnapshot(ns);
    public Task DeleteNamespaceAsync(string ns) => _inner.DeleteNamespaceAsync(ns);
    public Task DeleteNamespaceAsync(string ns, string tenantId) => _inner.DeleteNamespaceAsync(ns, tenantId);
    public void Flush() => _inner.Flush();
    public void Dispose() { }
}
