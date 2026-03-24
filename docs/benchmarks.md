# Benchmarks

[< Back to README](../README.md)

Benchmark results are stored in `benchmarks/` organized by date. Each run captures IR quality metrics (Recall@K, Precision@K, MRR, nDCG@K) and latency percentiles across 6 datasets and 4 search modes.

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

## Key Findings (v0.5.4)

| Dataset | Best Mode | Recall@K | MRR | nDCG@K | Notes |
|---------|-----------|----------|-----|--------|-------|
| default-v1 (25 seeds) | vector_rerank | 0.900 | 1.000 | 0.953 | Reranker adds +0.033 recall |
| paraphrase-v1 (25 seeds) | vector | 0.944 | 1.000 | 0.964 | Strong paraphrase resilience |
| multihop-v1 (25 seeds) | vector | 0.939 | 1.000 | 0.952 | Highest precision (0.587) |
| scale-v1 (80 seeds) | hybrid | 0.771 | 1.000 | 0.903 | Cascade retrieval eliminates BM25 noise |
| realworld-v1 (30 seeds) | hybrid | 0.792 | 0.883 | 0.835 | Synonym expansion bridges vocab gaps |
| compound-v1 (20 seeds) | hybrid | 0.900 | 0.978 | 0.937 | Compound tokenization + stemming |

## v0.5.4 Improvements

Porter stemming, expanded synonyms (98 mappings), cascade retrieval (for namespaces >= 50 entries), auto-PRF, and category-aware score boosting dramatically improved hybrid search at scale. scale-v1 hybrid recall jumped from 0.689 to 0.771 (+11.9%) with perfect MRR.

## Mode Selection Guide

Default to `hybrid` — it is now the best mode across most datasets thanks to cascade retrieval and synonym expansion. Use `vector` for minimal-latency queries. Use `hybrid_rerank` for maximum precision. The cascade mode automatically prevents BM25 noise in large namespaces (>= 50 entries) while preserving BM25 rescue for small namespaces.

## Six Benchmark Datasets

Four covering generic CS topics (programming languages, data structures, ML, databases, networking, systems, security, DevOps), one real-world dataset modeled after actual cognitive memory entries (architecture decisions, bug fixes, code patterns, user preferences, lessons learned), and one compound tokenization dataset testing BM25 handling of hyphenated terms and vocabulary gaps. Relevance grades use a 0-3 scale (3 = highly relevant).
