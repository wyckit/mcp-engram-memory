using McpEngramMemory.Core.Models.Constitution;

namespace McpEngramMemory.Core.Services.Constitution;

public interface IConstitutionProvider
{
    ConstitutionBundle Current { get; }
    ConstitutionBundle PublishOverlay(ConstitutionVersion overlay);
}

/// <summary>Thread-safe immutable-version provider. Published versions are never mutated.</summary>
public sealed class InMemoryConstitutionProvider : IConstitutionProvider
{
    private readonly object _gate = new();
    private readonly ConstitutionVersion _root;
    private readonly List<ConstitutionVersion> _overlays = new();
    private ConstitutionBundle _current;

    public InMemoryConstitutionProvider(ConstitutionVersion? root = null)
    {
        _root = root ?? RootConstitution.Version;
        _current = ConstitutionComposer.Compose(_root);
    }

    public ConstitutionBundle Current
    {
        get { lock (_gate) return _current; }
    }

    public ConstitutionBundle PublishOverlay(ConstitutionVersion overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        lock (_gate)
        {
            var duplicate = _overlays.FirstOrDefault(item =>
                item.Definition.ConstitutionId == overlay.Definition.ConstitutionId &&
                item.Version == overlay.Version);
            if (duplicate is not null)
            {
                if (duplicate.ContentHash != overlay.ContentHash)
                    throw new ConstitutionCompositionException("A published Constitution identity is immutable.");
                return _current;
            }

            var proposed = ConstitutionComposer.Compose(_root, _overlays.Append(overlay));
            _overlays.Add(overlay);
            _current = proposed;
            return proposed;
        }
    }
}

/// <summary>
/// Common governed-operation boundary. Every decision, including denial and evaluator failure,
/// is converted to an append-only audit event before it is returned.
/// </summary>
public sealed class ConstitutionKernel
{
    private readonly IConstitutionProvider _provider;
    private readonly IConstitutionEvaluator _evaluator;
    private readonly IConstitutionAuditStore _audit;
    private readonly TimeProvider _timeProvider;

    public ConstitutionKernel(
        IConstitutionProvider provider,
        IConstitutionEvaluator evaluator,
        IConstitutionAuditStore audit,
        TimeProvider? timeProvider = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ConstitutionDecision> EvaluateAndAuditAsync(
        OperationEnvelope operation,
        ConstitutionPhase phase,
        CancellationToken cancellationToken = default)
        => (await EvaluateAndAppendAsync(operation, phase, cancellationToken).ConfigureAwait(false)).Decision;

    /// <summary>Issues an unforgeable-in-API receipt only for an allowed, durably audited commit.</summary>
    public async ValueTask<ConstitutionCommitReceipt> AuthorizeCommitAsync(
        OperationEnvelope operation,
        CancellationToken cancellationToken = default)
    {
        var evaluated = await EvaluateAndAppendAsync(
            operation, ConstitutionPhase.Commit, cancellationToken).ConfigureAwait(false);
        if (evaluated.Decision.Outcome != ConstitutionOutcome.Allow)
            throw new InvalidOperationException("The Constitution did not authorize this commit.");
        return new ConstitutionCommitReceipt(operation, evaluated.Decision, evaluated.Audit);
    }

    private async ValueTask<(ConstitutionDecision Decision, ConstitutionAuditRecord Audit)> EvaluateAndAppendAsync(
        OperationEnvelope operation,
        ConstitutionPhase phase,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ConstitutionDecision decision;
        try
        {
            decision = await _evaluator.EvaluateAsync(
                operation, _provider.Current, phase, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var current = _provider.Current;
            decision = new ConstitutionDecision(operation.OperationId, phase, ConstitutionOutcome.Deny,
                new[]
                {
                    new ConstitutionFinding("constitution.kernel", "evaluation-failed-closed",
                        ConstitutionOutcome.Deny,
                        $"Constitution evaluation failed closed: {exception.GetType().Name}.")
                }, current.VersionHashes);
        }

        var audit = await _audit.AppendAsync(new ConstitutionAuditRecord(
            0,
            $"{operation.OperationId}:{phase}:{Guid.NewGuid():N}",
            operation.OperationId,
            operation.TenantId,
            operation.PrincipalId,
            phase,
            decision.Outcome,
            decision.ConstitutionVersionHashes,
            decision.Findings.Select(finding => finding.Code),
            _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        return (decision, audit);
    }
}
