using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Retrieval;

namespace McpEngramMemory.Tests;

public class HybridSearchEngineTelemetryTests
{
    [Fact]
    public void HybridSearch_HighConfidenceEarlyExit_RecordsCounter()
    {
        var metrics = new MetricsCollector();
        var engine = new HybridSearchEngine(metrics);

        engine.HybridSearch(new[] { Result("a", 0.90f), Result("b", 0.88f) },
            "query", "ns", k: 2, includeStates: null, category: null,
            rerank: false, rrfK: 60, bm25: new BM25Index(),
            reranker: new TokenReranker(), getEntry: (_, _) => null);

        Assert.Equal(1, metrics.GetSummary("hybrid.early_exit").Count);
    }

    [Fact]
    public void HybridSearch_AdaptiveRrfBands_RecordCounters()
    {
        var metrics = new MetricsCollector();
        var engine = new HybridSearchEngine(metrics);
        var bm25 = new BM25Index();
        var reranker = new TokenReranker();

        engine.HybridSearch(new[] { Result("a", 0.82f) },
            "query", "ns", k: 2, includeStates: null, category: null,
            rerank: false, rrfK: 60, bm25: bm25, reranker: reranker,
            getEntry: (_, _) => null);
        engine.HybridSearch(new[] { Result("b", 0.40f) },
            "query", "ns", k: 2, includeStates: null, category: null,
            rerank: false, rrfK: 60, bm25: bm25, reranker: reranker,
            getEntry: (_, _) => null);
        engine.HybridSearch(new[] { Result("c", 0.60f) },
            "query", "ns", k: 2, includeStates: null, category: null,
            rerank: false, rrfK: 60, bm25: bm25, reranker: reranker,
            getEntry: (_, _) => null);

        Assert.Equal(1, metrics.GetSummary("hybrid.adaptive.suppress").Count);
        Assert.Equal(1, metrics.GetSummary("hybrid.adaptive.rescue").Count);
        Assert.Equal(1, metrics.GetSummary("hybrid.adaptive.standard").Count);
    }

    [Fact]
    public void HybridSearch_Cascade_RecordsCounter()
    {
        var metrics = new MetricsCollector();
        var engine = new HybridSearchEngine(metrics);

        engine.HybridSearch(new[] { Result("a", 0.70f) },
            "query", "ns", k: 2, includeStates: null, category: null,
            rerank: false, rrfK: 60, bm25: new BM25Index(),
            reranker: new TokenReranker(), getEntry: (_, _) => null,
            queryVector: new[] { 1f, 0f }, entryCount: 100);

        Assert.Equal(1, metrics.GetSummary("hybrid.cascade").Count);
    }

    private static CognitiveSearchResult Result(string id, float score)
        => new(id, $"text {id}", score, "stm", 0f, null, null, false, null, 1);
}
