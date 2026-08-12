using McpEngramMemory.Core.Models.Constitution;

namespace McpEngramMemory.Core.Services.Governance.Persistence;

/// <summary>Append-only, fsync-backed audit journal with deterministic corrupt-tail recovery.</summary>
public sealed class FileConstitutionAuditStore : IConstitutionAuditStore
{
    private const string StoreName = "constitution-audit";
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ConstitutionAuditRecord> _records = new();
    private readonly List<PersistenceDiagnostic> _diagnostics = new();
    private bool _loaded;

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
            await EnsureLoadedAsync(cancellationToken);
            long sequence = _records.Count + 1L;
            var stored = record.WithSequence(sequence);
            await CrashSafeJsonPersistence.AppendAsync(
                _path, StoreName, stored.TenantId, sequence, PersistedAudit.From(stored), cancellationToken);
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
            await EnsureLoadedAsync(cancellationToken);
            return _records.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        var replay = await CrashSafeJsonPersistence.ReplayAsync<PersistedAudit>(
            _path, StoreName, expectedTenant: null, cancellationToken);
        foreach (var item in replay.Records)
        {
            var record = item.Value.ToDomain();
            if (record.Sequence != item.Sequence || record.TenantId != item.TenantId)
                throw new InvalidDataException("Audit payload identity does not match its journal envelope.");
            _records.Add(record);
        }
        lock (_diagnostics) _diagnostics.AddRange(replay.Diagnostics);
        _loaded = true;
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
