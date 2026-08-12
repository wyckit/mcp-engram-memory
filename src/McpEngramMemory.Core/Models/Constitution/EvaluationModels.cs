using System.Collections.ObjectModel;

namespace McpEngramMemory.Core.Models.Constitution;

public enum ConstitutionPhase
{
    Precondition,
    Postcondition,
    Commit
}

public enum ConstitutionOutcome
{
    Allow,
    RequireApproval,
    Quarantine,
    Deny
}

/// <summary>One deterministic rule result. Higher-severity outcomes dominate lower ones.</summary>
public sealed record ConstitutionFinding(
    string RuleId,
    string Code,
    ConstitutionOutcome Outcome,
    string Message,
    IReadOnlyList<OperationArtifactReference>? Evidence = null);

public sealed record ConstitutionEvaluationContext(
    OperationEnvelope Operation,
    ConstitutionBundle Constitution,
    ConstitutionPhase Phase);

/// <summary>Implemented by deterministic, local rule evaluators; no model or network is implied.</summary>
public interface IConstitutionRule
{
    string RuleId { get; }
    int Priority { get; }
    IReadOnlySet<CognitiveOperationKind> AppliesTo { get; }

    ValueTask<IReadOnlyList<ConstitutionFinding>> EvaluateAsync(
        ConstitutionEvaluationContext context,
        CancellationToken cancellationToken = default);
}

public interface IConstitutionEvaluator
{
    ValueTask<ConstitutionDecision> EvaluateAsync(
        OperationEnvelope operation,
        ConstitutionBundle constitution,
        ConstitutionPhase phase,
        CancellationToken cancellationToken = default);
}

/// <summary>Complete deterministic decision, pinned to every Constitution version in the bundle.</summary>
public sealed class ConstitutionDecision
{
    public string OperationId { get; }
    public ConstitutionPhase Phase { get; }
    public ConstitutionOutcome Outcome { get; }
    public IReadOnlyList<ConstitutionFinding> Findings { get; }
    public IReadOnlyList<string> ConstitutionVersionHashes { get; }

    public ConstitutionDecision(
        string operationId,
        ConstitutionPhase phase,
        ConstitutionOutcome outcome,
        IEnumerable<ConstitutionFinding> findings,
        IEnumerable<string> constitutionVersionHashes)
    {
        OperationId = operationId;
        Phase = phase;
        Outcome = outcome;
        Findings = new ReadOnlyCollection<ConstitutionFinding>(findings.ToArray());
        ConstitutionVersionHashes = new ReadOnlyCollection<string>(constitutionVersionHashes.ToArray());
    }
}
