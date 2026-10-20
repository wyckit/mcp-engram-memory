# Independent recall and concurrent load validation

## Protocol frozen before measurement

Base: post-PR51 main `f51c16e4935d8c0db3b27bbc528186f4d32ca34f`.
The initial commit of this document and `recall-independent-v1.json` freezes
questions, source memories, labels and acceptance criteria before execution.
This is independently authored synthetic coverage, not independent authorship,
an external dataset, or a permanently unseen holdout. Once measured it is a
regression corpus. Do not change its labels or production retrieval to obtain
a passing result in this evaluation PR.

### Quality matrix

24 new questions (16 answerable, 8 missing-answer), 22 source memories, fixed
non-query-equal expert descriptions. Use the bundled real ONNX embedding model,
the public recall entry point and its default search options. JSON and SQLite,
direct/automatic/broadcast routes, warm and verified persisted restart:
288 observations of 24 questions, not 288 independent questions.

Each observation reports returned source IDs/text identity, source presence,
top-one correctness where requested, all required facets, negative abstention,
project/environment mismatches and unsupported conflict resolution. For direct
recall only the expected sources/facets inside the explicitly requested
namespace count; global gold must never widen direct scope.

Release acceptance for this new suite: every answerable query contains an
expected in-scope source, every requested top-one label is first, all reachable
facets are covered, all missing-answer questions abstain, no wrong-project or
wrong-environment evidence, and no unsupported conflict resolution.
Report each failed condition. A successful execution with quality misses is
**not** release readiness. CI validates complete evidence and safety; a separate
strict release check rejects any quality miss. Existing regression gates stay
strict and unchanged.

### Concurrent workload

JSON and SQLite at 1,000 and 5,000 source memories across 8 namespaces.
Fixed synthetic background records and a stable positive/negative query pair
from the frozen corpus. Real embeddings, no query-result cache or stub vectors.
Measure loaded-restart first request separately from warmed serial and concurrent
requests. No claim of operating-system cold-cache timing.

Per cell, first collect 36 serial reads (12 per route, alternating positive and
negative), and 12 serial writer batches. Then 48 synchronized rounds with three
reader lanes (direct/automatic/broadcast), two independent writer lanes in the
same hot namespace, and one maintenance lane. A writer batch appends a durable
record, replaces its own versioned record, inserts and deletes an ephemeral
record. Maintenance performs real zero-rate decay, preventing lifecycle changes
from obscuring recall correctness. Expect 144 concurrent reads, 96 writer
batches (384 mutations), and 48 maintenance passes. Record actual read/write
request interval overlap; simultaneous thread creation alone is insufficient.

Verify exact operation totals, no worker errors or hangs within a 180-second
per-cell concurrency deadline, positive source presence, negative abstention,
monotonic observed versions for each writer, all acknowledged appended/final
versioned records after flush/restart, absence of deleted records and stable
source content/vectors. Report per-route and writer p50/p95/p99, max, observed
overlap, and serial-versus-concurrent ratios. Timing is diagnostic; the worker
deadline is a liveness bound, **not a product latency SLA**. No percentile
threshold or regression claim is invented after seeing measurements.

Harness detail recorded before measurement: the broadcast control uses a real
empty expert catalogue while sharing the contended data index and authorization
with the other lanes. Automatic routing uses the fixed profiles and may choose
broadcast itself. Load writes exercise index/storage entry operations rather
than `remember`'s duplicate-detection pipeline. Background seed texts have real
embeddings; mutation vectors reuse the positive witness embedding intentionally
to create hard retrieval distractors, not to simulate semantic write ingestion.

### Scope and safety acceptance

Use an identified reader with read-only grants and a distinct tenant. Include
same-ID private-namespace, foreign-tenant and legacy-tenant decoys, plus a real
public-to-private graph edge. During concurrent reads inspect the complete
serialized response for canaries. Explicit denied/empty namespaces must remain
empty; a minimum-score rejection must not recover out-of-scope results.
Inaccessible and read-only archived source records must not change access or
lifecycle state. Recheck after restart. Any scope disclosure, unintended access
mutation, lost acknowledged write, resurrected delete or hang fails execution.

**Protocol correction after initial harness execution:** the access-count clause
above was too strong for authorized reads. Existing `CompleteRecallAsync`
intentionally records access for selected readable entries; only resurrection
requires write access. The first load attempt stopped before concurrency on the
public archived guard's count increasing from 1 to 2. Correct acceptance:
inaccessible records retain both access count and lifecycle state; the readable
archived guard retains its archived state and may accumulate authorized access.
This corrects a harness assumption, preserves the existing API behavior and
does not change any frozen question, expected answer or quality criterion.

### Limits and remaining release work

Bounded single-process closed-loop load; no saturation/QPS capacity estimate,
multi-process writers, SQL Server, 48-hour soak, or end-to-end stdio client test.
No semantic clusters or generated summaries. These need separate evidence.
The older unexplained .NET 8 CI hang remains unlocalized; PR51's watchdog
improves diagnosis and does not establish its cause.

## First complete measurements

Code revision `92dcab1`, Windows x64, .NET 8, bundled bge-micro-v2 model SHA256
`ED65E36025AA94CB74207DAB863C85452919EC0AB7DF3512092932AA22C9A33A`.
Fixture SHA256
`11D86B86DF1629FC9931DE7DC846E4A7B317A9C31F15FB6363D73323CC4F6553`.
The measured test collection runs without other test collections in parallel.
Raw reports include timestamps, full source revision, actual routing strategies,
operation intervals, percentiles and contention ratios.

Both backends produced identical quality outcomes: **40/144 failed observations
per backend, 80/288 overall, across seven distinct questions**. No project or
environment mismatch and no unsupported conflict resolution was observed.
Source presence was 15/16 answerable questions per route/phase/backend; only
3/8 missing-answer questions correctly abstained. These are small synthetic
sample results, not a production accuracy estimate.

| Frozen question | Finding | Affected routes |
|---|---|---|
| `restore-order` | Empty result despite an explicit ordered restore procedure | All |
| `compound-recovery` | Deployment owner returned; backup cadence facet missing | Automatic, broadcast |
| `absent-date` | An unrecorded date note and unrelated facts returned as evidence | All |
| `absent-approval-owner` | An unanswered approval-owner question and deployment owner returned | All |
| `proposal-not-decision` | An explicitly unselected scheduler proposal returned | All |
| `question-not-number` | A request to measure queue depth returned as the answer | All |
| `topic-not-answer` | DNS planning notes without a provider returned | All |

Direct compound recall correctly requires only its namespace-local owner facet.
No question, answer label or quality acceptance condition was changed after
these results. **The strict release gate fails intentionally on these findings.**
Recommended next fixes: non-answer evidence first, then procedural question
interpretation and the missing compound facet, using these unchanged labels.

All four load cells passed safety, operation counts, version checks and verified
restart durability. Totals: 576 concurrent recalls, 1,536 index mutations and
192 maintenance passes; 432 appended records and 12 final version records
survived restart, and 432 ephemeral records remained deleted.

| Backend / base memories | Concurrent phase (s) | Direct p95 (ms) | Automatic p95 (ms) | Broadcast p95 (ms) | Slower writer batch p95 (ms) | Reads overlapping writes |
|---|---:|---:|---:|---:|---:|---:|
| JSON / 1,000 | 2.65 | 61.5 | 79.6 | 83.1 | 41.4 | 142/144 |
| JSON / 5,000 | 5.37 | 77.2 | 153.2 | 142.8 | 49.2 | 144/144 |
| SQLite / 1,000 | 2.25 | 11.2 | 53.5 | 53.4 | 3.2 | 144/144 |
| SQLite / 5,000 | 4.68 | 26.7 | 108.7 | 108.9 | 6.3 | 144/144 |

Each reader distribution has 48 mixed positive/negative samples and each writer
distribution 48 four-operation batches. Serial controls have only 12 samples
per lane; p95/p99 there are the maximum, and p99 for concurrent lanes is also
the maximum. These are exploratory measurements, not statistically established
performance regressions or backend capacity comparisons. JSON writer cost and
broadcast cost warrant profiling if real workloads show latency problems.

## Reproduce and interpret CI

Use a fresh output directory per run. In PowerShell:

```powershell
$env:ENGRAM_INDEPENDENT_OUTPUT = Join-Path $PWD 'artifacts/independent-fresh'
$env:ENGRAM_EVAL_REVISION = git rev-parse HEAD
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release -f net8.0 --filter 'Category=IndependentRecall' --blame-hang-timeout 5m --blame-hang-dump-type mini
python scripts/check-independent-recall.py $env:ENGRAM_INDEPENDENT_OUTPUT
python scripts/check-independent-recall.py $env:ENGRAM_INDEPENDENT_OUTPUT --release
python -m unittest discover -s scripts/tests -p test_independent_recall_gate.py
```

The execution gate returns 0 only with both quality reports and all four load
reports, correct metrics and complete operation evidence. Missing or malformed
evidence returns 2. `--release` additionally returns 1 for any frozen quality
failure. CI uploads the raw reports and explicitly prints **NOT RELEASE READY**
in its summary while the execution/safety check can be green. Existing strict
noisy-recall CI remains unchanged.

The report validator recomputes answer/facet/scope metrics from the frozen
fixture, verifies complete route/phase matrices and source identities, and
recomputes load overlap, percentiles and ratios from raw samples. Its adversarial
tests reject missing/duplicate observations, altered gold, hidden failures,
namespace escapes, incomplete acknowledgements, false safety flags, disjoint
requests, invalid timings and false release claims.

Local regression validation: 2,479 ordinary tests passed on each of .NET 8, 9
and 10; the six new measured backend/load cases plus the quality-oracle test
passed on .NET 8; all 24 existing/new Python report-gate tests passed. The 27
earlier recall reports (2,976 observations) and 624 scale observations match
PR51 after excluding timings and validating generated provenance identities.
All earlier small/scale strict release gates passed. Production source, MCP
interfaces and existing evaluation fixtures are unchanged.
