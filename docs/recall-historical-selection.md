# Historical answer selection

A confident current answer previously prevented archive retrieval for historical
questions. Even when both versions were retrieved, relevance alone could put the
current value ahead of the value requested for the earlier period.

Historical selection now searches live and archived records together. It prefers
source evidence for the requested topic and explicit temporal relation, such as
`before the August review`. A matching retirement statement immediately following
the fact can also establish that relation. Plain past/previous questions can
prefer explicitly past or superseded facts. IDs, insertion order, access counts,
metadata and storage timestamps never establish a fact's effective period.

The initial search uses the existing bounded candidate window (up to 32 extra
candidates). If no matching historical source survives it, the existing exact
recovery path filters within the authorized tenant/namespace before scoring and
truncation. That fallback is O(N) in the partition; the public result budget and
cosine threshold remain unchanged. Direct, expert-routed and broadcast recall use
the same selection policy, including after graph expansion and final merging.

Scope checks still apply to the original text. Another project/environment,
private namespace, foreign tenant or below-threshold source cannot become eligible
because it mentions the requested event. Non-answer notes and unrelated temporal
clauses do not receive the historical preference. Only returned memories receive
access updates. Supported historical queries leave archived records archived,
including unmarked past facts: historical usefulness does not make them current.
Scope parsing now looks through historical record labels (including stacked
labels) to inspect the actual subject; `Historical Cedar` cannot masquerade as
unspecified-project context for a Nectar question.

Current questions, replacement questions, incident explanations/lessons,
comparisons, general history requests, `rerank:false` and `summaryFirst:true`
retain their existing selection behavior.
The rule is a bounded English lexical preference, not general temporal reasoning:
it does not calculate date intervals or infer an `as of`/original value. Other
in-scope context stays eligible when a matching historical source is unavailable;
its inclusion is not a claim that it answers the requested period. MCP signatures,
result schemas, embeddings, persisted facts and conflict resolution are unchanged.

## Regression contract

The frozen recall fixtures, questions, relevance labels, noise generator, model
and release thresholds are unchanged. The noisy evaluation now requires its
existing `timeout-history` source at rank one on every route before and after
restart, alongside the six case requirements added in PR #48.

The three original real-embedding historical pair controls now require the
historical source first while retaining both versions and their relevance scores.
The old timeout assertion permitted the current value first; changing that
ordering assertion tests the intended fix without relabeling a corpus.

The 56 new focused tests cover noisy recovery, all routes, hybrid/vector modes,
restart, temporal anchors, wrong periods, reference/unknown notes, adjacent
clauses, lower-case and wrapped questions, current/comparison behavior, opt-outs,
namespace/tenant access, graph scope, thresholds and archive retention. All six
initial route/mode reproductions failed before the production change.
Five additional scope reproductions failed before the label-parsing correction.

## Validation

The complete ordinary suites pass **2,430 tests per framework** on .NET 8, 9 and
10, with no failures or skips in the final concurrent run. All 27 earlier recall
reports / 2,976 repeated observations match PR #48 after excluding timing and
validated generated promotion identities/hashes. The strict small gates, 12
report-gate tests, 48 pinned benchmark checks and all three package builds pass.
Pinned checks are not a fresh MSA run.

The final 1,000/5,000-memory × 8/32-namespace matrix passes its strict release
gate with zero declared blockers. All 24 historical route/layout/restart
observations now return the expected source first. The other 600 observations
match PR #48 excluding timing: no lost answer hits, changed negative outcomes or
scope regressions. All tenant/ACL, graph, threshold, archive and durability checks
pass. Fixture, generated-manifest and model hashes match the baseline.

| Memories | Namespaces | Direct hits | Automatic hits | Broadcast hits | Correct negatives, every route |
|---|---|---:|---:|---:|---:|
| 1,000 | 8 / 32 | 20/22 → 21/22 | 20/22 → 21/22 | 21/22 → 22/22 | 4/4 |
| 5,000 | 8 / 32 | 19/22 → 20/22 | 20/22 → 21/22 | 19/22 → 20/22 | 4/4 |

These are distinct questions within each route/phase; layouts and restart phases
agree. The authored cases do not estimate production accuracy. At 5,000 memories,
the historical question took 8–12 ms direct, 7–9 ms automatic and 79–125 ms
broadcast in this local run, including embedding. The archive/recovery work adds
cost (the previous broadcast observations were 32–65 ms); these few observations
are not a latency guarantee or a statistical performance comparison.

Run the ordinary suites and separate scale matrix:

```powershell
$env:ENGRAM_RECALL_EVAL_OUTPUT = Join-Path $PWD 'artifacts/history/full'
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release --filter 'Category!=MSA&Category!=RecallScale'
$env:ENGRAM_RECALL_EVAL_OUTPUT = Join-Path $PWD 'artifacts/history/scale'
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release -f net8.0 --filter 'Category=RecallScale'
python scripts/check-noisy-recall.py artifacts/history/scale --release
```

This correction does not establish release readiness. Broad/compound answer
coverage, the previously observed concurrency-test reliability failures, fresh
MSA results, installation/upgrade checks and a soak remain separate release work.
