using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class CompoundQuestionRecallTests
{
    private const string Query = "Who owns Citrine production deployment, and how often are backups taken?";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticRecallCombinesFacetsWhileDirectScopeStaysLocal(bool experts)
    {
        using var store = new RecallTestStore();
        store.Add("owner", "expert_citrine_ops", "Citrine production deployment owner is the Harbor team.", .9f);
        store.Add("backup", "expert_citrine_data", "Citrine production backups run every 6 hours.", .8f);
        if (experts) store.Dispatcher.CreateExpert("citrine_ops", Query);
        foreach (bool restart in new[] { false, true })
        {
            if (restart) store.Restart();
            var direct = Assert.IsType<RecallResult>(store.Tools().Recall(Query, "expert_citrine_ops", expandGraph: false));
            Assert.Equal("owner", Assert.Single(direct.Results).Id);
            var automatic = Assert.IsType<RecallResult>(store.Tools().Recall(Query, expandGraph: false));
            Assert.Equal(new[] { "backup", "owner" }, automatic.Results.Select(r => r.Id).Order().ToArray());
        }
    }

    [Fact]
    public void DuplicateOwnerHitsCannotHideOrCrowdOutBackupFacet()
    {
        using var store = new RecallTestStore();
        for (int i = 0; i < 40; i++)
            store.Add("owner-" + i, "citrine", "Citrine production deployment owner is the Harbor team.");
        store.Add("backup", "citrine", "Citrine production backups run every 6 hours.", .6f);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, "citrine", k: 2, expandGraph: false));
        Assert.Equal(2, result.Results.Count);
        Assert.Contains(result.Results, r => r.Id == "backup");
        Assert.Contains(result.Results, r => r.Id.StartsWith("owner-"));
        Assert.Equal(1, store.Index.Get("owner-9", "citrine", "")!.AccessCount);
    }

    [Theory]
    [InlineData(Query)]
    [InlineData("Who owns Citrine production deployment and when do backups run?")]
    [InlineData("Who owns Citrine production deployment, and how often are Citrine production backups taken?")]
    [InlineData("How often are Citrine production backups taken, and who owns deployment?")]
    [InlineData("Who owns citrine production deployment, and how often are backups taken?")]
    [InlineData("Please tell me who owns Citrine production deployment, and how often are backups taken?")]
    public void SharedScopeAppliesToEachFactualClause(string query)
    {
        var evidence = Assert.IsType<RecallQueryEvidence>(RecallQueryEvidence.ParseCompound(query));
        Assert.True(evidence.Matches("Citrine production deployment owner is Harbor.", "citrine"));
        Assert.True(evidence.Matches("Citrine production backups run every 6 hours.", "citrine"));
        Assert.False(evidence.Matches("Mariner production backups run every 6 hours.", "citrine"));
        Assert.False(evidence.Matches("Citrine staging backups run every 6 hours.", "citrine"));
        Assert.False(evidence.Matches("Backups run every 6 hours.", "citrine"));
    }

    [Theory]
    [InlineData("Citrine production backups run every 6 hours.", true)]
    [InlineData("Citrine production backups run every six hours.", true)]
    [InlineData("Citrine production backups run nightly.", true)]
    [InlineData("Citrine production backups run at 04:00.", true)]
    [InlineData("Citrine production backups use encrypted snapshots.", false)]
    [InlineData("Citrine production backups frequency is unknown.", false)]
    [InlineData("Citrine production backups frequency is still to be recorded.", false)]
    [InlineData("How often are Citrine production backups taken?", false)]
    [InlineData("Citrine production backups note asks: how often are they taken?", false)]
    [InlineData("Citrine production backups schedule is documented in the runbook.", false)]
    [InlineData("Citrine production backups proposal: run every 6 hours.", false)]
    [InlineData("Citrine production backups task asks operators to record a cadence every 6 hours.", false)]
    [InlineData("Citrine production restore rehearsal runs every 6 hours.", false)]
    [InlineData("Citrine production deployment owner is Harbor. Mariner production backups run hourly.", false)]
    [InlineData("Citrine production deployment owner is Harbor; Citrine staging backups run hourly.", false)]
    public void CadenceRequiresScopedAnswerEvidence(string text, bool expected) =>
        Assert.Equal(expected, RecallQueryEvidence.ParseCompound(Query)!.Matches(text, "citrine"));

    [Theory]
    [InlineData("Who owns Citrine production deployment?")]
    [InlineData("Citrine production deployment and recovery")]
    [InlineData("Which steps restore Citrine production, and in what order?")]
    [InlineData("Who owns Citrine production deployment, and why do backups matter?")]
    [InlineData("Who owns Citrine production deployment, and which document describes backups?")]
    [InlineData("Who owns Citrine production deployment, and what backup schedule was proposed?")]
    [InlineData("Who owns Citrine production deployment, and how often are Mariner production backups taken?")]
    [InlineData("Who owns Citrine production deployment, and how often are Citrine staging backups taken?")]
    [InlineData("Who owns Citrine production deployment, and how often are backups taken, and where is the database?")]
    [InlineData("Who owns Citrine release 27, and how often are backups taken?")]
    [InlineData("Who owns \"Citrine production deployment\", and how often are backups taken?")]
    [InlineData("Where is the Atlas production deploy-token stored and when is it rotated?")]
    [InlineData("When do Boreal database backups run and how long are they retained?")]
    [InlineData("When do Atlas database backups run and how long are they retained?")]
    public void UnsupportedShapesKeepExistingInterpretation(string query) =>
        Assert.Null(RecallQueryEvidence.ParseCompound(query));

    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void ScopeCanariesCannotSupplyMissingFacetEvenThroughGraph(string route)
    {
        using var store = new RecallTestStore();
        const string ns = "expert_citrine_ops";
        store.Add("owner", ns, "Citrine production deployment owner is Harbor.", .9f);
        store.Add("wrong-project", ns, "Mariner production backups run hourly CANARY.");
        store.Add("wrong-environment", ns, "Citrine staging backups run hourly CANARY.");
        store.Add("mixed", ns, "Citrine production deployment owner is Harbor. Mariner production backups run hourly CANARY.");
        store.Add("note", ns, "Citrine production backups schedule is still to be recorded CANARY.");
        store.Add("backup", ns, "Citrine production backups run every 6 hours.", .6f, state: "archived");
        foreach (string id in new[] { "wrong-project", "wrong-environment", "mixed", "note" })
            store.Graph.AddEdge(new GraphEdge("owner", id, "related_to"));
        if (route == "expert") store.Dispatcher.CreateExpert("citrine_ops", Query);
        foreach (bool restart in new[] { false, true })
        {
            if (restart) store.Restart();
            var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, route == "direct" ? ns : null, k: 2));
            Assert.Equal(new[] { "backup", "owner" }, result.Results.Select(r => r.Id).Order().ToArray());
            Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(result));
            Assert.Equal("archived", store.Index.Get("backup", ns, "")!.LifecycleState);
            foreach (string id in new[] { "wrong-project", "wrong-environment", "mixed", "note" })
                Assert.Equal(1, store.Index.Get(id, ns, "")!.AccessCount);
        }
    }

    [Fact]
    public void TenantPermissionsCosineFloorAndArchiveWriteAccessRemainEnforced()
    {
        using var store = new RecallTestStore();
        const string ns = "citrine", tenant = "team";
        store.Registry.EnsureOwnership(ns, "owner", tenant);
        store.Registry.Share(ns, "owner", "reader", "read", tenant);
        store.Registry.EnsureOwnership("private", "owner", tenant);
        store.Add("owner", ns, "Citrine production deployment owner is Harbor.", .9f, tenant: tenant);
        store.Add("backup", ns, "Citrine production backups run every 6 hours.", .75f, state: "archived", tenant: tenant);
        store.Add("private", "private", "Citrine production backups run hourly CANARY.", tenant: tenant);
        store.Add("foreign", ns, "Citrine production backups run hourly CANARY.", tenant: "other");
        store.Add("legacy", ns, "Citrine production backups run hourly CANARY.");
        store.Graph.AddEdge(new GraphEdge("owner", "private", "related_to", tenantId: tenant));
        var tools = store.Tools("reader", tenant);
        foreach (string? target in new string?[] { ns, null })
        {
            var high = Assert.IsType<RecallResult>(tools.Recall(Query, target, minScore: .8f));
            Assert.Equal("owner", Assert.Single(high.Results).Id);
            var result = Assert.IsType<RecallResult>(tools.Recall(Query, target, k: 2));
            Assert.Equal(new[] { "backup", "owner" }, result.Results.Select(r => r.Id).Order().ToArray());
            Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(result));
        }
        Assert.Empty(Assert.IsType<RecallResult>(tools.Recall(Query, "private")).Results);
        Assert.Empty(Assert.IsType<RecallResult>(tools.Recall(Query, "empty")).Results);
        Assert.Equal("archived", store.Index.Get("backup", ns, tenant)!.LifecycleState);
        Assert.Equal(1, store.Index.Get("private", "private", tenant)!.AccessCount);
        Assert.Equal(1, store.Index.Get("foreign", ns, "other")!.AccessCount);
        Assert.Equal(1, store.Index.Get("legacy", ns, "")!.AccessCount);
    }

    [Theory]
    [InlineData("vector")]
    [InlineData("no-rerank")]
    [InlineData("summary-first")]
    [InlineData("specific")]
    [InlineData("none")]
    public void OptOutsKeepExistingExpertRoute(string mode)
    {
        using var store = new RecallTestStore();
        store.Add("owner", "expert_citrine_ops", "Citrine production deployment owner is Harbor.");
        store.Add("backup", "data", "Citrine production backups run every 6 hours.");
        store.Dispatcher.CreateExpert("citrine_ops", Query);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, k: 2, expandGraph: false,
            hybrid: mode != "vector", rerank: mode != "no-rerank", summaryFirst: mode == "summary-first",
            spectralMode: mode is "specific" or "none" ? mode : "auto"));
        Assert.Equal("expert_routed", result.Strategy);
        Assert.Equal("owner", Assert.Single(result.Results).Id);
        Assert.Equal(1, store.Index.Get("backup", "data", "")!.AccessCount);
    }

    [Fact]
    public void MissingFacetRemainsMissingAndSummariesCannotInventIt()
    {
        using var store = new RecallTestStore();
        store.Add("owner", "ops", "Citrine production deployment owner is Harbor.");
        store.Add("summary", "data", "Citrine production backups run every 6 hours.", summary: true);
        store.Add("note", "data", "Citrine production backups schedule is still to be recorded.");
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, expandGraph: false));
        Assert.Equal("owner", Assert.Single(result.Results).Id);
        Assert.Equal(1, store.Index.Get("summary", "data", "")!.AccessCount);
        Assert.Equal(1, store.Index.Get("note", "data", "")!.AccessCount);
        Assert.Single(Assert.IsType<RecallResult>(store.Tools().Recall(Query, k: 1)).Results);
    }

    [Fact]
    public void AnswerToOneFacetCannotTurnDocumentationTaskIntoAnotherAnswer()
    {
        var evidence = RecallQueryEvidence.ParseCompound(Query)!;
        const string text = "Citrine production deployment owner is Harbor, and the backups task asks operators to record a cadence every 6 hours.";
        Assert.True(evidence.Matches(text, "citrine"));
        Assert.True(evidence.Facets[0].Matches(text, "citrine"));
        Assert.False(evidence.Facets[1].Matches(text, "citrine"));
    }

    [Fact]
    public void CompleteExpertAnswerKeepsItsRouteAndDoesNotAccessOtherNamespaces()
    {
        using var store = new RecallTestStore();
        const string ns = "expert_citrine_ops";
        store.Add("owner", ns, "Citrine production deployment owner is Harbor.");
        store.Add("backup", ns, "Citrine production backups run every 6 hours.");
        store.Add("other", "other", "Citrine production backups run hourly.");
        store.Dispatcher.CreateExpert("citrine_ops", Query);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, k: 2, expandGraph: false));
        Assert.Equal("expert_routed", result.Strategy);
        Assert.Equal(new[] { "backup", "owner" }, result.Results.Select(r => r.Id).Order().ToArray());
        Assert.Equal(1, store.Index.Get("other", "other", "")!.AccessCount);
    }
}
