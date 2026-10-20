# Pending questions and proposals are not factual answers

The frozen [independent evaluation](recall-independent-validation.md) exposed
five missing-answer questions that returned related notes instead of abstaining.
This correction changes the bounded English evidence matcher. It preserves MCP
interfaces, embeddings, thresholds, candidate budgets and namespace routing.

## Correction

- Interpret `On what date ...` as a factual date request. Require the date
  attribute rather than accepting an ownership or incident note. Explicit date
  fields can use any representation; ISO and month-first calendar literals also
  supply date evidence when the source omits the word `date`.
- Keep `rollback` as a requested attribute. Deployment ownership alone cannot
  answer a rollback approval ownership question.
- Exclude clauses that defer a value (`still to be recorded`, `not selected`),
  embed an unanswered question, describe questions about a topic, omit the
  requested identity, or propose a value without establishing it as fact.
- Retain supplied answers beside unrelated unknowns or questions. Explicit
  proposal questions, documentation-location questions and explanations retain
  their useful context. Reranking and summary-first opt-outs keep their existing
  behavior.

Project and environment checks still examine the original statement before
non-answer clauses are removed. The correction does not widen explicit namespace
scope or change tenant/ACL, minimum-score or archive protections.

These are lexical checks, not semantic entailment or truth verification. They
retain non-backtracking regular expressions and the existing query, source and
statement limits. Additional omission predicates use separate small expressions
to stay below the regex engine's automaton-size limit on supported runtimes.

## Regression evidence

All five initial reproductions failed on merged PR52 before the production
change. The 46 new focused tests cover those frozen questions, supplied answers
after restart, lowercase phrasing, date formats, proposals versus decisions,
references/explanations, unrelated omissions, explicit opt-outs and scope
protections. Comma-separated topic lists remain non-evidence for broad recall.
The independent evaluation now also asserts that all five resolved
cases abstain on every route, backend and restart phase.

The corpus, source memories, questions, answer labels, model and original release
criteria are unchanged. Fixture SHA256 remains
`11D86B86DF1629FC9931DE7DC846E4A7B317A9C31F15FB6363D73323CC4F6553`.

Real ONNX evaluation on JSON and SQLite improves from **80 to 20 failed
observations out of 288**, across the same 24 questions. All eight missing-answer
questions now abstain on every route and restart phase, including:

| Resolved case | Rejected non-answer |
|---|---|
| `absent-date` | An unrecorded decision date and unrelated facts |
| `absent-approval-owner` | An unanswered approval question and deployment owner |
| `proposal-not-decision` | An explicitly unselected scheduler proposal |
| `question-not-number` | A request to measure queue depth |
| `topic-not-answer` | DNS planning notes without a named provider |

All four concurrent load cells (JSON/SQLite at 1,000/5,000 memories) pass their
operation, scope, version and restart-durability checks: 576 concurrent reads,
1,536 mutations and 192 maintenance passes overall. These bounded tests do not
establish a production latency SLA or replace a sustained soak.

The final ordinary suites pass **2,525 tests per framework** on .NET 8, 9 and 10,
without skips. Across the 27 earlier recall reports (2,976 observations), all
quality metrics are preserved. The only changed results remove an obsolete
deployment-owner extra from 21 rollback-approval observations, retaining the
expected owner. Comparisons exclude timing and validate generated provenance
identities before normalizing them. All three small noisy-corpus strict gates
and all 24 Python report-validator tests pass. The four 1,000/5,000-memory,
8/32-namespace cells pass their strict gate, with all 624 scale observations
unchanged from PR52.

## Remaining release blockers and reproduction

The strict independent release check still exits 1 for **two unchanged
questions**: `restore-order` has no answer on any route; `compound-recovery`
misses its backup facet on automatic and broadcast routes. Direct compound recall
correctly requires only its namespace-local facet. These account for the remaining
20 failed observations, not 20 independent defects.

Use fresh output directories to reproduce:

```powershell
dotnet test McpEngramMemory.slnx -c Release --filter 'Category!=MSA&Category!=RecallScale&Category!=IndependentRecall' --blame-hang-timeout 5m --blame-hang-dump-type mini

$env:ENGRAM_INDEPENDENT_OUTPUT = Join-Path $PWD 'artifacts/pending-evidence-independent'
$env:ENGRAM_EVAL_REVISION = git rev-parse HEAD
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release -f net8.0 --filter 'Category=IndependentRecall' --blame-hang-timeout 5m --blame-hang-dump-type mini
python scripts/check-independent-recall.py $env:ENGRAM_INDEPENDENT_OUTPUT
python scripts/check-independent-recall.py $env:ENGRAM_INDEPENDENT_OUTPUT --release

$env:ENGRAM_RECALL_EVAL_OUTPUT = Join-Path $PWD 'artifacts/pending-evidence-scale'
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release -f net8.0 --filter 'Category=RecallScale'
python scripts/check-noisy-recall.py $env:ENGRAM_RECALL_EVAL_OUTPUT --release
python -m unittest discover -s scripts/tests -p '*recall_gate.py'
```

The normal independent validator checks complete execution and safety. A green CI
run does not override its explicit remaining quality failures. Fresh MSA,
packaged install/upgrade checks and sustained reliability validation remain
separate release work; this change claims none of those results.
