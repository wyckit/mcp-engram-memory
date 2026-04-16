using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class MultiAgentToolsTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly NamespaceRegistry _registry;
    private readonly MetricsCollector _metrics;
    private readonly MultiAgentTools _tools;

    private sealed class StubEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    private readonly StubEmbeddingService _embedding;

    public MultiAgentToolsTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"multiagent_tools_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _embedding = new StubEmbeddingService();
        _metrics = new MetricsCollector();
        _registry = new NamespaceRegistry(_index, _embedding);
        _tools = new MultiAgentTools(_index, _embedding, _metrics, _registry, AgentIdentity.Default);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    // ── CrossSearch ──

    [Fact]
    public void CrossSearch_ValidQuery_ReturnsResults()
    {
        var vec = _embedding.Embed("test content");
        _index.Upsert(new CognitiveEntry("e1", vec, "ns1", "first namespace entry", lifecycleState: "stm"));
        _index.Upsert(new CognitiveEntry("e2", vec, "ns2", "second namespace entry", lifecycleState: "stm"));

        var result = _tools.CrossSearch("ns1,ns2", "test content") as CrossSearchResponse;

        Assert.NotNull(result);
        Assert.Equal(2, result!.NamespacesSearched);
        Assert.True(result.TotalResults >= 2);
    }

    [Fact]
    public void CrossSearch_EmptyNamespaces_ReturnsError()
    {
        var result = _tools.CrossSearch("", "test query");

        Assert.Equal("Error: namespaces must not be empty.", result);
    }

    [Fact]
    public void CrossSearch_EmptyText_ReturnsError()
    {
        var result = _tools.CrossSearch("ns1", "");

        Assert.Equal("Error: text must not be empty.", result);
    }

    [Fact]
    public void CrossSearch_SingleNamespace_Works()
    {
        var vec = _embedding.Embed("single ns content");
        _index.Upsert(new CognitiveEntry("e1", vec, "solo", "single namespace content", lifecycleState: "ltm"));

        var result = _tools.CrossSearch("solo", "single ns content") as CrossSearchResponse;

        Assert.NotNull(result);
        Assert.Equal(1, result!.NamespacesSearched);
        Assert.True(result.TotalResults >= 1);
    }

    [Fact]
    public void CrossSearch_NamespaceFiltering_ReturnsCorrectNamespaces()
    {
        var vec = _embedding.Embed("content");
        _index.Upsert(new CognitiveEntry("e1", vec, "alpha", "alpha content", lifecycleState: "stm"));
        _index.Upsert(new CognitiveEntry("e2", vec, "beta", "beta content", lifecycleState: "stm"));
        _index.Upsert(new CognitiveEntry("e3", vec, "gamma", "gamma content", lifecycleState: "stm"));

        // Search only alpha and gamma, excluding beta
        var result = _tools.CrossSearch("alpha,gamma", "content") as CrossSearchResponse;

        Assert.NotNull(result);
        Assert.Equal(2, result!.NamespacesSearched);
        // Results should come from alpha and gamma only
        var namespaces = result.Results.Select(r => r.Namespace).Distinct().ToList();
        Assert.DoesNotContain("beta", namespaces);
    }

    // ── ShareNamespace ──

    [Fact]
    public void ShareNamespace_ValidInput_SharesAccess()
    {
        var agent = new AgentIdentity("agent-owner");
        var tools = new MultiAgentTools(_index, _embedding, _metrics, _registry, agent);

        var result = tools.ShareNamespace("myns", "agent-reader", "read") as ShareResult;

        Assert.NotNull(result);
        Assert.Equal("shared", result!.Status);
        Assert.Equal("myns", result.Namespace);
        Assert.Equal("agent-reader", result.AgentId);
        Assert.Equal("read", result.AccessLevel);
    }

    [Fact]
    public void ShareNamespace_EmptyNamespace_ReturnsError()
    {
        var result = _tools.ShareNamespace("", "agent-b");

        Assert.Equal("Error: namespace must not be empty.", result);
    }

    [Fact]
    public void ShareNamespace_EmptyAgentId_ReturnsError()
    {
        var result = _tools.ShareNamespace("ns1", "");

        Assert.Equal("Error: agentId must not be empty.", result);
    }

    // ── UnshareNamespace ──

    [Fact]
    public void UnshareNamespace_ValidInput_RevokesAccess()
    {
        var agent = new AgentIdentity("agent-owner");
        var tools = new MultiAgentTools(_index, _embedding, _metrics, _registry, agent);

        // First share, then unshare
        tools.ShareNamespace("myns", "agent-reader", "read");
        var result = tools.UnshareNamespace("myns", "agent-reader") as ShareResult;

        Assert.NotNull(result);
        Assert.Equal("unshared", result!.Status);
    }

    [Fact]
    public void UnshareNamespace_EmptyNamespace_ReturnsError()
    {
        var result = _tools.UnshareNamespace("", "agent-b");

        Assert.Equal("Error: namespace must not be empty.", result);
    }

    // ── ListShared ──

    [Fact]
    public void ListShared_ReturnsOnlySharedByOthers()
    {
        // agent-owner owns a namespace and shares it with agent-lister
        _registry.EnsureOwnership("shared-ns", "agent-owner");
        _registry.Share("shared-ns", "agent-owner", "agent-lister", "read");

        // agent-lister owns its own namespace (should NOT appear)
        _registry.EnsureOwnership("own-ns", "agent-lister");

        var agent = new AgentIdentity("agent-lister");
        var tools = new MultiAgentTools(_index, _embedding, _metrics, _registry, agent);

        var result = tools.ListShared() as IReadOnlyList<NamespacePermission>;

        Assert.NotNull(result);
        Assert.Contains(result, p => p.Namespace == "shared-ns");
        Assert.DoesNotContain(result, p => p.Namespace == "own-ns");
    }

    // ── WhoAmI ──

    [Fact]
    public void WhoAmI_ReturnsIdentity()
    {
        var agent = new AgentIdentity("test-agent-99");
        var tools = new MultiAgentTools(_index, _embedding, _metrics, _registry, agent);

        var result = tools.WhoAmI() as WhoAmIResult;

        Assert.NotNull(result);
        Assert.Equal("test-agent-99", result!.AgentId);
    }
}
