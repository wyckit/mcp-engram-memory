using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpEngramMemory.Core.Models;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Storage;

/// <summary>
/// JSON file-based persistence per namespace with debounced async writes.
/// Uses Base64 encoding for float[] vectors to reduce disk footprint by ~60%.
/// </summary>
public sealed class PersistenceManager : IStorageProvider
{
    /// <summary>Current storage format version. Increment when making breaking changes to the JSON schema.</summary>
    public const int CurrentStorageVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new FloatArrayBase64Converter() }
    };

    private readonly string _basePath;
    private readonly object _timerLock = new();
    private readonly TimeSpan _debounceDelay;
    private readonly ILogger<PersistenceManager>? _logger;
    private bool _disposed;

    // Tracks every write running file I/O outside the timer lock — debounced callbacks AND
    // synchronous collapse-history RMWs. Flush() waits on _writesIdle so callers (e.g. tests
    // deleting the data dir on teardown) cannot race a still-running write. Every tracked
    // write begins under _flushGate, so a flush holding the gate drains a BOUNDED set.
    private int _inFlightWrites;
    private readonly ManualResetEventSlim _writesIdle = new(initialState: true);

    // Pending namespace saves (keyed by namespace name)
    private readonly Dictionary<string, (Timer Timer, Func<NamespaceData> DataProvider)> _pendingNsSaves = new();

    // Pending global edge save (separate from namespace saves to avoid dummy-data overwrite)
    private Timer? _pendingEdgeTimer;
    private Func<List<GraphEdge>>? _pendingEdgeProvider;

    // Pending cluster save
    private Timer? _pendingClusterTimer;
    private Func<List<SemanticCluster>>? _pendingClusterProvider;

    // Pending collapse history save

    // Pending decay config save
    private Timer? _pendingDecayConfigTimer;
    private Func<Dictionary<string, DecayConfig>>? _pendingDecayConfigProvider;

    /// <summary>JSON backend does not support incremental writes — always uses full namespace snapshots.</summary>
    public bool SupportsIncrementalWrites => false;

    /// <inheritdoc />
    public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) { }

    /// <inheritdoc />
    public void ScheduleDeleteEntry(string ns, string entryId) { }

    public PersistenceManager(string? basePath = null, int debounceMs = 500, ILogger<PersistenceManager>? logger = null)
    {
        // Frozen ABSOLUTE at construction: a relative path resolved per operation would move
        // the backing store (and the gate identity) whenever CurrentDirectory changes.
        _basePath = Path.GetFullPath(basePath ?? Path.Combine(AppContext.BaseDirectory, "data"));
        _debounceDelay = TimeSpan.FromMilliseconds(debounceMs);
        _logger = logger;

        // Load-bearing for GetPersistedNamespaces, not merely a convenience for the first write:
        // it is what makes "the data directory is not there" an anomaly rather than an ordinary
        // first run, so that method can refuse instead of reporting an empty store. A failure here
        // fails construction, which is the fail-closed answer — a PersistenceManager that never
        // established its directory must not exist to be enumerated.
        Directory.CreateDirectory(_basePath);

        // LAST, after everything that can throw: a constructor that failed after acquiring
        // would leak a refcount no Dispose will ever release.
        _collapseHistoryGate = AcquireCollapseHistoryGate(StoreIdentity);
    }

    /// <summary>
    /// Load namespace data from disk. Returns empty data if file does not exist or is corrupted.
    /// Validates checksum if a companion .sha256 file exists.
    /// </summary>
    public NamespaceData LoadNamespace(string ns)
    {
        var path = GetNamespacePath(ns);
        if (!File.Exists(path))
            return new NamespaceData { StorageVersion = CurrentStorageVersion };

        try
        {
            var json = File.ReadAllText(path);
            if (!VerifyChecksum(path, json))
            {
                _logger?.LogWarning("Checksum mismatch for namespace '{Namespace}', data may be corrupted", ns);
            }
            var data = JsonSerializer.Deserialize<NamespaceData>(json, JsonOptions) ?? new NamespaceData();

            // Forward-compatibility guard: reject files from newer versions
            if (data.StorageVersion > CurrentStorageVersion)
            {
                _logger?.LogError(
                    "Namespace '{Namespace}' has storage version {FileVersion} but this server supports up to version {CurrentVersion}. " +
                    "Upgrade the server or use the version that created this data.",
                    ns, data.StorageVersion, CurrentStorageVersion);
                return new NamespaceData { StorageVersion = CurrentStorageVersion };
            }

            // Run migrations for older versions
            if (data.StorageVersion < CurrentStorageVersion)
            {
                data = RunMigrations(data, ns);
            }

            return data;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Corrupted JSON in namespace '{Namespace}', returning empty data", ns);
            return new NamespaceData { StorageVersion = CurrentStorageVersion };
        }
    }

    /// <summary>
    /// Run sequential migrations from the data's version to CurrentStorageVersion.
    /// Follows the same pattern as SqliteStorageProvider.RunMigrations.
    /// </summary>
    private NamespaceData RunMigrations(NamespaceData data, string ns)
    {
        int fromVersion = data.StorageVersion;

        // v1 → v2: No structural changes needed — v1 data is compatible.
        // Future migrations add cases here:
        // if (data.StorageVersion < 3) MigrateToV3(data);

        data.StorageVersion = CurrentStorageVersion;
        _logger?.LogInformation("Namespace '{Namespace}' migrated from storage v{From} to v{To}", ns, fromVersion, CurrentStorageVersion);

        // Persist the migrated data immediately
        SaveNamespaceSync(ns, data);
        return data;
    }

    /// <summary>
    /// Load global (cross-namespace) edges from disk. Returns empty list if file is corrupted.
    /// </summary>
    public List<GraphEdge> LoadGlobalEdges()
        => LoadGlobalFile<List<GraphEdge>>(Path.Combine(_basePath, "_edges.json"), "edges") ?? new();

    /// <summary>
    /// Load clusters from disk. Returns empty list if file is corrupted.
    /// </summary>
    public List<SemanticCluster> LoadClusters()
        => LoadGlobalFile<List<SemanticCluster>>(Path.Combine(_basePath, "_clusters.json"), "clusters") ?? new();

    /// <summary>
    /// Load collapse history from disk.
    /// </summary>
    // Envelope-aware (see TryReadHistorySet for the format) and LENIENT, like every boot
    // load: a checksum mismatch here warns and returns the data — refusal is the strict
    // paths' job, and a boot that silently dropped the whole history would orphan every
    // receipt it carries.
    public List<CollapseRecord> LoadCollapseHistory()
    {
        var path = Path.Combine(_basePath, "_collapse_history.json");
        if (!File.Exists(path))
            return new();

        try
        {
            var content = File.ReadAllText(path);
            var trimmed = content.TrimStart();
            if (trimmed.StartsWith('['))
                return DeserializeCollapseHistoryLenient(content);

            using var doc = System.Text.Json.JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("records", out var recordsEl))
            {
                _logger?.LogWarning("Collapse-history file has an unrecognized shape; returning empty data");
                return new();
            }
            if (doc.RootElement.TryGetProperty("checksum", out var checksumEl))
            {
                var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recordsEl.GetRawText())));
                if (!string.Equals(actual, checksumEl.GetString(), StringComparison.OrdinalIgnoreCase))
                    _logger?.LogWarning("Checksum mismatch for collapse history file, data may be corrupted");
            }
            return DeserializeCollapseHistoryLenient(recordsEl.GetRawText());
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Corrupted JSON in collapse history file, returning empty data");
            return new();
        }
    }

    // The LENIENT half of the malformed-row rule: boot loads degrade by DROPPING a null-shaped
    // row (with a warning) so one bad row cannot brick the cache keying, while the strict
    // reads refuse the whole set (HistorySetIsWellFormed) so nothing malformed is ever acted
    // on or laundered into a commit.
    private List<CollapseRecord> DeserializeCollapseHistoryLenient(string json)
    {
        var set = CollapseRecordShape.DeserializeLenient(json, JsonOptions, out var dropped);
        if (dropped > 0)
            _logger?.LogWarning(
                "Collapse-history boot load dropped {Dropped} malformed, duplicate, or forward-unknown " +
                "record row(s); this store holds data this version cannot safely act on and should be repaired.",
                dropped);
        return set;
    }

    /// <summary>
    /// Load per-namespace decay configs from disk.
    /// </summary>
    public Dictionary<string, DecayConfig> LoadDecayConfigs()
    {
        var list = LoadGlobalFile<List<DecayConfig>>(Path.Combine(_basePath, "_decay_configs.json"), "decay configs");
        // Key by the (tenant, ns) partition so two tenants can each hold a config for the same
        // namespace without colliding. Legacy configs (tenant "") key on the bare ns as before.
        // A store poisoned before partition-component validation existed must still boot, so the
        // shared builder resolves a colliding pair rather than throwing on a duplicate key.
        return NamespaceStore.DecayConfigsByPartition(list, _logger);
    }

    private T? LoadGlobalFile<T>(string path, string label) where T : class
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            if (!VerifyChecksum(path, json))
                _logger?.LogWarning("Checksum mismatch for {Label} file, data may be corrupted", label);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Corrupted JSON in {Label} file, returning empty data", label);
            return null;
        }
    }

    /// <summary>
    /// Schedule a debounced save of namespace data.
    /// </summary>
    public void ScheduleSave(string ns, Func<NamespaceData> dataProvider)
    {
        lock (_timerLock)
        {
            if (_disposed) return;

            if (_pendingNsSaves.TryGetValue(ns, out var existing))
                existing.Timer.Dispose();

            var timer = new Timer(_ =>
            {
                // CAPTURE AND COMMIT under the flush gate, like the SQL providers: a callback
                // that captured before a flush drained cannot land its (older) snapshot after
                // the flush's writes, and a callback firing DURING the flush's write phase
                // waits -- commit order always follows capture order, which also makes the
                // retention's slot-empty check truthful (nothing newer can have committed
                // behind its back). A failed write is RETAINED (newer-wins): TryFlush vouches
                // for durability, and a snapshot silently lost on the timer path would let it
                // vouch over a hole.
                lock (_flushGate)
                {
                    Func<NamespaceData>? provider = null;
                    lock (_timerLock)
                    {
                        if (_pendingNsSaves.TryGetValue(ns, out var entry))
                        {
                            provider = entry.DataProvider;
                            entry.Timer.Dispose();
                            _pendingNsSaves.Remove(ns);
                            BeginTrackedWrite();
                        }
                    }
                    if (provider is not null)
                        RunWriteAndRelease(() =>
                        {
                            if (!WriteNamespace(ns, provider))
                                RetainNsSave(ns, provider);
                        });
                }
            }, null, _debounceDelay, Timeout.InfiniteTimeSpan);

            _pendingNsSaves[ns] = (timer, dataProvider);
        }
    }

    // Mark a debounced write as in-flight. Must be called while holding _timerLock so
    // Flush() — which takes the same lock — observes the increment before it inspects
    // _writesIdle. Without that ordering, a callback could pass the lock check, exit
    // the lock, and only then increment, after Flush already returned through Wait().
    private void BeginTrackedWrite()
    {
        if (Interlocked.Increment(ref _inFlightWrites) == 1)
            _writesIdle.Reset();
    }

    // Run the write and release the in-flight slot. The lock must already have been
    // exited so the file I/O does not block other ScheduleSave callers — but the RELEASE
    // re-takes it: BeginTrackedWrite's increment-and-Reset runs under _timerLock, and a
    // decrement-and-Set that did not would let this exact interleaving mark the manager
    // idle with a write in flight — A decrements to 0, B (under the lock) increments to 1
    // and finds the event already reset, A's stale Set then fires — after which Flush
    // returns early, Dispose tears down the event and releases the store gate UNDER B's
    // still-running read-modify-write. Flush waits on the event OUTSIDE the lock, so
    // taking it here cannot deadlock.
    private void RunWriteAndRelease(Action write)
    {
        try { write(); }
        finally
        {
            lock (_timerLock)
            {
                if (Interlocked.Decrement(ref _inFlightWrites) == 0)
                    _writesIdle.Set();
            }
        }
    }

    /// <summary>
    /// Schedule a debounced save of global edges.
    /// Data provider should return a pre-captured snapshot (no lock re-entry).
    /// </summary>
    public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider)
    {
        lock (_timerLock)
        {
            if (_disposed) return;

            _pendingEdgeTimer?.Dispose();
            _pendingEdgeProvider = dataProvider;

            _pendingEdgeTimer = new Timer(_ =>
            {
                // CAPTURE AND TRACK inside the flush gate, like the namespace callback: a
                // callback that marked itself in-flight BEFORE blocking on the gate deadlocked
                // with TryFlush, which holds the gate while waiting for tracked writes to go
                // idle. Under the gate, tracking and writing are one uninterruptible step.
                lock (_flushGate)
                {
                    Func<List<GraphEdge>>? provider;
                    lock (_timerLock)
                    {
                        // CROSS-QUEUE CAUSAL ORDER on the timer path — see TryFlushCore's
                        // part 2: a graph-level save referencing entries by id must not
                        // commit while namespace writes are still pending, or a crash
                        // reloads durable topology naming entries that never became
                        // durable. Deferred, not dropped; a flush drains both in order.
                        if (_pendingNsSaves.Count > 0)
                        {
                            _pendingEdgeTimer?.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
                            return;
                        }
                        provider = _pendingEdgeProvider;
                        _pendingEdgeProvider = null;
                        _pendingEdgeTimer?.Dispose();
                        _pendingEdgeTimer = null;
                        if (provider is not null) BeginTrackedWrite();
                    }
                    if (provider is not null)
                        RunWriteAndRelease(() =>
                        {
                            // THE CHECK AND THE COMMIT ARE ONE ATOM, under the lock that
                            // guards the namespace queue. Checking and then releasing before
                            // writing — which is what this used to do — left exactly the
                            // window it was meant to close: ScheduleSave queues a namespace
                            // save under _timerLock ALONE, not the flush gate this callback
                            // holds, so one landing between the check and the commit was
                            // still overtaken. A narrower race is not a closed one.
                            //
                            // The lock is therefore held ACROSS the write, deliberately. It
                            // stalls namespace scheduling for the duration of one graph-level
                            // file write, and that is the cheaper cost: a graph save that
                            // beats its entries to disk reloads, after a crash, as durable
                            // topology naming entries that never became durable. Deferred,
                            // not dropped, exactly as above.
                            lock (_timerLock)
                            {
                                if (_pendingNsSaves.Count > 0 || !WriteGlobalEdges(provider))
                                {
                                    if (!_disposed && _pendingEdgeProvider is null)
                                        ScheduleSaveGlobalEdges(provider);
                                }
                            }
                        });
                }
            }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Schedule a debounced save of clusters.
    /// Data provider should return a pre-captured snapshot (no lock re-entry).
    /// </summary>
    public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider)
    {
        lock (_timerLock)
        {
            if (_disposed) return;

            _pendingClusterTimer?.Dispose();
            _pendingClusterProvider = dataProvider;

            _pendingClusterTimer = new Timer(_ =>
            {
                // CAPTURE AND TRACK inside the flush gate, like the namespace callback: a
                // callback that marked itself in-flight BEFORE blocking on the gate deadlocked
                // with TryFlush, which holds the gate while waiting for tracked writes to go
                // idle. Under the gate, tracking and writing are one uninterruptible step.
                lock (_flushGate)
                {
                    Func<List<SemanticCluster>>? provider;
                    lock (_timerLock)
                    {
                        // CROSS-QUEUE CAUSAL ORDER on the timer path — see TryFlushCore's
                        // part 2: a graph-level save referencing entries by id must not
                        // commit while namespace writes are still pending, or a crash
                        // reloads durable topology naming entries that never became
                        // durable. Deferred, not dropped; a flush drains both in order.
                        if (_pendingNsSaves.Count > 0)
                        {
                            _pendingClusterTimer?.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
                            return;
                        }
                        provider = _pendingClusterProvider;
                        _pendingClusterProvider = null;
                        _pendingClusterTimer?.Dispose();
                        _pendingClusterTimer = null;
                        if (provider is not null) BeginTrackedWrite();
                    }
                    if (provider is not null)
                        RunWriteAndRelease(() =>
                        {
                            // THE CHECK AND THE COMMIT ARE ONE ATOM, under the lock that
                            // guards the namespace queue. Checking and then releasing before
                            // writing — which is what this used to do — left exactly the
                            // window it was meant to close: ScheduleSave queues a namespace
                            // save under _timerLock ALONE, not the flush gate this callback
                            // holds, so one landing between the check and the commit was
                            // still overtaken. A narrower race is not a closed one.
                            //
                            // The lock is therefore held ACROSS the write, deliberately. It
                            // stalls namespace scheduling for the duration of one graph-level
                            // file write, and that is the cheaper cost: a graph save that
                            // beats its entries to disk reloads, after a crash, as durable
                            // topology naming entries that never became durable. Deferred,
                            // not dropped, exactly as above.
                            lock (_timerLock)
                            {
                                if (_pendingNsSaves.Count > 0 || !WriteClusters(provider))
                                {
                                    if (!_disposed && _pendingClusterProvider is null)
                                        ScheduleSaveClusters(provider);
                                }
                            }
                        });
                }
            }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The store's durable identity: the data directory canonicalized by
    /// <see cref="StoreIdentityUtil.CanonicalPath"/> — resolved, prefix-stripped,
    /// separator-trimmed, case-folded on Windows — so alias spellings (a trailing backslash,
    /// a <c>\?\</c> prefix) of one directory share one identity. Every consumer that equates
    /// identities ordinally (the scanner's in-flight gate) relies on this; the gate table
    /// below additionally compares case-insensitively.</summary>
    public string StoreIdentity => StoreIdentityUtil.CanonicalPath(_basePath);

    // One collapse-history gate per STORE, never per provider instance: two instances over the
    // same directory each hold their own locks, so instance-local gating let their
    // read-modify-writes interleave and lose each other's records. Keyed by the resolved base
    // path; process-wide. (Cross-OS-process coordination is out of scope here, as it is for
    // every other structure this store persists.)
    //
    // REFERENCE-COUNTED so the table cannot grow forever: each manager acquires its store's
    // gate at construction and releases it at Dispose, and a key with no live holders is
    // removed. A gate held only by leaked (never-disposed) managers persists, which is the
    // leak's fault, not the table's.
    private static readonly object s_collapseHistoryGateTableLock = new();
    private static readonly Dictionary<string, (object Gate, int Holders)> s_collapseHistoryGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _collapseHistoryGate;

    private static object AcquireCollapseHistoryGate(string storeKey)
    {
        lock (s_collapseHistoryGateTableLock)
        {
            if (s_collapseHistoryGates.TryGetValue(storeKey, out var slot))
            {
                s_collapseHistoryGates[storeKey] = (slot.Gate, slot.Holders + 1);
                return slot.Gate;
            }
            var gate = new object();
            s_collapseHistoryGates[storeKey] = (gate, 1);
            return gate;
        }
    }

    private static void ReleaseCollapseHistoryGate(string storeKey)
    {
        lock (s_collapseHistoryGateTableLock)
        {
            if (!s_collapseHistoryGates.TryGetValue(storeKey, out var slot))
                return;
            if (slot.Holders <= 1) s_collapseHistoryGates.Remove(storeKey);
            else s_collapseHistoryGates[storeKey] = (slot.Gate, slot.Holders - 1);
        }
    }

    /// <summary>
    /// The INTERPROCESS half of the collapse-history gate: an exclusively-held lock file in
    /// the store directory, spanning read-through-replace. The in-process gate above cannot
    /// stop a SECOND PROCESS from reading the same history and replacing it with a conflicting
    /// snapshot — last-write-wins erases records, and even the generation CAS is only atomic
    /// when the compare and the write cannot interleave with another process's. Every
    /// history-store operation (mutate, conditional ops, strict read) runs its body while the
    /// lock file is held with <see cref="FileShare.None"/>; a process that cannot acquire it
    /// within the timeout gets <paramref name="failValue"/> — an honest refusal, never a
    /// blind write. The stream is opened fresh per operation and never deleted, so the file's
    /// identity is stable for every process over this store.
    /// </summary>
    private T WithInterprocessHistoryLock<T>(T failValue, Func<T> body)
    {
        var lockPath = Path.Combine(_basePath, "_collapse_history.lock");
        const int attempts = 120;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            FileStream? stream = null;
            try
            {
                stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(25);
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(25);
                continue;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Interprocess collapse-history lock could not be opened");
                return failValue;
            }

            try { return body(); }
            finally { stream.Dispose(); }
        }

        _logger?.LogError("Interprocess collapse-history lock could not be acquired within the timeout; operation refused");
        return failValue;
    }

    /// <summary>
    /// Record-level synchronous collapse-history writes — see <see cref="IStorageProvider"/>.
    /// </summary>
    public bool UpsertCollapseRecordSync(CollapseRecord record)
    {
        if (CollapseRecordShape.Describe(record) is { } defect)
        {
            _logger?.LogError("Collapse-history upsert refused: {Defect}", defect);
            return false;
        }
        return MutateCollapseHistorySync(records =>
        {
            records.RemoveAll(r => r.CollapseId == record.CollapseId);
            records.Add(record);
            return true;
        });
    }

    public bool DeleteCollapseRecordSync(string collapseId)
        => MutateCollapseHistorySync(records => records.RemoveAll(r => r.CollapseId == collapseId) > 0);

    /// <summary>
    /// The generation-compared delete — compare and removal one atom inside both gates. See
    /// <see cref="IStorageProvider.DeleteCollapseRecordSync(string, long)"/>.
    /// </summary>
    public CollapseRecordCas DeleteCollapseRecordSync(string collapseId, long onlyIfGeneration)
    {
        var outcome = CollapseRecordCas.StoreFailed;
        bool storeAgrees = MutateCollapseHistorySync(records =>
        {
            var current = records.FirstOrDefault(r => r.CollapseId == collapseId);
            if (current is null) { outcome = CollapseRecordCas.AlreadyAbsent; return false; }
            if (current.Generation != onlyIfGeneration) { outcome = CollapseRecordCas.GenerationMoved; return false; }
            records.RemoveAll(r => r.CollapseId == collapseId);
            outcome = CollapseRecordCas.Applied;
            return true;
        });
        return storeAgrees ? outcome : CollapseRecordCas.StoreFailed;
    }

    /// <summary>
    /// Strict single-record read — a failed or checksum-refuted read REFUSES (false) rather
    /// than reporting the record absent, because <see cref="LoadCollapseHistory"/> deliberately
    /// degrades to an empty list and a verification caller must be able to tell "gone" from
    /// "unreadable". Taken under both gates so it cannot race a commit mid-replace, in this
    /// process or another.
    /// </summary>
    public bool TryReadCollapseRecord(string collapseId, out CollapseRecord? record)
    {
        record = null;
        if (!TryReadCollapseHistory(out var records))
            return false;
        record = records.FirstOrDefault(r => r.CollapseId == collapseId);
        return true;
    }

    /// <summary>Strict full-set read — see <see cref="IStorageProvider.TryReadCollapseHistory"/>.
    /// Same gates, same checksum discipline as the single-record read.</summary>
    public bool TryReadCollapseHistory(out List<CollapseRecord> records)
    {
        List<CollapseRecord> found = new();
        bool ok;
        lock (_collapseHistoryGate)
        {
            ok = WithInterprocessHistoryLock(false, () =>
            {
                var path = Path.Combine(_basePath, "_collapse_history.json");
                return TryReadHistorySet(path, out found);
            });
        }
        records = found;
        return ok;
    }

    // The read-modify-write behind every record-level sync op, entirely inside BOTH gates —
    // the in-process store gate and the interprocess lock file: read the CURRENT set
    // (strictly — an unreadable or checksum-refuted set must fail the call, never masquerade
    // as empty, or the commit would erase records this caller never saw — and never launder
    // tampered content into the next commit), apply the one edit, commit through the same
    // commit-precise writer. The mutate callback reports whether it CHANGED the set: a no-op
    // (deleting a record that is not there, refusing a generation compare) commits nothing
    // and reports true — the store already agrees.
    private bool MutateCollapseHistorySync(Func<List<CollapseRecord>, bool> mutate)
    {
        // GATE FIRST, tracking second — the same discipline as the debounced timer
        // callbacks. These RMWs feed _inFlightWrites (the teardown-safety counter), and a
        // tracked write that BEGINS while a flush owns the gate re-arms the drain loop: a
        // sustained stream of collapse RMWs keeps the count nonzero forever and the flush
        // starves every debounced save in the process behind the gate it holds. Entering
        // the gate first bounds the drain to the writes already in flight. The write itself
        // runs OUTSIDE the gate — the flush never touches the history file, so only the
        // tracking handshake needs the gate, not the (interprocess-lock-waiting) commit.
        lock (_flushGate)
        {
            lock (_timerLock)
            {
                if (_disposed) return false;
                BeginTrackedWrite();
            }
        }
        bool committed = false;
        RunWriteAndRelease(() =>
        {
            lock (_collapseHistoryGate)
            {
                committed = WithInterprocessHistoryLock(false, () =>
                {
                    var path = Path.Combine(_basePath, "_collapse_history.json");
                    if (!TryReadHistorySet(path, out var records))
                        return false;

                    return !mutate(records) || TryCommitCollapseHistory(records, path);
                });
            }
        });
        return committed;
    }

    // The strict, checksum-validated read every history operation shares. False REFUSES —
    // unreadable, unparsable, or failing its checksum (valid-JSON tampering with a stale
    // checksum must not be accepted and then normalized by the next commit). A missing file is
    // a definitive EMPTY set.
    //
    // The checksum lives INSIDE the file, as an envelope {"checksum": ..., "records": [...]}
    // committed by one atomic replace — a COMPANION-file checksum had an unhealable crash
    // window (die between the replace and the companion write, and every later strict read
    // refuses perfectly good data forever). The hash covers the records element's raw text
    // exactly as written. A raw-ARRAY file is pre-envelope data; it was written WITH a
    // companion .sha256, so the array branch still validates against the companion when one
    // exists (a stale companion refuses; a missing one is truly-legacy and passes).
    private bool TryReadHistorySet(string path, out List<CollapseRecord> records)
    {
        records = new List<CollapseRecord>();
        try
        {
            if (!File.Exists(path)) return true;
            var content = File.ReadAllText(path);
            var trimmed = content.TrimStart();

            if (trimmed.StartsWith('['))
            {
                if (!VerifyChecksum(path, content))
                {
                    _logger?.LogError("Collapse-history read refused: pre-envelope content does not match its checksum companion");
                    return false;
                }
                if (!CollapseRecordShape.TryDeserializeStrict(
                        content, JsonOptions, out records, out var defect))
                {
                    _logger?.LogError("Collapse-history read refused: {Defect}", defect);
                    records = new List<CollapseRecord>();
                    return false;
                }
                return true;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("checksum", out var checksumEl)
                || !doc.RootElement.TryGetProperty("records", out var recordsEl))
            {
                _logger?.LogError("Collapse-history read refused: unrecognized envelope shape");
                return false;
            }

            var recordsRaw = recordsEl.GetRawText();
            var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recordsRaw)));
            if (!string.Equals(actual, checksumEl.GetString(), StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogError("Collapse-history read refused: records do not match the envelope checksum");
                return false;
            }

            if (!CollapseRecordShape.TryDeserializeStrict(
                    recordsRaw, JsonOptions, out records, out var envelopeDefect))
            {
                _logger?.LogError("Collapse-history read refused: {Defect}", envelopeDefect);
                records = new List<CollapseRecord>();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Collapse-history read refused: the current set could not be read");
            return false;
        }
    }

    /// <summary>
    /// The generation-compared upsert — compare and write one atom inside both gates. See
    /// <see cref="IStorageProvider.UpsertCollapseRecordSync(CollapseRecord, long?)"/>.
    /// </summary>
    public CollapseRecordCas UpsertCollapseRecordSync(CollapseRecord record, long? onlyIfGeneration)
    {
        if (CollapseRecordShape.Describe(record) is { } defect)
        {
            _logger?.LogError("Conditional collapse-history upsert refused: {Defect}", defect);
            return CollapseRecordCas.StoreFailed;
        }
        var outcome = CollapseRecordCas.StoreFailed;
        bool storeAgrees = MutateCollapseHistorySync(records =>
        {
            var current = records.FirstOrDefault(r => r.CollapseId == record.CollapseId);
            // NULL expected = must be absent; a NUMBER (0 included -- legacy records carry a
            // real generation 0) must match a RESIDENT record. See the interface contract.
            if (current is null && onlyIfGeneration is not null) { outcome = CollapseRecordCas.AlreadyAbsent; return false; }
            if (current is not null && (onlyIfGeneration is null || current.Generation != onlyIfGeneration.Value)) { outcome = CollapseRecordCas.GenerationMoved; return false; }
            records.RemoveAll(r => r.CollapseId == record.CollapseId);
            records.Add(record);
            outcome = CollapseRecordCas.Applied;
            return true;
        });
        return storeAgrees ? outcome : CollapseRecordCas.StoreFailed;
    }

    // Returns true ONLY once the atomic replace has demonstrably happened; every failure before
    // it leaves the previous file untouched, and nothing after it can flip the answer — a
    // post-commit cleanup exception reported as failure would make the caller roll back state
    // the store has already accepted, the divergence this boundary exists to prevent.
    //
    // The payload is the checksum ENVELOPE (see TryReadHistorySet): the hash and the records
    // it covers land in ONE atomic replace, so there is no instant at which a strict reader
    // can see one without the other. Composed textually so the raw records element read back
    // is byte-identical to the string hashed here.
    private bool TryCommitCollapseHistory(List<CollapseRecord> records, string path)
    {
        string json;
        try
        {
            var recordsJson = JsonSerializer.Serialize(records, JsonOptions);
            var recordsChecksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recordsJson)));
            json = $"{{\"checksum\":\"{recordsChecksum}\",\"records\":{recordsJson}}}";
        }
        catch (Exception ex) { _logger?.LogError(ex, "Collapse-history serialize failed"); return false; }

        // Unique per attempt: a shared ".tmp" name lets two concurrent writers (a second
        // provider instance in particular) overwrite each other's staging file, committing the
        // wrong payload under a "successful" replace.
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(tmp, json); }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Collapse-history temp write failed");
            try { File.Delete(tmp); } catch { /* best effort */ }
            return false;
        }

        try
        {
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Collapse-history commit failed; previous file left intact");
            try { File.Delete(tmp); } catch { /* best effort */ }
            return false;
        }

        // Retire any pre-envelope companion, best-effort and post-commit: the envelope file
        // carries its own checksum, no current reader consults a companion for envelope-shaped
        // content, and a stale companion left behind would spuriously refuse the raw-array
        // legacy validation if the file ever reverted shape.
        try { File.Delete(path + ".sha256"); }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Stale collapse-history checksum companion could not be removed; the commit stands");
        }

        return true;
    }

    /// <summary>
    /// Schedule a debounced save of decay configs.
    /// </summary>
    public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider)
    {
        lock (_timerLock)
        {
            if (_disposed) return;

            _pendingDecayConfigTimer?.Dispose();
            _pendingDecayConfigProvider = dataProvider;

            _pendingDecayConfigTimer = new Timer(_ =>
            {
                // CAPTURE AND TRACK inside the flush gate, like the namespace callback: a
                // callback that marked itself in-flight BEFORE blocking on the gate deadlocked
                // with TryFlush, which holds the gate while waiting for tracked writes to go
                // idle. Under the gate, tracking and writing are one uninterruptible step.
                lock (_flushGate)
                {
                    Func<Dictionary<string, DecayConfig>>? provider;
                    lock (_timerLock)
                    {
                        // CROSS-QUEUE CAUSAL ORDER on the timer path — see TryFlushCore's
                        // part 2: a graph-level save referencing entries by id must not
                        // commit while namespace writes are still pending, or a crash
                        // reloads durable topology naming entries that never became
                        // durable. Deferred, not dropped; a flush drains both in order.
                        if (_pendingNsSaves.Count > 0)
                        {
                            _pendingDecayConfigTimer?.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
                            return;
                        }
                        provider = _pendingDecayConfigProvider;
                        _pendingDecayConfigProvider = null;
                        _pendingDecayConfigTimer?.Dispose();
                        _pendingDecayConfigTimer = null;
                        if (provider is not null) BeginTrackedWrite();
                    }
                    if (provider is not null)
                        RunWriteAndRelease(() =>
                        {
                            // THE CHECK AND THE COMMIT ARE ONE ATOM, under the lock that
                            // guards the namespace queue. Checking and then releasing before
                            // writing — which is what this used to do — left exactly the
                            // window it was meant to close: ScheduleSave queues a namespace
                            // save under _timerLock ALONE, not the flush gate this callback
                            // holds, so one landing between the check and the commit was
                            // still overtaken. A narrower race is not a closed one.
                            //
                            // The lock is therefore held ACROSS the write, deliberately. It
                            // stalls namespace scheduling for the duration of one graph-level
                            // file write, and that is the cheaper cost: a graph save that
                            // beats its entries to disk reloads, after a crash, as durable
                            // topology naming entries that never became durable. Deferred,
                            // not dropped, exactly as above.
                            lock (_timerLock)
                            {
                                if (_pendingNsSaves.Count > 0 || !WriteDecayConfigs(provider))
                                {
                                    if (!_disposed && _pendingDecayConfigProvider is null)
                                        ScheduleSaveDecayConfigs(provider);
                                }
                            }
                        });
                }
            }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Synchronously save namespace data.
    /// </summary>
    public void SaveNamespaceSync(string ns, NamespaceData data)
    {
        data.StorageVersion = CurrentStorageVersion;
        var path = GetNamespacePath(ns);
        var json = JsonSerializer.Serialize(data, JsonOptions);
        AtomicWriteAllText(path, json);
    }

    /// <summary>
    /// Get all namespace names from existing files on disk. An empty list means one thing only:
    /// <see cref="Directory.GetFiles(string, string)"/> ran to completion over the data directory and found no
    /// namespace files. Every other outcome throws.
    ///
    /// An I/O failure throws rather than degrading to an empty list, matching the database
    /// providers. The two used to be the same value, and a caller cannot separate them by
    /// inspection — so the full-load sweep would mark itself complete over a failed listing and
    /// leave persisted entries invisible for the life of the process. An invisible twin makes a
    /// duplicated id look unique, which is the fail-open answer to the tenant-wide duplicate test
    /// topology depends on.
    ///
    /// The absent directory is included in that, and no longer short-circuits to empty. An
    /// <see cref="Directory.Exists"/> pre-check cannot tell a store that has no directory yet from
    /// one whose directory has been moved, unmounted or had its ACL revoked underneath a running
    /// process, and only the first of those is an empty store. The constructor removes the
    /// ambiguity by creating the directory, so by the time anyone can reach this method the
    /// never-created case cannot occur and an absence can only be the unavailable one — which
    /// arrives here as the <see cref="DirectoryNotFoundException"/> from the listing itself and is
    /// refused like any other failure to read.
    /// </summary>
    /// <exception cref="NamespaceEnumerationException">The data directory could not be listed.</exception>
    public IReadOnlyList<string> GetPersistedNamespaces()
    {
        try
        {
            return Directory.GetFiles(_basePath, "*.json")
                .Where(f => !f.EndsWith(".hnsw.json", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n != null && !n.StartsWith("_") && !n.StartsWith("__"))
                .Select(n => n!)
                .ToList();
        }
        catch (Exception ex)
        {
            // Catching broadly, like the database providers do, so the boundary raises exactly one
            // type whatever the backend failed with — a caller that has to enumerate the ways a
            // directory walk can fail is back to being able to miss one. Logged here with the path
            // and rethrown without it, so a caller-visible error cannot disclose the data
            // directory's location.
            _logger?.LogWarning(ex, "Error listing namespace files under '{BasePath}'", _basePath);
            throw new NamespaceEnumerationException(ex);
        }
    }

    /// <summary>
    /// Flush all pending saves immediately and synchronously.
    /// </summary>
    public void Flush() => TryFlush();

    // Serializes whole flushes: without it, an older flush blocked mid-way can write its
    // (older) captured snapshots AFTER a newer flush committed and returned true — commit
    // order inverting capture order. One flush at a time; the SQL providers carry the same
    // gate for the same reason.
    private readonly object _flushGate = new();

    /// <summary>
    /// Flush every pending debounced write and REPORT: true only when all of them committed.
    /// A failed write is RE-SCHEDULED — but only into an EMPTY slot: a newer snapshot that
    /// arrived meanwhile supersedes the failed one, and clobbering it would resurrect stale
    /// state under a "retained" banner. During Dispose the re-schedule is refused by the
    /// disposed check and dropped with an explicit log — the process is going away, and there
    /// is nobody left to retry.
    /// </summary>
    public bool TryFlush()
    {
        lock (_flushGate)
            return TryFlushCore(refuseWhenDisposed: true);
    }

    private bool TryFlushCore(bool refuseWhenDisposed)
    {
        // A PUBLIC flush that reaches the gate AFTER Dispose must refuse here, not race the
        // drain event: Dispose sets the flag (under _timerLock) before taking the gate and
        // disposes _writesIdle only after releasing it, so every post-dispose entrant
        // observes the flag at this check — while a pre-dispose entrant holds the gate
        // until it finishes, keeping the event alive under its Wait. Without the check, the
        // Wait below threw ObjectDisposedException into callers expecting a bool — the same
        // refusal discipline as MutateCollapseHistorySync's post-dispose false. Dispose's
        // OWN final flush passes refuseWhenDisposed: false — it set the flag itself and the
        // event is still alive until it returns.
        if (refuseWhenDisposed)
        {
            lock (_timerLock)
            {
                if (_disposed) return false;
            }
        }

        List<(string Ns, Func<NamespaceData> Provider)> pendingNs;
        Func<List<GraphEdge>>? edgeProvider;
        Func<List<SemanticCluster>>? clusterProvider;
        Func<Dictionary<string, DecayConfig>>? decayConfigProvider;

        // CROSS-QUEUE CAUSAL ORDER, part 3 — graph payloads MATERIALIZE BEFORE the drain,
        // for the reason the SQL providers state: entry changes apply-and-schedule in one
        // atom, so everything materialized here is covered by the entry writes this flush
        // commits, where a write-time materialization could name entries scheduled after
        // the drain — committed topology naming uncommitted entries across a crash. A
        // provider replaced between this read and the drain stays pending.
        Func<List<GraphEdge>>? edgeProviderRef;
        Func<List<SemanticCluster>>? clusterProviderRef;
        Func<Dictionary<string, DecayConfig>>? decayProviderRef;
        lock (_timerLock)
        {
            edgeProviderRef = _pendingEdgeProvider;
            clusterProviderRef = _pendingClusterProvider;
            decayProviderRef = _pendingDecayConfigProvider;
        }
        List<GraphEdge>? edgeData = null;
        List<SemanticCluster>? clusterData = null;
        Dictionary<string, DecayConfig>? decayData = null;
        bool edgeMaterialized = false, clusterMaterialized = false, decayMaterialized = false;
        try { if (edgeProviderRef is not null) { edgeData = edgeProviderRef(); edgeMaterialized = true; } } catch (Exception ex) { _logger?.LogError(ex, "Edge payload materialization failed; the pending save is retained"); }
        try { if (clusterProviderRef is not null) { clusterData = clusterProviderRef(); clusterMaterialized = true; } } catch (Exception ex) { _logger?.LogError(ex, "Cluster payload materialization failed; the pending save is retained"); }
        try { if (decayProviderRef is not null) { decayData = decayProviderRef(); decayMaterialized = true; } } catch (Exception ex) { _logger?.LogError(ex, "Decay-config payload materialization failed; the pending save is retained"); }

        // Drain only once the tracked writes are QUIESCENT, and prove it while holding the
        // checkpoint lock. Two orderings both fail alone: draining first lets an in-flight
        // callback (older snapshot, tracked before the drain) land AFTER our writes; waiting
        // first lets a callback slip its checkpoint between the wait and the drain, fail its
        // write, and RETAIN into a slot this flush already decided was empty — TryFlush would
        // then vouch over the retained hole. Checking the in-flight count UNDER _timerLock
        // closes both: a callback increments it under this same lock, so zero-while-held means
        // nothing is in flight and nothing can take the checkpoint until the drain finishes.
        // A graph slot refilled between our materialization and this drain is deliberately
        // LEFT for its own timer — resurrecting our older snapshot over it would invert
        // capture order. But it is still a pending debounced write that this flush did not
        // commit, and the contract is "true only when all of them committed", so it must
        // withhold the vouch. Reporting true here let a caller treat a slot it could see was
        // still occupied as durable.
        bool supersededDuringDrain = false;

        while (true)
        {
            _writesIdle.Wait();
            lock (_timerLock)
            {
                if (Volatile.Read(ref _inFlightWrites) != 0)
                    continue;

                pendingNs = _pendingNsSaves
                    .Select(kv => (kv.Key, kv.Value.DataProvider))
                    .ToList();
                foreach (var (_, (timer, _)) in _pendingNsSaves)
                    timer.Dispose();
                _pendingNsSaves.Clear();

                // Drain a graph slot only while it still holds the provider whose payload
                // was materialized above — see part 3.
                if (ReferenceEquals(_pendingEdgeProvider, edgeProviderRef))
                {
                    edgeProvider = _pendingEdgeProvider;
                    _pendingEdgeProvider = null;
                    _pendingEdgeTimer?.Dispose();
                    _pendingEdgeTimer = null;
                }
                // Not ours any more. Something newer is queued there and this flush is
                // not committing it — record that so the report stays honest.
                else { edgeProvider = null; supersededDuringDrain |= _pendingEdgeProvider is not null; }

                if (ReferenceEquals(_pendingClusterProvider, clusterProviderRef))
                {
                    clusterProvider = _pendingClusterProvider;
                    _pendingClusterProvider = null;
                    _pendingClusterTimer?.Dispose();
                    _pendingClusterTimer = null;
                }
                // Not ours any more. Something newer is queued there and this flush is
                // not committing it — record that so the report stays honest.
                else { clusterProvider = null; supersededDuringDrain |= _pendingClusterProvider is not null; }

                if (ReferenceEquals(_pendingDecayConfigProvider, decayProviderRef))
                {
                    decayConfigProvider = _pendingDecayConfigProvider;
                    _pendingDecayConfigProvider = null;
                    _pendingDecayConfigTimer?.Dispose();
                    _pendingDecayConfigTimer = null;
                }
                // Not ours any more. Something newer is queued there and this flush is
                // not committing it — record that so the report stays honest.
                else { decayConfigProvider = null; supersededDuringDrain |= _pendingDecayConfigProvider is not null; }
                break;
            }
        }

        bool allCommitted = !supersededDuringDrain;

        foreach (var (ns, provider) in pendingNs)
        {
            if (!WriteNamespace(ns, provider))
            {
                allCommitted = false;
                RetainNsSave(ns, provider);
            }
        }

        // CROSS-QUEUE CAUSAL ORDER — the graph-level saves (edges, clusters, decay) commit
        // only when every ENTRY-level namespace write above committed: a cluster or edge
        // save references entries by id (a cluster's SummaryEntryId in particular), and
        // committing it over a failed-and-retained namespace write, then crashing, reloads
        // durable topology naming entries that never became durable. On failure they are
        // RETAINED unattempted through their own re-schedule paths (the trailing teardown
        // wait below still runs; only the dependent writes are withheld).
        if (!allCommitted)
        {
            lock (_timerLock)
            {
                if (_disposed)
                {
                    if (edgeProvider is not null) _logger?.LogError("Pending global-edges write dropped: provider disposed during a failed flush");
                    if (clusterProvider is not null) _logger?.LogError("Pending clusters write dropped: provider disposed during a failed flush");
                    if (decayConfigProvider is not null) _logger?.LogError("Pending decay-config write dropped: provider disposed during a failed flush");
                }
                else
                {
                    if (edgeProvider is not null && _pendingEdgeProvider is null) ScheduleSaveGlobalEdges(edgeProvider);
                    if (clusterProvider is not null && _pendingClusterProvider is null) ScheduleSaveClusters(clusterProvider);
                    if (decayConfigProvider is not null && _pendingDecayConfigProvider is null) ScheduleSaveDecayConfigs(decayConfigProvider);
                }
            }
            edgeProvider = null;
            clusterProvider = null;
            decayConfigProvider = null;
        }

        // The lock is reentrant, so the check and the (re-)schedule are one atom: a newer
        // provider that landed in the slot since the drain supersedes the failed one and the
        // failed snapshot is dropped rather than resurrected over it.
        if (edgeProvider is not null && (!edgeMaterialized || !WriteGlobalEdges(() => edgeData!)))
        {
            allCommitted = false;
            lock (_timerLock)
            {
                if (_disposed)
                    _logger?.LogError("Pending global-edges write dropped: provider disposed during a failed flush");
                else if (_pendingEdgeProvider is null)
                    ScheduleSaveGlobalEdges(edgeProvider);
            }
        }

        if (clusterProvider is not null && (!clusterMaterialized || !WriteClusters(() => clusterData!)))
        {
            allCommitted = false;
            lock (_timerLock)
            {
                if (_disposed)
                    _logger?.LogError("Pending clusters write dropped: provider disposed during a failed flush");
                else if (_pendingClusterProvider is null)
                    ScheduleSaveClusters(clusterProvider);
            }
        }

        if (decayConfigProvider is not null && (!decayMaterialized || !WriteDecayConfigs(() => decayData!)))
        {
            allCommitted = false;
            lock (_timerLock)
            {
                if (_disposed)
                    _logger?.LogError("Pending decay-config write dropped: provider disposed during a failed flush");
                else if (_pendingDecayConfigProvider is null)
                    ScheduleSaveDecayConfigs(decayConfigProvider);
            }
        }

        // And once more after our own writes, for the teardown guarantee: a caller deleting
        // the data dir must not race a write still in progress.
        _writesIdle.Wait();

        // FINAL LINEARIZATION CHECKPOINT. Every schedule publishes under _timerLock, so at
        // this instant every write queued before the checkpoint is either committed above or
        // visible here. An entry queued while one of the writes above was blocked therefore
        // prevents a true report. Work scheduled after this lock is released is ordered after
        // the checkpoint and belongs to the next flush.
        lock (_timerLock)
        {
            if (_pendingNsSaves.Count > 0
                || _pendingEdgeProvider is not null
                || _pendingClusterProvider is not null
                || _pendingDecayConfigProvider is not null)
            {
                allCommitted = false;
            }
        }
        return allCommitted;
    }

    // Retention with newer-wins: re-queue the failed snapshot ONLY when no newer one is
    // pending for the same slot (snapshot providers capture at schedule time, so the failed
    // one is strictly older than anything scheduled since).
    private void RetainNsSave(string ns, Func<NamespaceData> provider)
    {
        lock (_timerLock)
        {
            if (_disposed)
            {
                _logger?.LogError("Pending write for namespace '{Namespace}' dropped: provider disposed during a failed flush", ns);
                return;
            }
            if (_pendingNsSaves.ContainsKey(ns))
                return;
            // Inside the (reentrant) lock so the emptiness check and the schedule are one
            // atom — a newer snapshot landing in between must win, not be clobbered.
            ScheduleSave(ns, provider);
        }
    }

    /// <summary>Load persisted HNSW graph snapshot for a namespace.</summary>
    public HnswSnapshot? LoadHnswSnapshot(string ns)
    {
        var path = GetHnswPath(ns);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            if (!VerifyChecksum(path, json))
                _logger?.LogWarning("Checksum mismatch for HNSW snapshot '{Namespace}'", ns);
            return JsonSerializer.Deserialize<HnswSnapshot>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Corrupted HNSW snapshot for namespace '{Namespace}', will rebuild", ns);
            return null;
        }
    }

    /// <summary>Save an HNSW graph snapshot for a namespace.</summary>
    public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot)
    {
        var path = GetHnswPath(ns);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        AtomicWriteAllText(path, json);
    }

    /// <summary>Delete persisted HNSW snapshot for a namespace.</summary>
    public void DeleteHnswSnapshot(string ns)
    {
        var path = GetHnswPath(ns);
        if (File.Exists(path)) File.Delete(path);
        var checksumPath = path + ".sha256";
        if (File.Exists(checksumPath)) File.Delete(checksumPath);
    }

    /// <summary>Delete all entries in a namespace by removing its JSON and checksum files from disk.</summary>
    public Task DeleteNamespaceAsync(string ns)
    {
        var path = GetNamespacePath(ns);
        if (File.Exists(path)) File.Delete(path);
        var checksumPath = path + ".sha256";
        if (File.Exists(checksumPath)) File.Delete(checksumPath);
        DeleteHnswSnapshot(ns);
        return Task.CompletedTask;
    }

    /// <summary>Delete only one tenant partition while preserving co-named partitions.</summary>
    public Task DeleteNamespaceAsync(string ns, string tenantId)
    {
        tenantId = string.IsNullOrWhiteSpace(tenantId) ? string.Empty : tenantId.Trim();
        var data = LoadNamespace(ns);
        var remaining = data.Entries.Where(entry => entry.TenantId != tenantId).ToList();
        if (remaining.Count == 0)
            return DeleteNamespaceAsync(ns);

        data.Entries = remaining;
        SaveNamespaceSync(ns, data);
        DeleteHnswSnapshot(NamespaceStore.PartitionKey(tenantId, ns));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_timerLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // The disposing thread's own final flush — bypasses the post-dispose refusal it
        // itself armed; see TryFlushCore.
        lock (_flushGate)
            TryFlushCore(refuseWhenDisposed: false);
        _writesIdle.Dispose();
        ReleaseCollapseHistoryGate(StoreIdentity);
    }

    private string GetNamespacePath(string ns)
    {
        // Sanitize namespace for filename safety
        var safe = string.Join("_", ns.Split(Path.GetInvalidFileNameChars()));
        safe = safe.Replace("..", "_"); // Prevent path traversal
        var path = Path.Combine(_basePath, $"{safe}.json");

        // Guard against path traversal
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(_basePath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Invalid namespace: '{ns}'");

        return path;
    }

    private string GetHnswPath(string ns)
    {
        var safe = string.Join("_", ns.Split(Path.GetInvalidFileNameChars()));
        safe = safe.Replace("..", "_");
        var path = Path.Combine(_basePath, $"{safe}.hnsw.json");

        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(_basePath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Invalid namespace: '{ns}'");

        return path;
    }

    // Each writer reports whether its commit demonstrably happened; the debounced timer paths
    // discard the report (their loss is logged, as before), while TryFlush aggregates it and
    // RETAINS what failed.
    private bool WriteNamespace(string ns, Func<NamespaceData> provider)
    {
        try
        {
            var data = provider();
            SaveNamespaceSync(ns, data);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save namespace '{Namespace}'", ns);
            return false;
        }
    }

    private bool WriteGlobalEdges(Func<List<GraphEdge>> provider)
    {
        try
        {
            var edges = provider();
            var json = JsonSerializer.Serialize(edges, JsonOptions);
            var path = Path.Combine(_basePath, "_edges.json");
            AtomicWriteAllText(path, json);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save global edges");
            return false;
        }
    }

    private bool WriteClusters(Func<List<SemanticCluster>> provider)
    {
        try
        {
            var clusters = provider();
            var json = JsonSerializer.Serialize(clusters, JsonOptions);
            var path = Path.Combine(_basePath, "_clusters.json");
            AtomicWriteAllText(path, json);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save clusters");
            return false;
        }
    }

    private bool WriteDecayConfigs(Func<Dictionary<string, DecayConfig>> provider)
    {
        try
        {
            var configs = provider();
            var list = configs.Values.ToList();
            var json = JsonSerializer.Serialize(list, JsonOptions);
            var path = Path.Combine(_basePath, "_decay_configs.json");
            AtomicWriteAllText(path, json);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save decay configs");
            return false;
        }
    }

    /// <summary>Write to a temp file then rename for crash-safe atomic writes. Also writes a SHA-256 checksum companion file.</summary>
    private static void AtomicWriteAllText(string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        // Unique per attempt, like TryCommitCollapseHistory: a shared ".tmp" name lets two
        // concurrent writers (a second provider instance over the same store in particular)
        // overwrite each other's staging file and publish the wrong payload under a
        // "successful" move.
        var tmpPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmpPath, bytes);
        File.Move(tmpPath, path, overwrite: true);

        // Write checksum companion file (reuse already-encoded bytes)
        var hash = SHA256.HashData(bytes);
        var checksumPath = path + ".sha256";
        File.WriteAllText(checksumPath, Convert.ToHexString(hash));
    }

    /// <summary>
    /// Verify file content against its companion .sha256 checksum file. Returns true only when
    /// no companion exists (genuinely legacy data) or the companion matches. A companion that
    /// EXISTS but cannot be read fails CLOSED: evidence is on disk and unavailable, and a
    /// verifier that answered "valid" in that state let a caller holding the companion open
    /// exclusively turn a strict read into an unconditional pass.
    /// </summary>
    private bool VerifyChecksum(string path, string content)
    {
        var checksumPath = path + ".sha256";
        if (!File.Exists(checksumPath))
            return true; // No checksum = legacy data, pass through

        try
        {
            var expected = File.ReadAllText(checksumPath).Trim();
            var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Checksum file for '{Path}' exists but could not be read; verification fails closed", path);
            return false;
        }
    }
}
