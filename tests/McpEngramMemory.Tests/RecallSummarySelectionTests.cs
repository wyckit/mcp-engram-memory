using McpEngramMemory.Core.Models;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class RecallSummarySelectionTests
{
    private const string Ns = "expert_summary";
    private const string Query = "deployment overview";

    private static string? Route(RecallTestStore store, string route)
    {
        if (route == "expert") store.Dispatcher.CreateExpert("summary", Query);
        return route == "direct" ? Ns : null;
    }

    [Theory]
    [InlineData("direct", false)]
    [InlineData("expert", false)]
    [InlineData("broadcast", false)]
    [InlineData("direct", true)]
    [InlineData("expert", true)]
    [InlineData("broadcast", true)]
    public void SummaryHubDoesNotCreateEvidenceOfClusterDominance(string route, bool summaryFirst)
    {
        using var store = new RecallTestStore();
        store.Add("overview", Ns, Query, cosine: .99f, summary: true);
        store.Add("answer", Ns, "deployment steps", cosine: .90f);
        store.Add("unrelated", Ns, "database details", cosine: .40f);
        store.Graph.AddEdge(new GraphEdge("overview", "answer", "parent_child"));
        store.Graph.AddEdge(new GraphEdge("overview", "unrelated", "parent_child"));
        var ns = Route(store, route);
        var none = Assert.IsType<RecallResult>(store.Tools().Recall(Query, ns, k: 3, hybrid: false,
            rerank: false, summaryFirst: summaryFirst, spectralMode: "none"));
        var broad = Assert.IsType<RecallResult>(store.Tools().Recall(Query, ns, k: 3, hybrid: false,
            rerank: false, summaryFirst: summaryFirst, spectralMode: "broad"));
        Assert.Equal(none.Results.Select(r => (r.Id, r.Score)), broad.Results.Select(r => (r.Id, r.Score)));
        Assert.True(broad.Results[0].IsSummaryNode); // A relevant overview remains useful.
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void BroadTraversalDoesNotCrossSummaryIntoUnrelatedSourceGroup(string route)
    {
        using var store = new RecallTestStore();
        store.Add("overview", Ns, Query, cosine: .99f, summary: true);
        store.Add("leader", Ns, "deployment steps", cosine: .95f);
        store.Add("peer-1", Ns, "deployment prerequisite", cosine: .90f);
        store.Add("peer-2", Ns, "deployment recovery", cosine: .80f);
        store.Add("other", Ns, "different topic", cosine: .75f);
        store.Add("a-irrelevant", Ns, "unrelated detail", cosine: 0f);
        store.Graph.AddEdge(new GraphEdge("leader", "peer-1", "similar_to"));
        store.Graph.AddEdge(new GraphEdge("leader", "peer-2", "similar_to"));
        store.Graph.AddEdge(new GraphEdge("leader", "overview", "parent_child"));
        store.Graph.AddEdge(new GraphEdge("overview", "a-irrelevant", "parent_child"));
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Route(store, route),
            hybrid: false, rerank: false, spectralMode: "broad"));
        Assert.DoesNotContain(result.Results, r => r.Id == "a-irrelevant");
        Assert.Contains(result.Results, r => r.Id == "peer-2" && r.Score > .80f);
        Assert.Equal(.95f * .95f, result.Results.Single(r => r.Id == "peer-2").Score, precision: 5);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void SummaryDrilldownRanksSourcesByQueryAndOnlyUpdatesSelectedMemories(string route)
    {
        using var store = new RecallTestStore();
        store.Add("overview", Ns, Query, cosine: .99f, summary: true);
        var filler = store.Add("filler", Ns, "general reference", cosine: .98f);
        store.Add("filler-2", Ns, "another reference", cosine: .97f);
        var best = store.Add("z-best", Ns, "most relevant source", cosine: .9f);
        var next = store.Add("y-next", Ns, "next relevant source", cosine: .8f);
        var omitted = store.Add("a-weaker", Ns, "weaker source", cosine: .7f);
        var below = store.Add("below-threshold", Ns, "off topic", cosine: .2f);
        store.Index.Upsert(new CognitiveEntry("old-model", [1f, 0f, 0f], Ns, "incompatible embedding"));
        foreach (var id in new[] { "a-weaker", "y-next", "z-best", "below-threshold", "old-model" })
            store.Graph.AddEdge(new GraphEdge("overview", id, "parent_child"));
        var accesses = new[] { filler, best, next, omitted, below }.Select(e => e.AccessCount).ToArray();
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Route(store, route), k: 3,
            minScore: .5f, hybrid: false, rerank: false, spectralMode: "none"));

        Assert.Equal(new[] { "overview", "z-best", "y-next" }, result.Results.Select(r => r.Id));
        Assert.Equal(.99f * .9f, result.Results[1].Score, precision: 5);
        Assert.Equal(.99f * .8f, result.Results[2].Score, precision: 5);
        Assert.Equal(accesses[0], filler.AccessCount);
        Assert.Equal(accesses[1] + 1, best.AccessCount);
        Assert.Equal(accesses[2] + 1, next.AccessCount);
        Assert.Equal(accesses[3], omitted.AccessCount);
        Assert.Equal(accesses[4], below.AccessCount);
    }

    [Theory]
    [InlineData(1, false, true, "strongest")]
    [InlineData(2, false, true, "strongest,overview")]
    [InlineData(3, false, true, "strongest,overview,source")]
    [InlineData(4, false, true, "strongest,overview,source,second-source")]
    [InlineData(3, true, true, "overview,strongest,filler")]
    [InlineData(3, false, false, "strongest,overview,filler")]
    public void SourceSelectionRespectsBudgetStrongestHitAndExplicitOptions(
        int k, bool summaryFirst, bool expandGraph, string expected)
    {
        using var store = new RecallTestStore();
        store.Add("strongest", Ns, "exact answer", cosine: 1f);
        store.Add("overview", Ns, Query, cosine: .99f, summary: true);
        store.Add("filler", Ns, "general reference", cosine: .98f);
        store.Add("filler-2", Ns, "another reference", cosine: .97f);
        store.Add("source", Ns, "source detail", cosine: .9f);
        store.Add("second-source", Ns, "more detail", cosine: .8f);
        store.Graph.AddEdge(new GraphEdge("overview", "source", "parent_child"));
        store.Graph.AddEdge(new GraphEdge("overview", "second-source", "parent_child"));
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: k, hybrid: false,
            rerank: false, summaryFirst: summaryFirst, expandGraph: expandGraph, spectralMode: "none"));
        Assert.Equal(expected.Split(','), result.Results.Select(r => r.Id));
        Assert.Equal(1f, result.Results.Single(r => r.Id == "strongest").Score);
    }

    [Fact]
    public void MultipleSummariesShareTheBudgetWithoutDuplicatingSources()
    {
        using var store = new RecallTestStore();
        store.Add("overview-a", Ns, Query, cosine: .99f, summary: true);
        store.Add("overview-b", Ns, Query, cosine: .98f, summary: true);
        for (int i = 0; i < 3; i++) store.Add($"filler-{i}", Ns, "general reference", cosine: .97f - .01f * i);
        store.Add("shared-source", Ns, "shared context", cosine: .90f);
        store.Add("a-source", Ns, "first overview detail", cosine: .80f);
        store.Add("b-source", Ns, "second overview detail", cosine: .70f);
        store.Graph.AddEdge(new GraphEdge("overview-a", "shared-source", "parent_child"));
        store.Graph.AddEdge(new GraphEdge("overview-a", "a-source", "parent_child"));
        store.Graph.AddEdge(new GraphEdge("overview-b", "shared-source", "parent_child"));
        store.Graph.AddEdge(new GraphEdge("overview-b", "b-source", "parent_child"));
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, hybrid: false,
            rerank: false, spectralMode: "none"));
        Assert.Equal(new[] { "overview-a", "shared-source", "a-source", "overview-b", "b-source" },
            result.Results.Select(r => r.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnlinkedOrContradictingMemoriesAreNotForcedAlongsideASummary(bool contradicts)
    {
        using var store = new RecallTestStore();
        store.Add("overview", Ns, Query, summary: true);
        store.Add("runner-up", Ns, "independent answer", cosine: .95f);
        store.Add("candidate", Ns, "other fact", cosine: .8f);
        if (contradicts) store.Graph.AddEdge(new GraphEdge("overview", "candidate", "contradicts"));
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: 2,
            hybrid: false, rerank: false, spectralMode: "none"));
        Assert.Equal(new[] { "overview", "runner-up" }, result.Results.Select(r => r.Id));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("broadcast")]
    public void LinkedSourcesRespectPermissionsTenantsAndActualSourceNamespace(string route)
    {
        using var store = new RecallTestStore();
        store.Add("overview", Ns, Query, summary: true, tenant: "team-a");
        store.Add("filler", Ns, "general reference", cosine: .95f, tenant: "team-a");
        var source = store.Add("source", "shared-notes", "source detail", cosine: .8f, tenant: "team-a");
        var denied = store.Add("denied", "private", "secret detail", cosine: .9f, tenant: "team-a");
        var foreign = store.Add("foreign", "shared-notes", "foreign detail", tenant: "team-b");
        foreach (var ns in new[] { Ns, "shared-notes", "private" })
            store.Registry.EnsureOwnership(ns, "alice", "team-a");
        foreach (var ns in new[] { Ns, "shared-notes" })
            Assert.Equal("shared", store.Registry.Share(ns, "alice", "bob", "read", "team-a").Status);
        foreach (var id in new[] { "source", "denied", "foreign" })
            store.Graph.AddEdge(new GraphEdge("overview", id, "parent_child", tenantId: "team-a"));
        int sourceBefore = source.AccessCount, deniedBefore = denied.AccessCount, foreignBefore = foreign.AccessCount;
        var result = Assert.IsType<RecallResult>(store.Tools("bob", "team-a").Recall(Query,
            route == "direct" ? Ns : null, k: 2, hybrid: false, rerank: false, spectralMode: "none"));
        Assert.Equal(new[] { "overview", "source" }, result.Results.Select(r => r.Id));
        Assert.Equal(sourceBefore + 1, source.AccessCount);
        Assert.Equal(deniedBefore, denied.AccessCount);
        Assert.Equal(foreignBefore, foreign.AccessCount);
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("source")]
    public void AmbiguousTopologyCannotSelectLinkedSources(string twinId)
    {
        using var store = new RecallTestStore();
        store.Add("overview", Ns, Query, summary: true);
        store.Add("filler", Ns, "independent answer", cosine: .95f);
        store.Add("source", Ns, "source detail", cosine: .8f);
        store.Graph.AddEdge(new GraphEdge("overview", "source", "parent_child"));
        store.Add(twinId, "other-project", "same id in another namespace");
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: 2,
            hybrid: false, rerank: false, spectralMode: "none"));
        Assert.Equal(new[] { "overview", "filler" }, result.Results.Select(r => r.Id));
    }

    [Fact]
    public void BroadTraversalStopsAtASummaryOutsideTheInitialCandidatePool()
    {
        using var store = new RecallTestStore();
        store.Add("leader", Ns, Query, cosine: .95f);
        store.Add("peer-1", Ns, "deployment prerequisite", cosine: .90f);
        store.Add("peer-2", Ns, "deployment recovery", cosine: .80f);
        store.Add("other", Ns, "different topic", cosine: .75f);
        store.Add("other-2", Ns, "another topic", cosine: .70f);
        store.Add("bridge", Ns, "low relevance cluster member", cosine: 0f);
        store.Add("hidden-overview", Ns, "overview beyond first hop", cosine: 0f, summary: true);
        store.Add("a-irrelevant", Ns, "unrelated detail behind overview", cosine: 0f);
        store.Graph.AddEdge(new GraphEdge("leader", "peer-1", "similar_to"));
        store.Graph.AddEdge(new GraphEdge("leader", "peer-2", "similar_to"));
        store.Graph.AddEdge(new GraphEdge("peer-2", "bridge", "similar_to"));
        store.Graph.AddEdge(new GraphEdge("bridge", "hidden-overview", "parent_child"));
        store.Graph.AddEdge(new GraphEdge("hidden-overview", "a-irrelevant", "parent_child"));
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns,
            hybrid: false, rerank: false, spectralMode: "broad"));
        Assert.Contains(result.Results, r => r.Id == "bridge");
        Assert.DoesNotContain(result.Results, r => r.Id is "hidden-overview" or "a-irrelevant");
    }

    [Fact]
    public void SummaryWithoutEligibleSourcesRemainsUsableAndDoesNotRelaxMinScore()
    {
        using var store = new RecallTestStore();
        store.Add("overview", Ns, Query, summary: true);
        store.Add("below-threshold", Ns, "off topic", cosine: .2f);
        store.Index.Upsert(new CognitiveEntry("old-model", [1f, 0f, 0f], Ns, "incompatible embedding"));
        store.Graph.AddEdge(new GraphEdge("overview", "below-threshold", "parent_child"));
        store.Graph.AddEdge(new GraphEdge("overview", "old-model", "parent_child"));
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: 5,
            minScore: .5f, hybrid: false, rerank: false, spectralMode: "none"));
        Assert.Equal("overview", Assert.Single(result.Results).Id);
    }

    [Fact]
    public void AlreadyRetrievedSourceKeepsItsScoreAndStrongerSourcesStayBeforeSummary()
    {
        using var store = new RecallTestStore();
        store.Add("strong-source", Ns, "exact answer", cosine: .99f);
        store.Add("overview", Ns, Query, cosine: .95f, summary: true);
        store.Add("second-source", Ns, "supporting detail", cosine: .9f);
        store.Graph.AddEdge(new GraphEdge("overview", "strong-source", "parent_child"));
        store.Graph.AddEdge(new GraphEdge("overview", "second-source", "parent_child"));
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: 3,
            hybrid: false, rerank: false, spectralMode: "none"));
        Assert.Equal(new[] { "strong-source", "overview", "second-source" }, result.Results.Select(r => r.Id));
        Assert.Equal(.99f, result.Results[0].Score, precision: 5);
        Assert.Equal(.9f, result.Results[2].Score, precision: 5);
    }

    [Fact]
    public void SelectedGraphOnlyArchivedSourceIsNotResurrected()
    {
        using var store = new RecallTestStore();
        store.Add("overview", Ns, Query, cosine: .45f, summary: true);
        store.Add("live", Ns, "live reference", cosine: .44f);
        store.Add("local-archive", Ns, "old reference", cosine: .4f, state: "archived");
        var graphOnly = store.Add("graph-only", "supporting-notes", "archived source", cosine: .9f, state: "archived");
        store.Graph.AddEdge(new GraphEdge("overview", "graph-only", "parent_child"));
        int before = graphOnly.AccessCount;
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: 3,
            hybrid: false, rerank: false, spectralMode: "none"));
        Assert.Contains(result.Results, r => r.Id == "graph-only" && r.LifecycleState == "archived");
        Assert.Equal("archived", graphOnly.LifecycleState);
        Assert.Equal(before + 1, graphOnly.AccessCount);
    }
}
