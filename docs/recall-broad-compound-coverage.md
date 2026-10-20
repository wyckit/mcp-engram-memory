# Broad and compound recall coverage

An overview could retrieve many topic indexes or documentation tasks while
missing the actual facts behind them. A compound request could also stop at one
populated expert, even when another readable namespace held complementary evidence.

Explicit overview/summary requests and unquoted compound topic phrases now use
coverage recovery under the default hybrid/rerank options. Compound phrases can
activate it despite the existing five-word specificity heuristic. Factual
questions, historical queries, numeric/quoted precision requests, `hybrid:false`,
`rerank:false`, `summaryFirst:true`, and spectral `specific`/`none` keep their
existing paths.

For an overview, recovery is needed when the bounded candidate window contains
explicit non-answer notes or no in-scope source. Compound requests always inspect
complementary candidates. The existing exact recovery operation filters the
authorized tenant/namespace before scoring and truncation, using the original
query embedding and cosine floor. The source window remains `k` plus at most 32;
the final response remains at most `k`. This scan is O(N) in each searched
partition. It does not change indexed embeddings, raw search, persisted records,
MCP signatures or response schemas.

The bounded English lexical preference demotes questions, missing values,
document pointers, topic-only lists and plans to document an answer. Ordinary
explanations and fragments remain useful. It is not an entailment checker, and
unclassified or non-answer context remains available when stronger evidence is
absent. Scope checks inspect the original text before any evidence preference.

Recovered pools trade more relevance for diversity (60/40 instead of the existing
80/20 selection blend). A single-topic overview can favor the namespace matching
both its project and topic over one matching only its project. Compound ranking
can use namespace tokens as topic context for terse sources. These are ranking
hints, never permission or scope evidence.

Automatic compound recall retains expert probes and completes the existing
authorized broadcast, reusing each probe once. It combines evidence before final
selection instead of treating one expert's partial answer as complete. Direct
namespace recall keeps its namespace boundary and existing opted-in graph
expansion. Only selected records receive access updates or eligible resurrection.

With graph expansion enabled, compound selection also gives a bounded 0.1
selection bonus to complementary `related_to`/`elaborates` source pairs already
in the eligible pool. There is no degree/count bonus. Both endpoints must be
current, substantive, non-summary sources with distinct text and unambiguous IDs.
Private, foreign-tenant, wrong-scope, below-threshold, superseded and hidden-twin
neighbors cannot supply this support. Similarity alone, `contradicts` and
`supersedes` edges do not supply it. Returned relevance scores are unchanged by
this selection preference.

## Regression contract

The recall fixtures, questions, relevance labels, noise generator, model and
release thresholds are unchanged. The noisy evaluation now requires all existing
overview facets and all automatically accessible compound facets, before and
after restart. For the direct compound case, only the deployment facet lives in
the requested namespace; tests derive its attainable requirement from the frozen
fixture instead of widening the namespace to chase global labels.

Focused tests reproduce source starvation and partial expert routing and cover
scope saturation, mixed/labelled scopes, original cosine thresholds, graph
support/opt-outs, hidden duplicate IDs, read-only archives, access updates,
tenant/private namespaces, non-answer clauses and restart. Earlier failing runs
and final reports are retained under `artifacts/broad-compound-recall/`.

## Validation

The ordinary suites pass **2,470 tests per framework** on .NET 8, 9 and 10,
with no failures or skips in the concurrent run. All 27 earlier recall reports /
2,976 repeated observations match PR #49 after excluding timing and validated
generated promotion identities/hashes. The strict small-corpus gates, 12 report
gate tests, 48 pinned benchmark checks and all three package builds pass. Pinned
checks are not a fresh MSA run.

The final 1,000/5,000-memory × 8/32-namespace matrix passes all 624 observations
and its strict release gate with zero declared blockers. Every route finds
**22/22 answerable cases** and correctly abstains on **4/4 negative cases**, before
and after restart. Fixture, generated-manifest and model hashes match PR #49.
All 552 observations outside the three targeted cases match PR #49 except timing.
Tenant/ACL, graph, threshold, archive, restart and durability checks pass.

| Memories | Direct hits, before → after | Automatic hits | Broadcast hits |
|---|---:|---:|---:|
| 1,000 (8 / 32 namespaces) | 21/22 → 22/22 | 21/22 → 22/22 | 22/22 → 22/22 |
| 5,000 (8 / 32 namespaces) | 20/22 → 22/22 | 21/22 → 22/22 | 20/22 → 22/22 |

All overview facets and automatically accessible compound facets are now present
in every layout/phase. Direct compound recall retains its one reachable facet out
of three; the other two belong to a different namespace with no connecting edge
in this fixture. It does not cross that boundary to improve a metric.

The extra work costs latency. At 5,000 memories, these three requests took about
24–33 ms direct and 93–180 ms broadcast locally. Automatic compound recall took
97–137 ms, versus 13–16 ms on PR #49 when it stopped at one partial expert answer.
These few ordered samples are not a statistical performance comparison or SLA.
The exact scan and wider automatic compound route warrant monitoring on larger
production corpora.

## Reproduction

```powershell
$env:ENGRAM_RECALL_EVAL_OUTPUT = Join-Path $PWD 'artifacts/broad-compound/full'
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release --filter 'Category!=MSA&Category!=RecallScale'
$env:ENGRAM_RECALL_EVAL_OUTPUT = Join-Path $PWD 'artifacts/broad-compound/scale'
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release -f net8.0 --no-build --filter 'Category=RecallScale'
python scripts/check-noisy-recall.py artifacts/broad-compound/scale --release
```

These are authored regression cases, not a production-accuracy estimate. A green
recall matrix does not replace fresh MSA benchmarks, installer/upgrade validation,
soak testing or investigation of previously recorded concurrency-test failures.
