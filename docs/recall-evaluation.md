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
2. If the live results are empty or low-scoring, search archived memories with those same options.
3. Honor `expandGraph` and apply `spectralMode` within each searched namespace. With reranking
   enabled, prefer unmarked candidates over explicitly superseded records for current-answer
   queries, both before namespace truncation and during final selection.
4. Select at most `k` results, keeping linked sources alongside retained summaries when
   possible; broadcast considers up to `k` from each readable namespace and applies the
   same selection policy to the combined candidates.
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
The default remains false; the summary/source policy below applies with graph expansion on.
MCP names, argument defaults, primary result schemas, and route strategy labels are unchanged.
Recall adds an optional top-level `conflicts` diagnostic for supported unmarked disagreements.

## Summary and source selection

An overview can link many unrelated facts. Its links do not establish that those facts form
one query-relevant cluster: broad-mode dominance and traversal now use source-to-source
connections, excluding summary nodes even when they are reached outside the initial pool.
A relevant summary can still be returned at its retrieval score.

New graph candidates reached from a summary receive the summary's retrieval score multiplied
by their query cosine, clamped to `[0, 1]`. This ranks the drilldown by query relevance without
comparing raw cosine to hybrid/RRF scores or giving every linked fact the same score.
Already-retrieved candidates keep their scores.

With default selection, each retained summary can bring up to two of its highest-scored
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

If the initial retrieval contains a superseded candidate and fewer than `k` unmarked alternatives,
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
window in that same authorized namespace: requested `k + min(k, 32)` candidates, reusing
existing vector/status overfetch when sufficient. Vector reranking retains its existing
internal 2x overfetch. At most 64 evidence candidates per namespace, 32 selected primary
hits, and 8 distinct conflict pairs are examined/reported. The original `minScore`, tenant,
and chosen lifecycle scope apply. Archives enter only through existing fallback; candidates
named in a reported conflict remain archived. Evidence-only hits receive no access or
lifecycle update, and no text, metadata, or graph links are written by diagnosis.

Recognized historical/comparison queries and `rerank: false` bypass this current-intent
diagnostic. Graph expansion and summary preference keep their existing behavior; a primary
summary is not treated as a factual claimant. Cross-namespace graph neighbors are not probed
as if they belonged to the original namespace. No timestamp, insertion order, ID, arbitrary
metadata, graph direction, or vector similarity is treated as proof of truth.

This is conservative conflict surfacing, not general contradiction detection or automatic
resolution. It can miss unlisted phrasings, unsupported languages/values, distant candidates,
cross-namespace conflicts, or qualified claims. A missing diagnostic does not certify
consistency. It also does not change ingest-time duplicate blocking or connect raw memories
to the separate governed-knowledge authority model.

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

## Run and review

From the repository root:

```sh
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release --filter "FullyQualifiedName~RealisticRecallEvaluationTests|FullyQualifiedName~RecallRouteConsistencyTests|FullyQualifiedName~RecallSummarySelectionTests|FullyQualifiedName~RecallFreshnessTests|FullyQualifiedName~RecallConflict"
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
