# Restore-order interpretation

The frozen [independent recall evaluation](recall-independent-validation.md)
asked “Which steps restore Citrine production, and in what order?” Its existing
source described a restore procedure: pause consumers, restore the snapshot,
then resume consumers. The lexical matcher treated `steps`, `restore` and
`order` as three separate topics. Matching only `restore` rejected the source,
so every route returned no answer.

## Correction and boundaries

For factual `what`/`which` requests about restore steps, sequence or order, treat
those instruction words as the requested answer form. Retain the restore
operation and resource qualifiers. Eligible evidence must supply an ordering
cue within a scoped statement: comma-`then`, `first` followed by `next`/`then`/
`finally`, `1)` followed by `2)`, or an explicitly identified single step.
Existing non-answer filtering also excludes questions, deferred values,
proposals and documentation tasks.

Ordinary procedure questions, timing, review, explanation, document-location
and explicit proposal questions keep their existing interpretation. Instruction
vocabulary is excluded from capitalized-name guessing only for the procedural
request, rather than globally discarding possible project names such as
“Restore” or “Order.” The explicit subject position in “steps restore …”
preserves lower-case project scope too.

Scope checks inspect the original statement before non-answer filtering.
An explicit contrary project inside a restore action is checked as well as
the statement's opening subject and environments. Namespace/tenant/ACL,
minimum-score, graph and archive protections retain the existing pipeline.
Only selected readable records receive access updates; retrieving a procedure
below the existing resurrection threshold does not promote its archive state.

This remains bounded English lexical matching, not semantic entailment,
procedure execution or arbitrary cross-sentence step reconstruction. Input
limits and non-backtracking regexes remain in force. There are no changes to
MCP interfaces, embeddings, routing, search thresholds or candidate budgets.

## Regression contract

All four initial reproductions failed on merged PR53: the evidence matcher and
direct/expert/broadcast recall of the unchanged source. The 42 new focused tests
cover paraphrases and casing, ordered and single-step evidence, adjacent facts,
non-answers, references/proposals, scope, graph canaries, explicit opt-outs,
score thresholds, archives and restart.

The independent evaluation now requires `restore-order` to return the existing
expected source first on all routes, backends and restart phases. Its source
memories, questions, gold labels, oracle and release criteria remain unchanged.
The non-answer abstention assertions from PR53 remain mandatory.

## Results and remaining release work

Final local validation passes **2,567 ordinary tests per framework** on .NET 8,
9 and 10, without skips, plus all 24 Python report-validator tests. The real
ONNX evaluation returns the existing restore procedure first in all 12
backend/route/restart observations. The other 276 observations are unchanged.
All eight missing-answer questions still abstain.

All 27 earlier recall reports (2,976 observations) and all 624 scale observations
match PR53 after excluding timings and validating generated provenance identities
before normalization. Strict small and 1,000/5,000-memory scale gates pass.
All four concurrent JSON/SQLite load cells pass scope, operation-count, version
and restart-durability checks: 576 concurrent reads, 1,536 mutations and 192
maintenance passes overall.

Failures in the independent evaluation fall from **20/288 to 8/288**. The
remaining question is `compound-recovery`: its backup facet is missing on
automatic/broadcast routes. Direct recall correctly requires only its
namespace-local facet. The strict independent `--release` gate still exits 1;
green execution CI is not release readiness. These are repeated observations
of a small synthetic corpus, not a production accuracy estimate. Fresh MSA,
packaged install/upgrade and sustained reliability qualification remain
separate release work.

## Reproduce

Use fresh output directories:

```powershell
$env:ENGRAM_EVAL_REVISION = git rev-parse HEAD
$env:ENGRAM_RECALL_EVAL_OUTPUT = Join-Path $PWD 'artifacts/restore-order-fresh/full'
dotnet test McpEngramMemory.slnx -c Release --filter 'Category!=MSA&Category!=RecallScale&Category!=IndependentRecall' --blame-hang-timeout 5m --blame-hang-dump-type mini

$env:ENGRAM_RECALL_EVAL_OUTPUT = Join-Path $PWD 'artifacts/restore-order-fresh/scale'
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release -f net8.0 --filter 'Category=RecallScale'
python scripts/check-noisy-recall.py $env:ENGRAM_RECALL_EVAL_OUTPUT --release

$env:ENGRAM_INDEPENDENT_OUTPUT = Join-Path $PWD 'artifacts/restore-order-fresh/independent'
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release -f net8.0 --filter 'Category=IndependentRecall' --blame-hang-timeout 5m --blame-hang-dump-type mini
python scripts/check-independent-recall.py $env:ENGRAM_INDEPENDENT_OUTPUT
python scripts/check-independent-recall.py $env:ENGRAM_INDEPENDENT_OUTPUT --release
python -m unittest discover -s scripts/tests -p '*recall_gate.py'
```
