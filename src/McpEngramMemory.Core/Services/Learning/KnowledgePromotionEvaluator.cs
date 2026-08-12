using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Models.Learning;

namespace McpEngramMemory.Core.Services.Learning;

/// <summary>
/// Pure, fail-closed promotion gate. It returns an audit-ready result and never mutates memory,
/// knowledge stores, active-version pointers, or the proposal.
/// </summary>
public sealed class KnowledgePromotionEvaluator
{
    public PromotionResult Evaluate(PromotionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var proposal = request.Proposal;
        var trace = request.Verification;
        var decision = request.ConstitutionDecision;
        var findings = new List<PromotionFinding>();
        var currentEvidenceVersions = request.CurrentEvidenceVersions
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        if (proposal.SupportingEvidence.Count == 0)
            findings.Add(new PromotionFinding("supporting-evidence-required", "Knowledge promotion requires supporting evidence."));

        foreach (var evidence in proposal.AllEvidence)
        {
            string key = KnowledgeProposal.EvidenceKey(evidence);
            if (!currentEvidenceVersions.TryGetValue(key, out var currentVersion) ||
                !string.Equals(currentVersion, evidence.Version, StringComparison.Ordinal))
            {
                findings.Add(new PromotionFinding(
                    "evidence-version-changed",
                    $"Evidence '{key}' is missing or no longer at evaluated version '{evidence.Version}'."));
            }
        }

        if (decision.ConstitutionVersionHashes.Count == 0 ||
            !string.Equals(
                decision.ConstitutionVersionHashes[^1],
                proposal.ConstitutionVersionHash,
                StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new PromotionFinding(
                "constitution-version-changed",
                "The promotion decision was not evaluated under the proposal's exact Constitution version."));
        }

        if (!trace.DeterministicChecksPassed)
            findings.Add(new PromotionFinding("deterministic-verification-required", "Deterministic verification did not pass."));
        if (trace.Status == VerificationStatus.Failed)
            findings.Add(new PromotionFinding("verification-failed", "The verifier trace contains a veto or failure."));

        if (!proposal.RequestedCapabilities.IsSubsetOf(proposal.InheritedCapabilities))
            findings.Add(new PromotionFinding(
                "permission-broadening-forbidden",
                "Promoted knowledge cannot request capabilities broader than its supporting evidence."));

        if (decision.Outcome == ConstitutionOutcome.Deny)
            findings.Add(new PromotionFinding("constitution-denied", "The Constitution denied promotion."));
        if (decision.Outcome == ConstitutionOutcome.Quarantine)
            return Result(PromotionOutcome.Quarantined, "constitution-quarantined",
                "The Constitution requires the proposal to remain quarantined.");

        if (findings.Count > 0)
            return new PromotionResult(PromotionOutcome.Denied, findings, trace, decision);

        bool approvalRequired = decision.Outcome == ConstitutionOutcome.RequireApproval ||
                                trace.Status == VerificationStatus.RequireApproval;
        if (approvalRequired && !trace.HumanApproved)
            return Result(PromotionOutcome.RequireApproval, "human-approval-required",
                "An authorized human approval must be recorded before promotion.");

        return new PromotionResult(PromotionOutcome.Promoted, Array.Empty<PromotionFinding>(), trace, decision);

        PromotionResult Result(PromotionOutcome outcome, string code, string message)
            => new(outcome, new[] { new PromotionFinding(code, message) }, trace, decision);
    }
}
