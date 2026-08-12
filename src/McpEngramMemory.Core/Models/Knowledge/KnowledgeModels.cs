using System.Collections.ObjectModel;

namespace McpEngramMemory.Core.Models.Knowledge;

public enum ArtifactKind
{
    Memory,
    Knowledge,
    Skill,
    Evidence,
    Verification,
    Document,
    Code,
    Curriculum,
    DeclassificationProposal,
    Approval
}

/// <summary>An exact, tenant- and namespace-scoped reference to a versioned artifact.</summary>
public sealed record ArtifactRef
{
    public string TenantId { get; }
    public string Namespace { get; }
    public ArtifactKind Kind { get; }
    public string ArtifactId { get; }
    public string Version { get; }

    public ArtifactRef(string tenantId, string @namespace, ArtifactKind kind, string artifactId, string version)
    {
        TenantId = string.IsNullOrWhiteSpace(tenantId) ? string.Empty : tenantId.Trim();
        Namespace = Required(@namespace, nameof(@namespace));
        Kind = kind;
        ArtifactId = Required(artifactId, nameof(artifactId));
        Version = Required(version, nameof(version));
    }

    public override string ToString()
        => $"{TenantId}/{Namespace}/{Kind}/{ArtifactId}@{Version}";

    private static string Required(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty.", parameterName)
            : value.Trim();
}

public enum KnowledgeMaturity
{
    Proposed,
    Hypothesized,
    Supported,
    Verified,
    Established
}

public enum KnowledgeStatus
{
    Active,
    Disputed,
    Superseded,
    Withdrawn
}

/// <summary>Valid-time and transaction-time coordinates for one knowledge version.</summary>
public sealed record BitemporalValidity
{
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset RecordedAt { get; }
    public DateTimeOffset ValidFrom { get; }
    public DateTimeOffset? ValidUntil { get; }
    public DateTimeOffset? VerifiedAt { get; }
    public DateTimeOffset? SupersededAt { get; }

    public BitemporalValidity(
        DateTimeOffset createdAt,
        DateTimeOffset recordedAt,
        DateTimeOffset validFrom,
        DateTimeOffset? validUntil = null,
        DateTimeOffset? verifiedAt = null,
        DateTimeOffset? supersededAt = null)
    {
        if (validUntil < validFrom)
            throw new ArgumentException("ValidUntil cannot precede ValidFrom.", nameof(validUntil));
        CreatedAt = createdAt;
        RecordedAt = recordedAt;
        ValidFrom = validFrom;
        ValidUntil = validUntil;
        VerifiedAt = verifiedAt;
        SupersededAt = supersededAt;
    }
}

/// <summary>One calibrated epistemic component, including its method and versioned basis.</summary>
public sealed record CalibratedComponent
{
    public decimal Value { get; }
    public string Basis { get; }
    public string CalibrationVersion { get; }
    public DateTimeOffset EvaluatedAt { get; }

    public CalibratedComponent(decimal value, string basis, string calibrationVersion, DateTimeOffset evaluatedAt)
    {
        if (value is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(value), "A calibrated component must be in [0,1].");
        Value = value;
        Basis = Required(basis, nameof(basis));
        CalibrationVersion = Required(calibrationVersion, nameof(calibrationVersion));
        EvaluatedAt = evaluatedAt;
    }

    private static string Required(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty.", parameterName)
            : value.Trim();
}

/// <summary>Explainable epistemic dimensions. No opaque aggregate truth score is stored.</summary>
public sealed record EpistemicProfile(
    CalibratedComponent Confidence,
    CalibratedComponent Authority,
    CalibratedComponent Trust,
    CalibratedComponent EvidenceStrength,
    CalibratedComponent Freshness,
    CalibratedComponent Consensus);

/// <summary>An exact, stable source version and the authorization attached to traversing it.</summary>
public sealed record EvidenceReference
{
    public ArtifactRef Artifact { get; }
    public string ContentHash { get; }
    public DateTimeOffset ObservedAt { get; }
    public string IndependentSourceKey { get; }
    public PermissionEnvelope Permissions { get; }

    public bool IsStable => ContentHash.Length == 64 && ContentHash.All(Uri.IsHexDigit);

    public EvidenceReference(
        ArtifactRef artifact,
        string contentHash,
        DateTimeOffset observedAt,
        string independentSourceKey,
        PermissionEnvelope permissions)
    {
        Artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
        ContentHash = string.IsNullOrWhiteSpace(contentHash)
            ? throw new ArgumentException("Evidence must name an exact content hash.", nameof(contentHash))
            : contentHash.Trim().ToLowerInvariant();
        ObservedAt = observedAt;
        IndependentSourceKey = string.IsNullOrWhiteSpace(independentSourceKey)
            ? throw new ArgumentException("Evidence must identify its independent source.", nameof(independentSourceKey))
            : independentSourceKey.Trim();
        Permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
    }
}

/// <summary>Immutable semantic state from which a content-addressed knowledge version is published.</summary>
public sealed class KnowledgeVersionDefinition
{
    public ArtifactRef Reference { get; }
    public string Claim { get; }
    public KnowledgeMaturity Maturity { get; }
    public KnowledgeStatus Status { get; }
    public BitemporalValidity Temporal { get; }
    public EpistemicProfile Epistemic { get; }
    public IReadOnlyList<EvidenceReference> SupportingEvidence { get; }
    public IReadOnlyList<EvidenceReference> ContradictingEvidence { get; }
    public PermissionEnvelope Permissions { get; }
    public string ConstitutionVersionHash { get; }
    public string DerivationBranchId { get; }

    public KnowledgeVersionDefinition(
        ArtifactRef reference,
        string claim,
        KnowledgeMaturity maturity,
        KnowledgeStatus status,
        BitemporalValidity temporal,
        EpistemicProfile epistemic,
        IEnumerable<EvidenceReference>? supportingEvidence,
        IEnumerable<EvidenceReference>? contradictingEvidence,
        PermissionEnvelope permissions,
        string constitutionVersionHash,
        string derivationBranchId = "main")
    {
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));
        if (reference.Kind != ArtifactKind.Knowledge)
            throw new ArgumentException("A knowledge version must use ArtifactKind.Knowledge.", nameof(reference));
        Claim = Required(claim, nameof(claim));
        Maturity = maturity;
        Status = status;
        Temporal = temporal ?? throw new ArgumentNullException(nameof(temporal));
        Epistemic = epistemic ?? throw new ArgumentNullException(nameof(epistemic));
        SupportingEvidence = ReadOnlyEvidence(supportingEvidence);
        ContradictingEvidence = ReadOnlyEvidence(contradictingEvidence);
        if (SupportingEvidence.Concat(ContradictingEvidence)
            .Any(value => !string.Equals(value.Artifact.TenantId, Reference.TenantId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Knowledge evidence must belong to the same tenant as the knowledge version.",
                nameof(supportingEvidence));
        }
        Permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        ConstitutionVersionHash = Required(constitutionVersionHash, nameof(constitutionVersionHash)).ToLowerInvariant();
        DerivationBranchId = Required(derivationBranchId, nameof(derivationBranchId));
    }

    private static IReadOnlyList<EvidenceReference> ReadOnlyEvidence(IEnumerable<EvidenceReference>? evidence)
        => new ReadOnlyCollection<EvidenceReference>((evidence ?? Array.Empty<EvidenceReference>())
            .GroupBy(value => (value.Artifact, value.ContentHash))
            .Select(group => group.First())
            .OrderBy(value => value.Artifact.ToString(), StringComparer.Ordinal)
            .ThenBy(value => value.ContentHash, StringComparer.Ordinal)
            .ToArray());

    private static string Required(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty.", parameterName)
            : value.Trim();
}

/// <summary>An immutable knowledge version addressed by its canonical SHA-256 hash.</summary>
public sealed record KnowledgeVersion(KnowledgeVersionDefinition Definition, string ContentHash)
{
    public ArtifactRef Reference => Definition.Reference;
    public KnowledgeMaturity Maturity => Definition.Maturity;
    public KnowledgeStatus Status => Definition.Status;
    public IReadOnlyList<EvidenceReference> SupportingEvidence => Definition.SupportingEvidence;
    public IReadOnlyList<EvidenceReference> ContradictingEvidence => Definition.ContradictingEvidence;
    public PermissionEnvelope Permissions => Definition.Permissions;
}

/// <summary>An immutable aggregate of exact versions with a content-addressed active pointer.</summary>
public sealed class KnowledgeAsset
{
    public string TenantId { get; }
    public string Namespace { get; }
    public string ArtifactId { get; }
    public IReadOnlyList<KnowledgeVersion> Versions { get; }
    public string ActiveVersionHash { get; }
    public string ContentHash { get; }

    public KnowledgeAsset(
        string tenantId,
        string @namespace,
        string artifactId,
        IEnumerable<KnowledgeVersion> versions,
        string activeVersionHash,
        string contentHash)
    {
        ArgumentNullException.ThrowIfNull(versions);
        TenantId = string.IsNullOrWhiteSpace(tenantId) ? string.Empty : tenantId.Trim();
        Namespace = Required(@namespace, nameof(@namespace));
        ArtifactId = Required(artifactId, nameof(artifactId));
        Versions = new ReadOnlyCollection<KnowledgeVersion>(versions.ToArray());
        if (Versions.Count == 0)
            throw new ArgumentException("A knowledge asset requires at least one version.", nameof(versions));
        ActiveVersionHash = Required(activeVersionHash, nameof(activeVersionHash)).ToLowerInvariant();
        ContentHash = Required(contentHash, nameof(contentHash)).ToLowerInvariant();
    }

    private static string Required(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty.", parameterName)
            : value.Trim();
}

public sealed record LeakageCheckResult(
    string CheckId,
    bool Passed,
    string Details,
    DateTimeOffset CheckedAt);

public sealed class DeclassificationProposal
{
    public string ProposalId { get; }
    public ArtifactRef Source { get; }
    public ArtifactRef ProposedBranch { get; }
    public PermissionEnvelope ProposedPermissions { get; }
    public IReadOnlyList<EvidenceReference> SanitizationEvidence { get; }
    public IReadOnlyList<LeakageCheckResult> LeakageChecks { get; }
    public string RequestedBy { get; }
    public DateTimeOffset CreatedAt { get; }

    public DeclassificationProposal(
        string proposalId,
        ArtifactRef source,
        ArtifactRef proposedBranch,
        PermissionEnvelope proposedPermissions,
        IEnumerable<EvidenceReference> sanitizationEvidence,
        IEnumerable<LeakageCheckResult> leakageChecks,
        string requestedBy,
        DateTimeOffset createdAt)
    {
        ProposalId = Required(proposalId, nameof(proposalId));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ProposedBranch = proposedBranch ?? throw new ArgumentNullException(nameof(proposedBranch));
        if (Source == ProposedBranch)
            throw new ArgumentException("Declassification must publish a distinct derivation branch.", nameof(proposedBranch));
        ProposedPermissions = proposedPermissions ?? throw new ArgumentNullException(nameof(proposedPermissions));
        SanitizationEvidence = new ReadOnlyCollection<EvidenceReference>(sanitizationEvidence.ToArray());
        LeakageChecks = new ReadOnlyCollection<LeakageCheckResult>(leakageChecks.ToArray());
        RequestedBy = Required(requestedBy, nameof(requestedBy));
        CreatedAt = createdAt;
    }

    private static string Required(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty.", parameterName)
            : value.Trim();
}

public sealed record DeclassificationApproval(
    string ProposalId,
    string Approver,
    PermissionEnvelope AuthorizationSnapshot,
    ArtifactRef ApprovalRecord,
    DateTimeOffset ApprovedAt);

/// <summary>The auditable new branch; Original is retained byte-for-byte and is never replaced.</summary>
public sealed record DeclassificationBranch(
    KnowledgeVersion Original,
    KnowledgeVersion Released,
    DeclassificationProposal Proposal,
    DeclassificationApproval Approval,
    string OriginalContentHash);
