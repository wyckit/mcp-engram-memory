# Changelog

All notable changes to this project will be documented in this file.

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
