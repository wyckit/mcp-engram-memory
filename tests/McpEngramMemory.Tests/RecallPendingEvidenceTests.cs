using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public sealed class RecallPendingEvidenceTests
{
    public static TheoryData<string, string> FrozenAnswers => new()
    {
        { "absent-date", "Citrine production deployment ownership decision date is 2026-09-14." },
        { "absent-approval-owner", "Citrine production rollback approval owner is the Beacon team." },
        { "proposal-not-decision", "Citrine production scheduler selected is Quartz." },
        { "question-not-number", "Citrine production maximum queue depth is 2500 messages." },
        { "topic-not-answer", "Citrine production DNS provider is CloudHarbor." }
    };

    [Theory]
    [MemberData(nameof(FrozenAnswers))]
    public void FrozenNonAnswersAbstainAndNewAnswersRemainRecallable(string id, string answer)
    {
        var corpus = IndependentRecallValidation.Load();
        var q = corpus.Queries.Single(q => q.Id == id);
        foreach (string route in new[] { "direct", "expert", "broadcast" })
        {
            using var store = new RecallTestStore();
            foreach (var m in corpus.Entries) store.Add(m.Id, m.Ns, m.Text, .99f, state: "archived");
            if (route == "expert") store.Dispatcher.CreateExpert("citrine_ops", "Citrine operations");
            string? ns = route == "direct" ? q.Ns : null;
            var absent = Assert.IsType<RecallResult>(store.Tools().Recall(q.Query, ns));
            Assert.Empty(absent.Results);
            Assert.Equal("insufficient_evidence", absent.QueryEvidence?.Status);
            store.Add("answer", q.Ns, answer, .7f, state: "archived");
            store.Restart();
            var present = Assert.IsType<RecallResult>(store.Tools().Recall(q.Query, ns));
            Assert.Equal("answer", Assert.Single(present.Results).Id);
            foreach (var m in corpus.Entries)
            {
                Assert.Equal(1, store.Index.Get(m.Id, m.Ns, "")!.AccessCount);
                Assert.Equal("archived", store.Index.Get(m.Id, m.Ns, "")!.LifecycleState);
            }
        }
    }

    [Theory]
    [InlineData("Nectar production queue review asks: what is the maximum queue depth?")]
    [InlineData("Nectar production maximum queue depth is still to be recorded.")]
    [InlineData("Nectar production maximum queue depth: to be recorded.")]
    [InlineData("Nectar production notes list open questions about maximum queue depth.")]
    [InlineData("Nectar production proposes a maximum queue depth of 300.")]
    [InlineData("Nectar production maximum queue depth has not been selected.")]
    public void EmbeddedQuestionsPendingValuesAndProposalsAreNotFacts(string text) =>
        Assert.False(RecallQueryEvidence.Parse("What is Nectar production maximum queue depth?")!.Matches(text, "expert_nectar"));

    [Theory]
    [InlineData("On what date was Nectar production deployment ownership decided?", "Nectar production deployment ownership decision date is 2026-09-14.", true)]
    [InlineData("On what date was Nectar production deployment ownership decided?", "Nectar production deployment ownership was decided on September 14, 2026.", true)]
    [InlineData("On what date was Nectar production deployment ownership decided?", "Nectar production deployment ownership decision date is 14/09/2026.", true)]
    [InlineData("On what date was Nectar production deployment ownership decided?", "Cedar production deployment ownership decision date is 2026-09-14.", false)]
    [InlineData("On what date was Nectar production deployment ownership decided?", "Nectar staging deployment ownership decision date is 2026-09-14.", false)]
    [InlineData("On what date was Nectar production deployment ownership decided?", "Nectar production deployment owner is the Harbor team.", false)]
    [InlineData("On what date was Nectar production deployment ownership decided?", "Nectar production deployment ownership decision date is still to be recorded.", false)]
    [InlineData("Who owns Nectar production rollback approval?", "Nectar production deployment owner is the Harbor team.", false)]
    [InlineData("Who owns Nectar production rollback approval?", "Nectar production rollback approval owner is the Harbor team.", true)]
    [InlineData("Which scheduler does Nectar production use?", "Nectar production proposes Quartz, but no scheduler has been selected.", false)]
    [InlineData("Which scheduler does Nectar production use?", "Nectar production scheduler is Quartz, but its retry count is unknown.", true)]
    [InlineData("Which scheduler does Nectar production use?", "Nectar production scheduler is Quartz, but its notes ask: which retry count should we use?", true)]
    [InlineData("Which scheduler was proposed for Nectar production?", "Nectar production proposes Quartz as its scheduler.", true)]
    [InlineData("Which scheduler was proposed for Nectar production?", "Nectar production scheduler proposal is Quartz.", true)]
    [InlineData("Which document lists Nectar production scheduler proposals?", "Nectar production scheduler proposals are documented in the handbook.", true)]
    [InlineData("Why is Nectar production scheduler undecided?", "Nectar production scheduler is undecided because the benchmark is incomplete.", true)]
    [InlineData("What is Nectar production maximum queue depth?", "Nectar production queue review asks: what is the maximum queue depth? The maximum queue depth is 300.", true)]
    [InlineData("What is Nectar production maximum queue depth?", "Nectar production maximum queue depth is 300; the ownership review asks: who approves it?", true)]
    [InlineData("Which DNS provider does Nectar production use?", "Nectar production DNS provider is CloudHarbor without naming its billing contact.", true)]
    [InlineData("Which DNS provider does Nectar production use?", "Nectar production DNS provider is CloudHarbor, without naming its billing contact.", true)]
    [InlineData("Which DNS provider does Nectar production use?", "Without naming its billing contact, Nectar production DNS provider is CloudHarbor.", true)]
    [InlineData("Which DNS provider does Nectar production use?", "Nectar production notes list open questions about the DNS provider, without naming a provider.", false)]
    [InlineData("What is Nectar production maximum queue depth?", "Nectar production notes ask about queue depth. The staging maximum queue depth is 300.", false)]
    [InlineData("What is Nectar production maximum queue depth?", "Nectar production notes ask about queue depth. Cedar production maximum queue depth is 300.", false)]
    public void RequestedFactsAndContextKeepTheirMeaning(string query, string text, bool matches)
    {
        foreach (string phrasing in new[] { query, query.ToLowerInvariant() })
            Assert.Equal(matches, RecallQueryEvidence.Parse(phrasing)!.Matches(text, "expert_nectar"));
    }

    [Theory]
    [InlineData("Nectar production index lists deployment, readiness, backups and payments as topics for a future workshop.", false)]
    [InlineData("Nectar production owner is Harbor. Its index lists deployment, readiness, backups and payments as topics for a future workshop.", true)]
    [InlineData("Nectar production recovery planning note asks the team to review backup diagrams and label the screenshots.", false)]
    [InlineData("Nectar production proposes Quartz, Hangfire and other schedulers for review.", false)]
    public void ListsQuestionsAndProposalsDoNotBecomeSourceEvidence(string text, bool expected) =>
        Assert.Equal(expected, BroadRecallIntent.HasSourceEvidence(text));

    [Theory]
    [MemberData(nameof(FrozenAnswers))]
    public void RecoveredFactsRespectPermissionsTenantsThresholdsAndArchiveState(string id, string answer)
    {
        var corpus = IndependentRecallValidation.Load();
        var q = corpus.Queries.Single(q => q.Id == id);
        using var store = new RecallTestStore();
        foreach (string ns in new[] { q.Ns, "private", "empty" }) store.Registry.EnsureOwnership(ns, "owner", "team");
        store.Registry.Share(q.Ns, "owner", "reader", "read", "team");
        store.Registry.Share("empty", "owner", "reader", "read", "team");
        foreach (var m in corpus.Entries) store.Add(m.Id, q.Ns, m.Text, tenant: "team", state: "archived");
        store.Add("same", "private", "PRIVATE_CANARY " + answer, tenant: "team", state: "archived");
        store.Add("same", q.Ns, "FOREIGN_CANARY " + answer, tenant: "foreign", state: "archived");
        store.Add("same", q.Ns, "LEGACY_CANARY " + answer, state: "archived");
        store.Add("wrong-project", q.Ns, answer.Replace("Citrine", "Mariner"), tenant: "team", state: "archived");
        store.Add("wrong-environment", q.Ns, answer.Replace("production", "staging"), tenant: "team", state: "archived");
        Assert.Empty(Assert.IsType<RecallResult>(store.Tools("reader", "team").Recall(q.Query)).Results);
        store.Add("answer", q.Ns, answer, .6f, tenant: "team", state: "archived");
        store.Add("hidden-graph", "private", "GRAPH_CANARY " + answer, tenant: "team", state: "archived");
        store.Graph.AddEdge(new GraphEdge("answer", "hidden-graph", "elaborates", tenantId: "team"));
        Assert.Contains(store.Graph.GetAllEdges("team"), e => e.SourceId == "answer" && e.TargetId == "hidden-graph");
        store.Restart();
        var tools = store.Tools("reader", "team");
        foreach (string? ns in new string?[] { q.Ns, null })
        {
            Assert.Empty(Assert.IsType<RecallResult>(tools.Recall(q.Query, ns, minScore: .8f)).Results);
            var result = Assert.IsType<RecallResult>(tools.Recall(q.Query, ns));
            Assert.Equal("answer", Assert.Single(result.Results).Id);
            Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(result));
        }
        Assert.Empty(Assert.IsType<RecallResult>(tools.Recall(q.Query, "private")).Results);
        Assert.Empty(Assert.IsType<RecallResult>(tools.Recall(q.Query, "empty")).Results);
        Assert.Equal("archived", store.Index.Get("answer", q.Ns, "team")!.LifecycleState);
        foreach (var key in new[] { ("same", "private", "team"), ("same", q.Ns, "foreign"), ("same", q.Ns, ""),
                     ("wrong-project", q.Ns, "team"), ("wrong-environment", q.Ns, "team"), ("hidden-graph", "private", "team") })
        {
            var entry = store.Index.Get(key.Item1, key.Item2, key.Item3)!;
            Assert.Equal(1, entry.AccessCount);
            Assert.Equal("archived", entry.LifecycleState);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ExplicitOptOutsRetainQuestionAndProposalContext(bool rerank, bool summaryFirst)
    {
        using var store = new RecallTestStore();
        store.Add("note", "expert_nectar", "Nectar production proposes Quartz as its scheduler.");
        var result = Assert.IsType<RecallResult>(store.Tools().Recall("Which scheduler does Nectar production use?",
            "expert_nectar", rerank: rerank, summaryFirst: summaryFirst));
        Assert.Equal("note", Assert.Single(result.Results).Id);
    }
}
