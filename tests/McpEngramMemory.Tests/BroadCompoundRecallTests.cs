using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class BroadCompoundRecallTests
{
    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void OverviewRecoversSourcesBehindTopicOnlyNoise(string route)
    {
        using var store = new RecallTestStore();
        const string ns = "expert_cedar", query = "Cedar production operational overview";
        for (int i = 0; i < 40; i++)
            store.Add("noise-" + i, ns, $"Cedar production index {i} lists deployment and readiness as topics for a future workshop.");
        store.Add("owner", ns, "Cedar production deployment owner is Talia Moss.", cosine: .8f);
        store.Add("endpoint", ns, "Cedar production readiness endpoint is /ready.", cosine: .75f);
        store.Add("region", ns, "Cedar production database region is us-east-1.", cosine: .7f);
        if (route == "expert") store.Dispatcher.CreateExpert("cedar", query);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(query, route == "direct" ? ns : null,
            k: 3, expandGraph: false));
        Assert.Equal(new[] { "endpoint", "owner", "region" }, result.Results.Select(r => r.Id).Order().ToArray());
        Assert.Equal(1, store.Index.Get("noise-0", ns, "")!.AccessCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompoundAutoRouteCombinesPopulatedExpertsButExplicitNamespaceStaysLocal(bool experts)
    {
        using var store = new RecallTestStore();
        const string query = "Cedar production deployment and recovery";
        store.Add("owner", "expert_cedar_ops", "Cedar production deployment owner is Talia Moss.", cosine: .9f);
        store.Add("backup", "expert_cedar_recovery", "Cedar production backups run nightly.", cosine: .85f);
        store.Add("restore", "expert_cedar_recovery", "Cedar production recovery rehearsal took 12 minutes.", cosine: .8f);
        if (experts)
        {
            store.Dispatcher.CreateExpert("cedar_ops", query);
            store.Dispatcher.CreateExpert("cedar_recovery", query);
        }
        var direct = Assert.IsType<RecallResult>(store.Tools().Recall(query, "expert_cedar_ops", expandGraph: false));
        Assert.Equal("owner", Assert.Single(direct.Results).Id);
        var automatic = Assert.IsType<RecallResult>(store.Tools().Recall(query, expandGraph: false));
        Assert.Equal(new[] { "backup", "owner", "restore" }, automatic.Results.Select(r => r.Id).Order().ToArray());
    }

    [Theory]
    [InlineData("Cedar production operational overview")]
    [InlineData("Cedar production deployment and recovery")]
    public void RecoveryKeepsOriginalScopeThroughRankingAndGraphExpansion(string query)
    {
        foreach (string route in new[] { "direct", "expert", "broadcast" })
        {
            using var store = new RecallTestStore();
            const string ns = "expert_cedar";
            store.Add("good", ns, "Cedar production deployment owner is Talia Moss.", .8f);
            store.Add("wrong-project", ns, "Maple production deployment owner is CANARY. Cedar production owner is documented elsewhere.");
            store.Add("wrong-environment", ns, "Cedar staging deployment owner is CANARY. Cedar production owner is unknown.");
            store.Add("stacked-labels", ns, "Historical previous Maple production deployment owner is CANARY.");
            store.Add("mixed", ns, "Cedar production deployment owner is Talia; Maple production backups run every hour CANARY.");
            store.Add("noise", ns, "Cedar production index lists deployment and backups as topics for a workshop.");
            foreach (string id in new[] { "wrong-project", "wrong-environment", "stacked-labels", "mixed" })
                store.Graph.AddEdge(new GraphEdge("good", id, "related_to"));
            if (route == "expert") store.Dispatcher.CreateExpert("cedar", query);
            foreach (bool restart in new[] { false, true })
            {
                if (restart) store.Restart();
                var result = Assert.IsType<RecallResult>(store.Tools().Recall(query, route == "direct" ? ns : null));
                Assert.Contains(result.Results, r => r.Id == "good");
                Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(result));
                foreach (string id in new[] { "wrong-project", "wrong-environment", "stacked-labels", "mixed" })
                    Assert.Equal(1, store.Index.Get(id, ns, "")!.AccessCount);
            }
        }
    }

    [Theory]
    [InlineData("Cedar production overview")]
    [InlineData("Cedar production deployment and recovery")]
    public void TenantAccessThresholdsAndReadOnlyArchivesRemainProtected(string query)
    {
        using var store = new RecallTestStore();
        const string ns = "cedar", tenant = "team";
        store.Registry.EnsureOwnership(ns, "owner", tenant);
        store.Registry.Share(ns, "owner", "reader", "read", tenant);
        store.Registry.EnsureOwnership("private", "owner", tenant);
        store.Add("good", ns, "Cedar production deployment owner is Talia Moss.", .75f, state: "archived", tenant: tenant);
        store.Add("noise", ns, "Cedar production index lists deployment and backups as topics for a workshop.", tenant: tenant);
        store.Add("secret", "private", "Cedar production backups run nightly CANARY.", tenant: tenant);
        store.Add("foreign", ns, "Cedar production backups run nightly CANARY.", tenant: "other");
        store.Graph.AddEdge(new GraphEdge("good", "secret", "related_to", tenantId: tenant));
        var tools = store.Tools("reader", tenant);
        foreach (string? target in new string?[] { ns, null })
        {
            var threshold = Assert.IsType<RecallResult>(tools.Recall(query, target, minScore: .8f));
            Assert.DoesNotContain(threshold.Results, r => r.Id == "good");
            var result = Assert.IsType<RecallResult>(tools.Recall(query, target));
            Assert.Contains(result.Results, r => r.Id == "good");
            Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(result));
        }
        Assert.Empty(Assert.IsType<RecallResult>(tools.Recall(query, "private")).Results);
        Assert.Equal("archived", store.Index.Get("good", ns, tenant)!.LifecycleState);
        Assert.Equal(1, store.Index.Get("secret", "private", tenant)!.AccessCount);
        Assert.Equal(1, store.Index.Get("foreign", ns, "other")!.AccessCount);
    }

    [Theory]
    [InlineData("vector")]
    [InlineData("no-rerank")]
    [InlineData("summary-first")]
    [InlineData("specific")]
    [InlineData("none")]
    public void OptOutsDoNotActivateCompoundBroadcastOrArchiveRecovery(string mode)
    {
        using var store = new RecallTestStore();
        const string query = "Cedar production deployment and recovery", ns = "expert_cedar";
        store.Add("live", ns, "Cedar production deployment owner is Talia Moss.");
        store.Add("archive", ns, "Cedar production backups run nightly.", .7f, state: "archived");
        store.Add("other", "other", "Cedar production recovery rehearsal took 12 minutes.");
        store.Dispatcher.CreateExpert("cedar", query);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(query, k: 1, hybrid: mode != "vector",
            rerank: mode != "no-rerank", summaryFirst: mode == "summary-first", expandGraph: false,
            spectralMode: mode is "specific" or "none" ? mode : "auto"));
        Assert.Equal("expert_routed", result.Strategy);
        Assert.Equal("live", Assert.Single(result.Results).Id);
        Assert.Equal(1, store.Index.Get("archive", ns, "")!.AccessCount);
        Assert.Equal(1, store.Index.Get("other", "other", "")!.AccessCount);
    }

    [Theory]
    [InlineData("Cedar port is unknown.", false)]
    [InlineData("Cedar endpoint is documented in the runbook.", false)]
    [InlineData("Cedar release index lists owners and commands as topics for a workshop.", false)]
    [InlineData("Cedar schedule note asks the operator to record a frequency.", false)]
    [InlineData("Cedar policy task links to the checklist; it does not provide a setting.", false)]
    [InlineData("What are Cedar backup settings?", false)]
    [InlineData("Cedar cache invalidation lesson", true)]
    [InlineData("Cedar readiness path is /live, but the budget is unknown.", true)]
    [InlineData("Cedar backup window is recorded in the runbook as 04:00 UTC.", true)]
    [InlineData("Cedar cache preserves metadata and adds the optional header.", true)]
    [InlineData("Cedar owner is Talia. A ticket links to the checklist.", true)]
    [InlineData("Cedar owner is Talia, but the index lists backups and settings as topics for a workshop.", true)]
    public void EvidencePreferenceKeepsFactsAndExplanationsAlongsideNonAnswerClauses(string text, bool expected) =>
        Assert.Equal(expected, BroadRecallIntent.HasSourceEvidence(text));

    [Theory]
    [InlineData("Cedar production deployment and recovery", true)]
    [InlineData("Cedar production operational overview", true)]
    [InlineData("How does Cedar deployment and recovery work?", false)]
    [InlineData("Cedar deployment and recovery 2026", false)]
    [InlineData("Cedar \"deployment and recovery\"", false)]
    public void CoverageIntentKeepsPrecisionQueriesSeparate(string query, bool expected) =>
        Assert.Equal(expected, BroadRecallIntent.Parse(query) is not null);

    [Theory]
    [InlineData("related_to", true, true)]
    [InlineData("elaborates", true, true)]
    [InlineData("related_to", false, false)]
    [InlineData("contradicts", true, false)]
    [InlineData("supersedes", true, false)]
    public void CompoundSelectionUsesOnlyOptedInComplementaryGraphSupport(string relation, bool expandGraph, bool supported)
    {
        using var store = new RecallTestStore();
        const string ns = "expert_cedar", query = "Cedar production deployment and recovery";
        store.Add("top", ns, query + " owner is Talia.", .99f);
        store.Add("next", ns, query + " codename is Finch.", .98f);
        store.Add("procedure", ns, query + " backups run nightly.", .85f);
        store.Add("outcome", ns, query + " rehearsal took twelve minutes.", .84f);
        store.Graph.AddEdge(new GraphEdge("procedure", "outcome", relation));
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(query, ns, k: 1, expandGraph: expandGraph));
        Assert.Equal(supported ? "procedure" : "top", Assert.Single(result.Results).Id);
    }

    [Fact]
    public void InvisibleOrBelowThresholdGraphNeighborsCannotSupplySupport()
    {
        using var store = new RecallTestStore();
        const string ns = "cedar", query = "Cedar production deployment and recovery", tenant = "team";
        store.Registry.EnsureOwnership(ns, "reader", tenant);
        store.Registry.EnsureOwnership("private", "owner", tenant);
        store.Add("top", ns, query + " owner is Talia.", .99f, tenant: tenant);
        store.Add("next", ns, query + " codename is Finch.", .98f, tenant: tenant);
        store.Add("procedure", ns, query + " backups run nightly.", .85f, tenant: tenant);
        store.Add("private", "private", query + " rehearsal took twelve minutes CANARY.", tenant: tenant);
        store.Add("low", ns, query + " rehearsal took eleven minutes.", .6f, tenant: tenant);
        foreach (string id in new[] { "private", "low" })
            store.Graph.AddEdge(new GraphEdge("procedure", id, "related_to", tenantId: tenant));
        var result = Assert.IsType<RecallResult>(store.Tools("reader", tenant).Recall(query, k: 1, minScore: .8f));
        Assert.Equal("top", Assert.Single(result.Results).Id);
        Assert.Equal(1, store.Index.Get("procedure", ns, tenant)!.AccessCount);
        Assert.Equal(1, store.Index.Get("private", "private", tenant)!.AccessCount);
        Assert.Equal(1, store.Index.Get("low", ns, tenant)!.AccessCount);
    }

    [Theory]
    [InlineData("Cedar production overview")]
    [InlineData("Cedar production deployment and recovery")]
    public void WrongScopeSourcesCannotConsumeRecoveredCandidateBudget(string query)
    {
        using var store = new RecallTestStore();
        const string ns = "cedar";
        for (int i = 0; i < 40; i++)
            store.Add("wrong-" + i, ns, "Maple production deployment owner is CANARY " + i);
        store.Add("note", ns, "Cedar production owner is documented in a checklist.");
        store.Add("answer", ns, "Cedar production deployment owner is Talia.", .5f);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(query, ns, k: 1, expandGraph: false));
        Assert.Equal("answer", Assert.Single(result.Results).Id);
    }

    [Fact]
    public void HiddenDuplicateIdCannotSupplyGraphSupport()
    {
        using var store = new RecallTestStore();
        const string ns = "cedar", query = "Cedar production deployment and recovery", tenant = "team";
        store.Registry.EnsureOwnership(ns, "reader", tenant);
        store.Registry.EnsureOwnership("private", "owner", tenant);
        store.Add("top", ns, query + " owner is Talia.", .99f, tenant: tenant);
        store.Add("procedure", ns, query + " backups run nightly.", .85f, tenant: tenant);
        store.Add("outcome", ns, query + " rehearsal took twelve minutes.", .84f, tenant: tenant);
        store.Graph.AddEdge(new GraphEdge("procedure", "outcome", "related_to", tenantId: tenant));
        store.Add("procedure", "private", "CANARY", tenant: tenant);
        var result = Assert.IsType<RecallResult>(store.Tools("reader", tenant).Recall(query, k: 1));
        Assert.Equal("top", Assert.Single(result.Results).Id);
        Assert.Equal(1, store.Index.Get("procedure", "private", tenant)!.AccessCount);
    }
}
