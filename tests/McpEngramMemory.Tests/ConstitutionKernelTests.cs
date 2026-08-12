using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Services.Constitution;

namespace McpEngramMemory.Tests;

public sealed class ConstitutionKernelTests
{
    [Fact]
    public void BuiltInRootContainsAllNonNegotiablePrinciplesAndStableHash()
    {
        Assert.Equal(8, RootConstitution.Principles.Count);
        Assert.Equal(RootConstitution.Version.ContentHash,
            ConstitutionCanonicalizer.ComputeHash(RootConstitution.Version));
        Assert.True(RootConstitution.Bundle.EffectiveConstraints.PreserveProvenance);
        Assert.True(RootConstitution.Bundle.EffectiveConstraints.RequireEvidenceForKnowledge);
        Assert.Equal(RootConstitution.AuditEnvelopeRuleId,
            Assert.Single(RootConstitution.Version.Definition.Rules).RuleId);
    }

    [Fact]
    public async Task BuiltInRuleRejectsInvalidDigestAndCrossTenantReference()
    {
        var rule = new AuditEnvelopeConstitutionRule();
        var operation = new OperationEnvelope("op", CognitiveOperationKind.PromoteKnowledge,
            "tenant-a", "agent", "test",
            [new OperationArtifactReference("tenant-b", "knowledge", "knowledge", "claim", "1")],
            null, "not-a-digest", DateTimeOffset.UnixEpoch);

        var decision = await new DeterministicConstitutionEvaluator([rule]).EvaluateAsync(
            operation, RootConstitution.Bundle, ConstitutionPhase.Precondition);

        Assert.Equal(ConstitutionOutcome.Deny, decision.Outcome);
        Assert.Contains(decision.Findings, finding => finding.Code == "invalid-payload-digest");
        Assert.Contains(decision.Findings, finding => finding.Code == "cross-tenant-artifact-reference");
    }

    [Fact]
    public void ProviderRejectsPublishedIdentityMutationAndWeakening()
    {
        var provider = new InMemoryConstitutionProvider();
        var constraints = ConstitutionConstraints.RootDefaults;
        var overlay = ConstitutionCanonicalizer.Publish(new ConstitutionDefinition(
            "tenant-policy", "Tenant", ConstitutionLayerKind.Overlay, constraints,
            new[] { "Require two sources." }, Array.Empty<ConstitutionRuleDefinition>(),
            RootConstitution.Version.ContentHash), "1", DateTimeOffset.UnixEpoch);
        provider.PublishOverlay(overlay);
        Assert.Equal(overlay.ContentHash, provider.Current.EffectiveVersionHash);

        var mutated = ConstitutionCanonicalizer.Publish(new ConstitutionDefinition(
            "tenant-policy", "Mutated", ConstitutionLayerKind.Overlay, constraints,
            new[] { "Different." }, Array.Empty<ConstitutionRuleDefinition>(),
            RootConstitution.Version.ContentHash), "1", DateTimeOffset.UnixEpoch);
        Assert.Throws<ConstitutionCompositionException>(() => provider.PublishOverlay(mutated));
    }

    [Fact]
    public async Task KernelAuditsAllowAndFailClosedDenialIncludingLegacyTenant()
    {
        var audit = new InMemoryConstitutionAuditStore();
        var allowKernel = new ConstitutionKernel(new InMemoryConstitutionProvider(),
            new DeterministicConstitutionEvaluator(new IConstitutionRule[]
            {
                new AuditEnvelopeConstitutionRule()
            }), audit);
        var operation = Operation("op-allow", string.Empty);
        var allowed = await allowKernel.EvaluateAndAuditAsync(operation, ConstitutionPhase.Precondition);
        Assert.Equal(ConstitutionOutcome.Allow, allowed.Outcome);

        var denyKernel = new ConstitutionKernel(new InMemoryConstitutionProvider(),
            new ThrowingEvaluator(), audit);
        var denied = await denyKernel.EvaluateAndAuditAsync(Operation("op-deny", "tenant"),
            ConstitutionPhase.Commit);
        Assert.Equal(ConstitutionOutcome.Deny, denied.Outcome);
        var records = await audit.ReadAllAsync();
        Assert.Equal(new long[] { 1, 2 }, records.Select(value => value.Sequence));
        Assert.Equal(string.Empty, records[0].TenantId);
        Assert.Contains("evaluation-failed-closed", records[1].FindingCodes);
    }

    private static OperationEnvelope Operation(string id, string tenant)
        => new(id, CognitiveOperationKind.Retrieve, tenant, "agent", "test",
            null, null, new string('a', 64), DateTimeOffset.UnixEpoch);

    private sealed class ThrowingEvaluator : IConstitutionEvaluator
    {
        public ValueTask<ConstitutionDecision> EvaluateAsync(OperationEnvelope operation,
            ConstitutionBundle constitution, ConstitutionPhase phase,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }
}
