# Benchmarks

[< Back to README](../README.md)

Benchmark results are stored in `benchmarks/` organized by date. Each run captures IR quality metrics (Recall@K, Precision@K, MRR, nDCG@K) and latency percentiles across 17 datasets and 4 search modes.

## Directory Structure

```
benchmarks/
  baseline-v1.json                    # Original Sprint 1 baseline (2026-03-07)
  baseline-paraphrase-v1.json
  baseline-multihop-v1.json
  baseline-scale-v1.json
  2026-03-10-ablation/                # First ONNX ablation study (10 configs)
  2026-03-20/                         # Day 10 stability test (12 configs + ops)
  2026-04-16/                         # Post-cleanup stability run (34 configs: 12 datasets × up to 4 modes)
  runner/                             # Benchmark execution infrastructure
  ideas/                              # Benchmark proposals and analysis
```

## Key Findings (2026-04-16 — post-cleanup run)

Full test suite: **850 passed, 0 failed** (net8.0). Architecture cleanup run after dead code removal, bug fixes, and `DeleteMemory` signature correction. Zero regression from v0.6.1. 34 benchmark result files across 12 datasets.

| Dataset | Best Mode | Recall@K | MRR | nDCG@K | Notes |
|---------|-----------|----------|-----|--------|-------|
| default-v1 (25 seeds) | vector_rerank | **0.900** | 1.000 | **0.956** | Stable; nDCG +0.003 vs March 20 run |
| scale-v1 (80 seeds) | hybrid (recall) / vector_rerank (nDCG) | **0.745** / 0.734 | 0.975 / 0.983 | 0.856 / **0.884** | Run variance vs v0.5.4 doc; all regression thresholds pass |
| paraphrase-v1 (25 seeds) | vector | 0.944 | 1.000 | 0.964 | March 2026 baseline |
| multihop-v1 (25 seeds) | vector | 0.939 | 1.000 | 0.952 | March 2026 baseline |
| realworld-v1 (30 seeds) | hybrid | 0.792 | 0.883 | 0.835 | March 2026 baseline |
| compound-v1 (20 seeds) | hybrid | 0.900 | 0.978 | 0.937 | March 2026 baseline |
| ambiguity-v1 (24 seeds) | vector_rerank | **0.922** | 1.000 | **0.941** | Stable vs March 25 baseline |
| distractor-v1 (22 seeds) | vector_rerank / hybrid_rerank | 0.737 | 1.000 | 0.988 | March 25 baseline |
| specificity-v1 (30 seeds) | vector_rerank | **0.919** | 0.972 | **0.905** | Stable vs March 25 baseline |
| contamination-v1 (15 seeds) | vector / vector_rerank | **0.972** | 0.958 | **0.917** | Strong cross-domain resistance; all modes ≥ 0.972 recall |
| physics-v1 (20 seeds) | hybrid_rerank | **1.000** | 1.000 | **0.917** | Perfect recall all modes; hybrid_rerank best nDCG |
| lifecycle-v1 (25 seeds) | vector_rerank | 0.666 | 0.867 | 0.655 | Lifecycle-state-aware retrieval; lower due to STM/LTM/archived split |
| msa-multihop-v1 (30 seeds) | hybrid | **0.800** | 1.000 | **0.900** | MSA multi-hop reasoning; hybrid best across all metrics |
| msa-coldstart-v1 (20 seeds) | vector | **0.815** | 0.900 | **0.855** | Cold-start namespace scenario |
| scale-v2 (10000 seeds) | hybrid_rerank | **0.614** | 0.708 | **0.625** | Extreme-scale stress test (10× scale-v1); redesigned gold sets (easy K=50, medium K=10, hard K=20); hybrid 0.524, vector 0.460 |
| disambiguation-v1 (24 seeds) | hybrid | — | — | — | Dense-domain diversity; data in March 2026 baseline |
| cluster-summary-v1 | hybrid | — | — | — | Cluster summary retrieval quality |

## Key Findings (v0.6.0)

| Dataset | Best Mode | Recall@K | MRR | nDCG@K | Notes |
|---------|-----------|----------|-----|--------|-------|
| default-v1 (25 seeds) | vector_rerank | 0.900 | 1.000 | 0.953 | Reranker adds +0.033 recall |
| paraphrase-v1 (25 seeds) | vector | 0.944 | 1.000 | 0.964 | Strong paraphrase resilience |
| multihop-v1 (25 seeds) | vector | 0.939 | 1.000 | 0.952 | Highest precision (0.587) |
| scale-v1 (80 seeds) | hybrid | 0.771 | 1.000 | 0.903 | Cascade retrieval eliminates BM25 noise |
| realworld-v1 (30 seeds) | hybrid | 0.792 | 0.883 | 0.835 | Synonym expansion bridges vocab gaps |
| compound-v1 (20 seeds) | hybrid | 0.900 | 0.978 | 0.937 | Compound tokenization + stemming |
| disambiguation-v1 (24 seeds) | hybrid | — | — | — | Dense-domain diversity across 4 clusters |
| ambiguity-v1 | vector_rerank | 0.922 | 1.000 | 0.941 | Reranker provides +9.2% recall vs vector |
| distractor-v1 | vector_rerank / hybrid_rerank | 0.737 | 1.000 | 0.988 | High nDCG — correct answer always ranks #1 |
| specificity-v1 | vector_rerank | 0.919 | 0.972 | 0.905 | +5.0% recall vs pure vector |
| scale-v1 (extended) | hybrid | — | — | — | Large namespace stress test |
| contamination-v1 | hybrid | — | — | — | Cross-domain contamination resistance |
| cluster-summary-v1 | hybrid | — | — | — | Cluster summary retrieval quality |
| physics-v1 | hybrid | — | — | — | Physics engine domain retrieval |

## 2026-04-16 Architecture Cleanup (post-v0.6.1)

Code quality pass: dead code removal, bug fixes, no algorithm changes.

**Changes verified non-regressive:**
- `DeleteMemory` tool signature fix (removed `KnowledgeGraph`/`ClusterManager` from MCP args — was causing deserialization failure on the live server)
- `AccretionScanner` collapse/auto-summary ID collision fixed (timestamp → `Guid.NewGuid()`)
- `BenchmarkRunner` cleanup: per-entry delete loop → `DeleteAllInNamespace` (O(1) vs O(N))
- `RebuildEmbeddings` now calls `InvalidateHnswIndex` so stale HNSW is dropped after re-embedding
- `SqliteStorageProvider.DeleteNamespaceAsync` wrapped in a transaction (atomicity fix)
- `ExpertDispatcher.BuildTreeNode` now populates `ChildNodeIds` correctly from `nodeMap`
- `MultiAgentTools.list_shared` now returns shared namespaces only (was incorrectly returning `WhoAmIResult`)
- Removed dead methods: `VectorQuantizer.Dequantize`, `SynonymExpander.HasExpansions`/`GetSynonymMap`, `DocumentEnricher.GetReverseMap`, `NamespaceStore.HasBM25Namespace`/`RebuildBM25Namespace`, `ClusterManager.GetAllClusters`, `ExpertDispatcher.GetChildren(string)`
- `SynthesisEngine.MapWorkerAsync` exceptions now logged instead of silently swallowed
- `MaintenanceTools.compression_stats` savings ratio corrected (was including STM bytes in denominator)

**Result:** 850/850 tests pass across net8.0. default-v1 vector_rerank stable at 0.900/1.000/0.956 (within noise of prior runs).

## v0.6.0 Improvements

Cluster-aware MMR diversity reranking prevents search results from clustering around a single sub-topic. Activated via `diversity: true` on `search_memory` with configurable lambda (0.0 = pure diversity, 1.0 = pure relevance, default 0.5). ONNX concurrent inference removes the `SemaphoreSlim(1,1)` bottleneck with per-call `ArrayPool` scratch buffers. Namespace-partitioned locking switches from `UpgradeableReadLock` to `ReadLock` at 10 call sites for N-concurrent readers. Expert system consolidation links 6 orphaned experts and removes 36 dead/duplicate entries. 56-combination regression baseline now covers all 17 datasets x 4 modes.

## v0.5.5 Improvements

BM25 semantic gate filters keyword-only matches through semantic similarity before RRF fusion, eliminating noise from irrelevant BM25 hits. 52-combination regression baseline (13 datasets x 4 search modes) ensures no metric drops below Recall@K >= 0.20, MRR >= 0.20, nDCG@K >= 0.15. HNSW graph snapshots now persist to disk, avoiding O(N log N) rebuild cost on cold start.

## v0.5.4 Improvements

Porter stemming, expanded synonyms (98 mappings), cascade retrieval (for namespaces >= 50 entries), auto-PRF, and category-aware score boosting dramatically improved hybrid search at scale. scale-v1 hybrid recall jumped from 0.689 to 0.771 (+11.9%) with perfect MRR.

## Mode Selection Guide

Default to `hybrid` — it is now the best mode across most datasets thanks to cascade retrieval and synonym expansion. Use `vector` for minimal-latency queries. Use `hybrid_rerank` for maximum precision. The cascade mode automatically prevents BM25 noise in large namespaces (>= 50 entries) while preserving BM25 rescue for small namespaces. Add `diversity: true` for dense namespaces where results tend to cluster around one sub-topic.

## Seventeen Benchmark Datasets

Six core datasets: four covering generic CS topics (programming languages, data structures, ML, databases, networking, systems, security, DevOps), one real-world dataset modeled after actual cognitive memory entries (architecture decisions, bug fixes, code patterns, user preferences, lessons learned), and one compound tokenization dataset testing BM25 handling of hyphenated terms and vocabulary gaps.

Seven stress-test datasets (v0.5.5): ambiguity-v1 (ambiguous query disambiguation), distractor-v1 (noise resistance with grade-0 distractors), specificity-v1 (precise vs. broad retrieval), scale-v1 extended (large namespace stress), compound-v1 extended, contamination-v1 (cross-domain contamination resistance), and cluster-summary-v1 (cluster summary retrieval quality).

Four datasets added in v0.6.0: disambiguation-v1 (dense-domain diversity — 4 clusters × 5 seeds + 4 distractors, 10 queries measuring result diversity across semantically dense namespaces), and physics-v1 tested in 4 modes (vector, vector_rerank, hybrid, hybrid_rerank) for physics engine domain retrieval.

All datasets use a 0-3 relevance grade scale (3 = highly relevant). The 56-combination regression baseline covers all 17 datasets × 4 search modes (vector, hybrid, vector_rerank, hybrid_rerank) with minimum thresholds.
