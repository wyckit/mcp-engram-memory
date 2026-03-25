# Changelog

All notable changes to this project will be documented in this file.

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
