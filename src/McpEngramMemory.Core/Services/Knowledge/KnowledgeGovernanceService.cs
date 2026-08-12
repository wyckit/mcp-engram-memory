using McpEngramMemory.Core.Models.Knowledge;

namespace McpEngramMemory.Core.Services.Knowledge;

/// <summary>Model-free promotion and declassification rules for versioned knowledge.</summary>
public static class KnowledgeGovernanceService
{
    public static KnowledgeVersion Promote(
        KnowledgeVersion current,
        ArtifactRef nextReference,
        KnowledgeMaturity targetMaturity,
        IEnumerable<EvidenceReference>? supportingEvidence,
        IEnumerable<EvidenceReference>? contradictingEvidence,
        BitemporalValidity temporal,
        EpistemicProfile epistemic,
        string constitutionVersionHash)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(nextReference);
        EnsureSameAsset(current.Reference, nextReference);

        if ((int)targetMaturity != (int)current.Maturity + 1)
            throw new InvalidOperationException("Knowledge promotion must advance exactly one maturity state.");

        var support = current.SupportingEvidence
            .Concat(supportingEvidence ?? Array.Empty<EvidenceReference>())
            .ToArray();
        if (support.Length == 0)
            throw new InvalidOperationException("Knowledge promotion requires supporting evidence; memory lifecycle and salience are not evidence.");
        if (support.Any(evidence => !evidence.IsStable))
            throw new InvalidOperationException("Every supporting artifact must have a stable exact content hash.");

        var contradictions = current.ContradictingEvidence
            .Concat(contradictingEvidence ?? Array.Empty<EvidenceReference>())
            .ToArray();
        if (contradictions.Any(evidence => !evidence.IsStable))
            throw new InvalidOperationException("Every contradicting artifact must have a stable exact content hash.");

        // Including the prior effective envelope makes permission evolution monotone even when a
        // later promotion happens to cite fewer sources than an earlier version.
        var permissions = PermissionEnvelopeService.Intersect(
            new[] { current.Permissions }.Concat(support.Select(evidence => evidence.Permissions)));

        var definition = new KnowledgeVersionDefinition(
            nextReference,
            current.Definition.Claim,
            targetMaturity,
            current.Status,
            temporal,
            epistemic,
            support,
            contradictions,
            permissions,
            constitutionVersionHash,
            current.Definition.DerivationBranchId);
        return KnowledgeCanonicalizer.PublishVersion(definition);
    }

    public static DeclassificationBranch CreateDeclassificationBranch(
        KnowledgeVersion original,
        DeclassificationProposal proposal,
        DeclassificationApproval approval,
        string sanitizedClaim,
        BitemporalValidity temporal,
        string constitutionVersionHash)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(approval);

        if (proposal.Source != original.Reference)
            throw new InvalidOperationException("The proposal does not name the exact source version.");
        if (!string.Equals(proposal.ProposalId, approval.ProposalId, StringComparison.Ordinal))
            throw new InvalidOperationException("The approval does not belong to this proposal.");
        if (!approval.AuthorizationSnapshot.Allows(ArtifactCapability.Declassify, approval.Approver))
            throw new UnauthorizedAccessException("The approver was not authorized to declassify this artifact.");
        if (proposal.SanitizationEvidence.Count == 0 || proposal.SanitizationEvidence.Any(value => !value.IsStable))
            throw new InvalidOperationException("Declassification requires stable redaction or sanitization evidence.");
        if (proposal.LeakageChecks.Count == 0 || proposal.LeakageChecks.Any(check => !check.Passed))
            throw new InvalidOperationException("Every required deterministic leakage check must pass.");
        if (PermissionEnvelopeService.IsNarrowerThanOrEqual(proposal.ProposedPermissions, original.Permissions))
            throw new InvalidOperationException("A declassification branch must explicitly broaden at least one permission.");
        if (proposal.ProposedBranch.Kind != ArtifactKind.Knowledge)
            throw new InvalidOperationException("A declassified knowledge branch must publish a knowledge artifact.");

        var definition = new KnowledgeVersionDefinition(
            proposal.ProposedBranch,
            sanitizedClaim,
            original.Maturity,
            original.Status,
            temporal,
            original.Definition.Epistemic,
            original.SupportingEvidence.Concat(proposal.SanitizationEvidence),
            original.ContradictingEvidence,
            proposal.ProposedPermissions,
            constitutionVersionHash,
            proposal.ProposalId);
        var released = KnowledgeCanonicalizer.PublishVersion(definition);

        return new DeclassificationBranch(
            original,
            released,
            proposal,
            approval,
            original.ContentHash);
    }

    private static void EnsureSameAsset(ArtifactRef current, ArtifactRef next)
    {
        if (next.Kind != ArtifactKind.Knowledge ||
            current.TenantId != next.TenantId ||
            current.Namespace != next.Namespace ||
            current.ArtifactId != next.ArtifactId ||
            current.Version == next.Version)
            throw new ArgumentException("The next reference must be a distinct version of the same knowledge asset.", nameof(next));
    }
}
