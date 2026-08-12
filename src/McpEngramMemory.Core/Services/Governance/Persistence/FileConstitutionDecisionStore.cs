using McpEngramMemory.Core.Models.Constitution;

namespace McpEngramMemory.Core.Services.Governance.Persistence;

/// <summary>Tenant-partitioned replay journal for complete deterministic Constitution decisions.</summary>
public sealed class FileConstitutionDecisionStore
{
    private const string StoreName = "constitution-decisions";
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, List<ConstitutionDecision>> _byTenant = new(StringComparer.Ordinal);
    private readonly List<PersistenceDiagnostic> _diagnostics = new();

    public FileConstitutionDecisionStore(string root)
        => _root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));

    public IReadOnlyList<PersistenceDiagnostic> Diagnostics
    {
        get { lock (_diagnostics) return _diagnostics.ToArray(); }
    }

    public async ValueTask<ConstitutionDecision> AppendAsync(
        string tenantId,
        ConstitutionDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        tenantId ??= string.Empty;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await GetTenantAsync(tenantId, cancellationToken);
            var existing = records.FirstOrDefault(value =>
                value.OperationId == decision.OperationId && value.Phase == decision.Phase);
            if (existing is not null)
            {
                if (!Equivalent(existing, decision))
                    throw new InvalidOperationException("A Constitution decision is immutable once journaled.");
                return existing;
            }

            await CrashSafeJsonPersistence.AppendAsync(
                PathFor(tenantId), StoreName, tenantId, records.Count + 1L,
                PersistedDecision.From(decision), cancellationToken);
            records.Add(decision);
            return decision;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ConstitutionDecision>> ReadAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        tenantId ??= string.Empty;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await GetTenantAsync(tenantId, cancellationToken)).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<List<ConstitutionDecision>> GetTenantAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        if (_byTenant.TryGetValue(tenantId, out var existing)) return existing;
        var replay = await CrashSafeJsonPersistence.ReplayAsync<PersistedDecision>(
            PathFor(tenantId), StoreName, tenantId, cancellationToken);
        var loaded = replay.Records.Select(value => value.Value.ToDomain()).ToList();
        if (loaded.GroupBy(value => (value.OperationId, value.Phase)).Any(group => group.Count() > 1))
            throw new InvalidDataException("Decision journal contains a duplicate operation phase.");
        _byTenant.Add(tenantId, loaded);
        lock (_diagnostics) _diagnostics.AddRange(replay.Diagnostics);
        return loaded;
    }

    private string PathFor(string tenantId)
        => Path.Combine(CrashSafeJsonPersistence.TenantDirectory(_root, tenantId), "decisions.journal");

    private static bool Equivalent(ConstitutionDecision left, ConstitutionDecision right)
        => left.Outcome == right.Outcome &&
           left.ConstitutionVersionHashes.SequenceEqual(right.ConstitutionVersionHashes) &&
           left.Findings.SequenceEqual(right.Findings);

    private sealed record PersistedFinding(
        string RuleId,
        string Code,
        ConstitutionOutcome Outcome,
        string Message,
        OperationArtifactReference[] Evidence)
    {
        public ConstitutionFinding ToDomain() => new(RuleId, Code, Outcome, Message, Evidence);
    }

    private sealed record PersistedDecision(
        string OperationId,
        ConstitutionPhase Phase,
        ConstitutionOutcome Outcome,
        PersistedFinding[] Findings,
        string[] ConstitutionVersionHashes)
    {
        public static PersistedDecision From(ConstitutionDecision value)
            => new(value.OperationId, value.Phase, value.Outcome,
                value.Findings.Select(finding => new PersistedFinding(
                    finding.RuleId, finding.Code, finding.Outcome, finding.Message,
                    finding.Evidence?.ToArray() ?? [])).ToArray(),
                value.ConstitutionVersionHashes.ToArray());

        public ConstitutionDecision ToDomain()
            => new(OperationId, Phase, Outcome, Findings.Select(value => value.ToDomain()),
                ConstitutionVersionHashes);
    }
}
