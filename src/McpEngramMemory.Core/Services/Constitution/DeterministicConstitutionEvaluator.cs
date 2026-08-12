using McpEngramMemory.Core.Models.Constitution;

namespace McpEngramMemory.Core.Services.Constitution;

/// <summary>
/// Model-free evaluator with stable priority/id ordering and fail-closed handling of missing,
/// mismatched, or failed deterministic rule implementations.
/// </summary>
public sealed class DeterministicConstitutionEvaluator : IConstitutionEvaluator
{
    private readonly IReadOnlyDictionary<string, IConstitutionRule> _rules;

    public DeterministicConstitutionEvaluator(IEnumerable<IConstitutionRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var dictionary = new Dictionary<string, IConstitutionRule>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (!dictionary.TryAdd(rule.RuleId, rule))
                throw new ArgumentException($"Rule implementation '{rule.RuleId}' occurs more than once.", nameof(rules));
        }
        _rules = dictionary;
    }

    public async ValueTask<ConstitutionDecision> EvaluateAsync(
        OperationEnvelope operation,
        ConstitutionBundle constitution,
        ConstitutionPhase phase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(constitution);

        var context = new ConstitutionEvaluationContext(operation, constitution, phase);
        var orderedFindings = new List<(int Priority, ConstitutionFinding Finding)>();

        if (!constitution.EffectiveConstraints.AllowedOperations.Contains(operation.Kind))
        {
            orderedFindings.Add((int.MinValue, new ConstitutionFinding(
                "root.allowed-operations",
                "operation-not-allowed",
                ConstitutionOutcome.Deny,
                $"Operation '{operation.Kind}' is not allowed by the effective Constitution.")));
        }

        foreach (var definition in constitution.Rules
                     .Where(rule => rule.AppliesTo.Contains(operation.Kind))
                     .OrderBy(rule => rule.Priority)
                     .ThenBy(rule => rule.RuleId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_rules.TryGetValue(definition.RuleId, out var implementation))
            {
                orderedFindings.Add((definition.Priority, ConfigurationFinding(
                    definition.RuleId, "rule-implementation-missing")));
                continue;
            }

            if (implementation.Priority != definition.Priority ||
                !implementation.AppliesTo.SetEquals(definition.AppliesTo))
            {
                orderedFindings.Add((definition.Priority, ConfigurationFinding(
                    definition.RuleId, "rule-implementation-mismatch")));
                continue;
            }

            try
            {
                var findings = await implementation.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
                foreach (var finding in findings
                             .OrderBy(value => value.Code, StringComparer.Ordinal)
                             .ThenBy(value => value.Message, StringComparer.Ordinal))
                {
                    orderedFindings.Add((definition.Priority, finding with { RuleId = definition.RuleId }));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                orderedFindings.Add((definition.Priority, new ConstitutionFinding(
                    definition.RuleId,
                    "rule-evaluation-failed",
                    ConstitutionOutcome.Deny,
                    $"Deterministic rule failed closed: {exception.GetType().Name}.")));
            }
        }

        var findingsInOrder = orderedFindings
            .OrderBy(value => value.Priority)
            .ThenBy(value => value.Finding.RuleId, StringComparer.Ordinal)
            .ThenBy(value => value.Finding.Code, StringComparer.Ordinal)
            .Select(value => value.Finding)
            .ToArray();
        var outcome = findingsInOrder.Length == 0
            ? ConstitutionOutcome.Allow
            : findingsInOrder.Max(value => value.Outcome);

        return new ConstitutionDecision(
            operation.OperationId,
            phase,
            outcome,
            findingsInOrder,
            constitution.VersionHashes);
    }

    private static ConstitutionFinding ConfigurationFinding(string ruleId, string code)
        => new(
            ruleId,
            code,
            ConstitutionOutcome.Deny,
            $"Constitution rule '{ruleId}' is not backed by its declared deterministic implementation.");
}
