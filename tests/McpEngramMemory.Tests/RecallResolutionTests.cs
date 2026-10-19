using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Models.Governance;
using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Constitution;
using McpEngramMemory.Core.Services.Governance;
using McpEngramMemory.Core.Services.Knowledge;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace McpEngramMemory.Tests;

public class RecallResolutionTests
{
    internal const string Ns = "expert_atlas";
    internal const string Tenant = "team-a";
    internal const string Agent = "alice";
    internal const string Query = "What is the Atlas gateway timeout?";
    internal const string A = "Atlas gateway timeout is 30 seconds.";
    internal const string B = "Atlas gateway timeout is 45 seconds.";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CitesCurrentGovernedSupportWithoutReorderingOrRewritingMemories(bool broadcast, bool restart)
    {
        using var store = Create();
        await Support(store, "b", B, "source", "Atlas gateway times out after 45000 ms.");
        if (restart) store.Restart();
        var without = Recall(store, broadcast, knowledge: new EmptyKnowledgeStore());
        var result = Recall(store, broadcast);
        Assert.Equal(without.Results.Select(r => (r.Id, r.Score)), result.Results.Select(r => (r.Id, r.Score)));
        var conflict = Pair(result);
        Assert.Equal("resolved", conflict.Status);
        var resolution = JsonSerializer.SerializeToElement(conflict).GetProperty("resolution");
        Assert.Equal("b", resolution.GetProperty("preferredId").GetString());
        Assert.Equal("governed_support", resolution.GetProperty("basis").GetString());
        Assert.Equal("source", resolution.GetProperty("sources")[0].GetProperty("id").GetString());
        Assert.Equal(64, resolution.GetProperty("knowledgeContentHash").GetString()!.Length);
        Assert.Equal(1, store.Index.Get("source", Ns, Tenant)!.AccessCount);
        Assert.Equal(B, store.Index.Get("b", Ns, Tenant)!.Text);
        Assert.Equal("ltm", store.Index.Get("b", Ns, Tenant)!.LifecycleState);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed-text")]
    [InlineData("changed-metadata")]
    [InlineData("archived")]
    [InlineData("summary")]
    [InlineData("unrelated")]
    [InlineData("self-citation")]
    [InlineData("disputed")]
    [InlineData("both-supported")]
    [InlineData("malformed-other-ref")]
    [InlineData("stale-knowledge")]
    [InlineData("wrong-hash")]
    [InlineData("private-knowledge")]
    [InlineData("foreign-knowledge")]
    [InlineData("unavailable-store")]
    public async Task InsufficientOrInaccessibleEvidenceRemainsUnresolved(string failure)
    {
        using var store = Create();
        await Support(store, "b", B, failure == "self-citation" ? "b" : "source",
            failure == "unrelated" ? "Atlas gateway region is us-east-1." : B,
            contradicting: failure == "disputed" ? ["a"] : null);
        var source = store.Index.Get("source", Ns, Tenant);
        switch (failure)
        {
            case "missing": store.Index.Delete("source", Ns, Tenant); break;
            case "changed-text": store.Add("source", Ns, A, cosine: .1f, tenant: Tenant); break;
            case "changed-metadata": source!.Metadata["review"] = "changed"; break;
            case "archived": store.Index.SetLifecycleState("source", "archived", Ns, Tenant); break;
            case "summary": source!.IsSummaryNode = true; break;
            case "both-supported": await Support(store, "a", A, "source-a", A); break;
            case "malformed-other-ref": store.Index.Get("a", Ns, Tenant)!.Metadata["recallKnowledge"] = "{"; break;
            case "stale-knowledge": await Promote(store, "knowledge-b", B, ["source"], ["a"]); break;
            case "wrong-hash": store.Index.Get("b", Ns, Tenant)!.Metadata["recallKnowledge"] = JsonSerializer.Serialize(new { id = "knowledge-b", contentHash = new string('0', 64) }); break;
            case "foreign-knowledge":
                store.Index.Get("b", Ns, Tenant)!.Metadata["recallKnowledge"] = JsonSerializer.Serialize(new { id = "knowledge-b", contentHash = new string('1', 64), tenantId = "foreign" }); break;
        }
        if (failure == "private-knowledge")
            Assert.Equal("shared", store.Registry.Share(Ns, Agent, "bob", "read", Tenant).Status);
        var result = Recall(store, agent: failure == "private-knowledge" ? "bob" : Agent,
            knowledge: failure == "unavailable-store" ? new UnavailableKnowledgeStore() : null);
        Assert.Equal("unresolved", Pair(result).Status);
        Assert.DoesNotContain("knowledge-b", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task AllDisagreementsInAPairNeedConsistentSupport()
    {
        using var store = Create();
        store.Add("a", Ns, A + " Atlas retry limit is 2.", .99f, tenant: Tenant);
        store.Add("b", Ns, B + " Atlas retry limit is 5.", .8f, tenant: Tenant);
        await Support(store, "b", B, "source", B);
        Assert.Equal("unresolved", Pair(Recall(store)).Status);
    }

    [Fact]
    public async Task AnInternallyContradictoryMemoryCannotBecomeThePreferredAnswer()
    {
        using var store = Create();
        store.Add("b", Ns, B + " " + A, .8f, tenant: Tenant);
        await Support(store, "b", B, "source", B);
        Assert.Equal("unresolved", Pair(Recall(store)).Status);
    }

    [Fact]
    public async Task HistoricalAndRerankOptOutDoNotResolve()
    {
        using var store = Create();
        await Support(store, "b", B, "source", B);
        foreach (var (query, rerank) in new[] { ("What was the Atlas gateway timeout?", true), (Query, false) })
        {
            var result = Assert.IsType<RecallResult>(store.Tools(Agent, Tenant).Recall(query, Ns,
                hybrid: false, rerank: rerank, expandGraph: false, spectralMode: "none"));
            Assert.Null(result.Conflicts);
        }
    }

    [Fact]
    public async Task AThirdClaimWithContraryGovernedSupportVetoesTheConnectedDisagreement()
    {
        using var store = Create();
        store.Add("c", Ns, "Atlas gateway timeout is 60 seconds.", .9f, tenant: Tenant);
        await Support(store, "b", B, "source-b", B);
        await Support(store, "c", "Atlas gateway timeout is 60 seconds.", "source-c", "Atlas gateway timeout is 60 seconds.");
        var result = Assert.IsType<RecallResult>(store.Tools(Agent, Tenant).Recall(Query, Ns, k: 3,
            hybrid: false, expandGraph: false, spectralMode: "none"));
        Assert.Equal(3, result.Conflicts!.Count);
        Assert.All(result.Conflicts, c => Assert.Equal("unresolved", c.Status));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("superseded")]
    [InlineData("withdrawn")]
    [InlineData("proposed")]
    [InlineData("source-permission")]
    [InlineData("no-provenance")]
    [InlineData("no-audit")]
    [InlineData("corrupt-hash")]
    [InlineData("too-many-sources")]
    public async Task InvalidGovernedSnapshotsCannotSupplyAResolution(string failure)
    {
        using var store = Create();
        await Support(store, "b", B, "source", B);
        var snapshot = await store.Knowledge.ReadAsync(Tenant, Ns, "knowledge-b");
        var d = snapshot.ActiveVersion!.Definition;
        var temporal = failure switch
        {
            "expired" => new BitemporalValidity(d.Temporal.CreatedAt, d.Temporal.RecordedAt, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1)),
            "future" => new BitemporalValidity(d.Temporal.CreatedAt, d.Temporal.RecordedAt, DateTimeOffset.UtcNow.AddDays(1)),
            _ => d.Temporal
        };
        var support = d.SupportingEvidence;
        if (failure == "source-permission")
            support = support.Select(e => new EvidenceReference(e.Artifact, e.ContentHash, e.ObservedAt,
                e.IndependentSourceKey, new PermissionEnvelope())).ToArray();
        if (failure == "too-many-sources")
            support = Enumerable.Range(0, 17).Select(i => new EvidenceReference(
                new ArtifactRef(Tenant, Ns, ArtifactKind.Evidence, "source-" + i, d.SupportingEvidence[0].ContentHash),
                d.SupportingEvidence[0].ContentHash, DateTimeOffset.UtcNow, "source-" + i, d.Permissions)).ToArray();
        var version = KnowledgeCanonicalizer.PublishVersion(new KnowledgeVersionDefinition(d.Reference, d.Claim,
            failure == "proposed" ? KnowledgeMaturity.Proposed : d.Maturity,
            failure == "superseded" ? KnowledgeStatus.Superseded : failure == "withdrawn" ? KnowledgeStatus.Withdrawn : d.Status,
            temporal, d.Epistemic, support, d.ContradictingEvidence, d.Permissions, d.ConstitutionVersionHash));
        var asset = KnowledgeCanonicalizer.PublishAsset([version], version.ContentHash);
        var transformed = new GovernedKnowledgeSnapshot(asset,
            failure == "corrupt-hash" ? version with { ContentHash = new string('f', 64) } : version,
            failure == "no-provenance" ? [] : snapshot.Provenance, failure == "no-audit" ? [] : snapshot.Audit);
        store.Index.Get("b", Ns, Tenant)!.Metadata["recallKnowledge"] = JsonSerializer.Serialize(new
            { id = "knowledge-b", contentHash = version.ContentHash });
        Assert.Equal("unresolved", Pair(Recall(store, knowledge: new SnapshotKnowledgeStore(transformed))).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APrivateOrForeignSourceTwinCannotRepairAMissingCitation(bool foreign)
    {
        using var store = Create();
        await Support(store, "b", B, "source", B);
        store.Index.Delete("source", Ns, Tenant);
        var twin = store.Add("source", foreign ? Ns : "private", B, tenant: foreign ? "other-team" : Tenant);
        if (!foreign) store.Registry.EnsureOwnership("private", "bob", Tenant);
        Assert.Equal("unresolved", Pair(Recall(store)).Status);
        Assert.Equal(1, twin.AccessCount);
    }

    [Theory]
    [InlineData("{\"id\":\"x\",\"contentHash\":false}")]
    [InlineData("[]")]
    [InlineData("{\"id\":\"x\",\"contentHash\":\"abc\"}")]
    [InlineData("{\"id\":\"x\",\"id\":\"y\",\"contentHash\":\"abc\"}")]
    public void InvalidReferenceShapesAreOptionalEvidenceFailures(string json)
    {
        using var store = Create();
        store.Index.Get("b", Ns, Tenant)!.Metadata["recallKnowledge"] = json;
        Assert.Equal("unresolved", Pair(Recall(store)).Status);
    }

    [Fact]
    public async Task StoreCancellationLeavesRecallAvailable()
    {
        using var store = Create();
        await Support(store, "b", B, "source", B);
        Assert.Equal("unresolved", Pair(Recall(store, knowledge: new SlowKnowledgeStore())).Status);
    }

    [Fact]
    public async Task IncompleteEvidencePassDoesNotRetainAnEarlierPreference()
    {
        using var store = Create();
        await Support(store, "b", B, "source", B);
        store.Add("c", Ns, "Atlas deployment owner is Maya.", tenant: Tenant);
        store.Add("d", Ns, "Atlas deployment owner is Leon.", tenant: Tenant);
        await Support(store, "d", "Atlas deployment owner is Leon.", "owner-source", "Leon owns Atlas deployments.");
        RecallConflict Conflict(string first, string second) => new("unresolved", "same_subject_different_value", "test",
            new[] { first, second }.Select(id => new RecallConflictEvidence(id, Ns, store.Index.Get(id, Ns, Tenant)!.Text!)).ToArray());
        var conflicts = new[] { Conflict("a", "b"), Conflict("c", "d") };
        var resolver = new RecallEvidenceResolution(store.Index,
            new NamespaceAccess(store.Registry, new PrincipalContext(Tenant, Agent)), new FirstThenSlowStore(store.Knowledge));
        Assert.All(resolver.Resolve(conflicts), c => Assert.Equal("unresolved", c.Status));
    }

    [Fact]
    public async Task CompleteSupportCanSpanMultipleCitedSources()
    {
        using var store = Create();
        string claim = B + " Atlas retry limit is 5.";
        store.Add("a", Ns, A + " Atlas retry limit is 2.", .99f, tenant: Tenant);
        store.Add("b", Ns, claim, .8f, tenant: Tenant);
        store.Add("timeout-source", Ns, B, .1f, tenant: Tenant);
        store.Add("retry-source", Ns, "Atlas retry limit is 5.", .1f, tenant: Tenant);
        var snapshot = await Promote(store, "knowledge-b", claim, ["timeout-source", "retry-source"]);
        store.Index.Get("b", Ns, Tenant)!.Metadata["recallKnowledge"] = JsonSerializer.Serialize(new
            { id = "knowledge-b", contentHash = snapshot.ActiveVersion!.ContentHash });
        var pair = Pair(Recall(store));
        Assert.Equal("resolved", pair.Status);
        Assert.Equal(2, pair.Resolution!.Sources.Count);
    }

    [Fact]
    public async Task ReachingTheDiagnosticCapWithholdsResolution()
    {
        using var store = Create();
        for (int i = 0; i < 7; i++) store.Add("copy-" + i, Ns, A, .95f, tenant: Tenant);
        await Support(store, "b", B, "source", B);
        var result = Assert.IsType<RecallResult>(store.Tools(Agent, Tenant).Recall(Query, Ns, k: 9,
            hybrid: false, expandGraph: false, spectralMode: "none"));
        Assert.Equal(8, result.Conflicts!.Count);
        Assert.All(result.Conflicts, c => Assert.Equal("unresolved", c.Status));
    }

    [Fact]
    public async Task ResolvedArchivedClaimsRemainArchivedAndEvidenceReadsDoNotRecordAccess()
    {
        using var store = Create();
        await Support(store, "b", B, "source", B);
        store.Index.SetLifecycleState("a", "archived", Ns, Tenant);
        store.Index.SetLifecycleState("b", "archived", Ns, Tenant);
        var result = Recall(store);
        Assert.Equal("resolved", Pair(result).Status);
        Assert.All(new[] { "a", "b" }, id => Assert.Equal("archived", store.Index.Get(id, Ns, Tenant)!.LifecycleState));
        Assert.Equal(1, store.Index.Get("b", Ns, Tenant)!.AccessCount);
        Assert.Equal(1, store.Index.Get("source", Ns, Tenant)!.AccessCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostActivationPreservesOptionalStoreCompatibility(bool withStore)
    {
        using var store = Create();
        await Support(store, "b", B, "source", B);
        var services = new ServiceCollection();
        services.AddSingleton(store.Index);
        services.AddSingleton<IEmbeddingService>(new ConstantEmbedding());
        services.AddSingleton(store.Graph);
        services.AddSingleton(new LifecycleEngine(store.Index));
        services.AddSingleton(store.Dispatcher);
        services.AddSingleton(new MetricsCollector());
        services.AddSingleton(new SpectralRetrievalReranker(new MemoryDiffusionKernel(store.Index, store.Graph)));
        services.AddSingleton(new NamespaceAccess(store.Registry, new PrincipalContext(Tenant, Agent)));
        if (withStore) services.AddSingleton(store.Knowledge);
        using var provider = services.BuildServiceProvider();
        var tools = ActivatorUtilities.CreateInstance<CompositeTools>(provider);
        var result = Assert.IsType<RecallResult>(tools.Recall(Query, Ns, k: 1, hybrid: false,
            expandGraph: false, spectralMode: "none"));
        Assert.Equal(withStore ? "resolved" : "unresolved", Pair(result).Status);
    }

    private sealed class ConstantEmbedding : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [1f, 0f];
    }

    internal static RecallTestStore Create()
    {
        var store = new RecallTestStore();
        store.Registry.EnsureOwnership(Ns, Agent, Tenant);
        store.Add("a", Ns, A, .99f, tenant: Tenant);
        store.Add("b", Ns, B, .8f, tenant: Tenant);
        return store;
    }

    internal static RecallResult Recall(RecallTestStore store, bool broadcast = false,
        string agent = Agent, IGovernedKnowledgeStore? knowledge = null) =>
        Assert.IsType<RecallResult>(store.Tools(agent, Tenant, knowledge).Recall(Query, broadcast ? null : Ns,
            k: 1, hybrid: false, expandGraph: false, spectralMode: "none"));

    internal static RecallConflict Pair(RecallResult result) => Assert.Single(result.Conflicts!, c =>
        c.Evidence.Select(e => e.Id).Order().SequenceEqual(new[] { "a", "b" }));

    internal static async Task Support(RecallTestStore store, string candidate, string claim,
        string sourceId, string sourceText, string[]? contradicting = null)
    {
        if (sourceId != candidate) store.Add(sourceId, Ns, sourceText, .1f, tenant: Tenant);
        string id = "knowledge-" + candidate;
        var snapshot = await Promote(store, id, claim, [sourceId], contradicting);
        var entry = store.Index.Get(candidate, Ns, Tenant)!;
        entry.Metadata["recallKnowledge"] = JsonSerializer.Serialize(new { id, contentHash = snapshot.ActiveVersion!.ContentHash });
        store.Index.Upsert(entry);
    }

    internal static async Task<GovernedKnowledgeSnapshot> Promote(RecallTestStore store, string id,
        string claim, string[] sources, string[]? contradictions = null)
    {
        var principal = new PrincipalContext(Tenant, Agent);
        var provider = new InMemoryConstitutionProvider();
        var kernel = new ConstitutionKernel(provider,
            new DeterministicConstitutionEvaluator([new AuditEnvelopeConstitutionRule()]), new InMemoryConstitutionAuditStore());
        var tools = new GovernedLearningTools(store.Index, new NamespaceAccess(store.Registry, principal),
            principal, provider, kernel, store.Knowledge);
        var result = JsonSerializer.SerializeToElement(await tools.PromoteKnowledge(id, Ns, claim, sources, contradictions));
        Assert.Equal("committed", result.GetProperty("status").GetString());
        return await store.Knowledge.ReadAsync(Tenant, Ns, id);
    }

    internal class EmptyKnowledgeStore : IGovernedKnowledgeStore
    {
        public ValueTask<GovernedCommitResult> CommitPromotionAsync(GovernedPromotionCommit commit,
            Func<CommitAuthorityState> resolveCurrentAuthority, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public virtual ValueTask<GovernedKnowledgeSnapshot> ReadAsync(string tenantId, string ns, string artifactId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new GovernedKnowledgeSnapshot(null, null, [], []));
    }

    private sealed class UnavailableKnowledgeStore : EmptyKnowledgeStore
    {
        public override ValueTask<GovernedKnowledgeSnapshot> ReadAsync(string tenantId, string ns, string artifactId,
            CancellationToken cancellationToken = default) => throw new IOException("unavailable");
    }

    private sealed class SnapshotKnowledgeStore(GovernedKnowledgeSnapshot snapshot) : EmptyKnowledgeStore
    {
        public override ValueTask<GovernedKnowledgeSnapshot> ReadAsync(string tenantId, string ns, string artifactId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(snapshot);
    }

    private sealed class SlowKnowledgeStore : EmptyKnowledgeStore
    {
        public override async ValueTask<GovernedKnowledgeSnapshot> ReadAsync(string tenantId, string ns, string artifactId,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class FirstThenSlowStore(IGovernedKnowledgeStore inner) : EmptyKnowledgeStore
    {
        private int _reads;
        public override async ValueTask<GovernedKnowledgeSnapshot> ReadAsync(string tenantId, string ns, string artifactId,
            CancellationToken cancellationToken = default)
        {
            if (++_reads == 1) return await inner.ReadAsync(tenantId, ns, artifactId, cancellationToken).ConfigureAwait(false);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }
    }
}
