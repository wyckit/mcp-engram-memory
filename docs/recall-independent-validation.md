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

### Limits and remaining release work

Bounded single-process closed-loop load; no saturation/QPS capacity estimate,
multi-process writers, SQL Server, 48-hour soak, or end-to-end stdio client test.
No semantic clusters or generated summaries. These need separate evidence.
The older unexplained .NET 8 CI hang remains unlocalized; PR51's watchdog
improves diagnosis and does not establish its cause.
