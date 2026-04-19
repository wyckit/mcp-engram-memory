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
| `engram_retrieval` | every turn ingested into a scratch namespace, one chunk per turn | top-K chunks returned by hybrid BM25 + vector search, keyed on the probe |

Both arms share identical probes, gold answers, embedding model, and generation
parameters so the only difference between them is the memory policy.

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
  topK             = 8                    // snippets retrieved by the engram arm
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
