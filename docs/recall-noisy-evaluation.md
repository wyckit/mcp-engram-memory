# Noisy recall and scale evaluation

## Pre-run protocol

This protocol and `recall-noisy-v1.json` were frozen before the first measurement on
main `e8b2b6e`. This PR changes evaluation only, not retrieval or scope policy.
The synthetic corpus is not a sample of production traffic. The 12 development
and 14 evaluation questions are both reported; the latter are new frozen questions,
not a permanently blind benchmark after their results have been inspected.

Use the bundled real ONNX model, fixed expert descriptions (never question-equal
routing hints), and public recall defaults. Exercise direct, automatic expert and
broadcast retrieval. Broad and compound questions have labeled answer facets;
underspecified environment questions credit either valid answer and measure coverage
without inventing an abstention requirement. Contradictory unmarked claims have no
declared winner. History questions may return old and current evidence.

Run a small corpus for development, plus a factorial scale matrix of 1,000 and
5,000 data records across 8 and 32 namespaces. Count system/profile/permission rows
separately. Distractors include similar projects and environments, superseded notes,
reference-only notes, near-duplicates and unrelated records. All distinct texts are
embedded with the same model; no random vectors or cloned embeddings substitute for
noise. The generator is deterministic and its full manifest hash is reported.

Report answer-source hit rate, false/correct abstention, project/environment
mismatches, stale top-one selection, facet coverage, unresolved conflicts, actual
route selected, warm query latency, seeding and restart cost, and persisted bytes.
Keep pre/post-restart observations separate. Query timings include embedding and
access updates, but exclude seeding and restart. They are measurements, not a
production latency promise or a fragile shared-runner stopwatch assertion.

Tenant/ACL tests must inspect complete responses, including optional diagnostics,
with same-ID foreign/private decoys and graph edges. Read-only archives must stay
archived and inaccessible evidence must not acquire access updates. Explicit
namespace and minimum-score controls remain enforced. Persistent data and graph
content must survive restart. These are hard test failures, not quality scores.

The quality report is distinct from CI correctness: new questions can expose
pre-existing product gaps without changing their labels to make the run green.
Release blockers are any scope mismatch on an explicitly scoped question, an
unsupported conflict resolution, or a miss/non-abstention on a query marked
`critical` before measurement. Other novel paraphrase misses and incomplete broad
coverage require triage. A strict release report gate must fail when blockers are
present. Evaluation tests verify execution and safety invariants. Following the
[candidate recovery and scope fixes](recall-candidate-recovery.md), scale CI also
requires the strict report gate. It does not certify overall release readiness.

Existing recall fixtures, thresholds and gates remain unchanged. Do not add a
baseline that normalizes away failures found by this evaluation.

## Running and interpreting the checks

```powershell
# Use a fresh evidence directory for each run. No real memory store is accessed.
$env:ENGRAM_RECALL_EVAL_OUTPUT = Join-Path $PWD 'artifacts/noisy-recall-new-run'
$env:ENGRAM_EVAL_REVISION = git rev-parse HEAD
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release -f net8.0 --filter 'Category=RecallScale' --logger 'trx;LogFileName=recall-scale.trx'
python scripts/check-noisy-recall.py $env:ENGRAM_RECALL_EVAL_OUTPUT
python scripts/check-noisy-recall.py $env:ENGRAM_RECALL_EVAL_OUTPUT --release
```

The first Python invocation validates all four matrix cells, manifest/fixture hashes,
unchanged labels, complete route/phase coverage and independently recomputed quality
metrics. It prints **NOT RELEASE READY** when blockers exist but exits successfully
if the evaluation itself is valid. `--release` additionally exits **1** for product
blockers; malformed or missing evidence exits **2**. There is no baseline fallback.
The strict release gate is deliberately separate from evaluation CI, so this PR can
add reproducible evidence of existing failures without concealing them or changing
production behavior. A passing evaluation job is not a release approval.

The small 128-memory case runs on all three test frameworks in ordinary CI. The
four-cell scale matrix runs once on .NET 8 in a separate job with a 20-minute timeout.
Run the small case alone with `--filter FullyQualifiedName~NoisyRecallEvaluationTests`
and validate its report using `--small --framework 8` (or 9/10). The ordinary full
suite uses `--filter 'Category!=MSA&Category!=RecallScale'`; the scale lane is required
alongside it. Missing model files fail rather than skipping or substituting embeddings.

Each scale cell emits a full data manifest and a report with 156 observations:
26 questions x three modes x two phases. These are repeated observations, not 156
independent questions. There are 1,000 or 5,000 data records; automatic mode adds
four expert-profile records. Security fixtures separately mirror the data into an
identified tenant and add six boundary records plus permission metadata. They test
same-ID private/foreign/legacy twins and a distinct public-to-private graph edge.
All fixture roots are temporary local directories.

The test verifies persisted content, vectors, metadata, lifecycle/access state and
graph edges across restart. System profiles and permission rows are loaded explicitly
because ordinary namespace discovery intentionally excludes system files. Restart
timing includes flushing, reopening, loading and verifying the fixture; the subsequent
queries run against that loaded store. This is not a cold-start latency measurement.
The query trace is deliberately stateful and fixed in fixture order: returned memories
can gain access counts or be resurrected before later queries. Each route gets a fresh
store; phase results are reported separately instead of asserting identical rankings.

The scope fixture also runs 32 writes, 16 reads and eight maintenance passes in bounded
dedicated lanes, then verifies the writes after restart. This is a short mixed workload,
not the planned 48-hour release soak. JSON persistence is exercised here; SQLite and
SQL Server release/upgrade checks remain separate. No hand-crafted semantic clusters
or generated summaries are added, and all query language is English. These limits
must accompany any accuracy or latency claim drawn from this corpus.

## Initial findings on the post-#45 engine

Measured on Windows x64, .NET 8, with bundled model SHA-256
`ED65E36025AA94CB74207DAB863C85452919EC0AB7DF3512092932AA22C9A33A`.
The initial and repeated scale runs produced identical quality observations after
excluding timings; the repeated run also verifies that the private graph edge exists
and that its public anchor is retrieved. No production code or frozen labels changed.

Each hit denominator below is **22 answerable questions x two phases = 44**.
Four negative questions x two phases give eight abstention observations per route.
Every matrix cell also has 11 passing boundary/durability checks and a completed
32-write/16-read/eight-maintenance-pass workload. The full matrix has 624 query
observations; it is not a 624-question benchmark.

| Data memories | Namespaces | Direct hits | Automatic hits | Broadcast hits | Blocker observations |
|---:|---:|---:|---:|---:|---:|
| 1,000 | 8 | 34/44 | 34/44 | 36/44 | 2 |
| 1,000 | 32 | 34/44 | 34/44 | 36/44 | 2 |
| 5,000 | 8 | 26/44 | 28/44 | 26/44 | 14 |
| 5,000 | 32 | 26/44 | 28/44 | 26/44 | 14 |

Correct abstentions are **6/8 on every route**: the credential-value question still
returns a note saying the value is not recorded. That query was not marked critical
in the frozen protocol, so it is a visible quality gap rather than silently promoted
into or excluded from the predeclared blocker count.

There are **32 blocker observations across four question IDs**, not 32 independent
defects. The strict release gate exits 1:

1. **Canonical answer loss under noise.** At 5,000 memories,
   `What is Juniper production readiness endpoint?` returns no result in all three
   modes, before and after restart, although its answer remains in the verified store.
   It succeeds at 1,000. This needs candidate-recovery investigation before release.
2. **Project/environment mismatches in broad and historical recall.** The historical
   Juniper timeout question can return Kestrel timeout evidence on broadcast. At 5,000,
   recovery overviews and the automatic deployment/recovery query can include Sorrel
   production notes. These are semantic scope errors among otherwise accessible records;
   they are distinct from a tenant or ACL leak. Namespace access checks passed.

Other visible gaps include `Who looks after Juniper production releases?`, rollback
wording and recovery-duration wording returning no answer, and reference-only notes
displacing broad sources. On the two broad questions plus the cross-expert question,
mean facet coverage is 44.4%/55.6%/36.1% at 1,000 and 16.7%/27.8%/16.7% at 5,000
(direct/automatic/broadcast). This is a different corpus from the older 80.6% broad
supplement and must not be presented as a before/after regression on that supplement.

In the repeated run, 5,000-memory p50/p95 request times were 5.8/17.2 ms direct,
10.9/27.2 ms automatic and 23.4/36.2 ms broadcast with eight namespaces; with 32
namespaces they were 5.6/18.5, 14.8/60.8 and 53.9/70.7 ms respectively. These are small,
ordered warm query samples on one developer machine. The 1,000-memory first cell had
higher tail times than some later 5,000-memory cells, illustrating why this run cannot
justify a precise scaling law or a latency SLA. Seeding includes normal background
persistence and index construction; the report records that cost separately.

An initial full-suite run also failed the unchanged
`CandidateIndexConcurrencyTests.ConcurrentPublishAndRetire_NeverUnpublishesALivePlacement`
on net10 at its cutoff: 7,679 live placements versus 7,680 expected. Inspection finds
a possible test-harness race: worker 0 can publish `stopFromRound = r + 1` before a
slower peer executes round `r`; the peer's unconditional `stopFromRound >= 0` check
then skips work counted as complete. There is no end-of-round barrier. This is a
concrete follow-up for release-test reliability, not proof of a production lost write.
This PR preserves that test and its assertions. Retain the failed TRX even if later
runs pass; investigate/fix the cutoff before treating release validation as reliable.

The [candidate recovery and scope follow-up](recall-candidate-recovery.md) addresses
these recall blockers against the unchanged cases. The independent stress-test
cutoff issue still needs resolution before the final release validation cycle.
