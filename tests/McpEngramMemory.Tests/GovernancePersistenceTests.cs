using System.Text.Json;
using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Models.Provenance;
using McpEngramMemory.Core.Services.Constitution;
using McpEngramMemory.Core.Services.Governance.Persistence;
using McpEngramMemory.Core.Services.Knowledge;
using McpEngramMemory.Core.Services.Provenance;

namespace McpEngramMemory.Tests;

public sealed class GovernancePersistenceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 20, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "engram-governance-persistence-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ConstitutionSnapshot_IsSchemaWrappedAtomicAndRecoversPastStaleTemp()
    {
        var store = new FileConstitutionVersionStore(_root);
        var version = RootConstitution.Version;
        await store.SaveAsync("tenant-a", [version], version.ContentHash);

        string snapshotPath = Directory.EnumerateFiles(_root, "constitution.json", SearchOption.AllDirectories).Single();
        using (var document = JsonDocument.Parse(await File.ReadAllBytesAsync(snapshotPath)))
        {
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("constitution-versions", document.RootElement.GetProperty("store").GetString());
            Assert.Equal("tenant-a", document.RootElement.GetProperty("tenantId").GetString());
        }
        await File.WriteAllTextAsync($"{snapshotPath}.crash.tmp", "partial replacement");

        var loaded = await new FileConstitutionVersionStore(_root).LoadAsync("tenant-a");

        Assert.NotNull(loaded.Value);
        Assert.Equal(version.ContentHash, loaded.Value!.ActiveVersionHash);
        Assert.Equal(version.ContentHash, Assert.Single(loaded.Value.Versions).ContentHash);
        Assert.Contains(loaded.Diagnostics, value => value.Code == "stale-temp-ignored" && value.Recovered);
    }

    [Fact]
    public async Task KnowledgeSnapshot_PreservesImmutableVersionsAndConsistentActivePointer()
    {
        var first = KnowledgeVersion("1.0", "first claim");
        var second = KnowledgeVersion("2.0", "second claim");
        var asset = KnowledgeCanonicalizer.PublishAsset([second, first], second.ContentHash);
        var store = new FileKnowledgeAssetStore(_root);
        await store.SaveAsync(asset);

        var loaded = await new FileKnowledgeAssetStore(_root)
            .LoadAsync("tenant-a", "project", "claim");

        Assert.NotNull(loaded.Value);
        Assert.Equal(asset.ContentHash, loaded.Value!.ContentHash);
        Assert.Equal(second.ContentHash, loaded.Value.ActiveVersionHash);
        Assert.Equal([first.ContentHash, second.ContentHash],
            loaded.Value.Versions.Select(value => value.ContentHash));
    }

    [Fact]
    public async Task KnowledgeAndConstitutionPartitions_DoNotCrossTenants()
    {
        var constitution = RootConstitution.Version;
        var constitutionStore = new FileConstitutionVersionStore(_root);
        await constitutionStore.SaveAsync("tenant-a", [constitution], constitution.ContentHash);

        Assert.Null((await constitutionStore.LoadAsync("tenant-b")).Value);

        var version = KnowledgeVersion("1.0", "tenant a");
        var asset = KnowledgeCanonicalizer.PublishAsset([version], version.ContentHash);
        var knowledgeStore = new FileKnowledgeAssetStore(_root);
        await knowledgeStore.SaveAsync(asset);

        Assert.Null((await knowledgeStore.LoadAsync("tenant-b", "project", "claim")).Value);
    }

    [Fact]
    public async Task AuditReplay_IgnoresOnlyCorruptTailAndReportsRecovery()
    {
        var store = new FileConstitutionAuditStore(_root);
        await store.AppendAsync(Audit("event-1", "tenant-a"));
        await store.AppendAsync(Audit("event-2", "tenant-b"));
        string journal = Path.Combine(_root, "audit.journal");
        await File.AppendAllTextAsync(journal, "{\"schemaVersion\":");

        var reopened = new FileConstitutionAuditStore(_root);
        var records = await reopened.ReadAllAsync();

        Assert.Equal(2, records.Count);
        Assert.Equal([1L, 2L], records.Select(value => value.Sequence));
        Assert.Contains(reopened.Diagnostics,
            value => value.Code == "corrupt-tail-ignored" && value.Recovered);

        await reopened.AppendAsync(Audit("event-3", "tenant-a"));
        var afterRecovery = await new FileConstitutionAuditStore(_root).ReadAllAsync();
        Assert.Equal(["event-1", "event-2", "event-3"], afterRecovery.Select(value => value.EventId));
    }

    [Fact]
    public async Task AuditAppend_KeepsSequenceDenseAcrossConcurrentStoreInstances()
    {
        // Two instances stand in for two server processes sharing one governance root — the
        // default path is install-relative, so the documented per-agent deployment shares it.
        // Each must number from the journal, not from its own in-memory count.
        var first = new FileConstitutionAuditStore(_root);
        var second = new FileConstitutionAuditStore(_root);

        await first.AppendAsync(Audit("event-1", "tenant-a"));
        await second.AppendAsync(Audit("event-2", "tenant-b"));
        await first.AppendAsync(Audit("event-3", "tenant-a"));
        await second.AppendAsync(Audit("event-4", "tenant-b"));

        var reopened = await new FileConstitutionAuditStore(_root).ReadAllAsync();

        Assert.Equal([1L, 2L, 3L, 4L], reopened.Select(value => value.Sequence));
        Assert.Equal(["event-1", "event-2", "event-3", "event-4"],
            reopened.Select(value => value.EventId));
        // Both live instances must also see the peer's records, not just their own.
        Assert.Equal(4, (await first.ReadAllAsync()).Count);
        Assert.Equal(4, (await second.ReadAllAsync()).Count);
    }

    [Fact]
    public async Task AuditAppend_SurvivesConcurrentWritersUnderContention()
    {
        var stores = Enumerable.Range(0, 4).Select(_ => new FileConstitutionAuditStore(_root)).ToArray();

        await Task.WhenAll(stores.SelectMany((store, storeIndex) =>
            Enumerable.Range(0, 5).Select(async index =>
                await store.AppendAsync(Audit($"event-{storeIndex}-{index}", "tenant-a")))));

        var records = await new FileConstitutionAuditStore(_root).ReadAllAsync();

        Assert.Equal(20, records.Count);
        Assert.Equal(Enumerable.Range(1, 20).Select(value => (long)value),
            records.Select(value => value.Sequence));
        Assert.Equal(20, records.Select(value => value.EventId).Distinct().Count());
    }

    [Fact]
    public async Task AuditReplay_FailedIdentityCheckLeavesNoPartialState()
    {
        var store = new FileConstitutionAuditStore(_root);
        await store.AppendAsync(Audit("event-1", "tenant-a"));
        await store.AppendAsync(Audit("event-2", "tenant-a"));
        await store.AppendAsync(Audit("event-3", "tenant-a"));

        // Desync the middle record's envelope tenant from its payload. The envelope's own fields
        // are outside the payload checksum, so this reaches the store's identity check rather than
        // being caught earlier by replay's hash verification.
        string journal = Path.Combine(_root, "audit.journal");
        string[] lines = await File.ReadAllLinesAsync(journal);
        string original = lines[1];
        lines[1] = original.Replace(
            "\"store\":\"constitution-audit\",\"tenantId\":\"tenant-a\"",
            "\"store\":\"constitution-audit\",\"tenantId\":\"tenant-x\"");
        Assert.NotEqual(original, lines[1]);
        await File.WriteAllLinesAsync(journal, lines);

        var reopened = new FileConstitutionAuditStore(_root);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await reopened.ReadAllAsync());

        // A retry must re-derive from disk rather than replay on top of a half-applied set.
        await Assert.ThrowsAsync<InvalidDataException>(async () => await reopened.ReadAllAsync());

        await File.WriteAllLinesAsync(journal, [lines[0], original, lines[2]]);
        var recovered = await reopened.ReadAllAsync();

        Assert.Equal([1L, 2L, 3L], recovered.Select(value => value.Sequence));
        await reopened.AppendAsync(Audit("event-4", "tenant-a"));
        Assert.Equal([1L, 2L, 3L, 4L],
            (await new FileConstitutionAuditStore(_root).ReadAllAsync()).Select(value => value.Sequence));
    }

    [Fact]
    public async Task AuditReplay_RejectsCorruptionBeforeTail()
    {
        var store = new FileConstitutionAuditStore(_root);
        await store.AppendAsync(Audit("event-1", "tenant-a"));
        await store.AppendAsync(Audit("event-2", "tenant-a"));
        string journal = Path.Combine(_root, "audit.journal");
        string[] lines = await File.ReadAllLinesAsync(journal);
        lines[0] = "!" + lines[0][1..];
        await File.WriteAllLinesAsync(journal, lines);

        var reopened = new FileConstitutionAuditStore(_root);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await reopened.ReadAllAsync());
    }

    [Fact]
    public async Task ProvenanceJournal_ReplaysWithTenantIsolationAndImmutableHashes()
    {
        var permissions = Permissions("alice");
        var assertionA = Assertion("tenant-a", "assertion", "target-a", permissions);
        var assertionB = Assertion("tenant-b", "assertion", "target-b", permissions);
        var store = new FileProvenanceStore(_root);
        await store.AppendAsync(assertionA, assertionA.Sources.ToDictionary(value => value, _ => permissions));
        await store.AppendAsync(assertionB, assertionB.Sources.ToDictionary(value => value, _ => permissions));

        var reopened = new FileProvenanceStore(_root);
        var lineageA = await reopened.ReadLineageAsync(new ProvenanceQuery(
            "tenant-a", assertionA.Target, "alice", ArtifactCapability.Read));
        var lineageB = await reopened.ReadLineageAsync(new ProvenanceQuery(
            "tenant-b", assertionB.Target, "alice", ArtifactCapability.Read));

        Assert.Equal(assertionA.ContentHash, Assert.Single(lineageA.Assertions).ContentHash);
        Assert.Equal(assertionB.ContentHash, Assert.Single(lineageB.Assertions).ContentHash);
        Assert.DoesNotContain(lineageA.Assertions, value => value.TenantId == "tenant-b");
    }

    [Fact]
    public async Task DecisionJournal_ReplaysPerTenantAndRejectsConflictingRewrite()
    {
        var decisionA = Decision("operation-a", ConstitutionOutcome.Allow);
        var decisionB = Decision("operation-b", ConstitutionOutcome.Deny);
        var store = new FileConstitutionDecisionStore(_root);
        await store.AppendAsync("tenant-a", decisionA);
        await store.AppendAsync("tenant-b", decisionB);

        var reopened = new FileConstitutionDecisionStore(_root);
        Assert.Equal("operation-a", Assert.Single(await reopened.ReadAsync("tenant-a")).OperationId);
        Assert.Equal("operation-b", Assert.Single(await reopened.ReadAsync("tenant-b")).OperationId);

        var conflict = Decision("operation-a", ConstitutionOutcome.Deny);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await reopened.AppendAsync("tenant-a", conflict));
    }

    [Fact]
    public async Task SnapshotLoad_RejectsPayloadTamperingInsteadOfGuessing()
    {
        var store = new FileConstitutionVersionStore(_root);
        var version = RootConstitution.Version;
        await store.SaveAsync("tenant-a", [version], version.ContentHash);
        string path = Directory.EnumerateFiles(_root, "constitution.json", SearchOption.AllDirectories).Single();
        string json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace("Engram Root Constitution", "Tampered Constitution"));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new FileConstitutionVersionStore(_root).LoadAsync("tenant-a"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static KnowledgeVersion KnowledgeVersion(string version, string claim)
        => KnowledgeCanonicalizer.PublishVersion(new KnowledgeVersionDefinition(
            new ArtifactRef("tenant-a", "project", ArtifactKind.Knowledge, "claim", version),
            claim,
            KnowledgeMaturity.Verified,
            KnowledgeStatus.Active,
            new BitemporalValidity(Now, Now, Now.AddDays(-1), Now.AddYears(1), Now, null),
            Profile(),
            [Evidence("tenant-a", "source")],
            [],
            Permissions("alice"),
            Hash('c')));

    private static EvidenceReference Evidence(string tenant, string id)
        => new(
            new ArtifactRef(tenant, "sources", ArtifactKind.Evidence, id, "1"),
            Hash('e'), Now, id, Permissions("alice"));

    private static ProvenanceAssertion Assertion(
        string tenant,
        string assertionId,
        string targetId,
        PermissionEnvelope permissions)
    {
        var target = new ArtifactRef(tenant, "project", ArtifactKind.Knowledge, targetId, "1");
        var source = new ArtifactRef(tenant, "sources", ArtifactKind.Evidence, $"source-{targetId}", "1");
        return ProvenanceCanonicalizer.Publish(
            assertionId, target, [source], ProvenanceRelation.DerivedFrom,
            "teacher", "runtime", "1", [], Hash('c'), $"audit-{targetId}", permissions, Now);
    }

    private static ConstitutionAuditRecord Audit(string eventId, string tenant)
        => new(
            0, eventId, $"operation-{eventId}", tenant, "principal",
            ConstitutionPhase.Commit, ConstitutionOutcome.Allow,
            [RootConstitution.Version.ContentHash], [], Now);

    private static ConstitutionDecision Decision(string operationId, ConstitutionOutcome outcome)
        => new(operationId, ConstitutionPhase.Commit, outcome, [], [RootConstitution.Version.ContentHash]);

    private static PermissionEnvelope Permissions(params string[] subjects)
        => new([new CapabilityGrant(ArtifactCapability.Read, subjects)]);

    private static EpistemicProfile Profile()
    {
        CalibratedComponent Component(decimal value) => new(value, "basis", "v1", Now);
        return new EpistemicProfile(
            Component(.8m), Component(.7m), Component(.6m),
            Component(.9m), Component(.9m), Component(.7m));
    }

    private static string Hash(char value) => new(value, 64);
}
