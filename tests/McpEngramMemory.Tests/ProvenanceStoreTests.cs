using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Models.Provenance;
using McpEngramMemory.Core.Services.Provenance;

namespace McpEngramMemory.Tests;

public sealed class ProvenanceStoreTests
{
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    [Fact]
    public async Task AppendIsIdempotentButRejectsImmutableIdConflict()
    {
        var store = new InMemoryProvenanceStore();
        var assertion = Assertion("edge-1", "tenant-a", "v1", DateTimeOffset.UnixEpoch);
        var permissions = SourcePermissions(assertion, "agent");

        Assert.Equal(ProvenanceAppendOutcome.Appended,
            (await store.AppendAsync(assertion, permissions)).Outcome);
        Assert.Equal(ProvenanceAppendOutcome.AlreadyPresent,
            (await store.AppendAsync(assertion, permissions)).Outcome);

        var conflicting = Assertion("edge-1", "tenant-a", "v2", DateTimeOffset.UnixEpoch.AddSeconds(1));
        await Assert.ThrowsAsync<ProvenanceConflictException>(async () =>
            await store.AppendAsync(conflicting, SourcePermissions(conflicting, "agent")));
    }

    [Fact]
    public async Task AppendRejectsPermissionBroadeningAndMissingSourceSnapshot()
    {
        var assertion = Assertion("edge-1", "tenant-a", "v1", DateTimeOffset.UnixEpoch,
            subject: "agent", additionalSubject: "intruder");
        var store = new InMemoryProvenanceStore();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await store.AppendAsync(assertion, new Dictionary<ArtifactRef, PermissionEnvelope>()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await store.AppendAsync(assertion, SourcePermissions(assertion, "agent")));
    }

    [Fact]
    public async Task LineageIsTenantAndPermissionScopedAndDeterministicallyOrdered()
    {
        var store = new InMemoryProvenanceStore();
        var later = Assertion("z-edge", "tenant-a", "v2", DateTimeOffset.UnixEpoch.AddMinutes(1));
        var earlier = Assertion("a-edge", "tenant-a", "v2", DateTimeOffset.UnixEpoch);
        var otherTenant = Assertion("other", "tenant-b", "v2", DateTimeOffset.UnixEpoch);
        await store.AppendAsync(later, SourcePermissions(later, "agent"));
        await store.AppendAsync(earlier, SourcePermissions(earlier, "agent"));
        await store.AppendAsync(otherTenant, SourcePermissions(otherTenant, "agent"));

        var lineage = await store.ReadLineageAsync(new ProvenanceQuery(
            "tenant-a", earlier.Target, "agent", ArtifactCapability.Read));

        Assert.Equal(new[] { "a-edge", "z-edge" }, lineage.Assertions.Select(value => value.AssertionId));
        Assert.All(lineage.Assertions, value => Assert.Equal("tenant-a", value.TenantId));
        Assert.Empty((await store.ReadLineageAsync(new ProvenanceQuery(
            "tenant-a", earlier.Target, "nobody", ArtifactCapability.Read))).Assertions);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await store.ReadLineageAsync(new ProvenanceQuery(
                "tenant-b", earlier.Target, "agent", ArtifactCapability.Read)));
    }

    [Fact]
    public void ProvenanceProjectionCanNeverParticipateInCognitiveDiffusion()
    {
        var assertion = Assertion("edge-1", "tenant-a", "v1", DateTimeOffset.UnixEpoch);
        Assert.Equal(GraphProjectionKind.Provenance, assertion.Projection);
        Assert.False(assertion.ParticipatesInDiffusion);
    }

    private static ProvenanceAssertion Assertion(
        string id,
        string tenant,
        string targetVersion,
        DateTimeOffset recordedAt,
        string subject = "agent",
        string? additionalSubject = null)
    {
        var target = Ref(tenant, "derived", targetVersion, ArtifactKind.Knowledge);
        var source = Ref(tenant, "source", "v1", ArtifactKind.Evidence);
        var subjects = additionalSubject is null ? new[] { subject } : new[] { subject, additionalSubject };
        return ProvenanceCanonicalizer.Publish(id, target, new[] { source }, ProvenanceRelation.DerivedFrom,
            "teacher", "runtime", "1.0", new[] { Ref(tenant, "verifier", "v1", ArtifactKind.Verification) },
            HashA, "audit-1", Envelope(subjects), recordedAt);
    }

    private static Dictionary<ArtifactRef, PermissionEnvelope> SourcePermissions(
        ProvenanceAssertion assertion,
        string subject)
        => assertion.Sources.ToDictionary(source => source, _ => Envelope(new[] { subject }));

    private static PermissionEnvelope Envelope(IEnumerable<string> subjects)
        => new(new[]
        {
            new CapabilityGrant(ArtifactCapability.Read, subjects),
            new CapabilityGrant(ArtifactCapability.Use, subjects),
            new CapabilityGrant(ArtifactCapability.Train, subjects)
        });

    private static ArtifactRef Ref(string tenant, string id, string version, ArtifactKind kind)
        => new(tenant, "knowledge", kind, id, version);
}
