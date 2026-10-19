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
present. Ordinary evaluation CI verifies execution, safety invariants and complete
reports; it does not certify release readiness.

Existing recall fixtures, thresholds and gates remain unchanged. Do not add a
baseline that normalizes away failures found by this evaluation.
