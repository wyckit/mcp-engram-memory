using McpEngramMemory.Core.Models;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Storage;

/// <summary>Composite incremental entry key used by storage coordinators that do not supply a provider-local key type.</summary>
internal readonly record struct DefaultEntryStorageKey(string TenantId, string EntryId);

/// <summary>
/// Shared debounced-write, causal-ordering, retry-backoff, anti-starvation, flush, and
/// namespace-cancellation coordinator used by <see cref="PersistenceManager"/>,
/// <see cref="SqliteStorageProvider"/>, and <see cref="SqlServerStorageProvider"/>.
///
/// Locking invariants:
/// - <see cref="TimerLock"/> is strictly a short-held metadata lock and is NEVER held across
///   snapshot capture callbacks or blocking disk/DB I/O.
/// - <see cref="FlushGate"/> serializes timer commits, <see cref="TryFlush"/>,
///   <see cref="Dispose"/>, and namespace deletion so commit order matches capture order and
///   stale timers can never resurrect deleted data.
/// - Cross-queue causal order: global saves (edges, clusters, decay configs) never commit ahead
///   of pending entry-level writes; bounded deferral (<see cref="MaxGlobalDeferrals"/>) prevents
///   indefinite global-save starvation under steady entry traffic by draining pending entry writes
///   first (still preserving causal order).
/// </summary>
internal sealed class DebouncedStorageCoordinator<TEntryKey> : IDisposable
    where TEntryKey : struct, IEquatable<TEntryKey>
{
    private static readonly TimeSpan MaxRetryBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum consecutive times a global save (edges, clusters, decay configs) will defer when
    /// entry-level writes are pending before proactively draining those entry writes first so
    /// steady entry traffic cannot starve global saves indefinitely.
    /// </summary>
    internal const int DefaultMaxGlobalDeferrals = 3;

    private readonly TimeSpan _debounceDelay;
    private readonly bool _supportsIncrementalWrites;
    private readonly ILogger? _logger;
    private readonly Func<string, string, TEntryKey> _makeKey;
    private readonly Func<TEntryKey, string> _getTenantId;
    private readonly Func<string, Func<NamespaceData>, bool> _writeNamespace;
    private readonly Func<string, Dictionary<TEntryKey, CognitiveEntry>?, HashSet<TEntryKey>?, bool>? _writeIncremental;
    private readonly Func<List<GraphEdge>, bool> _writeEdges;
    private readonly Func<List<SemanticCluster>, bool> _writeClusters;
    private readonly Func<Dictionary<string, DecayConfig>, bool> _writeDecayConfigs;
    private readonly Func<string, HnswSnapshot, bool> _writeHnsw;
    private readonly Action? _onDisposedUnderLock;

    internal readonly object TimerLock = new();
    internal readonly SemaphoreSlim FlushGate = new(1, 1);

    private bool _disposed;
    private int _inFlightWrites;
    private readonly ManualResetEventSlim _writesIdle = new(initialState: true);

    private readonly Dictionary<string, (ITimer Timer, Func<NamespaceData> DataProvider)> _pendingNsSaves = new();
    private readonly Dictionary<string, int> _nsSaveFailures = new();

    internal readonly Dictionary<string, Dictionary<TEntryKey, CognitiveEntry>> PendingEntryUpserts = new();
    internal readonly Dictionary<string, HashSet<TEntryKey>> PendingEntryDeletes = new();
    private readonly Dictionary<string, ITimer> _incrementalTimers = new();
    private readonly Dictionary<string, int> _incrementalFailures = new();

    private readonly GlobalSaveSlot<List<GraphEdge>> _edgesSlot = new("global-edges");
    private readonly GlobalSaveSlot<List<SemanticCluster>> _clustersSlot = new("clusters");
    private readonly GlobalSaveSlot<Dictionary<string, DecayConfig>> _decaySlot = new("decay-config");

    private readonly Dictionary<string, (ITimer Timer, Func<HnswSnapshot?> Provider)> _pendingHnswSaves = new();
    private readonly Dictionary<string, int> _hnswSaveFailures = new();

    internal TimeProvider TimerProvider { get; set; } = TimeProvider.System;
    internal int MaxGlobalDeferrals { get; set; } = DefaultMaxGlobalDeferrals;
    internal bool IsDisposed { get { lock (TimerLock) return _disposed; } }

    public DebouncedStorageCoordinator(
        TimeSpan debounceDelay,
        bool supportsIncrementalWrites,
        ILogger? logger,
        Func<string, string, TEntryKey> makeKey,
        Func<TEntryKey, string> getTenantId,
        Func<string, Func<NamespaceData>, bool> writeNamespace,
        Func<string, Dictionary<TEntryKey, CognitiveEntry>?, HashSet<TEntryKey>?, bool>? writeIncremental,
        Func<List<GraphEdge>, bool> writeEdges,
        Func<List<SemanticCluster>, bool> writeClusters,
        Func<Dictionary<string, DecayConfig>, bool> writeDecayConfigs,
        Func<string, HnswSnapshot, bool> writeHnsw,
        Action? onDisposedUnderLock = null)
    {
        _debounceDelay = debounceDelay;
        _supportsIncrementalWrites = supportsIncrementalWrites;
        _logger = logger;
        _makeKey = makeKey;
        _getTenantId = getTenantId;
        _writeNamespace = writeNamespace;
        _writeIncremental = writeIncremental;
        _writeEdges = writeEdges;
        _writeClusters = writeClusters;
        _writeDecayConfigs = writeDecayConfigs;
        _writeHnsw = writeHnsw;
        _onDisposedUnderLock = onDisposedUnderLock;
    }

    // ── Tracked external synchronous writes (e.g. collapse-history RMW) ──

    internal bool TryBeginTrackedExternalWrite()
    {
        FlushGate.Wait();
        try
        {
            lock (TimerLock)
            {
                if (_disposed) return false;
                BeginTrackedWriteUnlocked();
                return true;
            }
        }
        finally
        {
            FlushGate.Release();
        }
    }

    internal void EndTrackedWrite()
    {
        lock (TimerLock)
        {
            if (Interlocked.Decrement(ref _inFlightWrites) == 0)
                _writesIdle.Set();
        }
    }

    private void BeginTrackedWriteUnlocked()
    {
        if (Interlocked.Increment(ref _inFlightWrites) == 1)
            _writesIdle.Reset();
    }

    internal void ResetNamespaceFailure(string ns)
    {
        lock (TimerLock)
            _nsSaveFailures.Remove(ns);
    }

    // ── Scheduling: Full namespace snapshots ──

    internal void ScheduleSave(string ns, Func<NamespaceData> dataProvider)
        => ScheduleSaveCore(ns, dataProvider, delayOverride: null);

    private void ScheduleSaveCore(string ns, Func<NamespaceData> dataProvider, TimeSpan? delayOverride)
    {
        lock (TimerLock)
        {
            if (_disposed) return;

            if (_pendingNsSaves.TryGetValue(ns, out var existing))
                existing.Timer.Dispose();

            var dueTime = delayOverride ?? _debounceDelay;
            ITimer? timer = null;
            timer = TimerProvider.CreateTimer(_ =>
            {
                FlushGate.Wait();
                try
                {
                    Func<NamespaceData>? provider = null;
                    lock (TimerLock)
                    {
                        if (_pendingNsSaves.TryGetValue(ns, out var entry)
                            && ReferenceEquals(entry.Timer, timer))
                        {
                            provider = entry.DataProvider;
                            entry.Timer.Dispose();
                            _pendingNsSaves.Remove(ns);

                            if (_supportsIncrementalWrites)
                            {
                                PendingEntryUpserts.Remove(ns);
                                PendingEntryDeletes.Remove(ns);
                                if (_incrementalTimers.Remove(ns, out var incTimer))
                                    incTimer.Dispose();
                                _incrementalFailures.Remove(ns);
                            }

                            BeginTrackedWriteUnlocked();
                        }
                    }

                    if (provider is not null)
                    {
                        try
                        {
                            if (_writeNamespace(ns, provider))
                            {
                                lock (TimerLock) { _nsSaveFailures.Remove(ns); }
                            }
                            else
                            {
                                RetainNsSave(ns, provider, "timer write");
                            }
                        }
                        finally
                        {
                            EndTrackedWrite();
                        }
                    }
                }
                finally
                {
                    FlushGate.Release();
                }
            }, null, dueTime, Timeout.InfiniteTimeSpan);

            _pendingNsSaves[ns] = (timer, dataProvider);
        }
    }

    // ── Scheduling: Incremental entry upserts / deletes ──

    internal void ScheduleUpsertEntry(string ns, CognitiveEntry entry)
    {
        lock (TimerLock)
        {
            if (_disposed) return;

            if (!PendingEntryUpserts.TryGetValue(ns, out var upserts))
            {
                upserts = new();
                PendingEntryUpserts[ns] = upserts;
            }
            var key = _makeKey(entry.TenantId, entry.Id);
            upserts[key] = entry;

            if (PendingEntryDeletes.TryGetValue(ns, out var deletes))
            {
                deletes.Remove(key);
                if (deletes.Count == 0)
                    PendingEntryDeletes.Remove(ns);
            }

            ScheduleIncrementalFlushUnlocked(ns, delayOverride: null);
        }
    }

    internal void ScheduleDeleteEntry(string ns, string entryId, string tenantId)
    {
        lock (TimerLock)
        {
            if (_disposed) return;

            var key = _makeKey(NormalizeTenantId(tenantId), entryId);

            if (!PendingEntryDeletes.TryGetValue(ns, out var deletes))
            {
                deletes = new();
                PendingEntryDeletes[ns] = deletes;
            }
            deletes.Add(key);

            if (PendingEntryUpserts.TryGetValue(ns, out var upserts))
            {
                upserts.Remove(key);
                if (upserts.Count == 0)
                    PendingEntryUpserts.Remove(ns);
            }

            ScheduleIncrementalFlushUnlocked(ns, delayOverride: null);
        }
    }

    private void ScheduleIncrementalFlushUnlocked(string ns, TimeSpan? delayOverride)
    {
        if (_incrementalTimers.TryGetValue(ns, out var existing))
            existing.Dispose();

        var dueTime = delayOverride ?? _debounceDelay;
        ITimer? selfRef = null;
        selfRef = TimerProvider.CreateTimer(_ =>
        {
            FlushGate.Wait();
            try
            {
                Dictionary<TEntryKey, CognitiveEntry>? upserts = null;
                HashSet<TEntryKey>? deletes = null;

                lock (TimerLock)
                {
                    if (!_incrementalTimers.TryGetValue(ns, out var current)
                        || !ReferenceEquals(current, selfRef))
                        return;

                    if (PendingEntryUpserts.Remove(ns, out var u) && u.Count > 0)
                        upserts = u;
                    if (PendingEntryDeletes.Remove(ns, out var d) && d.Count > 0)
                        deletes = d;
                    current.Dispose();
                    _incrementalTimers.Remove(ns);
                    BeginTrackedWriteUnlocked();
                }

                try
                {
                    if (_writeIncremental!(ns, upserts, deletes))
                    {
                        lock (TimerLock) { _incrementalFailures.Remove(ns); }
                    }
                    else
                    {
                        RetainIncrementalBatch(ns, upserts, deletes);
                    }
                }
                finally
                {
                    EndTrackedWrite();
                }
            }
            finally
            {
                FlushGate.Release();
            }
        }, null, dueTime, Timeout.InfiniteTimeSpan);
        _incrementalTimers[ns] = selfRef;
    }

    // ── Scheduling: Global saves (edges, clusters, decay configs) ──

    internal void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider)
        => ScheduleGlobalCore(_edgesSlot, dataProvider, _writeEdges, delayOverride: null);

    internal void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider)
        => ScheduleGlobalCore(_clustersSlot, dataProvider, _writeClusters, delayOverride: null);

    internal void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider)
        => ScheduleGlobalCore(_decaySlot, dataProvider, _writeDecayConfigs, delayOverride: null);

    private void ScheduleGlobalCore<T>(
        GlobalSaveSlot<T> slot,
        Func<T> dataProvider,
        Func<T, bool> writer,
        TimeSpan? delayOverride,
        int initialDeferrals = 0) where T : class
    {
        lock (TimerLock)
        {
            if (_disposed) return;

            slot.Timer?.Dispose();
            slot.Provider = dataProvider;
            slot.ConsecutiveDeferrals = initialDeferrals;

            var dueTime = delayOverride ?? _debounceDelay;
            ITimer? timer = null;
            timer = TimerProvider.CreateTimer(_ =>
            {
                FlushGate.Wait();
                try
                {
                    Func<T>? provider = null;
                    bool drainEntriesFirst = false;
                    int priorDeferrals = 0;

                    lock (TimerLock)
                    {
                        if (!ReferenceEquals(slot.Timer, timer)) return;

                        if (HasPendingEntryLevelWorkUnlocked())
                        {
                            if (slot.ConsecutiveDeferrals < MaxGlobalDeferrals)
                            {
                                slot.ConsecutiveDeferrals++;
                                slot.Timer?.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
                                return;
                            }

                            // Anti-starvation threshold reached: capture the global snapshot FIRST
                            // and then proactively drain pending entry writes (while holding FlushGate)
                            // so causal ordering is preserved and steady entry traffic cannot starve
                            // this global save even if newer unrelated entries arrive during I/O.
                            drainEntriesFirst = true;
                        }

                        provider = slot.Provider;
                        slot.Provider = null;
                        slot.Timer?.Dispose();
                        slot.Timer = null;
                        priorDeferrals = slot.ConsecutiveDeferrals;
                        slot.ConsecutiveDeferrals = 0;
                    }

                    if (provider is null) return;

                    // Snapshot capture may acquire graph/cluster/index locks; never hold TimerLock here.
                    // Capturing BEFORE draining entries guarantees every entry referenced by `snapshot`
                    // was already queued prior to the drain (same causal invariant as TryFlushCore).
                    bool captured = StorageSnapshot.TryCapture(provider, _logger, out var snapshot);

                    if (!captured)
                    {
                        lock (TimerLock)
                        {
                            if (slot.Provider is not null) return;
                            if (_disposed)
                                slot.Provider = provider;
                            else
                                ScheduleGlobalCore(slot, provider, writer, RecordFailureAndComputeDelayUnlocked(ref slot.ConsecutiveFailures));
                        }
                        return;
                    }

                    if (drainEntriesFirst)
                    {
                        // FlushGate is held and our own tracked write has not started yet, so waiting
                        // on _writesIdle inside DrainAndCommitPendingEntriesUnderFlushGate cannot self-deadlock.
                        bool entriesDrainedCleanly = DrainAndCommitPendingEntriesUnderFlushGate();
                        if (!entriesDrainedCleanly)
                        {
                            // Entry writes failed (and were re-queued with exponential backoff). Back off
                            // this global slot as well so it never bypasses entry backoff at _debounceDelay.
                            lock (TimerLock)
                            {
                                if (slot.Provider is not null) return;
                                if (_disposed)
                                    slot.Provider = provider;
                                else
                                    ScheduleGlobalCore(slot, provider, writer, RecordFailureAndComputeDelayUnlocked(ref slot.ConsecutiveFailures));
                            }
                            return;
                        }
                    }
                    else
                    {
                        lock (TimerLock)
                        {
                            // Newer schedule arrived while capturing: let it win.
                            if (slot.Provider is not null) return;

                            if (HasPendingEntryLevelWorkUnlocked())
                            {
                                if (_disposed)
                                    slot.Provider = provider;
                                else
                                    ScheduleGlobalCore(
                                        slot, provider, writer, _debounceDelay,
                                        initialDeferrals: Math.Min(priorDeferrals + 1, MaxGlobalDeferrals));
                                return;
                            }
                        }
                    }

                    lock (TimerLock)
                    {
                        BeginTrackedWriteUnlocked();
                    }

                    try
                    {
                        if (writer(snapshot!))
                        {
                            lock (TimerLock) { slot.ConsecutiveFailures = 0; }
                        }
                        else
                        {
                            lock (TimerLock)
                            {
                                if (slot.Provider is not null) return;
                                if (_disposed)
                                    slot.Provider = provider;
                                else
                                    ScheduleGlobalCore(slot, provider, writer, RecordFailureAndComputeDelayUnlocked(ref slot.ConsecutiveFailures));
                            }
                        }
                    }
                    finally
                    {
                        EndTrackedWrite();
                    }
                }
                finally
                {
                    FlushGate.Release();
                }
            }, null, dueTime, Timeout.InfiniteTimeSpan);

            slot.Timer = timer;
        }
    }

    /// <summary>
    /// Drains and commits all currently pending entry-level work while <see cref="FlushGate"/>
    /// is already held. Used by anti-starvation global callbacks to satisfy cross-queue causal
    /// ordering before writing global topology.
    /// </summary>
    private bool DrainAndCommitPendingEntriesUnderFlushGate()
    {
        List<(string Ns, Func<NamespaceData> Provider)> pendingNs;
        List<(string Ns, Dictionary<TEntryKey, CognitiveEntry>? Upserts, HashSet<TEntryKey>? Deletes)> pendingIncremental;

        while (true)
        {
            _writesIdle.Wait();
            lock (TimerLock)
            {
                if (Volatile.Read(ref _inFlightWrites) != 0)
                    continue;

                DrainEntryQueuesUnlocked(out pendingNs, out pendingIncremental);
                break;
            }
        }

        return CommitEntryWork(pendingNs, pendingIncremental, refuseWhenDisposed: true);
    }

    // ── Scheduling: Debounced HNSW snapshots ──

    internal void ScheduleHnswSave(string partitionKey, Func<HnswSnapshot?> snapshotProvider)
        => ScheduleHnswSaveCore(partitionKey, snapshotProvider, delayOverride: null);

    private void ScheduleHnswSaveCore(string partitionKey, Func<HnswSnapshot?> snapshotProvider, TimeSpan? delayOverride)
    {
        lock (TimerLock)
        {
            if (_disposed) return;

            if (_pendingHnswSaves.TryGetValue(partitionKey, out var existing))
                existing.Timer.Dispose();

            var dueTime = delayOverride ?? _debounceDelay;
            ITimer? timer = null;
            timer = TimerProvider.CreateTimer(_ =>
            {
                FlushGate.Wait();
                try
                {
                    Func<HnswSnapshot?>? provider = null;
                    lock (TimerLock)
                    {
                        if (_pendingHnswSaves.TryGetValue(partitionKey, out var entry)
                            && ReferenceEquals(entry.Timer, timer))
                        {
                            provider = entry.Provider;
                            entry.Timer.Dispose();
                            _pendingHnswSaves.Remove(partitionKey);
                            BeginTrackedWriteUnlocked();
                        }
                    }

                    if (provider is not null)
                    {
                        try
                        {
                            if (WriteHnswFromProvider(partitionKey, provider))
                            {
                                lock (TimerLock) { _hnswSaveFailures.Remove(partitionKey); }
                            }
                            else
                            {
                                RetainHnswSave(partitionKey, provider);
                            }
                        }
                        finally
                        {
                            EndTrackedWrite();
                        }
                    }
                }
                finally
                {
                    FlushGate.Release();
                }
            }, null, dueTime, Timeout.InfiniteTimeSpan);

            _pendingHnswSaves[partitionKey] = (timer, snapshotProvider);
        }
    }

    internal void CancelHnswSave(string partitionKey)
    {
        lock (TimerLock)
        {
            if (_pendingHnswSaves.Remove(partitionKey, out var pending))
                pending.Timer.Dispose();
            _hnswSaveFailures.Remove(partitionKey);
        }
    }

    // ── Namespace deletion cancellation (must be invoked while holding FlushGate) ──

    internal void CancelAllPendingForNamespaceUnlockedByGate(string ns)
    {
        lock (TimerLock)
        {
            if (_pendingNsSaves.Remove(ns, out var pendingNs))
                pendingNs.Timer.Dispose();
            _nsSaveFailures.Remove(ns);

            if (_supportsIncrementalWrites)
            {
                if (_incrementalTimers.Remove(ns, out var incTimer))
                    incTimer.Dispose();
                PendingEntryUpserts.Remove(ns);
                PendingEntryDeletes.Remove(ns);
                _incrementalFailures.Remove(ns);
            }

            CancelHnswForNamespaceUnlocked(ns, tenantFilter: null);
        }
    }

    internal void CancelTenantPendingForNamespaceUnlockedByGate(
        string ns,
        string tenantId,
        out Func<NamespaceData>? pendingNsProvider)
    {
        tenantId = NormalizeTenantId(tenantId);
        pendingNsProvider = null;

        lock (TimerLock)
        {
            if (_pendingNsSaves.Remove(ns, out var pendingNs))
            {
                pendingNs.Timer.Dispose();
                pendingNsProvider = pendingNs.DataProvider;
            }
            _nsSaveFailures.Remove(ns);

            if (_supportsIncrementalWrites)
            {
                if (PendingEntryUpserts.TryGetValue(ns, out var upserts))
                {
                    var matching = upserts.Keys.Where(k => _getTenantId(k) == tenantId).ToList();
                    foreach (var k in matching)
                        upserts.Remove(k);
                    if (upserts.Count == 0)
                        PendingEntryUpserts.Remove(ns);
                }

                if (PendingEntryDeletes.TryGetValue(ns, out var deletes))
                {
                    deletes.RemoveWhere(k => _getTenantId(k) == tenantId);
                    if (deletes.Count == 0)
                        PendingEntryDeletes.Remove(ns);
                }

                if (!PendingEntryUpserts.ContainsKey(ns) && !PendingEntryDeletes.ContainsKey(ns))
                {
                    if (_incrementalTimers.Remove(ns, out var incTimer))
                        incTimer.Dispose();
                    _incrementalFailures.Remove(ns);
                }
            }

            CancelHnswForNamespaceUnlocked(ns, tenantFilter: tenantId);
        }
    }

    private void CancelHnswForNamespaceUnlocked(string ns, string? tenantFilter)
    {
        if (_pendingHnswSaves.Count == 0) return;

        var toRemove = new List<string>();
        foreach (var pk in _pendingHnswSaves.Keys)
        {
            if (tenantFilter is null)
            {
                if (pk == ns || pk.EndsWith(Tenancy.PartitionSeparator + ns, StringComparison.Ordinal))
                    toRemove.Add(pk);
            }
            else
            {
                string expectedPk = NamespaceStore.PartitionKey(tenantFilter, ns);
                if (pk == expectedPk)
                    toRemove.Add(pk);
            }
        }

        foreach (var pk in toRemove)
        {
            if (_pendingHnswSaves.Remove(pk, out var pending))
                pending.Timer.Dispose();
            _hnswSaveFailures.Remove(pk);
        }
    }

    // ── Flush & Dispose ──

    internal bool TryFlush()
    {
        FlushGate.Wait();
        try
        {
            return TryFlushCore(refuseWhenDisposed: true);
        }
        finally
        {
            FlushGate.Release();
        }
    }

    public void Dispose()
    {
        lock (TimerLock)
        {
            if (_disposed) return;
            _disposed = true;
            _onDisposedUnderLock?.Invoke();
        }

        FlushGate.Wait();
        try
        {
            TryFlushCore(refuseWhenDisposed: false);
        }
        finally
        {
            FlushGate.Release();
        }

        _writesIdle.Dispose();
    }

    private bool TryFlushCore(bool refuseWhenDisposed)
    {
        if (refuseWhenDisposed)
        {
            lock (TimerLock)
            {
                if (_disposed) return false;
            }
        }

        // CROSS-QUEUE CAUSAL ORDER, part 3: materialize global payloads BEFORE draining entry
        // queues so everything referenced by global topology is covered by the entry drain below.
        Func<List<GraphEdge>>? edgeProviderRef;
        Func<List<SemanticCluster>>? clusterProviderRef;
        Func<Dictionary<string, DecayConfig>>? decayProviderRef;
        lock (TimerLock)
        {
            edgeProviderRef = _edgesSlot.Provider;
            clusterProviderRef = _clustersSlot.Provider;
            decayProviderRef = _decaySlot.Provider;
        }

        List<GraphEdge>? edgeData = null;
        List<SemanticCluster>? clusterData = null;
        Dictionary<string, DecayConfig>? decayData = null;
        bool edgeMaterialized = false, clusterMaterialized = false, decayMaterialized = false;

        try { if (edgeProviderRef is not null) { edgeData = edgeProviderRef(); edgeMaterialized = true; } }
        catch (Exception ex) { _logger?.LogError(ex, "Edge payload materialization failed; the pending save is retained"); }

        try { if (clusterProviderRef is not null) { clusterData = clusterProviderRef(); clusterMaterialized = true; } }
        catch (Exception ex) { _logger?.LogError(ex, "Cluster payload materialization failed; the pending save is retained"); }

        try { if (decayProviderRef is not null) { decayData = decayProviderRef(); decayMaterialized = true; } }
        catch (Exception ex) { _logger?.LogError(ex, "Decay-config payload materialization failed; the pending save is retained"); }

        List<(string Ns, Func<NamespaceData> Provider)> pendingNs;
        List<(string Ns, Dictionary<TEntryKey, CognitiveEntry>? Upserts, HashSet<TEntryKey>? Deletes)> pendingIncremental;
        List<(string PartitionKey, Func<HnswSnapshot?> Provider)> pendingHnsw;
        Func<List<GraphEdge>>? edgeProvider;
        Func<List<SemanticCluster>>? clusterProvider;
        Func<Dictionary<string, DecayConfig>>? decayProvider;
        bool supersededDuringDrain = false;

        while (true)
        {
            _writesIdle.Wait();
            lock (TimerLock)
            {
                if (Volatile.Read(ref _inFlightWrites) != 0)
                    continue;

                DrainEntryQueuesUnlocked(out pendingNs, out pendingIncremental);

                pendingHnsw = _pendingHnswSaves
                    .Select(kv => (kv.Key, kv.Value.Provider))
                    .ToList();
                foreach (var (_, (timer, _)) in _pendingHnswSaves)
                    timer.Dispose();
                _pendingHnswSaves.Clear();

                edgeProvider = TakeSlotIfUnchangedUnlocked(_edgesSlot, edgeProviderRef, ref supersededDuringDrain);
                clusterProvider = TakeSlotIfUnchangedUnlocked(_clustersSlot, clusterProviderRef, ref supersededDuringDrain);
                decayProvider = TakeSlotIfUnchangedUnlocked(_decaySlot, decayProviderRef, ref supersededDuringDrain);
                break;
            }
        }

        bool entriesCommitted = CommitEntryWork(pendingNs, pendingIncremental, refuseWhenDisposed);
        bool allCommitted = !supersededDuringDrain && entriesCommitted;

        // CROSS-QUEUE CAUSAL ORDER, part 2: commit global topology only if every entry write succeeded.
        if (!entriesCommitted)
        {
            lock (TimerLock)
            {
                RetainWithheldGlobalSlotUnlocked(_edgesSlot, edgeProvider, _writeEdges);
                RetainWithheldGlobalSlotUnlocked(_clustersSlot, clusterProvider, _writeClusters);
                RetainWithheldGlobalSlotUnlocked(_decaySlot, decayProvider, _writeDecayConfigs);
            }
            edgeProvider = null;
            clusterProvider = null;
            decayProvider = null;
        }
        else
        {
            if (!CommitGlobalSlot(_edgesSlot, edgeProvider, edgeMaterialized, edgeData, _writeEdges))
                allCommitted = false;
            if (!CommitGlobalSlot(_clustersSlot, clusterProvider, clusterMaterialized, clusterData, _writeClusters))
                allCommitted = false;
            if (!CommitGlobalSlot(_decaySlot, decayProvider, decayMaterialized, decayData, _writeDecayConfigs))
                allCommitted = false;
        }

        // Derived HNSW snapshots flush after entries.
        foreach (var (pk, hnswProvider) in pendingHnsw)
        {
            if (WriteHnswFromProvider(pk, hnswProvider))
            {
                lock (TimerLock) { _hnswSaveFailures.Remove(pk); }
            }
            else
            {
                allCommitted = false;
                RetainHnswSave(pk, hnswProvider);
            }
        }

        _writesIdle.Wait();

        // Final linearization checkpoint.
        lock (TimerLock)
        {
            if (HasPendingEntryLevelWorkUnlocked()
                || _edgesSlot.Provider is not null
                || _clustersSlot.Provider is not null
                || _decaySlot.Provider is not null
                || _pendingHnswSaves.Count > 0)
            {
                allCommitted = false;
            }
        }

        return allCommitted;
    }

    private void DrainEntryQueuesUnlocked(
        out List<(string Ns, Func<NamespaceData> Provider)> pendingNs,
        out List<(string Ns, Dictionary<TEntryKey, CognitiveEntry>? Upserts, HashSet<TEntryKey>? Deletes)> pendingIncremental)
    {
        pendingNs = _pendingNsSaves
            .Select(kv => (kv.Key, kv.Value.DataProvider))
            .ToList();
        foreach (var (_, (timer, _)) in _pendingNsSaves)
            timer.Dispose();
        _pendingNsSaves.Clear();

        pendingIncremental = new();
        if (_supportsIncrementalWrites)
        {
            var incrementalNs = new HashSet<string>(PendingEntryUpserts.Keys);
            foreach (var k in PendingEntryDeletes.Keys)
                incrementalNs.Add(k);
            foreach (var ns in incrementalNs)
            {
                Dictionary<TEntryKey, CognitiveEntry>? upserts = null;
                HashSet<TEntryKey>? deletes = null;

                if (PendingEntryUpserts.Remove(ns, out var u) && u.Count > 0)
                    upserts = u;
                if (PendingEntryDeletes.Remove(ns, out var d) && d.Count > 0)
                    deletes = d;
                if (upserts is not null || deletes is not null)
                    pendingIncremental.Add((ns, upserts, deletes));
            }
            foreach (var (_, timer) in _incrementalTimers)
                timer.Dispose();
            _incrementalTimers.Clear();
        }
    }

    private bool CommitEntryWork(
        List<(string Ns, Func<NamespaceData> Provider)> pendingNs,
        List<(string Ns, Dictionary<TEntryKey, CognitiveEntry>? Upserts, HashSet<TEntryKey>? Deletes)> pendingIncremental,
        bool refuseWhenDisposed)
    {
        bool entriesCommitted = true;

        var fullSaveNs = new HashSet<string>();
        foreach (var (ns, _) in pendingNs)
            fullSaveNs.Add(ns);

        var unsubsumedBatchFailures = new HashSet<string>();
        foreach (var (ns, upserts, deletes) in pendingIncremental)
        {
            // During normal flushes, a full namespace save subsumes an incremental batch for the
            // same namespace. On Dispose (refuseWhenDisposed == false), attempt the incremental
            // batch first so a failing full save does not take both down.
            if (refuseWhenDisposed && fullSaveNs.Contains(ns))
                continue;

            if (_writeIncremental!(ns, upserts, deletes))
            {
                lock (TimerLock) { _incrementalFailures.Remove(ns); }
            }
            else
            {
                unsubsumedBatchFailures.Add(ns);
                RetainIncrementalBatch(ns, upserts, deletes);
            }
        }

        foreach (var (ns, provider) in pendingNs)
        {
            if (_writeNamespace(ns, provider))
            {
                lock (TimerLock)
                {
                    _nsSaveFailures.Remove(ns);
                    _incrementalFailures.Remove(ns);
                }
                unsubsumedBatchFailures.Remove(ns);
            }
            else
            {
                entriesCommitted = false;
                RetainNsSave(ns, provider, "flush");
            }
        }

        if (unsubsumedBatchFailures.Count > 0)
            entriesCommitted = false;

        return entriesCommitted;
    }

    private bool CommitGlobalSlot<T>(
        GlobalSaveSlot<T> slot,
        Func<T>? provider,
        bool materialized,
        T? data,
        Func<T, bool> writer) where T : class
    {
        if (provider is null) return true;

        if (materialized && writer(data!))
        {
            lock (TimerLock) { slot.ConsecutiveFailures = 0; }
            return true;
        }

        lock (TimerLock)
        {
            if (_disposed)
            {
                _logger?.LogError("Pending {Label} write dropped: provider disposed during a failed flush", slot.Label);
            }
            else if (slot.Provider is null)
            {
                ScheduleGlobalCore(slot, provider, writer, RecordFailureAndComputeDelayUnlocked(ref slot.ConsecutiveFailures));
            }
        }

        return false;
    }

    private void RetainWithheldGlobalSlotUnlocked<T>(
        GlobalSaveSlot<T> slot,
        Func<T>? provider,
        Func<T, bool> writer) where T : class
    {
        if (provider is null) return;
        if (_disposed)
        {
            _logger?.LogError("Pending {Label} write dropped: provider disposed during a failed flush", slot.Label);
            return;
        }
        if (slot.Provider is null)
            ScheduleGlobalCore(slot, provider, writer, _debounceDelay);
    }

    private static Func<T>? TakeSlotIfUnchangedUnlocked<T>(
        GlobalSaveSlot<T> slot,
        Func<T>? expectedProviderRef,
        ref bool supersededDuringDrain) where T : class
    {
        if (ReferenceEquals(slot.Provider, expectedProviderRef))
        {
            var provider = slot.Provider;
            slot.Provider = null;
            slot.Timer?.Dispose();
            slot.Timer = null;
            slot.ConsecutiveDeferrals = 0;
            return provider;
        }

        supersededDuringDrain |= slot.Provider is not null;
        return null;
    }

    private void RetainNsSave(string ns, Func<NamespaceData> provider, string source)
    {
        lock (TimerLock)
        {
            if (_disposed)
            {
                _logger?.LogError(
                    "Pending write for namespace '{Namespace}' dropped: provider disposed during a failed {Source}",
                    ns, source);
                return;
            }
            if (_pendingNsSaves.ContainsKey(ns))
                return;
            ScheduleSaveCore(ns, provider, RecordKeyFailureAndComputeDelayUnlocked(_nsSaveFailures, ns));
        }
    }

    private void RetainIncrementalBatch(
        string ns,
        Dictionary<TEntryKey, CognitiveEntry>? upserts,
        HashSet<TEntryKey>? deletes)
    {
        if ((upserts is null || upserts.Count == 0) && (deletes is null || deletes.Count == 0))
            return;

        lock (TimerLock)
        {
            if (_disposed)
            {
                _logger?.LogError(
                    "Pending incremental write for namespace '{Namespace}' dropped: provider disposed during a failed flush",
                    ns);
                return;
            }

            if (upserts is not null && upserts.Count > 0)
            {
                if (!PendingEntryUpserts.TryGetValue(ns, out var currentUpserts))
                {
                    currentUpserts = new();
                    PendingEntryUpserts[ns] = currentUpserts;
                }
                PendingEntryDeletes.TryGetValue(ns, out var currentDeletes);

                foreach (var (key, entry) in upserts)
                {
                    if (!currentUpserts.ContainsKey(key) && (currentDeletes is null || !currentDeletes.Contains(key)))
                        currentUpserts[key] = entry;
                }

                if (currentUpserts.Count == 0)
                    PendingEntryUpserts.Remove(ns);
            }

            if (deletes is not null && deletes.Count > 0)
            {
                if (!PendingEntryDeletes.TryGetValue(ns, out var currentDeletes))
                {
                    currentDeletes = new();
                    PendingEntryDeletes[ns] = currentDeletes;
                }
                PendingEntryUpserts.TryGetValue(ns, out var currentUpserts);

                foreach (var key in deletes)
                {
                    if (!currentDeletes.Contains(key) && (currentUpserts is null || !currentUpserts.ContainsKey(key)))
                        currentDeletes.Add(key);
                }

                if (currentDeletes.Count == 0)
                    PendingEntryDeletes.Remove(ns);
            }

            bool hasPending =
                (PendingEntryUpserts.TryGetValue(ns, out var u) && u.Count > 0) ||
                (PendingEntryDeletes.TryGetValue(ns, out var d) && d.Count > 0);
            if (hasPending)
            {
                ScheduleIncrementalFlushUnlocked(ns, RecordKeyFailureAndComputeDelayUnlocked(_incrementalFailures, ns));
            }
        }
    }

    private bool WriteHnswFromProvider(string partitionKey, Func<HnswSnapshot?> provider)
    {
        try
        {
            var snapshot = provider();
            if (snapshot is null)
                return true;
            return _writeHnsw(partitionKey, snapshot);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save HNSW snapshot for partition '{Partition}'", partitionKey);
            return false;
        }
    }

    private void RetainHnswSave(string partitionKey, Func<HnswSnapshot?> provider)
    {
        lock (TimerLock)
        {
            if (_disposed)
            {
                _logger?.LogError(
                    "Pending HNSW snapshot for '{Partition}' dropped: provider disposed during a failed write",
                    partitionKey);
                return;
            }
            if (_pendingHnswSaves.ContainsKey(partitionKey))
                return;
            ScheduleHnswSaveCore(partitionKey, provider, RecordKeyFailureAndComputeDelayUnlocked(_hnswSaveFailures, partitionKey));
        }
    }

    private bool HasPendingEntryLevelWorkUnlocked()
    {
        if (_pendingNsSaves.Count > 0) return true;
        if (!_supportsIncrementalWrites) return false;
        foreach (var u in PendingEntryUpserts.Values)
            if (u.Count > 0) return true;
        foreach (var d in PendingEntryDeletes.Values)
            if (d.Count > 0) return true;
        return false;
    }

    private TimeSpan ComputeRetryDelay(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return _debounceDelay;

        var baseDelay = _debounceDelay > TimeSpan.Zero ? _debounceDelay : TimeSpan.FromMilliseconds(500);
        var cap = baseDelay > MaxRetryBackoff ? baseDelay : MaxRetryBackoff;
        int shift = Math.Min(consecutiveFailures, 20);
        double scaledMs = baseDelay.TotalMilliseconds * (1 << shift);
        return scaledMs >= cap.TotalMilliseconds ? cap : TimeSpan.FromMilliseconds(scaledMs);
    }

    private TimeSpan RecordFailureAndComputeDelayUnlocked(ref int failureCounter)
    {
        if (failureCounter < 30)
            failureCounter++;
        return ComputeRetryDelay(failureCounter);
    }

    private TimeSpan RecordKeyFailureAndComputeDelayUnlocked(Dictionary<string, int> failureMap, string key)
    {
        int failures = failureMap.TryGetValue(key, out var count) ? count : 0;
        if (failures < 30)
            failures++;
        failureMap[key] = failures;
        return ComputeRetryDelay(failures);
    }

    private static string NormalizeTenantId(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? string.Empty : tenantId.Trim();

    private sealed class GlobalSaveSlot<T> where T : class
    {
        public string Label { get; }
        public ITimer? Timer;
        public Func<T>? Provider;
        public int ConsecutiveFailures;
        public int ConsecutiveDeferrals;

        public GlobalSaveSlot(string label) => Label = label;
    }
}
