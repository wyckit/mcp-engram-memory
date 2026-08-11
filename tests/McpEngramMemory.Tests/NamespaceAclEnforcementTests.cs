using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

/// <summary>
/// End-to-end namespace ACL enforcement through the MCP tool surface.
///
/// The pre-existing CrossNamespaceIsolationTests asserted isolation through raw
/// CognitiveIndex calls and cross_search — the one tool that already checked access. Every
/// other tool went unexercised, which is precisely why it went unnoticed that
/// NamespaceRegistry.HasAccess was called in exactly one place in the whole server and that
/// nothing ever registered ownership, leaving share_namespace protecting nothing.
///
/// These tests drive the actual tools as two distinct, honestly-identified agents. They are
/// not about a malicious agent lying about its AGENT_ID — that is a known, documented
/// limitation. They are about whether a cooperating agent can reach another agent's data.
/// </summary>
public class NamespaceAclEnforcementTests : IDisposable
{
    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 2;
        // Everything embeds identically, so any leak is a permission failure and never a
        // similarity artifact: if an entry is reachable at all, it will score as a hit.
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly NamespaceRegistry _registry;
    private readonly StubEmbedding _embedding = new();

    private const string AliceNs = "alice-private";

    public NamespaceAclEnforcementTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"acl_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 10);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
        _registry = new NamespaceRegistry(_index, _embedding);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    private CoreMemoryTools Core(string agentId) => new(
        _index, new PhysicsEngine(), _embedding, new MetricsCollector(), _graph,
        new QueryExpander(), new SpreadingActivationService(_index, _graph, _clusters),
        _clusters, _registry, new AgentIdentity(agentId));

    private AdminTools Admin(string agentId) => new(
        _index, _graph, _clusters, _persistence, _registry, new AgentIdentity(agentId));

    private CompositeTools Composite(string agentId) => new(
        _index, _embedding, _graph,
        new LifecycleEngine(_index, _persistence),
        new ExpertDispatcher(_index, _embedding),
        new MetricsCollector(),
        new SpectralRetrievalReranker(new MemoryDiffusionKernel(_index, _graph)),
        _registry, new AgentIdentity(agentId));

    /// <summary>Alice writes a secret, which also claims ownership of the namespace.</summary>
    private void AliceStoresSecret()
    {
        var result = Core("alice").StoreMemory("alice-secret", AliceNs, "the launch code is hunter2");
        Assert.Contains("Stored entry", result);

        // Ownership registration is what makes every later check meaningful: HasAccess treats
        // an unregistered namespace as open, so without this the gates are all no-ops.
        Assert.True(_registry.HasAccess("alice", AliceNs));
        Assert.False(_registry.HasAccess("bob", AliceNs));
    }

    [Fact]
    public void Write_ClaimsOwnership_AndBlocksOtherAgents()
    {
        AliceStoresSecret();

        var bobWrite = Core("bob").StoreMemory("bob-entry", AliceNs, "bob was here");
        Assert.Contains("owned by another agent", bobWrite);
        Assert.Null(_index.Get("bob-entry"));
    }

    [Fact]
    public void SearchMemory_DoesNotReturnAnotherAgentsEntries()
    {
        AliceStoresSecret();

        var bobSearch = Core("bob").SearchMemory(ns: AliceNs, text: "launch code");

        Assert.DoesNotContain("hunter2", System.Text.Json.JsonSerializer.Serialize(bobSearch));
    }

    [Fact]
    public void GetMemory_DoesNotDiscloseAnotherAgentsEntryByGuessedId()
    {
        AliceStoresSecret();

        // get_memory resolves by id across every namespace, so guessing or observing an id
        // was previously enough to read the full text and metadata.
        var bobGet = Admin("bob").GetMemory("alice-secret");

        var json = System.Text.Json.JsonSerializer.Serialize(bobGet);
        Assert.DoesNotContain("hunter2", json);
        // Reply must be shaped like a genuine miss so it is not an existence oracle.
        Assert.Contains("not found", json);
    }

    [Fact]
    public void DeleteMemory_CannotDestroyAnotherAgentsEntry()
    {
        AliceStoresSecret();
        _graph.AddEdge(new GraphEdge("alice-secret", "alice-secret-2", "similar_to"));

        var bobDelete = Core("bob").DeleteMemory("alice-secret");

        Assert.Contains("not found", bobDelete);
        Assert.NotNull(_index.Get("alice-secret"));
        // The edge cascade used to run before the existence check, so an unauthorized caller
        // could strip an entry's edges even while failing to delete it.
        Assert.NotEmpty(_graph.GetEdgesForEntry("alice-secret"));
    }

    [Fact]
    public void RecallBroadcast_DoesNotReachAnotherAgentsNamespace()
    {
        AliceStoresSecret();

        // ns omitted: the broadcast strategy searches every namespace in the store.
        var bobRecall = Composite("bob").Recall(query: "launch code");

        Assert.DoesNotContain("hunter2", System.Text.Json.JsonSerializer.Serialize(bobRecall));
    }

    [Fact]
    public void CognitiveStats_DoesNotDiscloseAnotherAgentsNamespaceNames()
    {
        AliceStoresSecret();

        var stats = Admin("bob").CognitiveStats();

        Assert.DoesNotContain(AliceNs, stats.Namespaces);
    }

    [Fact]
    public void SharedNamespace_BecomesReadableByTheGrantee()
    {
        AliceStoresSecret();
        _registry.Share(AliceNs, "alice", "bob", "read");

        var bobSearch = Core("bob").SearchMemory(ns: AliceNs, text: "launch code");

        // The whole point of the ACL: sharing must actually grant access, not just record it.
        Assert.Contains("hunter2", System.Text.Json.JsonSerializer.Serialize(bobSearch));
    }

    [Fact]
    public void DefaultAgent_IsUnaffected()
    {
        // A server with no AGENT_ID set runs as the default identity, and must behave exactly
        // as before: no ownership records created, nothing gated. This is the single-user case
        // and by far the most common deployment.
        var tools = Core(AgentIdentity.DefaultAgentId);
        Assert.Contains("Stored entry", tools.StoreMemory("d1", "default-ns", "ordinary note"));

        Assert.True(_registry.HasAccess("someone-else", "default-ns"),
            "default-agent writes must not claim ownership, or setting AGENT_ID later would lock the operator out of their own data");
    }
}
