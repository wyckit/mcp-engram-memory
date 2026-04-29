[< Back to README](../README.md)

# Services & Configuration

### Services

`CognitiveIndex` is a thin facade managing CRUD, locking, and memory limits. Search, hybrid search, and duplicate detection are delegated to stateless engines that operate on data snapshots.

| Service | Namespace | Description |
|---------|-----------|-------------|
| `CognitiveIndex` | `Services` | Thread-safe facade: CRUD, lifecycle state, access tracking, memory limits enforcement. Delegates search to engines below |
| `NamespaceStore` | `Services` | Namespace-partitioned storage with `ConcurrentDictionary`, per-namespace load locks (double-check pattern), lazy loading from disk, and BM25 indexing |
| `VectorSearchEngine` | `Retrieval` | Stateless k-NN search with HNSW ANN candidate generation (≥200 entries) or two-stage Int8 screening (≥30 entries) → FP32 exact reranking |
| `HnswIndex` | `Retrieval` | Hierarchical Navigable Small World graph for O(log N) approximate nearest neighbor search with soft deletion, compacting rebuild, and topology-only snapshot serialization for cold-start persistence |
| `HybridSearchEngine` | `Retrieval` | Adaptive RRF fusion with confidence-gated k parameter. Two modes: parallel RRF for small namespaces (<50 entries), cascade mode for large namespaces (BM25 boosts vector results up to 15% instead of introducing new candidates). Auto-escalation to hybrid when vector-only confidence is low |
| `BM25Index` | `Retrieval` | In-memory keyword search with TF-IDF scoring, Porter stemming for morphological normalization, and compound word tokenization (hyphen splitting + joining) |
| `SynonymExpander` | `Retrieval` | Query-time domain synonym expansion (98 mappings) bridging colloquial and technical vocabulary across security, ML, systems, networking, data/storage, and general CS domains |
| `DocumentEnricher` | `Retrieval` | Store-time keyword enrichment using reverse synonym mapping (47 entries). Auto-generates searchable keyword aliases so BM25 indexes both entry text and colloquial equivalents |
| `PorterStemmer` | `Retrieval` | Lightweight Porter stemmer implementing steps 1-3 (plurals, verb forms, derivational suffixes including custom `-tion` → `-t` normalization). "encrypting" and "encryption" both stem to "encrypt" |
| `QueryExpander` | `Retrieval` | IDF-based query term expansion with pseudo-relevance feedback (auto-PRF). PRF activates when hybrid top score is low (<0.015 RRF), extracting key terms from initial results to improve recall |
| `TokenReranker` | `Retrieval` | Token-overlap reranker implementing `IReranker` |
| `VectorMath` | `Retrieval` | SIMD-accelerated dot product and norm (static utility) |
| `VectorQuantizer` | `Retrieval` | Int8 scalar quantization: `Quantize`, `Dequantize`, SIMD `Int8DotProduct`, `ApproximateCosine` |
| `DuplicateDetector` | `Intelligence` | Pairwise cosine similarity duplicate detection. Above 256 candidates, switches to a three-pass spectral pre-filter via `EmbeddingSubspace`: project to a 64-dim subspace via randomized SVD over E^T E, scan in projection space at a widened threshold, confirm survivors with full FP32 cosine. Recall preserved against the original O(N²) scan; cost grows much more gracefully on large namespaces |
| `EmbeddingSubspace` | `Retrieval` | Builds a low-rank approximation of an embedding matrix via randomized SVD. Returns the right singular vector basis (V) plus per-row projections; consumed by `DuplicateDetector` and any future low-rank Gram-style operation. Stateless |
| `KnowledgeGraph` | `Graph` | In-memory directed graph with adjacency lists, bidirectional edge support, edge transfer, contradiction surfacing, and a monotonic `Revision` counter incremented on every edge mutation (consumed by `MemoryDiffusionKernel` for lock-free staleness detection) |
| `MemoryDiffusionKernel` | `Graph` | Per-namespace cache and operator for diffusing per-entry signals through the memory graph. Holds the top-K eigenbasis of the normalized Laplacian L = I − D^(-1/2) W D^(-1/2) built from positive-relation edges only (`parent_child`, `cross_reference`, `similar_to`, `elaborates`, `depends_on`; `contradicts` excluded so L stays PSD). `ApplySpectralFilter` is the primary verb. Consumed by spectral decay debt diffusion (`LifecycleEngine.RunDecayCycle`), sleep consolidation (`LifecycleEngine.RunConsolidationPass`), and spectral retrieval re-ranking (`SpectralRetrievalReranker`) |
| `RandomizedEigensolver` | `Graph` | Halko-Martinsson-Tropp randomized subspace iteration for top-K largest-magnitude eigenpairs of symmetric matrices supplied via a matrix-vector callback. Internal Modified Gram-Schmidt + cyclic Jacobi; falls through to direct dense Jacobi when m ≥ n. No external numerical dependency. Static utility |
| `AutoLinkScanner` | `Graph` | Periodic background pass that scans a namespace for high-cosine-similarity pairs and adds `similar_to` edges between them. Reuses `DuplicateDetector`'s spectral pre-filter at a looser threshold (default 0.85). Skips pairs with any pre-existing edge between them. Idempotent (re-scans don't duplicate). The graph builds itself from embedding similarity, giving the diffusion kernel and consolidation more topology to work with |
| `SpectralRetrievalReranker` | `Retrieval` | Re-ranks search results through the memory-diffusion kernel. Modes: `None` (passthrough), `Broad` (low-pass — boosts cluster-supported memories for thematic queries), `Specific` (high-pass — emphasizes per-entry deviation from cluster mean for precise queries) |
| `ClusterManager` | `Intelligence` | Semantic cluster CRUD with automatic centroid computation and membership transfer |
| `AccretionScanner` | `Intelligence` | DBSCAN-based density scanning with reversible collapse history (persisted to disk) |
| `AutoSummarizer` | `Intelligence` | TF-IDF keyword extraction for auto-generated cluster summaries |
| `LifecycleEngine` | `Lifecycle` | Activation energy computation, agent feedback reinforcement, per-namespace decay configs, decay cycles, sleep consolidation, and state transitions (STM/LTM/archived). When the memory-diffusion kernel is injected and a namespace qualifies, `RunDecayCycle` diffuses per-entry decay debt through the graph heat kernel before applying it, and `RunConsolidationPass` drives lifecycle transitions on the smoothed (cluster-aware) activation field |
| `PhysicsEngine` | `Services` | Gravitational force re-ranking with "Asteroid" (semantic) + "Sun" (importance) output |
| `BenchmarkRunner` | `Evaluation` | IR quality benchmark execution with Recall@K, Precision@K, MRR, nDCG@K scoring |
| `AgentOutcomeBenchmarkRunner` | `Evaluation` | Proxy task-style benchmark comparing no-memory, transcript replay, vector memory, and full Engram memory policies |
| `LiveAgentOutcomeBenchmarkRunner` | `Evaluation` | Real-model task benchmark that injects condition-specific memory context and grades structured answers deterministically |
| `MetricsCollector` | `Evaluation` | Thread-safe operational metrics with P50/P95/P99 latency percentiles |
| `DebateSessionManager` | `Experts` | Volatile in-memory session state for debate workflows with integer alias mapping and 1-hour TTL auto-purge |
| `ExpertDispatcher` | `Experts` | Semantic routing engine with flat and hierarchical (HMoE) modes — maps queries to specialized expert namespaces via cosine similarity through a 3-level domain tree (root → branch → leaf). Zero LLM API calls |
| `NamespaceRegistry` | `Sharing` | Manages namespace ownership and sharing permissions for multi-agent memory sharing |
| `PersistenceManager` | `Storage` | JSON file-based `IStorageProvider` with debounced async writes, SHA-256 checksums, crash recovery, storage version validation, and HNSW snapshot persistence |
| `SqliteStorageProvider` | `Storage` | SQLite-based `IStorageProvider` with WAL mode, busy_timeout for multi-process safety, schema migration framework, incremental per-entry writes, and HNSW snapshot persistence |
| `SqlServerStorageProvider` | `Storage` | Microsoft SQL Server-backed `IStorageProvider`. Configurable schema (default `dbo`), `MERGE`-based upserts, transactional writes, incremental per-entry persistence, HNSW snapshots stored as `hnsw_{ns}` keys |
| `DiversityReranker` | `Retrieval` | Cluster-aware Maximal Marginal Relevance (MMR) reranking — spreads results across sub-topics using cluster and category penalties. Activated via `diversity: true` on search. Configurable lambda trade-off (0.0 = pure diversity, 1.0 = pure relevance, default 0.5) |
| `SpreadingActivationService` | `Services` | Collins & Loftus spreading activation model for graph-coupled energy transfer with depth-3 recursive propagation and cluster-based pre-warming |
| `SynthesisEngine` | `Synthesis` | Map-reduce synthesis via Ollama for dense reasoning over large memory sets without expanding context windows. Paired with `OllamaClient` for local SLM inference |
| `OnnxEmbeddingService` | `Services` | 384-dimensional vector embeddings via bge-micro-v2 ONNX model with FastBertTokenizer. Fully concurrent inference with per-call ArrayPool scratch buffers (no semaphore bottleneck) |
| `HashEmbeddingService` | `Services` | Deterministic hash-based embeddings for testing/CI (no model dependency) |

### Background Services

| Service | Interval | Description |
|---------|----------|-------------|
| `EmbeddingWarmupService` | Startup | Warms up the embedding model on server start so first queries are fast |
| `DecayBackgroundService` | 15 minutes | Runs activation energy decay on all namespaces using stored per-namespace configs. Spectral diffusion of decay debt fires automatically on qualifying namespaces |
| `AccretionBackgroundService` | 30 minutes | Scans all namespaces for dense LTM clusters needing summarization |
| `DiffusionKernelWarmupService` | 30 minutes (after 5s startup delay) | Pre-computes the diffusion basis for all qualifying namespaces, so the first decay/consolidation/retrieval call after startup doesn't pay the eigendecomposition cost on the foreground path |
| `AutoLinkBackgroundService` | 6 hours (after 15-min startup delay) | Sweeps all non-system namespaces, runs `AutoLinkScanner`, adds `similar_to` edges between high-similarity pairs. Per-namespace opt-out via `DecayConfig.EnableAutoLink` |
| `ConsolidationBackgroundService` | 24 hours (after 10-min startup delay) | Runs `LifecycleEngine.RunConsolidationPass` across every namespace. Topology-driven STM→LTM promotion and LTM→archived archival without LLM involvement |

### Models

| Model | Description |
|-------|-------------|
| `CognitiveEntry` | Core memory entry with vector, text, keywords (auto-enriched), metadata, lifecycle state, and activation energy |
| `QuantizedVector` | Int8 quantized vector with `sbyte[]` data, min/scale for reconstruction, and precomputed self-dot product |
| `FloatArrayBase64Converter` | JSON converter for `float[]` — writes Base64 strings, reads both Base64 and legacy JSON arrays for backwards compatibility |
| `SearchRequest` | Search request model with options for hybrid, rerank, diversity (MMR), expand query, explain, physics, and summary-first modes |
| `ExplainedSearchResult` | Extended search result with full retrieval diagnostics (cosine, physics, lifecycle breakdown) |
| `HnswSnapshot` | Topology-only HNSW graph snapshot for cold-start persistence (node IDs, levels, connections — no vectors) |
| `ToolError` | Standard `{ status, error }` structured error response for consistent MCP tool error reporting |

### Searchable Compression

Vectors use a lifecycle-driven compression pipeline:

- **STM entries**: Full FP32 precision for maximum search accuracy
- **LTM/archived entries**: Auto-quantized to Int8 (asymmetric min/max → [-128, 127]) on state transition
- **HNSW index**: Namespaces with 200+ entries auto-build an HNSW graph for O(log N) approximate nearest neighbor candidate generation
- **Two-stage search**: Namespaces with 30–199 entries use Int8 screening (top k×5 candidates) followed by FP32 exact cosine reranking
- **SIMD acceleration**: `Int8DotProduct` uses `System.Numerics.Vector<T>` for portable hardware-accelerated dot products (sbyte→short→int widening pipeline)
- **Base64 persistence**: Vectors are serialized as Base64 strings instead of JSON number arrays, reducing disk usage by ~60%. Legacy JSON arrays are still readable for backwards compatibility.

### Persistence

Two storage backends are available, selectable via environment variable:

**JSON file backend** (default):
- Data stored in a `data/` directory as JSON files
- `{namespace}.json` — entries with Base64-encoded vectors (per namespace)
- `{namespace}.hnsw.json` — HNSW graph topology snapshots (per namespace, vectors reconstructed from entries on load)
- `_edges.json` — graph edges (global)
- `_clusters.json` — semantic clusters (global)
- `_collapse_history.json` — reversible collapse records (global)
- `_decay_configs.json` — per-namespace decay configurations (global)
- Writes are debounced (500ms default) with SHA-256 checksums for crash recovery

**SQLite backend** (`MEMORY_STORAGE=sqlite`):
- Single `memory.db` file with WAL mode for concurrent read/write
- `PRAGMA busy_timeout=5000` on every connection — retries on lock contention for up to 5 seconds, enabling multi-process access to the same database
- Tables: `entries`, `edges`, `clusters`, `collapse_history`, `decay_configs`, `global_data` (HNSW snapshots stored as `hnsw_{ns}` keys), `schema_version`
- Automatic schema migrations (v1→v2 adds `lifecycle_state` column with backfill)
- Suitable for higher-throughput or multi-process scenarios

**SQL Server backend** (`MEMORY_STORAGE=sqlserver`):
- Bundled into the `McpEngramMemory.Core` package via `Microsoft.Data.SqlClient`.
- Configurable schema via `MEMORY_SQLSERVER_SCHEMA` (default `dbo`); the schema is created automatically if missing. Schema name is validated against `^[A-Za-z_][A-Za-z0-9_]*$` to prevent injection.
- Tables (all under the chosen schema): `entries`, `global_data` (holds edges, clusters, collapse history, decay configs, and HNSW snapshots), `schema_version`
- Upserts use `MERGE … WITH (HOLDLOCK)` for safe concurrent writes
- Connection string supplied via `MEMORY_SQLSERVER_CONNECTION` — auth (SQL, integrated, Azure AD) is whatever the connection string specifies

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `MEMORY_TOOL_PROFILE` | `full` | Tool profile: `minimal` (16 tools), `standard` (41 tools), `full` (65 tools) |
| `AGENT_ID` | `default` | Agent identity for multi-agent sharing. Set unique ID per agent instance to enable namespace ownership and permissions |
| `MEMORY_STORAGE` | `json` | Storage backend: `json`, `sqlite`, or `sqlserver` |
| `MEMORY_SQLITE_PATH` | `data/memory.db` | SQLite database file path (only when `MEMORY_STORAGE=sqlite`) |
| `MEMORY_SQLSERVER_CONNECTION` | _required_ | SQL Server connection string (only when `MEMORY_STORAGE=sqlserver`) |
| `MEMORY_SQLSERVER_SCHEMA` | `dbo` | SQL Server schema name (only when `MEMORY_STORAGE=sqlserver`) |
| `MEMORY_MAX_NAMESPACE_SIZE` | unlimited | Maximum entries per namespace |
| `MEMORY_MAX_TOTAL_COUNT` | unlimited | Maximum total entries across all namespaces |
