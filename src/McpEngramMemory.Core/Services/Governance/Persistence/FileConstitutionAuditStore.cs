using System.Collections.Concurrent;
using McpEngramMemory.Core.Models.Constitution;

namespace McpEngramMemory.Core.Services.Governance.Persistence;

/// <summary>
/// Append-only, fsync-backed audit journal with deterministic corrupt-tail recovery.
///
/// Sequence numbers must stay dense — a gap is what makes a removed record detectable — so they are
/// derived from the journal on disk rather than from process-local state. Every append and read
/// takes a cross-process lock beside the journal and re-replays whenever the file changed since
/// this instance last observed it. Without that, two servers sharing a governance root (the default
/// path is install-relative, so the documented per-agent deployment shares one) would each number
/// from their own in-memory count, emit a duplicate sequence, and leave a journal whose replay
/// fails as mid-journal corruption — which, because the kernel audits on every tool call, would
/// fail every subsequent MCP request with no in-product recovery.
/// </summary>
public sealed class FileConstitutionAuditStore : IConstitutionAuditStore, IDisposable, IAsyncDisposable
{
    private const string StoreName = "constitution-audit";

    /// <summary>Default max buffered read-only Allow records before forcing a batched fsync.</summary>
    public const int DefaultMaxBufferedRecords = 64;

    /// <summary>Bounded so a peer that leaked the lock handle cannot wedge the audit path.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// One in-process gate per journal path, shared by every instance pointing at that journal.
    ///
    /// The cross-process file lock exists to exclude other processes, and it is a 10 ms spin-poll
    /// because that is all a file lock can be. Letting in-process writers contend on it turns N
    /// concurrent appends into N pool work items spinning on synchronous file I/O — which starves
    /// the thread pool for everything else in the process, not just for the audit path. Serializing
    /// in memory first leaves the file lock uncontended within a process and doing only the job it
    /// is actually needed for.
    ///
    /// Keyed case-insensitively: over-serializing two spellings of one path costs nothing, while
    /// under-serializing would put them back on the file lock.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly SemaphoreSlim _gate;
    private readonly TimeSpan _readOnlyFlushInterval;
    private readonly int _maxBufferedRecords;
    private readonly Queue<ConstitutionAuditRecord> _pendingBuffered = new();
    private readonly object _timerLock = new();
    private Timer? _flushTimer;
    private volatile bool _disposed;
    private long _fsyncCount;
    private readonly List<PersistenceDiagnostic> _diagnostics = new();
    // Tracks only the dense record count and last-observed journal metadata instead of retaining
    // every historical audit record in RAM across long-running server lifetimes.
    // _syncedLength == -1 forces the first sync; any change in length or write timestamp means
    // a peer modified the journal and triggers a validated re-sync before assigning the next sequence.
    private long _recordCount;
    private long _syncedLength = -1;
    private long _syncedLastWriteTicks = -1;

    public FileConstitutionAuditStore(string root)
        : this(root, TimeSpan.Zero, DefaultMaxBufferedRecords) { }

    /// <summary>
    /// Create a file-backed audit store with optional batching for read-only Allow audit events.
    /// When <paramref name="readOnlyFlushInterval"/> is greater than <see cref="TimeSpan.Zero"/>,
    /// read-only Allow records submitted via <see cref="Constitution.ConstitutionKernel"/> are buffered and
    /// flushed in a single lock+fsync batch when the interval elapses, <paramref name="maxBufferedRecords"/>
    /// is reached, a mutating/Deny/Commit record arrives, <see cref="ReadAllAsync"/> is called,
    /// or the store is flushed/disposed. Mutating, Commit, and Deny records ALWAYS fsync immediately.
    /// </summary>
    public FileConstitutionAuditStore(
        string root,
        TimeSpan readOnlyFlushInterval,
        int maxBufferedRecords = DefaultMaxBufferedRecords)
    {
        if (readOnlyFlushInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(readOnlyFlushInterval), "Flush interval cannot be negative.");
        if (maxBufferedRecords <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBufferedRecords), "Max buffered records must be positive.");

        _path = Path.Combine(Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root))), "audit.journal");
        _gate = PathGates.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
        _readOnlyFlushInterval = readOnlyFlushInterval;
        _maxBufferedRecords = maxBufferedRecords;
    }

    internal long FsyncCount => Interlocked.Read(ref _fsyncCount);

    internal int PendingBufferedCount
    {
        get
        {
            _gate.Wait();
            try { return _pendingBuffered.Count; }
            finally { _gate.Release(); }
        }
    }

    public IReadOnlyList<PersistenceDiagnostic> Diagnostics
    {
        get { lock (_diagnostics) return _diagnostics.ToArray(); }
    }

    public async ValueTask<ConstitutionAuditRecord> AppendAsync(
        ConstitutionAuditRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = await FlushPendingAndAppendCoreAsync(record, cancellationToken).ConfigureAwait(false);
            return stored!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Buffer a read-only Allow audit event when read-only batching is enabled; otherwise fall
    /// straight through to immediate fsync-backed <see cref="AppendAsync"/>. Dense on-disk
    /// sequence numbers are assigned atomically under the cross-process lock when the batch flushes.
    /// </summary>
    internal async ValueTask<ConstitutionAuditRecord> AppendDeferredIfConfiguredAsync(
        ConstitutionAuditRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_readOnlyFlushInterval <= TimeSpan.Zero)
            return await AppendAsync(record, cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
                return (await FlushPendingAndAppendCoreAsync(record, cancellationToken).ConfigureAwait(false))!;

            _pendingBuffered.Enqueue(record);
            if (_pendingBuffered.Count >= _maxBufferedRecords)
            {
                CancelFlushTimer();
                await FlushPendingAndAppendCoreAsync(immediateRecord: null, cancellationToken).ConfigureAwait(false);
                return record;
            }

            ScheduleFlushTimer();
            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Flush any buffered read-only audit records durably to disk.</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancelFlushTimer();
            if (_pendingBuffered.Count > 0)
                await FlushPendingAndAppendCoreAsync(immediateRecord: null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ConstitutionAuditRecord>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read under the same lock as append: replay truncates a torn tail, and doing that to a
            // peer's in-flight write would destroy a record that is about to be committed.
            await using var journalLock = await CrashSafeJsonPersistence.AcquireExclusiveLockAsync(
                _path, LockTimeout, cancellationToken).ConfigureAwait(false);
            if (_pendingBuffered.Count > 0)
            {
                CancelFlushTimer();
                try
                {
                    await FlushPendingUnderLocksAsync(immediateRecord: null, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    if (_pendingBuffered.Count > 0 && _readOnlyFlushInterval > TimeSpan.Zero)
                        ScheduleFlushTimer();
                    throw;
                }
            }
            return await ReplayValidatedAsync(materializeRecords: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        StopTimerAndMarkDisposed();
        FlushAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        StopTimerAndMarkDisposed();
        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void StopTimerAndMarkDisposed()
    {
        lock (_timerLock)
        {
            _disposed = true;
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
    }

    private void ScheduleFlushTimer()
    {
        lock (_timerLock)
        {
            if (_disposed)
                return;
            if (_flushTimer is null)
            {
                _flushTimer = new Timer(OnFlushTimerElapsed, null, _readOnlyFlushInterval, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void CancelFlushTimer()
    {
        lock (_timerLock)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
    }

    private void OnFlushTimerElapsed(object? _)
    {
        lock (_timerLock)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
            if (_disposed)
                return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Records remain queued in _pendingBuffered and will be retried on the next
                // flush, read, immediate append, or shutdown.
                lock (_timerLock)
                {
                    if (!_disposed && _flushTimer is null)
                        _flushTimer = new Timer(OnFlushTimerElapsed, null, _readOnlyFlushInterval, Timeout.InfiniteTimeSpan);
                }
            }
        });
    }

    /// <summary>
    /// Flush all buffered records (and an optional immediate record) in one lock+fsync batch.
    /// Caller must hold <c>_gate</c>.
    /// </summary>
    private async ValueTask<ConstitutionAuditRecord?> FlushPendingAndAppendCoreAsync(
        ConstitutionAuditRecord? immediateRecord,
        CancellationToken cancellationToken)
    {
        CancelFlushTimer();
        try
        {
            await using var journalLock = await CrashSafeJsonPersistence.AcquireExclusiveLockAsync(
                _path, LockTimeout, cancellationToken).ConfigureAwait(false);
            return await FlushPendingUnderLocksAsync(immediateRecord, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Keep remaining buffered records scheduled for retry if lock acquisition or I/O
            // fails or is cancelled, so records are never stranded without a flush timer.
            if (_pendingBuffered.Count > 0 && _readOnlyFlushInterval > TimeSpan.Zero)
                ScheduleFlushTimer();
            throw;
        }
    }

    private async ValueTask<ConstitutionAuditRecord?> FlushPendingUnderLocksAsync(
        ConstitutionAuditRecord? immediateRecord,
        CancellationToken cancellationToken)
    {
        int totalCount = _pendingBuffered.Count + (immediateRecord is not null ? 1 : 0);
        if (totalCount == 0)
            return null;

        await SyncSequenceAsync(cancellationToken).ConfigureAwait(false);

        long sequence = _recordCount;
        var batch = new List<(string TenantId, long Sequence, PersistedAudit Value)>(totalCount);
        ConstitutionAuditRecord? storedImmediate = null;

        foreach (var buffered in _pendingBuffered)
        {
            sequence++;
            var stored = buffered.WithSequence(sequence);
            batch.Add((stored.TenantId, sequence, PersistedAudit.From(stored)));
        }

        if (immediateRecord is not null)
        {
            sequence++;
            storedImmediate = immediateRecord.WithSequence(sequence);
            batch.Add((storedImmediate.TenantId, sequence, PersistedAudit.From(storedImmediate)));
        }

        await CrashSafeJsonPersistence.AppendBatchAsync(
            _path, StoreName, batch, flushToDisk: true, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _fsyncCount);

        // Observe the new file state before advancing the count or clearing the buffer. If the
        // append tore, _syncedLength stays stale, the next sync re-replays, and replay truncates
        // any partial tail.
        (_syncedLength, _syncedLastWriteTicks) = CurrentFileState();
        _recordCount = sequence;
        _pendingBuffered.Clear();
        return storedImmediate;
    }

    /// <summary>
    /// Bring the dense sequence counter in line with the journal, replaying only when the file
    /// changed since this instance last observed it. Caller must hold both <c>_gate</c> and the
    /// journal lock.
    /// </summary>
    private async ValueTask SyncSequenceAsync(CancellationToken cancellationToken)
    {
        var (length, lastWriteTicks) = CurrentFileState();
        if (_syncedLength == length && _syncedLastWriteTicks == lastWriteTicks)
            return;

        _ = await ReplayValidatedAsync(materializeRecords: false, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ConstitutionAuditRecord[]> ReplayValidatedAsync(
        bool materializeRecords,
        CancellationToken cancellationToken)
    {
        var replay = await CrashSafeJsonPersistence.ReplayAsync<PersistedAudit>(
            _path, StoreName, expectedTenant: null, cancellationToken).ConfigureAwait(false);

        // Validate every record before updating instance state. A mid-replay identity failure must
        // not leave _recordCount or _syncedLength half-updated: a retry must re-derive from disk
        // rather than skipping sequence numbers.
        var replayed = materializeRecords ? new ConstitutionAuditRecord[replay.Records.Count] : null;
        for (int i = 0; i < replay.Records.Count; i++)
        {
            var item = replay.Records[i];
            var persisted = item.Value;
            if (persisted.Sequence != item.Sequence || persisted.TenantId != item.TenantId)
                throw new InvalidDataException("Audit payload identity does not match its journal envelope.");
            if (replayed is not null)
                replayed[i] = persisted.ToDomain();
        }

        _recordCount = replay.Records.Count;
        if (replay.Diagnostics.Count > 0)
            lock (_diagnostics) _diagnostics.AddRange(replay.Diagnostics);
        // Recompute: replay truncates a corrupt tail, so the post-replay state is the authority.
        (_syncedLength, _syncedLastWriteTicks) = CurrentFileState();
        return replayed ?? Array.Empty<ConstitutionAuditRecord>();
    }

    private (long Length, long LastWriteTicks) CurrentFileState()
    {
        var info = new FileInfo(_path);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (0L, 0L);
    }

    private sealed record PersistedAudit(
        long Sequence,
        string EventId,
        string OperationId,
        string TenantId,
        string PrincipalId,
        ConstitutionPhase Phase,
        ConstitutionOutcome Outcome,
        string[] ConstitutionVersionHashes,
        string[] FindingCodes,
        DateTimeOffset OccurredAt)
    {
        public static PersistedAudit From(ConstitutionAuditRecord value)
            => new(value.Sequence, value.EventId, value.OperationId, value.TenantId, value.PrincipalId,
                value.Phase, value.Outcome,
                value.ConstitutionVersionHashes as string[] ?? value.ConstitutionVersionHashes.ToArray(),
                value.FindingCodes as string[] ?? value.FindingCodes.ToArray(),
                value.OccurredAt);

        public ConstitutionAuditRecord ToDomain()
            => new(Sequence, EventId, OperationId, TenantId, PrincipalId, Phase, Outcome,
                ConstitutionVersionHashes, FindingCodes, OccurredAt);
    }
}
