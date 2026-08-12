using McpEngramMemory.Core.Models.Constitution;

namespace McpEngramMemory.Core.Services.Constitution;

/// <summary>
/// Built-in model-free root rule. It makes the universal audit/tenant boundary executable;
/// domain-specific promotion, provenance, permission, and verifier invariants remain enforced by
/// their typed services at the narrower transaction boundary where the necessary data exists.
/// </summary>
public sealed class AuditEnvelopeConstitutionRule : IConstitutionRule
{
    private static readonly IReadOnlySet<CognitiveOperationKind> Operations =
        Enum.GetValues<CognitiveOperationKind>().ToHashSet();

    public string RuleId => RootConstitution.AuditEnvelopeRuleId;
    public int Priority => -10_000;
    public IReadOnlySet<CognitiveOperationKind> AppliesTo => Operations;

    public ValueTask<IReadOnlyList<ConstitutionFinding>> EvaluateAsync(
        ConstitutionEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = context.Operation;
        var findings = new List<ConstitutionFinding>();
        if (operation.PayloadHash.Length != 64 || !operation.PayloadHash.All(Uri.IsHexDigit))
        {
            findings.Add(new ConstitutionFinding(RuleId, "invalid-payload-digest",
                ConstitutionOutcome.Deny, "The operation payload must have a canonical SHA-256 digest."));
        }

        var crossTenant = operation.Inputs.Any(value => value.TenantId != operation.TenantId) ||
                          operation.Target is { } target && target.TenantId != operation.TenantId;
        if (crossTenant)
        {
            findings.Add(new ConstitutionFinding(RuleId, "cross-tenant-artifact-reference",
                ConstitutionOutcome.Deny,
                "A governed operation cannot reference artifacts outside its tenant partition."));
        }

        return ValueTask.FromResult<IReadOnlyList<ConstitutionFinding>>(findings);
    }
}
