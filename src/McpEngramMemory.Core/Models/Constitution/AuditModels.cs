using System.Collections.ObjectModel;

namespace McpEngramMemory.Core.Models.Constitution;

/// <summary>Immutable audit event. Sequence is assigned atomically by the append-only store.</summary>
public sealed class ConstitutionAuditRecord
{
    public long Sequence { get; }
    public string EventId { get; }
    public string OperationId { get; }
    public string TenantId { get; }
    public string PrincipalId { get; }
    public ConstitutionPhase Phase { get; }
    public ConstitutionOutcome Outcome { get; }
    public IReadOnlyList<string> ConstitutionVersionHashes { get; }
    public IReadOnlyList<string> FindingCodes { get; }
    public DateTimeOffset OccurredAt { get; }

    public ConstitutionAuditRecord(
        long sequence,
        string eventId,
        string operationId,
        string tenantId,
        string principalId,
        ConstitutionPhase phase,
        ConstitutionOutcome outcome,
        IEnumerable<string> constitutionVersionHashes,
        IEnumerable<string> findingCodes,
        DateTimeOffset occurredAt)
    {
        Sequence = sequence;
        EventId = eventId;
        OperationId = operationId;
        TenantId = tenantId;
        PrincipalId = principalId;
        Phase = phase;
        Outcome = outcome;
        ConstitutionVersionHashes = new ReadOnlyCollection<string>(constitutionVersionHashes.ToArray());
        FindingCodes = new ReadOnlyCollection<string>(findingCodes.ToArray());
        OccurredAt = occurredAt;
    }

    internal ConstitutionAuditRecord WithSequence(long sequence)
        => new(sequence, EventId, OperationId, TenantId, PrincipalId, Phase, Outcome,
            ConstitutionVersionHashes, FindingCodes, OccurredAt);
}

/// <summary>Append-only audit capability. It intentionally exposes no update or delete operation.</summary>
public interface IConstitutionAuditStore
{
    ValueTask<ConstitutionAuditRecord> AppendAsync(
        ConstitutionAuditRecord record,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ConstitutionAuditRecord>> ReadAllAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Versions frozen by an allow decision and rechecked immediately before commit.</summary>
public sealed class CommitAuthorizationSnapshot
{
    public string ConstitutionVersionHash { get; }
    public IReadOnlyDictionary<string, string> ResourceVersions { get; }

    public CommitAuthorizationSnapshot(
        string constitutionVersionHash,
        IReadOnlyDictionary<string, string>? resourceVersions = null)
    {
        ConstitutionVersionHash = string.IsNullOrWhiteSpace(constitutionVersionHash)
            ? throw new ArgumentException("Constitution hash must not be empty.", nameof(constitutionVersionHash))
            : constitutionVersionHash.Trim().ToLowerInvariant();
        var sortedVersions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (resourceVersions is not null)
        {
            foreach (var (resource, version) in resourceVersions)
                sortedVersions[resource] = version;
        }
        ResourceVersions = new ReadOnlyDictionary<string, string>(sortedVersions);
    }
}

public sealed record CommitRecheckResult(
    bool CanCommit,
    string Code,
    IReadOnlyList<string> ChangedResources);
