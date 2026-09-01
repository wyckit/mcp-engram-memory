using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace McpEngramMemory.Tests;

/// <summary>
/// Shutdown-path regression tests for <see cref="CognitiveIndex.Dispose"/>.
///
/// The index documents that callers must quiesce in-flight work before disposing, because a
/// thread holding a per-namespace lock makes <c>ReaderWriterLockSlim.Dispose</c> throw
/// <see cref="SynchronizationLockException"/>. In the hosted server nothing enforces that: the
/// four maintenance workers run passes that take no CancellationToken, so once a pass starts it
/// cannot be interrupted. If it outlives the host's shutdown timeout, StopAsync gives up and the
/// container disposes the index underneath a running pass.
///
/// The consequence is worse than a noisy exception. ServiceProvider disposes singletons in reverse
/// creation order and does not catch, so a throwing Dispose abandons everything created earlier —
/// including PersistenceManager, whose Dispose is what calls Flush() and writes pending debounced
/// entries to disk. A shutdown-time exception here silently drops unflushed memories.
/// </summary>
public sealed class CognitiveIndexDisposalTests : IDisposable
{
    private readonly string _dataPath =
        Path.Combine(Path.GetTempPath(), $"index_disposal_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, recursive: true); }
        catch { /* best-effort */ }
    }

    private static CognitiveEntry Entry(string id, string ns)
        => new(id, new float[] { 0.5f, 0.5f }, ns, $"text for {id}");

    [Fact]
    public void Dispose_WithContendedNamespaceLock_DoesNotThrow()
    {
        using var scenario = new ContendedLockScenario(_dataPath);

        var thrown = Record.Exception(() => scenario.Index.Dispose());

        Assert.Null(thrown);
        // Surviving the throw is only half of it: the contention must still be visible, or a
        // shutdown race becomes indistinguishable from a clean one.
        Assert.Equal(1, scenario.Index.DisposalContendedLockCount);
    }

    [Fact]
    public void Dispose_WithContendedNamespaceLock_StillDisposesEarlierContainerSingletons()
    {
        using var gate = new ManualResetEventSlim(false);
        using var inLock = new ManualResetEventSlim(false);

        var services = new ServiceCollection();
        // Registered as a TYPE, not an instance: the container only disposes what it created, so
        // an instance registration would never be disposed and the assertion below would pass or
        // fail for reasons unrelated to the cascade being tested.
        services.AddSingleton<DisposalSentinel>();
        services.AddSingleton<IStorageProvider>(_ =>
            new BlockingStorageProvider(new PersistenceManager(_dataPath), gate, inLock));
        services.AddSingleton<CognitiveIndex>();

        var provider = services.BuildServiceProvider();

        // Resolve the sentinel first so the container creates it first and therefore disposes it
        // last — the position PersistenceManager occupies relative to CognitiveIndex in Program.cs,
        // since the index depends on it. That is the disposal whose loss costs a flush.
        var sentinel = provider.GetRequiredService<DisposalSentinel>();
        var index = provider.GetRequiredService<CognitiveIndex>();

        using (var contention = new ContendedLockScenario(index, gate, inLock))
        {
            var thrown = Record.Exception(() => provider.Dispose());

            Assert.Null(thrown);
        }

        Assert.True(sentinel.Disposed,
            "container disposal stopped at CognitiveIndex — in the real host this is the flush that never runs");
    }

    /// <summary>
    /// Puts a per-namespace lock into the state that actually makes ReaderWriterLockSlim.Dispose
    /// throw: one thread holding it and another queued behind it.
    ///
    /// A holder alone is not enough. IsWriteLockHeld is thread-affine, so the disposing thread sees
    /// false and Dispose succeeds; it is WaitingWriteCount that trips the check. The CI failure that
    /// prompted this said as much — "held by a thread and/or has active waiters".
    /// </summary>
    private sealed class ContendedLockScenario : IDisposable
    {
        private readonly ManualResetEventSlim _gate;
        private readonly ManualResetEventSlim _ownedGate;
        private readonly ManualResetEventSlim? _ownedInLock;
        private readonly PersistenceManager? _ownedPersistence;
        private readonly Thread _holder;
        private readonly Thread _waiter;

        public CognitiveIndex Index { get; }

        public ContendedLockScenario(string dataPath)
        {
            _ownedGate = new ManualResetEventSlim(false);
            _ownedInLock = new ManualResetEventSlim(false);
            _gate = _ownedGate;
            _ownedPersistence = new PersistenceManager(dataPath);
            Index = new CognitiveIndex(new BlockingStorageProvider(_ownedPersistence, _gate, _ownedInLock));
            (_holder, _waiter) = Start(Index, _ownedInLock);
        }

        public ContendedLockScenario(CognitiveIndex index, ManualResetEventSlim gate, ManualResetEventSlim inLock)
        {
            _gate = gate;
            _ownedGate = null!;
            Index = index;
            (_holder, _waiter) = Start(index, inLock);
        }

        private static (Thread Holder, Thread Waiter) Start(CognitiveIndex index, ManualResetEventSlim inLock)
        {
            // Holder: ScheduleSave is invoked while the per-namespace write lock is held, so
            // blocking there pins the lock deterministically rather than by timing.
            var holder = new Thread(() =>
            {
                try { index.Upsert(Entry("holder", "ns-a")); }
                catch { /* teardown races are not what these tests are about */ }
            }) { IsBackground = true };
            holder.Start();

            if (!inLock.Wait(TimeSpan.FromSeconds(10)))
                throw new InvalidOperationException("holder never reached the storage callback");

            // Waiter: same namespace, so it queues on the write lock the holder owns.
            var waiter = new Thread(() =>
            {
                try { index.Upsert(Entry("waiter", "ns-a")); }
                catch { /* expected once the lock is torn down */ }
            }) { IsBackground = true };
            waiter.Start();

            // The one timing dependency here. The waiter does nothing but call Upsert, which blocks
            // on EnterWriteLock almost immediately; this only has to outlast thread start-up. If it
            // were ever too short the test would report a false pass, so it is generous.
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            return (holder, waiter);
        }

        public void Dispose()
        {
            _gate.Set();
            _holder.Join(TimeSpan.FromSeconds(10));
            _waiter.Join(TimeSpan.FromSeconds(10));
            _ownedPersistence?.Dispose();
            _ownedInLock?.Dispose();
            _ownedGate?.Dispose();
        }
    }

    private sealed class DisposalSentinel : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Delegates everything to a real provider, but parks the caller inside ScheduleSave — which
    /// CognitiveIndex invokes while holding the per-namespace write lock.
    /// </summary>
    private sealed class BlockingStorageProvider : IStorageProvider
    {
        private readonly IStorageProvider _inner;
        private readonly ManualResetEventSlim _release;
        private readonly ManualResetEventSlim _entered;

        public BlockingStorageProvider(
            IStorageProvider inner, ManualResetEventSlim release, ManualResetEventSlim entered)
            => (_inner, _release, _entered) = (inner, release, entered);

        public void ScheduleSave(string ns, Func<NamespaceData> dataProvider)
        {
            _entered.Set();
            _release.Wait(TimeSpan.FromSeconds(30));
            _inner.ScheduleSave(ns, dataProvider);
        }

        public bool SupportsIncrementalWrites => false;
        public NamespaceData LoadNamespace(string ns) => _inner.LoadNamespace(ns);
        public IReadOnlyList<string> GetPersistedNamespaces() => _inner.GetPersistedNamespaces();
        public void SaveNamespaceSync(string ns, NamespaceData data) => _inner.SaveNamespaceSync(ns, data);
        public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) => _inner.ScheduleUpsertEntry(ns, entry);
        public void ScheduleDeleteEntry(string ns, string entryId) => _inner.ScheduleDeleteEntry(ns, entryId);
        public List<GraphEdge> LoadGlobalEdges() => _inner.LoadGlobalEdges();
        public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> p) => _inner.ScheduleSaveGlobalEdges(p);
        public List<SemanticCluster> LoadClusters() => _inner.LoadClusters();
        public void ScheduleSaveClusters(Func<List<SemanticCluster>> p) => _inner.ScheduleSaveClusters(p);
        public List<CollapseRecord> LoadCollapseHistory() => _inner.LoadCollapseHistory();
        public bool UpsertCollapseRecordSync(CollapseRecord record) => _inner.UpsertCollapseRecordSync(record);
        public bool DeleteCollapseRecordSync(string collapseId) => _inner.DeleteCollapseRecordSync(collapseId);
        public CollapseRecordCas UpsertCollapseRecordSync(CollapseRecord record, long? onlyIfGeneration) => _inner.UpsertCollapseRecordSync(record, onlyIfGeneration);
        public CollapseRecordCas DeleteCollapseRecordSync(string collapseId, long onlyIfGeneration) => _inner.DeleteCollapseRecordSync(collapseId, onlyIfGeneration);
        public bool TryReadCollapseRecord(string collapseId, out CollapseRecord? record) => _inner.TryReadCollapseRecord(collapseId, out record);
    public bool TryReadCollapseHistory(out List<CollapseRecord> records) => _inner.TryReadCollapseHistory(out records);
        public bool TryFlush() => _inner.TryFlush();
        public Dictionary<string, DecayConfig> LoadDecayConfigs() => _inner.LoadDecayConfigs();
        public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> p) => _inner.ScheduleSaveDecayConfigs(p);
        public HnswSnapshot? LoadHnswSnapshot(string ns) => _inner.LoadHnswSnapshot(ns);
        public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot) => _inner.SaveHnswSnapshotSync(ns, snapshot);
        public void DeleteHnswSnapshot(string ns) => _inner.DeleteHnswSnapshot(ns);
        public Task DeleteNamespaceAsync(string ns) => _inner.DeleteNamespaceAsync(ns);
        public void Flush() => _inner.Flush();
        public void Dispose() => _inner.Dispose();
    }
}
