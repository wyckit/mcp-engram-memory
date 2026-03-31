[< Back to README](../README.md)

# Project Structure

The solution is split into two projects:

| Project | Type | Description |
|---------|------|-------------|
| `McpEngramMemory` | Executable | MCP server with stdio transport — register this in your MCP client |
| `McpEngramMemory.Core` | NuGet Library | Core engine (vector index, graph, clustering, lifecycle, persistence) — use this to embed the memory engine in your own application |

```
src/
  McpEngramMemory/              # MCP server (Program.cs + Tool classes)
  McpEngramMemory.Core/         # Core library
    Models/                     # CognitiveEntry, SearchResults, HnswSnapshot, ToolError, etc.
    Services/
      CognitiveIndex.cs         # Thin facade: CRUD, locking, delegates to engines below
      NamespaceStore.cs         # Namespace-partitioned storage with lazy loading
      PhysicsEngine.cs          # Gravitational force re-ranking
      Retrieval/                # Search pipeline
        VectorMath.cs           #   SIMD-accelerated dot product & norm
        VectorSearchEngine.cs   #   Two-stage Int8 screening + FP32 reranking
        HnswIndex.cs            #   HNSW approximate nearest neighbor index
        HybridSearchEngine.cs   #   Adaptive RRF + cascade retrieval for large namespaces
        BM25Index.cs            #   Keyword search with Porter stemming & compound tokenization
        SynonymExpander.cs      #   Query-time domain synonym expansion (98 mappings)
        DocumentEnricher.cs     #   Store-time keyword enrichment (47 reverse maps)
        PorterStemmer.cs        #   Lightweight Porter stemmer (steps 1-3)
        QueryExpander.cs        #   IDF-based query expansion + pseudo-relevance feedback
        TokenReranker.cs        #   Token-overlap reranker (implements IReranker)
        DiversityReranker.cs    #   Cluster-aware MMR diversity reranking
        VectorQuantizer.cs      #   Int8 scalar quantization
        IReranker.cs            #   Pluggable reranker interface
      Graph/
        KnowledgeGraph.cs       #   Directed graph with adjacency lists
      Intelligence/
        ClusterManager.cs       #   Semantic cluster CRUD + centroid computation
        AccretionScanner.cs     #   DBSCAN density scanning + reversible collapse
        DuplicateDetector.cs    #   Pairwise cosine similarity duplicate detection
        AutoSummarizer.cs       #   TF-IDF keyword extraction for cluster summaries
        AccretionBackgroundService.cs
      Lifecycle/
        LifecycleEngine.cs      #   Decay, state transitions, deep recall
        DecayBackgroundService.cs
      Experts/
        ExpertDispatcher.cs     #   Semantic routing to expert namespaces
        DebateSessionManager.cs #   Debate session state + alias mapping
      Synthesis/
        SynthesisEngine.cs      #   Map-reduce synthesis via Ollama SLM
        OllamaClient.cs         #   Local Ollama HTTP client
      SpreadingActivationService.cs #   Collins & Loftus graph-coupled activation
      Evaluation/
        BenchmarkRunner.cs      #   IR quality benchmarks
        MetricsCollector.cs     #   Operational metrics + percentiles
      Storage/
        IStorageProvider.cs     #   Storage abstraction interface
        PersistenceManager.cs   #   JSON file backend with debounced writes
        SqliteStorageProvider.cs #   SQLite backend with WAL mode + busy_timeout
      Sharing/
        NamespaceRegistry.cs    #   Multi-agent namespace ownership & permissions
tests/
  McpEngramMemory.Tests/        # xUnit tests (842 tests across 46 test files)
.github/
  workflows/
    ci.yml                      # Build + test on push/PR (excludes MSA)
    benchmark.yml               # Nightly MSA benchmarks at 6 AM UTC
benchmarks/
  baseline-v1.json              # Sprint 1 baseline (2026-03-07)
  baseline-paraphrase-v1.json
  baseline-multihop-v1.json
  baseline-scale-v1.json
  2026-03-10-ablation/          # First ONNX ablation study (10 configs across 5 datasets x 4 modes)
  2026-03-20/                   # Day 10 stability test (12 configs + operational metrics)
  ideas/                        # Benchmark proposals and analysis
```

## NuGet Package

The core engine is available as a NuGet package for use in your own .NET applications.

```bash
dotnet add package McpEngramMemory.Core --version 0.6.1
```

### Library Usage

```csharp
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;

// Create services
var persistence = new PersistenceManager();
var embedding = new OnnxEmbeddingService();
var index = new CognitiveIndex(persistence);
var graph = new KnowledgeGraph(persistence, index);
var clusters = new ClusterManager(index, persistence);
var lifecycle = new LifecycleEngine(index, persistence);

// Store a memory
var vector = embedding.Embed("The capital of France is Paris");
var entry = new CognitiveEntry("fact-1", vector, "default", "The capital of France is Paris", "facts");
index.Upsert(entry);

// Search by text
var queryVector = embedding.Embed("French capital");
var results = index.Search(queryVector, "default", k: 5);
```

## Tech Stack

- .NET 8/9/10, C#
- [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) 1.0.0
- [FastBertTokenizer](https://www.nuget.org/packages/FastBertTokenizer) 0.4.67 (WordPiece tokenization)
- [Microsoft.ML.OnnxRuntime](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime) 1.17.0 (ONNX model inference)
- [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite) 8.0.11 (SQLite storage backend)
- [bge-micro-v2](https://huggingface.co/TaylorAI/bge-micro-v2) ONNX model (384-dimensional vectors, MIT license, downloaded at build time)
- Microsoft.Extensions.Hosting 8.0.1
- xUnit (tests)
