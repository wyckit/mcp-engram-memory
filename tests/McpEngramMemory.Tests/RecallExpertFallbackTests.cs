using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class RecallExpertFallbackTests
{
    private const string Query = "What is Nectar staging readiness endpoint?";
    private const string Answer = "Nectar staging readiness endpoint is /stage/ready.";

    private static CognitiveEntry Expert(RecallTestStore store, string id, string ns, float cosine)
    {
        var profile = store.Add(id, ExpertDispatcher.SystemNamespace, "Nectar operations specialist", cosine, summary: true);
        profile.Metadata["targetNamespace"] = ns;
        store.Index.Upsert(profile);
        return profile;
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("wrong-topic")]
    [InlineData("wrong-scope")]
    [InlineData("summary-only")]
    [InlineData("below-threshold")]
    [InlineData("foreign-tenant")]
    public void NextExpertMustHaveEligibleEvidenceAndOnlySelectedEvidenceIsAccessed(string firstKind)
    {
        var embedding = new CountingEmbedding();
        using var store = new RecallTestStore(embedding);
        var first = Expert(store, "first", "expert_empty", 1f);
        var second = Expert(store, "second", "expert_populated", .98f);
        CognitiveEntry? rejected = firstKind switch
        {
            "empty" => null,
            "wrong-topic" => store.Add("rejected", "expert_empty", "Nectar monthly budget is 1000 USD.", state: "archived"),
            "wrong-scope" => store.Add("rejected", "expert_empty", "Cedar staging readiness endpoint is /cedar.", state: "archived"),
            "summary-only" => store.Add("rejected", "expert_empty", Answer, summary: true),
            "below-threshold" => store.Add("rejected", "expert_empty", Answer, .1f, state: "archived"),
            _ => store.Add("rejected", "expert_empty", Answer, tenant: "other-tenant")
        };
        var answer = store.Add("answer", "expert_populated", Answer, .9f, state: "archived");
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, k: 1));
        Assert.Equal("expert_routed", result.Strategy);
        Assert.Equal("expert_populated", result.Namespace);
        Assert.Equal("answer", Assert.Single(result.Results).Id);
        Assert.Null(result.QueryEvidence);
        Assert.Equal(1, embedding.Calls);
        Assert.Equal(1, first.AccessCount);
        Assert.Equal(2, second.AccessCount);
        Assert.Equal(2, answer.AccessCount);
        Assert.Equal("stm", answer.LifecycleState);
        if (rejected is not null)
        {
            Assert.Equal(1, rejected.AccessCount);
            if (firstKind is "wrong-topic" or "wrong-scope" or "below-threshold") Assert.Equal("archived", rejected.LifecycleState);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExhaustedCandidateListFallsBackToBroadcastIncludingAfterRestart(bool restart)
    {
        var embedding = new CountingEmbedding();
        using var store = new RecallTestStore(embedding);
        Expert(store, "first", "expert_empty", 1f);
        Expert(store, "alias", "expert_empty", .99f);
        Expert(store, "third", "expert_wrong", .98f);
        Expert(store, "fourth", "expert_answer", .97f);
        store.Add("wrong", "expert_wrong", "Nectar production readiness endpoint is /prod.", state: "archived");
        store.Add("answer", "expert_answer", Answer, .9f, state: "archived");
        if (restart) store.Restart();
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, k: 1));
        // The fourth profile is outside the three-candidate routing window. Its
        // evidence remains reachable through authorized broadcast, without dispatch.
        Assert.Equal("broadcast", result.Strategy);
        Assert.Null(result.Namespace);
        Assert.Equal("answer", Assert.Single(result.Results).Id);
        Assert.Null(result.QueryEvidence);
        Assert.Contains("No eligible evidence", result.RoutingInfo);
        Assert.Equal(1, embedding.Calls);
        Assert.All(new[] { "first", "alias", "third", "fourth" }, id =>
            Assert.Equal(1, store.Dispatcher.GetExpert(id)!.TaskCount));
        Assert.Equal("archived", store.Index.Get("wrong", "expert_wrong", "")!.LifecycleState);
        Assert.Equal(1, store.Index.Get("wrong", "expert_wrong", "")!.AccessCount);
    }

    [Theory]
    [InlineData("ltm")]
    [InlineData("archived")]
    public void APartiallyPopulatedFirstExpertKeepsItsExistingResultAndDoesNotFillFromOthers(string state)
    {
        using var store = new RecallTestStore();
        var first = Expert(store, "first", "expert_first", 1f);
        var second = Expert(store, "second", "expert_second", .98f);
        store.Add("answer", "expert_first", Answer, .9f, state: state);
        var extra = store.Add("extra", "expert_second", Answer, .95f, state: "archived");
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, k: 5));
        Assert.Equal("expert_first", result.Namespace);
        Assert.Equal("answer", Assert.Single(result.Results).Id);
        Assert.Equal(2, first.AccessCount);
        Assert.Equal(1, second.AccessCount);
        Assert.Equal(1, extra.AccessCount);
        Assert.Equal("archived", extra.LifecycleState);
    }

    [Fact]
    public void GraphEvidenceCanKeepTheFirstRouteWithoutResurrectingAGraphOnlyArchive()
    {
        using var store = new RecallTestStore();
        var first = Expert(store, "first", "expert_first", 1f);
        var second = Expert(store, "second", "expert_second", .98f);
        store.Add("summary", "expert_first", Answer, summary: true);
        var source = store.Add("source", "supporting-notes", Answer, .9f, state: "archived");
        store.Graph.AddEdge(new GraphEdge("summary", "source", "elaborates"));
        store.Add("other", "expert_second", Answer);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, k: 1));
        Assert.Equal("expert_first", result.Namespace);
        Assert.Equal("source", Assert.Single(result.Results).Id);
        Assert.Equal(2, first.AccessCount);
        Assert.Equal(1, second.AccessCount);
        Assert.Equal(2, source.AccessCount);
        Assert.Equal("archived", source.LifecycleState);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ExplicitEvidenceOptOutsPreserveANonemptyFirstExpert(bool rerank, bool summaryFirst)
    {
        using var store = new RecallTestStore();
        Expert(store, "first", "expert_first", 1f);
        var second = Expert(store, "second", "expert_second", .98f);
        store.Add("context", "expert_first", "Nectar production readiness endpoint is /prod.");
        store.Add("answer", "expert_second", Answer);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, rerank: rerank, summaryFirst: summaryFirst));
        Assert.Equal("expert_first", result.Namespace);
        Assert.Equal("context", Assert.Single(result.Results).Id);
        Assert.Null(result.QueryEvidence);
        Assert.Equal(1, second.AccessCount);
    }

    [Fact]
    public void ExhaustionPreservesAbstentionThresholdsAndExplicitNamespaceScope()
    {
        using var store = new RecallTestStore();
        var profile = Expert(store, "first", "expert_empty", 1f);
        store.Add("answer", "expert_other", Answer, .8f, state: "archived");
        var direct = Assert.IsType<RecallResult>(store.Tools().Recall(Query, "expert_empty"));
        Assert.Empty(direct.Results);
        Assert.Equal("insufficient_evidence", direct.QueryEvidence?.Status);
        var automatic = Assert.IsType<RecallResult>(store.Tools().Recall(Query, minScore: .95f));
        Assert.Equal("broadcast", automatic.Strategy);
        Assert.Empty(automatic.Results);
        Assert.Equal("insufficient_evidence", automatic.QueryEvidence?.Status);
        Assert.Equal(1, profile.AccessCount);
        Assert.Equal(1, store.Index.Get("answer", "expert_other", "")!.AccessCount);
        Assert.Equal("archived", store.Index.Get("answer", "expert_other", "")!.LifecycleState);
    }

    [Theory]
    [InlineData("", "read", "archived")]
    [InlineData("team-a", "read", "archived")]
    [InlineData("team-a", "write", "stm")]
    public void IdentifiedCallersCannotRouteIntoPrivateOrForeignExpertEvidence(string tenant, string grant, string state)
    {
        using var store = new RecallTestStore();
        var profile = Expert(store, "secret-profile", "expert_private", 1f);
        store.Registry.EnsureOwnership("expert_public", "alice", tenant);
        store.Registry.EnsureOwnership("expert_private", "alice", tenant);
        store.Registry.Share("expert_public", "alice", "bob", grant, tenant);
        store.Add("same-id", "expert_public", Answer, .9f, state: "archived", tenant: tenant);
        var secret = store.Add("same-id", "expert_private", Answer + " private marker", state: "archived", tenant: tenant);
        var foreign = store.Add("same-id", "expert_public", Answer + " foreign marker", state: "archived", tenant: "team-b");
        var result = Assert.IsType<RecallResult>(store.Tools("bob", tenant).Recall(Query, k: 1));
        Assert.Equal("broadcast", result.Strategy); // Global expert routing stays disabled for identified callers.
        Assert.Equal(state, Assert.Single(result.Results).LifecycleState);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("foreign", JsonSerializer.Serialize(result));
        Assert.Equal(1, profile.AccessCount);
        Assert.All(new[] { secret, foreign }, e => { Assert.Equal(1, e.AccessCount); Assert.Equal("archived", e.LifecycleState); });
    }

    private sealed class CountingEmbedding : IEmbeddingService
    {
        public int Calls { get; private set; }
        public int Dimensions => 2;
        public float[] Embed(string text) { Calls++; return [1f, 0f]; }
    }
}
