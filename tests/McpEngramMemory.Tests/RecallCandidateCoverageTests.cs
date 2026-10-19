using McpEngramMemory.Core.Models;
using McpEngramMemory.Tools;
using static McpEngramMemory.Tests.RecallResolutionTests;

namespace McpEngramMemory.Tests;

public class RecallCandidateCoverageTests
{
    private const string Query = "What is the Atlas gateway timeout?";
    private const string Claim = "Atlas gateway timeout is 45 seconds.";
    private const string Other = "Atlas gateway timeout is 30 seconds.";

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(100)]
    public void BroadcastComparesRelevanceAcrossHybridPaths(int namespaceSize)
    {
        using var store = new RecallTestStore();
        store.Add("answer", "atlas", Claim, cosine: .84f);
        store.Add("distractor", "boreal", "Boreal gateway timeout is 30 seconds.", cosine: .86f);
        for (int i = 1; i < namespaceSize; i++)
        {
            store.Add("filler-" + i, "atlas", "unrelated gardening notes", cosine: .35f);
            store.Add("filler-" + i, "boreal", "unrelated gardening notes", cosine: .35f);
        }
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, k: 2,
            expandGraph: false, spectralMode: "none"));
        Assert.Equal("answer", result.Results[0].Id);
        Assert.True(result.Results[0].Score > result.Results[1].Score);
    }

    [Fact]
    public void BroadcastWithoutLexicalTermsStillUsesComparableScores()
    {
        using var store = new RecallTestStore();
        store.Add("first", "atlas", Claim, cosine: .84f);
        store.Add("second", "boreal", Other, cosine: .86f);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall("???", k: 2,
            expandGraph: false, spectralMode: "none"));
        Assert.Equal(new[] { "second", "first" }, result.Results.Select(r => r.Id));
        Assert.Equal(.86f, result.Results[0].Score, precision: 5);
        Assert.Equal(.84f, result.Results[1].Score, precision: 5);
    }

    [Theory]
    [InlineData(Query, false)]
    [InlineData("What was the Atlas gateway timeout?", true)]
    public void ExplicitOptOutAndHistoricalBroadcastKeepNamespaceScores(string query, bool rerank)
    {
        using var store = new RecallTestStore();
        store.Add("answer", "atlas", Claim, cosine: .84f);
        store.Add("distractor", "boreal", "Boreal gateway timeout is 30 seconds.", cosine: .86f);
        var expected = new[] { "atlas", "boreal" }.SelectMany(ns =>
            Assert.IsType<RecallResult>(store.Tools().Recall(query, ns, k: 2, rerank: rerank,
                expandGraph: false, spectralMode: "none")).Results).OrderByDescending(r => r.Score);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(query, k: 2, rerank: rerank,
            expandGraph: false, spectralMode: "none"));
        Assert.Equal(expected.Select(r => (r.Id, r.Score)), result.Results.Select(r => (r.Id, r.Score)));
        Assert.Null(result.Conflicts);
    }

    [Fact]
    public void NoncontributingNamespaceDoesNotChangeSingleNamespaceScores()
    {
        using var store = new RecallTestStore();
        store.Add("answer", "atlas", Claim, cosine: .84f);
        store.Add("below-threshold", "boreal", Claim, cosine: .1f);
        var direct = Assert.IsType<RecallResult>(store.Tools().Recall(Query, "atlas",
            expandGraph: false, spectralMode: "none"));
        var broadcast = Assert.IsType<RecallResult>(store.Tools().Recall(Query,
            expandGraph: false, spectralMode: "none"));
        Assert.Equal(direct.Results.Select(r => (r.Id, r.Score)), broadcast.Results.Select(r => (r.Id, r.Score)));
    }

    [Fact]
    public void BroadcastKeepsDiagnosticProvenanceWhenAClaimAlsoArrivesThroughAnotherGraph()
    {
        using var store = new RecallTestStore();
        store.Add("seed", "first", "Atlas gateway timeout", cosine: .83f);
        store.Add("a", "second", Other, cosine: .75f);
        store.Add("b", "second", Claim, cosine: .84f);
        store.Graph.AddEdge(new GraphEdge("seed", "b", "related_to"));
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, k: 2, spectralMode: "none"));
        Assert.Equal(new[] { "b", "seed" }, result.Results.Select(r => r.Id));
        Assert.Contains(result.Conflicts!, c => c.Evidence.Select(e => e.Id).Order().SequenceEqual(new[] { "a", "b" }));
        Assert.Equal(1, store.Index.Get("a", "second", "")!.AccessCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public void SmallResultBudgetStillInspectsSeveralCopies(int copies)
    {
        using var store = new RecallTestStore();
        store.Add("a", "notes", Other, cosine: .80f);
        store.Add("b", "notes", Claim, cosine: .81f);
        for (int i = 0; i < copies; i++) store.Add("copy-" + i, "notes", Claim, cosine: .99f);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, "notes", k: 1,
            expandGraph: false, spectralMode: "none"));
        Assert.Single(result.Results);
        Assert.Contains(result.Conflicts!, c => c.Evidence.Select(e => e.Id).Order().SequenceEqual(new[] { "a", "b" }));
        Assert.All(result.Conflicts!, c => Assert.Equal("unresolved", c.Status));
        Assert.Equal(1, store.Index.Get("a", "notes", "")!.AccessCount);
        Assert.Equal(1, store.Index.Get("b", "notes", "")!.AccessCount);
    }

    [Fact]
    public void EquivalentAnchorDoesNotIntroduceAnUnselectedSubjectsConflict()
    {
        using var store = new RecallTestStore();
        store.Add("selected", "notes", Claim, cosine: .99f);
        store.Add("copy", "notes", Claim + " Atlas retry limit is 2.", cosine: .85f);
        store.Add("other", "notes", "Atlas retry limit is 3.", cosine: .8f);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, "notes", k: 1,
            expandGraph: false, spectralMode: "none"));
        Assert.Equal("selected", Assert.Single(result.Results).Id);
        Assert.Null(result.Conflicts);
    }

    [Theory]
    [InlineData("direct", false)]
    [InlineData("expert", false)]
    [InlineData("broadcast", false)]
    [InlineData("direct", true)]
    [InlineData("expert", true)]
    [InlineData("broadcast", true)]
    public void CopiedClaimDoesNotHideTheOriginalPair(string route, bool restart)
    {
        using var store = new RecallTestStore();
        const string ns = "expert_coverage";
        store.Add("a", ns, Other, cosine: .80f);
        store.Add("b", ns, Claim, cosine: .81f);
        store.Add("source", ns, Claim, cosine: .99f);
        if (restart) store.Restart();
        if (route == "expert") store.Dispatcher.CreateExpert("coverage", Query);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, route == "direct" ? ns : null,
            k: 1, expandGraph: false, spectralMode: "none"));
        Assert.Equal("source", Assert.Single(result.Results).Id);
        Assert.Contains(result.Conflicts!, c => c.Evidence.Select(e => e.Id).Order().SequenceEqual(new[] { "a", "b" }));
        Assert.Equal(1, store.Index.Get("a", ns, "")!.AccessCount);
        Assert.Equal(1, store.Index.Get("b", ns, "")!.AccessCount);
        Assert.Equal(2, store.Index.Get("source", ns, "")!.AccessCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GovernedPairResolvesWhenItsUncitedCopyIsTheOnlyPrimaryHit(bool broadcast)
    {
        using var store = Create();
        // The original candidate has a citation; its higher-scoring copy does not.
        store.Add("a", Ns, Other, cosine: .80f, tenant: Tenant);
        store.Add("b", Ns, Claim, cosine: .81f, tenant: Tenant);
        await Support(store, "b", Claim, "source", Claim);
        store.Add("source", Ns, Claim, cosine: 1f, tenant: Tenant);
        store.Restart();
        var result = await Recall(store, broadcast);
        Assert.Equal("source", Assert.Single(result.Results).Id);
        Assert.Equal("resolved", Pair(result).Status);
        Assert.Equal("b", Pair(result).Resolution!.PreferredId);
        Assert.Equal(1, store.Index.Get("a", Ns, Tenant)!.AccessCount);
        Assert.Equal(1, store.Index.Get("b", Ns, Tenant)!.AccessCount);
    }
}
