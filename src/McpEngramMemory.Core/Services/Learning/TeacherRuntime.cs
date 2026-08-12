using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Models.Learning;

namespace McpEngramMemory.Core.Services.Learning;

/// <summary>Host-supplied proposal generator; Core does not require a model or network.</summary>
public interface IKnowledgeProposalGenerator
{
    ValueTask<KnowledgeProposalDraft> GenerateAsync(
        TeacherRequest request,
        CancellationToken cancellationToken = default);
}

public interface ITeacherRuntime
{
    ValueTask<KnowledgeProposal> ProposeAsync(
        TeacherRequest request,
        LearningExecutionBudget budget,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bounded Teacher coordinator. It seals generator output into immutable quarantine and never
/// writes memory or established knowledge.
/// </summary>
public sealed class TeacherRuntime : ITeacherRuntime
{
    private readonly IKnowledgeProposalGenerator _generator;
    private readonly TimeProvider _timeProvider;

    public TeacherRuntime(IKnowledgeProposalGenerator generator, TimeProvider? timeProvider = null)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<KnowledgeProposal> ProposeAsync(
        TeacherRequest request,
        LearningExecutionBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();
        budget.ThrowIfUnavailable(_timeProvider.GetUtcNow());
        if (budget.MaxGeneratorCalls < 1)
            throw new LearningBudgetExceededException("Teacher generation budget is exhausted.");

        // Freeze authorization inputs before crossing the asynchronous generator boundary.
        var authorizedInputs = request.AuthorizedInputs.ToArray();
        var inheritedCapabilities = request.InheritedCapabilities.ToArray();
        var draft = await _generator.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        budget.ThrowIfUnavailable(_timeProvider.GetUtcNow());
        ValidateEvidenceAuthorization(authorizedInputs, draft.SupportingEvidence, draft.ContradictingEvidence);

        return new KnowledgeProposal(
            request.ProposalId,
            request.TenantId,
            draft.Claim,
            draft.HypothesisType,
            draft.SupportingEvidence,
            draft.ContradictingEvidence,
            request.Generator,
            draft.Uncertainty,
            draft.KnownGaps,
            draft.Validity,
            request.ConstitutionVersionHash,
            inheritedCapabilities,
            draft.RequestedCapabilities,
            _timeProvider.GetUtcNow());
    }

    private static void ValidateEvidenceAuthorization(
        IEnumerable<OperationArtifactReference> authorized,
        params IReadOnlyList<OperationArtifactReference>[] proposedSets)
    {
        var allowed = authorized.ToHashSet();
        foreach (var reference in proposedSets.SelectMany(value => value))
        {
            if (!allowed.Contains(reference))
                throw new InvalidOperationException(
                    $"Teacher proposed unauthorized evidence '{KnowledgeProposal.EvidenceKey(reference)}'.");
        }
    }
}
