# Realistic recall evaluation and route consistency

The small recall evaluation runs 36 questions against 52 fictional operational memories
using the bundled BGE micro ONNX embedding model and the public `recall` defaults.
The corpus contains current and superseded decisions, exact error codes and commands,
similar projects, summaries alongside sources, archived incidents, and ambiguous queries.
Four questions reload the persisted store before searching. This is a repeatable regression
fixture, not production telemetry or an agent answer-generation benchmark.

## Route contract

Direct, expert-routed, and broadcast recall now use the same namespace retrieval pipeline:

1. Apply `hybrid`, `rerank`, `minScore`, and `summaryFirst` to retrieval.
2. For eligible broad topic queries, probe a bounded live-and-archived candidate pool as described
   below. Otherwise, search archives if the live results are empty or low-scoring.
   Supported factual questions also retry a bounded window when the initial results
   contain no source matching the requested subject, environment and topic.
3. Honor `expandGraph` and apply `spectralMode` within each searched namespace. With reranking
   enabled, prefer unmarked candidates over explicitly superseded records for current-answer
   queries, both before namespace truncation and during final selection.
4. Select at most `k` results. Broad topic selection favors complementary evidence; other
   queries keep linked sources alongside retained summaries when possible. Broadcast combines
   the retained candidates from readable namespaces, including the wider broad topic pools,
   before applying the final selection policy.
5. Diagnose supported unmarked disagreements against a bounded relevance window (see below).
6. Record access only for primary returned memories, using their actual source namespaces. Recover
   returned archived search hits with cosine similarity at least 0.7 only when the caller
   has write permission. Graph-only neighbors, explicitly superseded records, and reported disputed archives do not
   trigger resurrection.

Keyword rescue respects the requested cosine threshold. Expansion from ordinary source
memories can deliberately add related context below that threshold. Summary drilldown
requires compatible embeddings and query cosine at least `minScore`; disabling expansion also prevents broad-mode
traversal from introducing new candidates. Ambiguous bare IDs cannot be used to cross
namespace boundaries. Broadcast deduplicates by namespace plus ID and preserves distinct
memories that happen to share an ID.

Explicit `summaryFirst: true` is honored after hybrid/token/spectral ranking and before
the final limit. When Core `SearchRequest.Diversity` is also requested, MMR still selects
the result set and summary priority orders that selection.
The default remains false; the summary/source pairing below applies with graph expansion on
when broad topic coverage does not activate.
MCP names, argument defaults, primary result schemas, and route strategy labels are unchanged.
Recall adds optional top-level `conflicts` and `queryEvidence` diagnostics.

### Missing answers and project/environment scope

With reranking enabled, current factual questions now screen candidate evidence before
graph expansion and again before final selection on all three routes. For example, an
Atlas production endpoint cannot answer a question about Atlas staging, and a Boreal
deployment owner cannot answer a question about an unknown Orion project. Shared words
such as the project name or database alone cannot substitute for a requested password,
budget, encryption, lifetime, retention, timeout, endpoint, region, jitter or currency attribute. Owner questions
also require an ownership cue. A summary cannot be the only returned factual evidence;
`k: 1` keeps a source, and larger budgets can still pair summaries with sources.

This is a bounded lexical check, **not semantic entailment or an answer generator**. It
recognizes English questions starting with what/which/who/where/when/why/how, plus
bounded request wrappers such as "Please tell me…", "Could you show me…" and
"At what time…". It extracts
names from capitalization and common subject positions (including lowercase subjects),
normalizes a small set of domain aliases, and requires at least half the remaining topic
terms in a matching statement. Explicit environment labels must agree; a namespace
never supplies an unstated environment. An immediately following generic continuation
can inherit scope from the previous statement unless it specifies another recognized
subject or environment. A namespace can supply an omitted project name only for generic
instructions without another explicit name/recognized subject. Namespace membership
alone cannot override an explicit different project. These rules have limited grammar
and synonym coverage; unusual wording and implicit relationships can still be missed
or misclassified. A returned record is neither certified true nor certified complete.

Question scaffolding and pronouns are excluded from name extraction, including title-cased
"Responsible For" and "its" in a timeout question. Lowercase subjects after auxiliaries
require a recognized resource or predicate, avoiding false names such as "changing" in
"Why does changing metadata…". URL/path, retention/retained, backup/snapshot and
recovery/RTO wording share bounded lexical concepts. Endpoint requests do not require
HTTP/probe/call wording in the same statement as the endpoint. Backup-time questions
require a schedule cue, and recovery-duration questions require a duration tied to a
recovery objective or "recover within/in/under" wording; a restore rehearsal or drill
frequency alone is insufficient. QA and UAT are distinct environments, not aliases for
testing or staging. Currency evidence can use an explicit currency term or one of the
recognized uppercase codes (USD, EUR, GBP, CAD, AUD, JPY, CHF, CNY, INR). These are finite
language rules, not unrestricted synonym or currency support. Interpreted questions
use the factual evidence path rather than broad topic coverage.

The existing confidence fallback runs first, preserving ranking when it already finds
matching source evidence. If it does not, recall retries the live and archived pool with
`k + min(max(k, 15), 32)` candidates (subject to integer bounds), then filters again.
The existing vector-only reranker may retrieve twice that pool internally. This adds no
embedding/model call or persistent cache. Candidate checks examine at most 32 statements
per record and reject texts longer than 16,384 characters. Queries over 1,024 characters,
unsupported/non-question forms and questions with no discriminating terms bypass the
check. History/comparison intent, `rerank: false`, and `summaryFirst: true` also preserve
their existing behavior. Broad topic searches retain the coverage policy below. Raw
`search_memory` is unchanged.

When no eligible source survives, `results` is empty and the response adds:

```json
"queryEvidence": {
  "status": "insufficient_evidence",
  "message": "No source in the eligible recall window matched the question's subject, environment and topic. This does not establish that an answer is absent from all stored memories."
}
```

The diagnostic describes this retrieval attempt, not global absence from the store.
It is omitted for retained evidence and bypassed checks; omission is not a positive
answerability signal. No MCP input, tool name, positional result constructor or primary
result field changes. Existing tenant/namespace permissions, cosine thresholds,
contradiction diagnostics and archive resurrection rules still apply. Rejected candidates
and diagnostic-only evidence receive no access or lifecycle updates. A newly stored
answer becomes eligible on the next recall.

`recall-missing-answer-v1.json` adds 16 absent-answer questions over the unchanged
52-memory corpus: four missing attributes, four environment mismatches, four unknown
projects and four unrelated questions. Each is paired with a newly supplied fictional
answer, alongside eight existing positive controls. It was authored before the fix and
is an inspected regression set, not a blind holdout. Real ONNX embeddings and default
direct/expert/broadcast recall produce 120 observations per framework. Before the fix,
all **48 negative observations returned context**; after the fix, all **48 return empty
evidence with the diagnostic**, while all **72 positive observations retain their
expected source**. Returning unrelated context is a retrieval failure here, not a measured
LLM hallucination.

Against merged PR #40, all 108 original query/route observations retain or improve
Hit@5, Recall@5, source recall, MRR, nDCG and stale-top-answer rate. The broad supplement
and contradiction/resolution gates remain in place. Original corpus labels, baselines
and floors are unchanged. `RecallQueryEvidenceTests` adds controls for lowercase names,
sentence continuations, missing attributes, environment aliases, wrong-scope results
ahead of a valid source, summary-only windows, graph neighbors, archive recovery,
private/foreign tenant twins, opt-outs and bounded retrieval. The original hybrid score
calibration control now uses a topic query so both projects remain eligible for that
comparison; separate factual-question controls require the wrong project to be excluded.

Reports are `recall-realistic-missing-answer-net*.json`, uploaded by the existing CI
artifact rule. They distinguish correct abstentions from positive-source hits and retain
each query/route's returned IDs and diagnostic status for inspection.

### Question interpretation and false abstentions

`recall-question-interpretation-v1.json` freezes the 32 questions from the assessment
after merged PR #41: eight canonical controls, 16 paraphrases and eight negative cases.
The original 52-memory corpus and designated source labels are unchanged. Each negative
is paired with a newly supplied answer to detect an always-abstain solution. The archived
Engram cache incident was already audited as a valid alternative for the metadata question
before implementation; it is listed separately from that question's designated source.

Real ONNX/default recall on direct, expert and broadcast routes produces 120 observations:
24 missing-answer observations and 96 answerable observations. Before this change, 12/24
correctly abstained and 66/96 found an accepted answer (63 designated-source hits). After
the change, 24/24 correctly abstain and 96/96 find an accepted answer (93 designated-source
hits). All seven previously empty paraphrases also retrieve their isolated correct source
on all three routes. These inspected regression cases do not estimate general answer accuracy.

Reports are `recall-realistic-question-interpretation-net*.json`, uploaded by the existing
CI rule. They retain designated hits, accepted alternative hits, returned IDs, diagnostics
and direct/expert parity separately. Expert tests provide a perfect routing hint and
exercise interpretation after selection; empty-expert fallback remains a separate task.
Controlled cases additionally reject wrong projects/environments, incomplete attributes,
historical restore records, drill durations, stored questions and incompatible sentence
continuations, while checking selected-only access/archive behavior and opt-outs.
Existing missing-answer, original recall, broad coverage and contradiction/resolution
fixtures and gates are retained without relabeling or lowering floors.

### Hybrid-search filter enforcement

Keyword candidates must pass the requested lifecycle and category filters before entering
rank fusion, including when the semantic gate is inactive or there are no eligible vector
hits. The fusion result builder uses only those admitted entries; it cannot resolve rejected
keyword hits again. Previously, a live-only or category-filtered hybrid search could return
excluded records, even as its sole top result. Namespace, tenant and minimum-cosine checks
continue to apply through the index's scoped entry resolver.

Explicit archive searches still return matching archives, and `recall` can deliberately
widen to archived memories through its low-confidence fallback or broad topic coverage. Raw search does
not resurrect returned archives. Empty lifecycle sets return no results.

`HybridSearchFilterTests` covers these contracts, keyword-only rescue, the semantic gate
on/off, reranking on/off, small result budgets, and the fusion, cascade and high-confidence
paths. Sixteen of its 29 cases fail against the pre-fix implementation. Against merged
PR #38, all 108 observations in the unchanged realistic corpus retain Hit@5, Recall@5,
source recall, MRR, nDCG and stale-top-answer rate. Four result lists change in their
non-answer context (`s04` on all routes and `a06` on broadcast). The original and broader
contradiction reports and governed-resolution report retain their decisions and citations.
No fixture labels, quality floors, MCP arguments or result schemas change.

### Comparable broadcast scores

For current-answer queries with hybrid search and reranking enabled, broadcast recalculates
scores when more than one namespace contributes candidates. Each candidate uses the same
blend of actual query cosine (60%) and token relevance (40%), including singleton namespace
pools; queries with no lexical terms use cosine alone. Previously, a namespace taking the hybrid high-confidence shortcut returned cosine-based
scores while another taking rank fusion returned much smaller RRF-based scores. Comparing
those values directly could exclude the correct project's memories from the final budget.

For queries outside the broad topic policy below, calibration occurs after each namespace's retrieval, archive fallback, graph expansion and
spectral selection. Those stages still determine the candidates; their local score boosts
are not carried into the cross-namespace relevance comparison. Final status ordering and
summary/source pairing still apply. The merge keeps namespace-qualified identities and the
original search evidence when a memory also arrives through another namespace's graph.
It does not widen permissions or the primary result budget. Direct/expert recall, single
contributing namespace recall, raw search, vector-only search, historical/comparison intent
and `rerank: false` retain their existing score behavior.

Against merged PR #37 (`108504a1`), the unchanged 36-question corpus has no per-query
regressions in Hit@5, Recall@5, source recall, MRR, nDCG or stale-top-answer rate. All 72
direct/expert observations retain their IDs and quality metrics. Broadcast changes the
returned IDs/order for 29 questions; four improve measured answer quality and the rest
retain it. Overall broadcast MRR rises from **0.9199 to 0.9537**, Recall@5 from **0.9074
to 0.9167**, source recall from **0.8981 to 0.9074**, and nDCG from **0.8857 to 0.9100**.
Hit@5 remains 1.0 and stale-top-answer rate remains zero. No baseline or labels were changed.

| Broadcast case | Before → after |
|----------------|----------------|
| `c06`, Atlas rollback approval | MRR 0.25 → 0.50; nDCG 0.4307 → 0.6309 |
| `s01`, Engram cache invalidation | nDCG 0.8772 → 1.0000 |
| `b03`, Engram caching | MRR 0.3333 → 0.50; nDCG 0.2346 → 0.2961 |
| `b04`, Atlas retries | Recall@5 0.3333 → 0.6667; MRR 0.20 → 1.00; nDCG 0.1815 → 0.6714 |

These are inspected regression cases, not a claim about unseen query quality.

## Broad topic coverage

Short topic queries can lose supporting facts when the initial five candidates contain an
overview and several similar general notes. With the public defaults, recall now probes
`k + min(max(k, 15), 32)` live-and-archived candidates per searched namespace (20 for `k: 5`).
This is a search result budget; the index's existing internal retrieval and graph expansion
still apply. The final public limit stays `k`, and no extra embedding or model call is made.

The policy requires hybrid search, reranking, `summaryFirst: false`, current intent, and
`spectralMode: auto` or `broad`. The existing broad heuristic must identify a short conceptual
query (fewer than five words, without numbers or a quoted phrase). Question prefixes such as
`who`, `what`, and `how` keep the prior selection policy. A distinct, non-summary,
non-superseded source must contain a query term that occurs in at most half of the distinct
source texts. Environment/overview terms (`production`, `staging`, `development`, `testing`,
`overview`, `summary`) cannot activate coverage alone. If no distinguishing topic evidence
exists, recall uses the prior pipeline, including confidence-triggered archive fallback.
This keeps generic project overviews from overemphasizing an incidental matching word.

Eligible candidates use 60% actual query cosine and 40% weighted query-term coverage.
Terms common throughout the candidate pool receive less weight than distinguishing topics;
exact text copies do not count as independent documents. Local normalization bridges common
forms such as cache/caching, retry/retries, and retrieval/retrieved without changing the
index tokenizer or raw search. Graph expansion and broad cluster ranking then apply as before.
Broadcast recalibrates authorized candidates together and applies a soft 25% discount to
other namespaces when a namespace name matches a query term. This is a ranking hint, not a
filter or a permission rule; namespace-qualified IDs and search evidence are retained.

Final selection preserves explicit status priority and the strongest first hit. Subsequent
choices balance relevance (80%) against the maximum cosine similarity to an already selected
candidate (20%). Sources are compared with sources, and summaries with summaries: a useful
overview must not suppress its own evidence. This replaces the fixed two-source pairing
only for an activated broad topic pool. Diversity can change order without changing returned
scores. `expandGraph: false` still prevents graph additions, while topic selection can use
independently retrieved sources. Archives can complement a confident live overview, but only
returned, eligible primary hits receive the existing access/resurrection updates.

### Coverage evaluation

Against merged PR #39 (`69dcfe5`), the original six broad questions improve from **8/18 to
12/18 labeled sources** within five results on each route. Caching improves from 1/3 to 3/3,
retries from 2/3 to 3/3, and retrieval from 1/3 to 2/3. The other three broad cases retain
their measured source coverage. The original 30 non-broad questions keep their result IDs
and quality metrics; no original per-query quality metric regresses. Existing conflict and
resolution gates still pass. One broad timeout case changes which unresolved claim is the
primary result, while retaining both claims in its warning.

`recall-broad-v1.json` supplements the unchanged original corpus with 12 graded questions:
the six broad topics and six short paraphrases. It credits alternative useful sources and
groups them into answer facets. Grades are 2 for direct evidence, 1 for useful context, and
0 for unlisted records. Labels were authored before implementation, and
`recall-broad-v1.baseline.json` freezes the pre-change measurements. Every query/route must
cover at least two facets and retain its baseline facet count and graded nDCG@5.

| Measure | Before | After |
|---------|--------|-------|
| Mean facet coverage, all routes | 58.3% | 80.6% |
| Graded nDCG@5, direct/expert | 0.6075 | 0.7583 |
| Graded nDCG@5, broadcast | 0.5982 | 0.7500 |

Reports are `recall-realistic-broad-coverage-net*.json` and join the existing CI artifacts.
Controlled cases cover sources outside the initial five without graph links, archived
context and final-only mutations, score thresholds, opt-outs, short questions, namespace
and tenant isolation, duplicate IDs, morphology and candidate bounds. These are inspected,
authored regression cases, not a blind holdout or a production accuracy/latency guarantee.
Coverage remains incomplete: the original source metric is 12/18, and the graded supplement
still returns some unlisted context. The bounded candidate pool, English lexical heuristics,
and namespace hint cannot guarantee coverage for arbitrary queries or larger corpora.

## Summary and source selection

An overview can link many unrelated facts. Its links do not establish that those facts form
one query-relevant cluster: broad-mode dominance and traversal now use source-to-source
connections, excluding summary nodes even when they are reached outside the initial pool.
A relevant summary can still be returned at its retrieval score.

New graph candidates reached from a summary receive the summary's retrieval score multiplied
by their query cosine, clamped to `[0, 1]`. This ranks the drilldown by query relevance without
comparing raw cosine to hybrid/RRF scores or giving every linked fact the same score.
Already-retrieved candidates keep their scores.

Outside activated broad topic coverage, each retained summary can bring up to two of its highest-scored
linked non-summary candidates into the result budget. This allows an overview, two supporting
details, and independent hits within the default five slots. The highest-ranked hit after status ordering is
protected; small budgets and earlier pairings can leave fewer source slots. Supporting
candidates follow their summary, unless they were already ranked ahead of it. Selection
does not change scores, so the returned order need not be strictly descending by score.
Shared sources appear once, and `contradicts` links do not force a supporting pair.

Pairing only uses candidates already retrieved through authorized, tenant-scoped search
and graph expansion. It does not add a model call, fetch missing sources, or relax lifecycle
and namespace access checks. A summary without eligible sources remains usable. Setting
`summaryFirst: true` keeps explicit summary priority and bypasses pairing; `expandGraph: false`
also bypasses pairing. These switches do not restore summary-based broad cluster boosts.

## Superseded answers and historical queries

Default `recall` selection places candidates explicitly marked as superseded/obsolete after
other candidates. This applies on direct, expert, and broadcast routes, including after graph
boosts and summary selection. It does not rewrite relevance scores or remove stored memories.
An unmarked candidate is not necessarily current or authoritative; it simply lacks a recognized
obsolete-record declaration.

The local, case-insensitive English detector recognizes explicit leading labels such as
`[obsolete]` or `Superseded:`, leading past-record descriptions such as `Superseded ... was ...`
or `Superseded ... decision from ...`, and self-status sentences such as `This ownership record
is obsolete after the handover.` Mentions in other records, negations (`is not obsolete`), and
conditional wording (`is obsolete only if ...`) are not enough. It does not inspect memory IDs,
creation/access dates, fixture labels, or graph direction to infer which fact is correct.

Outside the already widened broad topic pool, if the initial retrieval contains a superseded candidate and fewer than `k` unmarked alternatives,
recall retries that search once with a requested pool of `2k`. Existing vector/token oversampling
still applies, and `minScore`, lifecycle filters, tenant, and namespace permissions stay fixed.
This prevents a stale first hit from hiding a replacement just outside a small result limit.
There is no additional embedding or model call, but affected namespaces may need another search.
The retry is bounded and does not guarantee discovery of every replacement.

Summary pairing does not force superseded sources back ahead of unmarked candidates. Explicit
`summaryFirst: true` still takes precedence over status ordering between summaries and ordinary
memories. `rerank: false` bypasses status ordering and the wider search. Recognized historical or
comparison queries also retain relevance ordering, for example `What was ... before the migration?`,
`Show the historical ...`, and `Compare previous and current ...`. Replacement questions such as
`Which timeout replaced the old setting?` continue to prefer unmarked candidates. These are limited
language cues, not a general temporal-query parser; `rerank: false` provides an explicit opt-out.

When only superseded hits are available, they can still be returned. A recognized superseded
archive remains archived even for a historical query or with reranking disabled: recalling history
does not make the fact current again. Other archive recovery and final-only access tracking are
unchanged. The lower-level `search_memory` and Core ranking APIs are unchanged.

## Cases and measurements

The versioned fixture and its initial quality reference live in
[`tests/McpEngramMemory.Tests/Fixtures`](../tests/McpEngramMemory.Tests/Fixtures).
There are 24 development questions and 12 held-out paraphrases, with six questions per
scenario. Labels come from the authored source facts. Held-out here means a fixed separate
reporting split over the same corpus, not an independent hidden production dataset.
Both splits have been inspected during summary-selection development; the held-out name
does not imply blind validation. The original labels and aggregate baseline remain unchanged.

Each question gets a fresh store for each route, so access and resurrection from a previous
query cannot influence it. Expert profiles deliberately match the query: this checks the
retrieval route after correct dispatch, not expert-selection accuracy. Broadcast searches
all four project namespaces. The test requires the bundled model and does not silently skip.

Reports include each query's returned IDs and these aggregate metrics per split and route:

| Metric | Definition |
|---|---|
| Hit@5 | Fraction of questions with any labeled relevant result in the first five |
| Recall@5 | Fraction of labeled relevant memories returned, averaged per question |
| Source Recall@5 | Same calculation after excluding summary nodes from relevant labels |
| MRR | Reciprocal rank of the first relevant result; zero on a miss |
| nDCG@5 | Discounted relevance with binary labels, normalized by the ideal top five |
| Stale Top-1 | Fraction of questions whose first result is a labeled superseded answer |
| Summary share | Fraction of returned entries marked as summaries |
| Redundant pair share | Fraction of returned pairs with embedding cosine at least 0.95 |
| Returned characters | Total returned text length; not a token estimate |
| p50/p95 latency | Nearest-rank percentiles, including query embedding, retrieval, and result updates |

Latency uses a warmed embedding model and excludes corpus construction and restart time.
It is diagnostic: machine load, OS, and hardware matter, so CI does not impose timing floors.
The dataset is too small to test HNSW behavior or large-store broadcast costs. Controlled
route tests separately cover qualifying diffusion graphs, option combinations, tenant/ACL
boundaries, duplicate IDs, metadata, and archive mutations.

## Initial comparison

Measured on October 18, 2026 against merged main `4a1ba7e`, with the same corpus, model,
and test harness. All 36 questions are included below; the JSON reports retain both splits.

| Route | Hit@5 before → after | Recall@5 before → after | MRR before → after |
|---|---|---|---|
| Direct | 1.000 → 0.972 | 0.935 → 0.912 | 0.921 → 0.905 |
| Expert | 0.806 → 0.972 | 0.748 → 0.912 | 0.738 → 0.905 |
| Broadcast | 0.833 → 0.944 | 0.731 → 0.903 | 0.769 → 0.875 |

Expert/direct ID and score equality improves from 10/36 to 36/36 questions. The original
code fails the evaluation's expert hit-rate floor and route parity check.

There is a measured direct-route trade-off: archive fallback previously replaced hybrid
results with vector-only results. Preserving the requested hybrid policy changes that
ordering. `b03` ("Engram caching") now misses its source labels, and `c05` puts the old
timeout before the corrected value. Across routes, `c03` also puts an old region first;
`c06` and `b05` illustrate summaries competing with specific sources. Stale Top-1 after
the change is 2/36 for every route (previously 1/36 direct, 2/36 expert, 1/36 broadcast).
These were the remaining failures after the route-consistency change;
the corpus labels have not been changed to count obsolete facts as correct.

## Summary-selection comparison

Measured against merged PR #32 (`74600b3e`), using the same corpus, labels, model, and
public defaults. Each cell below is before → after; values are rounded to four decimals.

| Split / route | Hit@5 | Recall@5 | Source Recall@5 | MRR | nDCG@5 |
|---|---|---|---|---|---|
| Development / direct | 0.9583 → 1.0000 | 0.9063 → 0.9132 | 0.9028 → 0.9028 | 0.9375 → 0.9583 | 0.9001 → 0.9030 |
| Development / expert | 0.9583 → 1.0000 | 0.9063 → 0.9132 | 0.9028 → 0.9028 | 0.9375 → 0.9583 | 0.9001 → 0.9030 |
| Development / broadcast | 0.9167 → 1.0000 | 0.8924 → 0.8993 | 0.8889 → 0.8889 | 0.8958 → 0.9181 | 0.8755 → 0.8758 |
| Held-out / direct | 1.0000 → 1.0000 | 0.9236 → 0.9236 | 0.9167 → 0.9167 | 0.8403 → 0.8472 | 0.8434 → 0.8498 |
| Held-out / expert | 1.0000 → 1.0000 | 0.9236 → 0.9236 | 0.9167 → 0.9167 | 0.8403 → 0.8472 | 0.8434 → 0.8498 |
| Held-out / broadcast | 1.0000 → 1.0000 | 0.9236 → 0.9236 | 0.9167 → 0.9167 | 0.8333 → 0.8403 | 0.8376 → 0.8440 |

All three routes now hit a labeled relevant result for all 36 questions. `b03` ("Engram
caching") retrieves `engram-cache` at rank 2 direct/expert and rank 3 broadcast, where it
previously missed. `b04` also gains a broadcast hit, and `b05` retrieves a relevant source
at rank 3 instead of 4. Direct/expert IDs and scores still agree on all 36 questions.

The per-query trade-off is `b01` ("Atlas operations"): Recall@5 falls from 0.75 to 0.25
on every route. Previously, the summary hub gave unrelated sources equal boosted scores,
and ID tie-breaking happened to select labeled backup/endpoint facts. Query-based drilldown
now selects owner/drill details, including a superseded owner, instead. The fixture labels
have not been broadened to hide that loss. Aggregate source recall is unchanged because
source gains on other questions offset it. Correction ranking is still needed: `c03` and
`c05` continue to return superseded answers first (Stale Top-1 remains 2/36 per route).

Controlled tests cover summary hubs, summaries beyond the first graph hop, source relevance
and thresholds, small/shared budgets, explicit options, duplicate IDs, tenant/namespace
permissions, and access/archive effects. The real-model evaluation additionally pins the
recovered `b03`/`b04` hits and `b05` source rank so aggregate gains cannot conceal their return.

## Stale-answer ranking comparison

Measured against merged PR #33 (`1879949`) with the same 52 memories, 36 questions, relevance
labels, and original quality reference. All 108 query/route observations still hit a relevant
memory. Recall@5 and source recall are unchanged, and no per-query Recall/MRR/nDCG regresses.
Direct/expert IDs and scores continue to agree on all 36 questions.

| Split / route | MRR before → after | nDCG@5 before → after | Stale top answers before → after |
|---|---|---|---|
| Development / direct | 0.9583 → 0.9792 | 0.9030 → 0.9184 | 1/24 → 0/24 |
| Development / expert | 0.9583 → 0.9792 | 0.9030 → 0.9184 | 1/24 → 0/24 |
| Development / broadcast | 0.9181 → 0.9389 | 0.8758 → 0.8912 | 1/24 → 0/24 |
| Held-out / direct | 0.8472 → 0.8889 | 0.8498 → 0.8805 | 1/12 → 0/12 |
| Held-out / expert | 0.8472 → 0.8889 | 0.8498 → 0.8805 | 1/12 → 0/12 |
| Held-out / broadcast | 0.8403 → 0.8819 | 0.8440 → 0.8748 | 1/12 → 0/12 |

The current region (`c03`) and timeout (`c05`) move from rank 2 to rank 1 on every route.
New gates require zero labeled stale top answers and rank 1 on those two cases. Labels are
used only to evaluate results, never passed to retrieval. Separate real-model controls use
the same three old/current fact pairs and historical questions on all routes: both versions
remain retrievable with the original relevance scores and order. This does not guarantee the
historical version ranks first; the timeout history control still ranks the current record
first under the unchanged relevance model.

Controlled tests cover status declarations versus mentions/negation/conditions, replacement
versus history intent, `k=1`, hybrid/vector paths, graph boosts, summary priority, restart,
permissions, thresholds, final-only access updates, and obsolete archive recovery. Unmarked
stale facts remain unresolved, as do general date/authority inference and the existing `b01`
broad-query coverage weakness. Both fixture splits remain inspected regression cases rather
than blind validation.

## Unmarked disagreements

Two memories can give different values without either being labeled superseded. For
example, `Atlas gateway timeout is 30 seconds.` and `Atlas gateway timeout is 45 seconds.`
do not establish which value is authoritative. Default current-intent `recall` now returns
an optional `conflicts` diagnostic with both original texts and namespace-qualified IDs:

```json
{
  "conflicts": [{
    "status": "unresolved",
    "basis": "same_subject_different_value",
    "subject": "atlas gateway timeout",
    "evidence": [
      { "id": "a", "ns": "atlas", "text": "Atlas gateway timeout is 30 seconds." },
      { "id": "b", "ns": "atlas", "text": "Atlas gateway timeout is 45 seconds." }
    ]
  }]
}
```

Consumers should verify a flagged claim against its source before treating either value
as current. Primary `results` keep their existing order, scores, metadata, and `k` limit.
Diagnostic evidence is additional context, so `k: 1` can still reveal both claims.
Existing inputs, defaults, tool registrations, and the `RecallResult` constructor remain
unchanged. The new output property is omitted when empty. `search_memory`, `cross_search`,
and the separate `find_contradictions` tool are unchanged.

The local grammar compares the same subject within the same namespace. It recognizes
English `subject is value` sentences for duration settings (timeout, TTL,
retention), integer settings (retry limit, replica count, batch size, port), owner, region,
endpoint path, version, and enabled/disabled state. Whitespace/case, duration units, numeric
formatting, and explicit boolean negation are normalized. Duration and enabled state are
different claim types. Subjects must match after normalization; project/environment words
are retained. Summary nodes, superseded records, quoted examples, and supported conditional,
historical, or proposed wording are excluded.

Supported paraphrases map to these same typed properties:

| Form | Comparable claim |
|---|---|
| `Atlas gateway times out after 30 seconds` | `Atlas gateway timeout is 30 seconds` |
| `The timeout for the Atlas gateway is 30 seconds` | Same timeout property |
| `Atlas's gateway timeout: 30 seconds` | Same timeout property |
| `Atlas uses a gateway timeout of 30 seconds` | Same timeout property |
| `Atlas gateway timeout is configured to 30 seconds` | Same timeout property |
| `Maya owns Atlas deployments` / `Atlas deployments are owned by Maya` / `Maya is responsible for Atlas deployments` | `Atlas deployment owner is Maya` |
| `Atlas primary database is hosted in us-east-1` | `Atlas primary database region is us-east-1` |
| `Audit logging is enabled for Atlas` | `Atlas audit logging is enabled` |

Location aliases also accept `is located in` and `runs in`, but only with region-shaped
identifiers such as `us-east-1`; `runs in Docker` is not inferred to be a region. Settings
also accept `is set to`, `is configured as`, `equals`, and `=`. Boolean `on`/`off` map to
enabled/disabled. Singular possessives and supported `isn't`/`doesn't` contractions normalize;
other quotation remains excluded. Ownership aliases still assume one owner for the matched
scope and report a *possible* disagreement, not verified exclusivity.

An explicit value exclusion conflicts only with the matching positive value:
`timeout is not 30 seconds` conflicts with `timeout is 30 seconds`, while `45 seconds`
and another exclusion remain compatible. The supported predicates `supports`, `requires`,
`allows`/`permits`, and `uses` can be compared with explicit `does not` denials. Predicate
and object are part of that claim's key: supporting Linux and supporting Windows can both
be true; supporting authentication does not imply requiring it. Such denials use
`basis: "explicit_negation"`. The existing value-disagreement basis is unchanged.

Extraction now reads up to eight sentences or semicolon/newline clauses and keeps up to
four distinct claims per memory (2,048-character record and 256-character statement limits).
All extracted claims participate, so an agreeing first fact cannot hide a later conflict.
The same evidence pair is reported once. Record-wide qualification is checked even outside
that extraction window: an `If approved` or `Proposed settings` heading must not turn its
following sentences into unconditional facts. Supported conditional, historical, example,
modal, and ambiguous wording conservatively excludes the whole record.

When the initial retrieval includes a supported claim, recall inspects a bounded relevance
window in that same authorized namespace: requested `k + min(max(k, 8), 32)` candidates, reusing
existing vector/status overfetch when sufficient. Vector reranking retains its existing
internal 2x overfetch. At most 64 evidence candidates per namespace, 32 selected primary
hits, and 8 distinct conflict pairs are examined/reported. The original `minScore`, tenant,
and chosen lifecycle scope apply. Archives enter through fallback or activated broad topic coverage; candidates
named in a reported conflict remain archived. Evidence-only hits receive no access or
lifecycle update, and no text, metadata, or graph links are written by diagnosis.

Recognized historical/comparison queries and `rerank: false` bypass this current-intent
diagnostic. Graph expansion and summary preference keep their existing behavior; a primary
summary is not treated as a factual claimant. Cross-namespace graph neighbors are not probed
as if they belonged to the original namespace. No timestamp, insertion order, ID, arbitrary
metadata, graph direction, or vector similarity is treated as proof of truth.

The detector provides conservative conflict surfacing, not general contradiction detection.
It can miss unlisted phrasings, unsupported languages/values, distant candidates,
cross-namespace conflicts, or qualified claims. A missing diagnostic does not certify
consistency. Ingest-time duplicate blocking is unchanged. Explicit governed evidence can
now supply a preference for a detected pair, subject to the checks below.

### Evaluation

`recall-conflicts-v1.json` adds 16 inspected regression cases. Each case seeds the unchanged
52-memory corpus plus two unmarked assertions with opaque IDs and no graph links or authority
metadata. Eight cases cover disagreements (timeout, `k: 1`, owner, region, logging, retry limit,
endpoint, version); eight controls cover equivalent units/negation, different project/environment,
conditional/history/example wording, and historical query intent. Two cases restart the store.
The mandatory bundled ONNX model runs every case through direct, expert, and broadcast recall.

All 24 positive query/route observations surface both claims; all 24 controls have no conflict
diagnostic. Expert/direct IDs, scores, and diagnostics agree. On the original 36 questions,
all 108 result lists and quality metrics match merged PR #34 (`844c658`), with zero labeled
stale top answers and zero new conflict warnings. Original labels and baseline gates are
unchanged. These results measure a small authored grammar-focused corpus, not open-domain
contradiction accuracy or factual resolution.

Controlled tests additionally cover thresholds, tenant/namespace isolation, duplicate IDs,
summary/explicit-status exclusions, opt-out behavior, metadata, archive mutation, output bounds,
and preservation of primary ranking. Conflict reports are named
`recall-realistic-conflicts-net*.json`, so CI uploads them with the existing evaluation reports.

### Broader detection comparison

`recall-conflicts-broad-v1.json` adds 32 cases without changing the original 36 questions,
the earlier 16 conflict cases, or their labels. Each new case again seeds the original 52
memories plus two opaque-ID assertions and runs all three routes with the real ONNX model.
Sixteen positive cases cover paraphrased settings/ownership/location, explicit denial,
later sentences, `k: 1`, and persisted restarts. Sixteen negative controls cover equivalent
phrasing, compatible exclusions, multiple supported platforms, different predicates,
project/environment/region roles, qualifications, and historical queries.

Against merged PR #35 (`733f5ab`), the 48 positive query/route observations had zero reported
conflicts. With broader detection all 48 are reported; all 48 negative observations remain
free of conflict warnings. The earlier supplement retains its 24/24 positives and zero
false alarms in 24 controls. Direct/expert results, scores, and diagnostics agree. Primary
result lists and quality metrics for the original 108 observations are unchanged.

Additional controlled tests compare later claims when the first claim agrees, deduplicate
multiple disagreements between the same evidence pair, preserve paragraph qualification,
and check private/foreign evidence and extraction bounds. Two old grammar-rejection cases
(`is not 30 seconds`, `supports Python`) are now supported and are covered by explicit
denial and compatibility tests. The fixtures remain inspected, authored regression cases;
these numbers do not establish open-domain semantic accuracy. No model/dependency, inference
call, result schema, retrieval limit, or authority-resolution behavior was added. New reports
are named `recall-realistic-broader-conflicts-net*.json` and are included in the CI artifact.

## Evidence-based resolution

Recall can now attach a supported choice to a detected disagreement without rewriting
either memory or changing retrieval scores, ordering, or `k`. This is a read-time,
pair-scoped decision based on the accessible evidence inspected during that call. It is
not an external truth verifier, a new authority score, or a persistent adjudication.
The existing governed verifier checks source stability, not semantic truth; recall
additionally requires the governed claim and its source text to match the disputed facts
using the same bounded English parser as detection. No model call or dependency is added.

### Connect an existing governed publication

1. Use a named principal with a nonempty tenant and an owned namespace. These are the
   existing `promote_knowledge` requirements; legacy/default mode cannot create this
   authority. Publishing through MCP requires the `full` profile. Reading an already
   published reference through `recall` works in every profile with the built-in host.
2. Retain a separate source memory containing evidence for the claim. Call
   `promote_knowledge` with the claim and the exact source ID(s). Preserve any known
   contradicting evidence; a disputed publication will not resolve a recall conflict.
3. Attach the returned artifact ID and content hash to the candidate memory's metadata
   as `recallKnowledge`, preserving its text and other metadata. The reference is scoped
   to the candidate's namespace and the caller's tenant; it cannot redirect either one:

   ```json
   {
     "recallKnowledge": {
       "id": "atlas-gateway-timeout",
       "contentHash": "<exact 64-character contentHash returned by promote_knowledge>"
     }
   }
   ```

   `remember` accepts this nested metadata object. If its existing near-duplicate guard
   blocks an intentional update among contradictory memories, use `store_memory` with
   the same ID, text, category, metadata and lifecycle state. That explicit upsert bypasses
   duplicate blocking; it is not a metadata-only patch. Do not update the cited source
   itself with this reference: candidates cannot cite themselves or the opposing memory.

The metadata is only a locator. It cannot assert verification, authority, permissions or
support. The version must still be the active committed version with a valid canonical
hash, `Active` status, `Supported`/`Verified`/`Established` maturity, current validity, no
supersession timestamp, and no attached contradicting evidence. Recall checks readable
version/evidence/provenance permissions, the publication's evidence-verifier reference,
and its matching allowed commit audit. Maturity alone does not establish truth.

Every supporting source must still exist in the exact tenant/namespace, be readable,
be live and non-summary, and match the publication's existing content hash (identity,
text, category and metadata). Each source must address a disputed fact, none may contradict
one, and together they must cover **all** parsed disagreements in the pair. Internal
contradictions in the proposed preferred memory also prevent resolution. Hashes prove
content correspondence, not source reliability or real-world independence. A separate
memory can still be a copied or mistaken source; publication and source quality remain
the publisher's responsibility.

Two cited sides, even if one citation is invalid or inaccessible, remain unresolved.
An unresolved cited disagreement also vetoes preferences in its connected group of
reported conflicts. Uncited repetitions neither add support nor constitute a vote.
Missing/inaccessible/invalid/unavailable evidence is indistinguishable in the diagnostic.
History/comparison queries and `rerank: false` retain their existing opt-out behavior.

For a supported pair, `conflicts[].status` becomes `resolved` and `resolution` provides
`basis: "governed_support"`, the preferred memory's `preferredId`/`ns`, exact knowledge
ID/version/hash, provenance ID, and original source text with IDs/namespaces/hashes.
The existing detection `basis`, `subject`, and both conflicting texts remain intact.
Unresolved pairs omit `resolution`. Existing positional result constructors and MCP
input schemas are preserved, including in-process hosts without a governed store.
The MCP handler awaits evidence storage through `RecallAsync`; the existing synchronous
`Recall` method remains as an in-process compatibility wrapper. Concurrent in-process
servers should also use `RecallAsync` so pending file I/O does not occupy serving threads.

Evidence reads do not increment access counts, change facts/status, or resurrect archives.
All reported conflicting archives remain archived, even when a choice has support.
Only selected primary results retain normal access recording. No persistent cache is
introduced: snapshots are reused within one recall call, with a shared five-second lookup
budget and at most 16 supporting sources per publication. Hitting the eight-pair diagnostic
cap disables resolution for that call rather than deciding from a potentially truncated
group. A timeout or store I/O failure withholds all optional preferences for that call.
Detection's existing retrieval/extraction bounds still apply. The read is not an
atomic transaction across all memory and governance records; citations describe the
versions checked during the call.

### Resolution evaluation and candidate coverage

`recall-resolution-v1.json` adds 26 inspected cases over the unchanged original 52-memory
corpus, using real ONNX embeddings and actual governed publications. It covers settings,
ownership, region, feature flags, negation, multiple claims, both preferred sides, `k: 1`,
and restarts. Controls cover missing/changed/archived/self-cited evidence, unrelated or
contrary sources, disputed/stale/private knowledge, two supported sides, partial support,
environment mismatch and qualification. Named tenant principals exercise direct and
authorized broadcast recall. Expert dispatch is intentionally disabled for these principals
by the existing isolation policy; the original three-route evaluations remain unchanged.

All **24 positive query/route observations resolve**, with **zero retrieval misses** and
zero missed resolutions among visible pairs. All 28 negative observations remain unresolved.
Every positive pair resolves to the expected ID with source citations. Primary IDs/scores
match a run with the governed store unavailable: evidence resolution does not influence
ranking. The three misses recorded in PR #37 are now required to pass at their original `k`:

| Case | Route | Coverage fix |
|------|-------|---------------------|
| `multiple-claims`, `k: 5` | broadcast | Common-scale merge retains the Atlas facts instead of filling the budget with Boreal results. |
| `restart`, `k: 1` | direct and broadcast | A larger bounded evidence window finds the opposing claim despite the matching source copy. Equivalent-claim anchors retain the original pair even when the uncited copy is the sole primary hit. |

Wider companion cases (`multiple-claims-wide`, `k: 10`; `restart-wide`, `k: 5`) still retrieve
and resolve the same disagreements. The known-miss allowance is removed; every fixture pair
must surface, including negative controls that must remain unresolved. The original 36 questions and both earlier conflict
supplements retain their labels and gates. New reports are
`recall-realistic-resolution-net*.json`, included in the existing CI artifact. They report
retrieval misses separately from missed resolutions of visible pairs and false resolutions,
and include full citations. Publication hashes/IDs contain per-run timestamps/identities;
compare resolution decisions and source IDs across runs, not those generated identifiers.

Controlled tests additionally exercise validity/status/maturity, source permissions,
private/foreign twins, corrupt or absent provenance/audit, bounds and cancellation,
connected conflicting support, internal contradictions, multiple supporting sources,
archive behavior and optional dependency injection. This is an authored regression
suite, not blind validation or a measure of open-domain factual accuracy.

Candidate-coverage controls exercise mixed hybrid paths at different namespace sizes,
singleton scoring, opt-outs, a noncontributing namespace, graph/search provenance,
multiple copies, all three routes and restarts. Equivalent anchors only diagnose claims
also expressed by a selected hit; unrelated facts elsewhere in an anchor are not pulled
into the warning. Diagnostic-only anchors and alternatives do not receive access or
lifecycle updates, and duplicates never count as votes. The evidence window and eight-pair
cap remain bounded: sufficiently many copies or distant claims can still escape detection
or leave a conflict unresolved. This fix closes the three measured misses, not every
possible retrieval failure.

## Run and review

From the repository root:

```sh
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release --filter "FullyQualifiedName~RealisticRecallEvaluationTests|FullyQualifiedName~MissingAnswerRecallEvaluationTests|FullyQualifiedName~QuestionInterpretation|FullyQualifiedName~RecallQueryEvidenceTests|FullyQualifiedName~BroadRecall|FullyQualifiedName~RecallRouteConsistencyTests|FullyQualifiedName~RecallSummarySelectionTests|FullyQualifiedName~RecallFreshnessTests|FullyQualifiedName~RecallConflict|FullyQualifiedName~RecallResolution"
```

This runs on .NET 8, 9, and 10. Reports are written under each framework's output directory
at `TestResults/recall-realistic-net*.json`, and aggregate metrics also appear in test output.
Set `ENGRAM_RECALL_EVAL_OUTPUT` to choose another report directory. CI runs the evaluation
in its normal non-MSA test suite and uploads the JSON alongside test results, even on failure.

The test gates Hit@5 >= 0.80 and MRR >= 0.50 per split/route, exact expert/direct parity,
and no aggregate quality loss against the checked-in initial reference (including source
recall, nDCG, and stale-answer rate; numerical tolerance 0.0001). Baseline regeneration
is never automatic. To change it, attach before/after per-query reports and explain each
regression in review. Keep the development and held-out results separate, and add new
cases when fixing an uncovered failure instead of relabeling failures away.
