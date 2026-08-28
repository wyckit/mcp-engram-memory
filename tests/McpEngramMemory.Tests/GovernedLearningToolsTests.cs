using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Constitution;
using McpEngramMemory.Core.Services.Governance.Persistence;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public sealed class GovernedLearningToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"engram-learning-tool-{Guid.NewGuid():N}");

    [Fact]
    public async Task PromoteKnowledgePublishesReceiptBoundAuditProvenanceAndActiveAsset()
    {
        using var persistence = new PersistenceManager(Path.Combine(_root, "memory"));
        var index = new CognitiveIndex(persistence);
        var embedding = new HashEmbeddingService();
        var registry = new NamespaceRegistry(index, embedding);
        var principal = new PrincipalContext("tenant-a", "alice");
        registry.EnsureOwnership("project", "alice", tenantId: principal.TenantId);
        index.Upsert(new CognitiveEntry("source-1", embedding.Embed("evidence"), "project",
            "The launch date is Tuesday.", "evidence", tenantId: principal.TenantId));

        var provider = new InMemoryConstitutionProvider();
        var audit = new InMemoryConstitutionAuditStore();
        var kernel = new ConstitutionKernel(provider,
            new DeterministicConstitutionEvaluator([new AuditEnvelopeConstitutionRule()]), audit);
        var store = new FileGovernedKnowledgeStore(Path.Combine(_root, "governance"));
        var tools = new GovernedLearningTools(index, new NamespaceAccess(registry, principal), principal,
            provider, kernel, store);

        var result = await tools.PromoteKnowledge("launch-date", "project",
            "The launch date is Tuesday.", ["source-1"]);
        var json = JsonSerializer.Serialize(result);
        var snapshot = await store.ReadAsync("tenant-a", "project", "launch-date");

        Assert.Contains("committed", json, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(snapshot.ActiveVersion);
        Assert.Single(snapshot.Provenance);
        Assert.Single(snapshot.Audit);
        Assert.Equal(snapshot.Provenance[0].AuditEventId, snapshot.Audit[0].EventId);
        Assert.Equal("alice", snapshot.Audit[0].PrincipalId);
        Assert.Equal(provider.Current.EffectiveVersionHash,
            snapshot.Provenance[0].ConstitutionVersionHash);
        Assert.True(snapshot.ActiveVersion.Permissions.Allows(
            McpEngramMemory.Core.Models.Knowledge.ArtifactCapability.Read, "alice"));
        Assert.False(snapshot.ActiveVersion.Permissions.Allows(
            McpEngramMemory.Core.Models.Knowledge.ArtifactCapability.Use, "alice"));
        Assert.False(snapshot.ActiveVersion.Permissions.Allows(
            McpEngramMemory.Core.Models.Knowledge.ArtifactCapability.Train, "alice"));
        Assert.NotEmpty(await audit.ReadAllAsync());
    }

    [Fact]
    public async Task TenantDefaultPrincipalCannotUseGovernedPromotionOrBypassOwnerAcl()
    {
        using var persistence = new PersistenceManager(Path.Combine(_root, "default-memory"));
        var index = new CognitiveIndex(persistence);
        var embedding = new HashEmbeddingService();
        var registry = new NamespaceRegistry(index, embedding);
        registry.EnsureOwnership("private", "alice", tenantId: "tenant-a");
        index.Upsert(new CognitiveEntry("source", embedding.Embed("secret"), "private", "secret",
            "evidence", tenantId: "tenant-a"));
        var principal = new PrincipalContext("tenant-a", AgentIdentity.DefaultAgentId);
        var provider = new InMemoryConstitutionProvider();
        var kernel = new ConstitutionKernel(provider,
            new DeterministicConstitutionEvaluator([new AuditEnvelopeConstitutionRule()]),
            new InMemoryConstitutionAuditStore());
        var tools = new GovernedLearningTools(index, new NamespaceAccess(registry, principal), principal,
            provider, kernel, new FileGovernedKnowledgeStore(Path.Combine(_root, "default-governance")));

        var result = await tools.PromoteKnowledge("claim", "private", "secret", ["source"]);

        Assert.Contains("authenticated, non-default principal", JsonSerializer.Serialize(result));
        Assert.False(registry.HasAccess(AgentIdentity.DefaultAgentId, "private", "read", tenantId: "tenant-a"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
