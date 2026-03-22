using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Retrieval;

namespace McpEngramMemory.Tests;

public class QueryExpanderTests
{
    private readonly QueryExpander _expander = new();

    [Fact]
    public void Expand_WithNoResults_ReturnsOriginalQuery()
    {
        var result = _expander.Expand("test query", Array.Empty<CognitiveSearchResult>());
        Assert.Equal("test query", result);
    }

    [Fact]
    public void Expand_WithSingleResult_ReturnsOriginalQuery()
    {
        // minDocFreq defaults to 2, so single result can't meet threshold
        var results = new[]
        {
            new CognitiveSearchResult("1", "machine learning algorithms for classification", 0.9f, "stm", 0f, null, null, false, null, 1)
        };
        var result = _expander.Expand("classification", results);
        Assert.Equal("classification", result);
    }

    [Fact]
    public void Expand_WithMultipleResults_AddsExpansionTerms()
    {
        var results = new[]
        {
            new CognitiveSearchResult("1", "neural network training with backpropagation gradient descent", 0.9f, "stm", 0f, null, null, false, null, 1),
            new CognitiveSearchResult("2", "deep learning neural network architectures and gradient optimization", 0.85f, "stm", 0f, null, null, false, null, 1),
            new CognitiveSearchResult("3", "neural network layers with gradient computation and backpropagation", 0.8f, "stm", 0f, null, null, false, null, 1)
        };

        var expanded = _expander.Expand("neural networks", results);

        // Should add terms that appear in 2+ docs
        Assert.NotEqual("neural networks", expanded);
        Assert.StartsWith("neural networks ", expanded);
        // "gradient" appears in all 3 docs, "backpropagation" in 2
        Assert.Contains("gradient", expanded);
    }

    [Fact]
    public void Expand_DoesNotDuplicateQueryTerms()
    {
        var results = new[]
        {
            new CognitiveSearchResult("1", "machine learning for prediction tasks", 0.9f, "stm", 0f, null, null, false, null, 1),
            new CognitiveSearchResult("2", "machine learning models and prediction accuracy", 0.85f, "stm", 0f, null, null, false, null, 1)
        };

        var expanded = _expander.Expand("machine learning", results);

        // Count occurrences of "machine" — should appear exactly once (from original query)
        var parts = expanded.Split(' ');
        Assert.Equal(1, parts.Count(p => p.Equals("machine", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Expand_SkipsStopWords()
    {
        var results = new[]
        {
            new CognitiveSearchResult("1", "the quick brown fox jumps over the lazy dog", 0.9f, "stm", 0f, null, null, false, null, 1),
            new CognitiveSearchResult("2", "the fast brown fox runs over the sleepy dog", 0.85f, "stm", 0f, null, null, false, null, 1)
        };

        var expanded = _expander.Expand("fox", results);

        // Should not include "the", "over", etc.
        Assert.DoesNotContain(" the ", " " + expanded + " ");
        Assert.DoesNotContain(" over ", " " + expanded + " ");
    }

    [Fact]
    public void Expand_LimitsMaxTerms()
    {
        var results = new[]
        {
            new CognitiveSearchResult("1", "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima", 0.9f, "stm", 0f, null, null, false, null, 1),
            new CognitiveSearchResult("2", "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima", 0.85f, "stm", 0f, null, null, false, null, 1)
        };

        var expanded = _expander.Expand("query", results, maxTerms: 3);

        // Original query + at most 3 expansion terms
        var parts = expanded.Split(' ');
        Assert.True(parts.Length <= 4, $"Expected at most 4 tokens, got {parts.Length}: {expanded}");
    }

    [Fact]
    public void Expand_EmptyQuery_ReturnsEmpty()
    {
        var results = new[]
        {
            new CognitiveSearchResult("1", "some text", 0.9f, "stm", 0f, null, null, false, null, 1),
            new CognitiveSearchResult("2", "more text", 0.85f, "stm", 0f, null, null, false, null, 1)
        };

        Assert.Equal("", _expander.Expand("", results));
    }

    [Fact]
    public void Expand_NullText_ReturnsOriginal()
    {
        var results = new[]
        {
            new CognitiveSearchResult("1", null, 0.9f, "stm", 0f, null, null, false, null, 1),
            new CognitiveSearchResult("2", null, 0.85f, "stm", 0f, null, null, false, null, 1)
        };

        var expanded = _expander.Expand("test query", results);
        Assert.Equal("test query", expanded);
    }

    [Fact]
    public void Expand_SkipsShortTerms()
    {
        var results = new[]
        {
            new CognitiveSearchResult("1", "AI ML NLP transformers attention", 0.9f, "stm", 0f, null, null, false, null, 1),
            new CognitiveSearchResult("2", "AI ML NLP models attention mechanism", 0.85f, "stm", 0f, null, null, false, null, 1)
        };

        var expanded = _expander.Expand("deep learning", results);

        // "AI", "ML", "NLP" are < 3 chars, should be skipped
        Assert.DoesNotContain(" AI ", " " + expanded + " ");
        Assert.DoesNotContain(" ML ", " " + expanded + " ");
    }

    // ── BM25 compound tokenization tests ──

    [Fact]
    public void BM25Tokenize_HyphenatedWord_EmitsCompoundAndParts()
    {
        var tokens = BM25Index.Tokenize("time-stamped sensor data");

        // Should emit sub-parts (stemmed) AND compound form (stemmed)
        Assert.Contains("time", tokens);
        Assert.Contains("stamp", tokens); // "stamped" → "stamp" via Porter stemmer
        Assert.Contains("timestamp", tokens); // "timestamped" → "timestamp" via stemmer
        Assert.Contains("sensor", tokens);
        Assert.Contains("data", tokens);
    }

    [Fact]
    public void BM25Tokenize_MultipleHyphens_EmitsAllForms()
    {
        var tokens = BM25Index.Tokenize("long-running-task");

        Assert.Contains("long", tokens);
        Assert.Contains("run", tokens); // "running" → "run" via Porter stemmer
        Assert.Contains("task", tokens);
        Assert.Contains("longrunningtask", tokens); // joined compound, stemmed
    }

    [Fact]
    public void BM25Tokenize_NoHyphens_NormalTokenization()
    {
        var tokens = BM25Index.Tokenize("vector search optimization");

        Assert.Contains("vector", tokens);
        Assert.Contains("search", tokens);
        Assert.Contains("optimize", tokens); // "optimization" → "optimize" via Porter stemmer
        Assert.DoesNotContain("-", tokens);
    }

    [Fact]
    public void BM25Tokenize_HyphenatedQueryMatchesJoinedSeed()
    {
        // This is the s08 scenario: query has "time-stamped" but seed has "timestamped"
        var queryTokens = BM25Index.Tokenize("time-stamped sensor data");
        var seedTokens = BM25Index.Tokenize("timestamped data from InfluxDB");

        // "timestamp" (stemmed from "timestamped") should appear in BOTH token sets
        Assert.Contains("timestamp", queryTokens);
        Assert.Contains("timestamp", seedTokens);
    }

    [Fact]
    public void BM25Search_HyphenatedQueryFindsJoinedEntry()
    {
        // End-to-end BM25 test for s08 scenario
        var bm25 = new BM25Index();
        var entry = new CognitiveEntry("s-timeseries", new float[4], "test",
            "Time-series databases like InfluxDB and TimescaleDB optimize for timestamped data ingestion",
            lifecycleState: "ltm");
        bm25.Index(entry);

        var results = bm25.Search("Storing and querying time-stamped sensor data", "test", k: 5);

        Assert.NotEmpty(results);
        Assert.Equal("s-timeseries", results[0].Id);
    }
}
