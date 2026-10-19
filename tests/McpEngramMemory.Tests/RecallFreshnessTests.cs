using McpEngramMemory.Core.Models;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class RecallFreshnessTests
{
    private const string Ns = "expert_freshness";
    private const string Query = "What is the gateway timeout now?";
    private const string Old = "Superseded gateway timeout decision: use 30 seconds. Replaced by the incident review.";
    private const string Current = "Gateway timeout is 45 seconds after the incident review.";

    private static string? Route(RecallTestStore store, string route, string query = Query)
    {
        if (route == "expert") store.Dispatcher.CreateExpert("freshness", query);
        return route == "direct" ? Ns : null;
    }

    [Theory]
    [InlineData("direct", true, 1)]
    [InlineData("expert", true, 1)]
    [InlineData("broadcast", true, 1)]
    [InlineData("direct", false, 1)]
    [InlineData("expert", false, 1)]
    [InlineData("broadcast", false, 1)]
    [InlineData("direct", true, 5)]
    [InlineData("expert", true, 5)]
    [InlineData("broadcast", true, 5)]
    public void ExplicitlySupersededFactsDoNotDisplaceCurrentAnswers(string route, bool hybrid, int k)
    {
        using var store = new RecallTestStore();
        // IDs deliberately carry no old/new status, and the obsolete entry is created last.
        store.Add("z-answer", Ns, Current, cosine: .85f);
        var old = store.Add("a-answer", Ns, Old, cosine: .99f);
        int before = old.AccessCount;
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Route(store, route),
            k: k, hybrid: hybrid, expandGraph: false, spectralMode: "none"));
        Assert.Equal("z-answer", result.Results[0].Id);
        if (k > 1) Assert.Contains(result.Results, r => r.Id == "a-answer");
        else Assert.Equal(before, old.AccessCount);
        Assert.Equal("ltm", old.LifecycleState);
    }

    [Theory]
    [InlineData("What was the gateway timeout before the incident review?")]
    [InlineData("Show the historical gateway timeout decision")]
    [InlineData("Compare the previous and current gateway timeouts")]
    public void HistoricalQueriesKeepRelevanceOrdering(string query)
    {
        using var store = new RecallTestStore();
        store.Add("a-answer", Ns, Old, cosine: 1f);
        store.Add("z-answer", Ns, Current, cosine: .6f);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(query, Ns,
            hybrid: false, expandGraph: false, spectralMode: "none"));
        Assert.Equal("a-answer", result.Results[0].Id);
    }

    [Fact]
    public void DisablingRerankingPreservesTheRawOrder()
    {
        using var store = new RecallTestStore();
        store.Add("a-answer", Ns, Old, cosine: .99f);
        store.Add("z-answer", Ns, Current, cosine: .8f);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: 1,
            hybrid: false, rerank: false, expandGraph: false, spectralMode: "none"));
        Assert.Equal("a-answer", Assert.Single(result.Results).Id);
    }

    [Fact]
    public void SummaryPairingCannotReintroduceAnObsoleteSourceAheadOfCurrentEvidence()
    {
        using var store = new RecallTestStore();
        store.Add("overview", Ns, "Gateway timeout overview", summary: true);
        store.Add("old-source", Ns, Old, cosine: .99f);
        store.Add("source", Ns, Current, cosine: .8f);
        store.Add("independent", Ns, "Gateway timeout configuration reference", cosine: .75f);
        store.Graph.AddEdge(new GraphEdge("overview", "old-source", "parent_child"));
        store.Graph.AddEdge(new GraphEdge("overview", "source", "parent_child"));
        var result = Assert.IsType<RecallResult>(store.Tools().Recall("gateway timeout", Ns, k: 3,
            hybrid: false, spectralMode: "broad"));
        Assert.DoesNotContain(result.Results, r => r.Id == "old-source");
        Assert.Contains(result.Results, r => r.Id == "source");
    }

    [Theory]
    [InlineData("[obsolete] The gateway timeout is 30 seconds.")]
    [InlineData("Superseded: the gateway timeout is 30 seconds.")]
    [InlineData("Superseded database region was east; it moved west.")]
    [InlineData("Obsolete endpoint note from April: use /health.")]
    [InlineData("Former owner was Maya. This ownership record is obsolete after the handover.")]
    [InlineData("This memory has been superseded.")]
    public void RecognizesExplicitRecordStatusWithoutUsingIdsOrFixtureLabels(string text)
    {
        Assert.True(RecallFreshness.IsSuperseded(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("This record is not obsolete.")]
    [InlineData("This record is no longer obsolete.")]
    [InlineData("This record is obsolete only if the migration finishes.")]
    [InlineData("This record will be superseded next month.")]
    [InlineData("Current timeout is 45 seconds; this replaces the earlier 30 second setting.")]
    [InlineData("Instructions: mark old memories as superseded or obsolete.")]
    [InlineData("Superseded records must remain searchable for historical questions.")]
    [InlineData("The obsolete TLS protocol is disabled by the current security policy.")]
    [InlineData("The word superseded was mentioned in the incident review.")]
    [InlineData("Example: 'This record is obsolete.' is a status declaration.")]
    public void MentionsNegationAndConditionalStatementsAreNotRecordStatus(string? text)
    {
        Assert.False(RecallFreshness.IsSuperseded(text));
    }

    [Theory]
    [InlineData("What is the gateway timeout now?", true)]
    [InlineData("Which timeout replaced the old 30 second setting?", true)]
    [InlineData("What replaces the previous timeout?", true)]
    [InlineData("Who superseded the former deployment owner?", true)]
    [InlineData("Which checks run before executing a rollback?", true)]
    [InlineData("Show the old timeout setting", false)]
    [InlineData("What was the gateway timeout?", false)]
    [InlineData("What was replaced by the new timeout?", false)]
    [InlineData("Compare current and previous settings", false)]
    [InlineData("Show the history of timeout changes", false)]
    [InlineData("Gateway timeout before the May incident review", false)]
    public void DistinguishesReplacementQuestionsFromHistoricalIntent(string query, bool preferCurrent)
    {
        Assert.Equal(preferCurrent, RecallFreshness.PrefersCurrent(query));
    }

    [Theory]
    [InlineData("direct", true)]
    [InlineData("expert", true)]
    [InlineData("broadcast", true)]
    [InlineData("direct", false)]
    public void AnOnlySupersededArchivedHitIsReturnedWithoutResurrection(string route, bool rerank)
    {
        using var store = new RecallTestStore();
        var old = store.Add("only-hit", Ns, Old, state: "archived");
        int before = old.AccessCount;
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Route(store, route),
            hybrid: false, rerank: rerank, expandGraph: false, spectralMode: "none"));
        Assert.Equal("only-hit", Assert.Single(result.Results).Id);
        Assert.Equal("archived", old.LifecycleState);
        Assert.Equal(before + 1, old.AccessCount);
    }

    [Fact]
    public void WideningDoesNotRelaxMinScoreOrResurrectAnUnselectedArchive()
    {
        using var store = new RecallTestStore();
        var old = store.Add("old", Ns, Old, state: "archived");
        var low = store.Add("replacement", Ns, Current, cosine: .7f, state: "archived");
        int before = low.AccessCount;
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: 1, minScore: .9f,
            hybrid: true, expandGraph: false, spectralMode: "none"));
        Assert.Equal("old", Assert.Single(result.Results).Id);
        Assert.Equal("archived", old.LifecycleState);
        Assert.Equal("archived", low.LifecycleState);
        Assert.Equal(before, low.AccessCount);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("broadcast")]
    public void CurrentnessCannotRetrieveAPrivateOrForeignReplacement(string route)
    {
        using var store = new RecallTestStore();
        store.Add("old", Ns, Old, tenant: "team-a");
        var denied = store.Add("private-replacement", "private", Current, tenant: "team-a");
        var foreign = store.Add("foreign-replacement", Ns, Current, tenant: "team-b");
        store.Registry.EnsureOwnership(Ns, "alice", "team-a");
        store.Registry.EnsureOwnership("private", "alice", "team-a");
        Assert.Equal("shared", store.Registry.Share(Ns, "alice", "bob", "read", "team-a").Status);
        store.Graph.AddEdge(new GraphEdge("old", "private-replacement", "contradicts", tenantId: "team-a"));
        int deniedBefore = denied.AccessCount, foreignBefore = foreign.AccessCount;
        var result = Assert.IsType<RecallResult>(store.Tools("bob", "team-a").Recall(Query,
            route == "direct" ? Ns : null, k: 1));
        Assert.Equal("old", Assert.Single(result.Results).Id);
        Assert.Equal(deniedBefore, denied.AccessCount);
        Assert.Equal(foreignBefore, foreign.AccessCount);
    }

    [Fact]
    public void ExplicitSummaryPriorityAndOriginalScoresArePreserved()
    {
        using var store = new RecallTestStore();
        store.Add("summary", Ns, "Superseded: gateway timeout overview", cosine: .99f, summary: true);
        store.Add("source", Ns, Current, cosine: .8f);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: 2, summaryFirst: true,
            hybrid: false, expandGraph: false, spectralMode: "none"));
        Assert.Equal("summary", result.Results[0].Id);
        var baseline = store.Index.Rerank(Query, store.Index.Search([1f, 0f], Ns, k: 2, tenantId: ""));
        Assert.All(result.Results, r => Assert.Equal(baseline.Single(b => b.Id == r.Id).Score, r.Score));
    }

    [Fact]
    public void StatusSelectionSurvivesRestartAndDoesNotRewriteStoredFacts()
    {
        using var store = new RecallTestStore();
        store.Add("a-answer", Ns, Old, cosine: .99f);
        store.Add("z-answer", Ns, Current, cosine: .85f);
        store.Restart();
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: 1,
            hybrid: false, expandGraph: false, spectralMode: "none"));
        Assert.Equal("z-answer", Assert.Single(result.Results).Id);
        Assert.Equal(Old, store.Index.Get("a-answer", Ns, "")!.Text);
        Assert.Equal("ltm", store.Index.Get("a-answer", Ns, "")!.LifecycleState);
    }

    [Fact]
    public void BroadGraphBoostCannotRestoreASupersededCandidateToTheTopResults()
    {
        using var store = new RecallTestStore();
        store.Add("leader", Ns, "Gateway timeout guidance", cosine: .95f);
        store.Add("peer-1", Ns, "Gateway timeout deployment", cosine: .9f);
        store.Add("peer-2", Ns, "Gateway timeout recovery", cosine: .8f);
        store.Add("a-stale", Ns, Old, cosine: 0f);
        foreach (var id in new[] { "peer-1", "peer-2", "a-stale" })
            store.Graph.AddEdge(new GraphEdge("leader", id, "similar_to"));
        var result = Assert.IsType<RecallResult>(store.Tools().Recall("gateway timeout", Ns, k: 3,
            hybrid: false, spectralMode: "broad"));
        Assert.DoesNotContain(result.Results, r => r.Id == "a-stale");
        Assert.Contains(result.Results, r => r.Id == "peer-2");
    }
}
