using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Models.Learning;
using McpEngramMemory.Core.Models.Provenance;

namespace McpEngramMemory.Core.Models.Governance;

/// <summary>All records that must become visible atomically during knowledge promotion.</summary>
public sealed record GovernedPromotionCommit(
    KnowledgeVersion Version,
    string? ExpectedActiveVersionHash,
    PromotionResult Promotion,
    ProvenanceAssertion Provenance,
    ConstitutionCommitReceipt Receipt,
    CommitAuthorizationSnapshot AuthorizationSnapshot,
    IReadOnlyDictionary<ArtifactRef, PermissionEnvelope> SourcePermissionSnapshots);

/// <summary>
/// Authoritative state sampled inside the store's commit lock. The resolver is deliberately
/// invoked at the visibility boundary so callers cannot pass a previously captured "current"
/// value that becomes stale before publication.
/// </summary>
public sealed record CommitAuthorityState(
    string ConstitutionVersionHash,
    IReadOnlyDictionary<string, string> ResourceVersions);

public enum GovernedCommitOutcome
{
    Committed,
    AlreadyCommitted,
    Denied,
    VersionConflict
}

public sealed record GovernedCommitResult(
    GovernedCommitOutcome Outcome,
    string Code,
    ArtifactRef? ActiveVersion,
    string? ActiveVersionHash);

public sealed record GovernedKnowledgeSnapshot(
    KnowledgeAsset? Asset,
    KnowledgeVersion? ActiveVersion,
    IReadOnlyList<ProvenanceAssertion> Provenance,
    IReadOnlyList<ConstitutionAuditRecord> Audit);
