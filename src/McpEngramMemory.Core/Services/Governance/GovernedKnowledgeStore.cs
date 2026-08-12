using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Models.Governance;
using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Models.Learning;
using McpEngramMemory.Core.Models.Provenance;
using McpEngramMemory.Core.Services.Constitution;
using McpEngramMemory.Core.Services.Knowledge;
using McpEngramMemory.Core.Services.Provenance;

namespace McpEngramMemory.Core.Services.Governance;

/// <summary>Focused transactional store; intentionally separate from legacy memory persistence.</summary>
public interface IGovernedKnowledgeStore
{
    ValueTask<GovernedCommitResult> CommitPromotionAsync(
        GovernedPromotionCommit commit,
        Func<CommitAuthorityState> resolveCurrentAuthority,
        CancellationToken cancellationToken = default);

    ValueTask<GovernedKnowledgeSnapshot> ReadAsync(
        string tenantId,
        string @namespace,
        string artifactId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reference implementation of the atomic promotion boundary. Asset version, active pointer,
/// provenance, and audit become visible under one lock or not at all.
/// </summary>
public sealed class InMemoryGovernedKnowledgeStore : IGovernedKnowledgeStore
{
    private readonly object _gate = new();
    private readonly ConstitutionCommitGuard _commitGuard;
    private readonly Dictionary<(string Tenant, string Namespace, string Id), List<KnowledgeVersion>> _versions = new();
    private readonly Dictionary<(string Tenant, string Namespace, string Id), string> _active = new();
    private readonly Dictionary<(string Tenant, string AssertionId), ProvenanceAssertion> _provenance = new();
    private readonly List<ConstitutionAuditRecord> _audit = new();

    public InMemoryGovernedKnowledgeStore(ConstitutionCommitGuard? commitGuard = null)
        => _commitGuard = commitGuard ?? new ConstitutionCommitGuard();

    public ValueTask<GovernedCommitResult> CommitPromotionAsync(
        GovernedPromotionCommit commit,
        Func<CommitAuthorityState> resolveCurrentAuthority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(resolveCurrentAuthority);
        cancellationToken.ThrowIfCancellationRequested();
        if (commit.Promotion.Outcome != PromotionOutcome.Promoted)
            return ValueTask.FromResult(Denied("promotion-not-authorized"));

        var version = commit.Version;
        if (!string.Equals(KnowledgeCanonicalizer.ComputeHash(version), version.ContentHash, StringComparison.Ordinal))
            return ValueTask.FromResult(Denied("knowledge-hash-invalid"));
        if (!string.Equals(ProvenanceCanonicalizer.ComputeHash(commit.Provenance),
                commit.Provenance.ContentHash, StringComparison.Ordinal))
            return ValueTask.FromResult(Denied("provenance-hash-invalid"));
        if (commit.Provenance.Target != version.Reference)
            return ValueTask.FromResult(Denied("provenance-target-mismatch"));
        if (commit.Provenance.AuditEventId != commit.Receipt.AuditRecord.EventId)
            return ValueTask.FromResult(Denied("provenance-audit-receipt-mismatch"));
        var receipt = commit.Receipt;
        var audit = receipt.AuditRecord;
        if (!ReferenceEquals(commit.Promotion.ConstitutionDecision, receipt.Decision) ||
            audit.OperationId != receipt.Decision.OperationId ||
            audit.Outcome != ConstitutionOutcome.Allow)
            return ValueTask.FromResult(Denied("audit-decision-mismatch"));
        var decision = receipt.Decision;
        if (decision.Outcome != ConstitutionOutcome.Allow ||
            decision.Phase != ConstitutionPhase.Commit ||
            audit.Phase != ConstitutionPhase.Commit ||
            audit.TenantId != version.Reference.TenantId ||
            !decision.ConstitutionVersionHashes.SequenceEqual(audit.ConstitutionVersionHashes) ||
            !decision.ConstitutionVersionHashes.Contains(
                commit.AuthorizationSnapshot.ConstitutionVersionHash, StringComparer.Ordinal))
        {
            return ValueTask.FromResult(Denied("constitution-commit-receipt-invalid"));
        }
        if (!ReceiptTargetsVersion(receipt, version))
            return ValueTask.FromResult(Denied("constitution-commit-target-mismatch"));

        if (commit.Provenance.Sources.Any(source => !commit.SourcePermissionSnapshots.ContainsKey(source)))
            return ValueTask.FromResult(Denied("source-authorization-missing"));
        var evidence = version.SupportingEvidence.Concat(version.ContradictingEvidence).ToArray();
        if (evidence.Any(item => !commit.Provenance.Sources.Contains(item.Artifact)))
            return ValueTask.FromResult(Denied("evidence-provenance-mismatch"));
        if (evidence.Any(item => !PermissionEquivalent(
                item.Permissions, commit.SourcePermissionSnapshots[item.Artifact])))
            return ValueTask.FromResult(Denied("evidence-authorization-snapshot-mismatch"));
        var inherited = PermissionEnvelopeService.Intersect(
            commit.Provenance.Sources.Select(source => commit.SourcePermissionSnapshots[source]));
        if (!PermissionEnvelopeService.IsNarrowerThanOrEqual(version.Permissions, inherited) ||
            !PermissionEnvelopeService.IsNarrowerThanOrEqual(commit.Provenance.EffectivePermissions, inherited))
            return ValueTask.FromResult(Denied("permission-broadening-forbidden"));

        lock (_gate)
        {
            var authority = resolveCurrentAuthority();
            var recheck = _commitGuard.Recheck(commit.AuthorizationSnapshot,
                authority.ConstitutionVersionHash, authority.ResourceVersions);
            if (!recheck.CanCommit)
                return ValueTask.FromResult(Denied(recheck.Code));

            var reference = version.Reference;
            var key = (reference.TenantId, reference.Namespace, reference.ArtifactId);
            _active.TryGetValue(key, out var activeHash);
            if (!string.Equals(activeHash, commit.ExpectedActiveVersionHash, StringComparison.Ordinal))
                return ValueTask.FromResult(new GovernedCommitResult(
                    GovernedCommitOutcome.VersionConflict, "active-version-changed", null, activeHash));

            if (_provenance.TryGetValue((reference.TenantId, commit.Provenance.AssertionId), out var existingAssertion) &&
                !string.Equals(existingAssertion.ContentHash, commit.Provenance.ContentHash, StringComparison.Ordinal))
                return ValueTask.FromResult(Denied("provenance-id-conflict"));

            if (!_versions.TryGetValue(key, out var versions))
            {
                versions = new List<KnowledgeVersion>();
                _versions.Add(key, versions);
            }
            var existingVersion = versions.FirstOrDefault(item => item.Reference.Version == reference.Version);
            if (existingVersion is not null)
            {
                if (!string.Equals(existingVersion.ContentHash, version.ContentHash, StringComparison.Ordinal))
                    return ValueTask.FromResult(Denied("knowledge-version-conflict"));
                return ValueTask.FromResult(new GovernedCommitResult(
                    GovernedCommitOutcome.AlreadyCommitted, "already-committed",
                    existingVersion.Reference, existingVersion.ContentHash));
            }

            versions.Add(version);
            _active[key] = version.ContentHash;
            _provenance[(reference.TenantId, commit.Provenance.AssertionId)] = commit.Provenance;
            _audit.Add(audit.WithSequence(_audit.Count + 1L));

            return ValueTask.FromResult(new GovernedCommitResult(
                GovernedCommitOutcome.Committed, "committed", reference, version.ContentHash));
        }

        GovernedCommitResult Denied(string code)
            => new(GovernedCommitOutcome.Denied, code, null, null);
    }

    private static bool PermissionEquivalent(PermissionEnvelope left, PermissionEnvelope right)
        => PermissionEnvelopeService.IsNarrowerThanOrEqual(left, right) &&
           PermissionEnvelopeService.IsNarrowerThanOrEqual(right, left);

    private static bool ReceiptTargetsVersion(ConstitutionCommitReceipt receipt, KnowledgeVersion version)
    {
        var operation = receipt.Operation;
        var target = operation.Target;
        var reference = version.Reference;
        return operation.Kind == CognitiveOperationKind.PromoteKnowledge &&
               operation.TenantId == reference.TenantId &&
               operation.PayloadHash == version.ContentHash &&
               target is not null &&
               target.TenantId == reference.TenantId &&
               target.Namespace == reference.Namespace &&
               target.ArtifactKind == reference.Kind.ToString() &&
               target.ArtifactId == reference.ArtifactId &&
               target.Version == reference.Version;
    }

    public ValueTask<GovernedKnowledgeSnapshot> ReadAsync(
        string tenantId,
        string @namespace,
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = (tenantId ?? string.Empty, @namespace, artifactId);
            var versions = _versions.TryGetValue(key, out var found) ? found.ToArray() : [];
            KnowledgeAsset? asset = null;
            KnowledgeVersion? active = null;
            if (versions.Length > 0 && _active.TryGetValue(key, out var activeHash))
            {
                asset = KnowledgeCanonicalizer.PublishAsset(versions, activeHash);
                active = versions.Single(value => value.ContentHash == activeHash);
            }
            var provenance = _provenance.Values
                .Where(value => value.TenantId == key.Item1 &&
                                value.Target.Namespace == @namespace &&
                                value.Target.ArtifactId == artifactId)
                .OrderBy(value => value.RecordedAt)
                .ThenBy(value => value.AssertionId, StringComparer.Ordinal)
                .ToArray();
            var audit = _audit.Where(value => value.TenantId == key.Item1).OrderBy(value => value.Sequence).ToArray();
            return ValueTask.FromResult(new GovernedKnowledgeSnapshot(asset, active, provenance, audit));
        }
    }
}
