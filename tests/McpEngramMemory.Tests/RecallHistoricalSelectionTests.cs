using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class RecallHistoricalSelectionTests
{
    private const string Ns = "expert_nectar";
    private const string Query = "What was Nectar production gateway timeout before the August review?";
    private const string Answer = "Nectar production gateway timeout was 90 seconds before the August review.";
    private const string Current = "Nectar production gateway timeout is 45 seconds after the August review.";

    [Theory]
    [InlineData(Answer, true)]
    [InlineData("Nectar production gateway timeout was 90 seconds prior to the August review.", true)]
    [InlineData("[superseded] Nectar production gateway timeout was 90 seconds. Replaced by the August review.", true)]
    [InlineData("Nectar production gateway timeout was 90 seconds. This record is obsolete after the August review.", true)]
    [InlineData(Current, false)]
    [InlineData("[superseded] Nectar production gateway timeout was 90 seconds before the September review.", false)]
    [InlineData("[superseded] Nectar production gateway timeout was 90 seconds before the September review, after the August review.", false)]
    [InlineData("Nectar production gateway timeout was 90 seconds, not before the August review.", false)]
    [InlineData("Nectar production gateway timeout was unknown before the August review.", false)]
    [InlineData("Nectar production gateway timeout was documented in the handbook before the August review.", false)]
    [InlineData("Nectar production gateway timeout was 90 seconds. Backups ran daily before the August review.", false)]
    [InlineData("Nectar production gateway timeout is 45 seconds. The August review discussed the previous timeout.", false)]
    [InlineData("Nectar production gateway timeout is 45 seconds after the August review, but its gateway timeout was unknown before the August review.", false)]
    [InlineData("Nectar production gateway timeout is 45 seconds after the August review, but the backup retention was 90 days before the August review.", false)]
    [InlineData("Nectar production deployment owner was Maya before the August review.", false)]
    public void PreferenceRequiresAnAnswerAndTheRequestedTemporalRelation(string text, bool preferred) =>
        Assert.Equal(preferred, RecallHistorySelection.Parse(Query)!.Prefers(text, Ns));

    [Theory]
    [InlineData("1 May 2025", true)]
    [InlineData("2 May 2025", false)]
    [InlineData("1 May 2024", false)]
    public void EventAnchorsRetainLiteralDatesWithoutInferringDateOrder(string date, bool preferred) =>
        Assert.Equal(preferred, RecallHistorySelection.Parse(Query.Replace("August", "1 May 2025"))!
            .Prefers(Answer.Replace("August", date), Ns));

    [Theory]
    [InlineData("What is Nectar production gateway timeout now?")]
    [InlineData("What replaced the old Nectar production gateway timeout?")]
    [InlineData("Compare the previous and current Nectar production gateway timeout")]
    [InlineData("Show the history of Nectar production gateway timeout")]
    [InlineData("What was Nectar production gateway timeout after the August review?")]
    [InlineData("What was Nectar production gateway timeout as of August?")]
    [InlineData("What was the original Nectar production gateway timeout?")]
    [InlineData("How was the Nectar certificate failure repaired?")]
    [InlineData("What was learned about graceful shutdown in Nectar restart incident?")]
    [InlineData("Please tell me what was learned before the August review?")]
    [InlineData("Why was Nectar production unavailable before the August review?")]
    public void CurrentComparisonAndUnsupportedPeriodsDoNotGuessAHistoricalWinner(string query) =>
        Assert.Null(RecallHistorySelection.Parse(query));

    [Theory]
    [InlineData(Query)]
    [InlineData("what was nectar production gateway timeout before the august review?")]
    [InlineData("Please tell me what was Nectar production gateway timeout before the August review?")]
    [InlineData("Previous Nectar production gateway timeout")]
    [InlineData("What was Nectar production gateway timeout?")]
    [InlineData("How long was Nectar production gateway timeout before the August review?")]
    public void SupportedPhrasingsPreferPastEvidence(string query) =>
        Assert.True(RecallHistorySelection.Parse(query)!.Prefers(Answer, Ns));

    [Fact]
    public void PreferenceRespectsInputBounds()
    {
        Assert.Null(RecallHistorySelection.Parse(Query + new string('x', 1024)));
        var history = RecallHistorySelection.Parse(Query)!;
        Assert.False(history.Prefers(Answer + new string('x', 16_384), Ns));
        Assert.False(history.Prefers(string.Concat(Enumerable.Repeat("Nectar production notes. ", 33)) + Answer, Ns));
    }

    [Fact]
    public void AHistoricalSummaryCannotHideItsSource()
    {
        using var store = new RecallTestStore();
        store.Add("summary", Ns, Answer, summary: true);
        store.Add("source", Ns, Answer, .8f, state: "archived");
        store.Graph.AddEdge(new GraphEdge("summary", "source", "parent_child"));
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: 1));
        Assert.Equal("source", Assert.Single(result.Results).Id);
        Assert.Equal("archived", store.Index.Get("source", Ns, "")!.LifecycleState);
        Assert.Equal(1, store.Index.Get("summary", Ns, "")!.AccessCount);
    }

    [Theory]
    [InlineData("Historical")]
    [InlineData("Previous")]
    [InlineData("Former")]
    [InlineData("Old")]
    [InlineData("[historical]")]
    [InlineData("[superseded] Historical")]
    [InlineData("[old]")]
    [InlineData("Original")]
    public void HistoricalLabelsCannotHideAContraryProjectInReturnedContext(string label)
    {
        foreach (string route in new[] { "direct", "expert", "broadcast" })
        {
            using var store = new RecallTestStore();
            store.Add("current", Ns, Current);
            store.Add("answer", Ns, Answer, .8f, state: "archived");
            store.Add("wrong", Ns, label + " " + Answer.Replace("Nectar", "Cedar"), .99f, state: "archived");
            if (route == "expert") store.Dispatcher.CreateExpert("nectar", "Nectar production operations");
            var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, route == "direct" ? Ns : null));
            Assert.Equal("answer", result.Results[0].Id);
            Assert.DoesNotContain("Cedar", JsonSerializer.Serialize(result));
            Assert.Equal(1, store.Index.Get("wrong", Ns, "")!.AccessCount);
        }
    }

    [Theory]
    [InlineData("direct", true)]
    [InlineData("expert", true)]
    [InlineData("broadcast", true)]
    [InlineData("direct", false)]
    [InlineData("expert", false)]
    [InlineData("broadcast", false)]
    public void HistoricalSourceSurvivesConfidentCurrentAnswersAndNoise(string route, bool hybrid)
    {
        using var store = new RecallTestStore();
        store.Add("current", Ns, Current, .99f);
        for (int i = 0; i < 100; i++)
            store.Add("noise-" + i, Ns, $"[superseded] Nectar production gateway timeout was {100 + i} seconds before the September review.", .98f, state: "archived");
        store.Add("answer", Ns, Answer, .7f, state: "archived");
        store.Add("reference", Ns, "Nectar production gateway timeout before the August review was documented in the handbook.", .99f, state: "archived");
        store.Add("wrong-project", Ns, Answer.Replace("Nectar", "Cedar"), .99f, state: "archived");
        store.Add("wrong-env", Ns, Answer.Replace("production", "staging"), .99f, state: "archived");
        store.Graph.AddEdge(new GraphEdge("current", "wrong-project", "related_to"));
        if (route == "expert") store.Dispatcher.CreateExpert("nectar", "Nectar production operations");
        string? ns = route == "direct" ? Ns : null;
        foreach (bool restart in new[] { false, true })
        {
            if (restart) store.Restart();
            var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, ns, k: 1, hybrid: hybrid));
            Assert.Equal("answer", Assert.Single(result.Results).Id);
            Assert.Null(result.Conflicts);
            Assert.Equal("archived", store.Index.Get("answer", Ns, "")!.LifecycleState);
        }
        foreach (string id in new[] { "current", "reference", "wrong-project", "wrong-env", "noise-0" })
            Assert.Equal(1, store.Index.Get(id, Ns, "")!.AccessCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoricalRecoveryPreservesTenantAclScopeAndThresholds(bool restart)
    {
        using var store = new RecallTestStore();
        foreach (string ns in new[] { Ns, "private", "empty" }) store.Registry.EnsureOwnership(ns, "owner", "team");
        store.Registry.Share(Ns, "owner", "reader", "read", "team");
        store.Registry.Share("empty", "owner", "reader", "read", "team");
        store.Add("current", Ns, Current, tenant: "team");
        store.Add("answer", Ns, Answer, .6f, state: "archived", tenant: "team");
        store.Add("answer", "private", "PRIVATE_CANARY " + Answer, tenant: "team");
        store.Add("answer", Ns, "FOREIGN_CANARY " + Answer, tenant: "other");
        store.Add("answer", Ns, "LEGACY_CANARY " + Answer);
        store.Graph.AddEdge(new GraphEdge("current", "answer", "related_to", tenantId: "team"));
        if (restart) store.Restart();
        var tools = store.Tools("reader", "team");
        foreach (string? ns in new string?[] { Ns, null })
        {
            var threshold = Assert.IsType<RecallResult>(tools.Recall(Query, ns, k: 1, minScore: .8f));
            Assert.Equal("current", Assert.Single(threshold.Results).Id);
            var result = Assert.IsType<RecallResult>(tools.Recall(Query, ns, k: 1));
            Assert.Equal("answer", Assert.Single(result.Results).Id);
            Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(result));
        }
        Assert.Empty(Assert.IsType<RecallResult>(tools.Recall(Query, "private")).Results);
        Assert.Empty(Assert.IsType<RecallResult>(tools.Recall(Query, "empty")).Results);
        Assert.Equal("archived", store.Index.Get("answer", Ns, "team")!.LifecycleState);
        Assert.Equal(1, store.Index.Get("answer", "private", "team")!.AccessCount);
        Assert.Equal(1, store.Index.Get("answer", Ns, "other")!.AccessCount);
        Assert.Equal(1, store.Index.Get("answer", Ns, "")!.AccessCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ExplicitOptOutsKeepTheirExistingSelection(bool rerank, bool summaryFirst)
    {
        using var store = new RecallTestStore();
        store.Add("current", Ns, Current, summary: summaryFirst);
        store.Add("answer", Ns, Answer, .6f, state: "archived");
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: 1,
            rerank: rerank, summaryFirst: summaryFirst));
        Assert.Equal("current", Assert.Single(result.Results).Id);
        Assert.Equal(1, store.Index.Get("answer", Ns, "")!.AccessCount);
    }

    [Fact]
    public void CurrentQuestionsAndComparisonsStillKeepTheirMeaning()
    {
        using var store = new RecallTestStore();
        store.Add("old", Ns, "[superseded] " + Answer);
        store.Add("current", Ns, Current, .8f);
        var tools = store.Tools();
        var current = Assert.IsType<RecallResult>(tools.Recall("What is Nectar production gateway timeout now?", Ns, k: 1));
        Assert.Equal("current", Assert.Single(current.Results).Id);
        const string comparison = "Compare previous and current Nectar production gateway timeout";
        var baseline = store.Index.Search(new SearchRequest { Query = [1f, 0f], QueryText = comparison,
            Namespace = Ns, TenantId = "", K = 5, MinScore = .3f, Hybrid = true, Rerank = true });
        var result = Assert.IsType<RecallResult>(tools.Recall(comparison, Ns, expandGraph: false, spectralMode: "none"));
        Assert.Equal(baseline.OrderByDescending(r => r.Score).ThenBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => (r.Id, r.Score)), result.Results.Select(r => (r.Id, r.Score)));
        Assert.Null(result.Conflicts);
    }
}
