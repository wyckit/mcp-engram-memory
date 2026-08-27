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

    // A second namespace Alice owns. Contradictions legitimately span namespaces, so the
    // over-correction control needs two readable ones to prove the feature still works.
    private const string AliceOtherNs = "alice-notes";
    private const string ArchivedSecret = "the archived launch code is hunter2";

    // Namespaces Bob genuinely owns. An identified principal inherits nothing from an
    // unregistered namespace (HasAccess returns false when no permission record exists), so
    // Bob has to be a real owner here or the CanRead gate would deny for the wrong reason and
    // the test would pass without ever reaching the code under test.
    private const string BobNs = "bob-ns";
    private const string BobQuietNs = "bob-quiet";

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

    private IntelligenceTools IntelligenceAs(string agentId) => new(
        _index, _graph, _embedding, new AccretionScanner(_index), _clusters,
        new LifecycleEngine(_index, _persistence), As(agentId));

    private LifecycleTools LifecycleAs(string agentId) => new(
        new LifecycleEngine(_index, _persistence), _embedding, _index, As(agentId));

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

    // ── find_contradictions: authorize the endpoint, not the namespace that was asked for ──
    //
    // KnowledgeGraph.GetContradictions matches an edge when EITHER endpoint lives in the queried
    // namespace, so the CanRead(ns) gate at the top of the tool authorizes only half of what the
    // tool is about to disclose. The opposite endpoint can be any namespace in the tenant,
    // including one the caller was never granted. These drive it as Bob, an identified second
    // agent, because as the default agent HasAccess short-circuits to unrestricted and the leak
    // is invisible.

    [Fact]
    public void FindContradictions_DoesNotReturnCrossNamespaceEndpointsOrCountThem()
    {
        // Bob owns bob-ns, so the tool's namespace gate legitimately opens. The edge is anchored
        // here by its SOURCE; its TARGET is Alice's private entry, which the gate said nothing
        // about and which the tool previously handed straight back.
        _index.Upsert(new CognitiveEntry("bob-claim", _embedding.Embed("bobs claim"), BobNs, "bobs claim"));
        _registry.EnsureOwnership(BobNs, "bob");
        _graph.AddEdge(new GraphEdge("bob-claim", "alice-secret", "contradicts", 0.9f));

        // A second namespace Bob owns, with content but no contradiction edge at all: the
        // genuine "there is nothing to report here" reply, used as the indistinguishability
        // reference below.
        _index.Upsert(new CognitiveEntry("bob-quiet-note", _embedding.Embed("unrelated"), BobQuietNs, "unrelated note"));
        _registry.EnsureOwnership(BobQuietNs, "bob");

        var bob = IntelligenceAs("bob");

        var withheld = (ContradictionResult)bob.FindContradictions(BobNs);
        var absent = (ContradictionResult)bob.FindContradictions(BobQuietNs);

        Assert.DoesNotContain("hunter2", Json(withheld));
        Assert.Empty(withheld.Contradictions);

        // The count is the half a row filter alone misses. Reporting "1 graph contradiction"
        // while returning zero rows is a counting oracle: it confirms that an entry Bob may not
        // see exists and disagrees with one of his. The tally has to be computed after the
        // filter, so assert the number and not merely the absence of rows.
        Assert.Equal(0, withheld.GraphEdgeCount);

        // And the whole reply must be identical to the no-such-edge reply. Asserting only
        // "nothing was returned" would still pass against an implementation that leaked
        // existence through some other field of the result; asserting equality pins the shape.
        Assert.Equal(Json(absent), Json(withheld));
    }

    [Fact]
    public void FindContradictions_FiltersWhenThePrivateEndpointIsTheEdgeSource()
    {
        // The same disclosure with the edge reversed. Because GetContradictions anchors on
        // either endpoint, an implementation that guards only the target - the natural half to
        // write first, since the caller's own entry is usually the source - still leaks here.
        // Both directions need covering or the fix is half a fix.
        _index.Upsert(new CognitiveEntry("bob-claim", _embedding.Embed("bobs claim"), BobNs, "bobs claim"));
        _registry.EnsureOwnership(BobNs, "bob");
        _graph.AddEdge(new GraphEdge("alice-secret", "bob-claim", "contradicts", 0.9f));

        var bob = IntelligenceAs("bob");

        var result = (ContradictionResult)bob.FindContradictions(BobNs);
        var json = Json(result);

        Assert.DoesNotContain("hunter2", json);
        // The id alone is disclosure even with the text stripped - it names an entry Bob was
        // never granted and confirms it contradicts his own.
        Assert.DoesNotContain("alice-secret", json);
        Assert.Empty(result.Contradictions);
        Assert.Equal(0, result.GraphEdgeCount);
    }

    [Fact]
    public void FindContradictions_OwnerStillSeesBothEndpoints()
    {
        // Over-correction control. The cheap wrong fix is "drop any endpoint outside the queried
        // namespace", which quietly deletes the feature - a contradiction spanning two of your
        // own namespaces is precisely the kind worth surfacing. The rule is READABILITY of the
        // resolved entry, not sameness of namespace, and this test is what tells those apart.
        _index.Upsert(new CognitiveEntry(
            "alice-note", _embedding.Embed("rotated"), AliceOtherNs, "the launch code was rotated"));
        _registry.EnsureOwnership(AliceOtherNs, "alice");
        _graph.AddEdge(new GraphEdge("alice-secret", "alice-note", "contradicts", 0.9f));

        var alice = IntelligenceAs("alice");

        var result = (ContradictionResult)alice.FindContradictions(AliceNs);

        Assert.Single(result.Contradictions);
        Assert.Equal(1, result.GraphEdgeCount);

        var pair = result.Contradictions[0];
        Assert.Equal("graph_edge", pair.Source);
        Assert.Equal("alice-secret", pair.EntryA.Id);
        Assert.Equal("alice-note", pair.EntryB.Id);
        // Deliberately two DIFFERENT namespaces: the cross-namespace edge survives whenever the
        // caller may read both ends.
        Assert.Equal(AliceNs, pair.EntryA.Namespace);
        Assert.Equal(AliceOtherNs, pair.EntryB.Namespace);
        Assert.Contains("hunter2", Json(result));
    }

    [Fact]
    public void DeepRecall_ByReadOnlyGrantee_DoesNotResurrectOwnersArchivedEntry()
    {
        // deep_recall is read-shaped but writes: it promotes high-scoring archived entries back
        // to STM on the caller's behalf. Read access buys the rows - the namespace gate above
        // (DeepRecall_DoesNotReturnAnotherAgentsEntries) is unchanged and still grants the
        // search to a grantee. It must not also buy the promotion, which mutates the OWNER's
        // entry. Authorize at the verb actually performed, not the verb the tool is named after.
        _index.Upsert(new CognitiveEntry(
            "alice-archived", _embedding.Embed(ArchivedSecret), AliceNs, ArchivedSecret,
            lifecycleState: "archived"));
        _registry.Share(AliceNs, "alice", "bob", "read");

        var bob = LifecycleAs("bob");

        var result = bob.DeepRecall(
            AliceNs, vector: [0.5f, 0.5f], minScore: 0f, resurrectionThreshold: 0.5f);
        var results = Assert.IsAssignableFrom<IReadOnlyList<CognitiveSearchResult>>(result);

        // The rows still come back, unchanged in content and count. Withholding the write must
        // not degrade into withholding the result, or a grantee could read their own permission
        // level off the size of the reply.
        var row = Assert.Single(results.Where(r => r.Id == "alice-archived"));
        Assert.Equal("archived", row.LifecycleState);

        // ...and nothing actually moved. This is the assertion the fix exists for: the uniform
        // stub embedding scores this entry at 1.0, well over the 0.5 resurrection threshold, so
        // before the fix the read-only grantee flipped Alice's archived entry to stm.
        Assert.Equal("archived", _index.Get("alice-archived", AliceNs)!.LifecycleState);
    }
}
