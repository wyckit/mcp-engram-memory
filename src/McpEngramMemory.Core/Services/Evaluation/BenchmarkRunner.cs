using System.Diagnostics;
using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// Runs benchmark datasets against the cognitive index and computes IR quality metrics
/// (Recall@K, Precision@K, MRR, nDCG@K) with latency measurement.
/// </summary>
public sealed class BenchmarkRunner
{
    private readonly CognitiveIndex _index;
    private readonly IEmbeddingService _embedding;

    public BenchmarkRunner(CognitiveIndex index, IEmbeddingService embedding)
    {
        _index = index;
        _embedding = embedding;
    }

    /// <summary>
    /// Build a contextual prefix from namespace/category to prepend before embedding.
    /// Improves embedding specificity per Anthropic's contextual retrieval research.
    /// </summary>
    public static string BuildContextualPrefix(string? ns = null, string? category = null)
    {
        if (category is not null)
            return $"[{category}] ";
        if (ns is not null)
            return $"[{ns}] ";
        return "";
    }

    /// <summary>
    /// Run a benchmark dataset: ingest seed entries, execute queries, compute metrics, clean up.
    /// Uses an isolated namespace to avoid contaminating real data.
    /// </summary>
    public BenchmarkRunResult Run(BenchmarkDataset dataset, SearchMode mode = SearchMode.Vector,
        bool useContextualPrefix = false, string lifecycleState = "ltm")
    {
        if (dataset.Queries.Count == 0)
            return new BenchmarkRunResult(dataset.DatasetId, DateTimeOffset.UtcNow,
                Array.Empty<QueryScore>(), 0f, 0f, 0f, 0f, 0, 0,
                dataset.SeedEntries.Count, 0);

        string ns = $"__benchmark_{Guid.NewGuid():N}";
        try
        {
            // 1. Ingest seed entries
            foreach (var seed in dataset.SeedEntries)
            {
                var textToEmbed = useContextualPrefix
                    ? BuildContextualPrefix(category: seed.Category) + seed.Text
                    : seed.Text;
                var vector = _embedding.Embed(textToEmbed);
                var entry = new CognitiveEntry(seed.Id, vector, ns, seed.Text, seed.Category, lifecycleState: lifecycleState);
                _index.Upsert(entry);
            }

            // 2. Run queries and score
            var scores = new List<QueryScore>(dataset.Queries.Count);
            foreach (var query in dataset.Queries)
            {
                // Embed separately so latency measures search only
                var queryVector = _embedding.Embed(query.QueryText);

                var sw = Stopwatch.StartNew();
                IReadOnlyList<CognitiveSearchResult> results = mode switch
                {
                    SearchMode.Hybrid => _index.HybridSearch(
                        queryVector, query.QueryText, ns, query.K),
                    SearchMode.HybridRerank => _index.HybridSearch(
                        queryVector, query.QueryText, ns, query.K, rerank: true),
                    SearchMode.VectorRerank => _index.Rerank(
                        query.QueryText, _index.Search(queryVector, ns, query.K * 2))
                        .Take(query.K).ToList(),
                    _ => _index.Search(queryVector, ns, query.K)
                };
                sw.Stop();

                var actualIds = results.Select(r => r.Id).ToList();
                var relevantIds = query.RelevanceGrades.Keys.ToHashSet();

                float recallAtK = ComputeRecallAtK(actualIds, relevantIds, query.K);
                float precisionAtK = ComputePrecisionAtK(actualIds, relevantIds, query.K);
                float mrr = ComputeMRR(actualIds, relevantIds);
                float ndcg = ComputeNdcgAtK(actualIds, query.RelevanceGrades, query.K);

                scores.Add(new QueryScore(
                    query.QueryId, recallAtK, precisionAtK, mrr, ndcg,
                    sw.Elapsed.TotalMilliseconds, actualIds));
            }

            // 3. Aggregate
            var latencies = scores.Select(s => s.LatencyMs).OrderBy(x => x).ToList();

            return new BenchmarkRunResult(
                dataset.DatasetId,
                DateTimeOffset.UtcNow,
                scores,
                scores.Average(s => s.RecallAtK),
                scores.Average(s => s.PrecisionAtK),
                scores.Average(s => s.MRR),
                scores.Average(s => s.NdcgAtK),
                latencies.Average(),
                MetricsCollector.Percentile(latencies, 0.95),
                dataset.SeedEntries.Count,
                dataset.Queries.Count);
        }
        finally
        {
            // 4. Cleanup: delete by namespace-scoped lookup to avoid touching real data
            foreach (var entry in _index.GetAllInNamespace(ns))
                _index.Delete(entry.Id);
        }
    }

    /// <summary>
    /// Run ablation study: baseline (vector-only, quantized) vs. each search mode and
    /// a no-quantization variant. Returns deltas showing each component's contribution.
    /// Pre-computes embeddings once and reuses across all 5 passes.
    /// </summary>
    public AblationResult RunAblation(BenchmarkDataset dataset)
    {
        // Pre-compute all embeddings once (5× speedup vs. re-embedding per pass)
        var seedVectors = dataset.SeedEntries
            .Select(s => _embedding.Embed(s.Text))
            .ToList();
        var queryVectors = dataset.Queries
            .Select(q => _embedding.Embed(q.QueryText))
            .ToList();

        var baseline = RunWithVectors(dataset, seedVectors, queryVectors, SearchMode.Vector, "ltm");

        var ablations = new (string Label, SearchMode Mode, string Lifecycle)[]
        {
            ("Hybrid", SearchMode.Hybrid, "ltm"),
            ("VectorRerank", SearchMode.VectorRerank, "ltm"),
            ("HybridRerank", SearchMode.HybridRerank, "ltm"),
            ("VectorNoQuantization", SearchMode.Vector, "stm"),
        };

        var comparisons = new List<AblationComparison>(ablations.Length);
        foreach (var (label, mode, lifecycle) in ablations)
        {
            var result = RunWithVectors(dataset, seedVectors, queryVectors, mode, lifecycle);
            comparisons.Add(new AblationComparison(
                label, result,
                result.MeanRecallAtK - baseline.MeanRecallAtK,
                result.MeanPrecisionAtK - baseline.MeanPrecisionAtK,
                result.MeanMRR - baseline.MeanMRR,
                result.MeanNdcgAtK - baseline.MeanNdcgAtK,
                result.MeanLatencyMs - baseline.MeanLatencyMs));
        }

        return new AblationResult(dataset.DatasetId, baseline, comparisons);
    }

    /// <summary>Run a benchmark pass with pre-computed vectors to avoid redundant embedding.</summary>
    private BenchmarkRunResult RunWithVectors(BenchmarkDataset dataset,
        IReadOnlyList<float[]> seedVectors, IReadOnlyList<float[]> queryVectors,
        SearchMode mode, string lifecycleState)
    {
        if (dataset.Queries.Count == 0)
            return new BenchmarkRunResult(dataset.DatasetId, DateTimeOffset.UtcNow,
                Array.Empty<QueryScore>(), 0f, 0f, 0f, 0f, 0, 0,
                dataset.SeedEntries.Count, 0);

        string ns = $"__benchmark_{Guid.NewGuid():N}";
        try
        {
            for (int i = 0; i < dataset.SeedEntries.Count; i++)
            {
                var seed = dataset.SeedEntries[i];
                var entry = new CognitiveEntry(seed.Id, seedVectors[i], ns, seed.Text, seed.Category, lifecycleState: lifecycleState);
                _index.Upsert(entry);
            }

            var scores = new List<QueryScore>(dataset.Queries.Count);
            for (int i = 0; i < dataset.Queries.Count; i++)
            {
                var query = dataset.Queries[i];
                var queryVector = queryVectors[i];

                var sw = Stopwatch.StartNew();
                IReadOnlyList<CognitiveSearchResult> results = mode switch
                {
                    SearchMode.Hybrid => _index.HybridSearch(queryVector, query.QueryText, ns, query.K),
                    SearchMode.HybridRerank => _index.HybridSearch(queryVector, query.QueryText, ns, query.K, rerank: true),
                    SearchMode.VectorRerank => _index.Rerank(query.QueryText, _index.Search(queryVector, ns, query.K * 2))
                        .Take(query.K).ToList(),
                    _ => _index.Search(queryVector, ns, query.K)
                };
                sw.Stop();

                var actualIds = results.Select(r => r.Id).ToList();
                var relevantIds = query.RelevanceGrades.Keys.ToHashSet();

                scores.Add(new QueryScore(
                    query.QueryId,
                    ComputeRecallAtK(actualIds, relevantIds, query.K),
                    ComputePrecisionAtK(actualIds, relevantIds, query.K),
                    ComputeMRR(actualIds, relevantIds),
                    ComputeNdcgAtK(actualIds, query.RelevanceGrades, query.K),
                    sw.Elapsed.TotalMilliseconds, actualIds));
            }

            var latencies = scores.Select(s => s.LatencyMs).OrderBy(x => x).ToList();
            return new BenchmarkRunResult(dataset.DatasetId, DateTimeOffset.UtcNow, scores,
                scores.Average(s => s.RecallAtK), scores.Average(s => s.PrecisionAtK),
                scores.Average(s => s.MRR), scores.Average(s => s.NdcgAtK),
                latencies.Average(), MetricsCollector.Percentile(latencies, 0.95),
                dataset.SeedEntries.Count, dataset.Queries.Count);
        }
        finally
        {
            foreach (var entry in _index.GetAllInNamespace(ns))
                _index.Delete(entry.Id);
        }
    }

    /// <summary>Search modes for benchmarking.</summary>
    public enum SearchMode
    {
        /// <summary>Pure vector cosine similarity (baseline).</summary>
        Vector,
        /// <summary>Hybrid BM25 + vector with RRF fusion.</summary>
        Hybrid,
        /// <summary>Vector search with token-level reranking.</summary>
        VectorRerank,
        /// <summary>Hybrid search with RRF fusion + token-level reranking.</summary>
        HybridRerank
    }

    // ── IR Quality Metrics ──

    /// <summary>Recall@K = |relevant ∩ retrieved@K| / |relevant|</summary>
    public static float ComputeRecallAtK(IReadOnlyList<string> retrievedIds, IReadOnlyCollection<string> relevantIds, int k = int.MaxValue)
    {
        if (relevantIds.Count == 0) return 1f;
        int hits = retrievedIds.Take(k).Count(id => relevantIds.Contains(id));
        return (float)hits / relevantIds.Count;
    }

    /// <summary>Precision@K = |relevant ∩ retrieved| / K</summary>
    public static float ComputePrecisionAtK(IReadOnlyList<string> retrievedIds, IReadOnlyCollection<string> relevantIds, int k)
    {
        if (k <= 0) return 0f;
        int hits = retrievedIds.Take(k).Count(id => relevantIds.Contains(id));
        return (float)hits / k;
    }

    /// <summary>MRR = 1 / rank of first relevant result (0 if none found)</summary>
    public static float ComputeMRR(IReadOnlyList<string> retrievedIds, IReadOnlyCollection<string> relevantIds)
    {
        for (int i = 0; i < retrievedIds.Count; i++)
        {
            if (relevantIds.Contains(retrievedIds[i]))
                return 1f / (i + 1);
        }
        return 0f;
    }

    /// <summary>nDCG@K = DCG@K / IDCG@K</summary>
    public static float ComputeNdcgAtK(IReadOnlyList<string> retrievedIds, Dictionary<string, int> relevanceGrades, int k)
    {
        double dcg = ComputeDcg(retrievedIds.Take(k).ToList(), relevanceGrades);

        var idealOrder = relevanceGrades
            .OrderByDescending(kv => kv.Value)
            .Take(k)
            .Select(kv => kv.Key)
            .ToList();
        double idcg = ComputeDcg(idealOrder, relevanceGrades);

        if (idcg == 0) return 0f;
        return (float)(dcg / idcg);
    }

    private static double ComputeDcg(IReadOnlyList<string> rankedIds, Dictionary<string, int> relevanceGrades)
    {
        double dcg = 0;
        for (int i = 0; i < rankedIds.Count; i++)
        {
            int rel = relevanceGrades.GetValueOrDefault(rankedIds[i], 0);
            dcg += (Math.Pow(2, rel) - 1) / Math.Log2(i + 2);
        }
        return dcg;
    }

    // ── Default Benchmark Dataset ──

    /// <summary>
    /// Creates the built-in benchmark dataset with 25 seed entries and 20 queries
    /// covering programming languages, data structures, ML, databases, networking, and systems.
    /// </summary>
    public static BenchmarkDataset CreateDefaultDataset()
    {
        var seeds = new List<BenchmarkSeedEntry>
        {
            new("bench-python", "Python is a high-level, interpreted programming language known for its simplicity and readability. It supports multiple paradigms including procedural, object-oriented, and functional programming."),
            new("bench-rust", "Rust is a systems programming language focused on safety, concurrency, and performance. It prevents memory errors through its ownership system without needing a garbage collector."),
            new("bench-javascript", "JavaScript is a dynamic scripting language primarily used for web development. It runs in browsers and on servers via Node.js, supporting event-driven and asynchronous programming."),
            new("bench-csharp", "C# is a modern, object-oriented programming language developed by Microsoft. It runs on the .NET platform and is used for web, desktop, mobile, and game development."),
            new("bench-hashtable", "A hash table is a data structure that maps keys to values using a hash function. It provides O(1) average-case lookup, insertion, and deletion, making it ideal for key-value storage."),
            new("bench-binarytree", "A binary tree is a hierarchical data structure where each node has at most two children. Binary search trees maintain sorted order, enabling O(log n) search, insertion, and deletion."),
            new("bench-linkedlist", "A linked list is a linear data structure where elements are stored in nodes connected by pointers. It allows O(1) insertion and deletion at known positions but O(n) random access."),
            new("bench-graph", "A graph is a data structure consisting of vertices connected by edges. Graphs can be directed or undirected and are used to model networks, relationships, and dependencies."),
            new("bench-neuralnet", "Neural networks are computing systems inspired by biological brain networks. They consist of layers of interconnected nodes that learn patterns from data through backpropagation."),
            new("bench-gradientdescent", "Gradient descent is an optimization algorithm that iteratively adjusts parameters to minimize a loss function. Variants include stochastic gradient descent, Adam, and RMSProp."),
            new("bench-transformer", "The Transformer architecture uses self-attention mechanisms to process sequential data in parallel. It forms the basis of modern language models like GPT, BERT, and T5."),
            new("bench-sql", "SQL databases use structured query language for data manipulation in relational tables. They enforce ACID properties: Atomicity, Consistency, Isolation, and Durability."),
            new("bench-nosql", "NoSQL databases provide flexible schemas and horizontal scaling for unstructured data. Types include document stores like MongoDB, key-value stores like Redis, and graph databases like Neo4j."),
            new("bench-tcpip", "TCP/IP is the fundamental protocol suite of the internet. TCP provides reliable, ordered delivery of data streams, while IP handles addressing and routing packets across networks."),
            new("bench-http", "HTTP is the application-layer protocol for transmitting hypermedia documents on the web. HTTP/2 adds multiplexing and header compression, while HTTP/3 uses QUIC over UDP."),
            new("bench-gc", "Garbage collection automatically reclaims memory that is no longer referenced by a program. Common algorithms include mark-and-sweep, generational collection, and reference counting."),
            new("bench-mutex", "Mutexes and semaphores are synchronization primitives for managing concurrent access to shared resources. A mutex provides exclusive access while a semaphore can allow multiple concurrent accessors."),
            new("bench-docker", "Docker containers package applications with their dependencies into isolated, portable units. Containers share the host OS kernel, making them lighter than virtual machines."),
            new("bench-restapi", "REST APIs use HTTP methods like GET, POST, PUT, and DELETE to perform CRUD operations on resources identified by URLs. RESTful design emphasizes statelessness and uniform interfaces."),
            new("bench-vectorsearch", "Vector similarity search finds the nearest neighbors to a query vector in high-dimensional space. Common metrics include cosine similarity, Euclidean distance, and dot product."),
            new("bench-embeddings", "Embedding models convert text, images, or other data into dense vector representations. These vectors capture semantic meaning, enabling similarity search and clustering."),
            new("bench-cosine", "Cosine similarity measures the angle between two vectors, ranging from -1 to 1. A score of 1 indicates identical direction. It is commonly used for text similarity in NLP."),
            new("bench-memmanage", "Memory management involves allocating and freeing memory during program execution. Techniques include stack allocation, heap allocation, memory pools, and arena allocators."),
            new("bench-sorting", "Sorting algorithms arrange elements in order. Common algorithms include quicksort with O(n log n) average case, mergesort which is stable, and heapsort which is in-place."),
            new("bench-concurrency", "Concurrency patterns manage parallel execution safely. Common patterns include producer-consumer, thread pools, actor model, and async/await for non-blocking I/O.")
        };

        var queries = new List<BenchmarkQuery>
        {
            new("q01", "What programming language is best for beginners?",
                new() { ["bench-python"] = 3, ["bench-javascript"] = 2, ["bench-csharp"] = 1 }),
            new("q02", "Systems programming with memory safety",
                new() { ["bench-rust"] = 3, ["bench-csharp"] = 1, ["bench-memmanage"] = 1 }),
            new("q03", "Fast key-value data storage",
                new() { ["bench-hashtable"] = 3, ["bench-nosql"] = 2, ["bench-sql"] = 1 }),
            new("q04", "How do trees work in computer science?",
                new() { ["bench-binarytree"] = 3, ["bench-graph"] = 1 }),
            new("q05", "Deep learning model architecture",
                new() { ["bench-neuralnet"] = 3, ["bench-transformer"] = 3, ["bench-gradientdescent"] = 2 }),
            new("q06", "How to store data in a relational database",
                new() { ["bench-sql"] = 3, ["bench-nosql"] = 1 }),
            new("q07", "Network communication protocols",
                new() { ["bench-tcpip"] = 3, ["bench-http"] = 2, ["bench-restapi"] = 1 }),
            new("q08", "Automatic memory cleanup in programming",
                new() { ["bench-gc"] = 3, ["bench-memmanage"] = 2, ["bench-rust"] = 1 }),
            new("q09", "Thread synchronization and locking",
                new() { ["bench-mutex"] = 3, ["bench-concurrency"] = 2 }),
            new("q10", "Containerization and deployment",
                new() { ["bench-docker"] = 3 }),
            new("q11", "Building web APIs",
                new() { ["bench-restapi"] = 3, ["bench-http"] = 2, ["bench-javascript"] = 1 }),
            new("q12", "Semantic search and embeddings",
                new() { ["bench-vectorsearch"] = 3, ["bench-embeddings"] = 3, ["bench-cosine"] = 2 }),
            new("q13", "How backpropagation trains neural networks",
                new() { ["bench-neuralnet"] = 3, ["bench-gradientdescent"] = 3 }),
            new("q14", "Efficient sorting of large datasets",
                new() { ["bench-sorting"] = 3, ["bench-hashtable"] = 1 }),
            new("q15", "Linked vs array-based data structures",
                new() { ["bench-linkedlist"] = 3, ["bench-sorting"] = 1 }),
            new("q16", "Graph traversal and network analysis",
                new() { ["bench-graph"] = 3, ["bench-binarytree"] = 1 }),
            new("q17", "Attention mechanisms in NLP",
                new() { ["bench-transformer"] = 3, ["bench-neuralnet"] = 2, ["bench-embeddings"] = 1 }),
            new("q18", "NoSQL vs SQL database tradeoffs",
                new() { ["bench-nosql"] = 3, ["bench-sql"] = 3 }),
            new("q19", "Web development with JavaScript",
                new() { ["bench-javascript"] = 3, ["bench-restapi"] = 1, ["bench-http"] = 1 }),
            new("q20", "Measuring vector distance and similarity",
                new() { ["bench-cosine"] = 3, ["bench-vectorsearch"] = 2, ["bench-embeddings"] = 1 })
        };

        return new BenchmarkDataset("default-v1", "Default IR Quality Benchmark", seeds, queries);
    }

    /// <summary>
    /// Paraphrase Robustness benchmark: same 25 seeds, 15 queries that heavily rephrase seed content.
    /// Tests whether the embedding model understands semantic meaning beyond lexical overlap.
    /// </summary>
    public static BenchmarkDataset CreateParaphraseDataset()
    {
        // Reuse the default seeds — the challenge is in the query wording
        var seeds = CreateDefaultDataset().SeedEntries;

        var queries = new List<BenchmarkQuery>
        {
            new("p01", "The language Microsoft created that targets the dotnet runtime",
                new() { ["bench-csharp"] = 3, ["bench-python"] = 0 }),
            new("p02", "A language that prevents dangling pointers without a GC through borrow checking",
                new() { ["bench-rust"] = 3, ["bench-gc"] = 1 }),
            new("p03", "The scripting language that powers interactive websites and runs on V8",
                new() { ["bench-javascript"] = 3, ["bench-http"] = 1 }),
            new("p04", "An easy-to-read language popular in data science and machine learning",
                new() { ["bench-python"] = 3, ["bench-neuralnet"] = 1 }),
            new("p05", "A lookup structure that converts keys into array indices via hashing",
                new() { ["bench-hashtable"] = 3 }),
            new("p06", "Hierarchical nodes where each parent has a left child and a right child",
                new() { ["bench-binarytree"] = 3, ["bench-graph"] = 1 }),
            new("p07", "Nodes chained together with next-pointers for sequential access",
                new() { ["bench-linkedlist"] = 3 }),
            new("p08", "The architecture behind GPT and BERT that processes tokens in parallel with attention",
                new() { ["bench-transformer"] = 3, ["bench-neuralnet"] = 2 }),
            new("p09", "An iterative optimizer that follows the steepest slope downhill to find a minimum",
                new() { ["bench-gradientdescent"] = 3, ["bench-neuralnet"] = 1 }),
            new("p10", "Tables with rows and columns queried using SELECT, JOIN, and WHERE clauses",
                new() { ["bench-sql"] = 3, ["bench-nosql"] = 0 }),
            new("p11", "Schema-free databases like Mongo and Redis that scale out horizontally",
                new() { ["bench-nosql"] = 3, ["bench-sql"] = 1 }),
            new("p12", "The reliable transport layer that guarantees ordered byte stream delivery over IP",
                new() { ["bench-tcpip"] = 3, ["bench-http"] = 1 }),
            new("p13", "Lightweight OS-level virtualization that bundles apps with their dependencies",
                new() { ["bench-docker"] = 3 }),
            new("p14", "Converting words and sentences into dense floating-point arrays that capture meaning",
                new() { ["bench-embeddings"] = 3, ["bench-vectorsearch"] = 2, ["bench-cosine"] = 1 }),
            new("p15", "Finding the closest points in high-dimensional feature space using angular distance",
                new() { ["bench-vectorsearch"] = 3, ["bench-cosine"] = 3, ["bench-embeddings"] = 2 })
        };

        return new BenchmarkDataset("paraphrase-v1", "Paraphrase Robustness Benchmark", seeds.ToList(), queries);
    }

    /// <summary>
    /// Multi-Hop Reasoning benchmark: queries that span two or more topics,
    /// requiring multiple relevant seeds to surface together.
    /// </summary>
    public static BenchmarkDataset CreateMultiHopDataset()
    {
        var seeds = CreateDefaultDataset().SeedEntries;

        var queries = new List<BenchmarkQuery>
        {
            new("m01", "Building a high-performance web server in Rust",
                new() { ["bench-rust"] = 3, ["bench-http"] = 2, ["bench-restapi"] = 2 }),
            new("m02", "Using Python to train a transformer model",
                new() { ["bench-python"] = 3, ["bench-transformer"] = 3, ["bench-neuralnet"] = 2, ["bench-gradientdescent"] = 1 }),
            new("m03", "Containerized microservices communicating over REST APIs",
                new() { ["bench-docker"] = 3, ["bench-restapi"] = 3, ["bench-http"] = 1 }),
            new("m04", "Storing graph relationships in a NoSQL database",
                new() { ["bench-graph"] = 3, ["bench-nosql"] = 3, ["bench-sql"] = 1 }),
            new("m05", "Thread-safe concurrent access to a hash table",
                new() { ["bench-mutex"] = 3, ["bench-hashtable"] = 3, ["bench-concurrency"] = 2 }),
            new("m06", "Using vector embeddings for semantic search in a SQL database",
                new() { ["bench-embeddings"] = 3, ["bench-vectorsearch"] = 3, ["bench-sql"] = 2 }),
            new("m07", "Garbage collection strategies for linked list memory reclamation",
                new() { ["bench-gc"] = 3, ["bench-linkedlist"] = 2, ["bench-memmanage"] = 2 }),
            new("m08", "Sorting algorithms implemented in C# using async patterns",
                new() { ["bench-sorting"] = 3, ["bench-csharp"] = 2, ["bench-concurrency"] = 2 }),
            new("m09", "Binary search tree indexes for faster SQL query performance",
                new() { ["bench-binarytree"] = 3, ["bench-sql"] = 3, ["bench-sorting"] = 1 }),
            new("m10", "Real-time neural network inference served over HTTP",
                new() { ["bench-neuralnet"] = 3, ["bench-http"] = 2, ["bench-restapi"] = 2 }),
            new("m11", "Cosine similarity for deduplicating records in a document store",
                new() { ["bench-cosine"] = 3, ["bench-nosql"] = 2, ["bench-vectorsearch"] = 2 }),
            new("m12", "Memory-safe concurrency without garbage collection overhead",
                new() { ["bench-rust"] = 3, ["bench-concurrency"] = 3, ["bench-gc"] = 2, ["bench-memmanage"] = 1 }),
            new("m13", "JavaScript event loop and asynchronous HTTP request handling",
                new() { ["bench-javascript"] = 3, ["bench-http"] = 2, ["bench-concurrency"] = 2 }),
            new("m14", "Gradient descent optimization for training embedding models",
                new() { ["bench-gradientdescent"] = 3, ["bench-embeddings"] = 3, ["bench-neuralnet"] = 2 }),
            new("m15", "Graph-based knowledge representation with vector similarity search",
                new() { ["bench-graph"] = 3, ["bench-vectorsearch"] = 3, ["bench-embeddings"] = 2 })
        };

        return new BenchmarkDataset("multihop-v1", "Multi-Hop Reasoning Benchmark", seeds.ToList(), queries);
    }

    /// <summary>
    /// Scale stress test: 80 seed entries across 8 categories with 30 queries.
    /// Tests metric and latency degradation at 3.2x the default corpus size.
    /// </summary>
    public static BenchmarkDataset CreateScaleDataset()
    {
        var seeds = new List<BenchmarkSeedEntry>
        {
            // Programming Languages (10)
            new("s-python", "Python is an interpreted, dynamically typed language with a focus on readability. Popular for web backends, data analysis, scripting, and machine learning applications.", "languages"),
            new("s-rust", "Rust guarantees memory safety through ownership and borrowing without a garbage collector. It targets systems programming, embedded devices, and WebAssembly.", "languages"),
            new("s-javascript", "JavaScript is the language of the web browser. With Node.js it also runs on servers. It uses prototypal inheritance and a single-threaded event loop.", "languages"),
            new("s-csharp", "C# is a strongly typed, object-oriented language on the .NET platform. It supports LINQ, async/await, pattern matching, and runs cross-platform via .NET Core.", "languages"),
            new("s-java", "Java is a compiled, object-oriented language that runs on the JVM. It emphasizes write-once-run-anywhere portability and has a large enterprise ecosystem.", "languages"),
            new("s-go", "Go (Golang) is a statically typed language by Google with built-in concurrency via goroutines and channels. It compiles to native code and has a fast compiler.", "languages"),
            new("s-typescript", "TypeScript adds static type checking to JavaScript. It compiles to plain JS and supports interfaces, generics, union types, and decorators.", "languages"),
            new("s-cpp", "C++ is a high-performance systems language with manual memory management. It supports templates, RAII, move semantics, and multiple paradigms.", "languages"),
            new("s-kotlin", "Kotlin is a JVM language by JetBrains, fully interoperable with Java. It features null safety, coroutines, data classes, and extension functions.", "languages"),
            new("s-swift", "Swift is Apple's language for iOS, macOS, and server-side development. It uses automatic reference counting, optionals, and protocol-oriented programming.", "languages"),

            // Data Structures (10)
            new("s-array", "An array stores elements in contiguous memory locations, enabling O(1) random access by index. Fixed-size arrays are stack-allocated; dynamic arrays resize via amortized doubling.", "data-structures"),
            new("s-hashtable", "A hash table uses a hash function to map keys to bucket indices. Open addressing and chaining handle collisions. Average O(1) lookup, worst case O(n).", "data-structures"),
            new("s-bst", "A binary search tree orders nodes so that left children are smaller and right children are larger. Balanced variants (AVL, red-black) guarantee O(log n) operations.", "data-structures"),
            new("s-heap", "A heap is a complete binary tree satisfying the heap property: each parent is smaller (min-heap) or larger (max-heap) than its children. Used for priority queues.", "data-structures"),
            new("s-trie", "A trie (prefix tree) stores strings character by character in a tree structure. It supports O(m) lookup where m is key length and enables prefix-based autocompletion.", "data-structures"),
            new("s-graph", "A graph consists of vertices and edges representing relationships. Representations include adjacency lists and adjacency matrices. Used for social networks, maps, and dependencies.", "data-structures"),
            new("s-linkedlist", "A linked list chains nodes via pointers. Singly linked lists traverse forward; doubly linked lists support bidirectional traversal. O(1) insertion at head.", "data-structures"),
            new("s-stack", "A stack is a LIFO data structure supporting push and pop operations in O(1). Used for function call tracking, expression evaluation, and backtracking algorithms.", "data-structures"),
            new("s-queue", "A queue is a FIFO data structure supporting enqueue and dequeue in O(1). Variants include circular queues, deques, and priority queues.", "data-structures"),
            new("s-bloomfilter", "A Bloom filter is a probabilistic data structure that tests set membership. It can return false positives but never false negatives, using multiple hash functions.", "data-structures"),

            // Machine Learning (10)
            new("s-neuralnet", "Artificial neural networks consist of layers of neurons connected by weighted edges. Forward propagation computes output; backpropagation adjusts weights using the chain rule.", "ml"),
            new("s-transformer", "Transformers use multi-head self-attention to weigh all positions in a sequence simultaneously. They power BERT, GPT, T5, and most modern NLP models.", "ml"),
            new("s-cnn", "Convolutional neural networks apply learnable filters over spatial data. They excel at image classification, object detection, and video analysis tasks.", "ml"),
            new("s-rnn", "Recurrent neural networks process sequential data by maintaining hidden state across time steps. LSTMs and GRUs address the vanishing gradient problem.", "ml"),
            new("s-reinforcement", "Reinforcement learning trains agents by rewarding desired actions in an environment. Key algorithms include Q-learning, policy gradients, and actor-critic methods.", "ml"),
            new("s-gradient", "Gradient descent minimizes a loss function by iteratively stepping in the direction of steepest descent. SGD, Adam, and AdaGrad are popular optimizers.", "ml"),
            new("s-embeddings", "Embeddings map discrete items (words, products, users) to continuous vector spaces. Word2Vec, GloVe, and sentence transformers are common approaches.", "ml"),
            new("s-regularization", "Regularization prevents overfitting by penalizing model complexity. Techniques include L1/L2 penalties, dropout, early stopping, and data augmentation.", "ml"),
            new("s-clustering", "Clustering groups similar data points without labels. K-means partitions by centroid distance; DBSCAN finds density-connected regions; hierarchical methods build dendrograms.", "ml"),
            new("s-transfer", "Transfer learning reuses a model trained on one task for a different task. Fine-tuning pretrained language models like BERT is a common example.", "ml"),

            // Databases (10)
            new("s-sql", "Relational databases store data in tables with schemas, enforcing ACID transactions. SQL provides SELECT, JOIN, GROUP BY, and subqueries for data manipulation.", "databases"),
            new("s-nosql", "NoSQL databases sacrifice strict consistency for scalability. Categories include document stores (MongoDB), key-value (Redis), column-family (Cassandra), and graph (Neo4j).", "databases"),
            new("s-indexing", "Database indexes (B-tree, hash, bitmap) speed up queries by creating sorted lookup structures. Composite indexes cover multiple columns; partial indexes filter subsets.", "databases"),
            new("s-transactions", "Database transactions group operations atomically. Isolation levels (read uncommitted, read committed, repeatable read, serializable) trade consistency for concurrency.", "databases"),
            new("s-replication", "Database replication copies data across nodes for availability and read scaling. Strategies include leader-follower, multi-leader, and leaderless (quorum-based) replication.", "databases"),
            new("s-sharding", "Sharding partitions data across multiple database servers by a shard key. It enables horizontal scaling but complicates cross-shard queries and transactions.", "databases"),
            new("s-caching", "Caching layers (Redis, Memcached) store frequently accessed data in memory. Strategies include write-through, write-behind, and cache-aside with TTL-based expiration.", "databases"),
            new("s-orm", "Object-relational mapping (ORM) translates between programming objects and database tables. Entity Framework, Hibernate, and SQLAlchemy are popular ORMs.", "databases"),
            new("s-timeseries", "Time-series databases (InfluxDB, TimescaleDB) optimize for timestamped data. They support downsampling, retention policies, and continuous aggregation queries.", "databases"),
            new("s-vectordb", "Vector databases (Pinecone, Weaviate, Milvus) store and index high-dimensional vectors for similarity search. They support ANN algorithms like HNSW and IVF.", "databases"),

            // Networking (10)
            new("s-tcp", "TCP provides reliable, ordered, connection-oriented byte stream delivery. It uses three-way handshake, flow control (sliding window), and congestion control (slow start, AIMD).", "networking"),
            new("s-udp", "UDP is a connectionless, lightweight transport protocol. It provides no delivery guarantees but has lower latency, making it suitable for gaming, streaming, and DNS.", "networking"),
            new("s-http", "HTTP is a request-response protocol for web communication. HTTP/2 introduces multiplexing and server push; HTTP/3 replaces TCP with QUIC for faster connections.", "networking"),
            new("s-dns", "The Domain Name System translates human-readable domain names to IP addresses. It uses a hierarchical distributed database with recursive and iterative resolution.", "networking"),
            new("s-tls", "TLS (Transport Layer Security) encrypts network communication. It uses asymmetric cryptography for key exchange and symmetric ciphers for data encryption.", "networking"),
            new("s-websocket", "WebSockets provide full-duplex communication channels over a single TCP connection. They enable real-time features like chat, live updates, and collaborative editing.", "networking"),
            new("s-grpc", "gRPC is a high-performance RPC framework using Protocol Buffers for serialization and HTTP/2 for transport. It supports streaming, load balancing, and service discovery.", "networking"),
            new("s-rest", "RESTful APIs use HTTP verbs (GET, POST, PUT, DELETE) on resource URLs. They are stateless, cacheable, and follow a uniform interface constraint.", "networking"),
            new("s-graphql", "GraphQL is a query language for APIs that lets clients request exactly the data they need. It uses a typed schema and resolvers to fulfill queries.", "networking"),
            new("s-loadbalancer", "Load balancers distribute incoming requests across backend servers. Algorithms include round-robin, least connections, and consistent hashing for session affinity.", "networking"),

            // Systems & Infrastructure (10)
            new("s-gc", "Garbage collectors automatically free unreachable memory. Generational GC exploits the weak generational hypothesis; concurrent collectors minimize pause times.", "systems"),
            new("s-containers", "Containers (Docker, Podman) isolate applications using OS-level namespaces and cgroups. They are lighter than VMs and enable reproducible deployments.", "systems"),
            new("s-kubernetes", "Kubernetes orchestrates containerized workloads across clusters. It manages scheduling, scaling, service discovery, rolling updates, and self-healing.", "systems"),
            new("s-cicd", "CI/CD pipelines automate building, testing, and deploying code. Tools like GitHub Actions, Jenkins, and GitLab CI run on every commit or merge.", "systems"),
            new("s-mutex", "Mutexes provide mutual exclusion for critical sections. Reader-writer locks allow concurrent reads. Deadlock prevention requires ordering lock acquisition.", "systems"),
            new("s-async", "Async/await enables non-blocking I/O by suspending execution until a result is ready. It avoids thread-per-request overhead in high-concurrency servers.", "systems"),
            new("s-virtualization", "Virtual machines emulate complete hardware environments via hypervisors (Type 1: bare-metal, Type 2: hosted). They provide strong isolation but higher overhead than containers.", "systems"),
            new("s-monitoring", "Observability combines metrics (Prometheus), logs (ELK stack), and traces (Jaeger/Zipkin). Alerting rules trigger notifications when SLOs are breached.", "systems"),
            new("s-messagequeue", "Message queues (Kafka, RabbitMQ, SQS) decouple producers and consumers. They provide buffering, at-least-once delivery, and horizontal scalability.", "systems"),
            new("s-filesystem", "File systems organize data on storage devices. Common types include ext4, NTFS, and ZFS. They manage inodes, journaling, caching, and access permissions.", "systems"),

            // Security (10)
            new("s-encryption", "Encryption transforms plaintext into ciphertext using algorithms and keys. AES is the standard symmetric cipher; RSA and ECC are asymmetric algorithms.", "security"),
            new("s-auth", "Authentication verifies identity (passwords, tokens, biometrics). Authorization determines permissions. OAuth 2.0 and OpenID Connect are standard protocols.", "security"),
            new("s-hashing", "Cryptographic hash functions (SHA-256, bcrypt, Argon2) produce fixed-size digests. They are used for password storage, data integrity, and digital signatures.", "security"),
            new("s-xss", "Cross-site scripting (XSS) injects malicious scripts into web pages. Prevention includes output encoding, Content Security Policy headers, and input sanitization.", "security"),
            new("s-sqli", "SQL injection exploits unsanitized user input in database queries. Parameterized queries and prepared statements are the primary defense.", "security"),
            new("s-jwt", "JSON Web Tokens (JWT) encode claims as signed JSON for stateless authentication. They contain header, payload, and signature sections.", "security"),
            new("s-cors", "Cross-Origin Resource Sharing (CORS) controls which domains can access API resources. Servers specify allowed origins, methods, and headers via response headers.", "security"),
            new("s-firewall", "Firewalls filter network traffic based on rules. Types include packet-filtering, stateful inspection, application-layer (WAF), and next-generation firewalls.", "security"),
            new("s-zerotrust", "Zero trust architecture assumes no implicit trust. Every request is authenticated and authorized regardless of network location. Microsegmentation limits lateral movement.", "security"),
            new("s-pentest", "Penetration testing simulates attacks to find vulnerabilities. Phases include reconnaissance, scanning, exploitation, and reporting. Tools include Burp Suite and Metasploit.", "security"),

            // DevOps & Cloud (10)
            new("s-terraform", "Terraform is an infrastructure-as-code tool that provisions cloud resources declaratively. It uses HCL syntax and maintains state files tracking deployed resources.", "devops"),
            new("s-serverless", "Serverless computing (AWS Lambda, Azure Functions) runs code without managing servers. Billing is per-invocation, and scaling is automatic.", "devops"),
            new("s-microservices", "Microservices architecture decomposes applications into independently deployable services. Each service owns its data and communicates via APIs or message queues.", "devops"),
            new("s-gitops", "GitOps uses Git as the single source of truth for infrastructure and deployments. Changes are applied automatically when committed to the repository.", "devops"),
            new("s-servicemesh", "Service meshes (Istio, Linkerd) handle inter-service communication. They provide traffic management, mTLS, observability, and circuit breaking as a sidecar proxy.", "devops"),
            new("s-cdn", "Content delivery networks cache content at edge locations worldwide. They reduce latency, absorb traffic spikes, and protect against DDoS attacks.", "devops"),
            new("s-objectstorage", "Object storage (S3, Azure Blob, GCS) stores unstructured data as objects with metadata. It provides high durability, scalability, and HTTP-based access.", "devops"),
            new("s-eventdriven", "Event-driven architecture uses events to trigger and communicate between services. Event sourcing stores state as an append-only log of events.", "devops"),
            new("s-featureflags", "Feature flags enable runtime toggling of functionality without redeployment. They support A/B testing, gradual rollouts, and kill switches.", "devops"),
            new("s-logging", "Structured logging captures events as key-value pairs for machine parsing. Centralized logging (ELK, Datadog) aggregates logs across distributed services.", "devops")
        };

        var queries = new List<BenchmarkQuery>
        {
            // Cross-category queries
            new("s01", "Best language for building web applications",
                new() { ["s-javascript"] = 3, ["s-typescript"] = 3, ["s-python"] = 2, ["s-csharp"] = 2, ["s-go"] = 1 }),
            new("s02", "How do hash maps handle key collisions",
                new() { ["s-hashtable"] = 3, ["s-hashing"] = 1, ["s-bloomfilter"] = 1 }),
            new("s03", "Training large language models with attention",
                new() { ["s-transformer"] = 3, ["s-neuralnet"] = 2, ["s-gradient"] = 2, ["s-transfer"] = 1 }),
            new("s04", "Scaling databases horizontally across servers",
                new() { ["s-sharding"] = 3, ["s-replication"] = 2, ["s-nosql"] = 2, ["s-loadbalancer"] = 1 }),
            new("s05", "Securing API endpoints against injection attacks",
                new() { ["s-sqli"] = 3, ["s-xss"] = 2, ["s-auth"] = 2, ["s-cors"] = 1 }),
            new("s06", "Container orchestration and automatic scaling",
                new() { ["s-kubernetes"] = 3, ["s-containers"] = 3, ["s-serverless"] = 1 }),
            new("s07", "Real-time bidirectional communication between client and server",
                new() { ["s-websocket"] = 3, ["s-grpc"] = 2, ["s-http"] = 1 }),
            new("s08", "Storing and querying time-stamped sensor data",
                new() { ["s-timeseries"] = 3, ["s-sql"] = 1, ["s-nosql"] = 1 }),
            new("s09", "Preventing memory leaks in systems programming",
                new() { ["s-rust"] = 3, ["s-gc"] = 2, ["s-cpp"] = 2 }),
            new("s10", "Infrastructure provisioning with code and version control",
                new() { ["s-terraform"] = 3, ["s-gitops"] = 3, ["s-cicd"] = 2 }),

            // Within-category queries
            new("s11", "Difference between stacks and queues",
                new() { ["s-stack"] = 3, ["s-queue"] = 3, ["s-linkedlist"] = 1 }),
            new("s12", "Image recognition and computer vision deep learning",
                new() { ["s-cnn"] = 3, ["s-neuralnet"] = 2, ["s-transfer"] = 1 }),
            new("s13", "Password hashing and secure credential storage",
                new() { ["s-hashing"] = 3, ["s-auth"] = 2, ["s-encryption"] = 2, ["s-jwt"] = 1 }),
            new("s14", "Deploying code automatically on every git push",
                new() { ["s-cicd"] = 3, ["s-gitops"] = 3, ["s-featureflags"] = 1 }),
            new("s15", "Nearest neighbor search in vector databases",
                new() { ["s-vectordb"] = 3, ["s-embeddings"] = 2, ["s-clustering"] = 1 }),

            // Harder cross-domain queries
            new("s16", "Decoupling microservices with asynchronous messaging",
                new() { ["s-messagequeue"] = 3, ["s-microservices"] = 3, ["s-eventdriven"] = 2 }),
            new("s17", "JVM languages with modern syntax and null safety",
                new() { ["s-kotlin"] = 3, ["s-java"] = 2, ["s-swift"] = 1 }),
            new("s18", "Prefix-based autocomplete for search suggestions",
                new() { ["s-trie"] = 3, ["s-hashtable"] = 1, ["s-bst"] = 1 }),
            new("s19", "Encrypting data in transit between services",
                new() { ["s-tls"] = 3, ["s-encryption"] = 2, ["s-servicemesh"] = 1 }),
            new("s20", "Caching strategies to reduce database load",
                new() { ["s-caching"] = 3, ["s-cdn"] = 2, ["s-nosql"] = 1 }),

            // Ambiguity / precision queries
            new("s21", "Processing sequences with memory of previous inputs",
                new() { ["s-rnn"] = 3, ["s-transformer"] = 2, ["s-neuralnet"] = 1 }),
            new("s22", "Choosing between VMs and containers for isolation",
                new() { ["s-virtualization"] = 3, ["s-containers"] = 3, ["s-kubernetes"] = 1 }),
            new("s23", "Learning from rewards without labeled training data",
                new() { ["s-reinforcement"] = 3, ["s-clustering"] = 1 }),
            new("s24", "Monitoring distributed systems and setting alerts",
                new() { ["s-monitoring"] = 3, ["s-logging"] = 2, ["s-servicemesh"] = 1 }),
            new("s25", "Stateless token-based authentication for APIs",
                new() { ["s-jwt"] = 3, ["s-auth"] = 3, ["s-rest"] = 1 }),

            // Specificity gradient
            new("s26", "Low-level systems language with manual memory control",
                new() { ["s-cpp"] = 3, ["s-rust"] = 3, ["s-go"] = 1 }),
            new("s27", "Apple's programming language for mobile apps",
                new() { ["s-swift"] = 3, ["s-kotlin"] = 1 }),
            new("s28", "Object storage for large binary files in the cloud",
                new() { ["s-objectstorage"] = 3, ["s-filesystem"] = 1, ["s-cdn"] = 1 }),
            new("s29", "Preventing overfitting during model training",
                new() { ["s-regularization"] = 3, ["s-gradient"] = 1, ["s-transfer"] = 1 }),
            new("s30", "Typed query language that returns only requested fields from an API",
                new() { ["s-graphql"] = 3, ["s-rest"] = 1, ["s-grpc"] = 1 })
        };

        return new BenchmarkDataset("scale-v1", "Scale Stress Test (80 entries, 30 queries)", seeds, queries);
    }

    /// <summary>Get all available dataset IDs.</summary>
    public static IReadOnlyList<string> GetAvailableDatasets()
    {
        return new[] { "default-v1", "paraphrase-v1", "multihop-v1", "scale-v1", "realworld-v1", "compound-v1", "ambiguity-v1", "distractor-v1", "specificity-v1" };
    }

    /// <summary>Create a dataset by ID.</summary>
    public static BenchmarkDataset? CreateDataset(string datasetId)
    {
        return datasetId switch
        {
            "default-v1" => CreateDefaultDataset(),
            "paraphrase-v1" => CreateParaphraseDataset(),
            "multihop-v1" => CreateMultiHopDataset(),
            "scale-v1" => CreateScaleDataset(),
            "realworld-v1" => CreateRealWorldDataset(),
            "compound-v1" => CreateCompoundDataset(),
            "ambiguity-v1" => CreateAmbiguityDataset(),
            "distractor-v1" => CreateDistractorDataset(),
            "specificity-v1" => CreateSpecificityDataset(),
            _ => null
        };
    }

    /// <summary>
    /// Real-world benchmark dataset modeled after actual cognitive memory usage patterns.
    /// Seeds represent architecture decisions, bug fixes, code patterns, user preferences,
    /// and lessons learned. Queries test the retrieval patterns agents actually use.
    /// </summary>
    public static BenchmarkDataset CreateRealWorldDataset()
    {
        var seeds = new List<BenchmarkSeedEntry>
        {
            new("rw-sqlitewal", "SQLite WAL (Write-Ahead Logging) mode is enabled for crash safety and concurrent read access. WAL allows readers and writers to operate simultaneously without blocking, and ensures data integrity if the process crashes mid-write.", "architecture"),
            new("rw-dlllock", "DLL lock issue caused by MCP server processes holding file handles after build. Multiple dotnet processes (PIDs) can lock the output DLL, preventing recompilation. Fix: kill the server processes before rebuilding, or build the Core project alone with BuildProjectReferences=false.", "bug-fix"),
            new("rw-lockorder", "Lock ordering pattern for thread safety: always acquire the ReaderWriterLockSlim before accessing namespace data. The CognitiveIndex owns the lock; NamespaceStore is not thread-safe and relies on the caller holding the lock. Use EnterWriteLock for mutations, EnterUpgradeableReadLock for reads that may need to load data.", "pattern"),
            new("rw-hybrid", "Hybrid search combines BM25 keyword matching with vector cosine similarity via Reciprocal Rank Fusion (RRF). BM25 captures exact keyword matches that embeddings may miss, while vector search captures semantic similarity. The fusion formula: RRF_score = sum(1/(k + rank_i)) across both result lists.", "architecture"),
            new("rw-quantization", "Int8 quantization reduces vector storage from 4 bytes to 1 byte per dimension (4x reduction). Asymmetric min-max quantization maps FP32 values to [-128, 127] range. Used for two-stage screening: quick Int8 approximate cosine filters candidates, then exact FP32 reranks top results.", "architecture"),
            new("rw-bm25rrf", "BM25 scoring uses term frequency, inverse document frequency, and document length normalization. Results from BM25 and vector search are fused using Reciprocal Rank Fusion with k=60. This avoids score normalization issues and provides robust rank-level fusion.", "pattern"),
            new("rw-darkmode", "User prefers dark mode in all interfaces and terminal-based workflows. Minimal use of emojis unless explicitly requested. Prefer concise, direct communication style without unnecessary preamble.", "preference"),
            new("rw-nocommit", "Never auto-commit code changes unless the user explicitly asks. Always show proposed changes first and wait for approval. Similarly, never push to remote repositories without explicit instruction.", "preference"),
            new("rw-testguid", "Use Guid-based temporary paths for test isolation: Path.Combine(Path.GetTempPath(), $\"test_{Guid.NewGuid():N}\"). This prevents test interference and allows parallel test execution. Always clean up temp directories in finally blocks.", "pattern"),
            new("rw-checksum", "SHA256 checksum verification prevents corrupted or tampered entries from being loaded. Each entry's JSON serialization is hashed on write and verified on read. Entries with mismatched checksums are silently skipped during LoadNamespace to maintain data integrity.", "architecture"),
            new("rw-debounce", "Debounced writes use a 500ms Timer for batching multiple rapid mutations into a single disk write. The timer resets on each new mutation. When it fires, all pending changes are flushed in a single transaction. This dramatically reduces I/O for burst operations like batch imports.", "architecture"),
            new("rw-decay", "Activation energy decay formula: ActivationEnergy = (accessCount * reinforcementWeight) - (hoursSinceLastAccess * decayRate). Default parameters: decayRate=0.1 per hour, reinforcementWeight=1.0 per access. Memories that aren't accessed gradually lose energy and transition through lifecycle states.", "pattern"),
            new("rw-promote", "Memory promotion lifecycle: STM (short-term) is the default state for new memories. When activation energy drops below stmThreshold (default 2.0), STM entries transition to LTM (long-term). LTM entries that decay below archiveThreshold (default -5.0) are archived. Archived entries can be resurrected via deep_recall.", "architecture"),
            new("rw-rerank", "Token-overlap reranker improves search precision by computing Jaccard similarity between query tokens and result text tokens. Applied as a second-stage ranker after vector or hybrid retrieval. Benchmarks show 3-5% precision improvement at minimal latency cost (~0.07ms).", "lesson"),
            new("rw-simd", "SIMD-accelerated vector operations using System.Numerics.Vector<float> for dot product and norm calculations. On AVX2 hardware, processes 8 float elements per iteration (vs 1 for scalar). Int8 dot product uses Vector<sbyte> with widen-to-int accumulation for 32 elements per iteration.", "pattern"),
            new("rw-migration", "Schema migration framework: sequential MigrateToVx methods run in a single transaction. GetSchemaVersion reads current version, RunMigrations applies needed upgrades. Each migration is idempotent (e.g., ALTER TABLE with duplicate column catch). Version is stored in schema_version table.", "architecture"),
            new("rw-idlocator", "IdLocator reverse index provides O(1) entry-to-namespace resolution via a Dictionary<string, string> mapping entry IDs to namespace names. The TryResolveOrLoad helper first checks the locator, then falls back to LoadAll and retries, ensuring entries are found even if their namespace hasn't been loaded yet.", "pattern"),
            new("rw-incremental", "Incremental SQLite writes via INSERT OR REPLACE allow single-entry updates without rewriting the entire namespace. The SqliteStorageProvider tracks pending upserts and deletes separately, flushing them in batched transactions. This replaces the old full-snapshot approach for SQLite backends.", "architecture"),
            new("rw-namespace", "Namespaces isolate memory domains: each project gets its own namespace (e.g., 'mcp-engram-memory', 'wyckit-platform'). Cross-project knowledge uses shared namespaces: 'work' for workflow preferences, 'synthesis' for architectural insights, 'expert_{id}' for specialist routing.", "architecture"),
            new("rw-collapse", "Cluster collapse summarizes groups of related memories into a single summary node. The original members are archived, and a new IsSummaryNode entry is created with the LLM-generated summary text. Collapse is reversible via uncollapse, which restores original member states.", "architecture"),
            new("rw-expert", "Expert routing dispatches queries to specialized knowledge domains. Experts are created with create_expert providing an ID and persona description. dispatch_task embeds the query, finds the closest expert via cosine similarity, and searches that expert's namespace.", "architecture"),
            new("rw-onnx", "ONNX bge-micro-v2 model provides 384-dimensional embeddings for semantic search. The model runs locally via Microsoft.ML.OnnxRuntime with no external API calls. FastBertTokenizer handles tokenization. Model file is loaded once at startup and shared across all embedding requests.", "reference"),
            new("rw-retro", "Session retrospectives capture lessons learned at the end of significant work sessions. Store with category 'lesson' and ID pattern 'retro-YYYY-MM-DD-topic'. Include specific, actionable insights rather than vague observations. Link to related memories with 'elaborates' or 'cross_reference' relations.", "pattern"),
            new("rw-graphedge", "Graph edges connect related memories with typed relations: parent_child (hierarchical), cross_reference (bidirectional, auto-creates reverse), similar_to, contradicts, elaborates, depends_on. Edges are stored globally and traversed via get_neighbors and traverse_graph tools.", "architecture"),
            new("rw-flakytest", "Flaky test caused by ONNX initialization race condition: OnnxEmbeddingService constructor loads the model file, and when multiple test classes initialize in parallel, they compete for the file handle. The test passes in isolation but fails in full suite. Workaround: run affected tests sequentially.", "bug-fix"),
            new("rw-contextual", "Contextual prefix embedding prepends category or namespace context to text before embedding, improving retrieval specificity. Format: '[category] text' or '[namespace] text'. Based on Anthropic's contextual retrieval research showing 49% fewer retrieval failures with context-enriched embeddings.", "pattern"),
            new("rw-lifecycle", "Three lifecycle states manage memory retention: STM (short-term memory, default for new entries, not quantized), LTM (long-term memory, promoted entries, Int8 quantized for fast screening), Archived (dormant entries, quantized, excluded from default searches but findable via deep_recall).", "architecture"),
            new("rw-benchmark", "Benchmark system tests retrieval quality using isolated namespaces with seed entries and graded queries. Four built-in datasets: default-v1 (general), paraphrase-v1 (rephrased queries), multihop-v1 (cross-topic), scale-v1 (80 seeds stress test). Metrics: Recall@K, Precision@K, MRR, nDCG@K, latency P95.", "reference"),
            new("rw-contradiction", "Contradiction detection surfaces conflicting memories via two methods: (1) graph edges with 'contradicts' relation type, and (2) high cosine similarity scan between entries in the same namespace. Results are aggregated and returned for human resolution.", "architecture"),
            new("rw-accretion", "Accretion scan detects dense clusters of related memories using pairwise cosine similarity within a namespace. When a cluster of entries exceeds the similarity threshold, it's flagged as a pending collapse candidate. The LLM generates a summary, and the cluster can be collapsed into a single summary node.", "architecture"),
        };

        var queries = new List<BenchmarkQuery>
        {
            new("rw-q01", "How to fix DLL file locking issues during build",
                new() { ["rw-dlllock"] = 3, ["rw-lockorder"] = 1 }),
            new("rw-q02", "What search modes are available and which performs best",
                new() { ["rw-hybrid"] = 3, ["rw-bm25rrf"] = 2, ["rw-rerank"] = 2, ["rw-quantization"] = 1 }),
            new("rw-q03", "How does memory decay and activation energy work",
                new() { ["rw-decay"] = 3, ["rw-promote"] = 2, ["rw-lifecycle"] = 1 }),
            new("rw-q04", "Database schema upgrade and migration process",
                new() { ["rw-migration"] = 3, ["rw-sqlitewal"] = 1, ["rw-incremental"] = 1 }),
            new("rw-q05", "Performance optimization for vector similarity search",
                new() { ["rw-simd"] = 3, ["rw-quantization"] = 3, ["rw-rerank"] = 1 }),
            new("rw-q06", "How are memories organized and isolated by project",
                new() { ["rw-namespace"] = 3, ["rw-lifecycle"] = 2, ["rw-idlocator"] = 1 }),
            new("rw-q07", "What embedding model is used and how does it work",
                new() { ["rw-onnx"] = 3, ["rw-contextual"] = 2 }),
            new("rw-q08", "How to prevent data corruption and ensure integrity",
                new() { ["rw-checksum"] = 3, ["rw-sqlitewal"] = 2, ["rw-debounce"] = 1 }),
            new("rw-q09", "Agent cognition expert routing and task dispatch",
                new() { ["rw-expert"] = 3, ["rw-collapse"] = 1, ["rw-graphedge"] = 1 }),
            new("rw-q10", "Testing best practices for isolation and reproducibility",
                new() { ["rw-testguid"] = 3, ["rw-flakytest"] = 2 }),
            new("rw-q11", "Batch write performance and debounced persistence",
                new() { ["rw-debounce"] = 3, ["rw-incremental"] = 3, ["rw-sqlitewal"] = 1 }),
            new("rw-q12", "Memory relationship types and knowledge graph structure",
                new() { ["rw-graphedge"] = 3, ["rw-contradiction"] = 2, ["rw-collapse"] = 1 }),
            new("rw-q13", "How does the benchmark system measure retrieval quality",
                new() { ["rw-benchmark"] = 3, ["rw-rerank"] = 1 }),
            new("rw-q14", "User workflow preferences and communication style",
                new() { ["rw-nocommit"] = 3, ["rw-darkmode"] = 2, ["rw-retro"] = 1 }),
            new("rw-q15", "Automatic memory maintenance cleanup and archival",
                new() { ["rw-accretion"] = 3, ["rw-decay"] = 2, ["rw-collapse"] = 2 }),
            new("rw-q16", "How to find entries across namespaces by ID",
                new() { ["rw-idlocator"] = 3, ["rw-namespace"] = 2, ["rw-expert"] = 1 }),
            new("rw-q17", "Memory clustering summarization and collapse",
                new() { ["rw-collapse"] = 3, ["rw-accretion"] = 2, ["rw-graphedge"] = 1 }),
            new("rw-q18", "Embedding quality contextual retrieval and prefix strategies",
                new() { ["rw-contextual"] = 3, ["rw-onnx"] = 2, ["rw-hybrid"] = 1 }),
            new("rw-q19", "What causes flaky tests and how to debug them",
                new() { ["rw-flakytest"] = 3, ["rw-testguid"] = 1 }),
            new("rw-q20", "Lessons learned from code reviews and retrospectives",
                new() { ["rw-retro"] = 3, ["rw-rerank"] = 2, ["rw-lockorder"] = 1 }),
        };

        return new BenchmarkDataset("realworld-v1", "Real-World Cognitive Memory Patterns", seeds, queries);
    }

    /// <summary>
    /// Compound tokenization and domain-jargon benchmark.
    /// Tests hyphenated term matching, cross-domain vocabulary gaps,
    /// and multi-agent/sharing terminology retrieval.
    /// </summary>
    public static BenchmarkDataset CreateCompoundDataset()
    {
        var seeds = new List<BenchmarkSeedEntry>
        {
            // Hyphenated/compound terms (tests BM25 compound tokenization)
            new("c-timeseries", "Time-series databases like InfluxDB and TimescaleDB optimize for timestamped data ingestion and real-time analytics"),
            new("c-realtime", "Real-time event processing with sub-millisecond latency using in-memory stream processors"),
            new("c-lowlatency", "Low-latency network protocols minimize round-trip time for high-frequency trading systems"),
            new("c-crossplatform", "Cross-platform development frameworks like .NET MAUI and Flutter enable write-once deploy-anywhere mobile applications"),
            new("c-multithread", "Multi-threaded concurrency with lock-free data structures and compare-and-swap atomic operations"),
            new("c-keyvalue", "Key-value stores like Redis and DynamoDB provide O(1) lookup for session state and caching"),

            // Domain jargon vs colloquial (tests semantic gap bridging)
            new("c-accretion", "Accretion scan detects dense clusters of related memories using pairwise cosine similarity and DBSCAN density estimation"),
            new("c-decay", "Activation energy decay formula applies exponential time-based reduction to memory salience scores"),
            new("c-collapse", "Cluster collapse summarizes groups of semantically related memories into a single summary node with TF-IDF keyword extraction"),
            new("c-consolidation", "Memory consolidation during sleep cycles transfers short-term episodic traces into long-term semantic knowledge"),
            new("c-quantize", "Scalar quantization compresses 32-bit floating point vectors into 8-bit integers with min-max normalization for 75% storage reduction"),
            new("c-embedding", "Dense vector embeddings encode semantic meaning into fixed-dimensional spaces where cosine similarity measures relatedness"),

            // Multi-agent and sharing concepts
            new("c-namespace", "Namespace isolation partitions memory entries into independent stores with separate indices and lifecycle management"),
            new("c-permission", "Role-based access control grants read or write permissions to agents on a per-namespace basis"),
            new("c-rrffusion", "Reciprocal Rank Fusion merges ranked lists from multiple retrieval systems by summing inverse rank scores"),
            new("c-agentident", "Agent identity distinguishes between multiple AI instances sharing a memory server via unique identifiers"),
            new("c-ownership", "Namespace ownership is established on first write and controls who can grant or revoke sharing permissions"),
            new("c-crosssearch", "Cross-namespace search queries multiple namespaces simultaneously and merges results using rank fusion"),

            // Hybrid retrieval concepts
            new("c-bm25", "BM25 keyword scoring uses term frequency, inverse document frequency, and document length normalization"),
            new("c-hybrid", "Hybrid retrieval combines sparse keyword matching with dense vector similarity for robust recall across query types"),
        };

        var queries = new List<BenchmarkQuery>
        {
            // Compound tokenization queries (should match hyphenated seeds via joined tokens)
            new("c-q01", "Storing and querying time-stamped sensor data",
                new() { ["c-timeseries"] = 3, ["c-realtime"] = 1 }),
            new("c-q02", "Building a real-time event processing pipeline",
                new() { ["c-realtime"] = 3, ["c-lowlatency"] = 2 }),
            new("c-q03", "Low-latency high-frequency trading infrastructure",
                new() { ["c-lowlatency"] = 3, ["c-realtime"] = 1 }),
            new("c-q04", "Cross-platform mobile development frameworks",
                new() { ["c-crossplatform"] = 3 }),
            new("c-q05", "Multi-threaded lock-free concurrent programming",
                new() { ["c-multithread"] = 3 }),
            new("c-q06", "Key-value caching for session management",
                new() { ["c-keyvalue"] = 3 }),

            // Domain jargon queries (colloquial phrasing vs technical seeds)
            new("c-q07", "Automatic memory maintenance cleanup and archival",
                new() { ["c-accretion"] = 3, ["c-decay"] = 2, ["c-collapse"] = 2 }),
            new("c-q08", "How does the system forget old unimportant memories",
                new() { ["c-decay"] = 3, ["c-consolidation"] = 2, ["c-collapse"] = 1 }),
            new("c-q09", "Compressing vectors to save storage space",
                new() { ["c-quantize"] = 3, ["c-embedding"] = 1 }),
            new("c-q10", "Converting text into searchable numeric representations",
                new() { ["c-embedding"] = 3, ["c-bm25"] = 1, ["c-quantize"] = 1 }),

            // Multi-agent queries
            new("c-q11", "How do multiple agents share knowledge between each other",
                new() { ["c-permission"] = 3, ["c-namespace"] = 2, ["c-crosssearch"] = 2, ["c-agentident"] = 1 }),
            new("c-q12", "Searching across different knowledge domains in one query",
                new() { ["c-crosssearch"] = 3, ["c-rrffusion"] = 2, ["c-namespace"] = 1 }),
            new("c-q13", "Who owns a namespace and how are permissions controlled",
                new() { ["c-ownership"] = 3, ["c-permission"] = 2, ["c-agentident"] = 1 }),
            new("c-q14", "Merging search results from multiple sources into a single ranking",
                new() { ["c-rrffusion"] = 3, ["c-hybrid"] = 2, ["c-crosssearch"] = 1 }),
            new("c-q15", "Combining keyword search with semantic vector search",
                new() { ["c-hybrid"] = 3, ["c-bm25"] = 2, ["c-embedding"] = 1, ["c-rrffusion"] = 1 }),
        };

        return new BenchmarkDataset("compound-v1", "Compound Tokenization, Domain Jargon & Multi-Agent Benchmark", seeds, queries);
    }

    /// <summary>
    /// Cross-Domain Ambiguity benchmark: seeds where the same term has different meanings
    /// across domains. Tests whether the system retrieves the correct semantic context
    /// rather than surface-level keyword matches.
    /// </summary>
    public static BenchmarkDataset CreateAmbiguityDataset()
    {
        // Ambiguous term groups:
        //   "network"  → computer networking vs neural networks
        //   "tree"     → data structure vs file system vs DOM
        //   "memory"   → RAM/hardware vs memory management vs cognitive memory
        //   "model"    → ML model vs data model vs design pattern (MVC)
        //   "kernel"   → OS kernel vs ML kernel vs image processing kernel
        //   "port"     → network port vs software porting vs hardware port
        //   "pipeline" → CI/CD vs data pipeline vs CPU pipeline
        //   "table"    → database table vs hash table vs routing table
        //   "branch"   → git branch vs tree branch vs conditional branch
        //   "node"     → network node vs tree node vs Node.js
        var seeds = new List<BenchmarkSeedEntry>
        {
            // "network" group
            new("a-net-comp", "Computer networks connect devices using routers, switches, and protocols like TCP/IP and Ethernet. Network topology describes how nodes are physically or logically arranged — star, mesh, ring, or bus.", "networking"),
            new("a-net-neural", "Neural networks are layered computational graphs where artificial neurons apply weighted sums and activation functions. Deep neural networks with many hidden layers can learn hierarchical feature representations from raw data.", "ml"),

            // "tree" group
            new("a-tree-ds", "A tree data structure organizes elements hierarchically with a root node and child nodes. Binary search trees, AVL trees, red-black trees, and B-trees each optimize for different access patterns and balance guarantees.", "data-structures"),
            new("a-tree-fs", "A file system tree organizes directories and files in a hierarchical structure starting from the root directory. Commands like ls, cd, and find navigate the tree. Inodes track metadata for each node in the hierarchy.", "systems"),
            new("a-tree-dom", "The Document Object Model (DOM) tree represents an HTML page as a hierarchy of element nodes. JavaScript manipulates the DOM tree to dynamically update web page content, structure, and styling.", "web"),

            // "memory" group
            new("a-mem-hw", "Computer memory includes RAM for fast volatile storage and ROM for firmware. DDR5 SDRAM operates at higher bandwidth than DDR4. The memory hierarchy spans registers, L1/L2/L3 cache, main memory, and disk.", "hardware"),
            new("a-mem-mgmt", "Memory management in programming involves allocating heap and stack memory, tracking references, and freeing unused allocations. Memory leaks occur when allocated memory is never freed, gradually exhausting available resources.", "systems"),
            new("a-mem-cognitive", "Cognitive memory systems model how biological brains store and retrieve information. Short-term memory has limited capacity, while long-term memory consolidates through rehearsal and sleep. Semantic memory stores facts and episodic memory stores experiences.", "cognitive-science"),

            // "model" group
            new("a-model-ml", "Machine learning models are trained on data to make predictions. Model architectures include linear regression, decision trees, random forests, SVMs, and deep neural networks. Hyperparameter tuning and cross-validation optimize model performance.", "ml"),
            new("a-model-data", "Data models define the structure of information in databases and applications. Entity-relationship diagrams, UML class diagrams, and schema definitions describe entities, attributes, and relationships between data objects.", "databases"),
            new("a-model-mvc", "The Model-View-Controller (MVC) design pattern separates application logic into three components. The Model manages data and business rules, the View renders the UI, and the Controller handles user input and coordinates between them.", "architecture"),

            // "kernel" group
            new("a-kernel-os", "The operating system kernel manages hardware resources, process scheduling, memory allocation, and system calls. Linux, Windows NT, and XNU are monolithic or hybrid kernels that mediate between user space and hardware.", "systems"),
            new("a-kernel-ml", "Kernel methods in machine learning map data into higher-dimensional feature spaces. The kernel trick enables SVMs to find non-linear decision boundaries efficiently. Common kernels include RBF (Gaussian), polynomial, and sigmoid.", "ml"),
            new("a-kernel-img", "Image processing kernels are small matrices convolved with images to apply effects. A 3x3 Gaussian kernel blurs images, a Sobel kernel detects edges, and a sharpening kernel enhances detail. Convolutional neural networks learn these kernels automatically.", "image-processing"),

            // "port" group
            new("a-port-net", "Network ports are numbered endpoints for communication. TCP port 80 serves HTTP, port 443 serves HTTPS, and port 22 serves SSH. Firewalls filter traffic by port number to control network access.", "networking"),
            new("a-port-sw", "Software porting adapts a program to run on a different platform or operating system. Porting involves handling differences in system calls, endianness, compiler behavior, and hardware-specific instructions.", "systems"),

            // "pipeline" group
            new("a-pipe-cicd", "CI/CD pipelines automate building, testing, and deploying software. GitHub Actions, Jenkins, and GitLab CI define pipeline stages that run on each commit — linting, unit tests, integration tests, and deployment to staging or production.", "devops"),
            new("a-pipe-data", "Data pipelines extract, transform, and load (ETL) data between systems. Apache Kafka streams events in real-time, Apache Spark processes batch data, and Airflow orchestrates complex multi-step data workflows.", "data-engineering"),
            new("a-pipe-cpu", "CPU instruction pipelines overlap fetch, decode, execute, and write-back stages to increase throughput. Pipeline hazards — data dependencies, control flow changes, and structural conflicts — cause stalls that reduce performance.", "hardware"),

            // "table" group
            new("a-table-db", "Database tables store data in rows and columns with defined schemas. Primary keys uniquely identify rows, foreign keys reference other tables, and indexes speed up queries. Normalization eliminates data redundancy.", "databases"),
            new("a-table-hash", "Hash tables implement associative arrays by mapping keys through a hash function to bucket indices. Collision resolution strategies include chaining (linked lists per bucket) and open addressing (linear or quadratic probing).", "data-structures"),
            new("a-table-route", "Routing tables in network devices map destination IP addresses to next-hop routers and interfaces. Protocols like OSPF, BGP, and RIP dynamically update routing tables as network topology changes.", "networking"),

            // "branch" group
            new("a-branch-git", "Git branches are lightweight pointers to commits that enable parallel development. Feature branches isolate changes, merge commits integrate them, and rebasing replays commits onto a new base for linear history.", "devops"),
            new("a-branch-cpu", "Branch prediction in CPUs speculates which path a conditional instruction will take. Mispredictions flush the pipeline, wasting cycles. Modern predictors use pattern history tables and neural-inspired perceptron predictors.", "hardware"),
        };

        var queries = new List<BenchmarkQuery>
        {
            // Unambiguous queries — clear domain context should retrieve correct sense
            new("a-q01", "How do routers forward packets across a computer network",
                new() { ["a-net-comp"] = 3, ["a-table-route"] = 2, ["a-port-net"] = 1 }),
            new("a-q02", "Training deep neural networks with backpropagation",
                new() { ["a-net-neural"] = 3, ["a-model-ml"] = 2, ["a-kernel-ml"] = 1 }),
            new("a-q03", "Navigating the file system directory hierarchy",
                new() { ["a-tree-fs"] = 3, ["a-tree-ds"] = 1 }),
            new("a-q04", "How JavaScript updates the DOM to render dynamic content",
                new() { ["a-tree-dom"] = 3, ["a-tree-ds"] = 1 }),
            new("a-q05", "DDR5 RAM bandwidth and the memory hierarchy",
                new() { ["a-mem-hw"] = 3, ["a-pipe-cpu"] = 1 }),
            new("a-q06", "Preventing memory leaks in C++ heap allocations",
                new() { ["a-mem-mgmt"] = 3, ["a-mem-hw"] = 1 }),

            // Ambiguous queries — the ambiguous term appears without clear domain context
            new("a-q07", "How do networks learn and adapt",
                new() { ["a-net-neural"] = 3, ["a-net-comp"] = 2 }),
            new("a-q08", "Working with trees and traversal algorithms",
                new() { ["a-tree-ds"] = 3, ["a-tree-fs"] = 1, ["a-tree-dom"] = 1 }),
            new("a-q09", "Understanding different types of memory and their roles",
                new() { ["a-mem-hw"] = 3, ["a-mem-mgmt"] = 2, ["a-mem-cognitive"] = 2 }),
            new("a-q10", "Using kernel functions for pattern recognition",
                new() { ["a-kernel-ml"] = 3, ["a-kernel-img"] = 2 }),
            new("a-q11", "How models are structured and validated",
                new() { ["a-model-ml"] = 2, ["a-model-data"] = 2, ["a-model-mvc"] = 2 }),
            new("a-q12", "Designing efficient pipelines for throughput",
                new() { ["a-pipe-cicd"] = 2, ["a-pipe-data"] = 2, ["a-pipe-cpu"] = 2 }),

            // Cross-domain queries — intentionally span two ambiguous senses
            new("a-q13", "Using tables to store and look up data efficiently",
                new() { ["a-table-db"] = 3, ["a-table-hash"] = 3, ["a-table-route"] = 1 }),
            new("a-q14", "Branch management in development workflows and version control",
                new() { ["a-branch-git"] = 3, ["a-branch-cpu"] = 0 }),
            new("a-q15", "Porting software across different operating system kernels",
                new() { ["a-port-sw"] = 3, ["a-kernel-os"] = 2, ["a-port-net"] = 0 }),
        };

        return new BenchmarkDataset("ambiguity-v1", "Cross-Domain Ambiguity Benchmark", seeds, queries);
    }

    /// <summary>
    /// Negative/Distractor Resilience benchmark: queries that are superficially similar to seeds
    /// but semantically different. Tests whether the system avoids false positives by distinguishing
    /// homonyms, false friends, and surface-level keyword overlap from genuine semantic relevance.
    /// Focuses on Precision (low false-positive rate) rather than Recall.
    /// </summary>
    public static BenchmarkDataset CreateDistractorDataset()
    {
        // Each seed group has a "target" (what the query actually wants) and "distractors"
        // (entries that share surface terms but are semantically unrelated to the query).
        // Grade 0 marks explicit distractors that should NOT appear in top results.
        var seeds = new List<BenchmarkSeedEntry>
        {
            // "Python" — programming language vs snake
            new("d-python-lang", "Python is a high-level interpreted programming language created by Guido van Rossum. It emphasizes code readability with significant whitespace and supports multiple programming paradigms including procedural, object-oriented, and functional.", "programming"),
            new("d-python-snake", "The python is a large non-venomous snake found in Africa, Asia, and Australia. Reticulated pythons can exceed 6 meters in length. They kill prey by constriction, wrapping their muscular body around the animal and squeezing.", "biology"),

            // "Java" — programming language vs island vs coffee
            new("d-java-lang", "Java is a class-based, object-oriented programming language designed to be platform-independent. The JVM (Java Virtual Machine) enables write-once-run-anywhere portability. Java is widely used for enterprise applications, Android development, and backend services.", "programming"),
            new("d-java-island", "Java is an Indonesian island and one of the most densely populated places on Earth. Its capital Jakarta is a major economic hub. The island has numerous volcanoes including the historically active Mount Merapi.", "geography"),
            new("d-java-coffee", "Java coffee refers to coffee grown on the Indonesian island of Java. Known for its heavy body, low acidity, and earthy flavor profile, Java coffee has been cultivated since Dutch colonial times and remains a premium single-origin variety.", "food"),

            // "Mars" — planet vs candy bar vs Roman god
            new("d-mars-planet", "Mars is the fourth planet from the Sun with a thin atmosphere of mostly carbon dioxide. Its reddish appearance comes from iron oxide on the surface. NASA's Perseverance rover is currently exploring Jezero Crater for signs of ancient microbial life.", "astronomy"),
            new("d-mars-candy", "Mars is a chocolate bar manufactured by Mars, Incorporated. It consists of nougat topped with caramel and coated in milk chocolate. The Mars bar was first produced in 1932 in Slough, England.", "food"),

            // "Spring" — Java framework vs season vs mechanical spring
            new("d-spring-framework", "Spring Framework is a comprehensive Java application framework providing dependency injection, aspect-oriented programming, and MVC web support. Spring Boot auto-configures applications with embedded servers and production-ready features.", "programming"),
            new("d-spring-season", "Spring is the season between winter and summer when temperatures rise, plants bloom, and daylight hours increase. The vernal equinox marks the astronomical beginning of spring in the Northern Hemisphere around March 20.", "nature"),
            new("d-spring-mechanical", "A mechanical spring stores elastic potential energy when deformed. Hooke's law states that force is proportional to displacement (F = -kx). Springs are used in suspension systems, watches, mattresses, and industrial shock absorbers.", "physics"),

            // "Rust" — programming language vs corrosion
            new("d-rust-lang", "Rust is a systems programming language emphasizing memory safety without garbage collection. Its ownership system with borrowing and lifetimes prevents data races at compile time. Rust is used for operating systems, game engines, and WebAssembly.", "programming"),
            new("d-rust-corrosion", "Rust is an iron oxide formed by the reaction of iron and oxygen in the presence of water or moisture. The electrochemical process of rusting gradually weakens metal structures. Galvanization, paint coatings, and stainless steel alloys prevent corrosion.", "chemistry"),

            // "Docker" — containerization vs dockworker
            new("d-docker-tech", "Docker is a platform for developing, shipping, and running applications in containers. Docker images package code with dependencies, and Docker Compose orchestrates multi-container applications. Kubernetes manages Docker containers at scale in production.", "devops"),
            new("d-docker-worker", "A docker or dockworker is a person who loads and unloads cargo from ships at ports. Historically known as longshoremen or stevedores, dockworkers operate cranes, forklifts, and cargo handling equipment in maritime shipping facilities.", "occupation"),

            // "Shell" — command shell vs seashell vs Shell oil
            new("d-shell-cli", "A command-line shell interprets user commands and scripts. Bash, Zsh, and PowerShell provide job control, variable expansion, piping, and redirection. Shell scripts automate system administration, build processes, and deployment tasks.", "systems"),
            new("d-shell-sea", "Seashells are the hard protective outer layers of marine mollusks. Gastropod shells spiral from a central axis, while bivalve shells have two hinged halves. Shells are composed of calcium carbonate secreted by the mantle tissue.", "biology"),

            // "Apache" — web server vs helicopter vs indigenous people
            new("d-apache-server", "Apache HTTP Server is the world's most widely used web server software. It supports virtual hosting, URL rewriting, SSL/TLS, and modules for PHP and Python. Apache is maintained by the Apache Software Foundation as open-source.", "web"),
            new("d-apache-helicopter", "The AH-64 Apache is a twin-engine attack helicopter used by the United States Army. Armed with Hellfire missiles, Hydra rockets, and a 30mm chain gun, it provides close air support and anti-armor capability in combat operations.", "military"),

            // "Latex" — typesetting vs material
            new("d-latex-typeset", "LaTeX is a document preparation system for high-quality typesetting. Built on TeX, it excels at mathematical notation, academic papers, and technical documents. BibTeX manages bibliographic references and citations.", "publishing"),
            new("d-latex-material", "Latex is a milky fluid produced by rubber trees (Hevea brasiliensis). Natural latex is harvested by tapping the bark and collected in cups. It is processed into rubber for gloves, balloons, tires, and medical equipment.", "materials"),

            // "Mercury" — planet vs element vs Roman god
            new("d-mercury-planet", "Mercury is the smallest planet and closest to the Sun. It has virtually no atmosphere and extreme temperature swings from 430°C during the day to -180°C at night. MESSENGER spacecraft mapped its heavily cratered surface.", "astronomy"),
            new("d-mercury-element", "Mercury is a chemical element (Hg) and the only metal that is liquid at room temperature. It was historically used in thermometers and dental amalgams but is now regulated due to neurotoxicity. Mercury vapor is particularly hazardous.", "chemistry"),
        };

        var queries = new List<BenchmarkQuery>
        {
            // Queries that should retrieve the programming/tech sense, NOT the non-tech distractor
            new("d-q01", "Python libraries for data science and machine learning",
                new() { ["d-python-lang"] = 3, ["d-python-snake"] = 0 }),
            new("d-q02", "Reticulated python habitat and behavior in Southeast Asia",
                new() { ["d-python-snake"] = 3, ["d-python-lang"] = 0 }),
            new("d-q03", "Building enterprise microservices with Java and Spring Boot",
                new() { ["d-java-lang"] = 3, ["d-spring-framework"] = 2, ["d-java-island"] = 0, ["d-java-coffee"] = 0, ["d-spring-season"] = 0 }),
            new("d-q04", "Coffee varieties and flavor profiles from Indonesian islands",
                new() { ["d-java-coffee"] = 3, ["d-java-island"] = 2, ["d-java-lang"] = 0, ["d-spring-framework"] = 0 }),
            new("d-q05", "Exploring the surface of Mars with robotic rovers",
                new() { ["d-mars-planet"] = 3, ["d-mercury-planet"] = 1, ["d-mars-candy"] = 0 }),
            new("d-q06", "Dependency injection and inversion of control in application frameworks",
                new() { ["d-spring-framework"] = 3, ["d-spring-season"] = 0, ["d-spring-mechanical"] = 0 }),
            new("d-q07", "Hooke's law and elastic deformation in mechanical systems",
                new() { ["d-spring-mechanical"] = 3, ["d-spring-framework"] = 0, ["d-spring-season"] = 0 }),
            new("d-q08", "Memory safety and ownership in systems programming languages",
                new() { ["d-rust-lang"] = 3, ["d-rust-corrosion"] = 0 }),
            new("d-q09", "Preventing iron corrosion with protective coatings",
                new() { ["d-rust-corrosion"] = 3, ["d-rust-lang"] = 0 }),
            new("d-q10", "Container orchestration and deployment pipelines",
                new() { ["d-docker-tech"] = 3, ["d-docker-worker"] = 0 }),
            new("d-q11", "Writing Bash scripts for server automation",
                new() { ["d-shell-cli"] = 3, ["d-shell-sea"] = 0, ["d-apache-server"] = 1 }),
            new("d-q12", "Configuring virtual hosts on a web server",
                new() { ["d-apache-server"] = 3, ["d-apache-helicopter"] = 0 }),
            new("d-q13", "Formatting equations and bibliographies in academic papers",
                new() { ["d-latex-typeset"] = 3, ["d-latex-material"] = 0 }),
            new("d-q14", "Harvesting natural rubber from tropical trees",
                new() { ["d-latex-material"] = 3, ["d-latex-typeset"] = 0 }),
            new("d-q15", "Toxic heavy metals and their health effects",
                new() { ["d-mercury-element"] = 3, ["d-mercury-planet"] = 0, ["d-rust-corrosion"] = 1 }),
        };

        return new BenchmarkDataset("distractor-v1", "Negative/Distractor Resilience Benchmark", seeds, queries);
    }

    /// <summary>
    /// Specificity Gradient Benchmark — 30 seeds across 6 topic clusters at varying abstraction levels.
    /// 18 queries at 3 specificity tiers (broad, medium, narrow) testing how retrieval precision and
    /// ranking change as queries move from general to highly specific.
    /// </summary>
    public static BenchmarkDataset CreateSpecificityDataset()
    {
        var seeds = new List<BenchmarkSeedEntry>
        {
            // ── Programming Languages cluster ──
            new("sp-lang-python", "Python is a high-level interpreted programming language emphasizing readability and rapid prototyping. Its extensive standard library and package ecosystem (pip, PyPI) make it popular for scripting, automation, data science, and web development with frameworks like Django and Flask.", "languages"),
            new("sp-lang-javascript", "JavaScript is a dynamic, prototype-based scripting language that runs in web browsers and Node.js. It powers interactive websites, single-page applications, and server-side APIs. Key features include closures, the event loop, promises, and async/await for asynchronous programming.", "languages"),
            new("sp-lang-rust", "Rust is a systems programming language focused on memory safety without garbage collection. Its ownership model with borrowing and lifetimes prevents data races at compile time. Rust is used for operating systems, game engines, WebAssembly, and high-performance network services.", "languages"),
            new("sp-lang-csharp", "C# is a statically-typed, object-oriented programming language developed by Microsoft for the .NET platform. It supports generics, LINQ, async/await, pattern matching, and records. C# is used for enterprise applications, game development (Unity), and cloud services.", "languages"),
            new("sp-lang-go", "Go (Golang) is a statically-typed compiled language designed at Google for simplicity and concurrency. Goroutines and channels provide lightweight concurrent programming. Go is widely used for cloud infrastructure, microservices, CLI tools, and container orchestration (Docker, Kubernetes).", "languages"),

            // ── Web Development cluster ──
            new("sp-web-http", "HTTP (Hypertext Transfer Protocol) is the foundation of data communication on the web. HTTP/1.1 introduced persistent connections and chunked transfer encoding. HTTP/2 added multiplexing and header compression. HTTP/3 uses QUIC over UDP for reduced latency.", "web"),
            new("sp-web-rest", "REST (Representational State Transfer) is an architectural style for designing web APIs. RESTful APIs use HTTP methods (GET, POST, PUT, DELETE) to operate on resources identified by URIs. Key principles include statelessness, uniform interface, and hypermedia as the engine of application state (HATEOAS).", "web"),
            new("sp-web-react", "React is a JavaScript library for building user interfaces through composable components. It uses a virtual DOM for efficient updates, JSX for declarative markup, and hooks (useState, useEffect) for state management. React powers single-page applications and can render server-side with Next.js.", "web"),
            new("sp-web-css", "CSS (Cascading Style Sheets) controls the visual presentation of HTML documents. Modern CSS includes Flexbox and Grid for layout, custom properties (variables), media queries for responsive design, animations, and transitions. Preprocessors like Sass extend CSS with nesting, mixins, and functions.", "web"),
            new("sp-web-auth", "Web authentication secures user access to web applications. Common approaches include session cookies, JWT (JSON Web Tokens), OAuth 2.0 for delegated authorization, and OpenID Connect for identity. Multi-factor authentication (MFA) adds security layers beyond passwords.", "web"),

            // ── Data & Databases cluster ──
            new("sp-data-sql", "SQL (Structured Query Language) is the standard language for relational database management. Core operations include SELECT, INSERT, UPDATE, DELETE with JOIN, GROUP BY, and subqueries. Modern SQL supports window functions, CTEs (Common Table Expressions), and JSON operations.", "databases"),
            new("sp-data-postgres", "PostgreSQL is an advanced open-source relational database with support for JSONB, full-text search, GIS (PostGIS), and extensible type systems. It offers MVCC for concurrent access, write-ahead logging for durability, and supports partitioning, materialized views, and custom indexes (GIN, GiST, BRIN).", "databases"),
            new("sp-data-redis", "Redis is an in-memory data structure store used as a database, cache, and message broker. It supports strings, hashes, lists, sets, sorted sets, streams, and geospatial indexes. Redis provides sub-millisecond latency, pub/sub messaging, Lua scripting, and cluster mode for horizontal scaling.", "databases"),
            new("sp-data-nosql", "NoSQL databases provide flexible schema designs for specific workloads. Document stores (MongoDB) handle nested JSON. Key-value stores (DynamoDB) optimize for simple lookups. Column-family stores (Cassandra) handle time-series at scale. Graph databases (Neo4j) model relationships natively.", "databases"),
            new("sp-data-indexing", "Database indexing accelerates query performance by maintaining sorted data structures. B-tree indexes support range queries, hash indexes optimize equality lookups, and inverted indexes power full-text search. Composite indexes cover multi-column queries. Over-indexing wastes storage and slows writes.", "databases"),

            // ── Machine Learning cluster ──
            new("sp-ml-neural", "Neural networks are computational models inspired by biological neurons. They consist of layers of interconnected nodes that learn representations through backpropagation. Architectures include feedforward networks, convolutional networks (CNNs) for images, and recurrent networks (RNNs) for sequences.", "ml"),
            new("sp-ml-transformer", "Transformers are neural network architectures based on self-attention mechanisms. They process input sequences in parallel rather than sequentially, enabling efficient training on large datasets. Transformers power large language models (GPT, BERT, LLaMA) and have been adapted for vision (ViT) and multimodal tasks.", "ml"),
            new("sp-ml-gradient", "Gradient descent is the fundamental optimization algorithm for training neural networks. Variants include stochastic gradient descent (SGD), Adam (adaptive moment estimation), and AdaGrad. Learning rate scheduling, momentum, and weight decay are key hyperparameters. Gradient clipping prevents exploding gradients.", "ml"),
            new("sp-ml-nlp", "Natural language processing (NLP) enables computers to understand and generate human language. Key tasks include tokenization, named entity recognition, sentiment analysis, machine translation, and question answering. Modern NLP relies on pretrained language models fine-tuned for specific tasks.", "ml"),
            new("sp-ml-embedding", "Embeddings map discrete tokens (words, sentences, documents) to dense vector representations in continuous space. Word2Vec and GloVe learn word embeddings from co-occurrence. Sentence embeddings (SBERT, BGE) capture semantic meaning for similarity search and retrieval-augmented generation.", "ml"),

            // ── Systems & Infrastructure cluster ──
            new("sp-sys-containers", "Containers package applications with their dependencies into isolated, portable units. Docker provides the container runtime and image format. Container images are built from layered Dockerfiles. Registries (Docker Hub, ECR) store and distribute images.", "systems"),
            new("sp-sys-kubernetes", "Kubernetes orchestrates containerized applications across clusters of machines. It manages deployment, scaling, and networking through declarative configuration. Core concepts include Pods, Services, Deployments, StatefulSets, and Ingress controllers. Helm charts package Kubernetes manifests.", "systems"),
            new("sp-sys-linux", "Linux is an open-source operating system kernel used in servers, embedded systems, and cloud infrastructure. Key concepts include process management, file systems (ext4, XFS), permissions (rwx, ACLs), systemd service management, and networking (iptables, nftables). Shell scripting automates administration.", "systems"),
            new("sp-sys-networking", "Computer networking connects devices through layered protocols. The TCP/IP model includes link, internet (IP), transport (TCP, UDP), and application layers. DNS resolves domain names, DHCP assigns IP addresses, and TLS/SSL encrypts communication. Load balancers distribute traffic across servers.", "systems"),
            new("sp-sys-ci", "Continuous integration and continuous deployment (CI/CD) automate software build, test, and release pipelines. Tools include GitHub Actions, GitLab CI, and Jenkins. Pipelines run linting, unit tests, integration tests, and deploy to staging and production environments. Artifacts are versioned and traceable.", "systems"),

            // ── Security cluster ──
            new("sp-sec-crypto", "Cryptography protects data through mathematical algorithms. Symmetric encryption (AES) uses shared keys. Asymmetric encryption (RSA, ECDSA) uses key pairs. Hash functions (SHA-256) produce fixed-size digests. Digital signatures verify authenticity. TLS combines these for secure network communication.", "security"),
            new("sp-sec-owasp", "The OWASP Top 10 lists critical web application security risks: injection (SQL, XSS), broken authentication, sensitive data exposure, XML external entities, broken access control, security misconfiguration, cross-site scripting, insecure deserialization, vulnerable components, and insufficient logging.", "security"),
            new("sp-sec-zerorust", "Zero-trust security architecture assumes no implicit trust for any user or system, regardless of network location. Every access request is verified with identity, device health, and context. Micro-segmentation limits lateral movement. Continuous monitoring detects anomalies in real time.", "security"),
            new("sp-sec-pentesting", "Penetration testing simulates adversarial attacks to identify security vulnerabilities. Methodologies include OSSTMM, PTES, and OWASP Testing Guide. Tools like Burp Suite, Nmap, and Metasploit help discover network, application, and configuration weaknesses. Findings are reported with severity ratings and remediation guidance.", "security"),
            new("sp-sec-iam", "Identity and access management (IAM) controls who can access what resources. Role-based access control (RBAC) assigns permissions to roles. Attribute-based access control (ABAC) uses policies with contextual attributes. Single sign-on (SSO) with SAML or OIDC centralizes authentication. Least privilege principle limits access to minimum necessary permissions.", "security"),
        };

        var queries = new List<BenchmarkQuery>
        {
            // ── Broad queries (should retrieve multiple seeds across a topic cluster) ──
            new("sp-q01", "Programming languages and software development",
                new() { ["sp-lang-python"] = 2, ["sp-lang-javascript"] = 2, ["sp-lang-rust"] = 2, ["sp-lang-csharp"] = 2, ["sp-lang-go"] = 2 }),
            new("sp-q02", "Building and deploying web applications",
                new() { ["sp-web-http"] = 1, ["sp-web-rest"] = 2, ["sp-web-react"] = 2, ["sp-web-css"] = 1, ["sp-web-auth"] = 1 }),
            new("sp-q03", "Storing and querying data in databases",
                new() { ["sp-data-sql"] = 2, ["sp-data-postgres"] = 2, ["sp-data-redis"] = 1, ["sp-data-nosql"] = 2, ["sp-data-indexing"] = 1 }),
            new("sp-q04", "Artificial intelligence and machine learning",
                new() { ["sp-ml-neural"] = 2, ["sp-ml-transformer"] = 2, ["sp-ml-gradient"] = 1, ["sp-ml-nlp"] = 2, ["sp-ml-embedding"] = 1 }),
            new("sp-q05", "Server infrastructure and cloud operations",
                new() { ["sp-sys-containers"] = 2, ["sp-sys-kubernetes"] = 2, ["sp-sys-linux"] = 2, ["sp-sys-networking"] = 1, ["sp-sys-ci"] = 1 }),
            new("sp-q06", "Cybersecurity and protecting systems from attacks",
                new() { ["sp-sec-crypto"] = 2, ["sp-sec-owasp"] = 2, ["sp-sec-zerorust"] = 2, ["sp-sec-pentesting"] = 2 }),

            // ── Medium queries (should retrieve 2-3 seeds within a topic cluster) ──
            new("sp-q07", "Backend web frameworks and API design patterns",
                new() { ["sp-web-rest"] = 3, ["sp-web-http"] = 2, ["sp-web-auth"] = 1, ["sp-lang-python"] = 1, ["sp-lang-go"] = 1 }),
            new("sp-q08", "Relational databases and SQL query optimization",
                new() { ["sp-data-sql"] = 3, ["sp-data-postgres"] = 3, ["sp-data-indexing"] = 2 }),
            new("sp-q09", "Training deep learning models with neural networks",
                new() { ["sp-ml-neural"] = 3, ["sp-ml-gradient"] = 3, ["sp-ml-transformer"] = 2 }),
            new("sp-q10", "Container orchestration and deployment automation",
                new() { ["sp-sys-containers"] = 3, ["sp-sys-kubernetes"] = 3, ["sp-sys-ci"] = 2 }),
            new("sp-q11", "Web application security vulnerabilities and prevention",
                new() { ["sp-sec-owasp"] = 3, ["sp-sec-pentesting"] = 2, ["sp-web-auth"] = 1 }),
            new("sp-q12", "Concurrent programming with goroutines and async/await",
                new() { ["sp-lang-go"] = 3, ["sp-lang-javascript"] = 2, ["sp-lang-csharp"] = 2, ["sp-lang-rust"] = 1 }),

            // ── Narrow queries (should strongly prefer 1-2 specific seeds) ──
            new("sp-q13", "PostgreSQL JSONB operators and GIN index performance for document queries",
                new() { ["sp-data-postgres"] = 3, ["sp-data-indexing"] = 2, ["sp-data-sql"] = 1 }),
            new("sp-q14", "Self-attention mechanism and positional encoding in transformer architectures",
                new() { ["sp-ml-transformer"] = 3, ["sp-ml-neural"] = 1 }),
            new("sp-q15", "Configuring Kubernetes Ingress controllers and service mesh routing",
                new() { ["sp-sys-kubernetes"] = 3, ["sp-sys-networking"] = 1 }),
            new("sp-q16", "Rust ownership model, borrowing rules, and lifetime annotations for memory safety",
                new() { ["sp-lang-rust"] = 3 }),
            new("sp-q17", "BGE and SBERT sentence embedding models for semantic similarity search",
                new() { ["sp-ml-embedding"] = 3, ["sp-ml-nlp"] = 2, ["sp-ml-transformer"] = 1 }),
            new("sp-q18", "Elliptic curve digital signatures and TLS 1.3 handshake protocol",
                new() { ["sp-sec-crypto"] = 3, ["sp-web-auth"] = 1 }),
        };

        return new BenchmarkDataset("specificity-v1", "Specificity Gradient Benchmark", seeds, queries);
    }
}
