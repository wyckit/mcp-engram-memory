using McpEngramMemory.Core.Models;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class RecallRouteConsistencyTests
{
    private const string Ns = "expert_routes";
    private const string Query = "Atlas rollback command 42";

    private static string? Route(RecallTestStore store, string route)
    {
        if (route == "expert") store.Dispatcher.CreateExpert("routes", Query);
        return route == "direct" ? Ns : null;
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void RequestedCountIsAvailableFromOneNamespace(string route)
    {
        using var store = new RecallTestStore();
        for (int i = 0; i < 8; i++) store.Add($"fact-{i}", Ns, Query);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Route(store, route), k: 7,
            expandGraph: false, spectralMode: "none"));
        Assert.Equal(route == "expert" ? "expert_routed" : route, result.Strategy);
        Assert.Equal(7, result.Results.Count);
    }

    [Theory]
    [InlineData("direct", false)]
    [InlineData("direct", true)]
    [InlineData("expert", false)]
    [InlineData("expert", true)]
    [InlineData("broadcast", false)]
    [InlineData("broadcast", true)]
    public void SummaryPreferenceIsHonoredAfterReranking(string route, bool hybrid)
    {
        using var store = new RecallTestStore();
        store.Add("source", Ns, Query);
        store.Add("summary", Ns, "Atlas deployment overview", cosine: 0.7f, summary: true);
        var ns = Route(store, route);
        var normal = Assert.IsType<RecallResult>(store.Tools().Recall(Query, ns, k: 1, hybrid: hybrid,
            summaryFirst: false, expandGraph: false, spectralMode: "none"));
        var preferred = Assert.IsType<RecallResult>(store.Tools().Recall(Query, ns, k: 1, hybrid: hybrid,
            summaryFirst: true, expandGraph: false, spectralMode: "none"));
        Assert.Equal("source", Assert.Single(normal.Results).Id);
        Assert.Equal("summary", Assert.Single(preferred.Results).Id);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void HybridChoiceSurvivesArchiveFallback(string route)
    {
        using var store = new RecallTestStore();
        store.Add("vector-hit", Ns, "unrelated garden planning", cosine: 0.8f);
        store.Add("keyword-hit", Ns, Query, cosine: 0.6f);
        var ns = Route(store, route);
        var vector = Assert.IsType<RecallResult>(store.Tools().Recall(Query, ns, k: 2, hybrid: false,
            rerank: false, expandGraph: false, spectralMode: "none"));
        var hybrid = Assert.IsType<RecallResult>(store.Tools().Recall(Query, ns, k: 2, hybrid: true,
            rerank: false, expandGraph: false, spectralMode: "none"));
        Assert.Equal("vector-hit", vector.Results[0].Id);
        Assert.Equal("keyword-hit", hybrid.Results[0].Id);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void TokenRerankingWorksWithVectorOnlySearch(string route)
    {
        using var store = new RecallTestStore();
        store.Add("vector-hit", Ns, "unrelated garden planning", cosine: 0.99f);
        store.Add("answer", Ns, Query, cosine: 0.9f);
        var ns = Route(store, route);
        var plain = Assert.IsType<RecallResult>(store.Tools().Recall(Query, ns, hybrid: false,
            rerank: false, expandGraph: false, spectralMode: "none"));
        var reranked = Assert.IsType<RecallResult>(store.Tools().Recall(Query, ns, hybrid: false,
            rerank: true, expandGraph: false, spectralMode: "none"));
        Assert.Equal("vector-hit", plain.Results[0].Id);
        Assert.Equal("answer", reranked.Results[0].Id);
    }

    [Theory]
    [InlineData("direct", false)]
    [InlineData("expert", false)]
    [InlineData("broadcast", false)]
    [InlineData("direct", true)]
    [InlineData("expert", true)]
    [InlineData("broadcast", true)]
    public void MinimumScoreIsNotRelaxedByArchiveFallback(string route, bool hybrid)
    {
        using var store = new RecallTestStore();
        store.Add("below-threshold", Ns, Query, cosine: 0.8f, state: "archived");
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Route(store, route), minScore: 0.95f,
            hybrid: hybrid, rerank: false, expandGraph: false, spectralMode: "none"));
        Assert.Empty(result.Results);
        Assert.Equal("archived", store.Index.Get("below-threshold", Ns, tenantId: "")!.LifecycleState);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void ArchiveFallbackSkipsIncompatibleEmbeddingDimensions(string route)
    {
        using var store = new RecallTestStore();
        store.Index.Upsert(new CognitiveEntry("old-model", [1f, 0f, 0f], Ns, Query, lifecycleState: "archived"));
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Route(store, route)));
        Assert.Empty(result.Results);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void GraphExpansionHonorsOptionAndTracksNeighborNamespace(string route)
    {
        using var store = new RecallTestStore();
        store.Add("answer", Ns, Query);
        var neighbor = store.Add("support", "supporting-notes", "rollback prerequisite", cosine: 0f);
        neighbor.Metadata["source"] = "runbook";
        store.Graph.AddEdge(new GraphEdge("answer", "support", "elaborates"));
        var ns = Route(store, route);
        var without = Assert.IsType<RecallResult>(store.Tools().Recall(Query, ns,
            hybrid: false, expandGraph: false, spectralMode: "none"));
        Assert.DoesNotContain(without.Results, r => r.Id == "support");
        int accessBefore = neighbor.AccessCount;
        var with = Assert.IsType<RecallResult>(store.Tools().Recall(Query, ns,
            hybrid: false, expandGraph: true, spectralMode: "none"));
        Assert.Contains(with.Results, r => r.Id == "support");
        Assert.Equal("runbook", with.Results.Single(r => r.Id == "support").Metadata!["source"]);
        Assert.Equal(accessBefore + 1, neighbor.AccessCount);
    }

    [Theory]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void ArchiveRecoveryAndAccessAreLimitedToReturnedResults(string route)
    {
        using var store = new RecallTestStore();
        store.Add("selected", Ns, Query, state: "archived");
        var unselected = store.Add("unselected", route == "broadcast" ? "other-project" : Ns,
            "earlier rollback reference", cosine: 0.8f, state: "archived");
        int accessBefore = unselected.AccessCount;
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Route(store, route), k: 1,
            hybrid: false, rerank: false, expandGraph: false, spectralMode: "none"));
        Assert.Equal("selected", Assert.Single(result.Results).Id);
        Assert.Equal("stm", result.Results[0].LifecycleState);
        Assert.Equal("archived", unselected.LifecycleState);
        Assert.Equal(accessBefore, unselected.AccessCount);
    }

    [Fact]
    public void BroadcastDoesNotDeduplicateDifferentNamespacesByBareId()
    {
        using var store = new RecallTestStore();
        var first = store.Add("same-id", "atlas", "Atlas endpoint", cosine: 1f);
        var second = store.Add("same-id", "boreal", "Boreal endpoint", cosine: 0.9f);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall("endpoint", k: 2,
            hybrid: false, rerank: false, expandGraph: false, spectralMode: "none"));
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(2, first.AccessCount);
        Assert.Equal(2, second.AccessCount);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void BroadModeCannotExpandGraphWhenExpansionIsDisabled(string route)
    {
        using var store = new RecallTestStore();
        store.Add("leader", Ns, Query, cosine: .95f);
        store.Add("peer", Ns, Query, cosine: .90f);
        store.Add("isolated", Ns, Query, cosine: .80f);
        store.Add("hidden", Ns, "graph-only context", cosine: 0f);
        store.Graph.AddEdge(new GraphEdge("leader", "peer", "related_to"));
        store.Graph.AddEdge(new GraphEdge("peer", "hidden", "related_to"));
        var ns = Route(store, route);
        var without = Assert.IsType<RecallResult>(store.Tools().Recall(Query, ns, k: 3,
            hybrid: false, rerank: false, expandGraph: false, spectralMode: "broad"));
        Assert.DoesNotContain(without.Results, r => r.Id == "hidden");
        var with = Assert.IsType<RecallResult>(store.Tools().Recall(Query, ns, k: 3,
            hybrid: false, rerank: false, expandGraph: true, spectralMode: "broad"));
        Assert.Contains(with.Results, r => r.Id == "hidden");
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void SpecificModeUsesQualifyingDiffusionBasisOnEveryRoute(string route)
    {
        using var store = new RecallTestStore();
        for (int i = 0; i < 32; i++)
        {
            store.Add($"linked-{i}", Ns, Query, cosine: i < 2 ? .95f - i * .1f : .05f);
            for (int j = 0; j < i; j++)
                store.Graph.AddEdge(new GraphEdge($"linked-{i}", $"linked-{j}", "similar_to"));
        }
        store.Add("isolate", Ns, "related but isolated evidence", cosine: .8f);
        var ns = Route(store, route);
        var baseline = Assert.IsType<RecallResult>(store.Tools().Recall(Query, ns, k: 3,
            hybrid: false, rerank: false, expandGraph: false, spectralMode: "none"));
        var specific = Assert.IsType<RecallResult>(store.Tools().Recall(Query, ns, k: 3,
            hybrid: false, rerank: false, expandGraph: false, spectralMode: "specific"));
        Assert.Equal(baseline.Results.Select(r => r.Id), specific.Results.Select(r => r.Id));
        Assert.True(specific.Results[0].Score > baseline.Results[0].Score);
        Assert.Equal(baseline.Results.Single(r => r.Id == "isolate").Score,
            specific.Results.Single(r => r.Id == "isolate").Score);
    }

    [Theory]
    [InlineData("read", "archived")]
    [InlineData("write", "stm")]
    public void BroadcastRespectsTenantAndNamespacePermissionsDuringArchiveRecovery(string grant, string expectedState)
    {
        using var store = new RecallTestStore();
        store.Add("allowed", Ns, Query, state: "archived", tenant: "team-a");
        var denied = store.Add("denied", "private", Query, state: "archived", tenant: "team-a");
        var otherTenant = store.Add("other-tenant", Ns, Query, state: "archived", tenant: "team-b");
        store.Registry.EnsureOwnership(Ns, "alice", "team-a");
        store.Registry.EnsureOwnership("private", "alice", "team-a");
        Assert.Equal("shared", store.Registry.Share(Ns, "alice", "bob", grant, "team-a").Status);
        int deniedAccess = denied.AccessCount, otherAccess = otherTenant.AccessCount;
        var result = Assert.IsType<RecallResult>(store.Tools("bob", "team-a").Recall(Query));
        Assert.Equal("broadcast", result.Strategy);
        Assert.Equal("allowed", Assert.Single(result.Results).Id);
        Assert.Equal(expectedState, result.Results[0].LifecycleState);
        Assert.Equal("archived", denied.LifecycleState);
        Assert.Equal("archived", otherTenant.LifecycleState);
        Assert.Equal(deniedAccess, denied.AccessCount);
        Assert.Equal(otherAccess, otherTenant.AccessCount);
    }

    [Fact]
    public void CoreSearchKeepsDiversityWhenSummariesArePreferred()
    {
        using var store = new RecallTestStore();
        store.Add("summary", Ns, "Atlas overview", summary: true);
        store.Add("duplicate", Ns, "Atlas repeated overview");
        store.Add("distinct", Ns, "Atlas separate operational detail", cosine: .5f);
        var request = new SearchRequest
        {
            Query = [1f, 0f], Namespace = Ns, TenantId = "", K = 2, MinScore = .3f,
            SummaryFirst = true, DiversityLambda = 0f
        };
        Assert.Equal(new[] { "summary", "duplicate" }, store.Index.Search(request).Select(r => r.Id));
        Assert.Equal(new[] { "summary", "distinct" },
            store.Index.Search(request with { Diversity = true }).Select(r => r.Id));
    }
}
