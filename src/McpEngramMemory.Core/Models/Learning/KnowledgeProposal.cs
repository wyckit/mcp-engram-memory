using System.Collections.ObjectModel;
using System.Collections.Frozen;
using McpEngramMemory.Core.Models.Constitution;

namespace McpEngramMemory.Core.Models.Learning;

public enum LearningProposalStatus
{
    Quarantined
}

public enum KnowledgeHypothesisType
{
    Observation,
    Generalization,
    CausalClaim,
    Procedure,
    Correction
}

public enum KnowledgeCapability
{
    Read,
    Search,
    Use,
    Train,
    Modify,
    Promote,
    Verify,
    Declassify,
    Administer
}

/// <summary>Identity needed to audit generation and assess verifier independence.</summary>
public sealed record GenerationIdentity(
    string ModelId,
    string RuntimeVersion,
    string PromptFamily,
    string PromptVersion,
    string EvidenceViewId);

public sealed record KnowledgeValidityInterval(DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil)
{
    public KnowledgeValidityInterval Validate()
    {
        if (ValidFrom is not null && ValidUntil is not null && ValidUntil < ValidFrom)
            throw new ArgumentException("Validity end cannot precede validity start.");
        return this;
    }
}

/// <summary>
/// Immutable Teacher output. It is always quarantined and cannot represent established knowledge.
/// </summary>
public sealed class KnowledgeProposal
{
    public string ProposalId { get; }
    public string TenantId { get; }
    public string Claim { get; }
    public KnowledgeHypothesisType HypothesisType { get; }
    public IReadOnlyList<OperationArtifactReference> SupportingEvidence { get; }
    public IReadOnlyList<OperationArtifactReference> ContradictingEvidence { get; }
    public GenerationIdentity Generator { get; }
    public double Uncertainty { get; }
    public IReadOnlyList<string> KnownGaps { get; }
    public KnowledgeValidityInterval Validity { get; }
    public string ConstitutionVersionHash { get; }
    public IReadOnlySet<KnowledgeCapability> InheritedCapabilities { get; }
    public IReadOnlySet<KnowledgeCapability> RequestedCapabilities { get; }
    public DateTimeOffset CreatedAt { get; }
    public LearningProposalStatus Status => LearningProposalStatus.Quarantined;

    public KnowledgeProposal(
        string proposalId,
        string tenantId,
        string claim,
        KnowledgeHypothesisType hypothesisType,
        IEnumerable<OperationArtifactReference> supportingEvidence,
        IEnumerable<OperationArtifactReference>? contradictingEvidence,
        GenerationIdentity generator,
        double uncertainty,
        IEnumerable<string>? knownGaps,
        KnowledgeValidityInterval validity,
        string constitutionVersionHash,
        IEnumerable<KnowledgeCapability> inheritedCapabilities,
        IEnumerable<KnowledgeCapability>? requestedCapabilities,
        DateTimeOffset createdAt)
    {
        if (uncertainty is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(uncertainty), "Uncertainty must be between zero and one.");

        ProposalId = Required(proposalId, nameof(proposalId));
        TenantId = Required(tenantId, nameof(tenantId));
        Claim = Required(claim, nameof(claim));
        HypothesisType = hypothesisType;
        SupportingEvidence = ReadOnlyArtifacts(supportingEvidence);
        ContradictingEvidence = ReadOnlyArtifacts(contradictingEvidence ?? []);
        Generator = generator ?? throw new ArgumentNullException(nameof(generator));
        Uncertainty = uncertainty;
        KnownGaps = new ReadOnlyCollection<string>((knownGaps ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray());
        Validity = (validity ?? throw new ArgumentNullException(nameof(validity))).Validate();
        ConstitutionVersionHash = Required(constitutionVersionHash, nameof(constitutionVersionHash)).ToLowerInvariant();
        InheritedCapabilities = inheritedCapabilities.ToFrozenSet();
        RequestedCapabilities = (requestedCapabilities ?? InheritedCapabilities).ToFrozenSet();
        CreatedAt = createdAt;
    }

    public IEnumerable<OperationArtifactReference> AllEvidence
        => SupportingEvidence.Concat(ContradictingEvidence);

    public static string EvidenceKey(OperationArtifactReference reference)
        => string.Join("|", reference.TenantId, reference.Namespace, reference.ArtifactKind, reference.ArtifactId);

    private static IReadOnlyList<OperationArtifactReference> ReadOnlyArtifacts(
        IEnumerable<OperationArtifactReference> values)
        => new ReadOnlyCollection<OperationArtifactReference>(values
            .OrderBy(EvidenceKey, StringComparer.Ordinal)
            .ThenBy(value => value.Version, StringComparer.Ordinal)
            .ToArray());

    private static string Required(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty.", parameterName)
            : value.Trim();
}

/// <summary>Mutable-at-the-boundary generator response sealed by TeacherRuntime into a proposal.</summary>
public sealed record KnowledgeProposalDraft(
    string Claim,
    KnowledgeHypothesisType HypothesisType,
    IReadOnlyList<OperationArtifactReference> SupportingEvidence,
    IReadOnlyList<OperationArtifactReference> ContradictingEvidence,
    double Uncertainty,
    IReadOnlyList<string> KnownGaps,
    KnowledgeValidityInterval Validity,
    IReadOnlySet<KnowledgeCapability> RequestedCapabilities);

public sealed record TeacherRequest(
    string ProposalId,
    string TenantId,
    string Purpose,
    IReadOnlyList<OperationArtifactReference> AuthorizedInputs,
    GenerationIdentity Generator,
    string ConstitutionVersionHash,
    IReadOnlySet<KnowledgeCapability> InheritedCapabilities,
    DateTimeOffset RequestedAt);

/// <summary>Shared bounded-execution contract for Teacher and Verifier work.</summary>
public sealed record LearningExecutionBudget(
    int MaxGeneratorCalls,
    int MaxVerifierRuns,
    DateTimeOffset Deadline,
    bool AllowModelVerifiers = true)
{
    public void ThrowIfUnavailable(DateTimeOffset now)
    {
        if (MaxGeneratorCalls < 0 || MaxVerifierRuns < 0)
            throw new ArgumentOutOfRangeException(nameof(LearningExecutionBudget), "Budget counts cannot be negative.");
        if (now > Deadline)
            throw new LearningBudgetExceededException("The learning deadline has elapsed.");
    }
}

public sealed class LearningBudgetExceededException : InvalidOperationException
{
    public LearningBudgetExceededException(string message) : base(message) { }
}
