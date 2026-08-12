using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Models.Provenance;

namespace McpEngramMemory.Core.Services.Governance.Persistence;

internal sealed record PermissionGrantDto(ArtifactCapability Capability, string[] Subjects);

internal sealed record PermissionEnvelopeDto(PermissionGrantDto[] Grants)
{
    public static PermissionEnvelopeDto From(PermissionEnvelope value)
        => new(value.Grants.Select(grant =>
            new PermissionGrantDto(grant.Capability, grant.Subjects.ToArray())).ToArray());

    public PermissionEnvelope ToDomain()
        => new(Grants.Select(grant => new CapabilityGrant(grant.Capability, grant.Subjects)));
}

internal sealed record EvidenceReferenceDto(
    ArtifactRef Artifact,
    string ContentHash,
    DateTimeOffset ObservedAt,
    string IndependentSourceKey,
    PermissionEnvelopeDto Permissions)
{
    public static EvidenceReferenceDto From(EvidenceReference value)
        => new(value.Artifact, value.ContentHash, value.ObservedAt, value.IndependentSourceKey,
            PermissionEnvelopeDto.From(value.Permissions));

    public EvidenceReference ToDomain()
        => new(Artifact, ContentHash, ObservedAt, IndependentSourceKey, Permissions.ToDomain());
}

internal sealed record TemporalDto(
    DateTimeOffset CreatedAt,
    DateTimeOffset RecordedAt,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidUntil,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? SupersededAt)
{
    public static TemporalDto From(BitemporalValidity value)
        => new(value.CreatedAt, value.RecordedAt, value.ValidFrom, value.ValidUntil,
            value.VerifiedAt, value.SupersededAt);

    public BitemporalValidity ToDomain()
        => new(CreatedAt, RecordedAt, ValidFrom, ValidUntil, VerifiedAt, SupersededAt);
}

internal sealed record ComponentDto(
    decimal Value,
    string Basis,
    string CalibrationVersion,
    DateTimeOffset EvaluatedAt)
{
    public static ComponentDto From(CalibratedComponent value)
        => new(value.Value, value.Basis, value.CalibrationVersion, value.EvaluatedAt);

    public CalibratedComponent ToDomain()
        => new(Value, Basis, CalibrationVersion, EvaluatedAt);
}

internal sealed record EpistemicDto(
    ComponentDto Confidence,
    ComponentDto Authority,
    ComponentDto Trust,
    ComponentDto EvidenceStrength,
    ComponentDto Freshness,
    ComponentDto Consensus)
{
    public static EpistemicDto From(EpistemicProfile value)
        => new(ComponentDto.From(value.Confidence), ComponentDto.From(value.Authority),
            ComponentDto.From(value.Trust), ComponentDto.From(value.EvidenceStrength),
            ComponentDto.From(value.Freshness), ComponentDto.From(value.Consensus));

    public EpistemicProfile ToDomain()
        => new(Confidence.ToDomain(), Authority.ToDomain(), Trust.ToDomain(),
            EvidenceStrength.ToDomain(), Freshness.ToDomain(), Consensus.ToDomain());
}

internal sealed record KnowledgeVersionDto(
    ArtifactRef Reference,
    string Claim,
    KnowledgeMaturity Maturity,
    KnowledgeStatus Status,
    TemporalDto Temporal,
    EpistemicDto Epistemic,
    EvidenceReferenceDto[] SupportingEvidence,
    EvidenceReferenceDto[] ContradictingEvidence,
    PermissionEnvelopeDto Permissions,
    string ConstitutionVersionHash,
    string DerivationBranchId,
    string ContentHash)
{
    public static KnowledgeVersionDto From(KnowledgeVersion value)
        => new(value.Reference, value.Definition.Claim, value.Maturity, value.Status,
            TemporalDto.From(value.Definition.Temporal), EpistemicDto.From(value.Definition.Epistemic),
            value.SupportingEvidence.Select(EvidenceReferenceDto.From).ToArray(),
            value.ContradictingEvidence.Select(EvidenceReferenceDto.From).ToArray(),
            PermissionEnvelopeDto.From(value.Permissions), value.Definition.ConstitutionVersionHash,
            value.Definition.DerivationBranchId, value.ContentHash);

    public KnowledgeVersion ToDomain()
        => new(new KnowledgeVersionDefinition(
                Reference, Claim, Maturity, Status, Temporal.ToDomain(), Epistemic.ToDomain(),
                SupportingEvidence.Select(value => value.ToDomain()),
                ContradictingEvidence.Select(value => value.ToDomain()), Permissions.ToDomain(),
                ConstitutionVersionHash, DerivationBranchId),
            ContentHash);
}

internal sealed record KnowledgeAssetDto(
    string TenantId,
    string Namespace,
    string ArtifactId,
    KnowledgeVersionDto[] Versions,
    string ActiveVersionHash,
    string ContentHash)
{
    public static KnowledgeAssetDto From(KnowledgeAsset value)
        => new(value.TenantId, value.Namespace, value.ArtifactId,
            value.Versions.Select(KnowledgeVersionDto.From).ToArray(),
            value.ActiveVersionHash, value.ContentHash);

    public KnowledgeAsset ToDomain()
        => new(TenantId, Namespace, ArtifactId, Versions.Select(value => value.ToDomain()),
            ActiveVersionHash, ContentHash);
}

internal sealed record ConstitutionConstraintDto(
    bool PreserveProvenance,
    bool RequireEvidenceForKnowledge,
    bool PreserveContradictions,
    bool RequireDeterministicVerificationFirst,
    bool RequireExplainability,
    bool RequireAudit,
    int MinimumEvidenceCount,
    CognitiveOperationKind[] AllowedOperations)
{
    public static ConstitutionConstraintDto From(ConstitutionConstraints value)
        => new(value.PreserveProvenance, value.RequireEvidenceForKnowledge,
            value.PreserveContradictions, value.RequireDeterministicVerificationFirst,
            value.RequireExplainability, value.RequireAudit, value.MinimumEvidenceCount,
            value.AllowedOperations.ToArray());

    public ConstitutionConstraints ToDomain()
        => new(PreserveProvenance, RequireEvidenceForKnowledge, PreserveContradictions,
            RequireDeterministicVerificationFirst, RequireExplainability, RequireAudit,
            MinimumEvidenceCount, AllowedOperations);
}

internal sealed record ConstitutionRuleDto(
    string RuleId,
    string RuleVersion,
    string ImplementationId,
    string Description,
    int Priority,
    CognitiveOperationKind[] AppliesTo)
{
    public static ConstitutionRuleDto From(ConstitutionRuleDefinition value)
        => new(value.RuleId, value.RuleVersion, value.ImplementationId, value.Description,
            value.Priority, value.AppliesTo.ToArray());

    public ConstitutionRuleDefinition ToDomain()
        => new(RuleId, RuleVersion, ImplementationId, Description, Priority, AppliesTo);
}

internal sealed record ConstitutionVersionDto(
    string ConstitutionId,
    string Name,
    ConstitutionLayerKind LayerKind,
    string? ParentVersionHash,
    ConstitutionConstraintDto Constraints,
    string[] Principles,
    ConstitutionRuleDto[] Rules,
    string Version,
    DateTimeOffset PublishedAt,
    string? SupersedesVersionHash,
    string ContentHash)
{
    public static ConstitutionVersionDto From(ConstitutionVersion value)
        => new(value.Definition.ConstitutionId, value.Definition.Name, value.Definition.LayerKind,
            value.Definition.ParentVersionHash, ConstitutionConstraintDto.From(value.Definition.Constraints),
            value.Definition.Principles.ToArray(), value.Definition.Rules.Select(ConstitutionRuleDto.From).ToArray(),
            value.Version, value.PublishedAt, value.SupersedesVersionHash, value.ContentHash);

    public ConstitutionVersion ToDomain()
        => new(new ConstitutionDefinition(ConstitutionId, Name, LayerKind, Constraints.ToDomain(),
                Principles, Rules.Select(value => value.ToDomain()), ParentVersionHash),
            Version, PublishedAt, SupersedesVersionHash, ContentHash);
}

internal sealed record PersistedConstitutionSetDto(
    string TenantId,
    ConstitutionVersionDto[] Versions,
    string ActiveVersionHash)
{
    public static PersistedConstitutionSetDto From(PersistedConstitutionSet value)
        => new(value.TenantId, value.Versions.Select(ConstitutionVersionDto.From).ToArray(),
            value.ActiveVersionHash);

    public PersistedConstitutionSet ToDomain()
        => new(TenantId, Versions.Select(value => value.ToDomain()).ToArray(), ActiveVersionHash);
}

internal sealed record ProvenanceAssertionDto(
    string AssertionId,
    ArtifactRef Target,
    ArtifactRef[] Sources,
    ProvenanceRelation Relation,
    string ActorId,
    string RuntimeId,
    string RuntimeVersion,
    ArtifactRef[] Verifiers,
    string ConstitutionVersionHash,
    string AuditEventId,
    PermissionEnvelopeDto EffectivePermissions,
    DateTimeOffset RecordedAt,
    string ContentHash)
{
    public static ProvenanceAssertionDto From(ProvenanceAssertion value)
        => new(value.AssertionId, value.Target, value.Sources.ToArray(), value.Relation,
            value.ActorId, value.RuntimeId, value.RuntimeVersion, value.Verifiers.ToArray(),
            value.ConstitutionVersionHash, value.AuditEventId,
            PermissionEnvelopeDto.From(value.EffectivePermissions), value.RecordedAt, value.ContentHash);

    public ProvenanceAssertion ToDomain()
        => new(AssertionId, Target, Sources, Relation, ActorId, RuntimeId, RuntimeVersion,
            Verifiers, ConstitutionVersionHash, AuditEventId, EffectivePermissions.ToDomain(),
            RecordedAt, ContentHash);
}
