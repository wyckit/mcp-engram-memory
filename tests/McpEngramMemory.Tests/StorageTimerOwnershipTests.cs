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
