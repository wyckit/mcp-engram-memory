using McpEngramMemory.Core.Models;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public sealed class RecallHistoricalEvidenceTests
{
    private const string Ns = "expert_nectar";
    private const string Query = "What was Nectar production readiness endpoint before release 27?";
    private const string Answer = "[superseded] Nectar production readiness endpoint was /ready/v1 before release 27.";
    private const string Current = "Nectar production readiness endpoint is /ready/v2 after release 27.";
    private const string Noise = "Library catalogue volume 27: coastal birds, tidal plants and navigation charts.";

    [Theory]
    [InlineData("Show the historical gateway timeout decision")]
    [InlineData("List the previous gateway timeout decision")]
    [InlineData("Find the old gateway timeout decision")]
    public void HistoricalTopicRequestsKeepRelevantDecisions(string query)
    {
        var history = RecallHistorySelection.Parse(query)!;
        Assert.True(history.MatchesTopic("Superseded gateway timeout decision: use 30 seconds.", Ns));
        Assert.False(history.MatchesTopic(Noise, Ns));
    }

    [Theory]
    [InlineData("What was Show production readiness endpoint before release 27?")]
    [InlineData("Previous Show production readiness endpoint")]
    public void AProjectNamedShowIsNotRemovedAsAnInstruction(string query)
    {
        var history = RecallHistorySelection.Parse(query)!;
        Assert.True(history.MatchesTopic(Answer.Replace("Nectar", "Show"), "expert_show"));
        Assert.False(history.MatchesTopic(Answer, Ns));
    }

    [Theory]
    [InlineData("direct", true)]
    [InlineData("expert", true)]
    [InlineData("broadcast", true)]
    [InlineData("direct", false)]
    [InlineData("expert", false)]
    [InlineData("broadcast", false)]
    public void UnrelatedEvidenceCannotFillHistoricalResultsOrReturnThroughGraph(string route, bool hybrid)
    {
        using var store = new RecallTestStore();
        store.Add("old", Ns, Answer, .7f, state: "archived");
        store.Add("current", Ns, Current, .8f);
        store.Add("noise", Ns, Noise, .99f);
        store.Add("linked-noise", "library", Noise, .99f);
        store.Graph.AddEdge(new GraphEdge("old", "linked-noise", "related_to"));
        if (route == "expert") store.Dispatcher.CreateExpert("nectar", "Nectar production operations");
        foreach (bool restart in new[] { false, true })
        {
            if (restart) store.Restart();
            var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query,
                route == "direct" ? Ns : null, hybrid: hybrid));
            Assert.Equal("old", result.Results[0].Id);
            Assert.All(result.Results, r => Assert.Contains(r.Id, new[] { "old", "current" }));
            Assert.Equal("archived", store.Index.Get("old", Ns, "")!.LifecycleState);
            Assert.Equal(1, store.Index.Get("noise", Ns, "")!.AccessCount);
            Assert.Equal(1, store.Index.Get("linked-noise", "library", "")!.AccessCount);
        }
    }

    [Fact]
    public void ExpertContainingOnlyUnrelatedHistoryContextFallsBackToEligibleEvidence()
    {
        using var store = new RecallTestStore();
        store.Dispatcher.CreateExpert("nectar", "Nectar production operations");
        store.Add("noise", Ns, Noise);
        store.Add("old", "nectar-archive", Answer, .7f, state: "archived");
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query));
        Assert.Equal("broadcast", result.Strategy);
        Assert.Equal("old", Assert.Single(result.Results).Id);
        Assert.Equal(1, store.Index.Get("noise", Ns, "")!.AccessCount);
        Assert.Empty(Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns)).Results);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnknownPeriodRetainsTopicContextEvenWhenNoiseFillsTheCandidateWindow(bool hybrid)
    {
        using var store = new RecallTestStore();
        for (int i = 0; i < 100; i++) store.Add("noise-" + i, Ns, Noise + " " + i, .99f);
        store.Add("current", Ns, Current, .7f);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: 1, hybrid: hybrid));
        Assert.Equal("current", Assert.Single(result.Results).Id);
        Assert.False(RecallHistorySelection.Parse(Query)!.Prefers(Current, Ns));
        Assert.Empty(Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, minScore: .8f, hybrid: hybrid)).Results);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ExplicitOptOutsPreserveUnfilteredContext(bool rerank, bool summaryFirst)
    {
        using var store = new RecallTestStore();
        store.Add("noise", Ns, Noise, summary: summaryFirst);
        Assert.Equal("noise", Assert.Single(Assert.IsType<RecallResult>(store.Tools().Recall(
            Query, Ns, rerank: rerank, summaryFirst: summaryFirst)).Results).Id);
    }
}
