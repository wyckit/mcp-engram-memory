using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class HybridSearchFilterTests
{
    private const string Ns = "expert_filters";
    private const string Query = "Needle";

    [Theory]
    [InlineData("fusion", false)]
    [InlineData("fusion", true)]
    [InlineData("cascade", false)]
    [InlineData("cascade", true)]
    [InlineData("early-exit", false)]
    [InlineData("early-exit", true)]
    public void SearchKeepsStateCategorySimilarityAndPartitionRestrictions(string path, bool rerank)
    {
        using var store = new RecallTestStore();
        for (int i = 0; i < (path == "cascade" ? 110 : 6); i++)
            Add(store, $"eligible-{i}", "Background context", path == "early-exit" ? .95f : .7f);
        Add(store, "archive", Query, .65f, state: "archived");
        Add(store, "category", Query, .65f, category: "reference");
        Add(store, "below-threshold", Query, .1f);
        Add(store, "other-namespace", Query, 1f, ns: "other");
        Add(store, "other-tenant", Query, 1f, tenant: "other");
        store.Index.Upsert(new CognitiveEntry("other-model", [1f, 0f, 0f], Ns, Query, "decision"));

        var result = store.Index.Search(Request(rerank) with { IncludeStates = ["stm", "ltm"] });

        Assert.Equal(5, result.Count);
        Assert.All(result, r => Assert.StartsWith("eligible-", r.Id));
        Assert.All(result, r => Assert.Equal("decision", r.Category));
        Assert.All(result, r => Assert.Equal("ltm", r.LifecycleState));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NoEligibleVectorResultsDoesNotPermitExcludedKeywordHits(bool cascade, bool rerank)
    {
        using var store = new RecallTestStore();
        Add(store, "archive", Query, .65f, state: "archived");
        Add(store, "category", Query, .65f, category: "reference");
        for (int i = 0; i < (cascade ? 110 : 1); i++)
            Add(store, $"below-{i}", Query, .1f);

        Assert.Empty(store.Index.Search(Request(rerank)));
    }

    [Theory]
    [InlineData("default", false)]
    [InlineData("default", true)]
    [InlineData("archive", false)]
    [InlineData("archive", true)]
    [InlineData("all", false)]
    [InlineData("all", true)]
    [InlineData("empty", false)]
    [InlineData("empty", true)]
    public void SearchHonorsDefaultExplicitAndEmptyStateSets(string states, bool rerank)
    {
        using var store = new RecallTestStore();
        Add(store, "live", Query, .7f);
        Add(store, "archive", Query, .65f, state: "archived");
        Add(store, "reference", Query, .6f, category: "reference");
        HashSet<string>? include = states switch
        {
            "archive" => ["archived"],
            "all" => ["stm", "ltm", "archived"],
            "empty" => [],
            _ => null
        };
        string[] expected = states switch
        {
            "archive" => ["archive"], "all" => ["archive", "live", "reference"],
            "empty" => [], _ => ["live", "reference"]
        };

        var result = store.Index.Search(Request(rerank) with { IncludeStates = include, Category = null });

        Assert.Equal(expected, result.Select(r => r.Id).Order(StringComparer.Ordinal));
        Assert.Equal("archived", store.Index.Get("archive", Ns, "")!.LifecycleState);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 5)]
    [InlineData(true, 1)]
    [InlineData(true, 5)]
    public void ExcludedKeywordMatchesCannotReplaceTheOnlyEligibleAnswer(bool rerank, int k)
    {
        using var store = new RecallTestStore();
        Add(store, "eligible", "Related context", .7f);
        Add(store, "archive", Query, .65f, state: "archived");
        Add(store, "category", Query, .65f, category: "reference");

        var result = store.Index.Search(Request(rerank) with { K = k });

        Assert.Equal("eligible", Assert.Single(result).Id);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void KeywordOnlyRescueRequiresEligibilityWithOrWithoutSemanticGate(bool semanticGate, bool rerank)
    {
        // Supply one prefiltered vector hit so the other live entry genuinely has
        // to be rescued by BM25, independently of the index's candidate-pool size.
        var vectorHit = new CognitiveSearchResult("vector", "Background context", .7f, "ltm", 0,
            "decision", null, false, null, 1);
        var entries = new[]
        {
            Entry("keyword", Query, .65f),
            Entry("archive", Query, .65f, "archived"),
            Entry("category", Query, .65f, category: "reference"),
            Entry("weak", Query, .1f)
        }.ToDictionary(e => e.Id);
        var bm25 = new BM25Index();
        foreach (var entry in entries.Values) bm25.Index(entry);
        var result = new HybridSearchEngine().HybridSearch([vectorHit], Query, Ns, 10, null, "decision",
            rerank, 60, bm25, new TokenReranker(), (id, _) => entries.GetValueOrDefault(id),
            queryVector: semanticGate ? [1f, 0f] : null);

        Assert.Contains(result, r => r.Id == "keyword");
        Assert.Contains(result, r => r.Id == "vector");
        Assert.DoesNotContain(result, r => r.Id is "archive" or "category");
        Assert.Equal(!semanticGate, result.Any(r => r.Id == "weak"));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("expert")]
    [InlineData("broadcast")]
    public void RecallStillRecoversAnArchiveThroughTheIntentionalFallback(string route)
    {
        using var store = new RecallTestStore();
        Add(store, "archive", Query, .8f, state: "archived");
        if (route == "expert") store.Dispatcher.CreateExpert("filters", Query);
        var result = Assert.IsType<RecallResult>(store.Tools().Recall(Query, route == "direct" ? Ns : null,
            k: 1, expandGraph: false, spectralMode: "none"));

        Assert.Equal(route == "direct" ? "deep_recall" : route == "expert" ? "expert_routed" : "broadcast", result.Strategy);
        Assert.Equal("archive", Assert.Single(result.Results).Id);
        Assert.Equal("stm", store.Index.Get("archive", Ns, "")!.LifecycleState);
    }

    private static SearchRequest Request(bool rerank) => new()
    {
        Query = [1f, 0f], QueryText = Query, Namespace = Ns, TenantId = "", K = 5,
        MinScore = .3f, Hybrid = true, Rerank = rerank, Category = "decision"
    };

    private static CognitiveEntry Entry(string id, string text, float cosine, string state = "ltm",
        string category = "decision", string ns = Ns, string tenant = "") => new(id,
        [cosine, MathF.Sqrt(1f - cosine * cosine)], ns, text, category, lifecycleState: state, tenantId: tenant);

    private static void Add(RecallTestStore store, string id, string text, float cosine, string state = "ltm",
        string category = "decision", string ns = Ns, string tenant = "") =>
        store.Index.Upsert(Entry(id, text, cosine, state, category, ns, tenant));
}
