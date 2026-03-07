using System.Diagnostics;
using McpVectorMemory.Core.Models;

namespace McpVectorMemory.Core.Services;

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
    /// Run a benchmark dataset: ingest seed entries, execute queries, compute metrics, clean up.
    /// Uses an isolated namespace to avoid contaminating real data.
    /// </summary>
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

    public BenchmarkRunResult Run(BenchmarkDataset dataset, SearchMode mode = SearchMode.Vector,
        bool useContextualPrefix = false)
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
                var entry = new CognitiveEntry(seed.Id, vector, ns, seed.Text, seed.Category, lifecycleState: "ltm");
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
        return new[] { "default-v1", "paraphrase-v1", "multihop-v1", "scale-v1" };
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
            _ => null
        };
    }
}
