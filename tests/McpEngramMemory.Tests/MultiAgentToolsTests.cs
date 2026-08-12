using System.Text.Json;
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

    /// <summary>Serializes a value the way an MCP client would send it over the wire.</summary>
    private static JsonElement Wire<T>(T value) => JsonSerializer.SerializeToElement(value);

    /// <summary>
    /// `cross_search` names its namespace list `namespaces` (plural, because it is a list),
    /// while every other tool takes a single `ns`. Both names are correct and the pair is
    /// inconsistent, so callers reach for `ns` from habit. Renaming would break every
    /// existing caller of a published tool, so `ns` is accepted as an alias instead — and
    /// the value may be an array, a comma-separated string, or a single namespace.
    /// </summary>
    [Fact]
    public void CrossSearch_AcceptsNsAliasAndAnyListShape()
    {
        _index.Upsert(new CognitiveEntry("a1", [0.5f, 0.5f], "alpha", "shared topic text"));
        _index.Upsert(new CognitiveEntry("b1", [0.5f, 0.5f], "beta", "shared topic text"));

        var viaNamespaces = _tools.CrossSearch(Wire("alpha,beta"), "shared topic") as CrossSearchResponse;
        var viaNsAlias    = _tools.CrossSearch(null, "shared topic", ns: Wire("alpha,beta")) as CrossSearchResponse;
        var viaArray      = _tools.CrossSearch(Wire(new[] { "alpha", "beta" }), "shared topic") as CrossSearchResponse;
        var viaSingle     = _tools.CrossSearch(Wire("alpha"), "shared topic") as CrossSearchResponse;

        Assert.NotNull(viaNamespaces);
        Assert.NotNull(viaNsAlias);
        Assert.NotNull(viaArray);
        Assert.NotNull(viaSingle);

        // All three list shapes must resolve to the same two namespaces.
        Assert.Equal(viaNamespaces!.Results.Count, viaNsAlias!.Results.Count);
        Assert.Equal(viaNamespaces.Results.Count, viaArray!.Results.Count);
        Assert.True(viaSingle!.Results.Count <= viaNamespaces.Results.Count);
    }

    [Fact]
    public void CrossSearch_RejectsBothNamespacesAndNs()
    {
        var result = _tools.CrossSearch(Wire("alpha"), "q", ns: Wire("beta"));
        Assert.Contains("not both", Assert.IsType<string>(result));
    }

    /// <summary>
    /// An omitted optional parameter does not always reach the tool as C# null: depending on the
    /// client and SDK binding it can arrive as a present JsonElement holding JSON null. The
    /// both-supplied guard originally tested nullability, so it fired on calls that passed only
    /// `namespaces` - rejecting the documented usage and making cross_search unreachable over MCP
    /// while every in-process test, which passes literal null, kept passing.
    /// </summary>
    [Fact]
    public void CrossSearch_TreatsJsonNullAliasAsOmitted()
    {
        _index.Upsert(new CognitiveEntry("a1", [0.5f, 0.5f], "alpha", "shared topic text"));

        var jsonNull = Wire<string?>(null);
        Assert.Equal(JsonValueKind.Null, jsonNull.ValueKind);

        var viaNamespaces = _tools.CrossSearch(Wire("alpha"), "shared topic", ns: jsonNull);
        var viaAlias = _tools.CrossSearch(jsonNull, "shared topic", ns: Wire("alpha"));

        Assert.IsType<CrossSearchResponse>(viaNamespaces);
        Assert.IsType<CrossSearchResponse>(viaAlias);
        Assert.Equal(
            ((CrossSearchResponse)viaNamespaces).Results.Count,
            ((CrossSearchResponse)viaAlias).Results.Count);
    }

    /// <summary>Both genuinely absent is still an empty-list error, not a both-supplied error.</summary>
    [Fact]
    public void CrossSearch_JsonNullForBothReportsEmptyNamespaces()
    {
        var jsonNull = Wire<string?>(null);

        var result = Assert.IsType<string>(_tools.CrossSearch(jsonNull, "q", ns: jsonNull));

        Assert.DoesNotContain("not both", result);
        Assert.Contains("must not be empty", result);
    }

    [Fact]
    public void CrossSearch_RejectsUnusableShape()
    {
        var result = _tools.CrossSearch(Wire(new { bad = "shape" }), "q");
        Assert.Contains("namespaces must be", Assert.IsType<string>(result));
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
        _registry.EnsureOwnership("myns", "agent-owner");

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
        _registry.EnsureOwnership("myns", "agent-owner");

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
