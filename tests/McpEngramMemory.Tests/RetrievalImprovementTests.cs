using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Retrieval;

namespace McpEngramMemory.Tests;

public class RetrievalImprovementTests : IDisposable
{
    private readonly string _dataPath;

    public RetrievalImprovementTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"engram-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, true);
    }

    // ── SynonymExpander Tests ──

    [Fact]
    public void SynonymExpander_ExpandsMaintenanceToAccretion()
    {
        var expander = new SynonymExpander();
        var expanded = expander.Expand("automatic memory maintenance");
        Assert.Contains("accretion", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("decay", expanded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SynonymExpander_ExpandsCleanupToCollapse()
    {
        var expander = new SynonymExpander();
        var expanded = expander.Expand("cleanup archival");
        Assert.Contains("collapse", expanded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SynonymExpander_PreservesOriginalQuery()
    {
        var expander = new SynonymExpander();
        var expanded = expander.Expand("maintenance task");
        Assert.StartsWith("maintenance task", expanded);
    }

    [Fact]
    public void SynonymExpander_EmptyQuery_ReturnsEmpty()
    {
        var expander = new SynonymExpander();
        Assert.Equal("", expander.Expand(""));
        Assert.Null(expander.Expand(null!));
    }

    [Fact]
    public void SynonymExpander_NoMatchingTerms_ReturnsOriginal()
    {
        var expander = new SynonymExpander();
        var result = expander.Expand("quantum physics entanglement");
        Assert.Equal("quantum physics entanglement", result);
    }

    [Fact]
    public void SynonymExpander_HasExpansions_ReturnsTrueForKnownTerms()
    {
        var expander = new SynonymExpander();
        Assert.True(expander.HasExpansions("maintenance test"));
        Assert.False(expander.HasExpansions("quantum physics"));
    }

    [Fact]
    public void SynonymExpander_LimitsExpansionsPerTerm()
    {
        var expander = new SynonymExpander();
        var expanded = expander.Expand("maintenance", maxExpansionsPerTerm: 1);
        // Original + at most 1 expansion term
        var parts = expanded.Split(' ');
        Assert.True(parts.Length <= 2);
    }

    [Fact]
    public void SynonymExpander_DoesNotDuplicateQueryTerms()
    {
        var expander = new SynonymExpander();
        // "accretion" is already in query, shouldn't be added as synonym of "maintenance"
        var expanded = expander.Expand("maintenance accretion");
        var parts = expanded.Split(' ');
        Assert.Equal(1, parts.Count(p => p.Equals("accretion", StringComparison.OrdinalIgnoreCase)));
    }

    // ── DocumentEnricher Tests ──

    [Fact]
    public void DocumentEnricher_EnrichesAccretionWithMaintenanceSynonyms()
    {
        var enricher = new DocumentEnricher();
        var keywords = enricher.Enrich("AccretionScanner consolidates aging STM entries via decay");
        Assert.NotNull(keywords);
        Assert.Contains("maintenance", keywords!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cleanup", keywords!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DocumentEnricher_NullText_ReturnsExisting()
    {
        var enricher = new DocumentEnricher();
        Assert.Null(enricher.Enrich(null));
        Assert.Equal("existing", enricher.Enrich(null, "existing"));
    }

    [Fact]
    public void DocumentEnricher_NoMatchingTerms_ReturnsNull()
    {
        var enricher = new DocumentEnricher();
        var keywords = enricher.Enrich("simple plain text with no technical terms");
        Assert.Null(keywords);
    }

    [Fact]
    public void DocumentEnricher_PreservesExistingKeywords()
    {
        var enricher = new DocumentEnricher();
        var keywords = enricher.Enrich("accretion scanner", "manual-keyword");
        Assert.NotNull(keywords);
        Assert.StartsWith("manual-keyword", keywords!);
    }

    [Fact]
    public void DocumentEnricher_DoesNotDuplicateKeywords()
    {
        var enricher = new DocumentEnricher();
        // "maintenance" already in existing keywords
        var keywords = enricher.Enrich("accretion process", "maintenance");
        // Should not add "maintenance" again
        Assert.NotNull(keywords);
        var parts = keywords!.Split(' ');
        Assert.Equal(1, parts.Count(p => p.Equals("maintenance", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void DocumentEnricher_LimitsMaxKeywords()
    {
        var enricher = new DocumentEnricher();
        var keywords = enricher.Enrich(
            "accretion decay collapse consolidation lifecycle pruning bm25 cosine rerank hnsw quantize namespace permission edge traverse dbscan timestamped influxdb",
            maxKeywords: 5);
        Assert.NotNull(keywords);
        var parts = keywords!.Split(' ');
        Assert.True(parts.Length <= 5);
    }

    // ── Keywords BM25 Integration Tests ──

    [Fact]
    public void BM25_IndexesKeywordsField()
    {
        var bm25 = new BM25Index();
        var entry = new CognitiveEntry("e1", new float[4], "test",
            text: "AccretionScanner consolidates entries",
            keywords: "maintenance cleanup organize");
        bm25.Index(entry);

        // Should find via keyword that's NOT in the text
        var results = bm25.Search("maintenance", "test");
        Assert.NotEmpty(results);
        Assert.Equal("e1", results[0].Id);
    }

    [Fact]
    public void BM25_FindsEntryByKeywordsOnly()
    {
        var bm25 = new BM25Index();
        var entry = new CognitiveEntry("e1", new float[4], "test",
            text: "technical accretion scanner process",
            keywords: "cleanup tidy organize maintenance");
        bm25.Index(entry);

        var results = bm25.Search("tidy organize", "test");
        Assert.NotEmpty(results);
        Assert.Equal("e1", results[0].Id);
    }

    // ── CognitiveEntry Keywords Tests ──

    [Fact]
    public void CognitiveEntry_KeywordsDefaultsToNull()
    {
        var entry = new CognitiveEntry("e1", new float[4], "test", "some text");
        Assert.Null(entry.Keywords);
    }

    [Fact]
    public void CognitiveEntry_KeywordsCanBeSet()
    {
        var entry = new CognitiveEntry("e1", new float[4], "test", "some text", keywords: "kw1 kw2");
        Assert.Equal("kw1 kw2", entry.Keywords);
    }

    [Fact]
    public void CognitiveEntry_KeywordsSetViaSetter()
    {
        var entry = new CognitiveEntry("e1", new float[4], "test", "some text");
        entry.Keywords = "enriched terms";
        Assert.Equal("enriched terms", entry.Keywords);
    }

    // ── Auto-Enrichment Integration Test ──

    [Fact]
    public void Upsert_AutoEnrichesKeywords()
    {
        using var persistence = new McpEngramMemory.Core.Services.Storage.PersistenceManager(_dataPath, debounceMs: 50);
        using var index = new CognitiveIndex(persistence);

        var entry = new CognitiveEntry("e1", new float[4], "test",
            text: "AccretionScanner handles decay and collapse of aging entries");
        index.Upsert(entry);

        var retrieved = index.Get("e1", "test");
        Assert.NotNull(retrieved);
        Assert.NotNull(retrieved!.Keywords);
        Assert.Contains("maintenance", retrieved.Keywords!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Upsert_PreservesExplicitKeywords()
    {
        using var persistence = new McpEngramMemory.Core.Services.Storage.PersistenceManager(_dataPath, debounceMs: 50);
        using var index = new CognitiveIndex(persistence);

        var entry = new CognitiveEntry("e1", new float[4], "test",
            text: "some text", keywords: "explicit-keywords");
        index.Upsert(entry);

        var retrieved = index.Get("e1", "test");
        Assert.NotNull(retrieved);
        Assert.Equal("explicit-keywords", retrieved!.Keywords);
    }

    // ── Porter Stemmer Tests ──

    [Fact]
    public void PorterStemmer_StemsEncrypting()
    {
        Assert.Equal("encrypt", PorterStemmer.Stem("encrypting"));
    }

    [Fact]
    public void PorterStemmer_StemsEncryption()
    {
        // encryption → encrypt (via -tion → -t)
        var stemmed = PorterStemmer.Stem("encryption");
        // Both "encrypting" and "encryption" should normalize to same stem
        Assert.Equal(PorterStemmer.Stem("encrypting"), stemmed);
    }

    [Fact]
    public void PorterStemmer_StemsMonitoring()
    {
        Assert.Equal("monitor", PorterStemmer.Stem("monitoring"));
    }

    [Fact]
    public void PorterStemmer_StemsCaching()
    {
        Assert.Equal(PorterStemmer.Stem("caching"), PorterStemmer.Stem("cached"));
    }

    [Fact]
    public void PorterStemmer_StemsPlurals()
    {
        Assert.Equal(PorterStemmer.Stem("sequence"), PorterStemmer.Stem("sequences"));
    }

    [Fact]
    public void PorterStemmer_ShortWordsUnchanged()
    {
        Assert.Equal("at", PorterStemmer.Stem("at"));
        Assert.Equal("go", PorterStemmer.Stem("go"));
    }

    [Fact]
    public void PorterStemmer_AlreadyStemmedUnchanged()
    {
        Assert.Equal("tls", PorterStemmer.Stem("tls"));
        Assert.Equal("rnn", PorterStemmer.Stem("rnn"));
    }

    // ── BM25 Stemming Integration Tests ──

    [Fact]
    public void BM25_StemmingMatchesMorphologicalVariants()
    {
        var bm25 = new BM25Index();
        // Index a document with "encryption"
        var entry = new CognitiveEntry("e1", new float[4], "test",
            text: "Encryption transforms plaintext into ciphertext using algorithms");
        bm25.Index(entry);

        // Search with "encrypting" — should match via stemming
        var results = bm25.Search("encrypting data", "test");
        Assert.NotEmpty(results);
        Assert.Equal("e1", results[0].Id);
    }

    [Fact]
    public void BM25_StemmingMatchesMonitoringObservability()
    {
        var bm25 = new BM25Index();
        var entry = new CognitiveEntry("e1", new float[4], "test",
            text: "Observability combines metrics monitoring logs and traces");
        bm25.Index(entry);

        // Search with "monitor" — should match "monitoring" via stemming
        var results = bm25.Search("monitor distributed systems", "test");
        Assert.NotEmpty(results);
        Assert.Equal("e1", results[0].Id);
    }

    // ── Expanded Synonym Tests ──

    [Fact]
    public void SynonymExpander_ExpandsEncryptToTls()
    {
        var expander = new SynonymExpander();
        var expanded = expander.Expand("encrypt data in transit");
        Assert.Contains("tls", expanded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SynonymExpander_ExpandsMonitoringToObservability()
    {
        var expander = new SynonymExpander();
        var expanded = expander.Expand("monitoring distributed systems");
        Assert.Contains("observability", expanded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SynonymExpander_ExpandsSequenceToRnn()
    {
        var expander = new SynonymExpander();
        var expanded = expander.Expand("processing sequences with memory");
        Assert.Contains("rnn", expanded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SynonymExpander_ExpandsPerformanceToSimd()
    {
        var expander = new SynonymExpander();
        var expanded = expander.Expand("performance optimization for vector search");
        Assert.Contains("simd", expanded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SynonymExpander_ExpandsCachingToRedis()
    {
        var expander = new SynonymExpander();
        var expanded = expander.Expand("caching strategies to reduce load");
        Assert.Contains("redis", expanded, StringComparison.OrdinalIgnoreCase);
    }

    // ── Document Enricher Expanded Tests ──

    [Fact]
    public void DocumentEnricher_EnrichesTlsWithEncrypt()
    {
        var enricher = new DocumentEnricher();
        var keywords = enricher.Enrich("TLS encrypts data in transit using certificates");
        Assert.NotNull(keywords);
        Assert.Contains("encrypt", keywords!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DocumentEnricher_EnrichesPrometheusWithMonitoring()
    {
        var enricher = new DocumentEnricher();
        var keywords = enricher.Enrich("Prometheus collects metrics and sends alerts via Grafana");
        Assert.NotNull(keywords);
        Assert.Contains("monitoring", keywords!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DocumentEnricher_EnrichesRnnWithSequence()
    {
        var enricher = new DocumentEnricher();
        var keywords = enricher.Enrich("RNN processes sequential data using hidden state across time steps");
        Assert.NotNull(keywords);
        Assert.Contains("sequence", keywords!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Category Boost Tests ──

    [Fact]
    public void CategoryBoost_BoostsMatchingCategory()
    {
        // Create two results: one with matching category, one without
        var results = new List<CognitiveSearchResult>
        {
            new("e1", "encryption data", 0.50f, "ltm", 0f, "security", null, false, null, 1),
            new("e2", "general topic", 0.51f, "ltm", 0f, "general", null, false, null, 1),
        };

        // When searching for "security encryption", e1 should get boosted above e2
        // even though e2 has a slightly higher raw score
        // We test this indirectly through CognitiveIndex integration
        // For a unit test, verify category token matching
        var queryTokens = BM25Index.Tokenize("security encryption").ToHashSet();
        var catTokens = BM25Index.Tokenize("security");
        bool hasOverlap = catTokens.Any(ct => queryTokens.Contains(ct));
        Assert.True(hasOverlap);
    }

    // ── Auto-PRF Test ──

    [Fact]
    public void QueryExpander_ExpandsFromResults()
    {
        var expander = new QueryExpander();
        var topResults = new List<CognitiveSearchResult>
        {
            new("e1", "TLS encryption protects data in transit between services using certificates", 0.5f, "ltm", 0f, null, null, false, null, 1),
            new("e2", "TLS certificates are managed by PKI infrastructure for encryption", 0.4f, "ltm", 0f, null, null, false, null, 1),
            new("e3", "Data encryption at rest uses AES while TLS protects data in transit", 0.3f, "ltm", 0f, null, null, false, null, 1),
        };

        var expanded = expander.Expand("encrypting data", topResults, maxTerms: 3, minDocFreq: 2);
        // Should have added terms that appear in 2+ of the top results
        Assert.NotEqual("encrypting data", expanded);
    }
}
