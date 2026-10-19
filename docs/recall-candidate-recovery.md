# Scoped candidate recovery

The frozen [noisy recall evaluation](recall-noisy-evaluation.md) exposed two
release blockers on the post-#45 engine: a canonical endpoint answer disappeared
behind reference-only notes at 5,000 memories, and broad/history retrieval could
return another project's or environment's records.

## Changes

Factual recall first uses its existing retrieval and bounded retry. If neither
window contains an eligible source, it now checks the same authorized
tenant/namespace partition with eligibility applied **before** exact cosine
selection. Rejected notes cannot consume the recovery budget. The query embedding,
minimum similarity, lifecycle rules and public result limit remain unchanged.
Eligible summaries remain graph anchors, so a linked source can still keep an
expert route. Only final results receive access or archive updates.

This last fallback is a linear scan under the partition's read lock, not an
unbounded increase to normal ANN/BM25 candidate budgets. It returns a bounded
source pool for the existing token reranker. It can cost more on large namespaces,
especially for negative questions; broadcast can invoke it across accessible
partitions. It does not establish that a matching record
is a complete or true answer. Raw search and MCP schemas are unchanged.

Broad and historical recall now reject explicit conflicting project/environment
scope independently of currentness and the short-query ranking heuristic. The
checks also cover graph candidates, conflict evidence and expert selection.
Broad relevance calibration retains its original candidate pool before final
scope selection, avoiding changes to topic weights caused solely by filtering.
Historical/superseded evidence can remain useful without becoming current again.
Comparisons can retain either requested environment. Broad/history context may
omit a scope label; factual answers still have the existing stricter requirements.
This remains a bounded English lexical guard, not general semantic entailment.

## Regression contract

All evaluation fixture files, question labels, distractors, model and quality
thresholds remain unchanged. Focused tests cover crowded candidate windows,
direct/expert/broadcast routes, restart, lowercase and wrapped scope requests,
longer overviews, graph expansion, read-only archives, explicit namespaces,
minimum scores, tenant twins and private evidence.

Older unit assertions that explicitly expected the two bugs are updated: a
buried answer no longer requires a larger public budget, and historical requests
no longer accept an explicitly different environment. The score-calibration unit
test uses an unscoped value/topic query to retain its original purpose. These
changes do not relabel the frozen evaluation corpora.

The scale CI job now uses `scripts/check-noisy-recall.py --release`. It requires
complete reports, unchanged protection checks and zero predeclared blocker
observations; the gate's criteria are unchanged. Passing this evaluation alone
does not approve a release.

## Validation

On Windows x64, the complete ordinary suite passed **2,328 tests per framework**
on .NET 8, 9 and 10, with no failures or skips. All 27 previous recall reports
match the pre-change reports after excluding timings and validated generated
promotion identifiers/hashes. The 12 report-gate tests, 48 pinned benchmark checks
and all three NuGet packages also pass. The pinned checks are not a fresh MSA run.

The unchanged 26-question scale matrix has 624 repeated route/phase observations.
The endpoint answer is recovered on all routes at 5,000 memories. Declared blocker
observations fall from **32 to zero**, including the broad/history scope mismatches.
All tenant/ACL, read-only archive, threshold, restart and mixed-workload protections
pass. The strict report gate exits zero.

| Memories | Namespaces | Direct answer hits | Automatic answer hits | Broadcast answer hits |
|---:|---:|---:|---:|---:|
| 1,000 | 8 / 32 | 34/44 → 34/44 | 34/44 → 34/44 | 36/44 → 36/44 |
| 5,000 | 8 / 32 | 26/44 → 28/44 | 28/44 → 30/44 | 26/44 → 28/44 |

No previously accepted answer hit was lost in the scale matrix. The 5,000-memory
automatic broad/cross-expert facet coverage increases from 27.8% to 38.9%; direct
and broadcast remain 16.7%. The three routes' 1,000-memory facet coverage is unchanged.

The scan has a measurable cost in this run. At 5,000 memories / eight namespaces,
request p50/p95 were 10.6/25.0 ms direct, 10.8/85.3 ms automatic and 78.3/91.7 ms
broadcast. With 32 namespaces they were 12.4/23.9, 12.4/120.3 and 98.4/120.3 ms.
The prior eight-namespace broadcast sample was 23.4/36.2 ms. These are ordered,
warm, JSON-only samples on one machine, including embedding and access updates;
they show a recovery cost, not a latency SLA or a controlled scaling benchmark.

Each denominator is 22 answerable questions repeated before/after restart, not
44 independent questions. Negative abstentions remain 6/8 per route; the stored
credential-value omission is still returned as context. Novel owner, rollback and
recovery-duration paraphrases and incomplete broad coverage remain follow-ups.
The existing stress-test cutoff concern and release upgrade/soak validation also
remain open; no release is approved by this result.

Run the ordinary suite on all supported frameworks:

```powershell
$env:ENGRAM_RECALL_EVAL_OUTPUT = Join-Path $PWD 'artifacts/scoped-recall/full'
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release --filter 'Category!=MSA&Category!=RecallScale'
```

Run the unchanged scale matrix and strict gate:

```powershell
$env:ENGRAM_RECALL_EVAL_OUTPUT = Join-Path $PWD 'artifacts/scoped-recall/scale'
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release -f net8.0 --filter 'Category=RecallScale'
python scripts/check-noisy-recall.py artifacts/scoped-recall/scale --release
python -m unittest discover -s scripts/tests -p test_noisy_recall_gate.py
```
