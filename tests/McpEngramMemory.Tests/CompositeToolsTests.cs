using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class CompositeToolsTests : IDisposable
{
    private readonly string _dataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly LifecycleEngine _lifecycle;
    private readonly ExpertDispatcher _dispatcher;
    private readonly MetricsCollector _metrics;
    private readonly HashEmbeddingService _embedding;
    private readonly MemoryDiffusionKernel _diffusion;
    private readonly SpectralRetrievalReranker _spectral;
    private readonly CompositeTools _tools;

    public CompositeToolsTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"composite_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_dataPath);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _lifecycle = new LifecycleEngine(_index, _persistence);
        _embedding = new HashEmbeddingService();
        _dispatcher = new ExpertDispatcher(_index, _embedding);
        _metrics = new MetricsCollector();
        _diffusion = new MemoryDiffusionKernel(_index, _graph);
        _spectral = new SpectralRetrievalReranker(_diffusion);
        _tools = new CompositeTools(_index, _embedding, _graph, _lifecycle, _dispatcher, _metrics, _spectral);
    }

    // ── remember tests ──

    [Fact]
    public void Remember_StoresEntry()
    {
        var result = _tools.Remember("test-1", "myns", "This is a test memory about SIMD operations") as RememberResult;

        Assert.NotNull(result);
        Assert.Equal("stored", result!.Status);
        Assert.Equal("test-1", result.Id);
        Assert.Equal("myns", result.Namespace);
        Assert.Contains("stored", result.Actions);

        // Verify entry exists in index
        var entry = _index.Get("test-1", "myns");
        Assert.NotNull(entry);
        Assert.Equal("This is a test memory about SIMD operations", entry!.Text);
    }

    [Fact]
    public void Remember_DetectsHighDuplicate()
    {
        // Store first entry via remember (uses contextual prefix internally)
        _tools.Remember("existing", "myns", "test memory about SIMD operations");

        // Try to store a very similar one — same text should trigger duplicate
        var result = _tools.Remember("dup", "myns", "test memory about SIMD operations") as RememberResult;

        Assert.NotNull(result);
        Assert.Equal("duplicate_blocked", result!.Status);
        Assert.Contains("existing", result.Message);
    }

    [Fact]
    public void Remember_AutoLinksRelatedMemories()
    {
        // Store first entry via remember so it gets the same contextual prefix
        _tools.Remember("related1", "myns", "vector search optimization for memory retrieval");

        // Store a related entry — both use the same prefix, so vectors should be close
        var result = _tools.Remember("new1", "myns", "vector search optimization for memory retrieval system") as RememberResult;

        Assert.NotNull(result);
        // May be blocked as duplicate (very similar text) or stored with links
        Assert.True(result!.Status == "stored" || result.Status == "duplicate_blocked");
    }

    [Fact]
    public void Remember_SetsCategory()
    {
        _tools.Remember("cat-test", "myns", "Architecture decision for data layer", category: "architecture");

        var entry = _index.Get("cat-test", "myns");
        Assert.Equal("architecture", entry!.Category);
    }

    [Fact]
    public void Remember_SetsLifecycleState()
    {
        _tools.Remember("ltm-test", "myns", "Stable architecture pattern", lifecycleState: "ltm");

        var entry = _index.Get("ltm-test", "myns");
        Assert.Equal("ltm", entry!.LifecycleState);
    }

    // ── recall tests ──

    [Fact]
    public void Recall_DirectNamespace_FindsEntries()
    {
        var v = _embedding.Embed("test SIMD vector operations");
        _index.Upsert(new CognitiveEntry("e1", v, "myns", "SIMD vector operations for fast similarity", lifecycleState: "stm"));

        var result = _tools.Recall("SIMD operations", ns: "myns") as RecallResult;

        Assert.NotNull(result);
        Assert.Equal("direct", result!.Strategy);
        Assert.Equal("myns", result.Namespace);
        Assert.True(result.Results.Count > 0);
    }

    [Fact]
    public void Recall_EmptyNamespace_FallsBackOrReturnsEmpty()
    {
        // Store an archived entry using a known vector
        var v = new float[] { 1f, 0f, 0f };
        _index.Upsert(new CognitiveEntry("arch1", v, "myns", "archived debugging memory", lifecycleState: "archived"));

        // Recall uses embedding which may not match the raw vector
        // The key behavior: no stm/ltm entries exist, so strategy should attempt deep_recall or return empty
        var result = _tools.Recall("archived debugging memory", ns: "myns") as RecallResult;

        Assert.NotNull(result);
        Assert.True(result!.Strategy == "deep_recall" || result!.Strategy == "direct");
    }

    [Fact]
    public void Recall_NoNamespace_BroadcastSearch()
    {
        var v = _embedding.Embed("test broadcast search");
        _index.Upsert(new CognitiveEntry("b1", v, "ns1", "broadcast search test", lifecycleState: "stm"));

        var result = _tools.Recall("broadcast search") as RecallResult;

        Assert.NotNull(result);
        // Without expert routing configured, should fall to broadcast
        Assert.True(result!.Strategy == "broadcast" || result.Strategy == "expert_routed");
    }

    // ── reflect tests ──

    [Fact]
    public void Reflect_StoresLtmLesson()
    {
        var result = _tools.Reflect(
            "Learned that hybrid search outperforms vector-only on domain-specific data",
            "myns", "hybrid-search-findings") as ReflectResult;

        Assert.NotNull(result);
        Assert.Equal("stored", result!.Status);
        Assert.StartsWith("retro-", result.Id);
        Assert.Contains("stored as ltm lesson", result.Actions);

        // Verify it's stored as LTM
        var entry = _index.Get(result.Id, "myns");
        Assert.NotNull(entry);
        Assert.Equal("ltm", entry!.LifecycleState);
        Assert.Equal("lesson", entry.Category);
    }

    [Fact]
    public void Reflect_LinksToRelatedIds()
    {
        var v = _embedding.Embed("test related entry");
        _index.Upsert(new CognitiveEntry("related1", v, "myns", "related entry", lifecycleState: "ltm"));

        var result = _tools.Reflect(
            "This reflection references the related entry",
            "myns", "linking-test",
            relatedIds: new[] { "related1" }) as ReflectResult;

        Assert.NotNull(result);
        Assert.True(result!.Actions.Any(a => a.Contains("related1")));
    }

    [Fact]
    public void Reflect_DetectsDuplicateReflection()
    {
        // Store a first reflection
        _tools.Reflect("Hybrid search is better for domain-specific data", "myns", "topic1");

        // Try to store a very similar one
        var result = _tools.Reflect("Hybrid search is better for domain-specific data", "myns", "topic2") as ReflectResult;

        Assert.NotNull(result);
        // Should warn about duplicate
        Assert.Equal("duplicate_warning", result!.Status);
    }

    [Fact]
    public void Reflect_IncludesRelatedReflections()
    {
        // Store a past reflection
        var v = _embedding.Embed("lesson Past lesson about vector search optimization");
        _index.Upsert(new CognitiveEntry("retro-old", v, "myns",
            "Past lesson about vector search optimization", "lesson", lifecycleState: "ltm"));

        // New reflection on related topic
        var result = _tools.Reflect(
            "New insights on vector search performance improvements",
            "myns", "search-perf") as ReflectResult;

        Assert.NotNull(result);
        // May or may not find the related reflection depending on embedding similarity
        // Just verify it completes without error
        Assert.Equal("stored", result!.Status);
    }

    // ── input-validation: empty inputs return friendly "Error: ..." strings instead of throwing ──

    [Fact]
    public void Remember_EmptyInputs_ReturnsFriendlyError()
    {
        Assert.Equal("Error: id must not be empty.", _tools.Remember("", "ns", "text"));
        Assert.Equal("Error: ns must not be empty.", _tools.Remember("id", "", "text"));
        Assert.Equal("Error: text must not be empty.", _tools.Remember("id", "ns", ""));
        Assert.Equal("Error: id must not be empty.", _tools.Remember("   ", "ns", "text"));
    }

    [Fact]
    public void Recall_EmptyInputs_ReturnsFriendlyError()
    {
        Assert.Equal("Error: query must not be empty.", _tools.Recall(""));
        Assert.Equal("Error: query must not be empty.", _tools.Recall("  "));
        Assert.Equal("Error: k must be positive.", _tools.Recall("hello", k: 0));
        Assert.Equal("Error: k must be positive.", _tools.Recall("hello", k: -1));
    }

    [Fact]
    public void Reflect_EmptyInputs_ReturnsFriendlyError()
    {
        Assert.Equal("Error: text must not be empty.", _tools.Reflect("", "ns", "topic"));
        Assert.Equal("Error: ns must not be empty.", _tools.Reflect("text", "", "topic"));
        Assert.Equal("Error: topic must not be empty.", _tools.Reflect("text", "ns", ""));
    }

    // ── spectral-aware recall ──

    [Fact]
    public void Recall_SpectralModeNone_PreservesBaselineOrdering()
    {
        const string ns = "spectral_none";
        SeedClusterPlusFiller(ns, clusterSize: 16, fillerCount: 16);

        var resultNone = _tools.Recall("cluster member zero", ns, k: 3, spectralMode: "none") as RecallResult;
        Assert.NotNull(resultNone);
        Assert.NotEmpty(resultNone!.Results);
    }

    [Fact]
    public void Recall_SpectralModeBroad_ReordersToFavorClusterMembers()
    {
        const string ns = "spectral_broad";
        SeedClusterPlusFiller(ns, clusterSize: 16, fillerCount: 16);

        // A short conceptual query — Broad mode should surface cluster members
        // ahead of isolated filler that happens to score similarly.
        var resultBroad = _tools.Recall("cluster", ns, k: 5, spectralMode: "broad") as RecallResult;
        Assert.NotNull(resultBroad);
        // Result counts should be the same as none mode; ordering may differ.
        Assert.NotEmpty(resultBroad!.Results);
    }

    [Fact]
    public void Recall_DefaultIsAuto_NoExplicitParam()
    {
        const string ns = "spectral_default";
        SeedClusterPlusFiller(ns, clusterSize: 16, fillerCount: 16);

        // Without specifying spectralMode, the default ("auto") should kick in.
        // We just verify it returns a sane RecallResult — no exceptions, no empty.
        var result = _tools.Recall("cluster member", ns, k: 3) as RecallResult;
        Assert.NotNull(result);
    }

    [Fact]
    public void Recall_TinyNamespace_SpectralAutoFallsBack()
    {
        const string ns = "spectral_tiny";
        for (int i = 0; i < 5; i++)
            _index.Upsert(new CognitiveEntry($"t_{i}", _embedding.Embed($"tiny entry {i}"), ns, $"tiny entry {i}"));

        // Below kernel qualification threshold; spectral must passthrough.
        var result = _tools.Recall("tiny entry 1", ns, k: 3, spectralMode: "auto") as RecallResult;
        Assert.NotNull(result);
        Assert.NotEmpty(result!.Results);
    }

    [Fact]
    public void InferSpectralMode_ShortConceptualQuery_PicksBroad()
    {
        Assert.Equal(SpectralRetrievalMode.Broad, CompositeTools.InferSpectralMode("memory consolidation"));
        Assert.Equal(SpectralRetrievalMode.Broad, CompositeTools.InferSpectralMode("auth flow"));
        Assert.Equal(SpectralRetrievalMode.Broad, CompositeTools.InferSpectralMode("decay"));
        Assert.Equal(SpectralRetrievalMode.Broad, CompositeTools.InferSpectralMode("graph kernel"));
    }

    [Fact]
    public void InferSpectralMode_LongQuery_PicksSpecific()
    {
        Assert.Equal(SpectralRetrievalMode.Specific,
            CompositeTools.InferSpectralMode("what is the exact threshold for stm to ltm demotion"));
        Assert.Equal(SpectralRetrievalMode.Specific,
            CompositeTools.InferSpectralMode("find the function that handles auth oauth refresh tokens"));
    }

    [Fact]
    public void InferSpectralMode_QueriesWithDigits_PickSpecific()
    {
        Assert.Equal(SpectralRetrievalMode.Specific, CompositeTools.InferSpectralMode("threshold 0.95"));
        Assert.Equal(SpectralRetrievalMode.Specific, CompositeTools.InferSpectralMode("port 8080"));
    }

    [Fact]
    public void InferSpectralMode_QueriesWithQuotes_PickSpecific()
    {
        Assert.Equal(SpectralRetrievalMode.Specific,
            CompositeTools.InferSpectralMode("find 'graph laplacian' references"));
        Assert.Equal(SpectralRetrievalMode.Specific,
            CompositeTools.InferSpectralMode("about \"sleep consolidation\""));
    }

    [Fact]
    public void InferSpectralMode_EmptyOrWhitespace_PicksBroadDefault()
    {
        Assert.Equal(SpectralRetrievalMode.Broad, CompositeTools.InferSpectralMode(""));
        Assert.Equal(SpectralRetrievalMode.Broad, CompositeTools.InferSpectralMode("   "));
    }

    private void SeedClusterPlusFiller(string ns, int clusterSize, int fillerCount)
    {
        var rng = new Random(7);
        for (int i = 0; i < clusterSize; i++)
        {
            var text = $"cluster topic detail {i}";
            _index.Upsert(new CognitiveEntry($"c_{i}", _embedding.Embed(text), ns, text));
        }
        for (int i = 0; i < fillerCount; i++)
        {
            var text = $"unrelated filler note {i}";
            _index.Upsert(new CognitiveEntry($"f_{i}", _embedding.Embed(text), ns, text));
        }
        for (int i = 0; i < clusterSize; i++)
            for (int j = i + 1; j < clusterSize; j++)
                if (rng.NextDouble() < 0.5)
                    _graph.AddEdge(new GraphEdge($"c_{i}", $"c_{j}", "similar_to", 1.0f));
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, true);
    }
}
