using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Models.Governance;
using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Models.Learning;
using McpEngramMemory.Core.Models.Provenance;
using McpEngramMemory.Core.Services.Governance.Persistence;
using McpEngramMemory.Core.Services.Constitution;
using McpEngramMemory.Core.Services.Knowledge;
using McpEngramMemory.Core.Services.Provenance;

namespace McpEngramMemory.Tests;

public sealed class FileGovernedKnowledgeStoreTests : IDisposable
{
    private static readonly string ConstitutionHash = RootConstitution.Version.ContentHash;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"engram-governed-{Guid.NewGuid():N}");

    [Fact]
    public async Task CommitSurvivesReopenAsOneCompleteAggregate()
    {
        var commit = Commit("tenant-a", "v1", null);
        var result = await new FileGovernedKnowledgeStore(_root).CommitPromotionAsync(
            commit, Authority);

        var snapshot = await new FileGovernedKnowledgeStore(_root)
            .ReadAsync("tenant-a", "knowledge", "claim");

        Assert.Equal(GovernedCommitOutcome.Committed, result.Outcome);
        Assert.Equal("v1", snapshot.ActiveVersion!.Reference.Version);
        Assert.Single(snapshot.Asset!.Versions);
        Assert.Single(snapshot.Provenance);
        Assert.Single(snapshot.Audit);
        Assert.Equal(1, snapshot.Audit[0].Sequence);
    }

    [Fact]
    public async Task StaleCasDoesNotPersistAnyPartOfRejectedPromotion()
    {
        var store = new FileGovernedKnowledgeStore(_root);
        await store.CommitPromotionAsync(Commit("tenant-a", "v1", null), Authority);

        var rejected = await store.CommitPromotionAsync(
            Commit("tenant-a", "v2", new string('f', 64)), Authority);
        var snapshot = await new FileGovernedKnowledgeStore(_root)
            .ReadAsync("tenant-a", "knowledge", "claim");

        Assert.Equal(GovernedCommitOutcome.VersionConflict, rejected.Outcome);
        Assert.Single(snapshot.Asset!.Versions);
        Assert.Single(snapshot.Provenance);
        Assert.Single(snapshot.Audit);
        Assert.Equal("v1", snapshot.ActiveVersion!.Reference.Version);
    }

    [Fact]
    public async Task IdenticalArtifactIdsRemainTenantPartitionedOnDisk()
    {
        var store = new FileGovernedKnowledgeStore(_root);
        await store.CommitPromotionAsync(Commit("tenant-a", "v1", null), Authority);
        await store.CommitPromotionAsync(Commit("tenant-b", "v1", null), Authority);

        var a = await new FileGovernedKnowledgeStore(_root).ReadAsync("tenant-a", "knowledge", "claim");
        var b = await new FileGovernedKnowledgeStore(_root).ReadAsync("tenant-b", "knowledge", "claim");

        Assert.Equal("tenant-a", a.ActiveVersion!.Reference.TenantId);
        Assert.Equal("tenant-b", b.ActiveVersion!.Reference.TenantId);
        Assert.All(a.Provenance, assertion => Assert.Equal("tenant-a", assertion.TenantId));
        Assert.All(b.Provenance, assertion => Assert.Equal("tenant-b", assertion.TenantId));
    }

    [Fact]
    public async Task ConcurrentStoreInstancesUseOneCrossProcessCasBoundary()
    {
        var attempts = Enumerable.Range(1, 12)
            .Select(index => new FileGovernedKnowledgeStore(_root).CommitPromotionAsync(
                Commit("tenant-a", $"v{index}", null), Authority).AsTask())
            .ToArray();

        var results = await Task.WhenAll(attempts);
        Assert.Single(results, result => result.Outcome == GovernedCommitOutcome.Committed);
        Assert.Equal(11, results.Count(result => result.Outcome == GovernedCommitOutcome.VersionConflict));

        var snapshot = await new FileGovernedKnowledgeStore(_root)
            .ReadAsync("tenant-a", "knowledge", "claim");
        Assert.Single(snapshot.Asset!.Versions);
        Assert.Single(snapshot.Provenance);
        Assert.Single(snapshot.Audit);
    }

    [Fact]
    public async Task ForgedPromotedOutcomeCannotOverrideDeniedConstitutionDecision()
    {
        var valid = Commit("tenant-a", "v1", null);
        var deniedDecision = new ConstitutionDecision("operation", ConstitutionPhase.Commit,
            ConstitutionOutcome.Deny,
            [new ConstitutionFinding("root", "denied", ConstitutionOutcome.Deny, "denied")],
            [ConstitutionHash]);
        var forged = valid with
        {
            Promotion = new PromotionResult(valid.Promotion.Outcome, valid.Promotion.Findings,
                valid.Promotion.VerificationTrace, deniedDecision)
        };

        var result = await new FileGovernedKnowledgeStore(_root)
            .CommitPromotionAsync(forged, Authority);

        Assert.Equal(GovernedCommitOutcome.Denied, result.Outcome);
        Assert.Equal("audit-decision-mismatch", result.Code);
        Assert.Null((await new FileGovernedKnowledgeStore(_root)
            .ReadAsync("tenant-a", "knowledge", "claim")).Asset);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static IReadOnlyDictionary<string, string> Versions()
        => new Dictionary<string, string> { ["evidence"] = "v1" };

    private static CommitAuthorityState Authority()
        => new(ConstitutionHash, Versions());

    private static GovernedPromotionCommit Commit(string tenant, string version, string? expected)
    {
        var source = new ArtifactRef(tenant, "evidence", ArtifactKind.Evidence, "source", "v1");
        var target = new ArtifactRef(tenant, "knowledge", ArtifactKind.Knowledge, "claim", version);
        var permissions = Envelope("agent");
        var definition = new KnowledgeVersionDefinition(target, "claim", KnowledgeMaturity.Supported,
            KnowledgeStatus.Active,
            new BitemporalValidity(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            Epistemic(),
            new[] { new EvidenceReference(source, new string('a', 64), DateTimeOffset.UnixEpoch, "source", permissions) },
            Array.Empty<EvidenceReference>(), permissions, ConstitutionHash);
        var knowledge = KnowledgeCanonicalizer.PublishVersion(definition);
        var receipt = Receipt(knowledge, target, source, tenant);
        var provenance = ProvenanceCanonicalizer.Publish($"p-{tenant}-{version}", target, new[] { source },
            ProvenanceRelation.DerivedFrom, "teacher", "runtime", "1", null, ConstitutionHash,
            receipt.AuditRecord.EventId, permissions, DateTimeOffset.UnixEpoch);
        var decision = receipt.Decision;
        var trace = new VerificationTrace("proposal", new[]
        {
            new VerificationRun(1, new VerifierIdentity("deterministic", "1", VerifierKind.Deterministic),
                VerificationStatus.Passed, true, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
        });
        var promotion = new PromotionResult(PromotionOutcome.Promoted, Array.Empty<PromotionFinding>(), trace, decision);
        return new GovernedPromotionCommit(knowledge, expected, promotion, provenance, receipt,
            new CommitAuthorizationSnapshot(ConstitutionHash, Versions()),
            new Dictionary<ArtifactRef, PermissionEnvelope> { [source] = permissions });
    }

    private static ConstitutionCommitReceipt Receipt(
        KnowledgeVersion version, ArtifactRef target, ArtifactRef source, string tenant)
    {
        var operation = new OperationEnvelope("operation", CognitiveOperationKind.PromoteKnowledge,
            tenant, "agent", "promote governed knowledge",
            [new OperationArtifactReference(source.TenantId, source.Namespace, source.Kind.ToString(),
                source.ArtifactId, source.Version)],
            new OperationArtifactReference(target.TenantId, target.Namespace, target.Kind.ToString(),
                target.ArtifactId, target.Version),
            version.ContentHash, DateTimeOffset.UnixEpoch);
        var kernel = new ConstitutionKernel(new InMemoryConstitutionProvider(),
            new DeterministicConstitutionEvaluator([new AuditEnvelopeConstitutionRule()]),
            new InMemoryConstitutionAuditStore());
        return kernel.AuthorizeCommitAsync(operation).AsTask().GetAwaiter().GetResult();
    }

    private static PermissionEnvelope Envelope(string subject)
        => new(new[]
        {
            new CapabilityGrant(ArtifactCapability.Read, new[] { subject }),
            new CapabilityGrant(ArtifactCapability.Use, new[] { subject }),
            new CapabilityGrant(ArtifactCapability.Train, new[] { subject })
        });

    private static EpistemicProfile Epistemic()
    {
        CalibratedComponent Component(string basis) => new(.7m, basis, "v1", DateTimeOffset.UnixEpoch);
        return new EpistemicProfile(Component("confidence"), Component("authority"), Component("trust"),
            Component("evidence"), Component("freshness"), Component("consensus"));
    }
}
