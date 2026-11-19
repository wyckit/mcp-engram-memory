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
public sealed class FileConstitutionAuditStore : IConstitutionAuditStore
{
    private const string StoreName = "constitution-audit";

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
    private readonly List<PersistenceDiagnostic> _diagnostics = new();
    // Tracks only the dense record count and last-observed journal metadata instead of retaining
    // every historical audit record in RAM across long-running server lifetimes.
    // _syncedLength == -1 forces the first sync; any change in length or write timestamp means
    // a peer modified the journal and triggers a validated re-sync before assigning the next sequence.
    private long _recordCount;
    private long _syncedLength = -1;
    private long _syncedLastWriteTicks = -1;

    public FileConstitutionAuditStore(string root)
    {
        _path = Path.Combine(Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root))), "audit.journal");
        _gate = PathGates.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
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
            await using var journalLock = await CrashSafeJsonPersistence.AcquireExclusiveLockAsync(
                _path, LockTimeout, cancellationToken).ConfigureAwait(false);
            await SyncSequenceAsync(cancellationToken).ConfigureAwait(false);

            long sequence = _recordCount + 1L;
            var stored = record.WithSequence(sequence);
            await CrashSafeJsonPersistence.AppendAsync(
                _path, StoreName, stored.TenantId, sequence, PersistedAudit.From(stored), cancellationToken)
                .ConfigureAwait(false);
            // Observe the new file state before advancing the count. If the append tore,
            // _syncedLength stays stale, the next sync re-replays, and replay truncates the partial tail.
            (_syncedLength, _syncedLastWriteTicks) = CurrentFileState();
            _recordCount = sequence;
            return stored;
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
            return await ReplayValidatedAsync(materializeRecords: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
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
