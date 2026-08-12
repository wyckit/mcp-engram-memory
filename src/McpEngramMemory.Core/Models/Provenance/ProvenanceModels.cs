using System.Collections.ObjectModel;
using McpEngramMemory.Core.Models.Knowledge;

namespace McpEngramMemory.Core.Models.Provenance;

/// <summary>Graph projections intentionally have different mutation and diffusion semantics.</summary>
public enum GraphProjectionKind
{
    CognitiveAssociation,
    Provenance
}
public enum ProvenanceRelation
{
    SourcedFrom,
    DerivedFrom,
    GeneratedBy,
    VerifiedBy,
    GovernedBy,
    AuditedBy,
    Supersedes,
    Redacts
}

/// <summary>
/// One immutable lineage assertion. The record names exact artifact versions and the complete
/// execution/governance identity required to explain how the target was produced.
/// </summary>
public sealed class ProvenanceAssertion
{
    public string AssertionId { get; }
    public string TenantId { get; }
    public ArtifactRef Target { get; }
    public IReadOnlyList<ArtifactRef> Sources { get; }
    public ProvenanceRelation Relation { get; }
    public string ActorId { get; }
    public string RuntimeId { get; }
    public string RuntimeVersion { get; }
    public IReadOnlyList<ArtifactRef> Verifiers { get; }
    public string ConstitutionVersionHash { get; }
    public string AuditEventId { get; }
    public PermissionEnvelope EffectivePermissions { get; }
    public DateTimeOffset RecordedAt { get; }
    public string ContentHash { get; }

    public GraphProjectionKind Projection => GraphProjectionKind.Provenance;
    public bool ParticipatesInDiffusion => false;

    public ProvenanceAssertion(
        string assertionId,
        ArtifactRef target,
        IEnumerable<ArtifactRef> sources,
        ProvenanceRelation relation,
        string actorId,
        string runtimeId,
        string runtimeVersion,
        IEnumerable<ArtifactRef>? verifiers,
        string constitutionVersionHash,
        string auditEventId,
        PermissionEnvelope effectivePermissions,
        DateTimeOffset recordedAt,
        string contentHash)
    {
        AssertionId = Required(assertionId, nameof(assertionId));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        TenantId = target.TenantId;
        Sources = new ReadOnlyCollection<ArtifactRef>((sources ?? throw new ArgumentNullException(nameof(sources)))
            .Distinct()
            .OrderBy(value => value.ToString(), StringComparer.Ordinal)
            .ToArray());
        if (Sources.Count == 0)
            throw new ArgumentException("A provenance assertion requires at least one exact source.", nameof(sources));
        if (Sources.Any(source => source.TenantId != TenantId))
            throw new ArgumentException("Cross-tenant provenance assertions are forbidden.", nameof(sources));
        Relation = relation;
        ActorId = Required(actorId, nameof(actorId));
        RuntimeId = Required(runtimeId, nameof(runtimeId));
        RuntimeVersion = Required(runtimeVersion, nameof(runtimeVersion));
        Verifiers = new ReadOnlyCollection<ArtifactRef>((verifiers ?? [])
            .Distinct()
            .OrderBy(value => value.ToString(), StringComparer.Ordinal)
            .ToArray());
        if (Verifiers.Any(verifier => verifier.TenantId != TenantId))
            throw new ArgumentException("Verifier references must be in the target tenant.", nameof(verifiers));
        ConstitutionVersionHash = Hash(constitutionVersionHash, nameof(constitutionVersionHash));
        AuditEventId = Required(auditEventId, nameof(auditEventId));
        EffectivePermissions = effectivePermissions ?? throw new ArgumentNullException(nameof(effectivePermissions));
        RecordedAt = recordedAt;
        ContentHash = Hash(contentHash, nameof(contentHash));
    }

    private static string Required(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty.", parameterName)
            : value.Trim();

    private static string Hash(string value, string parameterName)
    {
        var normalized = Required(value, parameterName).ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Value must be a SHA-256 hash.", parameterName);
        return normalized;
    }
}

public enum ProvenanceAppendOutcome
{
    Appended,
    AlreadyPresent
}

public sealed record ProvenanceAppendResult(ProvenanceAppendOutcome Outcome, ProvenanceAssertion Assertion);

public sealed class ProvenanceConflictException : InvalidOperationException
{
    public ProvenanceConflictException(string message) : base(message) { }
}

public sealed record ProvenanceQuery(
    string TenantId,
    ArtifactRef Root,
    string Subject,
    ArtifactCapability Capability,
    int MaxDepth = 32,
    int MaxAssertions = 1_000);

public sealed class ProvenanceLineage
{
    public ArtifactRef Root { get; }
    public IReadOnlyList<ProvenanceAssertion> Assertions { get; }
    public bool IsComplete { get; }

    public ProvenanceLineage(ArtifactRef root, IEnumerable<ProvenanceAssertion> assertions, bool isComplete)
    {
        Root = root;
        Assertions = new ReadOnlyCollection<ProvenanceAssertion>(assertions
            .OrderBy(value => value.RecordedAt)
            .ThenBy(value => value.AssertionId, StringComparer.Ordinal)
            .ToArray());
        IsComplete = isComplete;
    }
}
