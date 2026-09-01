using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class MultiAgentTests : IDisposable
{
    private readonly string _dataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly HashEmbeddingService _embedding;
    private readonly MetricsCollector _metrics;
    private readonly NamespaceRegistry _registry;

    public MultiAgentTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"multiagent_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_dataPath);
        _index = new CognitiveIndex(_persistence);
        _embedding = new HashEmbeddingService();
        _metrics = new MetricsCollector();
        _registry = new NamespaceRegistry(_index, _embedding);
    }

    // ── NamespaceRegistry tests ──

    [Fact]
    public void DefaultAgent_HasUnrestrictedAccess()
    {
        Assert.True(_registry.HasAccess(AgentIdentity.DefaultAgentId, "any-namespace", requiredLevel: "read", tenantId: ""));
        Assert.True(_registry.HasAccess(AgentIdentity.DefaultAgentId, "private-ns", "write", tenantId: ""));
        Assert.False(_registry.HasAccess(AgentIdentity.DefaultAgentId, "private-ns", "read", tenantId: "tenant-a"));
    }

    [Fact]
    public void PrincipalContext_MakesLegacyModeExplicit()
    {
        Assert.True(PrincipalContext.LegacyUnisolated.IsLegacyUnisolated);
        Assert.False(new PrincipalContext("tenant-a", AgentIdentity.DefaultAgentId).IsLegacyUnisolated);
        Assert.False(new PrincipalContext(string.Empty, "identified-agent").IsLegacyUnisolated);
    }

    [Fact]
    public void NamespaceRegistry_QualifiesOwnershipAndSharingByTenant()
    {
        _registry.EnsureOwnership("work", "owner-a", tenantId: "tenant-a");
        _registry.EnsureOwnership("work", "owner-b", tenantId: "tenant-b");
        _registry.Share("work", "owner-a", "reader", "read", tenantId: "tenant-a");

        Assert.True(_registry.HasAccess("owner-a", "work", "write", tenantId: "tenant-a"));
        Assert.False(_registry.HasAccess("owner-b", "work", "write", tenantId: "tenant-a"));
        Assert.True(_registry.HasAccess("owner-b", "work", "write", tenantId: "tenant-b"));
        Assert.False(_registry.HasAccess("owner-a", "work", "write", tenantId: "tenant-b"));
        Assert.True(_registry.HasAccess("reader", "work", "read", tenantId: "tenant-a"));
        Assert.False(_registry.HasAccess("reader", "work", "read", tenantId: "tenant-b"));

        var tenantA = _registry.GetAccessibleNamespaces("reader", tenantId: "tenant-a");
        var tenantB = _registry.GetAccessibleNamespaces("reader", tenantId: "tenant-b");
        Assert.Single(tenantA.SharedNamespaces);
        Assert.Empty(tenantB.SharedNamespaces);
    }

    [Fact]
    public void IdentifiedPrincipal_CanClaimOnlyEmptyUnregisteredNamespaceForWrite()
    {
        Assert.False(_registry.HasAccess("agent-x", "unregistered-ns", requiredLevel: "read", tenantId: ""));
        Assert.True(_registry.HasAccess("agent-x", "unregistered-ns", "write", tenantId: ""));
        Assert.True(_registry.HasAccess("agent-x", "unregistered-ns", "read", tenantId: ""));
        Assert.False(_registry.HasAccess("agent-y", "unregistered-ns", "write", tenantId: ""));
    }

    [Fact]
    public void SystemNamespaces_ArePrivateToInternalServices()
    {
        Assert.False(_registry.HasAccess("any-agent", "_system_sharing", requiredLevel: "read", tenantId: ""));
        Assert.False(_registry.HasAccess("restricted-agent", "_system_internal", requiredLevel: "read", tenantId: ""));
        Assert.True(_registry.HasAccess(AgentIdentity.DefaultAgentId, "_system_sharing", requiredLevel: "read", tenantId: ""));
    }

    [Fact]
    public void Share_GrantsReadAccess()
    {
        _registry.EnsureOwnership("work", "agent-a", tenantId: "");

        var result = _registry.Share("work", "agent-a", "agent-b", "read", tenantId: "");

        Assert.Equal("shared", result.Status);
        Assert.True(_registry.HasAccess("agent-b", "work", "read", tenantId: ""));
        Assert.False(_registry.HasAccess("agent-b", "work", "write", tenantId: ""));
    }

    [Fact]
    public void Share_GrantsWriteAccess()
    {
        _registry.EnsureOwnership("work", "agent-a", tenantId: "");

        _registry.Share("work", "agent-a", "agent-b", "write", tenantId: "");

        Assert.True(_registry.HasAccess("agent-b", "work", "read", tenantId: ""));
        Assert.True(_registry.HasAccess("agent-b", "work", "write", tenantId: ""));
    }

    [Fact]
    public void Share_DefaultAgent_RegistersOwnershipOnDemand()
    {
        // The default agent never claims ownership on write, so Share is the only place its
        // permission record can come into being. Without that, share_namespace is unusable on
        // every server started without AGENT_ID.
        var result = _registry.Share("work", AgentIdentity.DefaultAgentId, "agent-b", "read", tenantId: "");

        Assert.Equal("shared", result.Status);
        Assert.True(_registry.HasAccess("agent-b", "work", "read", tenantId: ""));
        Assert.False(_registry.HasAccess("agent-b", "work", "write", tenantId: ""));

        var revoked = _registry.Unshare("work", AgentIdentity.DefaultAgentId, "agent-b", tenantId: "");

        Assert.Equal("unshared", revoked.Status);
        Assert.False(_registry.HasAccess("agent-b", "work", "read", tenantId: ""));
    }

    [Fact]
    public void Share_IdentifiedAgent_CannotTakeOverUnregisteredNamespace()
    {
        var result = _registry.Share("work", "agent-a", "agent-b", "read", tenantId: "");

        Assert.Equal("error_not_found", result.Status);
        Assert.False(_registry.HasAccess("agent-b", "work", "read", tenantId: ""));
        Assert.False(_registry.HasAccess("agent-a", "work", "read", tenantId: ""));
    }

    [Fact]
    public void Share_RejectsSystemNamespaces()
    {
        // HasAccess refuses '_' namespaces ahead of any permission lookup, so a grant on one
        // would be inert. Fail loudly instead of reporting a share that grants nothing.
        var result = _registry.Share("_system_sharing", AgentIdentity.DefaultAgentId, "agent-b", "read", tenantId: "");

        Assert.Equal("error_not_found", result.Status);
        Assert.False(_registry.HasAccess("agent-b", "_system_sharing", requiredLevel: "read", tenantId: ""));
    }

    [Fact]
    public void Share_RejectsInvalidAccessLevel()
    {
        var result = _registry.Share("work", "agent-a", "agent-b", "admin", tenantId: "");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public void Share_NonOwnerCannotShare()
    {
        // Explicitly register agent-a as owner first
        _registry.EnsureOwnership("work", "agent-a", tenantId: "");

        // agent-b tries to share a namespace they don't own
        var result = _registry.Share("work", "agent-b", "agent-c", "read", tenantId: "");

        Assert.Equal("error_not_owner", result.Status);
    }

    [Fact]
    public void Unshare_RevokesAccess()
    {
        _registry.EnsureOwnership("work", "agent-a", tenantId: "");
        _registry.Share("work", "agent-a", "agent-b", "read", tenantId: "");

        var result = _registry.Unshare("work", "agent-a", "agent-b", tenantId: "");

        Assert.Equal("unshared", result.Status);
        Assert.False(_registry.HasAccess("agent-b", "work", requiredLevel: "read", tenantId: ""));
    }

    [Fact]
    public void Unshare_NonOwnerCannotRevoke()
    {
        _registry.EnsureOwnership("work", "agent-a", tenantId: "");
        _registry.Share("work", "agent-a", "agent-b", "read", tenantId: "");

        var result = _registry.Unshare("work", "agent-b", "agent-b", tenantId: "");

        Assert.Equal("error_not_owner", result.Status);
    }

    [Fact]
    public void Unshare_UnknownNamespace_ReturnsNotFound()
    {
        var result = _registry.Unshare("nonexistent", "agent-a", "agent-b", tenantId: "");

        Assert.Equal("error_not_found", result.Status);
    }

    [Fact]
    public void Owner_AlwaysHasFullAccess()
    {
        _registry.EnsureOwnership("private-ns", "agent-a", tenantId: "");

        Assert.True(_registry.HasAccess("agent-a", "private-ns", "read", tenantId: ""));
        Assert.True(_registry.HasAccess("agent-a", "private-ns", "write", tenantId: ""));
    }

    [Fact]
    public void NonOwnerNonShared_HasNoAccess()
    {
        _registry.EnsureOwnership("private-ns", "agent-a", tenantId: "");

        Assert.False(_registry.HasAccess("agent-c", "private-ns", requiredLevel: "read", tenantId: ""));
    }

    [Fact]
    public void EnsureOwnership_DoesNotOverwriteExisting()
    {
        _registry.EnsureOwnership("work", "agent-a", tenantId: "");
        _registry.Share("work", "agent-a", "agent-b", "read", tenantId: "");

        // Second call should not overwrite
        _registry.EnsureOwnership("work", "agent-x", tenantId: "");

        // agent-a is still owner, agent-b still has access
        Assert.True(_registry.HasAccess("agent-a", "work", "write", tenantId: ""));
        Assert.True(_registry.HasAccess("agent-b", "work", "read", tenantId: ""));
    }

    [Fact]
    public void Share_UpgradesAccessLevel()
    {
        _registry.EnsureOwnership("work", "agent-a", tenantId: "");
        _registry.Share("work", "agent-a", "agent-b", "read", tenantId: "");

        Assert.False(_registry.HasAccess("agent-b", "work", "write", tenantId: ""));

        _registry.Share("work", "agent-a", "agent-b", "write", tenantId: "");

        Assert.True(_registry.HasAccess("agent-b", "work", "write", tenantId: ""));
    }

    [Fact]
    public void GetAccessibleNamespaces_ReturnsOwnedAndShared()
    {
        _registry.EnsureOwnership("ns1", "agent-a", tenantId: "");
        _registry.EnsureOwnership("ns2", "agent-a", tenantId: "");
        _registry.EnsureOwnership("ns3", "agent-b", tenantId: "");
        _registry.Share("ns3", "agent-b", "agent-a", "read", tenantId: "");

        var result = _registry.GetAccessibleNamespaces("agent-a", tenantId: "");

        Assert.Equal("agent-a", result.AgentId);
        Assert.Contains("ns1", result.OwnedNamespaces);
        Assert.Contains("ns2", result.OwnedNamespaces);
        Assert.Single(result.SharedNamespaces);
        Assert.Equal("ns3", result.SharedNamespaces[0].Namespace);
    }

    // ── Cross-namespace search tests ──

    [Fact]
    public void SearchMultiple_MergesResultsAcrossNamespaces()
    {
        // Store entries in two different namespaces with identical text for high similarity
        var text = "vector search optimization";
        var vec = _embedding.Embed(text);
        _index.Upsert(new CognitiveEntry("e1", vec, "ns1", text, lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("e2", vec, "ns2", text, lifecycleState: "ltm"));

        var results = _index.SearchMultiple(vec, new[] { "ns1", "ns2" }, queryText: null, tenantId: "", k: 10,
            includeStates: new HashSet<string> { "ltm" });

        Assert.Equal(2, results.Count);
        // Results should come from both namespaces
        var namespaces = results.Select(r => r.Namespace).Distinct().ToList();
        Assert.Contains("ns1", namespaces);
        Assert.Contains("ns2", namespaces);
    }

    [Fact]
    public void SearchMultiple_RespectsNamespaceFilter()
    {
        var text = "unique content only in ns1";
        var vec = _embedding.Embed(text);
        _index.Upsert(new CognitiveEntry("e1", vec, "ns1", text, lifecycleState: "ltm"));

        var query = _embedding.Embed(text);
        var results = _index.SearchMultiple(query, new[] { "ns2" }, queryText: null, tenantId: "", k: 10,
            includeStates: new HashSet<string> { "ltm" });

        // Should not find anything since we only search ns2
        Assert.DoesNotContain(results, r => r.Id == "e1");
    }

    // ── MultiAgentTools integration tests ──

    [Fact]
    public void CrossSearch_FiltersInaccessibleNamespaces()
    {
        _registry.EnsureOwnership("private", "agent-a", tenantId: "");
        var agent = new AgentIdentity("agent-b"); // agent-b has no access

        var text = "test entry";
        var vec = _embedding.Embed(text);
        _index.Upsert(new CognitiveEntry("e1", vec, "private", text, lifecycleState: "ltm"));

        var tools = new MultiAgentTools(_index, _embedding, _metrics, _registry, agent);
        var result = tools.CrossSearch("private", "test query");

        Assert.Equal("Error: no accessible namespaces in the provided list.", result);
    }

    [Fact]
    public void CrossSearch_AllowsAccessibleNamespaces()
    {
        _registry.EnsureOwnership("shared", "agent-a", tenantId: "");
        _registry.Share("shared", "agent-a", "agent-b", "read", tenantId: "");
        var agent = new AgentIdentity("agent-b");

        var text = "shared knowledge about SIMD operations";
        var vec = _embedding.Embed(text);
        _index.Upsert(new CognitiveEntry("e1", vec, "shared", text, lifecycleState: "ltm"));

        var tools = new MultiAgentTools(_index, _embedding, _metrics, _registry, agent);
        // Use exact same text to ensure high similarity with HashEmbeddingService
        var result = tools.CrossSearch("shared", text) as CrossSearchResponse;

        Assert.NotNull(result);
        Assert.Equal(1, result!.NamespacesSearched);
        Assert.True(result.TotalResults >= 1);
    }

    [Fact]
    public void CrossSearch_DefaultAgentAccessesEverything()
    {
        var agent = AgentIdentity.Default;

        var text = "some data in random namespace";
        var vec = _embedding.Embed(text);
        _index.Upsert(new CognitiveEntry("e1", vec, "anyns", text, lifecycleState: "ltm"));

        var tools = new MultiAgentTools(_index, _embedding, _metrics, _registry, agent);
        var result = tools.CrossSearch("anyns", "some data") as CrossSearchResponse;

        Assert.NotNull(result);
        Assert.Equal(1, result!.NamespacesSearched);
    }

    [Fact]
    public void CrossSearch_EmptyNamespaces_ReturnsError()
    {
        var agent = AgentIdentity.Default;
        var tools = new MultiAgentTools(_index, _embedding, _metrics, _registry, agent);

        var result = tools.CrossSearch("", "test query");

        Assert.Equal("Error: namespaces must not be empty.", result);
    }

    [Fact]
    public void CrossSearch_EmptyText_ReturnsError()
    {
        var agent = AgentIdentity.Default;
        var tools = new MultiAgentTools(_index, _embedding, _metrics, _registry, agent);

        var result = tools.CrossSearch("ns1", "");

        Assert.Equal("Error: text must not be empty.", result);
    }

    [Fact]
    public void ShareNamespace_ValidatesEmptyInputs()
    {
        var agent = AgentIdentity.Default;
        var tools = new MultiAgentTools(_index, _embedding, _metrics, _registry, agent);

        Assert.Equal("Error: namespace must not be empty.", tools.ShareNamespace("", "agent-b"));
        Assert.Equal("Error: agentId must not be empty.", tools.ShareNamespace("ns", ""));
    }

    [Fact]
    public void UnshareNamespace_ValidatesEmptyInputs()
    {
        var agent = AgentIdentity.Default;
        var tools = new MultiAgentTools(_index, _embedding, _metrics, _registry, agent);

        Assert.Equal("Error: namespace must not be empty.", tools.UnshareNamespace("", "agent-b"));
        Assert.Equal("Error: agentId must not be empty.", tools.UnshareNamespace("ns", ""));
    }

    [Fact]
    public void ShareNamespace_GrantsAndReturnsResult()
    {
        var agent = new AgentIdentity("agent-a");
        var tools = new MultiAgentTools(_index, _embedding, _metrics, _registry, agent);
        _registry.EnsureOwnership("myns", "agent-a", tenantId: "");

        var result = tools.ShareNamespace("myns", "agent-b", "write") as ShareResult;

        Assert.NotNull(result);
        Assert.Equal("shared", result!.Status);
        Assert.Equal("myns", result.Namespace);
        Assert.Equal("agent-b", result.AgentId);
        Assert.Equal("write", result.AccessLevel);
    }

    [Fact]
    public void UnshareNamespace_RevokesAndReturnsResult()
    {
        var agent = new AgentIdentity("agent-a");
        var tools = new MultiAgentTools(_index, _embedding, _metrics, _registry, agent);
        _registry.EnsureOwnership("myns", "agent-a", tenantId: "");

        tools.ShareNamespace("myns", "agent-b", "read");
        var result = tools.UnshareNamespace("myns", "agent-b") as ShareResult;

        Assert.NotNull(result);
        Assert.Equal("unshared", result!.Status);
    }

    [Fact]
    public void ListShared_ReturnsOnlySharedByOthers()
    {
        // agent-b owns ns-shared and shares it with agent-a
        _registry.EnsureOwnership("ns-shared", "agent-b", tenantId: "");
        _registry.Share("ns-shared", "agent-b", "agent-a", "read", tenantId: "");

        // agent-a also owns its own namespace (should NOT appear in list_shared)
        _registry.EnsureOwnership("ns-own", "agent-a", tenantId: "");

        var agent = new AgentIdentity("agent-a");
        var tools = new MultiAgentTools(_index, _embedding, _metrics, _registry, agent);

        var result = tools.ListShared() as IReadOnlyList<NamespacePermission>;

        Assert.NotNull(result);
        Assert.Contains(result, p => p.Namespace == "ns-shared");
        Assert.DoesNotContain(result, p => p.Namespace == "ns-own");
    }

    [Fact]
    public void WhoAmI_ReturnsAgentIdentity()
    {
        var agent = new AgentIdentity("test-agent-42");
        var tools = new MultiAgentTools(_index, _embedding, _metrics, _registry, agent);

        var result = tools.WhoAmI() as WhoAmIResult;

        Assert.NotNull(result);
        Assert.Equal("test-agent-42", result!.AgentId);
    }

    [Fact]
    public void AgentIdentity_DefaultIsDefault()
    {
        Assert.True(AgentIdentity.Default.IsDefault);
        Assert.Equal("default", AgentIdentity.Default.AgentId);
    }

    [Fact]
    public void AgentIdentity_NonDefaultIsNotDefault()
    {
        var agent = new AgentIdentity("agent-x");
        Assert.False(agent.IsDefault);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }
}
