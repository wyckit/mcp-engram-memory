# Testing

[< Back to README](../README.md)

47 test files with 850 test cases (xUnit).

## Running Tests

```bash
cd mcp-engram-memory
dotnet test
```

## Test Files

| Test File | Tests | Focus |
|-----------|-------|-------|
| BenchmarkRunnerTests.cs | 106 | IR metrics, 17 benchmark datasets, 56-combination regression baseline, ONNX benchmarks, ablation study |
| CognitiveIndexTests.cs | 43 | Vector search, lifecycle filtering, persistence, memory limits |
| RetrievalImprovementTests.cs | 40 | Synonym expansion, document enrichment, Porter stemming, BM25 semantic gate, category boost, auto-PRF, adaptive RRF |
| IntelligenceTests.cs | 39 | Duplicate detection, contradictions, reversible collapse, decay tuning, merge |
| HierarchicalRoutingTests.cs | 36 | HMoE domain tree: node creation, parent-child linking, tree walk routing, flat fallback, auto-classification |
| InvariantTests.cs | 27 | Structural invariants across JSON and SQLite backends |
| MultiAgentTests.cs | 26 | Namespace registry, ownership, permissions, cross-search, sharing |
| KnowledgeGraphTests.cs | 20 | Edge operations, graph traversal, batch edge creation, edge transfer |
| CoreMemoryToolsTests.cs | 20 | Store, search, delete memory tool endpoints |
| SqliteStorageProviderTests.cs | 19 | SQLite backend: CRUD, persistence, concurrent access, WAL mode |
| PhysicsEngineTests.cs | 19 | Mass computation, gravitational force, slingshot |
| AccretionScannerTests.cs | 18 | DBSCAN clustering, pending collapses |
| DebateToolsTests.cs | 17 | Debate tools: validation, cold-start, expert retrieval, resolve lifecycle, E2E |
| ClusterManagerTests.cs | 16 | Cluster CRUD, centroid operations, membership transfer |
| ExpertToolsTests.cs | 15 | dispatch_task/create_expert: validation, routing, context retrieval, E2E |
| ExpertDispatcherTests.cs | 15 | Expert creation, routing hits/misses, threshold handling |
| HnswIndexTests.cs | 20 | HNSW index: add/search/remove, high-dimensional recall, rebuild, snapshot/restore round-trip |
| DebateSessionManagerTests.cs | 14 | Session management: alias registration, resolution, TTL purge |
| QueryExpanderTests.cs | 14 | IDF-based query expansion, BM25 compound tokenization |
| VectorQuantizerTests.cs | 13 | Int8 quantization/dequantization, SIMD dot product, cosine preservation |
| NamespaceCleanupTests.cs | 13 | Namespace deletion cascade, purge_debates, JSON and SQLite backends |
| CompositeToolsTests.cs | 12 | Composite tools: remember, recall, reflect — auto-dedup, auto-linking |
| LifecycleEngineTests.cs | 12 | State transitions, deep recall, decay cycles |
| FeedbackTests.cs | 11 | Agent feedback: energy boost/suppress, state transitions |
| AutoSummarizerTests.cs | 9 | Auto-summarization and cluster summary generation |
| RegressionTests.cs | 9 | Integration and edge-case scenarios |
| PersistenceManagerTests.cs | 19 | JSON serialization, debounced saves, checksums, storage version validation, HNSW snapshot persistence |
| FloatArrayBase64ConverterTests.cs | 9 | Base64 serialization roundtrip, legacy JSON array reading |
| QuantizedSearchTests.cs | 8 | Two-stage search pipeline, lifecycle-driven quantization |
| MetricsCollectorTests.cs | 8 | Latency recording, percentile computation |
| MaintenanceToolsTests.cs | 7 | Rebuild embeddings, compression stats |
| ChecksumTests.cs | 7 | SHA-256 persistence checksums, crash recovery, storage version persistence |
| AccretionToolsTests.cs | 7 | Accretion tool functionality |
| AutoSummarizeIntegrationTests.cs | 5 | Auto-summarize integration with clustering pipeline |
| DecayBackgroundServiceTests.cs | 2 | Background service decay cycles |
| AccretionBackgroundServiceTests.cs | 2 | Background service lifecycle |
| EmbeddingWarmupServiceTests.cs | 2 | Embedding warmup startup behavior |
| AdminToolsTests.cs | 9 | GetMemory, CognitiveStats, PurgeDebates tool endpoints |
| BenchmarkToolsTests.cs | 8 | RunBenchmark, GetMetrics, ResetMetrics tool endpoints |
| ClusterToolsTests.cs | 12 | CreateCluster, UpdateCluster, StoreClusterSummary, GetCluster, ListClusters tool endpoints |
| GraphToolsTests.cs | 15 | LinkMemories, UnlinkMemories, GetNeighbors, TraverseGraph tool endpoints |
| IntelligenceToolsTests.cs | 14 | DetectDuplicates, FindContradictions, UncollapseCluster, ListCollapseHistory, MergeMemories tool endpoints |
| LifecycleToolsTests.cs | 13 | PromoteMemory, DeepRecall, MemoryFeedback, DecayCycle, ConfigureDecay tool endpoints |
| MultiAgentToolsTests.cs | 12 | CrossSearch, ShareNamespace, UnshareNamespace, ListShared, WhoAmI tool endpoints |
| SynthesisToolsTests.cs | 2 | SynthesizeMemories, GetContextBlock tool endpoints |
| DiversityRerankerTests.cs | 11 | Cluster-aware MMR diversity reranking: selection, cluster/category penalties, edge cases |
| VisualizationToolsTests.cs | 10 | GetGraphSnapshot tool: node/edge serialization, filter options, stats accuracy |
