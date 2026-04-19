# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]

### Added
- **MRCR v2 (8-needle) long-context benchmark**: A/B harness that drives the Claude Code CLI (`claude -p`) via the user's subscription — no Anthropic API key required.
  - `run_mrcr_benchmark` runs two arms on the same probes: `full_context` (entire conversation in prompt) vs. `engram_retrieval` (hybrid BM25+vector search returns top-K chunks).
  - `compare_mrcr_artifacts` reports per-arm similarity / pass-rate deltas and the change in prompt-token reduction ratio.
  - Scoring uses local `bge-micro-v2` cosine similarity — matches the MRCR paper's metric and stays API-cost-free.
  - New `claude-cli` provider in `AgentOutcomeModelClientFactory` shells out to `claude -p --model <name>` with prompts piped over stdin (bypasses shell-argument-length limits on 128K contexts).
  - Dataset loader + HF download recipe in `benchmarks/datasets/mrcr-v2/README.md` (dataset is gitignored).
  - Methodology, usage, and interpretation guide in `docs/benchmarks-mrcr.md`.

## [0.7.0] - 2026-04-16

### Added
- **Memory Graph Visualizer**: `get_graph_snapshot` MCP tool returns a `GraphSnapshot` with nodes, typed edges, cluster memberships, and corpus stats. Companion D3.js viewer (`visualization/memory-graph.html`) renders a force-directed graph with Obsidian/Nexus dark aesthetic: gold entry points, cyan LTM, teal STM, gray archived.
- **Visualizer Features**: Zoom 0.01–6×, right-click drag to rotate, filter pills (STM / LTM / Archived / Connected-only), fractal density overlay below zoom k=0.20 (leaf-only rendering anchored to node bounding box), search bar with live node highlighting (matches pulse gold, non-matches dim to 4%), ›/‹ buttons + Enter/Shift+Enter to cycle through results with a 1/N counter, Esc or × to clear.
- `get_graph_snapshot` registered in `full` profile only (`VisualizationTools.cs`). 10 new unit tests.
- Benchmark suite expanded to 34 result files across 12 datasets in `benchmarks/2026-04-16/`.

### Changed
- **Architecture Cleanup**: Removed 12 dead-code and bug items — duplicate `CosineSimilarity` / `DotProduct` overloads on `VectorMath`, stale `ScaleTunedTests.cs` test infrastructure, obsolete lifecycle fields (`_lastDecay`, `_lastSave` serialization remnants), and corrected `DeleteMemory` method signature mismatch.
- Total tests: **850** (up from 842). All 850 pass across net8.0.

## [0.6.1] - 2026-03-31

### Fixed
- **SQLite Multi-Instance Lock Contention**: Added `PRAGMA busy_timeout=5000` to `SqliteStorageProvider.OpenConnection()` and `DeleteNamespaceAsync()`. When multiple MCP server instances share the same `memory.db`, the second instance's schema initialization DDL would fail with `SQLITE_BUSY` after a 30-second hang because the first instance held the WAL write lock. SQLite now retries for up to 5 seconds on lock contention instead of immediately failing. Also fixed `DeleteNamespaceAsync()` bypassing `OpenConnection()` and missing the `busy_timeout` and `synchronous=NORMAL` pragmas.

## [0.6.0] - 2026-03-31

### Added
- **Cluster-Aware MMR Diversity Reranking**: New `DiversityReranker` applies Maximal Marginal Relevance with cluster and category penalties to spread search results across sub-topics. Activated via `diversity: true` on `search_memory`. Configurable lambda trade-off (0.0 = pure diversity, 1.0 = pure relevance, default 0.5).
- **GitHub Actions CI/CD**: `ci.yml` pipeline on push/PR to main — builds, tests (excluding MSA), and packs NuGet on all 3 TFMs. `benchmark.yml` runs nightly MSA benchmarks at 6 AM UTC with manual dispatch.
- **`disambiguation-v1` benchmark**: Dense-domain disambiguation dataset — 4 clusters × 5 seeds + 4 distractors, 10 queries (broad/cross-cluster/narrow) measuring result diversity across semantically dense namespaces.
- **Spreading Activation Service**: Collins & Loftus spreading activation model for graph-coupled energy transfer with depth-3 recursive propagation and cluster-based pre-warming.
- **SLM Synthesis Engine**: Map-reduce synthesis via Ollama for dense reasoning over large memory sets without expanding context windows.
- **`get_context_block` tool**: Prompt-cache-aware context assembly for LLM consumption.
- **`synthesize_memories` tool**: Map-reduce synthesis via Ollama for dense reasoning over large memory sets.
- **`physics-v1` in MSA benchmark suite**: 4 modes (vector, vector_rerank, hybrid, hybrid_rerank) added to nightly MSA benchmarks.
- 108 new tests (total: 842 across 3 frameworks, up from 734).
- Total tools: 52 (up from 50).

### Changed
- **ONNX Concurrent Inference**: Removed `SemaphoreSlim(1,1)` bottleneck. All scratch buffers (input IDs, attention mask, token type IDs, shape) moved to per-call `ArrayPool` allocations. `InferenceSession.Run()` now runs fully concurrent.
- **Namespace-Partitioned Locking**: `NamespaceStore` converted from `Dictionary` to `ConcurrentDictionary` for all 4 internal collections. Per-namespace load locks with double-check pattern for safe lazy loading. `CognitiveIndex` read paths switched from `UpgradeableReadLock` (1 thread) to `ReadLock` (N concurrent threads) at 10 call sites.
- **Expert System Consolidation**: Linked 6 orphaned high-use experts to domain tree parent nodes. Deleted 36 dead/duplicate expert entries. Broadened `information_retrieval` branch node persona description for improved hierarchical routing.
- **MCP Tool Test Coverage**: 8 previously untested tool classes now have dedicated test files (AdminTools, BenchmarkTools, ClusterTools, GraphTools, IntelligenceTools, LifecycleTools, MultiAgentTools, SynthesisTools) with 85 new tests.

## [0.5.5] - 2026-03-25

### Added
- **HNSW Graph Persistence**: HNSW indices are now serialized to disk as topology-only snapshots (`{ns}.hnsw.json` for JSON backend, `global_data` for SQLite). Vectors are reconstructed from namespace entries on load, avoiding the O(N log N) rebuild cost on cold start for namespaces with 200+ entries. Snapshots are saved alongside namespace data and cleaned up on namespace deletion.
- **`store_batch` tool**: Bulk-store multiple entries in a single write-lock. Faster than repeated `store_memory` calls for batch imports. Each entry gets contextual prefix embedding. Returns stored count and duplicate warnings.
- **Storage Version Validation (JSON)**: `PersistenceManager` now validates `storageVersion` on load — rejects files from newer server versions (forward-compatibility guard), runs sequential migrations for older versions. Follows the same `RunMigrations()` pattern as `SqliteStorageProvider`. Current version bumped to v2.
- **`ToolError` structured error type**: Standard `{ status, error }` record for consistent MCP tool error responses. `FromException()` hides internal stack traces.
- **BM25 Semantic Gate**: Hybrid search now gates BM25 candidates through semantic similarity before RRF fusion, eliminating noise from keyword-only matches that are semantically irrelevant.
- **52-combination Regression Baseline**: Theory test covering all 13 IR benchmark datasets x 4 search modes (vector, hybrid, vector_rerank, hybrid_rerank) with minimum thresholds for Recall@K >= 0.20, MRR >= 0.20, nDCG@K >= 0.15.
- 7 new IR benchmark datasets: ambiguity-v1, distractor-v1, specificity-v1, scale-v1, compound-v1, contamination-v1, cluster-summary-v1.
- 125 new tests (total: 734 across 3 frameworks, up from 609).

### Changed
- **Tool Description Audit**: All 50 MCP tool descriptions rewritten for better LLM routing — shorter top-level descriptions (under 200 chars), "when to use" guidance, routing hints between similar tools (e.g., `remember` vs `store_memory`, `recall` vs `search_memory`).
- **Scale Recall Tuning**: Hybrid search cascade and RRF fusion parameters retuned for improved recall on large namespaces (500+ entries).
- Fixed stack trace leak in `ClusterTools.CreateCluster` error responses.
- Total tools: 50 (up from 49).

## [0.5.4] - 2026-03-22

### Added
- **Porter Stemming for BM25**: Lightweight Porter stemmer (steps 1-3) normalizes morphological variants in the BM25 tokenizer. "encrypting", "encryption", and "encrypted" all stem to "encrypt", dramatically improving keyword recall. Handles plurals (-s/-es/-ies), verb forms (-ed/-ing), and derivational suffixes (-tion/-ation/-ize/-ness/-ful/-ive/-al).
- **Expanded Synonym Maps**: 60+ new synonym mappings covering security (encrypt→TLS/cipher), ML (sequence→RNN/LSTM), systems (monitoring→observability/Prometheus), networking (protocol→HTTP/TCP), data/storage (cache→Redis/CDN), and general CS vocabulary bridges. Corresponding reverse maps added to DocumentEnricher for bidirectional vocabulary bridging.
- **Two-Stage Cascade Retrieval**: For namespaces ≥50 entries, hybrid search switches from parallel RRF fusion to cascade mode — BM25 boosts vector results (up to 15%) instead of introducing new candidates, eliminating BM25 noise at scale. Smaller namespaces retain full RRF fusion.
- **Auto-PRF Engagement**: Pseudo-Relevance Feedback automatically activates when hybrid search top score is low (<0.015 RRF). Extracts key terms from initial results and re-searches for improved recall. Only used if PRF improves the top score.
- **Category-Aware Score Boost**: 8% score boost when query tokens overlap with entry categories, improving disambiguation at scale (e.g., "security" query boosts entries categorized as "security").
- 19 new tests (Porter stemmer, BM25 stemming integration, expanded synonyms, expanded enrichment, category boost, auto-PRF).
- Total tests: 609 (up from 590).

## [0.5.3] - 2026-03-22

### Added
- **Adaptive RRF Fusion**: Confidence-gated hybrid search — dynamically adjusts RRF k parameter based on vector search confidence. High confidence (>0.70) suppresses BM25 noise, low confidence (<0.50) amplifies BM25 rescue. Eliminates hybrid regression on clean vector matches.
- **Synonym Expansion Layer**: Query-time domain synonym mapping bridges vocabulary gaps between colloquial user queries and technical terminology. Directly fixes rw-q15 and c-q07 semantic gaps (e.g., "maintenance cleanup" → "accretion decay collapse").
- **Document Enrichment**: Auto-generates keyword aliases at store time using reverse synonym mapping. BM25 indexes both entry text and enrichment keywords for improved recall on informal queries.
- **Auto-Escalation Search**: Vector-only searches with low confidence (top score <0.50) automatically escalate to hybrid+synonym search, picking the best strategy without caller configuration.
- **`Keywords` field on `CognitiveEntry`**: Searchable keyword aliases for document enrichment. BM25 indexes keywords alongside main text. Auto-populated on upsert via `DocumentEnricher`.
- 21 new retrieval improvement tests (synonym expansion, document enrichment, adaptive RRF, auto-escalation, Keywords integration).
- Total tests: 590 (up from 569).

## [0.5.2] - 2026-03-22

### Added
- **Multi-Agent Memory Sharing (Phase 6)**: Cross-agent namespace sharing with permission control. Set `AGENT_ID` env var per agent instance to enable namespace ownership and access control.
- **`cross_search` tool**: Search across multiple namespaces in a single call with Reciprocal Rank Fusion (RRF) merge. Results annotated with source namespace. Supports hybrid search and reranking.
- **`share_namespace` tool**: Grant another agent read or write access to a namespace you own.
- **`unshare_namespace` tool**: Revoke an agent's access to a namespace you own.
- **`list_shared` tool**: List all namespaces shared with the current agent.
- **`whoami` tool**: Return current agent identity and accessible namespaces summary.
- **`NamespaceRegistry` service**: Manages namespace ownership and sharing permissions. Stores permission data in `_system_sharing` namespace. Backward compatible — default agent has unrestricted access.
- **`SearchMultiple` method**: Cross-namespace search with per-namespace retrieval and RRF fusion in `CognitiveIndex`.
- 26 new multi-agent tests (registry permissions, cross-search, sharing/unsharing, agent identity, backward compatibility).
- Total tools: 49 (up from 44). Multi-agent tools included in all tool profiles.

## [0.5.1] - 2026-03-21

### Added
- **Hierarchical Expert Routing (HMoE)**: 3-level domain tree (root → branch → leaf) with coarse-to-fine semantic routing via cosine similarity. Supports 2-level and 3-level trees with automatic flat fallback. Zero LLM API calls — all routing uses local ONNX embeddings + SIMD dot products.
- **`get_domain_tree` tool**: Inspect the full expert hierarchy showing root domains, branches, and leaf experts.
- **`purge_debates` tool**: Clean up stale `active-debate-*` namespaces older than a configurable age with dry-run support.
- **Namespace cleanup infrastructure**: `DeleteNamespaceAsync` with cascade removal of entries, graph edges, and cluster memberships across JSON and SQLite backends.
- **`create_expert` enhancements**: `level` parameter (`root`, `branch`, `leaf`) and `parentNodeId` for hierarchical tree construction. **Auto-classification**: when `parentNodeId` is omitted for leaf experts, the system automatically scores the persona against all root and branch nodes and places the expert into the best-matching domain (`auto_linked` >= 0.82, `suggested` 0.60–0.82, `unclassified` < 0.60). Placement result is included in the response.
- **`dispatch_task` enhancements**: `hierarchical` parameter for tree-walk routing through domain nodes.
- **`link_to_parent` tool**: Link existing leaf experts to a parent node in the domain tree.
- 49 new tests (27 hierarchical routing + 9 auto-classification + 13 namespace cleanup), bringing total to 534.

## [0.4.1] - 2026-03-10

### Changed
- **NuGet Build Optimizations**: Embedded debug `.pdb` symbols internally inside the distributed DLL.
- **Embedded Sources**: Added embedded source link mapping so consuming code can cleanly step into `McpEngramMemory.Core` logic directly during debug sessions, offering an unparalleled developer experience.

## [0.4.0] - 2026-03-10

### Added
- **McpEngramMemory.Core NuGet Package**: Split the core memory engine into an independent `net8.0` library, extractable via the `McpEngramMemory.Core.csproj` target build. Allows consumers to integrate the vector index natively in-process without relying on MCP RPC endpoints.

## [0.3.0] - 2026-03-09

### Added
- **Tool profiles**: `MEMORY_TOOL_PROFILE` environment variable to control which tools are exposed (`minimal`, `standard`, `full`). Default: `full` for backward compatibility.
- **Docker support**: Dockerfile for containerized deployment.
- **Examples directory**: Ready-to-use MCP config files for Claude Code, VS Code/Copilot, Gemini CLI, and Codex.
- **Architecture diagram**: Mermaid diagram in README showing system layers.
- **Quickstart section**: 30-second setup guide at the top of README.
- This CHANGELOG.

## [0.2.0] - 2026-03-09

### Added
- Expert routing with `dispatch_task` and `create_expert` tools
- Debate workflow with `consult_expert_panel`, `map_debate_graph`, `resolve_debate`
- Intelligence tools: `detect_duplicates`, `find_contradictions`, `merge_memories`
- Reversible cluster collapse with `uncollapse_cluster` and `list_collapse_history`
- SQLite storage backend (`MEMORY_STORAGE=sqlite`)
- Memory limits via `MEMORY_MAX_NAMESPACE_SIZE` and `MEMORY_MAX_TOTAL_COUNT`
- Per-namespace decay configuration with `configure_decay`
- NuGet package `McpEngramMemory.Core` v0.2.0

### Changed
- Architecture decomposition: CognitiveIndex refactored to thin facade delegating to stateless engines
- Vector serialization switched to Base64 (60% disk reduction), with backward-compatible JSON array reading

## [0.1.0] - Initial Release

### Added
- Core memory storage with namespace isolation
- Vector search with cosine similarity (k-NN)
- Hybrid search: BM25 + vector via Reciprocal Rank Fusion
- Token-overlap reranking
- Knowledge graph with directed edges and BFS traversal
- Semantic clustering with DBSCAN-based accretion scanning
- Memory lifecycle management (STM → LTM → archived)
- Activation energy decay with background service
- Physics-based gravitational re-ranking
- Int8 scalar quantization with SIMD acceleration
- Two-stage search pipeline (Int8 screening → FP32 reranking)
- JSON file persistence with debounced writes and SHA-256 checksums
- IR quality benchmarks (Recall@K, Precision@K, MRR, nDCG@K)
- Operational metrics with P50/P95/P99 percentiles
- 397 test cases across 26 test files
