using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class RecallNoiseRecoveryTests
{
    private const string Ns = "expert_nectar";
    private const string Query = "What is Nectar production readiness endpoint?";
    private const string Answer = "Nectar production readiness endpoint is /ready/live.";

    [Theory]
    [InlineData("nectar production recovery overview", "cedar production recovery takes 3 minutes.", false)]
    [InlineData("what was nectar production gateway timeout?", "cedar production gateway timeout was 3 seconds.", false)]
    [InlineData("what was nectar production gateway timeout?", "[superseded] cedar production gateway timeout was 3 seconds.", false)]
    [InlineData("Previous nectar production gateway timeout", "nectar staging gateway timeout was 3 seconds.", false)]
    [InlineData("Nectar production recovery overview", "Nectar production guide. Cedar production recovery takes 3 minutes.", false)]
    [InlineData("Nectar production recovery overview", "Nectar production guide. The staging recovery takes 3 minutes.", false)]
    [InlineData("Nectar production recovery overview", "Nectar production guide. It recovers in 3 minutes.", true)]
    [InlineData("Nectar production recovery overview", "Nectar backups run daily.", true)]
    [InlineData("Nectar production recovery overview", "Restore the latest snapshot before replaying transactions.", true)]
    [InlineData("Please tell me what was Nectar staging readiness endpoint?", "Nectar production readiness endpoint is /prod.", false)]
    [InlineData("Compare Nectar staging and production readiness endpoints", "Nectar production readiness endpoint is /ready.", true)]
    [InlineData("Compare Nectar staging and production readiness endpoints", "Nectar staging readiness endpoint is /stage.", true)]
    [InlineData("Compare Nectar staging and production readiness endpoints", "Cedar production readiness endpoint is /ready.", false)]
    [InlineData("What was Nectar staging readiness endpoint?", "Nectar production readiness endpoint is /prod.", false)]
    [InlineData("Nectar staging readiness endpoint", "Nectar production readiness endpoint is /prod.", false)]
    public void ScopeIsIndependentOfCurrentnessAndFactualAnswerChecks(string query, string text, bool matches) =>
        Assert.Equal(matches, RecallQueryEvidence.ParseScope(query)!.Matches(text, Ns));

    [Fact]
    public void ScopeDoesNotSilentlyIgnoreContraryTextBeyondTheStatementLimit() =>
        Assert.False(RecallQueryEvidence.ParseScope("Nectar production recovery overview")!.Matches(
            string.Concat(Enumerable.Repeat("Nectar production guide. ", 32)) + "Cedar production recovery takes 3 minutes.", Ns));

    [Theory]
    [InlineData("direct", true, false)]
    [InlineData("expert", true, false)]
    [InlineData("broadcast", true, false)]
    [InlineData("direct", false, false)]
    [InlineData("expert", false, true)]
    [InlineData("broadcast", true, true)]
    public void NonAnswersBeyondBothCandidateWindowsCannotHideAnAnswer(string route, bool hybrid, bool restart)
    {
        using var store = new RecallTestStore();
        for (int i = 0; i < 300; i++) store.Add("noise-" + i, Ns,
            $"Nectar production readiness endpoint is documented in monitoring note {i}.", .99f);
        store.Add("answer", Ns, Answer, .6f);
        if (route == "expert") store.Dispatcher.CreateExpert("nectar", "Nectar production operations");
        if (restart) store.Restart();
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, route == "direct" ? Ns : null,
            hybrid: hybrid, expandGraph: false, spectralMode: "none"));
        Assert.Equal("answer", Assert.Single(result.Results).Id);
        Assert.Equal(route == "expert" ? "expert_routed" : route, result.Strategy);
        Assert.Equal(2, store.Index.Get("answer", Ns, "")!.AccessCount);
        Assert.All(store.Index.GetAllInNamespace(Ns).Where(e => e.Id.StartsWith("noise-")),
            e => Assert.Equal(1, e.AccessCount));
    }

    [Theory]
    [InlineData("Nectar production recovery overview", "direct")]
    [InlineData("Nectar production recovery overview", "expert")]
    [InlineData("Nectar production recovery overview", "broadcast")]
    [InlineData("Nectar production deployment and recovery", "direct")]
    [InlineData("Nectar production deployment and recovery", "expert")]
    [InlineData("Nectar production deployment and recovery", "broadcast")]
    [InlineData("What was Nectar production gateway timeout before August?", "direct")]
    [InlineData("What was Nectar production gateway timeout before August?", "expert")]
    [InlineData("What was Nectar production gateway timeout before August?", "broadcast")]
    public void BroadAndHistoricalScopeAppliesToLocalAndGraphCandidates(string query, string route)
    {
        using var store = new RecallTestStore();
        string state = RecallFreshness.PrefersCurrent(query) ? "ltm" : "archived";
        store.Add("answer", Ns, "[superseded] Nectar production gateway timeout was 90 seconds. Nectar production recovery took 12 minutes.", .85f, state: state);
        store.Add("wrong-project", Ns, "Cedar production gateway timeout was 20 seconds. Cedar production recovery took 3 minutes.", .99f);
        store.Add("wrong-env", Ns, "Nectar staging gateway timeout was 10 seconds. Nectar staging recovery took 1 minute.", .99f);
        store.Add("neighbor", "related", "Cedar staging gateway timeout was 5 seconds. Cedar staging recovery took 2 minutes.", .95f, state: "archived");
        store.Graph.AddEdge(new GraphEdge("answer", "neighbor", "related_to"));
        if (route == "expert") store.Dispatcher.CreateExpert("nectar", "Nectar production operations");
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(query, route == "direct" ? Ns : null));
        Assert.Equal("answer", Assert.Single(result.Results).Id);
        Assert.DoesNotContain("Cedar", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("staging", JsonSerializer.Serialize(result));
        Assert.Equal(state, store.Index.Get("answer", Ns, "")!.LifecycleState);
        Assert.Equal("archived", store.Index.Get("neighbor", "related", "")!.LifecycleState);
        Assert.Equal(1, store.Index.Get("wrong-project", Ns, "")!.AccessCount);
        Assert.Equal(1, store.Index.Get("wrong-env", Ns, "")!.AccessCount);
        Assert.Equal(1, store.Index.Get("neighbor", "related", "")!.AccessCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryPreservesTenantAclThresholdAndReadOnlyArchives(bool restart)
    {
        using var store = new RecallTestStore();
        foreach (string ns in new[] { Ns, "private", "empty" }) store.Registry.EnsureOwnership(ns, "owner", "team");
        store.Registry.Share(Ns, "owner", "reader", "read", "team");
        store.Registry.Share("empty", "owner", "reader", "read", "team");
        for (int i = 0; i < 300; i++) store.Add("noise-" + i, Ns,
            $"Nectar production readiness endpoint is documented in note {i}.", .99f, tenant: "team");
        store.Add("answer", Ns, Answer, .6f, state: "archived", tenant: "team");
        store.Add("answer", "private", "PRIVATE_CANARY " + Answer, tenant: "team");
        store.Add("answer", Ns, "FOREIGN_CANARY " + Answer, tenant: "other");
        store.Add("answer", Ns, "LEGACY_CANARY " + Answer);
        if (restart) store.Restart();
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
        Assert.Equal(1, store.Index.Get("answer", "private", "team")!.AccessCount);
        Assert.Equal(1, store.Index.Get("answer", Ns, "other")!.AccessCount);
        Assert.Equal(1, store.Index.Get("answer", Ns, "")!.AccessCount);
    }
}
