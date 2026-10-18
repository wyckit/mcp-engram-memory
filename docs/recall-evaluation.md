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
3. Honor `expandGraph` and apply `spectralMode` within each searched namespace.
4. Select at most `k` results; broadcast considers up to `k` from each readable namespace.
5. Record access only for returned memories, using their actual source namespaces. Recover
   returned archived search hits with cosine similarity at least 0.7 only when the caller
   has write permission. Graph-only neighbors do not trigger resurrection.

Keyword rescue respects the requested cosine threshold. Graph expansion can deliberately
add related context below that threshold; disabling expansion also prevents broad-mode
traversal from introducing new candidates. Ambiguous bare IDs cannot be used to cross
namespace boundaries. Broadcast deduplicates by namespace plus ID and preserves distinct
memories that happen to share an ID.

Explicit `summaryFirst: true` is honored after hybrid/token/spectral ranking and before
the final limit. When Core `SearchRequest.Diversity` is also requested, MMR still selects
the result set and summary priority orders that selection.
The default remains false. Adaptive summary/source balancing is separate future work.
MCP names, argument defaults, result schemas, and route strategy labels are unchanged.

## Cases and measurements

The versioned fixture and its initial quality reference live in
[`tests/McpEngramMemory.Tests/Fixtures`](../tests/McpEngramMemory.Tests/Fixtures).
There are 24 development questions and 12 held-out paraphrases, with six questions per
scenario. Labels come from the authored source facts. Held-out here means a fixed separate
reporting split over the same corpus, not an independent hidden production dataset.
No ranking weights were tuned against either split in this change.

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
These failures remain visible for the next summary-selection/correction-ranking work;
the corpus labels have not been changed to count obsolete facts as correct.

## Run and review

From the repository root:

```sh
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release --filter "FullyQualifiedName~RealisticRecallEvaluationTests|FullyQualifiedName~RecallRouteConsistencyTests"
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
