using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class RecallQueryEvidenceTests
{
    private const string Ns = "expert_nectar";
    private const string Query = "What is Nectar staging readiness endpoint?";
    private const string Answer = "Nectar staging readiness endpoint is /stage/ready.";

    private static string? Route(RecallTestStore store, string route, string query = Query)
    {
        if (route == "expert") store.Dispatcher.CreateExpert("nectar", query);
        return route == "direct" ? Ns : null;
    }

    [Theory]
    [InlineData("direct", 1)]
    [InlineData("expert", 1)]
    [InlineData("broadcast", 1)]
    [InlineData("direct", 5)]
    [InlineData("expert", 5)]
    [InlineData("broadcast", 5)]
    public void RejectedFirstWindowDoesNotHideAnAnswerOrRecordAccess(string route, int k)
    {
        using var store = new RecallTestStore();
        var wrong = Enumerable.Range(0, 8).Select(i => store.Add("wrong-" + i, Ns,
            "Nectar production readiness endpoint is /prod/ready.", .99f)).ToArray();
        var answer = store.Add("answer", Ns, Answer, .6f);
        var ns = Route(store, route);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, ns, k: k, hybrid: false,
            expandGraph: false, spectralMode: "none"));
        Assert.Equal("answer", Assert.Single(result.Results).Id);
        Assert.Null(result.QueryEvidence);
        Assert.Equal(2, answer.AccessCount);
        Assert.All(wrong, e => Assert.Equal(1, e.AccessCount));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void EmptyEvidenceIsExplicitAndANewAnswerImmediatelyBecomesRecallable(string route)
    {
        using var store = new RecallTestStore();
        var wrong = store.Add("wrong", Ns, "nectar production readiness endpoint is /prod/ready.", state: "archived");
        var foreign = store.Add("foreign", Ns, "cedar staging readiness endpoint is /cedar/ready.");
        var ns = Route(store, route);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query.ToLowerInvariant(), ns));
        Assert.Empty(result.Results);
        Assert.Equal("insufficient_evidence", result.QueryEvidence!.Status);
        Assert.Null(result.Conflicts);
        Assert.Equal("archived", wrong.LifecycleState);
        Assert.Equal(1, wrong.AccessCount);
        Assert.Equal(1, foreign.AccessCount);
        store.Add("answer", Ns, Answer);
        result = Assert.IsType<RecallResult>(store.Tools().Recall(Query.ToLowerInvariant(), ns));
        Assert.Equal("answer", Assert.Single(result.Results).Id);
        Assert.False(JsonSerializer.SerializeToElement(result).TryGetProperty("queryEvidence", out _));
    }

    [Theory]
    [InlineData("Nectar production readiness endpoint is /prod.", false)]
    [InlineData("Cedar staging readiness endpoint is /cedar.", false)]
    [InlineData("cedar staging readiness endpoint is /cedar.", false)]
    [InlineData("Nectar staging guide. Cedar production readiness endpoint is /cedar.", false)]
    [InlineData("Nectar staging guide. The cedar readiness endpoint is /cedar.", false)]
    [InlineData("Nectar staging guide. The production readiness endpoint is /prod.", false)]
    [InlineData("Nectar notes refer to cedar staging readiness endpoint /cedar.", false)]
    [InlineData("Set readiness endpoint for cedar staging to /cedar.", false)]
    [InlineData("Nectar staging guide. The cedar database is in eu-west-2. The readiness endpoint is /cedar.", false)]
    [InlineData("Nectar staging guide. The readiness endpoint is /stage.", true)]
    [InlineData("Nectar staging and production readiness endpoints are /unknown.", false)]
    [InlineData("What is Nectar staging readiness endpoint?", false)]
    [InlineData(Answer, true)]
    public void ScopeMustBelongToTheStatementContainingTheEvidence(string text, bool matches) =>
        Assert.Equal(matches, RecallQueryEvidence.Parse(Query)!.Matches(text, Ns));

    [Theory]
    [InlineData("What is Nectar database password?", "Nectar database is hosted in eu-west-2.", false)]
    [InlineData("What is Nectar database password?", "Nectar database password is stored in the secret manager.", true)]
    [InlineData("What is Nectar gateway timeout?", "Nectar gateway uses HTTP for payments.", false)]
    [InlineData("What is Nectar monthly cloud budget?", "Nectar monthly cloud report is ready.", false)]
    [InlineData("What is Nectar token lifetime?", "Nectar tokens expire after two hours.", true)]
    [InlineData("What is Nectar recovery time objective?", "Nectar recovery drills run monthly. The recovery time objective is 20 minutes.", true)]
    [InlineData("Who owns Nectar security reviews?", "Priya owns Nectar production deployments.", false)]
    [InlineData("Who owns Nectar security reviews?", "Priya owns Nectar security reviews.", true)]
    [InlineData("What is Nectar prod readiness endpoint?", "Nectar production readiness endpoint is /ready.", true)]
    [InlineData("What is Nectar test readiness endpoint?", "Nectar testing readiness endpoint is /ready.", true)]
    [InlineData("How do I test Nectar backups?", "Nectar backups can be validated by restoring a snapshot.", true)]
    public void RequestedAttributesAndEnvironmentAliasesAreDistinguishedFromSharedTopicWords(string query, string text, bool matches) =>
        Assert.Equal(matches, RecallQueryEvidence.Parse(query)!.Matches(text, Ns));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SummariesCannotOccupyTheWholeFactualEvidenceBudget(int k)
    {
        using var store = new RecallTestStore();
        for (int i = 0; i < 3; i++) store.Add("summary-" + i, Ns, Answer, .99f, summary: true);
        var empty = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: k));
        Assert.Empty(empty.Results);
        store.Add("answer", Ns, Answer, .6f);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: k));
        Assert.Contains(result.Results, r => r.Id == "answer");
        Assert.InRange(result.Results.Count, 1, k);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void ArchiveRecoveryAndGraphExpansionCannotReintroduceScopeMismatches(string route)
    {
        using var store = new RecallTestStore();
        store.Add("answer", Ns, Answer, .85f, state: "archived");
        var wrong = store.Add("wrong", Ns, "Nectar production readiness endpoint is /prod.", .99f, state: "archived");
        var neighbor = store.Add("neighbor", "other", "Cedar staging readiness endpoint is /cedar.", .9f, state: "archived");
        store.Graph.AddEdge(new GraphEdge("answer", "neighbor", "related_to"));
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Route(store, route), k: 1));
        Assert.Equal("answer", Assert.Single(result.Results).Id);
        Assert.Equal("stm", result.Results[0].LifecycleState);
        Assert.All(new[] { wrong, neighbor }, e => { Assert.Equal("archived", e.LifecycleState); Assert.Equal(1, e.AccessCount); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrivateOrForeignTenantAnswersCannotFillMissingPublicEvidence(bool direct)
    {
        using var store = new RecallTestStore();
        store.Registry.EnsureOwnership(Ns, "alice", "team-a");
        store.Registry.EnsureOwnership("private", "alice", "team-a");
        store.Registry.Share(Ns, "alice", "bob", "read", "team-a");
        var wrong = store.Add("same-id", Ns, "Nectar production readiness endpoint is /prod.", tenant: "team-a");
        var secret = store.Add("same-id", "private", Answer, tenant: "team-a");
        var foreign = store.Add("same-id", Ns, Answer, tenant: "team-b");
        var result = Assert.IsType<RecallResult>(store.Tools("bob", "team-a").Recall(Query, direct ? Ns : null));
        Assert.Empty(result.Results);
        Assert.Equal("insufficient_evidence", result.QueryEvidence!.Status);
        Assert.All(new[] { wrong, secret, foreign }, e => Assert.Equal(1, e.AccessCount));
        var denied = Assert.IsType<RecallResult>(store.Tools("bob", "team-a").Recall(Query, "private"));
        Assert.Empty(denied.Results);
        Assert.Equal(result.QueryEvidence, denied.QueryEvidence);
    }

    [Theory]
    [InlineData("Compare Nectar staging and production readiness endpoints", true, false)]
    [InlineData(Query, false, false)]
    [InlineData(Query, true, true)]
    [InlineData("¿Cuál es el endpoint de Nectar?", true, false)]
    public void ComparisonsAndExplicitOptOutsRetainTheirRecallBehavior(string query, bool rerank, bool summaryFirst)
    {
        using var store = new RecallTestStore();
        store.Add("context", Ns, "Nectar production readiness endpoint is /prod.");
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(query, Ns, rerank: rerank, summaryFirst: summaryFirst));
        Assert.Single(result.Results);
        Assert.Null(result.QueryEvidence);
    }

    [Fact]
    public void RejectedCandidateWindowsRecoverEligibleEvidenceWithoutIncreasingPublicBudget()
    {
        using var store = new RecallTestStore();
        for (int i = 0; i < 40; i++) store.Add("wrong-" + i, Ns, "Nectar production readiness endpoint is /prod.", .99f);
        var answer = store.Add("answer", Ns, Answer, .4f);
        var small = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: 1, hybrid: false, expandGraph: false));
        Assert.Equal("answer", Assert.Single(small.Results).Id);
        Assert.Null(small.QueryEvidence);
        Assert.Equal(2, answer.AccessCount);
        var wide = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: 50, hybrid: false, expandGraph: false));
        Assert.Equal("answer", Assert.Single(wide.Results).Id);
        Assert.Null(wide.QueryEvidence);
    }
}
