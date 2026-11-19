using System.Reflection;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Constitution;
using McpEngramMemory.Core.Services.Governance.Persistence;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Data.Sqlite;

namespace McpEngramMemory.Tests;

/// <summary>
/// Regression tests for 2.1.x storage-infra hardening:
/// - JSON filename-aliasing protection & multi-tenant HNSW path separation
/// - Non-blocking _timerLock across global save I/O (JSON & SQLite)
/// - DeleteNamespaceAsync timer-resurrection prevention & empty-key cleanup
/// - Capped exponential backoff on failed debounced writes + immediate TryFlush
/// - SQLite connection-string builder, tenant delete busy_timeout, and Dispose vs collapse RMW
/// - SQL Server PK 900-byte length guard (Error 1946 prevention)
/// - FileConstitutionAuditStore O(1) memory hygiene & T2BenchmarkRun temp artifact isolation
/// </summary>
public sealed class StorageInfraFixesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"storage_infra_fixes_{Guid.NewGuid():N}");

    public StorageInfraFixesTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }

    private string SubDir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static CognitiveEntry MakeEntry(string id, string ns, string text = "body", string tenant = "")
        => new(id, new[] { 1f, 0f, 0f }, ns, text: text, tenantId: tenant, lifecycleState: "ltm");

    private static Task RunDedicated(Action action) => Task.Factory.StartNew(
        action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    // ── 1. JSON filename-aliasing & path-traversal protection ──

    [Fact]
    public void PersistenceManager_UnsafeNamespaceNames_NeverAliasControlPlaneOrOtherNamespaces()
    {
        var dir = SubDir("aliasing");
        using var pm = new PersistenceManager(dir, debounceMs: 60_000);

        // Seed all control-plane files and a normal namespace containing an underscore.
        pm.ScheduleSaveGlobalEdges(() => new List<GraphEdge> { new("src", "dst", "related_to", 0.9f) });
        pm.ScheduleSaveClusters(() => new List<SemanticCluster> { new("c1", "work", new List<string> { "src" }, "lbl") });
        pm.ScheduleSaveDecayConfigs(() => new Dictionary<string, DecayConfig> { ["work"] = new("work", decayRate: 0.42f) });
        Assert.True(pm.TryFlush());
        Assert.True(pm.UpsertCollapseRecordSync(new CollapseRecord(
            "col-1", "c1", "sum-1", "work", new List<string> { "src" }, new Dictionary<string, string>())));
        pm.SaveNamespaceSync("_system_sharing", new NamespaceData { Entries = [MakeEntry("acl-1", "_system_sharing", "owner:alice")] });
        pm.SaveNamespaceSync("foo_bar", new NamespaceData { Entries = [MakeEntry("fb-1", "foo_bar", "legit foo_bar")] });

        // Attempt writes to adversarial namespaces that previously collapsed to _*.json or foo_bar.json.
        var adversarial = new[]
        {
            "..edges",
            "..clusters",
            "..collapse_history",
            "..decay_configs",
            "..system_sharing",
            "foo..bar",
            "foo/bar",
            "foo\\bar",
            "foo:bar",
            "foo.hnsw",
            "_edges",
        };

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < adversarial.Length; i++)
        {
            var ns = adversarial[i];
            var path = pm.GetNamespacePath(ns);
            var fileName = Path.GetFileName(path);

            Assert.False(fileName.StartsWith('_'), $"Encoded filename for '{ns}' must never start with '_': {fileName}");
            Assert.True(seenPaths.Add(path), $"Collision detected for namespace '{ns}' -> {fileName}");

            pm.SaveNamespaceSync(ns, new NamespaceData
            {
                Entries = [MakeEntry($"adv-{i}", ns, $"payload-{i}")]
            });
        }

        // Literal namespace matching an encoded stem must ALSO remain disjoint (strict injectivity).
        var fooDotDotStem = Path.GetFileNameWithoutExtension(pm.GetNamespacePath("foo..bar"));
        Assert.StartsWith("ns_enc_", fooDotDotStem);
        Assert.NotEqual(pm.GetNamespacePath("foo..bar"), pm.GetNamespacePath(fooDotDotStem));
        pm.SaveNamespaceSync(fooDotDotStem, new NamespaceData
        {
            Entries = [MakeEntry("lit-1", fooDotDotStem, "literal-encoded-prefix")]
        });

        // Control-plane files and foo_bar must be completely untouched.
        Assert.Equal("src", Assert.Single(pm.LoadGlobalEdges()).SourceId);
        Assert.Equal("c1", Assert.Single(pm.LoadClusters()).ClusterId);
        Assert.Equal(0.42f, pm.LoadDecayConfigs()["work"].DecayRate);
        Assert.Equal("col-1", Assert.Single(pm.LoadCollapseHistory()).CollapseId);
        Assert.Equal("owner:alice", Assert.Single(pm.LoadNamespace("_system_sharing").Entries).Text);
        Assert.Equal("legit foo_bar", Assert.Single(pm.LoadNamespace("foo_bar").Entries).Text);

        // Every adversarial namespace and the literal ns_enc_* namespace round-trip cleanly.
        for (int i = 0; i < adversarial.Length; i++)
        {
            var loaded = pm.LoadNamespace(adversarial[i]);
            var entry = Assert.Single(loaded.Entries);
            Assert.Equal($"payload-{i}", entry.Text);
        }
        Assert.Equal("literal-encoded-prefix", Assert.Single(pm.LoadNamespace(fooDotDotStem).Entries).Text);

        // GetPersistedNamespaces decodes encoded user namespaces and keeps _*-prefixed ones hidden.
        var persisted = pm.GetPersistedNamespaces();
        Assert.Contains("foo_bar", persisted);
        Assert.Contains("foo..bar", persisted);
        Assert.Contains("..edges", persisted);
        Assert.Contains(fooDotDotStem, persisted);
        Assert.DoesNotContain("_system_sharing", persisted);
        Assert.DoesNotContain("_edges", persisted);
        Assert.DoesNotContain("_clusters", persisted);
        Assert.DoesNotContain("_collapse_history", persisted);
        Assert.DoesNotContain("_decay_configs", persisted);
    }

    [Fact]
    public void PersistenceManager_SafeLegacyNamespaces_KeepExactBackwardCompatibleFilenames()
    {
        var dir = SubDir("legacy-compat");
        using var pm = new PersistenceManager(dir, debounceMs: 60_000);

        Assert.Equal(Path.Combine(dir, "work.json"), pm.GetNamespacePath("work"));
        Assert.Equal(Path.Combine(dir, "my-ns_1.json"), pm.GetNamespacePath("my-ns_1"));
        Assert.Equal(Path.Combine(dir, "_system_sharing.json"), pm.GetNamespacePath("_system_sharing"));
        Assert.Equal(Path.Combine(dir, "work.hnsw.json"), pm.GetHnswPath("work"));
        Assert.Equal(Path.Combine(dir, "acme_notes.hnsw.json"), pm.GetHnswPath("acme_notes"));
    }

    [Fact]
    public void PersistenceManager_LongUnsafeNamespace_UsesHashFallbackAndRoundTrips()
    {
        var dir = SubDir("long-unsafe");
        using var pm = new PersistenceManager(dir, debounceMs: 60_000);

        var longNs = new string('x', 100) + "..tail";
        var path = pm.GetNamespacePath(longNs);
        Assert.StartsWith("ns_enc_h_", Path.GetFileNameWithoutExtension(path));

        pm.SaveNamespaceSync(longNs, new NamespaceData { Entries = [MakeEntry("e1", longNs, "long-payload")] });

        Assert.Equal("long-payload", Assert.Single(pm.LoadNamespace(longNs).Entries).Text);
        Assert.Contains(longNs, pm.GetPersistedNamespaces());
    }

    [Fact]
    public void PersistenceManager_HnswSnapshots_TenantPartitionNeverCollidesWithLegacyUnderscoreNamespace()
    {
        var dir = SubDir("hnsw-tenant");
        using var pm = new PersistenceManager(dir, debounceMs: 60_000);

        var legacyKey = "acme_notes";
        var tenantKey = NamespaceStore.PartitionKey("acme", "notes"); // "acme\x1Fnotes"

        var legacyPath = pm.GetHnswPath(legacyKey);
        var tenantPath = pm.GetHnswPath(tenantKey);

        Assert.NotEqual(legacyPath, tenantPath);
        Assert.Equal("acme_notes.hnsw.json", Path.GetFileName(legacyPath));
        Assert.StartsWith("hnsw_t_", Path.GetFileName(tenantPath));
        Assert.EndsWith(".hnsw.json", Path.GetFileName(tenantPath));

        pm.SaveHnswSnapshotSync(legacyKey, new HnswSnapshot { NodeIds = ["legacy-node"] });
        pm.SaveHnswSnapshotSync(tenantKey, new HnswSnapshot { NodeIds = ["tenant-node"] });

        Assert.Equal(["legacy-node"], pm.LoadHnswSnapshot(legacyKey)!.NodeIds);
        Assert.Equal(["tenant-node"], pm.LoadHnswSnapshot(tenantKey)!.NodeIds);

        // Deleting tenant snapshot must not affect legacy snapshot.
        pm.DeleteHnswSnapshot(tenantKey);
        Assert.Null(pm.LoadHnswSnapshot(tenantKey));
        Assert.Equal(["legacy-node"], pm.LoadHnswSnapshot(legacyKey)!.NodeIds);
    }

    [Fact]
    public async Task PersistenceManager_TenantScopedDelete_RemovesTenantHnswWhenLastTenantDeleted()
    {
        var dir = SubDir("hnsw-tenant-delete");
        using var pm = new PersistenceManager(dir, debounceMs: 60_000);

        var tenantKey = NamespaceStore.PartitionKey("acme", "notes");
        pm.SaveNamespaceSync("notes", new NamespaceData { Entries = [MakeEntry("e1", "notes", "a", tenant: "acme")] });
        pm.SaveHnswSnapshotSync(tenantKey, new HnswSnapshot { NodeIds = ["e1"] });
        Assert.NotNull(pm.LoadHnswSnapshot(tenantKey));

        await pm.DeleteNamespaceAsync("notes", "acme");

        Assert.Empty(pm.LoadNamespace("notes").Entries);
        Assert.Null(pm.LoadHnswSnapshot(tenantKey));
    }

    // ── 2. _timerLock never held across blocking global save I/O ──

    [Theory]
    [InlineData(false, "edges")]
    [InlineData(false, "clusters")]
    [InlineData(false, "decay")]
    [InlineData(true, "edges")]
    [InlineData(true, "clusters")]
    [InlineData(true, "decay")]
    public async Task GlobalSaveTimerCallback_DoesNotHoldTimerLockDuringBlockingIO(bool sqlite, string kind)
    {
        var dir = SubDir($"nonblock_{sqlite}_{kind}");
        var clock = new ControlledTimeProvider();
        using IStorageProvider store = sqlite
            ? new SqliteStorageProvider(Path.Combine(dir, "memory.db")) { TimerProvider = clock }
            : new PersistenceManager(dir) { TimerProvider = clock };

        using var ioEntered = new ManualResetEventSlim();
        using var releaseIo = new ManualResetEventSlim();
        int hookCount = 0;
        Action<string> hook = _ =>
        {
            if (Interlocked.Increment(ref hookCount) == 1)
            {
                ioEntered.Set();
                if (!releaseIo.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Test did not release blocked global write");
            }
        };

        if (store is PersistenceManager pm) pm.OnBeforeGlobalWrite = hook;
        else if (store is SqliteStorageProvider sq) sq.OnBeforeGlobalWrite = hook;

        ScheduleGlobal(store, kind, "v1");
        var timer = clock.Latest;
        var callbackTask = RunDedicated(timer.DeliverQueuedCallback);

        try
        {
            Assert.True(ioEntered.Wait(TimeSpan.FromSeconds(5)), "Global write did not reach I/O hook");

            // While global I/O is blocked, scheduling operations acquiring _timerLock must NOT block.
            var concurrentSchedules = RunDedicated(() =>
            {
                if (sqlite)
                    store.ScheduleUpsertEntry("other-ns", MakeEntry("e1", "other-ns", "concurrent"));
                else
                    store.ScheduleSave("other-ns", () => new NamespaceData { Entries = [MakeEntry("e1", "other-ns", "concurrent")] });
                ScheduleGlobal(store, kind, "v2");
            });

            await concurrentSchedules.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(callbackTask.IsCompleted, "I/O hook should still be blocked");
        }
        finally
        {
            releaseIo.Set();
            await callbackTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.True(store.TryFlush());
        Assert.Equal("concurrent", Assert.Single(store.LoadNamespace("other-ns").Entries).Text);
        Assert.Equal(new[] { "v2" }, ReadGlobalIds(store, kind));
    }

    // ── 3. DeleteNamespaceAsync cancels queued timers and prevents resurrection ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteNamespaceAsync_CancelsPendingTimers_PreventsResurrection(bool sqlite)
    {
        var dir = SubDir($"delete_race_{sqlite}");
        var clock = new ControlledTimeProvider();
        using IStorageProvider store = sqlite
            ? new SqliteStorageProvider(Path.Combine(dir, "memory.db")) { TimerProvider = clock }
            : new PersistenceManager(dir) { TimerProvider = clock };

        store.SaveNamespaceSync("victim", new NamespaceData { Entries = [MakeEntry("old", "victim", "on-disk")] });
        store.ScheduleSave("victim", () => new NamespaceData { Entries = [MakeEntry("resurrect-full", "victim", "stale")] });
        var fullTimer = clock.Latest;

        ControlledTimeProvider.ControlledTimer? incTimer = null;
        if (sqlite)
        {
            store.ScheduleUpsertEntry("victim", MakeEntry("resurrect-inc", "victim", "stale-inc"));
            incTimer = clock.Latest;
        }

        await store.DeleteNamespaceAsync("victim");

        Assert.True(fullTimer.Disposed);
        if (incTimer is not null)
            Assert.True(incTimer.Disposed);

        // Even if queued callbacks fire after DeleteNamespaceAsync, nothing is resurrected.
        fullTimer.DeliverQueuedCallback();
        incTimer?.DeliverQueuedCallback();
        Assert.True(store.TryFlush());

        Assert.Empty(store.LoadNamespace("victim").Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteNamespaceAsync_TenantScoped_CancelsDeletedTenantPendingWorkAndPreservesOtherTenant(bool sqlite)
    {
        var dir = SubDir($"delete_tenant_race_{sqlite}");
        var clock = new ControlledTimeProvider();
        using IStorageProvider store = sqlite
            ? new SqliteStorageProvider(Path.Combine(dir, "memory.db")) { TimerProvider = clock }
            : new PersistenceManager(dir) { TimerProvider = clock };

        var entryA = MakeEntry("shared-id", "shared", "tenant-a-data", tenant: "a");
        var entryB = MakeEntry("shared-id", "shared", "tenant-b-data", tenant: "b");
        store.SaveNamespaceSync("shared", new NamespaceData { Entries = [entryA, entryB] });

        var timersToDeliver = new List<ControlledTimeProvider.ControlledTimer>();
        if (sqlite)
        {
            store.ScheduleUpsertEntry("shared", MakeEntry("shared-id", "shared", "tenant-a-pending", tenant: "a"));
            timersToDeliver.Add(clock.Latest);
            store.ScheduleUpsertEntry("shared", MakeEntry("b-new", "shared", "tenant-b-pending", tenant: "b"));
            timersToDeliver.Add(clock.Latest);
        }
        else
        {
            store.ScheduleSave("shared", () => new NamespaceData
            {
                Entries =
                [
                    MakeEntry("shared-id", "shared", "tenant-a-pending", tenant: "a"),
                    entryB,
                    MakeEntry("b-new", "shared", "tenant-b-pending", tenant: "b")
                ]
            });
            timersToDeliver.Add(clock.Latest);
        }

        await store.DeleteNamespaceAsync("shared", "a");

        foreach (var t in timersToDeliver)
            t.DeliverQueuedCallback();
        Assert.True(store.TryFlush());

        var loaded = store.LoadNamespace("shared").Entries;
        Assert.DoesNotContain(loaded, e => e.TenantId == "a");
        Assert.Contains(loaded, e => e.TenantId == "b" && e.Id == "shared-id");
        Assert.Contains(loaded, e => e.TenantId == "b" && e.Id == "b-new");
    }

    // ── 4. Empty namespace keys removed after flush (no HasPendingEntryLevelWork leak) ──

    [Fact]
    public void Sqlite_FlushAndIncrementalTimer_RemovesEmptyNamespaceKeysFromPendingDictionaries()
    {
        var dir = SubDir("empty-keys");
        var clock = new ControlledTimeProvider();
        using var store = new SqliteStorageProvider(Path.Combine(dir, "memory.db")) { TimerProvider = clock };

        for (int i = 0; i < 25; i++)
        {
            store.ScheduleUpsertEntry($"ns-timer-{i}", MakeEntry("e1", $"ns-timer-{i}"));
            clock.Latest.DeliverQueuedCallback();
        }

        for (int i = 0; i < 25; i++)
        {
            store.ScheduleUpsertEntry($"ns-flush-{i}", MakeEntry("e1", $"ns-flush-{i}"));
            store.ScheduleDeleteEntry($"ns-del-{i}", "e1", "");
        }
        Assert.True(store.TryFlush());

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var upsertsField = typeof(SqliteStorageProvider).GetField("_pendingEntryUpserts", flags)!;
        var deletesField = typeof(SqliteStorageProvider).GetField("_pendingEntryDeletes", flags)!;
        var upsertsDict = (System.Collections.IDictionary)upsertsField.GetValue(store)!;
        var deletesDict = (System.Collections.IDictionary)deletesField.GetValue(store)!;

        Assert.Empty(upsertsDict);
        Assert.Empty(deletesDict);
    }

    // ── 5. Capped exponential backoff on failed debounced writes + immediate TryFlush ──

    [Theory]
    [InlineData(false, "namespace")]
    [InlineData(false, "edges")]
    [InlineData(false, "clusters")]
    [InlineData(false, "decay")]
    [InlineData(true, "namespace")]
    [InlineData(true, "edges")]
    [InlineData(true, "clusters")]
    [InlineData(true, "decay")]
    public void FailedDebouncedWrite_UsesCappedExponentialBackoff_AndResetsOnSuccess_WhileTryFlushIsImmediate(
        bool sqlite, string kind)
    {
        var dir = SubDir($"backoff_{sqlite}_{kind}");
        var clock = new ControlledTimeProvider();
        using IStorageProvider store = sqlite
            ? new SqliteStorageProvider(Path.Combine(dir, "memory.db"), debounceMs: 500) { TimerProvider = clock }
            : new PersistenceManager(dir, debounceMs: 500) { TimerProvider = clock };

        bool shouldFail = true;
        Action failHook = () =>
        {
            if (shouldFail)
                throw new IOException("simulated persistent I/O failure");
        };

        ScheduleWithHook(store, kind, "v1", failHook);
        Assert.Equal(TimeSpan.FromMilliseconds(500), clock.Latest.DueTime);

        var expectedDelaysMs = new[] { 1000, 2000, 4000, 8000, 16000, 30000, 30000 };
        foreach (var expectedMs in expectedDelaysMs)
        {
            var current = clock.Latest;
            current.DeliverQueuedCallback();
            var next = clock.Latest;
            Assert.NotSame(current, next);
            Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), next.DueTime);
        }

        // TryFlush must commit immediately without waiting for the 30s backoff timer.
        shouldFail = false;
        Assert.True(store.TryFlush());

        // Subsequent failure starts fresh from 1s (500ms * 2^1), confirming backoff reset on success.
        shouldFail = true;
        ScheduleWithHook(store, kind, "v2", failHook);
        Assert.Equal(TimeSpan.FromMilliseconds(500), clock.Latest.DueTime);
        clock.Latest.DeliverQueuedCallback();
        Assert.Equal(TimeSpan.FromMilliseconds(1000), clock.Latest.DueTime);

        shouldFail = false;
        Assert.True(store.TryFlush());
    }

    // ── 6. SQLite connection-string builder, busy_timeout on tenant delete, Dispose vs collapse RMW ──

    [Fact]
    public void Sqlite_PathWithSemicolon_UsesConnectionStringBuilderSafely()
    {
        var dir = SubDir("semi;colon_dir");
        var dbPath = Path.Combine(dir, "mem;ory.db");
        using var store = new SqliteStorageProvider(dbPath, debounceMs: 10);

        store.SaveNamespaceSync("ns", new NamespaceData { Entries = [MakeEntry("e1", "ns", "works")] });
        var loaded = store.LoadNamespace("ns");

        Assert.Equal("works", Assert.Single(loaded.Entries).Text);
    }

    [Fact]
    public async Task Sqlite_DeleteNamespaceAsync_TenantScoped_HonorsBusyTimeoutUnderLockContention()
    {
        var dir = SubDir("sqlite-busy-timeout");
        var dbPath = Path.Combine(dir, "memory.db");
        using var store = new SqliteStorageProvider(dbPath, debounceMs: 10);

        store.SaveNamespaceSync("shared", new NamespaceData
        {
            Entries =
            [
                MakeEntry("id1", "shared", "tenant-a", tenant: "a"),
                MakeEntry("id1", "shared", "tenant-b", tenant: "b")
            ]
        });

        // Hold an explicit SQLite write lock so a missing busy_timeout would fail immediately with SQLITE_BUSY.
        var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        using var blocker = new SqliteConnection(connStr);
        blocker.Open();
        using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
        }
        using (var touch = blocker.CreateCommand())
        {
            touch.CommandText = "UPDATE schema_version SET version = version;";
            touch.ExecuteNonQuery();
        }

        var deleteTask = Task.Run(() => store.DeleteNamespaceAsync("shared", "a"));

        // Must wait on busy_timeout rather than failing immediately with SQLITE_BUSY.
        var finishedEarly = await Task.WhenAny(deleteTask, Task.Delay(250));
        Assert.NotSame(deleteTask, finishedEarly);
        Assert.False(deleteTask.IsFaulted);

        using (var commit = blocker.CreateCommand())
        {
            commit.CommandText = "COMMIT;";
            commit.ExecuteNonQuery();
        }

        await deleteTask.WaitAsync(TimeSpan.FromSeconds(5));

        var remaining = store.LoadNamespace("shared").Entries;
        var survivor = Assert.Single(remaining);
        Assert.Equal("b", survivor.TenantId);
    }

    [Fact]
    public async Task Sqlite_Dispose_WaitsForInFlightCollapseHistoryMutationBeforeCheckpointWal()
    {
        var dir = SubDir("sqlite-dispose-collapse");
        var dbPath = Path.Combine(dir, "memory.db");
        var store = new SqliteStorageProvider(dbPath, debounceMs: 10);

        using var commitEntered = new ManualResetEventSlim();
        using var releaseCommit = new ManualResetEventSlim();
        store.OnBeforeCollapseCommit = () =>
        {
            commitEntered.Set();
            if (!releaseCommit.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test did not release collapse commit");
        };

        var record = new CollapseRecord(
            "collapse:in-flight", "c1", "sum-1", "ns",
            new List<string> { "m1" }, new Dictionary<string, string>());

        var mutateTask = Task.Factory.StartNew(
            () => store.UpsertCollapseRecordSync(record),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        Assert.True(commitEntered.Wait(TimeSpan.FromSeconds(5)));

        var disposeTask = RunDedicated(store.Dispose);
        try
        {
            // Dispose must block until the active collapse-history transaction completes.
            var completedEarly = await Task.WhenAny(disposeTask, Task.Delay(200));
            Assert.NotSame(disposeTask, completedEarly);
        }
        finally
        {
            releaseCommit.Set();
            await Task.WhenAll(mutateTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.True(await mutateTask);

        // Reopen database and confirm the record survived Dispose + CheckpointWal cleanly.
        using var reopened = new SqliteStorageProvider(dbPath, debounceMs: 10);
        Assert.True(reopened.TryReadCollapseRecord("collapse:in-flight", out var loaded));
        Assert.NotNull(loaded);
        Assert.Equal("c1", loaded!.ClusterId);
    }

    // ── 7. SQL Server PK key-length guard (Error 1946 prevention) ──

    [Fact]
    public void SqlServer_ValidateEntryKeyLengths_Enforces900ByteClusteredIndexLimit()
    {
        // Exactly 450 UTF-16 chars (900 bytes) succeeds.
        SqlServerStorageProvider.ValidateEntryKeyLengths(
            new string('t', 64), new string('n', 128), new string('i', 258));

        // 451 UTF-16 chars (902 bytes) exceeds SQL Server's 900-byte clustered PK limit.
        var ex = Assert.Throws<ArgumentException>(() =>
            SqlServerStorageProvider.ValidateEntryKeyLengths(
                new string('t', 64), new string('n', 128), new string('i', 259)));
        Assert.Contains("900-byte", ex.Message);
        Assert.Contains("1946", ex.Message);

        // tenant_id > 64 chars fails column width guard.
        Assert.Throws<ArgumentException>(() =>
            SqlServerStorageProvider.ValidateEntryKeyLengths(
                new string('t', 65), "ns", "id"));
    }

    // ── 8. FileConstitutionAuditStore memory hygiene ──

    [Fact]
    public async Task FileConstitutionAuditStore_DoesNotRetainFullRecordsListInMemory_AndPreservesSemantics()
    {
        // Ensure no unbounded List<ConstitutionAuditRecord> field is retained on the store instance.
        var recordListFields = typeof(FileConstitutionAuditStore)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(List<ConstitutionAuditRecord>))
            .ToList();
        Assert.Empty(recordListFields);

        var dir = SubDir("audit-hygiene");
        var storeA = new FileConstitutionAuditStore(dir);
        var storeB = new FileConstitutionAuditStore(dir);

        for (int i = 1; i <= 10; i++)
        {
            var target = i % 2 == 1 ? storeA : storeB;
            var appended = await target.AppendAsync(new ConstitutionAuditRecord(
                0, $"evt-{i}", $"op-{i}", "tenant-1", "agent-1",
                ConstitutionPhase.Precondition, ConstitutionOutcome.Allow,
                [RootConstitution.Version.ContentHash], [], DateTimeOffset.UtcNow));
            Assert.Equal(i, appended.Sequence);
        }

        var all = await storeA.ReadAllAsync();
        Assert.Equal(10, all.Count);
        Assert.Equal(Enumerable.Range(1, 10).Select(x => (long)x), all.Select(r => r.Sequence));
    }

    // ── 9. T2BenchmarkRun writes to temp path unless BENCHMARK_ARTIFACTS_PATH is set ──

    [Fact]
    public void T2BenchmarkRun_ResolveArtifactDirectory_DefaultsToTempPathWhenEnvUnset()
    {
        var previous = Environment.GetEnvironmentVariable("BENCHMARK_ARTIFACTS_PATH");
        try
        {
            Environment.SetEnvironmentVariable("BENCHMARK_ARTIFACTS_PATH", null);
            var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
            var dir1 = T2BenchmarkRun.ResolveArtifactDirectory(now);
            var dir2 = T2BenchmarkRun.ResolveArtifactDirectory(now);

            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(dir1), StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(dir1, dir2);

            var customRoot = SubDir("explicit-bench");
            Environment.SetEnvironmentVariable("BENCHMARK_ARTIFACTS_PATH", customRoot);
            var explicitDir = T2BenchmarkRun.ResolveArtifactDirectory(now);
            Assert.Equal(Path.Combine(customRoot, "2026-11-19"), explicitDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BENCHMARK_ARTIFACTS_PATH", previous);
        }
    }

    // ── Helpers ──

    private static void ScheduleGlobal(IStorageProvider store, string kind, string id)
        => ScheduleWithHook(store, kind, id, static () => { });

    private static void ScheduleWithHook(IStorageProvider store, string kind, string id, Action hook)
    {
        switch (kind)
        {
            case "namespace":
                store.ScheduleSave("ns", () => { hook(); return new NamespaceData { Entries = [MakeEntry(id, "ns", id)] }; });
                break;
            case "edges":
                store.ScheduleSaveGlobalEdges(() => { hook(); return [new GraphEdge(id, "target", "related_to", 1f)]; });
                break;
            case "clusters":
                store.ScheduleSaveClusters(() => { hook(); return [new SemanticCluster(id, "ns", ["e1"])]; });
                break;
            case "decay":
                store.ScheduleSaveDecayConfigs(() => { hook(); return new Dictionary<string, DecayConfig> { [id] = new(id) }; });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static string[] ReadGlobalIds(IStorageProvider store, string kind) => kind switch
    {
        "edges" => store.LoadGlobalEdges().Select(e => e.SourceId).ToArray(),
        "clusters" => store.LoadClusters().Select(c => c.ClusterId).ToArray(),
        "decay" => store.LoadDecayConfigs().Keys.ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
