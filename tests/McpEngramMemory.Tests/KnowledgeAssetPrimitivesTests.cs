using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Services.Knowledge;

namespace McpEngramMemory.Tests;

public class KnowledgeAssetPrimitivesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
    private const string ConstitutionHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void KnowledgeVersionRejectsCrossTenantEvidence()
    {
        var crossTenant = new EvidenceReference(
            new ArtifactRef("other-tenant", "sources", ArtifactKind.Evidence, "source", "v1"),
            new string('a', 64), T0, "source",
            Envelope((ArtifactCapability.Read, new[] { "alice" })));

        Assert.Throws<ArgumentException>(() => new KnowledgeVersionDefinition(
            Ref("claim", "v1"), "claim", KnowledgeMaturity.Supported, KnowledgeStatus.Active,
            Temporal(), Profile(), [crossTenant], [],
            Envelope((ArtifactCapability.Read, new[] { "alice" })), ConstitutionHash));
    }

    [Fact]
    public void Promotion_RequiresStableEvidence_NotMemoryLifecycleOrSalience()
    {
        var proposed = Version("1.0", KnowledgeMaturity.Proposed, KnowledgeStatus.Active);

        var error = Assert.Throws<InvalidOperationException>(() => KnowledgeGovernanceService.Promote(
            proposed,
            Ref("claim", "1.1"),
            KnowledgeMaturity.Hypothesized,
            supportingEvidence: Array.Empty<EvidenceReference>(),
            contradictingEvidence: null,
            Temporal(),
            Profile(),
            ConstitutionHash));

        Assert.Contains("requires supporting evidence", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Promotion_PreservesExistingAndNewContradictions()
    {
        var permissions = Envelope((ArtifactCapability.Read, new[] { "alice" }));
        var oldConflict = Evidence("conflict-old", 'b', permissions);
        var newConflict = Evidence("conflict-new", 'c', permissions);
        var proposed = Version(
            "1.0",
            KnowledgeMaturity.Proposed,
            KnowledgeStatus.Disputed,
            contradictions: [oldConflict],
            permissions: permissions);

        var promoted = KnowledgeGovernanceService.Promote(
            proposed,
            Ref("claim", "1.1"),
            KnowledgeMaturity.Hypothesized,
            [Evidence("support", 'a', permissions)],
            [newConflict],
            Temporal(),
            Profile(),
            ConstitutionHash);

        Assert.Equal(KnowledgeStatus.Disputed, promoted.Status);
        Assert.Equal(2, promoted.ContradictingEvidence.Count);
        Assert.Contains(promoted.ContradictingEvidence, value => value.Artifact.ArtifactId == "conflict-old");
        Assert.Contains(promoted.ContradictingEvidence, value => value.Artifact.ArtifactId == "conflict-new");
    }

    [Fact]
    public void Promotion_IntersectsEveryCapability_AndCannotEscalatePermissions()
    {
        var currentPermissions = Envelope(
            (ArtifactCapability.Read, new[] { "alice", "bob" }),
            (ArtifactCapability.Use, new[] { "alice", "bob" }),
            (ArtifactCapability.Train, new[] { "alice" }));
        var sourceOne = Envelope(
            (ArtifactCapability.Read, new[] { "alice", "bob" }),
            (ArtifactCapability.Use, new[] { "alice" }),
            (ArtifactCapability.Train, new[] { "alice" }));
        var sourceTwo = Envelope(
            (ArtifactCapability.Read, new[] { "bob", "charlie" }),
            (ArtifactCapability.Use, new[] { "alice", "charlie" }),
            (ArtifactCapability.Train, Array.Empty<string>()));
        var proposed = Version(
            "1.0", KnowledgeMaturity.Proposed, KnowledgeStatus.Active, permissions: currentPermissions);

        var promoted = KnowledgeGovernanceService.Promote(
            proposed,
            Ref("claim", "1.1"),
            KnowledgeMaturity.Hypothesized,
            [Evidence("source-1", 'a', sourceOne), Evidence("source-2", 'b', sourceTwo)],
            null,
            Temporal(),
            Profile(),
            ConstitutionHash);

        Assert.Equal(["bob"], promoted.Permissions.SubjectsFor(ArtifactCapability.Read));
        Assert.Equal(["alice"], promoted.Permissions.SubjectsFor(ArtifactCapability.Use));
        Assert.Empty(promoted.Permissions.SubjectsFor(ArtifactCapability.Train));
        Assert.True(PermissionEnvelopeService.IsNarrowerThanOrEqual(promoted.Permissions, currentPermissions));
        Assert.True(PermissionEnvelopeService.IsNarrowerThanOrEqual(promoted.Permissions, sourceOne));
        Assert.True(PermissionEnvelopeService.IsNarrowerThanOrEqual(promoted.Permissions, sourceTwo));
    }

    [Fact]
    public void BroaderRelease_RequiresApprovalAndCreatesNewBranch_WithoutMutatingOriginal()
    {
        var restricted = Envelope((ArtifactCapability.Read, new[] { "alice" }));
        var original = Version(
            "2.0",
            KnowledgeMaturity.Verified,
            KnowledgeStatus.Disputed,
            support: [Evidence("source", 'a', restricted)],
            contradictions: [Evidence("counter", 'b', restricted)],
            permissions: restricted);
        string originalHash = original.ContentHash;

        var releasedPermissions = Envelope((ArtifactCapability.Read, new[] { "alice", "public" }));
        var proposal = new DeclassificationProposal(
            "declass-42",
            original.Reference,
            new ArtifactRef("tenant", "project", ArtifactKind.Knowledge, "claim-public", "1.0"),
            releasedPermissions,
            [Evidence("sanitizer-output", 'd', restricted)],
            [new LeakageCheckResult("pii-scan", true, "no identifiers", T0.AddMinutes(1))],
            "requester",
            T0);
        var approval = new DeclassificationApproval(
            proposal.ProposalId,
            "governor",
            Envelope((ArtifactCapability.Declassify, new[] { "governor" })),
            new ArtifactRef("tenant", "governance", ArtifactKind.Approval, "approval-42", "1"),
            T0.AddMinutes(2));

        var branch = KnowledgeGovernanceService.CreateDeclassificationBranch(
            original,
            proposal,
            approval,
            "Sanitized public claim",
            Temporal(),
            ConstitutionHash);

        Assert.Same(original, branch.Original);
        Assert.Equal(originalHash, branch.OriginalContentHash);
        Assert.Equal(originalHash, original.ContentHash);
        Assert.NotEqual(original.Reference, branch.Released.Reference);
        Assert.Equal("declass-42", branch.Released.Definition.DerivationBranchId);
        Assert.True(branch.Released.Permissions.Allows(ArtifactCapability.Read, "public"));
        Assert.Single(branch.Released.ContradictingEvidence);
    }

    [Fact]
    public void CanonicalHashes_AreDeterministicAcrossInputOrdering()
    {
        var permissionsOne = Envelope(
            (ArtifactCapability.Use, new[] { "bob", "alice" }),
            (ArtifactCapability.Read, new[] { "charlie", "alice" }));
        var permissionsTwo = Envelope(
            (ArtifactCapability.Read, new[] { "alice", "charlie" }),
            (ArtifactCapability.Use, new[] { "alice", "bob" }));
        var evidenceA = Evidence("a", 'a', permissionsOne);
        var evidenceB = Evidence("b", 'b', permissionsOne);

        var first = Version(
            "1.0", KnowledgeMaturity.Supported, KnowledgeStatus.Active,
            support: [evidenceB, evidenceA], permissions: permissionsOne);
        var second = Version(
            "1.0", KnowledgeMaturity.Supported, KnowledgeStatus.Active,
            support: [evidenceA, evidenceB], permissions: permissionsTwo);

        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal(first.ContentHash, KnowledgeCanonicalizer.ComputeHash(first));

        var next = Version("2.0", KnowledgeMaturity.Verified, KnowledgeStatus.Active);
        var assetOne = KnowledgeCanonicalizer.PublishAsset([next, first], next.ContentHash);
        var assetTwo = KnowledgeCanonicalizer.PublishAsset([first, next], next.ContentHash);
        Assert.Equal(assetOne.ContentHash, assetTwo.ContentHash);
        Assert.Equal(assetOne.ContentHash, KnowledgeCanonicalizer.ComputeHash(assetOne));
    }

    [Fact]
    public void Status_Maturity_AndBitemporalValidity_AreOrthogonal()
    {
        var temporal = new BitemporalValidity(
            createdAt: T0.AddDays(-20),
            recordedAt: T0.AddDays(-10),
            validFrom: T0.AddDays(-30),
            validUntil: T0.AddDays(-1),
            verifiedAt: T0.AddDays(-5),
            supersededAt: null);
        var disputedVerified = Version(
            "7.0", KnowledgeMaturity.Verified, KnowledgeStatus.Disputed, temporal: temporal);

        Assert.Equal(KnowledgeMaturity.Verified, disputedVerified.Maturity);
        Assert.Equal(KnowledgeStatus.Disputed, disputedVerified.Status);
        Assert.Equal(T0.AddDays(-1), disputedVerified.Definition.Temporal.ValidUntil);
        Assert.Equal(T0.AddDays(-10), disputedVerified.Definition.Temporal.RecordedAt);
        Assert.Null(disputedVerified.Definition.Temporal.SupersededAt);
        Assert.NotEqual(
            disputedVerified.Definition.Epistemic.Confidence.Value,
            disputedVerified.Definition.Epistemic.Authority.Value);
    }

    private static KnowledgeVersion Version(
        string version,
        KnowledgeMaturity maturity,
        KnowledgeStatus status,
        IEnumerable<EvidenceReference>? support = null,
        IEnumerable<EvidenceReference>? contradictions = null,
        PermissionEnvelope? permissions = null,
        BitemporalValidity? temporal = null)
        => KnowledgeCanonicalizer.PublishVersion(new KnowledgeVersionDefinition(
            Ref("claim", version),
            "The governed claim",
            maturity,
            status,
            temporal ?? Temporal(),
            Profile(),
            support,
            contradictions,
            permissions ?? Envelope((ArtifactCapability.Read, new[] { "alice", "bob" })),
            ConstitutionHash));

    private static ArtifactRef Ref(string id, string version)
        => new("tenant", "project", ArtifactKind.Knowledge, id, version);

    private static EvidenceReference Evidence(
        string id,
        char hashCharacter,
        PermissionEnvelope permissions)
        => new(
            new ArtifactRef("tenant", "sources", ArtifactKind.Evidence, id, "v1"),
            new string(hashCharacter, 64),
            T0,
            $"source-{id}",
            permissions);

    private static PermissionEnvelope Envelope(
        params (ArtifactCapability Capability, string[] Subjects)[] grants)
        => new(grants.Select(value => new CapabilityGrant(value.Capability, value.Subjects)));

    private static BitemporalValidity Temporal()
        => new(T0, T0.AddMinutes(1), T0.AddDays(-1), T0.AddDays(30));

    private static EpistemicProfile Profile()
        => new(
            Component(0.81m, "calibration set"),
            Component(0.72m, "domain policy"),
            Component(0.63m, "resolved outcomes"),
            Component(0.84m, "coverage and directness"),
            Component(0.95m, "freshness policy"),
            Component(0.55m, "independent sources"));

    private static CalibratedComponent Component(decimal value, string basis)
        => new(value, basis, "cal-v1", T0);
}
