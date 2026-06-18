using BenchmarkDotNet.Attributes;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Benchmarks;

/// <summary>
/// Scaling micro-benchmarks for the core retrieval paths, measured at 100 / 10k / 100k
/// entries in a single namespace. Reports mean latency (BenchmarkDotNet) and allocations
/// per operation ([MemoryDiagnoser]).
///
/// Embeddings use <see cref="HashEmbeddingService"/> — a fast, deterministic, network-free
/// embedder — so the numbers isolate the retrieval engine (HNSW / BM25 / RRF / graph) and
/// are NOT distorted by ONNX model load or inference cost. Recall/precision quality is
/// validated separately by the IR-quality harness (see docs/benchmarks.md); this project
/// measures throughput/latency/allocation only.
///
/// Persistence uses a temp-dir <see cref="PersistenceManager"/> with a long debounce so
/// no disk writes occur on the hot path during a benchmark run.
/// </summary>
[MemoryDiagnoser]
public class RetrievalScalingBenchmarks
{
    private const string Ns = "bench";
    private const int Dimensions = 384;

    [Params(100, 10_000, 100_000)]
    public int EntryCount { get; set; }

    private string _dataPath = null!;
    private PersistenceManager _persistence = null!;
    private CognitiveIndex _index = null!;
    private KnowledgeGraph _graph = null!;
    private HashEmbeddingService _embedding = null!;

    // CognitiveIndex keeps its BM25 index private, so we build a parallel standalone
    // BM25Index seeded with the same documents to measure the BM25-only path directly.
    private BM25Index _bm25Index = null!;

    // Pre-computed query inputs so the embed cost is not folded into the search benchmark.
    private float[] _queryVector = null!;
    private string _queryText = null!;

    // A real entry id that has graph neighbors, for the 1-hop expansion benchmark.
    private string _graphSeedId = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"engram_bench_{Guid.NewGuid():N}");
        // Large debounce + we never Flush -> no disk I/O on the measured paths.
        _persistence = new PersistenceManager(_dataPath, debounceMs: 600_000);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _embedding = new HashEmbeddingService(Dimensions);

        _bm25Index = new BM25Index();

        // Seed EntryCount synthetic entries. Deterministic text so embeddings (and thus
        // BM25 vocabulary) are stable across runs. Batched to amortize lock acquisition.
        const int batchSize = 5_000;
        var batch = new List<CognitiveEntry>(Math.Min(batchSize, EntryCount));
        for (int i = 0; i < EntryCount; i++)
        {
            string text = SyntheticText(i);
            var entry = new CognitiveEntry(
                id: $"e{i}",
                vector: _embedding.Embed(text),
                ns: Ns,
                text: text,
                category: i % 5 == 0 ? "decision" : "reference",
                lifecycleState: "stm");
            batch.Add(entry);
            _bm25Index.Index(entry);

            if (batch.Count >= batchSize)
            {
                _index.UpsertBatch(batch);
                batch.Clear();
            }
        }
        if (batch.Count > 0)
            _index.UpsertBatch(batch);

        // Build a small star of graph edges around entry 0 so GetNeighbors has work to do.
        _graphSeedId = "e0";
        int neighborCount = Math.Min(20, EntryCount - 1);
        var edges = new List<GraphEdge>(neighborCount);
        for (int i = 1; i <= neighborCount; i++)
            edges.Add(new GraphEdge(_graphSeedId, $"e{i}", "similar_to", 0.9f));
        _graph.AddEdges(edges);

        // Warm the HNSW index (built lazily on first search) and prepare query inputs.
        _queryText = SyntheticText(EntryCount / 2);
        _queryVector = _embedding.Embed(_queryText);
        _ = _index.Search(_queryVector, Ns, k: 10);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _index.Dispose();
        _persistence.Dispose();
        try
        {
            if (Directory.Exists(_dataPath))
                Directory.Delete(_dataPath, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup; ignore residual locked files.
        }
    }

    /// <summary>Vector-only k-NN search via the HNSW index (approximate nearest neighbor).</summary>
    [Benchmark(Description = "Vector (HNSW) k=10")]
    public IReadOnlyList<CognitiveSearchResult> VectorSearch()
        => _index.Search(_queryVector, Ns, k: 10);

    /// <summary>BM25 keyword-only ranking over the namespace inverted index.</summary>
    [Benchmark(Description = "BM25 k=10")]
    public IReadOnlyList<(string Id, float Score)> Bm25Search()
        => _bm25Index.Search(_queryText, Ns, k: 10);

    /// <summary>Full hybrid search: HNSW candidates + BM25, fused via Reciprocal Rank Fusion.</summary>
    [Benchmark(Description = "Hybrid RRF k=10")]
    public IReadOnlyList<CognitiveSearchResult> HybridSearch()
        => _index.HybridSearch(_queryVector, _queryText, Ns, k: 10);

    /// <summary>Graph 1-hop expansion: resolve all direct neighbors of a seed entry.</summary>
    [Benchmark(Description = "Graph 1-hop neighbors")]
    public GetNeighborsResult GraphOneHop()
        => _graph.GetNeighbors(_graphSeedId);

    /// <summary>Deterministic, lexically varied document text for entry <paramref name="i"/>.</summary>
    private static string SyntheticText(int i)
        => $"memory entry {i} about distributed systems vector search indexing " +
           $"retrieval namespace partition lifecycle topic{i % 37} concept{i % 53} " +
           $"keyword{i % 71} cognitive engram knowledge graph hybrid ranking";
}
