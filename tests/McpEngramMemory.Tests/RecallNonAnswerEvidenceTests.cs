using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class RecallNonAnswerEvidenceTests
{
    private const string Query = "What is Nectar staging readiness endpoint?";
    private const string Ns = "expert_nectar";
    private const string Answer = "Nectar staging readiness endpoint is /stage/ready.";

    [Theory]
    [InlineData("Nectar staging readiness endpoint is documented in the service handbook.")]
    [InlineData("Nectar staging readiness endpoint is recorded in the service handbook.")]
    [InlineData("Nectar staging readiness endpoint is listed in the service handbook.")]
    [InlineData("Nectar staging readiness endpoint is specified in the service handbook.")]
    [InlineData("Nectar staging readiness endpoint is unknown.")]
    [InlineData("Nectar staging readiness endpoint is not yet known.")]
    [InlineData("Nectar staging readiness endpoint has not been determined.")]
    [InlineData("Nectar staging readiness endpoint is pending confirmation.")]
    [InlineData("Nectar staging readiness endpoint will be determined later.")]
    [InlineData("Nectar staging readiness endpoint needs to be confirmed.")]
    [InlineData("Nectar staging readiness endpoint: TBD.")]
    [InlineData("Nectar staging readiness endpoint = <TBC>.")]
    [InlineData("Nectar staging readiness endpoint: see the service handbook.")]
    [InlineData("Nectar staging readiness endpoint is documented in handbook revision 45.")]
    [InlineData("Nectar staging readiness endpoint is documented in /runbooks/ready.")]
    [InlineData("Nectar staging readiness endpoint path and port are documented in the handbook.")]
    [InlineData("Nectar staging readiness endpoint path, port and protocol are unknown.")]
    [InlineData("Nectar staging readiness endpoint is documented in the handbook as TBD.")]
    public void ClearNonAnswersDoNotSatisfyAFactualQuestion(string text) =>
        Assert.False(RecallQueryEvidence.Parse(Query)!.Matches(text, Ns));

    [Theory]
    [InlineData("Nectar staging readiness endpoint is /stage/ready, documented in the handbook.")]
    [InlineData("Nectar staging readiness endpoint is documented as /stage/ready in the handbook.")]
    [InlineData("Nectar staging readiness endpoint is documented in the handbook as /stage/ready.")]
    [InlineData("Nectar staging readiness endpoint is unknown. The staging readiness endpoint is /stage/ready.")]
    [InlineData("Nectar staging readiness endpoint is documented in the handbook. Its staging readiness endpoint is /stage/ready.")]
    [InlineData("Nectar staging readiness endpoint is /stage/ready and its timeout is unknown.")]
    [InlineData("Nectar staging timeout is unknown, but its readiness endpoint is /stage/ready.")]
    [InlineData("Nectar staging readiness endpoint is /stage/ready; see the handbook for deployment details.")]
    [InlineData("Nectar staging readiness endpoint is disabled.")]
    [InlineData("Nectar staging readiness endpoint returns an unknown status when startup is incomplete.")]
    [InlineData("Nectar staging readiness endpoint is /pending/ready.")]
    public void FactsSurviveReferencesUnrelatedUnknownsAndContinuations(string text) =>
        Assert.True(RecallQueryEvidence.Parse(Query)!.Matches(text, Ns));

    [Theory]
    [InlineData("Where is Nectar staging readiness endpoint documented?", "Nectar staging readiness endpoint is documented in the handbook.")]
    [InlineData("Why is Nectar staging readiness endpoint unknown?", "Nectar staging readiness endpoint is unknown because the service has not been deployed.")]
    [InlineData("How does Nectar context fingerprint work?", "Nectar context fingerprint covers content and metadata and prevents stale cached answers.")]
    [InlineData("Who owns Nectar production deployments?", "Leon owns Nectar production deployments and approves rollback requests.")]
    public void DocumentationAndExplanationQuestionsRetainTheirEvidence(string query, string text) =>
        Assert.True(RecallQueryEvidence.Parse(query)!.Matches(text, Ns));

    [Theory]
    [InlineData("What is the documented Nectar staging readiness endpoint?")]
    [InlineData("Where is the documented Nectar staging readiness endpoint?")]
    [InlineData("Which readiness endpoint is listed for Nectar staging?")]
    public void AskingForADocumentedFactStillRequiresMoreThanADocumentPointer(string query) =>
        Assert.False(RecallQueryEvidence.Parse(query)!.Matches("Nectar staging readiness endpoint is documented in the handbook.", Ns));

    [Theory]
    [InlineData("Nectar staging readiness endpoint is unknown. The production readiness endpoint is /prod.")]
    [InlineData("Nectar staging readiness endpoint is documented in the handbook. Cedar staging readiness endpoint is /cedar.")]
    [InlineData("Nectar staging readiness endpoint is unknown, but Cedar staging readiness endpoint is /cedar.")]
    [InlineData("Nectar staging readiness endpoint is unknown, but cedar staging readiness endpoint is /cedar.")]
    [InlineData("Nectar staging readiness endpoint is unknown. Cedar database is in eu-west-1. The staging readiness endpoint is /cedar.")]
    [InlineData("Nectar staging readiness endpoint is unknown and its production readiness endpoint is /prod.")]
    public void NonAnswerContextCannotSupplyScopeForAContraryFact(string text) =>
        Assert.False(RecallQueryEvidence.Parse(Query)!.Matches(text, Ns));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonAnswersFallThroughWithoutAccessOrResurrectionAndRespectExplicitScope(bool restart)
    {
        using var store = new RecallTestStore();
        var first = store.Add("first", ExpertDispatcher.SystemNamespace, "Nectar operations", summary: true);
        first.Metadata["targetNamespace"] = "expert_first";
        store.Index.Upsert(first);
        var second = store.Add("second", ExpertDispatcher.SystemNamespace, "Nectar operations", .98f, summary: true);
        second.Metadata["targetNamespace"] = "expert_second";
        store.Index.Upsert(second);
        store.Add("note", "expert_first", "Nectar staging readiness endpoint is documented in the handbook.", state: "archived");
        store.Add("wrong-scope", "expert_first", "Nectar production readiness endpoint is /prod.", state: "archived");
        store.Add("answer", "expert_second", Answer, .9f, state: "archived");
        if (restart) store.Restart();

        var direct = Assert.IsType<RecallResult>(store.Tools().Recall(Query, "expert_first", k: 1));
        Assert.Empty(direct.Results);
        Assert.Equal("insufficient_evidence", direct.QueryEvidence?.Status);
        var automatic = Assert.IsType<RecallResult>(store.Tools().Recall(Query, k: 1));
        Assert.Equal("expert_second", automatic.Namespace);
        Assert.Equal("expert_routed", automatic.Strategy);
        Assert.Equal("answer", Assert.Single(automatic.Results).Id);
        Assert.Equal(1, store.Dispatcher.GetExpert("first")!.TaskCount);
        Assert.Equal(2, store.Dispatcher.GetExpert("second")!.TaskCount);
        foreach (string id in new[] { "note", "wrong-scope" })
        {
            var entry = store.Index.Get(id, "expert_first", "")!;
            Assert.Equal(1, entry.AccessCount);
            Assert.Equal("archived", entry.LifecycleState);
        }
        Assert.Equal("stm", store.Index.Get("answer", "expert_second", "")!.LifecycleState);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ExplicitOptOutsKeepContext(bool rerank, bool summaryFirst)
    {
        using var store = new RecallTestStore();
        store.Add("note", Ns, "Nectar staging readiness endpoint is documented in the handbook.");
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, rerank: rerank, summaryFirst: summaryFirst));
        Assert.Equal("note", Assert.Single(result.Results).Id);
        Assert.Null(result.QueryEvidence);
    }

    [Fact]
    public void CandidateRetryRecoversASourceBehindNotesAndFiltersGraphNonAnswers()
    {
        using var store = new RecallTestStore();
        for (int i = 0; i < 6; i++)
            store.Add("note-" + i, Ns, "Nectar staging readiness endpoint is documented in the handbook.", state: "archived");
        store.Add("answer", Ns, Answer, .8f, state: "archived");
        store.Graph.AddEdge(new GraphEdge("answer", "note-0", "related_to"));
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: 1));
        Assert.Equal("answer", Assert.Single(result.Results).Id);
        for (int i = 0; i < 6; i++)
        {
            var note = store.Index.Get("note-" + i, Ns, "")!;
            Assert.Equal(1, note.AccessCount);
            Assert.Equal("archived", note.LifecycleState);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("team-a")]
    public void MissingLocalEvidenceCannotBorrowPrivateForeignOrWrongEnvironmentAnswers(string tenant)
    {
        using var store = new RecallTestStore();
        store.Registry.EnsureOwnership(Ns, "alice", tenant);
        store.Registry.EnsureOwnership("private", "alice", tenant);
        store.Registry.Share(Ns, "alice", "bob", "read", tenant);
        store.Add("same", Ns, "Nectar staging readiness endpoint is unknown.", tenant: tenant);
        store.Add("same", "private", Answer, state: "archived", tenant: tenant);
        store.Add("same", Ns, Answer, state: "archived", tenant: "team-b");
        store.Add("production", Ns, "Nectar production readiness endpoint is /prod.", tenant: tenant);
        var missing = Assert.IsType<RecallResult>(store.Tools("bob", tenant).Recall(Query));
        Assert.Empty(missing.Results);
        Assert.Equal("insufficient_evidence", missing.QueryEvidence?.Status);
        Assert.DoesNotContain("private", missing.RoutingInfo);
        Assert.Equal(1, store.Index.Get("same", "private", tenant)!.AccessCount);
        Assert.Equal(1, store.Index.Get("same", Ns, "team-b")!.AccessCount);

        store.Add("answer", Ns, Answer, .8f, state: "archived", tenant: tenant);
        var highThreshold = Assert.IsType<RecallResult>(store.Tools("bob", tenant).Recall(Query, minScore: .95f));
        Assert.Empty(highThreshold.Results);
        var present = Assert.IsType<RecallResult>(store.Tools("bob", tenant).Recall(Query));
        Assert.Equal("answer", Assert.Single(present.Results).Id);
        Assert.Equal("archived", store.Index.Get("answer", Ns, tenant)!.LifecycleState); // Read grant does not resurrect.
    }
}
