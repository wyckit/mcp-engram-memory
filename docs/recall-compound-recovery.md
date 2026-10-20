# Compound factual recall

The frozen independent question “Who owns Citrine production deployment, and
how often are backups taken?” returned the owner but omitted the backup cadence.
The factual matcher applied the first clause's ownership requirement to the whole
question. Even a populated backup namespace could not supply eligible evidence,
and automatic routing stopped after the first expert supplied an owner.

## Correction and boundaries

Default current factual recall now recognizes two explicit factual questions
joined by `and`. Each clause retains its own evidence requirements; an omitted
project or environment inherits the other clause's scope. Explicit qualifiers
must agree. The outer guard checks the original record for contrary scopes
before either clause's non-answer filtering. A backup cadence must provide a
schedule, including numeric intervals such as “every 6 hours”; an owner, restore
drill, documentation task or missing-value note cannot supply that facet.

Automatic recall retains eligible expert probes and completes the authorized
broadcast when the experts leave a facet unanswered. An expert that already
supplies both facets keeps the existing expert route. Direct recall
keeps its requested namespace and existing opted-in graph behavior. Namespace,
tenant, read permission, original-query cosine floor and archive write permission
remain enforced. Only final selected memories receive access updates.

Within each searched partition, a missing facet can trigger an exact eligible
source scan, filtered before truncation. There are at most two such probes,
each bounded to `k` plus at most 32 results. They reuse the original embedding
and minimum score. Selection reserves source slots for distinct requested facts,
then fills remaining slots in relevance order without changing relevance scores
or the public `k` limit. Summaries cannot establish facet coverage. When `k` is
too small, or authorized evidence is absent, complete coverage is not guaranteed.

The new interpretation applies only with hybrid retrieval and reranking enabled,
summary-first disabled, spectral mode `auto`/`broad`, and current intent. Ordinary
single questions, topic lists, restore-order phrasing, historical queries,
explicit references/proposals, quoted/numeric precision queries, pronoun-dependent
clauses such as “when is it rotated,” more than two clauses and mismatched explicit
scopes retain their existing paths. This is
bounded English lexical interpretation, not a general query planner or semantic
entailment. MCP interfaces, embedding models and persisted formats are unchanged.

An exact scan is O(N) per searched partition. Completing the authorized sweep
costs more than stopping at a partial expert response. The existing bounded load
suite does not measure compound-query saturation or establish a production SLA.

## Regression contract

Three initial reproductions failed on merged PR54: automatic recall with and
without an expert, and duplicate owner records hiding the backup source inside
one namespace. Focused checks also cover paraphrases, scope inheritance,
non-answer clauses, graph canaries, tenants and permissions, score thresholds,
archives, restart, response budgets and explicit opt-outs.

The frozen source memories, questions, labels, model and release criteria remain
unchanged. All independent observations are now mandatory in the measured test,
and CI invokes the existing validator with `--release`. This closes the previous
gap between green execution CI and the independent recall quality gate.

## Reproduce

Use fresh output directories and record the tested source revision:

```powershell
$env:ENGRAM_EVAL_REVISION = git rev-parse HEAD
$env:ENGRAM_RECALL_EVAL_OUTPUT = Join-Path $PWD 'artifacts/compound-fresh/full'
dotnet test McpEngramMemory.slnx -c Release --filter 'Category!=MSA&Category!=RecallScale&Category!=IndependentRecall' --blame-hang-timeout 5m --blame-hang-dump-type mini

$env:ENGRAM_RECALL_EVAL_OUTPUT = Join-Path $PWD 'artifacts/compound-fresh/scale'
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release -f net8.0 --no-build --filter 'Category=RecallScale'
python scripts/check-noisy-recall.py $env:ENGRAM_RECALL_EVAL_OUTPUT --release

$env:ENGRAM_INDEPENDENT_OUTPUT = Join-Path $PWD 'artifacts/compound-fresh/independent'
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release -f net8.0 --no-build --filter 'Category=IndependentRecall' --blame-hang-timeout 5m --blame-hang-dump-type mini
python scripts/check-independent-recall.py $env:ENGRAM_INDEPENDENT_OUTPUT --release
python -m unittest discover -s scripts/tests -p '*recall_gate.py'
```

Passing these regression checks does not replace fresh MSA benchmarks, packaged
installation/upgrade validation or sustained reliability qualification.
