using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

/// <summary>
/// Explicit multi-tenancy data-leak isolation coverage (engram T5).
///
/// ParallelAgentTests/MultiAgentTests cover permission grant/revoke and concurrency, but none
/// assert that a secret written by one agent into namespace X can NEVER surface for another
/// agent querying ONLY namespace Y. This class closes that gap with a uniquely-tokened secret
/// and asserts zero leakage across vector search, hybrid search, and permission-filtered
/// cross_search — plus the inverse direction.
///
/// Visibility is asserted via GetAllInNamespace (NOT cross_search): cross_search has a
/// per-namespace similarity floor that can drop entries and yield a false PASS, so the
/// authoritative "no entry leaked" check enumerates the namespace directly. cross_search is
/// additionally checked as the permission-aware tool surface a real agent would call.
///
/// Owns an isolated tempdir + its own service graph, so it is safe under parallel collection
/// execution.
/// </summary>
public class CrossNamespaceIsolationTests : IDisposable
{
    private const string SecretToken = "SECRET-TOKEN-7Q9Z";
    private const string NsX = "agent-a-private-x";
    private const string NsY = "agent-b-private-y";

    private readonly string _dataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly HashEmbeddingService _embedding;
    private readonly MetricsCollector _metrics;
    private readonly NamespaceRegistry _registry;

    public CrossNamespaceIsolationTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"xns_isolation_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_dataPath);
        _index = new CognitiveIndex(_persistence);
        _embedding = new HashEmbeddingService();
        _metrics = new MetricsCollector();
        _registry = new NamespaceRegistry(_index, _embedding);
    }

    private CognitiveEntry MakeEntry(string id, string ns, string text, string state = "ltm")
        => new(id, _embedding.Embed(text), ns, text, lifecycleState: state);

    private MultiAgentTools ToolsFor(string agentId)
        => new(_index, _embedding, _metrics, _registry, new AgentIdentity(agentId));

    private static bool ContainsSecret(string? text)
        => text is not null && text.Contains(SecretToken, StringComparison.Ordinal);

    [Fact]
    public void CrossNamespaceIsolation_NoLeak()
    {
        // Agent_A owns namespace X and writes a uniquely-tokened secret there.
        _registry.EnsureOwnership(NsX, "agent-a");
        var secretText = $"Production database root password is {SecretToken} do not share";
        _index.Upsert(MakeEntry("a-secret", NsX, secretText));

        // Agent_B owns namespace Y and writes unrelated content there.
        _registry.EnsureOwnership(NsY, "agent-b");
        var benignText = "Quarterly planning notes about the sprint roadmap and team capacity";
        _index.Upsert(MakeEntry("b-note", NsY, benignText));

        // The query reuses the secret text verbatim so that — under HashEmbeddingService —
        // the query vector is a near-perfect match for A's secret. If any leak path existed,
        // this is the query most likely to surface it.
        var leakQueryVector = _embedding.Embed(secretText);
        var allStates = new HashSet<string> { "stm", "ltm" };

        // ── 1. Authoritative visibility: enumerate namespace Y. The secret must not exist. ──
        var nsYEntries = _index.GetAllInNamespace(NsY);
        Assert.Single(nsYEntries);
        Assert.DoesNotContain(nsYEntries, e => ContainsSecret(e.Text));
        Assert.DoesNotContain(nsYEntries, e => e.Id == "a-secret");

        // ── 2. Vector search scoped to Y must never return A's secret. ──
        var vectorHits = _index.Search(leakQueryVector, NsY, k: 50, includeStates: allStates);
        Assert.DoesNotContain(vectorHits, r => ContainsSecret(r.Text));
        Assert.DoesNotContain(vectorHits, r => r.Id == "a-secret");

        // ── 3. Hybrid (BM25 + vector) search scoped to Y must never return A's secret. ──
        // BM25 keyword matching on the literal token is the most aggressive leak vector.
        var hybridHits = _index.HybridSearch(
            leakQueryVector, secretText, NsY, k: 50, includeStates: allStates);
        Assert.DoesNotContain(hybridHits, r => ContainsSecret(r.Text));
        Assert.DoesNotContain(hybridHits, r => r.Id == "a-secret");

        // ── 4. Permission-filtered cross_search: Agent_B searching ONLY Y. ──
        // Agent_B has no access to X, so even if it named X the registry would strip it.
        var agentBTools = ToolsFor("agent-b");
        var crossY = agentBTools.CrossSearch(NsY, secretText, k: 50, hybrid: true) as CrossSearchResponse;
        Assert.NotNull(crossY);
        Assert.Equal(1, crossY!.NamespacesSearched);
        Assert.DoesNotContain(crossY.Results, r => ContainsSecret(r.Text));
        Assert.DoesNotContain(crossY.Results, r => r.Id == "a-secret");

        // ── 5. Defense in depth: Agent_B explicitly requesting X is denied (not silently leaked). ──
        // X is owned by agent-a with no grant to agent-b, so the only namespace is inaccessible.
        var crossDeniedX = agentBTools.CrossSearch(NsX, secretText, k: 50, hybrid: true);
        Assert.Equal("Error: no accessible namespaces in the provided list.", crossDeniedX);

        // ── 6. Inverse direction: Agent_A querying ONLY X must not see Agent_B's content. ──
        var benignQueryVector = _embedding.Embed(benignText);
        var nsXEntries = _index.GetAllInNamespace(NsX);
        Assert.Single(nsXEntries);
        Assert.DoesNotContain(nsXEntries, e => e.Id == "b-note");
        Assert.DoesNotContain(nsXEntries, e => e.Text == benignText);

        var inverseVectorHits = _index.Search(benignQueryVector, NsX, k: 50, includeStates: allStates);
        Assert.DoesNotContain(inverseVectorHits, r => r.Id == "b-note");

        var inverseHybridHits = _index.HybridSearch(
            benignQueryVector, benignText, NsX, k: 50, includeStates: allStates);
        Assert.DoesNotContain(inverseHybridHits, r => r.Id == "b-note");

        // ── Sanity: each agent CAN see its own content in its own namespace (the isolation
        // is not a degenerate "everything is empty" pass). ──
        var agentATools = ToolsFor("agent-a");
        var crossX = agentATools.CrossSearch(NsX, secretText, k: 50, hybrid: true) as CrossSearchResponse;
        Assert.NotNull(crossX);
        Assert.Contains(crossX!.Results, r => r.Id == "a-secret" && ContainsSecret(r.Text));
        Assert.Contains(_index.GetAllInNamespace(NsX), e => e.Id == "a-secret");
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }
}
