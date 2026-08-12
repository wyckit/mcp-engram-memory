using McpEngramMemory.Core.Models.Learning;

namespace McpEngramMemory.Core.Services.Learning;

/// <summary>Runs deterministic checks first, followed by model critics and human approval.</summary>
public sealed class VerifierPlanner : IVerifierPlanner
{
    private readonly TimeProvider _timeProvider;

    public VerifierPlanner(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    public async ValueTask<VerificationTrace> VerifyAsync(
        KnowledgeProposal proposal,
        IEnumerable<ILearningVerifier> verifiers,
        LearningExecutionBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(verifiers);
        ArgumentNullException.ThrowIfNull(budget);
        budget.ThrowIfUnavailable(_timeProvider.GetUtcNow());

        var ordered = verifiers
            .OrderBy(verifier => verifier.Identity.Kind)
            .ThenBy(verifier => verifier.Identity.VerifierId, StringComparer.Ordinal)
            .ToArray();
        var runs = new List<VerificationRun>();

        if (ordered.Length > budget.MaxVerifierRuns)
        {
            runs.Add(SystemRun(VerificationStatus.Error, "verifier-budget-insufficient",
                "Budget cannot execute the complete verifier plan."));
            return new VerificationTrace(proposal.ProposalId, runs);
        }

        int sequence = 0;
        foreach (var verifier in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ThrowIfUnavailable(_timeProvider.GetUtcNow());

            if (verifier.Identity.Kind == VerifierKind.Model && !budget.AllowModelVerifiers)
            {
                runs.Add(new VerificationRun(
                    ++sequence,
                    verifier.Identity,
                    VerificationStatus.Skipped,
                    IsIndependent(proposal.Generator, verifier.Identity),
                    new[] { new VerificationFinding("model-verifier-disabled", "Model verification is disabled by budget policy.") },
                    _timeProvider.GetUtcNow(),
                    _timeProvider.GetUtcNow()));
                continue;
            }

            var startedAt = _timeProvider.GetUtcNow();
            VerificationStatus status;
            IReadOnlyList<VerificationFinding> findings;
            try
            {
                (status, findings) = await verifier.VerifyAsync(proposal, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                status = VerificationStatus.Error;
                findings = new[]
                {
                    new VerificationFinding(
                        "verifier-failed-closed",
                        $"Verifier failed closed: {exception.GetType().Name}.")
                };
            }

            runs.Add(new VerificationRun(
                ++sequence,
                verifier.Identity,
                status,
                IsIndependent(proposal.Generator, verifier.Identity),
                findings,
                startedAt,
                _timeProvider.GetUtcNow()));

            // A later model or approval can never override a deterministic veto.
            if (verifier.Identity.Kind == VerifierKind.Deterministic &&
                status is VerificationStatus.Failed or VerificationStatus.Error)
            {
                break;
            }
        }

        return new VerificationTrace(proposal.ProposalId, runs);
    }

    public static bool IsIndependent(GenerationIdentity teacher, VerifierIdentity verifier)
    {
        if (verifier.Kind != VerifierKind.Model)
            return true;
        return !(string.Equals(teacher.ModelId, verifier.ModelId, StringComparison.Ordinal) &&
                 string.Equals(teacher.PromptFamily, verifier.PromptFamily, StringComparison.Ordinal) &&
                 string.Equals(teacher.EvidenceViewId, verifier.EvidenceViewId, StringComparison.Ordinal));
    }

    private VerificationRun SystemRun(VerificationStatus status, string code, string message)
    {
        var now = _timeProvider.GetUtcNow();
        return new VerificationRun(
            1,
            new VerifierIdentity("engram.verifier-planner", "1", VerifierKind.Deterministic),
            status,
            true,
            new[] { new VerificationFinding(code, message) },
            now,
            now);
    }
}
