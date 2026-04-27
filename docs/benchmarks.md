# Benchmarks

[< Back to README](../README.md)

Benchmark results are stored in `benchmarks/` organized by date. Each run captures IR quality metrics (Recall@K, Precision@K, MRR, nDCG@K) and latency percentiles across 17 datasets and 4 search modes.

## Spectral Retrieval Benchmark (v0.9.0)

**Date:** 2026-04-27. **Artifact:** [`benchmarks/2026-04-27/spectral-retrieval-v1-results.json`](../benchmarks/2026-04-27/spectral-retrieval-v1-results.json). **Harness:** [`SpectralRetrievalBenchmark.cs`](../tests/McpEngramMemory.Tests/SpectralRetrievalBenchmark.cs).

Validation harness for the v0.9.0 graph-aware retrieval pipeline. Synthetic 40-entry corpus across 8 topics × 5 entries each, intra-topic edges (the shape `auto-link` builds), real ONNX `bge-micro-v2` embeddings, 10 queries split between **broad** (5 short conceptual: e.g. `"vector throughput"`) and **specific** (5 longer precise: e.g. `"what is the default StmThreshold value below which STM demotes"`).

Each query is run through `recall` with each `spectralMode` (`none`, `broad`, `specific`, `auto`); metrics are averaged within the broad/specific subsets and overall.

| metric | none | broad | specific | auto | auto delta vs. none |
|---|---|---|---|---|---|
| **Overall Recall@5** | 0.820 | 0.920 | 0.780 | **0.920** | **+12%** |
| **Overall Precision@5** | 0.587 | 0.687 | 0.653 | **0.753** | **+28%** |
| Overall MRR | 0.950 | 0.950 | 0.767 | 0.950 | — |
| **Overall nDCG@5** | 0.847 | 0.909 | 0.742 | **0.907** | **+7%** |
| **Broad Recall@5** | 0.640 | 0.840 | 0.560 | **0.840** | **+31%** |
| **Broad Precision@5** | 0.640 | 0.840 | 0.640 | **0.840** | **+31%** |
| **Broad nDCG@5** | 0.694 | 0.817 | 0.488 | **0.817** | **+18%** |
| Specific Recall@5 | 1.000 | 1.000 | 1.000 | 1.000 | — |
| **Specific Precision@5** | 0.533 | 0.533 | 0.667 | **0.667** | **+25%** |
| Specific nDCG@5 | 1.000 | 1.000 | 0.997 | 0.997 | -0.3% (noise) |

**Read:** Auto mode wins across the board. The word-count + digit/quote heuristic correctly routes short conceptual queries to Broad (cluster-boost surfaces missing topic mates) and longer precise queries to Specific (high-pass demotes off-topic cluster mates). Forcing **Specific** on broad queries hurts (Recall 0.640 → 0.560, MRR 0.900 → 0.533, nDCG 0.694 → 0.488) — exactly why auto mode exists. Forcing **Broad** on specific queries does not regress on Recall or MRR — the cluster-dominance gate skips boost when the query is genuinely ambiguous, and the `max(original, boosted)` rule preserves strong individual hits.

**Per-query under auto mode:**

| query | recall | precision | MRR | notes |
|---|---|---|---|---|
| `broad-simd` | 0.6 → **1.0** | 0.6 → **1.0** | 1.0 | Cluster boost surfaces missing simd mates |
| `broad-graph` | 1.0 | 1.0 | 1.0 | Already perfect — no change |
| `broad-decay` (ambiguous) | 0.2 | 0.2 | 0.5 | Top-K split across 5 clusters; dominance gate correctly skips boost |
| `broad-embed` | 0.8 → **1.0** | 0.8 → **1.0** | 1.0 | embed-tokenizer surfaced via spectral expansion |
| `broad-storage` | 0.6 → **1.0** | 0.6 → **1.0** | 1.0 | Cluster boost works |
| `spec-stm-threshold` | 1.0 | 1.0 | 1.0 | Specific high-pass eliminates cluster noise |
| `spec-int8` | 1.0 | 0.667 | 1.0 | |
| `spec-bge-dim` | 1.0 | 0.5 | 1.0 | |
| `spec-heat-kernel` | 1.0 | 0.667 | 1.0 | |
| `spec-wal` | 1.0 | 0.5 | 1.0 | |

**Mechanisms:**

- **Broad mode**: cluster-dominance gate (≥3 of top-5 from same connected component) + max-neighbor boost (`score = max(original, max_neighbor × 0.95)`) + cluster expansion (BFS the full graph from a dominant-component candidate, surface members not in the original pool).
- **Specific mode**: high-pass spectral filter `1 - exp(-λ·t)` via the memory-diffusion kernel — kills the constant (cluster mean) mode, preserves per-entry deviation. Restricted to original candidate set.
- **Auto mode**: local heuristic — short queries (<5 words, no digits/quotes) → Broad; longer or precision-marked queries → Specific. Zero LLM/embedding calls.

## Agent Outcome Benchmark

In addition to IR benchmarks, the MCP server now exposes `run_agent_outcome_benchmark`, a task-style proxy benchmark that compares four memory conditions:

- `no_memory`
- `transcript_replay`
- `vector_memory`
- `full_engram`

It scores task success, required-evidence coverage, conflict rate, and latency on practical assistant behaviors such as archived recall, graph-assisted recovery, preference retention, interrupted repo-work recovery, and hybrid rescue of colloquial queries. Available datasets are `agent-outcome-v1`, `agent-outcome-repo-v1`, and `agent-outcome-hard-v1`. Results are written to `benchmarks/YYYY-MM-DD/{datasetId}-agent-outcome.json` by default. Treat this as a bridge between pure retrieval quality and full external-agent evaluation, not a substitute for end-to-end model/task A/B tests.

## Live Agent Outcome Benchmark
The MCP server also exposes `run_live_agent_outcome_benchmark`, which runs the same four memory conditions against a real generation model and grades the model's structured JSON output deterministically:

- `no_memory`
- `transcript_replay`
- `vector_memory`
- `full_engram`

The first supported live provider is `ollama`. The harness injects condition-specific memory context, requires the model to return JSON with cited memory IDs, and scores required coverage, conflict rate, response-format validity, and latency. Results are written to `benchmarks/YYYY-MM-DD/{datasetId}-live-agent-outcome-{provider}-{model}.json` by default.

Use `compare_live_agent_outcome_artifacts` to diff two live benchmark artifacts from the same dataset. The report summarizes condition-level metric deltas and highlights the specific tasks that improved or regressed between runs.

Use `check_for_regression` to perform automated regression testing in CI. It compares a candidate artifact against a pinned baseline and returns a failure status if `full_engram` metrics drop below configurable thresholds.

On 2026-04-17, `phi3.5:3.8b` was established as the baseline for `agent-outcome-hard-v1` (expanded with 3-hop graph chains and synonym gaps), reaching a high pass rate under `full_engram` while simpler memory policies (transcript replay, vector) failed to bridge the multi-memory links.

## Latest Results (2026-04-17)

### Overview Benchmarks

| Dataset | Model | Pass Rate | Success Score | Result |
|---------|-------|-----------|---------------|--------|
| **agent-outcome-hard-v1** | [phi3.5:3.8b (Baseline)](../benchmarks/baselines/agent-outcome-hard-v1-baseline.json) | **1.000** | **0.917** | **Best** |
| | [qwen2.5:7b](../benchmarks/2026-04-17/agent-outcome-hard-v1-live-agent-outcome-ollama-qwen2.5-7b.json) | 0.750 | 0.750 | Regressed |
| | [deepseek-r1:8b](../benchmarks/2026-04-17/agent-outcome-hard-v1-live-agent-outcome-ollama-deepseek-r1-8b.json) | 0.000 | 0.000 | Format Fail |
| **agent-outcome-reasoning-v1** | [phi3.5:3.8b](../benchmarks/2026-04-17/agent-outcome-reasoning-v1-live-agent-outcome-ollama-phi3.5-3.8b.json) | **0.250** | **0.667** | **New Baseline** |
| **agent-outcome-repo-v1** | [phi3.5:3.8b (Baseline)](../benchmarks/baselines/agent-outcome-repo-v1-baseline.json) | **1.000** | **0.900** | Stable |
| | [qwen2.5:7b](../benchmarks/2026-04-17/agent-outcome-repo-v1-live-agent-outcome-ollama-qwen2.5-7b.json) | **1.000** | **0.900** | Stable |
| **agent-outcome-v1** | [phi3.5:3.8b (Baseline)](../benchmarks/baselines/agent-outcome-v1-baseline.json) | **0.600** | **0.833** | **Best** |
| | [qwen2.5:7b](../benchmarks/2026-04-17/agent-outcome-v1-live-agent-outcome-ollama-qwen2.5-7b.json) | **0.600** | 0.767 | Slight Regression |

## Intelligence Tier Verification

We verify that Engram MCP makes AI "more intelligent" by measuring the **Reasoning Delta**—the gap between what a model can do with standard context vs. what it can do with the Engram Knowledge Graph.

See [benchmarks-intelligence.md](./benchmarks-intelligence.md) for the T2 intelligence-claim
framework: four new datasets (`reasoning-ladder-v1`, `contradiction-arena-v1`,
`adversarial-retrieval-v1`, `counterfactual-v1`), six new metrics (ReasoningPathValidity,
DependencyCompletionScore, StaleMemoryPenalty, MinimalEvidenceScore, NoiseResistanceScore,
ContradictionHandlingScore), and three ablation conditions (`full_engram_no_graph`,
`full_engram_no_lifecycle`, `full_engram_no_hybrid`) that attribute wins to specific
cognitive mechanisms.

### Current Intelligence Standing (2026-04-17)

| Condition | Success Score | Capability Level | Verification |
|-----------|---------------|-------------------|--------------|
| **Transcript Replay** | 0.458 | Basic Recall | AI is limited by recent history; fails to connect dots across sessions. |
| **Vector Memory** | 0.458 | Naive RAG | Semantic similarity alone is insufficient for multi-hop logic or contradictions. |
| **Full Engram** | **0.667** | **Intelligence Tier** | Graph-traversal enables **Dependency Reasoning** (linking signals to required actions). |

**Key Proof of Intelligence**: In the `agent-outcome-reasoning-v1` benchmark, the **Full Engram** condition was the only one to solve the `reason-safe-exit` task. It followed a graph edge (SIGTERM → Shutdown → DB Flush) to find a required behavior that was completely missing from the model's immediate context window.

### Detailed Artifacts

- **Hard Dataset (v1)**
    - [phi3.5:3.8b (Baseline)](../benchmarks/baselines/agent-outcome-hard-v1-baseline.json)
    - [qwen2.5:7b](../benchmarks/2026-04-17/agent-outcome-hard-v1-live-agent-outcome-ollama-qwen2.5-7b.json)
    - [deepseek-r1:8b](../benchmarks/2026-04-17/agent-outcome-hard-v1-live-agent-outcome-ollama-deepseek-r1-8b.json)
- **Repo Recovery (v1)**
    - [phi3.5:3.8b (Baseline)](../benchmarks/baselines/agent-outcome-repo-v1-baseline.json)
    - [qwen2.5:7b](../benchmarks/2026-04-17/agent-outcome-repo-v1-live-agent-outcome-ollama-qwen2.5-7b.json)
- **General Assistant (v1)**
    - [phi3.5:3.8b (Baseline)](../benchmarks/baselines/agent-outcome-v1-baseline.json)
    - [qwen2.5:7b](../benchmarks/2026-04-17/agent-outcome-v1-live-agent-outcome-ollama-qwen2.5-7b.json)

## Directory Structure

```
benchmarks/
  baselines/                          # Pinned artifacts for CI regression testing
  baseline-v1.json                    # Original Sprint 1 baseline (2026-03-07)
...
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
| [default-v1](../benchmarks/2026-04-16/default-v1-vector_rerank.json) (25 seeds) | vector_rerank | **0.900** | 1.000 | **0.956** | Stable; nDCG +0.003 vs March 20 run |
| [scale-v1](../benchmarks/2026-04-16/scale-v1-hybrid.json) (80 seeds) | hybrid (recall) / vector_rerank (nDCG) | **0.745** / 0.734 | 0.975 / 0.983 | 0.856 / **0.884** | Run variance vs v0.5.4 doc; all regression thresholds pass |
| [paraphrase-v1](../benchmarks/baseline-paraphrase-v1.json) (25 seeds) | vector | 0.944 | 1.000 | 0.964 | March 2026 baseline |
| [multihop-v1](../benchmarks/baseline-multihop-v1.json) (25 seeds) | vector | 0.939 | 1.000 | 0.952 | March 2026 baseline |
| [realworld-v1](../benchmarks/2026-03-20/realworld-v1-hybrid.json) (30 seeds) | hybrid | 0.792 | 0.883 | 0.835 | March 2026 baseline |
| [compound-v1](../benchmarks/2026-03-20/scale-v1-hybrid.json) (20 seeds) | hybrid | 0.900 | 0.978 | 0.937 | March 2026 baseline |
| [ambiguity-v1](../benchmarks/2026-04-16/ambiguity-v1-vector_rerank.json) (24 seeds) | vector_rerank | **0.922** | 1.000 | **0.941** | Stable vs March 25 baseline |
| [distractor-v1](../benchmarks/baseline-distractor-v1.json) (22 seeds) | vector_rerank / hybrid_rerank | 0.737 | 1.000 | 0.988 | March 25 baseline |
| [specificity-v1](../benchmarks/2026-04-16/specificity-v1-vector_rerank.json) (30 seeds) | vector_rerank | **0.919** | 0.972 | **0.905** | Stable vs March 25 baseline |
| [contamination-v1](../benchmarks/2026-04-16/contamination-v1-vector_rerank.json) (15 seeds) | vector / vector_rerank | **0.972** | 0.958 | **0.917** | Strong cross-domain resistance; all modes ≥ 0.972 recall |
| [physics-v1](../benchmarks/2026-04-16/physics-v1-hybrid_rerank.json) (20 seeds) | hybrid_rerank | **1.000** | 1.000 | **0.917** | Perfect recall all modes; hybrid_rerank best nDCG |
| [lifecycle-v1](../benchmarks/2026-04-16/lifecycle-v1-vector_rerank.json) (25 seeds) | vector_rerank | 0.666 | 0.867 | 0.655 | Lifecycle-state-aware retrieval; lower due to STM/LTM/archived split |
| [msa-multihop-v1](../benchmarks/2026-04-16/msa-multihop-v1-hybrid.json) (30 seeds) | hybrid | **0.800** | 1.000 | **0.900** | MSA multi-hop reasoning; hybrid best across all metrics |
| [msa-coldstart-v1](../benchmarks/2026-04-16/msa-coldstart-v1-vector.json) (20 seeds) | vector | **0.815** | 0.900 | **0.855** | Cold-start namespace scenario |
| [scale-v2](../benchmarks/2026-04-16/scale-v2-hybrid_rerank.json) (10000 seeds) | hybrid_rerank | **0.614** | 0.708 | **0.625** | Extreme-scale stress test (10× scale-v1); redesigned gold sets (easy K=50, medium K=10, hard K=20); hybrid 0.524, vector 0.460 |
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
