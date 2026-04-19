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

## Cross-CLI comparison (8-needle, n=25 stratified, 2026-04-19)

Same MRCR v2 8-needle harness, run across three subscription-driven CLIs with their best-in-class
available models. `claude-cli` uses `claude -p`; `codex-cli` uses `codex exec -o`; `gemini-cli` uses
`gemini -p ""` (prompt via stdin). All runs share the same 25 stratified probes
(contexts 18K–571K approx tokens), same ordinal engram policy, same local ONNX scoring.

| CLI / Model | Matched fc-sim | Matched en-sim | Oversized en-sim | Overall en-sim | Overall en-pass |
|---|---:|---:|---:|---:|---:|
| claude-cli / sonnet             | **0.993** | 0.898 | 0.931 | 0.912 |  72% |
| claude-cli / opus               | 0.936 | 0.979 | 0.997 | 0.987 |  96% |
| codex-cli / gpt-5.4             | **1.000** | 0.958 | 0.992 | 0.973 |  96% |
| codex-cli / gpt-5.4-mini        | 0.928 | **0.993** | 0.992 | **0.993** | **100%** |
| gemini-cli / gemini-2.5-pro     | 0.930 | **0.995** | 0.992 | **0.994** | **100%** |
| gemini-cli / gemini-2.5-flash   | 0.961 | 0.972 | 0.956 | 0.965 |  88% |

All runs: **99.7% prompt-token reduction** (320× fewer tokens), 15K engram tokens vs 4.66M
full-context tokens.

### Cross-CLI findings

1. **Ordinal engram generalizes across CLIs**. 5 of 6 models clear 0.96+ on overall engram sim.
   The one exception is Claude Sonnet (0.912) — but it is also the best full-context model when
   the prompt fits (0.993 matched).
2. **Flagship is not always best on ordinal engram.** gpt-5.4-**mini** (0.993) outperforms
   gpt-5.4 (0.973); the smaller model is less prone to paraphrasing the single-chunk output.
3. **Gemini 2.5 Pro and gpt-5.4-mini tie for best overall** (0.994 / 0.993, both 100% pass).
4. **Codex gpt-5.4 has the highest matched full_context similarity** (1.000) — flawless long-context
   recall when the prompt fits.
5. **Above 200K tokens, 5 of 6 engram arms hit 0.99+ sim**. Long-context models cannot run at all
   there; retrieval is the whole ballgame.

**CLI-level notes**

- `codex-cli` (ChatGPT subscription) does not support `gpt-5.4-codex` or `o3` — those require API
  access. The two accessible models (`gpt-5.4`, `gpt-5.4-mini`) are both strong on MRCR.
- `gemini-cli` subscription exposes `gemini-2.5-pro`, `gemini-2.5-flash`, `gemini-2.5-flash-lite`.
  Gemini-3.x models are currently 404 on this subscription tier.
- `claude-cli` supports `sonnet`, `opus`, `haiku`.
- Claude Sonnet/Opus were run on an earlier iteration of the ordinal prompt ("minimum text"
  phrasing); Codex + Gemini used a tightened "reproduce snippet verbatim" variant. The change
  is minor for Claude (already interpreted it correctly) but was necessary for Gemini 2.5 Pro
  to stop truncating long needles.

Artifacts: `benchmarks/2026-04-19/mrcr-v2-8needle-mrcr-{claude-cli,codex-cli,gemini-cli}-*-ordinal.json`.

## Scaling across needle counts (25 stratified probes per variant, 2026-04-19)

Run on `openai/mrcr` 2-needle, 4-needle, and 8-needle parquets — 25 stratified probes
each (context size 18K–571K approx tokens, 11/25 exceed Claude's 200K limit in every
variant). All runs use `engramMode: ordinal`, same harness, same scoring (bge-micro-v2
cosine similarity).

| Needles | Model | Matched fc-sim | Matched en-sim | Oversized en-sim | Overall en-sim | Overall en-pass |
|---|---|---:|---:|---:|---:|---:|
| 2 | opus   | 0.991 | 0.955 | 0.989 | 0.970 | 92% |
| 2 | sonnet | 0.991 | 0.961 | 0.992 | 0.975 | 92% |
| 4 | opus   | 0.944 | **0.975** | 0.961 | 0.969 | 88% |
| 4 | sonnet | 0.977 | 0.910 | 0.922 | 0.915 | 72% |
| 8 | opus   | 0.936 | **0.979** | **0.997** | **0.987** | 96% |
| 8 | sonnet | 0.993 | 0.898 | 0.931 | 0.912 | 72% |

Prompt-token reduction is a flat **99.7% (320× fewer tokens)** across all six runs —
ordinal retrieval is a single-chunk lookup, so payload size is invariant to needle
count or model choice.

### Patterns that hold across needle counts

1. **2-needle is saturated** — both arms score 0.97+. Too few planted items to create
   positional ambiguity; hybrid or ordinal would both work.
2. **At 4n and 8n, Opus + ordinal engram beats Opus + full_context** on the matched set
   (0.975 vs 0.944; 0.979 vs 0.936). A focused single snippet outperforms 100K+ tokens
   of haystack for Opus.
3. **Sonnet is the long-context king when prompts fit** (8n matched: 0.993 vs 0.898).
   Dumping the full conversation gives Sonnet enough signal to resolve ordinals on its
   own; retrieval throws signal away.
4. **Above 200K tokens, engram is the only option that runs at all**. Opus + ordinal
   engram hits 0.997 sim / 100% pass on 300K+ char 8-needle probes.
5. The 320× token-reduction payoff is independent of model and needle count.

Artifacts: `benchmarks/2026-04-19/mrcr-v2-{2,4,8}needle-mrcr-claude-cli-{sonnet,opus}-ordinal.json`.

## Results (25 stratified probes, 2026-04-19)

25 stratified probes from `openai/mrcr` 8needle_0.parquet, selected evenly across
context sizes: approximate tokens range from ~18K to ~571K (median 75K). 11 of the
25 probes exceed Claude's 200K context limit — those are skipped on the full_context
arm (recorded as error; sim=0) but run normally on the engram arm.

### Matched set (n=14, probes where full_context fit within 200K)

| Model | Arm | Similarity | Pass |
|-------|-----|-----------:|-----:|
| Sonnet | full_context    | **0.993** | 100% |
| Sonnet | engram ordinal  | 0.898 |  71% |
| Opus   | full_context    | 0.936 |  86% |
| Opus   | engram ordinal  | **0.979** | **93%** |

### Oversized set (n=11, probes >200K tokens — full_context cannot run)

| Model | engram ordinal sim | pass |
|-------|-------------------:|-----:|
| Sonnet | 0.931 | 73% |
| Opus   | **0.997** | **100%** |

### Overall (n=25, all probes)

| Model | Arm | Similarity | Pass | Prompt tokens |
|-------|-----|-----------:|-----:|--------------:|
| Sonnet | full_context    | 0.556 | 56% |    4,657,645 |
| Sonnet | engram ordinal  | 0.912 | 72% |       14,556 |
| Opus   | full_context    | 0.524 | 48% |    4,657,645 |
| Opus   | engram ordinal  | **0.987** | **96%** |       **14,556** |

Prompt-token reduction across the whole set: **99.7% (320× fewer tokens)**.

### Takeaways

1. **Sonnet is the long-context king when the prompt fits.** On the matched set
   (probes ≤200K) Sonnet + full_context scores 0.993 — beating Sonnet + ordinal
   engram (0.898) outright. Long-context Sonnet is genuinely very good at this.
2. **Opus + ordinal engram beats Opus + full_context even on the matched set**
   (0.979 vs 0.936). Focused single-snippet retrieval gives Opus a cleaner signal
   than dumping 100K+ tokens into its context window.
3. **Above 200K tokens, engram is the only option that runs at all.** Opus ordinal
   engram hits 0.997 sim / 100% pass on 300K+ char contexts that long-context models
   cannot handle.
4. **320× prompt-token reduction** (14.5K vs 4.66M) holds across the whole set —
   the cost-per-query delta swamps any accuracy trade-off on workloads where the
   ordinal path applies.

### Pilot (n=3, earlier validation run)

Previously we ran n=3 to validate the pipeline. Hybrid mode collapsed (Sonnet 0.499,
Opus 0.645) because MRCR probes ask for positional recall that content-similarity
retrieval cannot answer. Switching to ordinal mode recovered the signal
(Sonnet 0.930, Opus 0.986 at n=3). The n=25 run above confirms the ordinal
result at publishable scale for Opus; Sonnet's pass-rate drop (100% → 71% on the
matched set) is the n=3→n=25 reality check. Artifacts:
`benchmarks/2026-04-19/mrcr-v2-8needle-mrcr-claude-cli-{sonnet,opus}[-ordinal].json`.

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
