using System.Text;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;
// OnnxEmbeddingService lives directly under Core.Services (already imported above).
using McpEngramMemory.Tools;
using Xunit.Abstractions;

namespace McpEngramMemory.Tests;

/// <summary>
/// Benchmark harness comparing the four spectralMode settings for recall.
/// Builds a synthetic 5-topic corpus with 5 entries per topic, adds intra-topic
/// edges (the same shape auto-link would produce), and runs both broad
/// (short, conceptual) and specific (long, precise) queries through each mode.
/// Reports Recall@K, Precision@K, MRR, and nDCG@K so we can see where each
/// mode helps or hurts before committing the default.
///
/// Run with:
///   dotnet test --filter "FullyQualifiedName~SpectralRetrievalBenchmark"
/// Output is via ITestOutputHelper — run with logger=console or look at the
/// test output in the IDE / vstest results.
/// </summary>
public class SpectralRetrievalBenchmark : IDisposable
{
    private readonly string _dataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly LifecycleEngine _lifecycle;
    private readonly ExpertDispatcher _dispatcher;
    private readonly MetricsCollector _metrics;
    private readonly OnnxEmbeddingService _embedding;
    private readonly MemoryDiffusionKernel _diffusion;
    private readonly SpectralRetrievalReranker _spectral;
    private readonly CompositeTools _tools;
    private readonly ITestOutputHelper _output;

    public SpectralRetrievalBenchmark(ITestOutputHelper output)
    {
        _output = output;
        _dataPath = Path.Combine(Path.GetTempPath(), $"spectral_bench_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_dataPath);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _lifecycle = new LifecycleEngine(_index, _persistence);
        _embedding = new OnnxEmbeddingService();
        _dispatcher = new ExpertDispatcher(_index, _embedding);
        _metrics = new MetricsCollector();
        _diffusion = new MemoryDiffusionKernel(_index, _graph);
        _spectral = new SpectralRetrievalReranker(_diffusion);
        _tools = new CompositeTools(_index, _embedding, _graph, _lifecycle, _dispatcher, _metrics, _spectral);
    }

    public void Dispose()
    {
        _index.Dispose();
        _embedding.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, true);
    }

    [Fact]
    public void CompareSpectralModesOnSyntheticCorpus()
    {
        const string ns = "bench";
        var dataset = BuildDataset();
        SeedDataset(ns, dataset);

        var modes = new[] { "none", "broad", "specific", "auto" };
        var allRuns = new List<ModeRunSummary>();

        foreach (var mode in modes)
        {
            var perQuery = new List<QueryScore>();
            foreach (var query in dataset.Queries)
            {
                var result = _tools.Recall(query.QueryText, ns, k: query.K, spectralMode: mode) as RecallResult;
                Assert.NotNull(result);
                var actualIds = result!.Results.Select(r => r.Id).ToList();
                var relevantIds = query.RelevanceGrades.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToHashSet();

                perQuery.Add(new QueryScore(
                    query.QueryId,
                    Recall(actualIds, relevantIds, query.K),
                    Precision(actualIds, relevantIds, query.K),
                    Mrr(actualIds, relevantIds),
                    Ndcg(actualIds, query.RelevanceGrades, query.K),
                    LatencyMs: 0,
                    actualIds));
            }
            allRuns.Add(Aggregate(mode, perQuery, dataset));
        }

        PrintResultsTable(allRuns, dataset);
    }

    private void PrintResultsTable(IReadOnlyList<ModeRunSummary> runs, BenchmarkDataset dataset)
    {
        var sb = new StringBuilder();
        void Line(string s) { _output.WriteLine(s); sb.AppendLine(s); }

        Line("=== Spectral Retrieval Benchmark ===");
        Line($"Corpus: {dataset.SeedEntries.Count} entries, {dataset.Queries.Count} queries (5 broad + 5 specific).");
        Line("");

        // Overall comparison
        Line("Overall (all queries):");
        Line(string.Format("  {0,-10} {1,-10} {2,-12} {3,-8} {4,-10}", "mode", "Recall@K", "Precision@K", "MRR", "nDCG@K"));
        foreach (var r in runs)
            Line(string.Format("  {0,-10} {1,-10:F3} {2,-12:F3} {3,-8:F3} {4,-10:F3}",
                r.Mode, r.Overall.Recall, r.Overall.Precision, r.Overall.Mrr, r.Overall.Ndcg));
        Line("");

        // Broad-query subset
        Line("Broad queries (5 short conceptual):");
        Line(string.Format("  {0,-10} {1,-10} {2,-12} {3,-8} {4,-10}", "mode", "Recall@K", "Precision@K", "MRR", "nDCG@K"));
        foreach (var r in runs)
            Line(string.Format("  {0,-10} {1,-10:F3} {2,-12:F3} {3,-8:F3} {4,-10:F3}",
                r.Mode, r.Broad.Recall, r.Broad.Precision, r.Broad.Mrr, r.Broad.Ndcg));
        Line("");

        // Specific-query subset
        Line("Specific queries (5 longer, precise):");
        Line(string.Format("  {0,-10} {1,-10} {2,-12} {3,-8} {4,-10}", "mode", "Recall@K", "Precision@K", "MRR", "nDCG@K"));
        foreach (var r in runs)
            Line(string.Format("  {0,-10} {1,-10:F3} {2,-12:F3} {3,-8:F3} {4,-10:F3}",
                r.Mode, r.Specific.Recall, r.Specific.Precision, r.Specific.Mrr, r.Specific.Ndcg));
        Line("");

        // Per-query detail under auto mode for transparency
        var autoRun = runs.First(r => r.Mode == "auto");
        Line("Per-query detail (mode=auto):");
        Line(string.Format("  {0,-25} {1,-10} {2,-10} {3,-8}", "query", "Recall@K", "Prec@K", "MRR"));
        foreach (var q in autoRun.PerQuery)
            Line(string.Format("  {0,-25} {1,-10:F3} {2,-10:F3} {3,-8:F3}",
                q.QueryId, q.RecallAtK, q.PrecisionAtK, q.MRR));

        // Also drop a copy at a known path so it can be inspected outside the
        // test runner. Path under TEMP keeps it discoverable but tidy.
        try
        {
            var outPath = Path.Combine(Path.GetTempPath(), "spectral_retrieval_benchmark_results.txt");
            File.WriteAllText(outPath, sb.ToString());
        }
        catch { /* best-effort; don't fail the test on a write error */ }
    }

    // ── synthetic corpus & queries ──

    private BenchmarkDataset BuildDataset()
    {
        var seeds = new List<BenchmarkSeedEntry>();

        // Topic 1: SIMD / vector throughput
        seeds.Add(new("simd-fp32-bottleneck", "FP32 dot product on AVX2 CPUs hits a memory-bandwidth ceiling."));
        seeds.Add(new("simd-int8-quant", "Int8 quantization triples vector dot-product throughput on modern CPUs."));
        seeds.Add(new("simd-vector-pool", "Allocating per-query pools for SIMD vectors reduces GC pressure on hot search paths."));
        seeds.Add(new("simd-avx512", "AVX-512 widens vector ops to 16 floats but only on Skylake-X and newer."));
        seeds.Add(new("simd-arm-neon", "ARM NEON gives portable 128-bit SIMD across Apple Silicon and AWS Graviton."));

        // Topic 2: Graph algorithms / Laplacian
        seeds.Add(new("graph-laplacian-basis", "The eigenbasis of the normalized graph Laplacian L=I-D^-1/2 W D^-1/2 is the spectral basis of the graph."));
        seeds.Add(new("graph-heat-kernel", "exp(-tL) is the heat kernel and acts as a smoother on graph signals."));
        seeds.Add(new("graph-chebyshev-poly", "Chebyshev polynomial approximation of spectral filters avoids full eigendecomposition."));
        seeds.Add(new("graph-spreading-activation", "Spreading activation in cognitive science is mathematically a graph diffusion operation."));
        seeds.Add(new("graph-fractal-dim", "Spectral dimension d_s of a graph determines how fast heat spreads on it."));

        // Topic 3: Memory lifecycle / decay
        seeds.Add(new("decay-stm-threshold", "The default StmThreshold is 2.0; below this, STM entries demote to LTM."));
        seeds.Add(new("decay-archive-floor", "ArchiveThreshold default is -5.0; below it, LTM entries are archived."));
        seeds.Add(new("decay-multipliers", "Per-state multipliers apply asymmetric decay: STM 3x, LTM 1x, archived 0.1x."));
        seeds.Add(new("decay-reinforcement-weight", "Each access multiplies the current activation by ReinforcementWeight (default 1.0)."));
        seeds.Add(new("decay-half-life", "STM half-life under default settings is roughly 3 hours."));

        // Topic 4: Embeddings / models
        seeds.Add(new("embed-bge-micro", "bge-micro-v2 is a 384-dimensional embedding model with 22M parameters."));
        seeds.Add(new("embed-onnx", "ONNX Runtime hosts the embedding model with CPU and GPU EPs."));
        seeds.Add(new("embed-tokenizer", "FastBertTokenizer produces wordpiece tokens for the BERT-family encoders."));
        seeds.Add(new("embed-warmup", "Embedding warmup runs at startup to avoid first-query latency spikes."));
        seeds.Add(new("embed-quantized-vector", "QuantizedVector stores Int8 vectors with min/scale for 4x memory reduction."));

        // Topic 5: Storage / persistence
        seeds.Add(new("storage-sqlite-wal", "SQLite WAL mode allows concurrent reads with a single writer."));
        seeds.Add(new("storage-debounce", "Debounced writes batch persistence updates to amortize disk cost."));
        seeds.Add(new("storage-snapshot", "Snapshot-based persistence rewrites the full namespace on each flush."));
        seeds.Add(new("storage-incremental", "Incremental writes update only changed entries, saving I/O on large namespaces."));
        seeds.Add(new("storage-hnsw-rebuild", "HNSW indexes are rebuilt on every server start; serialization is roadmap."));

        // Topic 6: Concurrency / locking
        seeds.Add(new("conc-rwlock", "ReaderWriterLockSlim allows many concurrent readers but exclusive writers."));
        seeds.Add(new("conc-per-namespace", "Per-namespace locks let unrelated namespaces compute independently."));
        seeds.Add(new("conc-deadlock-prevention", "Lock ordering hierarchies prevent deadlocks across nested locks."));
        seeds.Add(new("conc-interlocked", "Interlocked.Increment provides lock-free atomic counters."));
        seeds.Add(new("conc-cancellation", "CancellationToken propagation is required for graceful shutdown of background services."));

        // Topic 7: Testing / benchmarks
        seeds.Add(new("test-xunit", "xUnit Fact and Theory attributes mark unit tests and parameterized tests."));
        seeds.Add(new("test-benchmark-recall", "Recall@K measures how many relevant entries appear in the top-K results."));
        seeds.Add(new("test-benchmark-mrr", "Mean Reciprocal Rank measures how high the first relevant entry appears."));
        seeds.Add(new("test-mock-timeprovider", "TimeProvider abstraction lets tests control time without sleeping."));
        seeds.Add(new("test-deterministic-seed", "Deterministic RNG seeds make randomized algorithm tests reproducible."));

        // Topic 8: API / MCP tools
        seeds.Add(new("mcp-tool-registration", "MCP tools are registered via attributes and DI in the server bootstrap."));
        seeds.Add(new("mcp-stdio-transport", "Standard MCP transport is JSON-RPC over stdin/stdout."));
        seeds.Add(new("mcp-tool-description", "Each MCP tool needs a Description attribute for the LLM to understand its use."));
        seeds.Add(new("mcp-server-tool-type", "McpServerToolType marks a class as exposing MCP tools."));
        seeds.Add(new("mcp-tool-profile", "Tool profiles (minimal/standard/full) limit which tools are exposed."));

        // Queries: 5 broad + 5 specific
        var queries = new List<BenchmarkQuery>
        {
            // Broad: short, conceptual; expect cluster-style results
            new("broad-simd", "vector throughput",
                new() { ["simd-fp32-bottleneck"] = 3, ["simd-int8-quant"] = 3, ["simd-avx512"] = 2, ["simd-arm-neon"] = 2, ["simd-vector-pool"] = 1 },
                K: 5),
            new("broad-graph", "graph diffusion",
                new() { ["graph-heat-kernel"] = 3, ["graph-laplacian-basis"] = 3, ["graph-spreading-activation"] = 3, ["graph-chebyshev-poly"] = 2, ["graph-fractal-dim"] = 1 },
                K: 5),
            new("broad-decay", "memory decay",
                new() { ["decay-stm-threshold"] = 3, ["decay-archive-floor"] = 3, ["decay-multipliers"] = 3, ["decay-half-life"] = 2, ["decay-reinforcement-weight"] = 2 },
                K: 5),
            new("broad-embed", "embedding model",
                new() { ["embed-bge-micro"] = 3, ["embed-onnx"] = 3, ["embed-tokenizer"] = 2, ["embed-warmup"] = 2, ["embed-quantized-vector"] = 2 },
                K: 5),
            new("broad-storage", "persistence layer",
                new() { ["storage-sqlite-wal"] = 3, ["storage-debounce"] = 3, ["storage-snapshot"] = 3, ["storage-incremental"] = 3, ["storage-hnsw-rebuild"] = 2 },
                K: 5),

            // Specific: longer, precise; expect a single best match plus context
            new("spec-stm-threshold", "what is the default StmThreshold value below which STM demotes",
                new() { ["decay-stm-threshold"] = 3, ["decay-archive-floor"] = 1 },
                K: 3),
            new("spec-int8", "how much does Int8 quantization improve dot product throughput on CPUs",
                new() { ["simd-int8-quant"] = 3, ["simd-fp32-bottleneck"] = 1 },
                K: 3),
            new("spec-bge-dim", "what is the embedding dimension of bge-micro-v2 model",
                new() { ["embed-bge-micro"] = 3 },
                K: 3),
            new("spec-heat-kernel", "what does the matrix exponential exp(-tL) represent on a graph",
                new() { ["graph-heat-kernel"] = 3, ["graph-laplacian-basis"] = 1 },
                K: 3),
            new("spec-wal", "does SQLite WAL mode allow concurrent readers and writers",
                new() { ["storage-sqlite-wal"] = 3 },
                K: 3),
        };

        return new BenchmarkDataset("spectral-retrieval-v1", "Spectral retrieval validation", seeds, queries);
    }

    private void SeedDataset(string ns, BenchmarkDataset dataset)
    {
        foreach (var seed in dataset.SeedEntries)
        {
            var v = _embedding.Embed(seed.Text);
            _index.Upsert(new CognitiveEntry(seed.Id, v, ns, seed.Text, seed.Category, lifecycleState: "ltm"));
        }

        // Add intra-topic edges (the shape auto-link would have built).
        // 5 entries per topic, each topic prefix groups them.
        var topics = new[]
        {
            new[] { "simd-fp32-bottleneck", "simd-int8-quant", "simd-vector-pool", "simd-avx512", "simd-arm-neon" },
            new[] { "graph-laplacian-basis", "graph-heat-kernel", "graph-chebyshev-poly", "graph-spreading-activation", "graph-fractal-dim" },
            new[] { "decay-stm-threshold", "decay-archive-floor", "decay-multipliers", "decay-reinforcement-weight", "decay-half-life" },
            new[] { "embed-bge-micro", "embed-onnx", "embed-tokenizer", "embed-warmup", "embed-quantized-vector" },
            new[] { "storage-sqlite-wal", "storage-debounce", "storage-snapshot", "storage-incremental", "storage-hnsw-rebuild" },
            new[] { "conc-rwlock", "conc-per-namespace", "conc-deadlock-prevention", "conc-interlocked", "conc-cancellation" },
            new[] { "test-xunit", "test-benchmark-recall", "test-benchmark-mrr", "test-mock-timeprovider", "test-deterministic-seed" },
            new[] { "mcp-tool-registration", "mcp-stdio-transport", "mcp-tool-description", "mcp-server-tool-type", "mcp-tool-profile" },
        };
        foreach (var topic in topics)
            for (int i = 0; i < topic.Length; i++)
                for (int j = i + 1; j < topic.Length; j++)
                    _graph.AddEdge(new GraphEdge(topic[i], topic[j], "similar_to", 0.85f));
    }

    // ── metrics ──

    private static float Recall(IReadOnlyList<string> actual, HashSet<string> relevant, int k)
    {
        if (relevant.Count == 0) return 0f;
        int hit = 0;
        for (int i = 0; i < Math.Min(actual.Count, k); i++)
            if (relevant.Contains(actual[i])) hit++;
        return (float)hit / relevant.Count;
    }

    private static float Precision(IReadOnlyList<string> actual, HashSet<string> relevant, int k)
    {
        if (k == 0 || actual.Count == 0) return 0f;
        int hit = 0;
        int considered = Math.Min(actual.Count, k);
        for (int i = 0; i < considered; i++)
            if (relevant.Contains(actual[i])) hit++;
        return (float)hit / considered;
    }

    private static float Mrr(IReadOnlyList<string> actual, HashSet<string> relevant)
    {
        for (int i = 0; i < actual.Count; i++)
            if (relevant.Contains(actual[i])) return 1f / (i + 1);
        return 0f;
    }

    private static float Ndcg(IReadOnlyList<string> actual, IReadOnlyDictionary<string, int> grades, int k)
    {
        float dcg = 0f;
        int considered = Math.Min(actual.Count, k);
        for (int i = 0; i < considered; i++)
        {
            int grade = grades.TryGetValue(actual[i], out var g) ? g : 0;
            dcg += (MathF.Pow(2, grade) - 1) / MathF.Log2(i + 2);
        }
        var ideal = grades.Values.OrderByDescending(x => x).Take(k).ToList();
        float idcg = 0f;
        for (int i = 0; i < ideal.Count; i++)
            idcg += (MathF.Pow(2, ideal[i]) - 1) / MathF.Log2(i + 2);
        return idcg == 0 ? 0 : dcg / idcg;
    }

    private static ModeRunSummary Aggregate(string mode, List<QueryScore> perQuery, BenchmarkDataset dataset)
    {
        var broad = perQuery.Where(q => q.QueryId.StartsWith("broad-", StringComparison.Ordinal)).ToList();
        var spec = perQuery.Where(q => q.QueryId.StartsWith("spec-", StringComparison.Ordinal)).ToList();
        return new ModeRunSummary(
            mode,
            new MeanScores(perQuery.Average(q => q.RecallAtK), perQuery.Average(q => q.PrecisionAtK), perQuery.Average(q => q.MRR), perQuery.Average(q => q.NdcgAtK)),
            new MeanScores(broad.Average(q => q.RecallAtK), broad.Average(q => q.PrecisionAtK), broad.Average(q => q.MRR), broad.Average(q => q.NdcgAtK)),
            new MeanScores(spec.Average(q => q.RecallAtK), spec.Average(q => q.PrecisionAtK), spec.Average(q => q.MRR), spec.Average(q => q.NdcgAtK)),
            perQuery);
    }

    private record ModeRunSummary(string Mode, MeanScores Overall, MeanScores Broad, MeanScores Specific, IReadOnlyList<QueryScore> PerQuery);
    private record MeanScores(double Recall, double Precision, double Mrr, double Ndcg);
}
