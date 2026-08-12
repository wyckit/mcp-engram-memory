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

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ConstitutionAuditRecord> _records = new();
    private readonly List<PersistenceDiagnostic> _diagnostics = new();
    // Journal length observed at the end of the last successful sync or append. -1 forces the
    // first sync. Any other value that disagrees with the file means a peer appended.
    private long _syncedLength = -1;

    public FileConstitutionAuditStore(string root)
        => _path = Path.Combine(Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root))), "audit.journal");

    public IReadOnlyList<PersistenceDiagnostic> Diagnostics
    {
        get { lock (_diagnostics) return _diagnostics.ToArray(); }
    }

    public async ValueTask<ConstitutionAuditRecord> AppendAsync(
        ConstitutionAuditRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var journalLock = await CrashSafeJsonPersistence.AcquireExclusiveLockAsync(
                _path, LockTimeout, cancellationToken).ConfigureAwait(false);
            await SyncAsync(cancellationToken);

            long sequence = _records.Count + 1L;
            var stored = record.WithSequence(sequence);
            await CrashSafeJsonPersistence.AppendAsync(
                _path, StoreName, stored.TenantId, sequence, PersistedAudit.From(stored), cancellationToken);
            // Observe the new length before publishing the record. If the append tore, _syncedLength
            // stays stale, the next sync re-replays, and replay truncates the partial tail.
            _syncedLength = CurrentLength();
            _records.Add(stored);
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
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Read under the same lock as append: replay truncates a torn tail, and doing that to a
            // peer's in-flight write would destroy a record that is about to be committed.
            await using var journalLock = await CrashSafeJsonPersistence.AcquireExclusiveLockAsync(
                _path, LockTimeout, cancellationToken).ConfigureAwait(false);
            await SyncAsync(cancellationToken);
            return _records.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Bring in-memory state in line with the journal, replaying only when the file changed since
    /// this instance last observed it. Caller must hold both <c>_gate</c> and the journal lock.
    /// </summary>
    private async ValueTask SyncAsync(CancellationToken cancellationToken)
    {
        if (_syncedLength == CurrentLength()) return;

        var replay = await CrashSafeJsonPersistence.ReplayAsync<PersistedAudit>(
            _path, StoreName, expectedTenant: null, cancellationToken);

        // Materialize into a local list first. A mid-replay identity failure must not leave the
        // in-memory journal half-applied: the retry would replay the same records on top of the
        // partial set, inflating the count and making the next append skip sequence numbers — the
        // exact dense-sequence break this store treats as unrecoverable corruption.
        var replayed = new List<ConstitutionAuditRecord>(replay.Records.Count);
        foreach (var item in replay.Records)
        {
            var record = item.Value.ToDomain();
            if (record.Sequence != item.Sequence || record.TenantId != item.TenantId)
                throw new InvalidDataException("Audit payload identity does not match its journal envelope.");
            replayed.Add(record);
        }

        _records.Clear();
        _records.AddRange(replayed);
        lock (_diagnostics) _diagnostics.AddRange(replay.Diagnostics);
        // Recompute: replay truncates a corrupt tail, so the post-replay length is the authority.
        _syncedLength = CurrentLength();
    }

    private long CurrentLength()
    {
        var info = new FileInfo(_path);
        return info.Exists ? info.Length : 0L;
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
                value.Phase, value.Outcome, value.ConstitutionVersionHashes.ToArray(),
                value.FindingCodes.ToArray(), value.OccurredAt);

        public ConstitutionAuditRecord ToDomain()
            => new(Sequence, EventId, OperationId, TenantId, PrincipalId, Phase, Outcome,
                ConstitutionVersionHashes, FindingCodes, OccurredAt);
    }
}
