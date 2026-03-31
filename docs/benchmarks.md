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
  runner/                             # Benchmark execution infrastructure
  ideas/                              # Benchmark proposals and analysis
```

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
| ambiguity-v1 | hybrid | — | — | — | Ambiguous query disambiguation |
| distractor-v1 | hybrid | — | — | — | Noise resistance with grade-0 distractors |
| specificity-v1 | hybrid | — | — | — | Precise vs. broad retrieval |
| scale-v1 (extended) | hybrid | — | — | — | Large namespace stress test |
| contamination-v1 | hybrid | — | — | — | Cross-domain contamination resistance |
| cluster-summary-v1 | hybrid | — | — | — | Cluster summary retrieval quality |
| physics-v1 | hybrid | — | — | — | Physics engine domain retrieval |

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
