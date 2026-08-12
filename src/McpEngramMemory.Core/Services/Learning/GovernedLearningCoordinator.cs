using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Models.Governance;
using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Models.Learning;
using McpEngramMemory.Core.Models.Provenance;
using McpEngramMemory.Core.Services.Constitution;
using McpEngramMemory.Core.Services.Governance;

namespace McpEngramMemory.Core.Services.Learning;

public sealed record GovernedLearningMaterialization(
    KnowledgeVersion Version,
    Func<ConstitutionCommitReceipt, ProvenanceAssertion> CreateProvenance,
    IReadOnlyDictionary<ArtifactRef, PermissionEnvelope> SourcePermissionSnapshots,
    string? ExpectedActiveVersionHash);

public sealed record GovernedLearningRequest(
    TeacherRequest TeacherRequest,
    string PrincipalId,
    LearningExecutionBudget Budget,
    IReadOnlyList<ILearningVerifier> Verifiers,
    Func<KnowledgeProposal, GovernedLearningMaterialization> Materialize,
    Func<CommitAuthorityState> ResolveCurrentAuthority);

public sealed record GovernedLearningResult(
    KnowledgeProposal Proposal,
    VerificationTrace Verification,
    PromotionResult Promotion,
    GovernedCommitResult? Commit);

/// <summary>
/// The single production publication path: Teacher output remains quarantined, deterministic
/// verification runs first, the kernel issues an opaque commit receipt, and the governed store
/// atomically publishes knowledge, provenance, audit, and the active pointer.
/// </summary>
public sealed class GovernedLearningCoordinator
{
    private readonly ITeacherRuntime _teacher;
    private readonly IVerifierPlanner _verifiers;
    private readonly KnowledgePromotionEvaluator _promotion;
    private readonly ConstitutionKernel _constitution;
    private readonly IGovernedKnowledgeStore _store;

    public GovernedLearningCoordinator(
        ITeacherRuntime teacher,
        IVerifierPlanner verifiers,
        KnowledgePromotionEvaluator promotion,
        ConstitutionKernel constitution,
        IGovernedKnowledgeStore store)
    {
        _teacher = teacher;
        _verifiers = verifiers;
        _promotion = promotion;
        _constitution = constitution;
        _store = store;
    }

    public async ValueTask<GovernedLearningResult> ExecuteAsync(
        GovernedLearningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var proposal = await _teacher.ProposeAsync(
            request.TeacherRequest, request.Budget, cancellationToken).ConfigureAwait(false);
        var verification = await _verifiers.VerifyAsync(
            proposal, request.Verifiers, request.Budget, cancellationToken).ConfigureAwait(false);
        var materialized = request.Materialize(proposal);
        var reference = materialized.Version.Reference;
        var operation = new OperationEnvelope(
            $"promote:{proposal.ProposalId}:{reference.ArtifactId}:{reference.Version}",
            CognitiveOperationKind.PromoteKnowledge,
            reference.TenantId,
            string.IsNullOrWhiteSpace(request.PrincipalId)
                ? throw new ArgumentException("An authenticated principal is required.", nameof(request))
                : request.PrincipalId.Trim(),
            request.TeacherRequest.Purpose,
            proposal.AllEvidence,
            new OperationArtifactReference(reference.TenantId, reference.Namespace,
                reference.Kind.ToString(), reference.ArtifactId, reference.Version),
            materialized.Version.ContentHash,
            request.TeacherRequest.RequestedAt,
            new Dictionary<string, string> { ["proposalId"] = proposal.ProposalId });
        var receipt = await _constitution.AuthorizeCommitAsync(operation, cancellationToken)
            .ConfigureAwait(false);
        var provenance = materialized.CreateProvenance(receipt);

        var authority = request.ResolveCurrentAuthority();
        var promotion = _promotion.Evaluate(new PromotionRequest(
            proposal, verification, receipt.Decision, authority.ResourceVersions));
        if (promotion.Outcome != PromotionOutcome.Promoted)
            return new GovernedLearningResult(proposal, verification, promotion, null);

        var commit = new GovernedPromotionCommit(
            materialized.Version,
            materialized.ExpectedActiveVersionHash,
            promotion,
            provenance,
            receipt,
            new CommitAuthorizationSnapshot(
                receipt.Decision.ConstitutionVersionHashes[^1], authority.ResourceVersions),
            materialized.SourcePermissionSnapshots);
        var committed = await _store.CommitPromotionAsync(
            commit, request.ResolveCurrentAuthority, cancellationToken).ConfigureAwait(false);
        return new GovernedLearningResult(proposal, verification, promotion, committed);
    }
}
