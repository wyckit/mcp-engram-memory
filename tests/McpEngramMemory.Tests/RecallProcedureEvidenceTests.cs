using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public sealed class RecallProcedureEvidenceTests
{
    private const string Ns = "expert_nectar";
    private const string Query = "Which steps restore Nectar production, and in what order?";
    private const string Answer = "Nectar production restore procedure: pause consumers, restore the snapshot, then resume consumers.";

    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void FrozenRestoreOrderFindsTheExistingProcedureBeforeAndAfterRestart(string route)
    {
        var corpus = IndependentRecallValidation.Load();
        var q = corpus.Queries.Single(q => q.Id == "restore-order");
        using var store = new RecallTestStore();
        foreach (var m in corpus.Entries) store.Add(m.Id, m.Ns, m.Text);
        if (route == "expert") store.Dispatcher.CreateExpert("citrine_data", "Citrine data operations");
        foreach (bool restart in new[] { false, true })
        {
            if (restart) store.Restart();
            var result = Assert.IsType<RecallResult>(store.Tools().Recall(q.Query, route == "direct" ? q.Ns : null));
            Assert.Equal("c-sequence", Assert.Single(result.Results).Id);
            Assert.Null(result.QueryEvidence);
        }
    }

    [Fact]
    public void FrozenProcedurePassesTheEvidenceMatcher()
    {
        var corpus = IndependentRecallValidation.Load();
        var q = corpus.Queries.Single(q => q.Id == "restore-order");
        var answer = corpus.Entries.Single(m => m.Id == "c-sequence");
        Assert.True(RecallQueryEvidence.Parse(q.Query)!.Matches(answer.Text, answer.Ns));
    }

    [Theory]
    [InlineData("Which steps restore Nectar production, and in what order?")]
    [InlineData("which steps restore nectar production, and in what order?")]
    [InlineData("Which Steps Restore Nectar Production, And In What Order?")]
    [InlineData("Please tell me which steps restore Nectar production, and in what order?")]
    [InlineData("What is the restore procedure for Nectar production?")]
    [InlineData("What is Nectar production restore sequence?")]
    [InlineData("Which steps restore Nectar production?")]
    public void ProcedureParaphrasesKeepTheOperationAndScope(string query)
    {
        var evidence = Assert.IsType<RecallQueryEvidence>(RecallQueryEvidence.Parse(query));
        Assert.True(evidence.Matches(Answer, Ns));
        Assert.False(evidence.Matches(Answer.Replace("Nectar", "Cedar"), Ns));
        Assert.False(evidence.Matches(Answer.Replace("production", "staging"), Ns));
        Assert.False(evidence.Matches(Answer.Replace("production", ""), Ns));
    }

    [Theory]
    [InlineData("Nectar production restore procedure: first pause consumers, next restore the snapshot, finally resume consumers.", true)]
    [InlineData("Nectar production restore: 1) pause consumers, 2) restore the snapshot, 3) resume consumers.", true)]
    [InlineData("Nectar production restore has one step: run restore-and-verify.", true)]
    [InlineData("Nectar production restore: pause consumers, restore the snapshot, then resume consumers. The operator name is unknown.", true)]
    [InlineData("Nectar production restore rehearsal took twelve minutes.", false)]
    [InlineData("Nectar production restore procedure is documented in the handbook.", false)]
    [InlineData("Nectar production restore procedure is still to be recorded.", false)]
    [InlineData("Nectar production restore procedure is under review.", false)]
    [InlineData("Nectar production restore planning note asks the team to document the procedure, then label the diagrams.", false)]
    [InlineData("Nectar production proposes a restore procedure: pause consumers, restore the snapshot, then resume consumers.", false)]
    [InlineData("Nectar production review asks: which restore steps run first, then next?", false)]
    [InlineData("Which steps restore Nectar production, and in what order?", false)]
    [InlineData("Nectar production notes. Cedar production restore: pause consumers, restore the snapshot, then resume consumers.", false)]
    [InlineData("Nectar production notes. The staging restore procedure: pause consumers, restore the snapshot, then resume consumers.", false)]
    [InlineData("Nectar production restore procedure: pause consumers, restore Cedar production snapshot, then resume consumers.", false)]
    [InlineData("Nectar production restore procedure: pause consumers, restore staging snapshot, then resume consumers.", false)]
    public void OrderedEvidenceDoesNotAcceptAdjacentFactsPlansOrContraryScopes(string text, bool expected) =>
        Assert.Equal(expected, RecallQueryEvidence.Parse(Query)!.Matches(text, Ns));

    [Theory]
    [InlineData("How long does Nectar production restore procedure take?", "Nectar production restore procedure takes twelve minutes.")]
    [InlineData("When is Nectar production restore procedure reviewed?", "Nectar production restore procedure is reviewed weekly.")]
    [InlineData("Which document contains Nectar production restore procedure?", "Nectar production restore procedure is documented in the handbook.")]
    [InlineData("Why does Nectar production restore procedure pause consumers?", "Nectar production restore procedure pauses consumers to prevent duplicate deliveries.")]
    [InlineData("Which restore procedure was proposed for Nectar production?", "Nectar production proposes a restore procedure: pause consumers, restore the snapshot, then resume consumers.")]
    [InlineData("What is Nectar production restore procedure?", "Nectar production restore procedure: run restore-latest.")]
    public void OtherQuestionsAboutAProcedureKeepTheirMeaning(string query, string text) =>
        Assert.True(RecallQueryEvidence.Parse(query)!.Matches(text, Ns));

    [Theory]
    [InlineData("Restore")]
    [InlineData("Order")]
    public void ProcedureVocabularyDoesNotEraseProjectNamesInOtherQuestions(string project)
    {
        var evidence = RecallQueryEvidence.Parse($"What is {project} production gateway timeout?")!;
        Assert.True(evidence.Matches($"{project} production gateway timeout is 45 seconds.", Ns));
        Assert.False(evidence.Matches("Cedar production gateway timeout is 45 seconds.", Ns));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ExplicitOptOutsKeepUnorderedContext(bool rerank, bool summaryFirst)
    {
        using var store = new RecallTestStore();
        store.Add("note", Ns, "Nectar production restore procedure is under review.");
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, rerank: rerank, summaryFirst: summaryFirst));
        Assert.Equal("note", Assert.Single(result.Results).Id);
    }

    [Fact]
    public void ExistingInputBoundsRemainInForce()
    {
        Assert.Null(RecallQueryEvidence.Parse(Query + new string(' ', 1024)));
        Assert.False(RecallQueryEvidence.Parse(Query)!.Matches(Answer + new string(' ', 16384), Ns));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void MissingProcedureAbstainsAndNewProcedureCanBeRecoveredFromArchive(string route)
    {
        using var store = new RecallTestStore();
        store.Add("timing", Ns, "Nectar production restore rehearsal took twelve minutes.", .99f, state: "archived");
        store.Add("pending", Ns, "Nectar production restore procedure is still to be recorded.", .98f, state: "archived");
        if (route == "expert") store.Dispatcher.CreateExpert("nectar", "Nectar operations");
        string? ns = route == "direct" ? Ns : null;
        var absent = Assert.IsType<RecallResult>(store.Tools().Recall(Query, ns));
        Assert.Empty(absent.Results);
        Assert.Equal("insufficient_evidence", absent.QueryEvidence?.Status);
        store.Add("answer", Ns, Answer, .6f, state: "archived");
        store.Restart();
        Assert.Equal("answer", Assert.Single(Assert.IsType<RecallResult>(store.Tools().Recall(Query, ns)).Results).Id);
        // Recall is allowed below the existing 0.7 resurrection threshold.
        Assert.Equal("archived", store.Index.Get("answer", Ns, "")!.LifecycleState);
        Assert.Equal(2, store.Index.Get("answer", Ns, "")!.AccessCount);
        foreach (string id in new[] { "timing", "pending" })
        {
            Assert.Equal(1, store.Index.Get(id, Ns, "")!.AccessCount);
            Assert.Equal("archived", store.Index.Get(id, Ns, "")!.LifecycleState);
        }
    }

    [Fact]
    public void ProceduresPreserveNamespaceTenantGraphScoreAndReadOnlyArchiveProtections()
    {
        using var store = new RecallTestStore();
        foreach (string ns in new[] { Ns, "private", "empty" }) store.Registry.EnsureOwnership(ns, "owner", "team");
        store.Registry.Share(Ns, "owner", "reader", "read", "team");
        store.Registry.Share("empty", "owner", "reader", "read", "team");
        store.Add("same", "private", "PRIVATE_CANARY " + Answer, tenant: "team", state: "archived");
        store.Add("same", Ns, "FOREIGN_CANARY " + Answer, tenant: "foreign", state: "archived");
        store.Add("same", Ns, "LEGACY_CANARY " + Answer, state: "archived");
        store.Add("project", Ns, Answer.Replace("Nectar", "Cedar"), tenant: "team", state: "archived");
        store.Add("environment", Ns, Answer.Replace("production", "staging"), tenant: "team", state: "archived");
        Assert.Empty(Assert.IsType<RecallResult>(store.Tools("reader", "team").Recall(Query)).Results);
        store.Add("answer", Ns, Answer, .6f, tenant: "team", state: "archived");
        store.Graph.AddEdge(new GraphEdge("answer", "same", "elaborates", tenantId: "team"));
        Assert.Single(store.Graph.GetAllEdges("team"));
        store.Restart();
        var tools = store.Tools("reader", "team");
        foreach (string? ns in new string?[] { Ns, null })
        {
            Assert.Empty(Assert.IsType<RecallResult>(tools.Recall(Query, ns, minScore: .8f)).Results);
            var result = Assert.IsType<RecallResult>(tools.Recall(Query, ns));
            Assert.Equal("answer", Assert.Single(result.Results).Id);
            Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(result));
        }
        Assert.Empty(Assert.IsType<RecallResult>(tools.Recall(Query, "private")).Results);
        Assert.Empty(Assert.IsType<RecallResult>(tools.Recall(Query, "empty")).Results);
        Assert.Equal("archived", store.Index.Get("answer", Ns, "team")!.LifecycleState);
        foreach (var key in new[] { ("same", "private", "team"), ("same", Ns, "foreign"), ("same", Ns, ""),
                     ("project", Ns, "team"), ("environment", Ns, "team") })
        {
            var entry = store.Index.Get(key.Item1, key.Item2, key.Item3)!;
            Assert.Equal(1, entry.AccessCount);
            Assert.Equal("archived", entry.LifecycleState);
        }
    }
}
