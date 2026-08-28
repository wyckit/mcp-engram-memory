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

    private CoreMemoryTools Core(string agentId, string tenantId = "") => new(
        _index, new PhysicsEngine(), _embedding, new MetricsCollector(), _graph,
        new QueryExpander(), new SpreadingActivationService(_index, _graph, _clusters),
        _clusters, _registry, new PrincipalContext(tenantId, agentId));

    private AdminTools Admin(string agentId, string tenantId = "") => new(
        _index, _graph, _clusters, _persistence, _registry, new PrincipalContext(tenantId, agentId));

    // CompositeTools now takes the shared NamespaceAccess guard rather than a registry plus a
    // principal, so that "did this tool check?" is answerable from its constructor alone.
    private CompositeTools Composite(string agentId, string tenantId = "") => new(
        _index, _embedding, _graph,
        new LifecycleEngine(_index, _persistence),
        new ExpertDispatcher(_index, _embedding),
        new MetricsCollector(),
        new SpectralRetrievalReranker(new MemoryDiffusionKernel(_index, _graph)),
        new NamespaceAccess(_registry, new PrincipalContext(tenantId, agentId)));

    private static string Json(object? value) => System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>Parse a relatedIds wire payload the way the MCP client would put it on the wire.</summary>
    private static System.Text.Json.JsonElement RelatedIds(params string[] ids) =>
        System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(ids)).RootElement;

    /// <summary>Alice writes a secret, which also claims ownership of the namespace.</summary>
    private void AliceStoresSecret()
    {
        var result = Core("alice").StoreMemory("alice-secret", AliceNs, "the launch code is hunter2");
        Assert.Contains("Stored entry", result);

        // The first write atomically claims an empty namespace for the identified principal.
        Assert.True(_registry.HasAccess("alice", AliceNs, requiredLevel: "read", tenantId: ""));
        Assert.False(_registry.HasAccess("bob", AliceNs, requiredLevel: "read", tenantId: ""));
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
        Assert.NotEmpty(_graph.GetEdgesForEntry("alice-secret", tenantId: ""));
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
    public void ContextBlock_DoesNotReturnAnotherAgentsStableMemories()
    {
        AliceStoresSecret();
        _index.SetLifecycleState("alice-secret", "ltm");

        var result = Composite("bob").GetContextBlock(AliceNs, minAccessCount: 1);
        var json = System.Text.Json.JsonSerializer.Serialize(result);

        Assert.DoesNotContain("hunter2", json);
        Assert.Contains("No accessible memories", json);
    }

    [Fact]
    public void ExpertRoutedRecall_SkipsInaccessibleExpertNamespace()
    {
        const string expertNs = "expert_private_security";
        Assert.Contains("Stored entry", Core("alice").StoreMemory(
            "expert-secret", expertNs, "private expert says rotate the vault key"));
        new ExpertDispatcher(_index, _embedding)
            .CreateExpert("private_security", "security vault key specialist");

        var result = Composite("bob").Recall("security vault key specialist");
        var json = System.Text.Json.JsonSerializer.Serialize(result);

        Assert.DoesNotContain("rotate the vault key", json);
        Assert.DoesNotContain(expertNs, json);
    }

    [Fact]
    public void RecallGraphExpansion_DoesNotCrossIntoUnreadableNamespace()
    {
        AliceStoresSecret();
        const string bobNs = "bob-work";
        Assert.Contains("Stored entry", Core("bob").StoreMemory(
            "bob-public", bobNs, "launch checklist"));
        _graph.AddEdge(new GraphEdge("bob-public", "alice-secret", "depends_on"));

        var result = Composite("bob").Recall("launch checklist", ns: bobNs, expandGraph: true);
        var json = System.Text.Json.JsonSerializer.Serialize(result);

        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("alice-secret", json);
    }

    [Fact]
    public void GetMemory_FiltersEdgesToUnreadableEndpoints()
    {
        AliceStoresSecret();
        const string bobNs = "bob-work";
        Assert.Contains("Stored entry", Core("bob").StoreMemory(
            "bob-public", bobNs, "launch checklist"));
        _graph.AddEdge(new GraphEdge("bob-public", "alice-secret", "depends_on"));

        var result = Assert.IsType<GetMemoryResult>(Admin("bob").GetMemory("bob-public"));

        Assert.Empty(result.Edges);
    }

    [Fact]
    public void CognitiveStats_DoesNotDiscloseAnotherAgentsNamespaceNames()
    {
        AliceStoresSecret();

        var stats = Admin("bob").CognitiveStats();

        Assert.DoesNotContain(AliceNs, stats.Namespaces);
    }

    [Fact]
    public void CognitiveStats_ExcludesUnreadableEntriesEdgesAndClusters()
    {
        AliceStoresSecret();
        Assert.Contains("Stored entry", Core("alice").StoreMemory(
            "alice-secret-2", AliceNs, "second private launch code"));
        _graph.AddEdge(new GraphEdge("alice-secret", "alice-secret-2", "similar_to"));
        _clusters.CreateCluster("alice-cluster", AliceNs, ["alice-secret", "alice-secret-2"], label: null, tenantId: "");

        var global = Admin("bob").CognitiveStats();
        var directProbe = Admin("bob").CognitiveStats(AliceNs);

        Assert.Equal(0, global.TotalEntries);
        Assert.Equal(0, global.EdgeCount);
        Assert.Equal(0, global.ClusterCount);
        Assert.Equal(0, directProbe.TotalEntries);
        Assert.Equal(0, directProbe.EdgeCount);
        Assert.Equal(0, directProbe.ClusterCount);
    }

    [Fact]
    public async Task PurgeDebates_CannotInspectOrDeleteAnotherAgentsSession()
    {
        const string debateNs = "active-debate-alice-session";
        var entry = new CognitiveEntry(
            "debate-alice-node", [0.5f, 0.5f], debateNs, "private deliberation")
        {
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-48)
        };
        _index.Upsert(entry);
        _registry.ClaimOwnershipOnWrite(debateNs, "alice", tenantId: "");

        var dryRun = Assert.IsType<PurgeDebatesResult>(
            await Admin("bob").PurgeDebates(maxAgeHours: 24, dryRun: true));
        var execute = Assert.IsType<PurgeDebatesResult>(
            await Admin("bob").PurgeDebates(maxAgeHours: 24, dryRun: false));

        Assert.Equal(0, dryRun.NamespacesAffected);
        Assert.Empty(dryRun.Namespaces);
        Assert.Equal(0, execute.NamespacesAffected);
        Assert.NotNull(_index.Get("debate-alice-node", debateNs, tenantId: ""));
    }

    [Fact]
    public void PrincipalTenant_SameNamespaceAndIdRemainStrictlyIsolated()
    {
        const string ns = "shared-name";
        const string id = "same-id";
        var tenantA = Core("alice", "tenant-a");
        var tenantB = Core("bob", "tenant-b");

        Assert.Contains("Stored entry", tenantA.StoreMemory(id, ns, "tenant A secret"));
        Assert.Contains("Stored entry", tenantB.StoreMemory(id, ns, "tenant B secret"));

        var aSearch = System.Text.Json.JsonSerializer.Serialize(
            tenantA.SearchMemory(ns, text: "secret"));
        var bSearch = System.Text.Json.JsonSerializer.Serialize(
            tenantB.SearchMemory(ns, text: "secret"));
        var aGet = Assert.IsType<GetMemoryResult>(Admin("alice", "tenant-a").GetMemory(id));
        var bGet = Assert.IsType<GetMemoryResult>(Admin("bob", "tenant-b").GetMemory(id));

        Assert.Contains("tenant A secret", aSearch);
        Assert.DoesNotContain("tenant B secret", aSearch);
        Assert.Contains("tenant B secret", bSearch);
        Assert.DoesNotContain("tenant A secret", bSearch);
        Assert.Equal("tenant A secret", aGet.Text);
        Assert.Equal("tenant B secret", bGet.Text);
        Assert.Equal(1, Admin("alice", "tenant-a").CognitiveStats().TotalEntries);
        Assert.Equal(1, Admin("bob", "tenant-b").CognitiveStats().TotalEntries);

        Assert.Contains("Deleted entry", tenantA.DeleteMemory(id));
        Assert.Null(_index.Get(id, ns, tenantId: "tenant-a"));
        Assert.Equal("tenant B secret", _index.Get(id, ns, tenantId: "tenant-b")?.Text);
    }

    [Fact]
    public void NamespaceOwnership_IsQualifiedByTenant()
    {
        const string ns = "same-project";
        Assert.Contains("Stored entry", Core("alice", "tenant-a").StoreMemory(
            "a", ns, "owned independently by A"));
        Assert.Contains("Stored entry", Core("bob", "tenant-b").StoreMemory(
            "b", ns, "owned independently by B"));

        Assert.True(_registry.HasAccess("alice", ns, "write", tenantId: "tenant-a"));
        Assert.False(_registry.HasAccess("bob", ns, "write", tenantId: "tenant-a"));
        Assert.True(_registry.HasAccess("bob", ns, "write", tenantId: "tenant-b"));
        Assert.False(_registry.HasAccess("alice", ns, "write", tenantId: "tenant-b"));
    }

    [Fact]
    public void SharedNamespace_BecomesReadableByTheGrantee()
    {
        AliceStoresSecret();
        _registry.Share(AliceNs, "alice", "bob", "read", tenantId: "");

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

        Assert.True(_registry.HasAccess(AgentIdentity.DefaultAgentId, "default-ns", requiredLevel: "read", tenantId: ""));
        Assert.False(_registry.HasAccess("someone-else", "default-ns", requiredLevel: "read", tenantId: ""),
            "identified principals must not take over legacy content; ownership requires an administrative migration");
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // Authorize the object you touch, at the verb you perform.
    //
    // Everything below drives the tools as Bob, an honestly-identified second agent, for the
    // reason this whole fixture exists: NamespaceRegistry.HasAccess short-circuits the DEFAULT
    // agent to unrestricted access, so a test written with AgentIdentity.Default cannot observe
    // an ACL failure at all and would pass identically with the fix reverted.
    // ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetMemory_FiltersClusterIdsInUnreadableNamespaces()
    {
        AliceStoresSecret();
        const string bobNs = "bob-work";
        const string bobArchiveNs = "bob-archive";
        Assert.Contains("Stored entry", Core("bob").StoreMemory(
            "bob-public", bobNs, "launch checklist"));
        Assert.Contains("Stored entry", Core("bob").StoreMemory(
            "bob-older", bobArchiveNs, "last quarter's checklist"));

        // Cluster membership is deliberately allowed to span namespaces, so a cluster that
        // LIVES in Alice's private namespace can legitimately contain a public entry of Bob's.
        // Returning its id is the same class of disclosure as an edge to a private endpoint,
        // one level of indirection out: it names a grouping Bob cannot read and tells him his
        // own entry was filed alongside content he cannot see.
        _clusters.CreateCluster("alice-topic-cluster", AliceNs, ["alice-secret", "bob-public"], label: null, tenantId: "");
        // Same-namespace membership, the ordinary case.
        _clusters.CreateCluster("bob-topic-cluster", bobNs, ["bob-public"], label: null, tenantId: "");
        // Over-correction control: a cluster in a DIFFERENT namespace that Bob can read. The
        // gate is CanRead(cluster.Ns) — the predicate ClusterTools.GetCluster already applies —
        // and not equality with entry.Ns. Filtering on equality would pass the exploit assertion
        // below while silently deleting cross-namespace clustering for everyone.
        _clusters.CreateCluster("bob-crossns-cluster", bobArchiveNs, ["bob-public", "bob-older"], label: null, tenantId: "");

        var result = Assert.IsType<GetMemoryResult>(Admin("bob").GetMemory("bob-public"));

        Assert.DoesNotContain("alice-topic-cluster", result.ClusterIds);
        Assert.DoesNotContain("alice-topic-cluster", Json(result));
        Assert.Contains("bob-topic-cluster", result.ClusterIds);
        Assert.Contains("bob-crossns-cluster", result.ClusterIds);
        Assert.Equal(2, result.ClusterIds.Count);
    }

    [Fact]
    public void Reflect_DoesNotLinkToOrRevealAnotherAgentsEntry()
    {
        AliceStoresSecret();

        // The same reflection run twice, differing in exactly one input: the probe names an id
        // that really exists in a namespace Bob may not write, the control names an id that
        // exists nowhere in the store. Two namespaces because the uniform stub embedding makes
        // every lesson a 1.0-similarity duplicate of every other, which would divert the second
        // run into the duplicate_warning branch and destroy the comparison.
        const string probeNs = "bob-probe";
        const string controlNs = "bob-control";

        var probe = Assert.IsType<ReflectResult>(Composite("bob").Reflect(
            "the retro found a missing rollback step", probeNs, "oracle-check",
            relatedIds: RelatedIds("alice-secret")));
        var control = Assert.IsType<ReflectResult>(Composite("bob").Reflect(
            "the retro found a missing rollback step", controlNs, "oracle-check",
            relatedIds: RelatedIds("no-such-entry-anywhere")));

        // (a) No edge was drawn. relatedIds arrive as bare ids with no namespace attached, so
        // linking without resolving-then-authorizing writes an edge onto an object the caller
        // was never entitled to touch.
        Assert.Empty(_graph.GetEdgesForEntry("alice-secret", tenantId: ""));
        Assert.Empty(_graph.GetEdgesForEntry(probe.Id, tenantId: ""));

        // (b) The two replies are identical once the namespace this test itself varied is
        // normalized away. THIS EQUALITY IS THE SECURITY PROPERTY. Asserting merely that the
        // link was refused would still pass against an implementation that answers "not
        // linkable" for one id and "not found" for the other — which turns reflect into an
        // existence oracle over every namespace Bob cannot see, one id per call.
        Assert.Equal(
            Json(control).Replace(controlNs, "<ns>", StringComparison.Ordinal),
            Json(probe).Replace(probeNs, "<ns>", StringComparison.Ordinal));
    }

    [Fact]
    public void Reflect_StillLinksToAnEntryTheCallerOwns()
    {
        // Over-correction control: the fix authorizes the link target, it does not remove
        // relatedIds linking. If this reddens, resolution has been made to fail closed on
        // everything rather than on what the caller may not write.
        AliceStoresSecret();
        const string bobNs = "bob-work";
        Assert.Contains("Stored entry", Core("bob").StoreMemory(
            "bob-note", bobNs, "launch checklist"));

        var result = Assert.IsType<ReflectResult>(Composite("bob").Reflect(
            "the checklist needed a rollback step", bobNs, "own-link",
            relatedIds: RelatedIds("bob-note")));

        Assert.Equal("stored", result.Status);
        Assert.Contains("linked to bob-note", result.Actions);
        Assert.DoesNotContain(result.Actions, a => a.Contains("skipped", StringComparison.Ordinal));
        Assert.Contains(_graph.GetEdgesForEntry(result.Id, tenantId: ""),
            e => e.TargetId == "bob-note" && e.Relation == "elaborates");
    }

    [Fact]
    public void Reflect_LinksOwnEntryEvenWhenTheSameIdExistsInAPrivateNamespace()
    {
        // An id is not an identity — entries are identified by (tenant, namespace, id) and ids
        // are unique only per (tenant, namespace). So a bare relatedId can name several entries
        // at once, and how the resolver breaks that tie is itself a disclosure channel.
        const string sharedId = "postmortem-notes";
        const string bobNs = "bob-work";
        const string bobArchiveNs = "bob-archive";

        Assert.Contains("Stored entry", Core("alice").StoreMemory(
            sharedId, AliceNs, "alice's private postmortem"));
        Assert.False(_registry.HasAccess("bob", AliceNs, "write", tenantId: ""));

        // A second twin Bob CAN write. Without the preferredNs short-circuit the resolution is
        // ambiguous among namespaces Bob is entitled to and collapses to null, so a legitimate
        // link silently disappears; with it, the namespace the call site is already authorized
        // for wins. Alice's invisible twin must contribute neither a match nor an ambiguity
        // signal — "your link did nothing" would otherwise announce that a private twin exists.
        Assert.Contains("Stored entry", Core("bob").StoreMemory(
            sharedId, bobArchiveNs, "bob's older copy"));
        Assert.Contains("Stored entry", Core("bob").StoreMemory(
            sharedId, bobNs, "bob's working copy"));

        var result = Assert.IsType<ReflectResult>(Composite("bob").Reflect(
            "the postmortem missed the rollback step", bobNs, "twin-id",
            relatedIds: RelatedIds(sharedId)));

        Assert.Equal("stored", result.Status);
        Assert.Contains($"linked to {sharedId}", result.Actions);
        Assert.DoesNotContain(result.Actions, a => a.Contains("skipped", StringComparison.Ordinal));
        Assert.Contains(_graph.GetEdgesForEntry(result.Id, tenantId: ""),
            e => e.TargetId == sharedId && e.Relation == "elaborates");
    }

    [Fact]
    public void Recall_ByReadOnlyGrantee_DoesNotResurrectOwnersArchivedEntry()
    {
        const string archiveNs = "alice-cold-storage";
        SeedAlicesArchivedNote(archiveNs, grantBobLevel: "read");

        // recall is the DEFAULT verb of the minimal profile, so this is the path a real
        // deployment actually hits. With nothing live in the namespace the hybrid pass returns
        // nothing and recall falls back to deep_recall, which promotes high-scoring archived
        // entries back to stm as a side effect of reading them — a write carried on a path the
        // caller only ever asked to read on.
        var result = Assert.IsType<RecallResult>(
            Composite("bob").Recall("cold storage rollback retrospective", ns: archiveNs));

        // The mutating path really was reached; without this the assertions below are vacuous.
        Assert.Equal("deep_recall", result.Strategy);
        Assert.NotEmpty(result.Results);

        // Read access legitimately authorizes seeing archived text, so the ROWS are not
        // withheld — only the write is. A read-only grantee gets the same rows, scores and
        // order, and the only thing that changes is that the reported state is the truth.
        Assert.Contains("cold storage rollback retrospective", Json(result));
        Assert.All(result.Results, r => Assert.Equal("archived", r.LifecycleState));
        Assert.Equal("archived", _index.Get("alice-archived-note", archiveNs, tenantId: "")?.LifecycleState);
    }

    [Fact]
    public void Recall_ByWriteGrantee_StillResurrectsArchivedEntry()
    {
        // Over-correction control: the gate is on the caller's write permission, not on the
        // resurrection feature. A grantee who may write still gets the promotion, so the fix
        // cannot have been implemented by simply passing resurrect:false everywhere.
        const string archiveNs = "alice-cold-storage";
        SeedAlicesArchivedNote(archiveNs, grantBobLevel: "write");

        var result = Assert.IsType<RecallResult>(
            Composite("bob").Recall("cold storage rollback retrospective", ns: archiveNs));

        Assert.Equal("deep_recall", result.Strategy);
        Assert.NotEmpty(result.Results);
        Assert.All(result.Results, r => Assert.Equal("stm", r.LifecycleState));
        Assert.Equal("stm", _index.Get("alice-archived-note", archiveNs, tenantId: "")?.LifecycleState);
    }

    /// <summary>
    /// Alice owns a namespace whose only entry is archived, shared with Bob at the given level.
    /// Nothing live in it, so recall's hybrid pass comes back empty and the deep_recall
    /// fallback — the one that mutates — is guaranteed to run.
    /// </summary>
    private void SeedAlicesArchivedNote(string archiveNs, string grantBobLevel)
    {
        Assert.Contains("Stored entry", Core("alice").StoreMemory(
            "alice-archived-note", archiveNs, "cold storage rollback retrospective",
            lifecycleState: "archived"));
        Assert.Equal("shared", _registry.Share(archiveNs, "alice", "bob", grantBobLevel, tenantId: "").Status);
        Assert.True(_registry.HasAccess("bob", archiveNs, requiredLevel: "read", tenantId: ""));
        Assert.Equal(grantBobLevel == "write", _registry.HasAccess("bob", archiveNs, "write", tenantId: ""));
    }
}
