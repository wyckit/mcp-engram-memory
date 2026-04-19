# MRCR v2 (8-needle) benchmark

A long-context A/B benchmark that measures whether hybrid retrieval over engram memory
can match a model's accuracy when the full conversation is stuffed into its context
window, at a fraction of the prompt-token cost.

## What it tests

MRCR v2 (Multi-Round Coreference Resolution, 8-needle variant) plants 8 retrievable
items inside extended multi-turn conversations, then asks the model to reason about
them. The llm-stats leaderboard reports mean cosine similarity of the answer against the
gold needle. Published numbers (April 2026):

| Model | Mean similarity |
|-------|-----------------|
| Claude Opus 4.6 | 0.930 |
| Gemini 3.1 Flash-Lite | 0.601 |
| GPT-5.4 mini | 0.336 |
| (average across all models) | 0.389 |

Paper: [arXiv:2409.12640](https://arxiv.org/abs/2409.12640).
Leaderboard: [llm-stats.com/benchmarks/mrcr-v2-(8-needle)](https://llm-stats.com/benchmarks/mrcr-v2-\(8-needle\)).

## Two arms

| Arm | Memory condition | Prompt payload |
|-----|------------------|----------------|
| `full_context` | none | the entire MRCR conversation (up to 128K tokens) |
| `engram_retrieval` | every turn ingested into a scratch namespace, one chunk per turn | top-K chunks (hybrid mode) **or** the single category+ordinal exact match (ordinal mode) |

Both arms share identical probes, gold answers, embedding model, and generation
parameters so the only difference between them is the memory policy.

### Engram modes

The engram arm has two retrieval policies, selectable via `engramMode`:

- **`hybrid`** (default) — every turn is ingested flat, and the probe drives a BM25+vector
  search for top-K chunks. This is the reference baseline; it has no positional awareness
  and deliberately fails on workloads where positional information is load-bearing.

- **`ordinal`** — pair-wise ingest: each (`user_ask`, `assistant_reply`) pair normalizes the
  user's ask into a category signature and increments a within-category counter. The
  assistant turn is stored with `Category` = signature and `Metadata["ordinal"]` = 1-based
  position within that category. The probe parser extracts `(RandomString, Ordinal, Category)`
  from the "Prepend X to the Nth (1 indexed) Y" template; retrieval is an exact category +
  ordinal lookup against the scratch namespace. Probes that don't match the template fall
  back to hybrid automatically, so the mode is safe to use on mixed datasets.

## Pilot results (3 probes, ~18–19K-token contexts, 2026-04-19)

The ordinal mode was developed in response to the hybrid pilot discovering that MRCR's
8-needle probes are adversarial to content-similarity retrieval — they ask for positional
recall ("the 6th scene") across cohorts of chunks that share topic keywords.

| Model | Mode | Arm | Similarity | Pass | Prompt tokens |
|-------|------|-----|-----------:|-----:|--------------:|
| Sonnet | hybrid  | full_context      | 0.964 | 100% | 57,745 |
| Sonnet | hybrid  | engram_retrieval  | 0.499 |   0% |  3,662 |
| Sonnet | ordinal | full_context      | 0.987 | 100% | 57,745 |
| Sonnet | ordinal | engram_retrieval  | **0.930** |  67% | **1,670** |
| Opus   | hybrid  | full_context      | 0.898 | 100% | 57,745 |
| Opus   | hybrid  | engram_retrieval  | 0.645 |  33% |  3,662 |
| Opus   | ordinal | full_context      | 0.924 | 100% | 57,745 |
| Opus   | ordinal | engram_retrieval  | **0.986** | **100%** | **1,670** |

**Key findings**

- Ordinal mode lifts engram similarity by **+0.43** (Sonnet) and **+0.34** (Opus) over hybrid
  mode — because the probe parser converts an ordinal-recall task into a deterministic
  category+ordinal lookup instead of a noisy dense-vector contest.
- **Opus + ordinal engram** (sim=0.986) **beats Opus + full_context** (sim=0.924, +0.062)
  at **34× fewer prompt tokens** (1,670 vs 57,745, a 97.1% reduction).
- Sonnet + ordinal engram passes 2/3 probes and hits 0.930 similarity — slightly trailing
  full-context Sonnet (0.987) but at the same 34× token savings.

These numbers are n=3 and deliberately small (pilot scale); treat them as directional
signal, not a production number. Scaling to 25+ probes per model would tighten the
confidence intervals.

Artifacts: `benchmarks/2026-04-19/mrcr-v2-8needle-mrcr-claude-cli-{sonnet,opus}[-ordinal].json`.

## Scoring

Mean cosine similarity of the model's answer vs. the gold answer, computed with the
local `bge-micro-v2` embedding model (`OnnxEmbeddingService`). This matches the paper's
metric while keeping scoring deterministic and API-cost-free.

A task is marked `passed` when similarity ≥ 0.85 (configurable via `MrcrScorer`).

## Running the benchmark

### 1. Download the dataset

See [`benchmarks/datasets/mrcr-v2/README.md`](../benchmarks/datasets/mrcr-v2/README.md)
for the Hugging Face download recipe. The runner expects a JSONL file at
`benchmarks/datasets/mrcr-v2/mrcr_v2_8needle.jsonl` (or the path set via
`MRCR_DATASET_PATH`).

### 2. Drive generation through your Claude subscription

```
run_mrcr_benchmark
  model            = "opus"               // or "sonnet" / "haiku"
  datasetId        = "mrcr-v2-8needle"
  provider         = "claude-cli"         // default — uses `claude -p` subprocess
  limit            = 25                   // pilot; set 0 for the full dataset
  topK             = 8                    // snippets retrieved by the engram arm (hybrid)
  engramMode       = "ordinal"            // "hybrid" (default) or "ordinal"
```

The `claude-cli` provider shells out to the Claude Code CLI, so generation charges
against your Claude subscription rather than the Anthropic API. Prompts are piped over
stdin so long conversations don't hit shell-argument-length limits.

Artifact output: `benchmarks/YYYY-MM-DD/mrcr-v2-8needle-mrcr-claude-cli-opus.json`.

### 3. Compare two runs

```
compare_mrcr_artifacts
  baselineArtifactPath  = "benchmarks/2026-04-18/mrcr-v2-8needle-mrcr-claude-cli-opus.json"
  candidateArtifactPath = "benchmarks/2026-04-20/mrcr-v2-8needle-mrcr-claude-cli-opus.json"
```

Returns per-arm similarity and pass-rate deltas plus the change in prompt-token
reduction ratio.

## What the result tells you

The `similarityDelta` field reports `engramMeanSimilarity − fullContextMeanSimilarity`:

- **≈ 0**: engram retrieval matches the long-context baseline at a fraction of the
  prompt budget — the value proposition holds.
- **Strongly negative**: retrieval is missing needles. Tune `topK`, enable summary-first
  search, or inspect `bucketMeans` for the context bucket where recall collapses.
- **Positive**: retrieval beats the raw long-context model — typically because the
  model drowns at 128K but can focus when given only the relevant turns.

The `promptTokenReductionRatio` reports how much prompt budget the engram arm saves
relative to the baseline (`1 − engramTokens / fullContextTokens`). Combine it with the
similarity delta to pick the operating point that trades accuracy for cost.

## Provider options

| Provider | Notes |
|----------|-------|
| `claude-cli` (default) | Spawns `claude -p --model <name>`; uses the Claude Code CLI subscription. Set `endpoint` to override the CLI path. |
| `ollama` | Local generation via the Ollama HTTP API. Set `endpoint` or the `OLLAMA_URL` env var. |

## Limitations

- Prompt-token counts are approximate (`chars / 4`) — accurate enough for relative
  reduction ratios between arms, but not a 1:1 match with Claude's tokenizer.
- The full_context arm skips probes whose concatenated prompt exceeds
  `maxContextTokens` (default 131072). Skipped probes are excluded from the arm's
  aggregates and surfaced via `error` on the per-task result.
- Engram retrieval quality depends on the hybrid-search tuning and the embedding
  model's coverage of the probe surface form — contextual-prefix and cluster-summary
  strategies that help other benchmarks may also help here.
