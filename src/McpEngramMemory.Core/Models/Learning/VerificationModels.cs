using System.Collections.ObjectModel;
using McpEngramMemory.Core.Models.Constitution;

namespace McpEngramMemory.Core.Models.Learning;

public enum VerifierKind
{
    Deterministic,
    Model,
    HumanApproval
}

public enum VerificationStatus
{
    Passed,
    Failed,
    RequireApproval,
    Error,
    Skipped
}

public sealed record VerifierIdentity(
    string VerifierId,
    string Version,
    VerifierKind Kind,
    string? ModelId = null,
    string? PromptFamily = null,
    string? EvidenceViewId = null);

public sealed record VerificationFinding(string Code, string Message);

public sealed class VerificationRun
{
    public int Sequence { get; }
    public VerifierIdentity Verifier { get; }
    public VerificationStatus Status { get; }
    public bool IsIndependentFromTeacher { get; }
    public IReadOnlyList<VerificationFinding> Findings { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset CompletedAt { get; }

    public VerificationRun(
        int sequence,
        VerifierIdentity verifier,
        VerificationStatus status,
        bool isIndependentFromTeacher,
        IEnumerable<VerificationFinding>? findings,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        Sequence = sequence;
        Verifier = verifier;
        Status = status;
        IsIndependentFromTeacher = isIndependentFromTeacher;
        Findings = new ReadOnlyCollection<VerificationFinding>((findings ?? []).ToArray());
        StartedAt = startedAt;
        CompletedAt = completedAt;
    }
}

public sealed class VerificationTrace
{
    public string ProposalId { get; }
    public VerificationStatus Status { get; }
    public IReadOnlyList<VerificationRun> Runs { get; }
    public bool DeterministicChecksPassed { get; }
    public bool HumanApproved { get; }
    public bool HasIndependentModelPass { get; }

    public VerificationTrace(string proposalId, IEnumerable<VerificationRun> runs)
    {
        ProposalId = proposalId;
        Runs = new ReadOnlyCollection<VerificationRun>(runs.OrderBy(run => run.Sequence).ToArray());
        DeterministicChecksPassed = Runs.Any(run => run.Verifier.Kind == VerifierKind.Deterministic) &&
            Runs.Where(run => run.Verifier.Kind == VerifierKind.Deterministic)
                .All(run => run.Status == VerificationStatus.Passed);
        HumanApproved = Runs.Any(run => run.Verifier.Kind == VerifierKind.HumanApproval &&
                                        run.Status == VerificationStatus.Passed);
        HasIndependentModelPass = Runs.Any(run => run.Verifier.Kind == VerifierKind.Model &&
                                                  run.IsIndependentFromTeacher &&
                                                  run.Status == VerificationStatus.Passed);
        Status = ComputeStatus(Runs);
    }

    private static VerificationStatus ComputeStatus(IEnumerable<VerificationRun> runs)
    {
        var statuses = runs.Select(run => run.Status).ToArray();
        if (statuses.Contains(VerificationStatus.Failed) || statuses.Contains(VerificationStatus.Error))
            return VerificationStatus.Failed;
        if (statuses.Contains(VerificationStatus.RequireApproval))
            return VerificationStatus.RequireApproval;
        return statuses.Length > 0 && statuses.All(status => status is VerificationStatus.Passed or VerificationStatus.Skipped)
            ? VerificationStatus.Passed
            : VerificationStatus.Failed;
    }
}

public interface ILearningVerifier
{
    VerifierIdentity Identity { get; }

    ValueTask<(VerificationStatus Status, IReadOnlyList<VerificationFinding> Findings)> VerifyAsync(
        KnowledgeProposal proposal,
        CancellationToken cancellationToken = default);
}

public interface IVerifierPlanner
{
    ValueTask<VerificationTrace> VerifyAsync(
        KnowledgeProposal proposal,
        IEnumerable<ILearningVerifier> verifiers,
        LearningExecutionBudget budget,
        CancellationToken cancellationToken = default);
}

public enum PromotionOutcome
{
    Promoted,
    Denied,
    Quarantined,
    RequireApproval
}

public sealed record PromotionFinding(string Code, string Message);

public sealed class PromotionResult
{
    public PromotionOutcome Outcome { get; }
    public IReadOnlyList<PromotionFinding> Findings { get; }
    public VerificationTrace VerificationTrace { get; }
    public ConstitutionDecision ConstitutionDecision { get; }

    public PromotionResult(
        PromotionOutcome outcome,
        IEnumerable<PromotionFinding> findings,
        VerificationTrace verificationTrace,
        ConstitutionDecision constitutionDecision)
    {
        Outcome = outcome;
        Findings = new ReadOnlyCollection<PromotionFinding>(findings.ToArray());
        VerificationTrace = verificationTrace;
        ConstitutionDecision = constitutionDecision;
    }
}

public sealed record PromotionRequest(
    KnowledgeProposal Proposal,
    VerificationTrace Verification,
    ConstitutionDecision ConstitutionDecision,
    IReadOnlyDictionary<string, string> CurrentEvidenceVersions);
