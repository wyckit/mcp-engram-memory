using McpEngramMemory.Core.Models;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class BroadRecallCoverageTests
{
    private const string Ns = "expert_nectar";
    private const string Query = "Nectar caching";

    private static string? Route(RecallTestStore store, string route)
    {
        if (route == "expert") store.Dispatcher.CreateExpert("nectar", Query);
        return route == "direct" ? Ns : null;
    }

    private static void Seed(RecallTestStore store)
    {
        store.Add("overview", Ns, Query + " overview", cosine: 1, summary: true);
        for (int i = 0; i < 8; i++) store.Add("filler-" + i, Ns, "Nectar garden reference " + i, cosine: .9f);
        store.Add("fingerprint", Ns, "Nectar cache fingerprint covers metadata", cosine: .72f);
        store.Add("header", Ns, "Nectar cache header can be excluded", cosine: .68f);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void TopicEvidenceOutsideTheInitialFiveSurvivesWithoutGraphLinks(string route)
    {
        using var store = new RecallTestStore();
        Seed(store);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Route(store, route), expandGraph: false));
        Assert.Equal(5, result.Results.Count);
        Assert.Equal("overview", result.Results[0].Id);
        Assert.Contains(result.Results, r => r.Id == "fingerprint");
        Assert.Contains(result.Results, r => r.Id == "header");
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void ArchiveCanComplementAConfidentLiveOverviewAndOnlyReturnedRecordsChange(string route)
    {
        using var store = new RecallTestStore();
        Seed(store);
        var incident = store.Add("incident", Ns, "Nectar cache invalidation lesson", cosine: .8f, state: "archived");
        var omitted = store.Add("omitted", Ns, "Nectar gardening history", cosine: .4f, state: "archived");
        int incidentAccesses = incident.AccessCount, omittedAccesses = omitted.AccessCount;
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Route(store, route), k: 3, expandGraph: false));
        Assert.Equal(3, result.Results.Count);
        Assert.Contains(result.Results, r => r.Id == "incident");
        Assert.Equal("stm", incident.LifecycleState);
        Assert.Equal(incidentAccesses + 1, incident.AccessCount);
        Assert.Equal("archived", omitted.LifecycleState);
        Assert.Equal(omittedAccesses, omitted.AccessCount);
        Assert.Equal(1, store.Index.Get("filler-7", Ns, "")!.AccessCount);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void WiderCoverageDoesNotResurrectDisputedOrSupersededArchives(string route)
    {
        using var store = new RecallTestStore();
        Seed(store);
        store.Add("current", Ns, "Nectar cache timeout is 30 seconds.", cosine: .85f);
        var disputed = store.Add("disputed", Ns, "Nectar cache timeout is 45 seconds.", cosine: .82f, state: "archived");
        var obsolete = store.Add("obsolete", Ns, "[superseded] Nectar cache timeout is 10 seconds.", cosine: .99f, state: "archived");
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Route(store, route), expandGraph: false));
        Assert.Contains(result.Results, r => r.Id == "current");
        Assert.Contains(result.Results, r => r.Id == "disputed");
        Assert.DoesNotContain(result.Results, r => r.Id == "obsolete");
        var conflict = Assert.Single(result.Conflicts!);
        Assert.Equal("unresolved", conflict.Status);
        Assert.Equal(new[] { "current", "disputed" }, conflict.Evidence.Select(e => e.Id).Order().ToArray());
        Assert.Equal("archived", disputed.LifecycleState);
        Assert.Equal("archived", obsolete.LifecycleState);
        Assert.Equal(1, obsolete.AccessCount);
    }

    [Theory]
    [InlineData("vector")]
    [InlineData("no-rerank")]
    [InlineData("summary-first")]
    [InlineData("specific")]
    [InlineData("none")]
    [InlineData("long-query")]
    [InlineData("historical")]
    [InlineData("short-question")]
    public void PrecisionAndExplicitOptOutsKeepTheExistingArchivePolicy(string mode)
    {
        using var store = new RecallTestStore();
        string query = mode switch { "long-query" => "How does Nectar caching work?", "historical" => "Previous Nectar caching",
            "short-question" => "Who owns Nectar?", _ => Query };
        store.Add("overview", Ns, query, summary: true);
        for (int i = 0; i < 8; i++) store.Add("filler-" + i, Ns, query + " note " + i, cosine: .95f);
        store.Add("archive", Ns, query, cosine: .7f, state: "archived");
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(query, Ns, k: 3,
            hybrid: mode != "vector", rerank: mode != "no-rerank", summaryFirst: mode == "summary-first",
            expandGraph: false, spectralMode: mode is "specific" or "none" ? mode : "auto"));
        Assert.DoesNotContain(result.Results, r => r.Id == "archive");
        Assert.Equal("archived", store.Index.Get("archive", Ns, "")!.LifecycleState);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void ExplicitMinimumSimilarityStillFiltersTheWiderPool(int k)
    {
        using var store = new RecallTestStore();
        Seed(store);
        store.Add("archive", Ns, Query, cosine: .79f, state: "archived");
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, Ns, k: k, minScore: .8f, expandGraph: false));
        Assert.InRange(result.Results.Count, 1, k);
        Assert.DoesNotContain(result.Results, r => r.Id is "archive" or "fingerprint" or "header");
    }

    [Fact]
    public void BroadcastKeepsQualifiedIdsAndDoesNotConsultPrivateOrOtherTenantEvidence()
    {
        using var store = new RecallTestStore();
        foreach (string ns in new[] { "nectar", "notes", "private" })
            store.Registry.EnsureOwnership(ns, ns == "private" ? "bob" : "alice", "tenant");
        store.Add("shared-id", "nectar", "Nectar cache fingerprint", cosine: .8f, tenant: "tenant");
        store.Add("shared-id", "notes", "Nectar cache header", cosine: .78f, tenant: "tenant");
        for (int i = 0; i < 8; i++)
        {
            store.Add("filler-" + i, "nectar", "Nectar unrelated notes " + i, cosine: .6f, tenant: "tenant");
            store.Add("filler-" + i, "notes", "Nectar unrelated notes " + i, cosine: .6f, tenant: "tenant");
        }
        var secret = store.Add("secret", "private", Query, tenant: "tenant");
        var foreign = store.Add("foreign", "nectar", Query, tenant: "other");
        var result = Assert.IsType<RecallResult>(store.Tools("alice", "tenant").Recall(Query, expandGraph: false));
        Assert.Equal(2, result.Results.Count(r => r.Id == "shared-id"));
        Assert.DoesNotContain(result.Results, r => r.Id is "secret" or "foreign");
        Assert.Equal(1, secret.AccessCount);
        Assert.Equal(1, foreign.AccessCount);
    }

    [Theory]
    [InlineData("cache", "caching")]
    [InlineData("retry", "retries")]
    [InlineData("retrieval", "retrieved")]
    public void CommonInflectionsMatchWithinBroadCoverage(string first, string second) =>
        Assert.Equal(BroadRecallRanking.Tokens(first), BroadRecallRanking.Tokens(second));

    [Fact]
    public void SummaryWordsAndRepeatedCopiesDoNotManufactureTopicEvidence()
    {
        CognitiveSearchResult Result(string id, string text, bool summary = false) =>
            new(id, text, .8f, "ltm", 0, null, null, summary, null, 1);
        var candidates = new[] { Result("overview", Query, true), Result("a", "Nectar notes"), Result("b", "Nectar background") };
        Assert.False(BroadRecallRanking.HasTopicEvidence(Query, candidates));
        var focused = candidates.Append(Result("topic", "Nectar cache header")).ToArray();
        Assert.True(BroadRecallRanking.HasTopicEvidence(Query, focused));
        Assert.True(BroadRecallRanking.HasTopicEvidence(Query, focused.Concat(Enumerable.Range(0, 20)
            .Select(i => Result("copy" + i, "Nectar cache header"))).ToArray()));
        Assert.False(BroadRecallRanking.HasTopicEvidence("???", focused));
        var environmentOnly = new[] { Result("a", "Nectar production endpoint"), Result("b", "Nectar release approval"),
            Result("c", "Nectar backups"), Result("d", "Nectar retry policy") };
        Assert.False(BroadRecallRanking.HasTopicEvidence("Nectar production overview", environmentOnly));
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(5, 20)]
    [InlineData(100, 132)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void ExtraCandidateBudgetIsBoundedAndDoesNotOverflow(int requested, int expected) =>
        Assert.Equal(expected, BroadRecallRanking.CandidateLimit(requested));
}
