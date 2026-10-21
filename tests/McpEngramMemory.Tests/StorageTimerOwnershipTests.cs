using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Data.Sqlite;

namespace McpEngramMemory.Tests;

public sealed class StorageTimerOwnershipTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer_ownership_{Guid.NewGuid():N}");
    private readonly ControlledTimeProvider _clock = new();
    private IStorageProvider? _store;

    private IStorageProvider Create(bool sqlite)
    {
        Directory.CreateDirectory(_path);
        return _store = sqlite
            ? new SqliteStorageProvider(Path.Combine(_path, "memory.db")) { TimerProvider = _clock }
            : new PersistenceManager(_path) { TimerProvider = _clock };
    }

    public void Dispose()
    {
        _store?.Dispose();
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_path, "memory.db")}");
        SqliteConnection.ClearPool(connection);
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    public static IEnumerable<object[]> SaveKinds()
    {
        foreach (var sqlite in new[] { false, true })
        foreach (var kind in new[] { "namespace", "edges", "clusters", "decay" })
            yield return new object[] { sqlite, kind };
    }

    public static IEnumerable<object[]> GlobalSaveKinds()
        => SaveKinds().Where(row => (string)row[1] != "namespace");

    public static IEnumerable<object[]> ShutdownSaveKinds()
        => GlobalSaveKinds().SelectMany(row => new[] { false, true }
            .Select(failCapture => new object[] { row[0], row[1], failCapture }));

    private static Task RunDedicated(Action action) => Task.Factory.StartNew(
        action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static string[] DurableGlobalIds(IStorageProvider store, string kind) => kind switch
    {
        "edges" => store.LoadGlobalEdges().Select(e => e.SourceId).ToArray(),
        "clusters" => store.LoadClusters().Select(c => c.ClusterId).ToArray(),
        "decay" => store.LoadDecayConfigs().Keys.ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    [Theory]
    [MemberData(nameof(ShutdownSaveKinds))]
    public async Task Snapshot_DeferredDuringShutdown_IsRetainedForFinalFlush(bool sqlite, string kind, bool failCapture)
    {
        var store = Create(sqlite);
        using var snapshotEntered = new ManualResetEventSlim();
        using var releaseSnapshot = new ManualResetEventSlim();
        int calls = 0;
        Schedule(store, kind, "final", () =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            snapshotEntered.Set();
            if (!releaseSnapshot.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Shutdown did not release snapshot capture");
            if (failCapture) throw new IOException("injected capture failure during shutdown");
        });
        var callback = RunDedicated(_clock.Latest.DeliverQueuedCallback);
        Assert.True(snapshotEntered.Wait(TimeSpan.FromSeconds(5)));
        if (sqlite) store.ScheduleUpsertEntry("ns", Entry("durable"));
        else store.ScheduleSave("ns", () => new NamespaceData { Entries = new() { Entry("durable") } });

        // Observe shutdown's admission flag only to force this interleaving; the
        // assertions below check durable data, not private implementation state.
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic;
        var timerLock = store.GetType().GetField("_timerLock", flags)!.GetValue(store)!;
        var disposed = store.GetType().GetField("_disposed", flags)!;
        var shutdown = RunDedicated(store.Dispose);
        try
        {
            Assert.True(SpinWait.SpinUntil(() =>
            {
                lock (timerLock) return (bool)disposed.GetValue(store)!;
            }, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseSnapshot.Set();
            await Task.WhenAll(callback, shutdown).WaitAsync(TimeSpan.FromSeconds(15));
        }

        Assert.Equal(2, calls);
        Assert.Equal("durable", Assert.Single(store.LoadNamespace("ns").Entries).Text);
        Assert.Equal(new[] { "final" }, DurableGlobalIds(store, kind));
    }

    [Theory]
    [MemberData(nameof(GlobalSaveKinds))]
    public async Task Snapshot_DoesNotBlockEntryScheduling_AndDefersUntilEntriesAreDurable(bool sqlite, string kind)
    {
        var store = Create(sqlite);
        using var snapshotEntered = new ManualResetEventSlim();
        using var entryScheduled = new ManualResetEventSlim();
        bool scheduledDuringSnapshot = false;
        Schedule(store, kind, "dependent", () =>
        {
            snapshotEntered.Set();
            scheduledDuringSnapshot = entryScheduled.Wait(TimeSpan.FromSeconds(5));
        });
        var globalTimer = _clock.Latest;
        var global = RunDedicated(globalTimer.DeliverQueuedCallback);
        Assert.True(snapshotEntered.Wait(TimeSpan.FromSeconds(5)));
        var entry = RunDedicated(() =>
        {
            if (sqlite) store.ScheduleUpsertEntry("ns", Entry("durable"));
            else store.ScheduleSave("ns", () => new NamespaceData { Entries = new() { Entry("durable") } });
            entryScheduled.Set();
        });
        await Task.WhenAll(global, entry).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(scheduledDuringSnapshot, "Snapshot capture held the persistence scheduling lock");
        Assert.Empty(DurableGlobalIds(store, kind));
        Assert.Empty(store.LoadNamespace("ns").Entries);
        Assert.True(store.TryFlush());
        Assert.Equal("durable", Assert.Single(store.LoadNamespace("ns").Entries).Text);
        Assert.Equal(new[] { "dependent" }, DurableGlobalIds(store, kind));
    }

    [Theory]
    [MemberData(nameof(GlobalSaveKinds))]
    public async Task Snapshot_SupersededDuringCapture_CannotPublishOrReplaceNewerSave(bool sqlite, string kind)
    {
        var store = Create(sqlite);
        using var snapshotEntered = new ManualResetEventSlim();
        using var replacementScheduled = new ManualResetEventSlim();
        bool replacedDuringSnapshot = false;
        Schedule(store, kind, "old", () =>
        {
            snapshotEntered.Set();
            replacedDuringSnapshot = replacementScheduled.Wait(TimeSpan.FromSeconds(5));
        });
        var oldTimer = _clock.Latest;
        var old = RunDedicated(oldTimer.DeliverQueuedCallback);
        Assert.True(snapshotEntered.Wait(TimeSpan.FromSeconds(5)));
        var replacement = RunDedicated(() =>
        {
            Schedule(store, kind, "new", () => { });
            replacementScheduled.Set();
        });
        await Task.WhenAll(old, replacement).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(replacedDuringSnapshot, "Snapshot capture held the persistence scheduling lock");
        Assert.Empty(DurableGlobalIds(store, kind));
        _clock.Latest.DeliverQueuedCallback();
        oldTimer.DeliverQueuedCallback();
        Assert.True(store.TryFlush());
        Assert.Equal(new[] { "new" }, DurableGlobalIds(store, kind));
    }

    private static CognitiveEntry Entry(string text, string tenant = "")
        => new("e", new[] { 1f, 0f }, "ns", text: text, tenantId: tenant, lifecycleState: "ltm");

    private static void Schedule(IStorageProvider store, string kind, string version, Action called)
    {
        switch (kind)
        {
            case "namespace":
                store.ScheduleSave("ns", () => { called(); return new NamespaceData { Entries = new() { Entry(version) } }; });
                break;
            case "edges":
                store.ScheduleSaveGlobalEdges(() => { called(); return new() { new GraphEdge(version, "e", "related_to", 1f) }; });
                break;
            case "clusters":
                store.ScheduleSaveClusters(() => { called(); return new() { new SemanticCluster(version, "ns", new() { "e" }) }; });
                break;
            case "decay":
                store.ScheduleSaveDecayConfigs(() => { called(); return new() { [version] = new DecayConfig(version) }; });
                break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    [Theory]
    [MemberData(nameof(SaveKinds))]
    public void ReplacedCallback_DoesNotConsumeNewerSave(bool sqlite, string kind)
    {
        var store = Create(sqlite);
        int oldCalls = 0, newCalls = 0;
        Schedule(store, kind, "old", () => oldCalls++);
        var oldTimer = _clock.Latest;
        Schedule(store, kind, "new", () => newCalls++);
        var replacement = _clock.Latest;
        Assert.True(oldTimer.Disposed);

        // Cancellation raced with dispatch: the old callback still arrives.
        oldTimer.DeliverQueuedCallback();
        Assert.Equal(0, oldCalls);
        Assert.Equal(0, newCalls);
        Assert.False(replacement.Disposed);

        replacement.DeliverQueuedCallback();
        Assert.Equal(0, oldCalls);
        Assert.Equal(1, newCalls);
        Assert.True(replacement.Disposed);
        Assert.True(store.TryFlush());
    }

    [Theory]
    [MemberData(nameof(SaveKinds))]
    public void CallbackQueuedBeforeFlush_DoesNotConsumeNextGeneration(bool sqlite, string kind)
    {
        var store = Create(sqlite);
        int firstCalls = 0, nextCalls = 0;
        Schedule(store, kind, "first", () => firstCalls++);
        var queued = _clock.Latest;
        Assert.True(store.TryFlush());
        Assert.Equal(1, firstCalls);
        Schedule(store, kind, "next", () => nextCalls++);
        var next = _clock.Latest;

        queued.DeliverQueuedCallback();
        Assert.Equal(0, nextCalls);
        next.DeliverQueuedCallback();
        Assert.Equal(1, nextCalls);
    }

    [Theory]
    [MemberData(nameof(SaveKinds))]
    public void FailedWrite_RetainsRetryWhichOnlyItsOwnTimerCanCommit(bool sqlite, string kind)
    {
        var store = Create(sqlite);
        int attempts = 0;
        Schedule(store, kind, "retry", () =>
        {
            if (++attempts == 1) throw new IOException("injected snapshot failure");
        });
        var first = _clock.Latest;
        first.DeliverQueuedCallback();
        Assert.Equal(1, attempts);
        var retry = _clock.Latest;
        Assert.NotSame(first, retry);

        first.DeliverQueuedCallback();
        Assert.Equal(1, attempts);
        retry.DeliverQueuedCallback();
        Assert.Equal(2, attempts);
        Assert.True(store.TryFlush());
    }

    [Theory]
    [MemberData(nameof(SaveKinds))]
    public void Dispose_DrainsSave_AndQueuedCallbackIsHarmless(bool sqlite, string kind)
    {
        var store = Create(sqlite);
        int calls = 0;
        Schedule(store, kind, "final", () => calls++);
        var queued = _clock.Latest;
        store.Dispose();
        Assert.Equal(1, calls);
        Assert.True(queued.Disposed);
        queued.DeliverQueuedCallback();
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false, "edges")]
    [InlineData(false, "clusters")]
    [InlineData(false, "decay")]
    [InlineData(true, "edges")]
    [InlineData(true, "clusters")]
    [InlineData(true, "decay")]
    public void ReplacedGraphCallback_DoesNotPostponeNewTimer_AndCurrentTimerDefersUntilEntriesCommit(
        bool sqlite, string kind)
    {
        var store = Create(sqlite);
        int calls = 0;
        Schedule(store, kind, "old", () => Assert.Fail("replaced provider invoked"));
        var old = _clock.Latest;
        Schedule(store, kind, "new", () =>
        {
            Assert.Equal("durable", Assert.Single(store.LoadNamespace("ns").Entries).Text);
            calls++;
        });
        var current = _clock.Latest;
        store.ScheduleSave("ns", () => new NamespaceData { Entries = new() { Entry("durable") } });
        var entries = _clock.Latest;

        old.DeliverQueuedCallback();
        Assert.Equal(0, current.RearmCount);
        current.DeliverQueuedCallback();
        Assert.Equal(1, current.RearmCount);
        Assert.Equal(0, calls);

        entries.DeliverQueuedCallback();
        current.DeliverQueuedCallback();
        Assert.Equal(1, calls);
        Assert.True(current.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacedIncrementalCallback_DoesNotConsumePendingUpsertOrDelete(bool delete)
    {
        var store = Create(sqlite: true);
        store.SaveNamespaceSync("ns", new NamespaceData { Entries = new() { Entry("original"), Entry("other tenant", "other") } });
        store.ScheduleUpsertEntry("ns", Entry("old"));
        var old = _clock.Latest;
        if (delete) store.ScheduleDeleteEntry("ns", "e");
        else store.ScheduleUpsertEntry("ns", Entry("new"));
        var current = _clock.Latest;

        old.DeliverQueuedCallback();
        Assert.Equal("original", store.LoadNamespace("ns").Entries.Single(e => e.TenantId == "").Text);
        Assert.False(current.Disposed);
        current.DeliverQueuedCallback();
        Assert.True(current.Disposed);
        var durable = store.LoadNamespace("ns").Entries;
        Assert.Equal("other tenant", durable.Single(e => e.TenantId == "other").Text);
        if (delete) Assert.DoesNotContain(durable, e => e.TenantId == "");
        else Assert.Equal("new", durable.Single(e => e.TenantId == "").Text);
    }
}
