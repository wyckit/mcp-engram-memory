# Intelligence-Claim Benchmarks (T2)

[< Back to Benchmarks](./benchmarks.md)

This document describes the T2 benchmark extensions that tie specific Engram mechanisms
(graph traversal, lifecycle/deep-recall, hybrid lexical rescue) to specific gains in
reasoning outcomes. The goal: defensibly claim that **structured memory causes
qualitatively new problem-solving behavior** — not just "better recall."

Framing after [ChatGPT review 2026-04-17](https://claude.ai/) + engram expert-panel
session `t2-benchmark-design-2026-04-17`.

## Two-Part Claim

| Part | Question it answers |
|------|---------------------|
| **Memory Capability Gain** | Which memory operations became possible? (graph traversal, lifecycle-aware recall, contradiction edges, cluster summaries, expert routing) |
| **Reasoning Outcome Gain** | Which tasks became solvable because of those operations? (multi-hop chains, contradiction resolution, noise-resistant retrieval, counterfactual reasoning) |

Each T2 dataset isolates one capability→outcome link so the gain can be attributed, not
just asserted.

## The Four T2 Datasets

### `reasoning-ladder-v1` — Multi-hop depth gradient

Same causal chain (FP32 bottleneck → Int8 quantization → HNSW latency → draft-mode SLO →
autocomplete UI) queried at hop depths 1, 2, 3, and 4. Transcript replay and
vector-only memory should break as hop depth rises; graph traversal keeps the chain
assemblable.

- **Primary metric**: `ReasoningPathValidity` (coverage × edge-coherence)
- **Secondary metric**: `DependencyCompletionScore`
- **Module under test**: graph traversal (`full_engram_no_graph` should collapse)

### `contradiction-arena-v1` — Old fact vs new fact

Eight seeds forming four old/new pairs (port, quantization, codename, auth lifetime).
Old facts are lifecycle-archived; new facts are LTM. Contradiction edges link them.
Queries ask for "the current answer, and any conflicting rule I should know about."

- **Primary metric**: `ContradictionHandlingScore` (1.0 detected+resolved / 0.5 both cited / 0.0 only stale)
- **Secondary metric**: `StaleMemoryPenalty`
- **Module under test**: lifecycle weighting (`full_engram_no_lifecycle` should degrade)

### `adversarial-retrieval-v1` — Distractor resistance

Real facts beside four trap types: near-duplicate paraphrases, synonym/jargon swaps,
stale/archived legacy facts, directly-contradictory wrong facts. Hybrid BM25+vector
should reject distractors that surface vector-only retrieval fools.

- **Primary metric**: `NoiseResistanceScore` (0.6·retrieval_purity + 0.4·citation_purity)
- **Module under test**: hybrid lexical rescue (`full_engram_no_hybrid` should degrade)

### `counterfactual-v1` — "What breaks if X is removed?"

Two dependency chains (WAL→atomic-delete→Accretion cleanup; HNSW snapshot→cold-start
latency). Queries require enumerating downstream consequences of a counterfactual
removal, which demands forward traversal from the removed node.

- **Primary metrics**: `ReasoningPathValidity` + `DependencyCompletionScore`
- **Module under test**: graph traversal + counterfactual reasoning on edges

## Six New Intelligence Metrics

All metrics are floats in `[0, 1]` (except `StaleMemoryPenalty` where 0 is best).
Default values preserve legacy dataset scores when the driving fields are absent.

| Metric | Formula | Why it's intelligence-shaped |
|--------|---------|------------------------------|
| `ReasoningPathValidity` | `coverage × coherence` where coverage = `cited_ordered_steps / total_steps` and coherence = `valid_consecutive_edge_pairs / (total_steps - 1)` | Rewards citing *and* correctly chaining. A model that finds all nodes but connects them randomly scores near zero. |
| `DependencyCompletionScore` | `cited_ordered_steps / total_ordered_steps` | Did the prerequisite chain complete? Simpler than RPV — no edge check. |
| `StaleMemoryPenalty` | `stale_cited / stale_total` (0 = perfect) | Relying on outdated/archived evidence is punished. |
| `MinimalEvidenceScore` | Quadratic penalty with +1 grace window for over-citation; linear penalty for under-citation | Over-citation inflates hallucination surface; under-citation misses required evidence. |
| `NoiseResistanceScore` | `0.6·retrieval_purity + 0.4·citation_purity` where purity = `1 - distractor_hits/total` | Splits failure into retrieval-layer vs reasoning-layer for ablation attribution. |
| `NoiseResistanceScoreRanked` | `reciprocal_rank(first_required) × (1 - distractors_outranking / total_distractors)` | Rank-aware variant (expert Q3 follow-up). Moves with graph/lifecycle demotions even in the offline proxy where `context == cited`. Exposes coverage-vs-ranking-precision trade-offs. |
| `ContradictionHandlingScore` | Three-tier rubric: 1.0 / 0.5 / 0.0 for detected+resolved / both-cited / stale-only | Epistemic honesty (saying uncertain) beats blending, and both beat confidently-wrong. |

These supplement — not replace — the existing `RequiredCoverage`, `ConflictRate`,
`SuccessScore`, `PassRate`, and `FormatValidityRate`.

### Success-score adjustment

`SuccessScore` is now modulated by two of the new metrics for intelligence-aware tasks:

- Tasks with an ordered chain (`OrderedSteps.Count > 1`): `success *= 0.6 + 0.4 × RPV`
- Tasks with stale memories: `success -= 0.25 × StaleMemoryPenalty`

Legacy datasets (no `OrderedSteps`, no `StaleMemoryIds`) are left unchanged.

## Ablation Conditions

Three new conditions run a full-Engram policy with one module disabled, so the
benchmark delta attributes specific losses to specific mechanisms.

| Condition | What's disabled | Which metric should collapse |
|-----------|-----------------|------------------------------|
| `full_engram_no_graph` | Graph expansion + neighbor traversal | `ReasoningPathValidity`, `DependencyCompletionScore` (chains fall apart) |
| `full_engram_no_lifecycle` | Deep-recall (archived-entry resurrection) | `ContradictionHandlingScore`, `StaleMemoryPenalty` (archived facts unreachable for contradiction pairs) |
| `full_engram_no_hybrid` | BM25 lexical fusion | `NoiseResistanceScore` — retrieval_purity specifically (lexical distractors bleed through) |

### Running ablations

```bash
# Offline (proxy scoring, no live model)
run_agent_outcome_benchmark datasetId=reasoning-ladder-v1 runAblations=true
```

```bash
# Live model (Ollama). Triples generation cost — ~3× the wall time.
run_live_agent_outcome_benchmark model=qwen2.5:7b datasetId=contradiction-arena-v1 runAblations=true
```

Artifacts include up to 6 conditions: the 3 baselines + the 3 ablations. Each condition
reports all 6 new means; a per-condition row showing which metric collapsed is the
smoking-gun evidence for causal attribution.

## Capability → Outcome Map

| Engram capability | Dataset that exercises it | Expected delta when ablated |
|-------------------|---------------------------|-----------------------------|
| Graph traversal | `reasoning-ladder-v1` (depths 3-4), `counterfactual-v1` | `no_graph` drops RPV 30-50% on 3+ hop tasks |
| Lifecycle / deep recall | `contradiction-arena-v1` | `no_lifecycle` drops ContradictionHandlingScore by loss of archived-entry discovery |
| Hybrid lexical fusion | `adversarial-retrieval-v1` (synonym trap, near-duplicate) | `no_hybrid` drops retrieval-layer NoiseResistance; citation-layer partially preserved |
| Contradiction edges | `contradiction-arena-v1` | Graph ablation alone does not collapse CHS (edges used for routing, not just traversal) |

These are *predictions* the benchmark is designed to verify. Running the suite with
`runAblations=true` turns the predictions into claims — or falsifies them.

## First Measured Results (2026-04-18)

Offline proxy + live Ollama (`phi3.5:3.8b`) runs with ablations enabled, after the two
expert-panel-recommended retrieval fixes landed (`archived-contradicts-skip`,
`hybrid-gate-at-50`). Full artifacts at `benchmarks/2026-04-18/`.

### Graph-traversal is the causal mechanism for multi-hop uplift

| Dataset | Condition | Success | RPV |
|---------|-----------|---------|-----|
| `reasoning-ladder-v1` (live phi3.5) | vector_memory | 0.570 | 0.642 |
| | **full_engram** | **0.934** | **0.950** |
| | full_engram_no_graph | 0.570 ↓ | 0.642 ↓ |
| `counterfactual-v1` (live phi3.5) | vector_memory | 0.436 | 0.556 |
| | **full_engram** | **0.800** | **1.000** |
| | full_engram_no_graph | 0.436 ↓ | 0.556 ↓ |

Removing graph expansion collapses `full_engram` to vector-only performance on both
multi-hop datasets — a **+36% / +45% success uplift attributable to one module**.

### Contradiction resolution — fixed after lifecycle-contradicts filter

Before the fix, `full_engram` scored 0.812 / CHS=0.625 (surfaced both the archived stale
and the authoritative live fact via deep_recall + contradicts-edge expansion). After the
fix ships, all indexed policies reach 1.000 success / CHS=1.000. Transcript replay alone
falls to 0.938 because some chunks still expose the archived fact.

### Adversarial retrieval — offline proxy limitation confirmed

`adversarial-retrieval-v1` plateaus across all conditions at success 0.750 / NRS ~0.45
(live) / NRS 0.25 (offline). Live model doubles noise resistance over the offline
proxy but does not move with ablations. This is a known offline-proxy limit noted by
the expert panel (Q3) — the offline runner's `context == cited` assumption conflates
retrieval purity with citation purity.

### Model-sensitivity matrix (2 models × 4 datasets)

After running the same benchmarks on `qwen2.5:7b` (2× the parameters of `phi3.5:3.8b`),
the result is **not monotonic with model size**. Deltas below show `full_engram` success
and the drop when graph traversal is ablated.

| Dataset | phi3.5:3.8b full | phi3.5 Δ(no_graph) | qwen2.5:7b full | qwen2.5 Δ(no_graph) |
|---------|:---:|:---:|:---:|:---:|
| reasoning-ladder-v1 | 0.934 | **−0.364** | 0.828 | **−0.341** |
| counterfactual-v1 | 0.800 | **−0.364** | 0.373 | **+0.063** |
| contradiction-arena-v1 | 1.000 | 0.000 | 1.000 | 0.000 |
| adversarial-retrieval-v1 | 0.750 | 0.000 | 1.000 | 0.000 |

Two observations drive the final framing:

1. **Graph helps multi-hop ladder reasoning on both models.** The ~0.35 drop under
   `full_engram_no_graph` is preserved across a 2× parameter gap. The mechanism is
   real and model-independent on this task family.
2. **Graph *hurts* qwen2.5 on counterfactual tasks** (Δ = +0.063 means qwen scores
   higher *without* graph expansion). The panel attributed this to salient-node
   anchoring: undirected graph expansion surfaces `cf-wal` (the *cause* node) as a
   high-similarity neighbor, and qwen2.5 cites it instead of traversing downstream to
   the required dependents (`cf-atomic-delete`, `cf-corruption`). Format validity is
   1.000 — this is reasoning behavior, not a parsing failure.

### Revised intelligence claim (panel scoping, 2026-04-18)

The falsifiable, defensible form of the claim after the scaling run:

> **Engram graph traversal amplifies a model's existing reasoning profile.**
> Dependency-traversal-capable models gain multi-hop success commensurate with the
> depth of the chain. Salient-node-anchoring models may lose on counterfactual tasks
> where graph expansion surfaces the upstream cause as a distractor for the downstream
> dependents. The mechanism is model-conditional, not universal — and the signed
> effect on both models on the same dataset is itself evidence the mechanism is real.

The unscoped claim ("graph always helps reasoning") is **falsified** by this run and
should not be advanced. Report model profiles as 2×4 matrices, not single headlines.

**Design variable surfaced**: counterfactual tasks may require **edge-directional**
graph expansion (traverse only outgoing `depends_on` edges from the removed node) to
avoid injecting the cause as a distractor. Queued for a follow-up pass.

### Coverage-vs-ranking-precision trade-off (NRS_rw)

The rank-weighted NRS variant landed in the follow-up pass exposes a new finding on
`counterfactual-v1`: `full_engram` NRS_rw=0.483 vs `vector_memory` NRS_rw=0.611. Graph
expansion and deep_recall broaden the candidate pool, which raises `RequiredCoverage`
and `ReasoningPathValidity` (the reason full_engram beats vector on success) but pushes
some required entries deeper in the rank order. This trade-off was invisible to the
binary NRS metric and is now measurable — a useful signal for future work on whether
graph-expanded candidates should be score-dampened below the primary retrieval's top-K.

## What this is not

- Not a claim that Engram "makes the base model smarter." The model weights are
  unchanged. What changes is what the model can solve **given memory access**.
- Not a substitute for end-to-end agent A/B tests. These are proxy benchmarks: they
  measure whether memory policy enables a structured, scorable answer format, not
  whether it wins on open-ended product metrics.
- Not a leaderboard. The point is the *delta curves* across hop depth and ablation
  conditions — absolute pass rates matter less than the shape of those curves.

## Adding a new family

To add another intelligence family (e.g., "Executive Continuity" — cross-session goal
recovery), follow the pattern in `IntelligenceDatasets.cs`:

1. Declare seeds with appropriate `LifecycleState` and `Category`.
2. Declare graph edges that reflect the relation types the benchmark relies on
   (`depends_on`, `contradicts`, `elaborates`).
3. Declare tasks using the extended `AgentOutcomeTask` fields
   (`OrderedSteps`, `StaleMemoryIds`, `DistractorMemoryIds`, `ReasoningHops`,
   `TaskFamily`, `MinEvidence`, `MaxEvidence`).
4. Register the dataset in `AgentOutcomeBenchmarkRunner.GetAvailableDatasets()` and
   `CreateDataset()`.
5. Update the tool descriptions in `BenchmarkTools.cs`.

No scoring-layer changes are needed — the metrics already cover the full matrix
because each metric defaults to no-penalty when its driving field is absent.
