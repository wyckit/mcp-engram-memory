using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

/// <summary>
/// ACL enforcement for the standard- and full-profile tools.
///
/// These exist because gating without tests is how the original bug survived: the previous
/// isolation suite exercised only the single tool that already checked access, so nobody
/// noticed the other thirty did not. Every other test in this repo runs as the DEFAULT agent,
/// which <c>HasAccess</c> short-circuits to full access — so those tests prove gating is
/// invisible to a single-identity server, and prove nothing whatsoever about whether it
/// blocks anyone. These drive the tools as a second, honestly-identified agent.
///
/// Focused on the tools that return entry content, where a miss actually discloses data.
/// </summary>
public class StandardProfileAclTests : IDisposable
{
    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 2;
        // Uniform embedding: anything reachable scores as a hit, so a leak can only be a
        // permission failure and never a similarity artifact.
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
    private const string Secret = "the launch code is hunter2";

    public StandardProfileAclTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"acl_std_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 10);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
        _registry = new NamespaceRegistry(_index, _embedding);

        // Alice owns the namespace; ownership is what makes any later check meaningful,
        // after the first identified write atomically claims the empty namespace.
        _index.Upsert(new CognitiveEntry("alice-secret", _embedding.Embed(Secret), AliceNs, Secret));
        _registry.EnsureOwnership(AliceNs, "alice");
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    private NamespaceAccess As(string agentId) => new(_registry, new AgentIdentity(agentId));

    private static string Json(object? o) => System.Text.Json.JsonSerializer.Serialize(o);

    [Fact]
    public void DeepRecall_DoesNotReturnAnotherAgentsEntries()
    {
        var bob = new LifecycleTools(
            new LifecycleEngine(_index, _persistence), _embedding, _index, As("bob"));

        var result = bob.DeepRecall(AliceNs, "launch code");

        Assert.DoesNotContain("hunter2", Json(result));
    }

    [Fact]
    public void SpectralRecall_DoesNotReturnAnotherAgentsEntries()
    {
        var bob = new SpectralRetrievalTools(
            _index, _embedding,
            new SpectralRetrievalReranker(new MemoryDiffusionKernel(_index, _graph)),
            As("bob"));

        var result = bob.SpectralRecall(AliceNs, "launch code");

        Assert.DoesNotContain("hunter2", Json(result));
    }

    [Fact]
    public void GraphSnapshot_ExcludesAnotherAgentsNodesEdgesAndNamespaceNames()
    {
        // get_graph_snapshot exports the whole graph in one call, so it is the widest single
        // read in the server: node text, edge topology, and namespace names all at once.
        _index.Upsert(new CognitiveEntry("alice-2", _embedding.Embed("second"), AliceNs, "second secret"));
        _graph.AddEdge(new GraphEdge("alice-secret", "alice-2", "similar_to"));

        var bob = new VisualizationTools(_index, _graph, _clusters, As("bob"));

        var json = Json(bob.GetGraphSnapshot());

        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("alice-secret", json);
        // Edges must go too - an edge between two hidden nodes still leaks their ids.
        Assert.DoesNotContain("alice-2", json);
        // And the namespace name itself, which is disclosure even with no content attached.
        Assert.DoesNotContain(AliceNs, json);
    }

    [Fact]
    public void GetNeighbors_DoesNotTraverseIntoAnotherAgentsNamespace()
    {
        // Edges are global and carry no namespace, so traversal is a natural way to walk out
        // of the namespace a caller is entitled to.
        _index.Upsert(new CognitiveEntry("bob-entry", _embedding.Embed("bobs note"), "bob-ns", "bobs note"));
        _graph.AddEdge(new GraphEdge("bob-entry", "alice-secret", "cross_reference"));

        var bob = new GraphTools(
            _graph, new AutoLinkScanner(_index, _graph, new DuplicateDetector()), _index, As("bob"));

        var json = Json(bob.GetNeighbors("bob-entry"));

        Assert.DoesNotContain("hunter2", json);
    }

    [Fact]
    public void LinkMemories_CannotLinkIntoAnotherAgentsNamespace()
    {
        _index.Upsert(new CognitiveEntry("bob-entry", _embedding.Embed("bobs note"), "bob-ns", "bobs note"));

        var bob = new GraphTools(
            _graph, new AutoLinkScanner(_index, _graph, new DuplicateDetector()), _index, As("bob"));

        var result = bob.LinkMemories("bob-entry", "alice-secret", "similar_to");

        Assert.DoesNotContain("Linked", Json(result));
        Assert.Empty(_graph.GetEdgesForEntry("alice-secret"));
    }

    [Fact]
    public void OwnerAndDefaultAgentAreUnaffected()
    {
        // Positive control: the gates must be invisible to the owner and to a server running
        // without an AGENT_ID (the common single-user deployment). Uses the graph snapshot
        // rather than spectral_recall - the latter needs a diffusion basis, which this small
        // fixture cannot build, so it returns empty for everyone and would prove nothing.
        foreach (var identity in new[] { "alice", AgentIdentity.DefaultAgentId })
        {
            var tools = new VisualizationTools(_index, _graph, _clusters, As(identity));
            var json = Json(tools.GetGraphSnapshot());

            Assert.Contains("hunter2", json);
            Assert.Contains(AliceNs, json);
        }
    }
}
