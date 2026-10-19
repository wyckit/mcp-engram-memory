# Natural questions and non-answer evidence

The unchanged [noisy recall cases](recall-noisy-evaluation.md) exposed five missed
answer-bearing questions and one false acceptance of a credential-rotation note.
This correction changes the bounded English evidence matcher, not MCP inputs,
embeddings, search thresholds, candidate budgets, storage or namespace routing.

## Interpretation and evidence

- Normalize ownership and rollback constructions (`looks after`, `rolls back`)
  before extracting the requested topic. In particular, `after` inside an
  ownership phrase must not discard the rest of the question as context.
- Recognize a conditional opening followed by a question. Retain its project and
  environment for scope checks while separating the condition from the requested
  attribute. Lowercase subjects follow the same restrictions. Conflicting scopes
  are not resolved by inventing a default environment or dropping the condition.
- Distinguish elapsed recovery/rehearsal time from a recovery objective. A duration
  for a completed recovery can answer the former; a target or a future drill
  schedule cannot substitute for it.
- Reject explicit omission predicates such as `does not identify` and
  `is intentionally not recorded`. A mention of an owner is not automatically an
  owner answer.
- An explicit value question requires an assignment to the requested topic or
  value, rather than just a rotation/activity mention. A generic value
  continuation may reuse the preceding statement's topic only after the original
  subject and environment checks pass. Actual values beside documentation
  pointers or unrelated unknown fields remain eligible. Document-location and
  explanation questions retain their existing behavior.

These are bounded lexical checks, not general semantic entailment or verification
of a fact's truth. They retain non-backtracking regular expressions and existing
input/statement limits. Historical answer selection and broad-answer completeness
remain separate work. Explicit reranking/summary-first opt-outs are unchanged.

## Regression contract

The corpus, questions, relevance labels, distractors, embedding model and original
release-blocker criteria are unchanged. The small evaluation and scale test now
also assert the existing expected outcomes for `owner-paraphrase`,
`endpoint-incident`, `recovery-duration`, `rollback-command`,
`underspecified-environment` and `missing-secret`, on all three routes before and
after restart. This makes those formerly measured misses regression failures
without rewriting their labels or lowering an existing gate.

There are 46 new focused tests, including paired answers/non-answers, lowercase
questions, actual versus target durations, conditional scope conflicts, value
continuations, all routes, restart, explicit namespaces, minimum scores,
tenant/private/legacy twins and read-only archives. The initial 12 reproductions
all failed before the production change.

## Validation

The complete ordinary suites passed **2,374 tests per framework** on .NET 8 and
.NET 10 in the concurrent run, and on .NET 9 in a separate full rerun, with no
skips. The initial concurrent .NET 9 run failed the unchanged
`DecayCycle_OnNsA_DoesNotBlockWritesToNsB` 60-second worker deadline; its workers
cancelled and joined. The same test also failed on the unchanged base in
[main CI run 39335486074](https://github.com/wyckit/mcp-engram-memory/actions/runs/39335486074).
The retained failure is an existing release reliability concern; a successful
rerun does not diagnose or resolve it. No deadline, test assertion, or concurrent
execution setting was changed.

All 27 earlier recall reports / 2,976 repeated observations match the baseline
after excluding timing and validated generated promotion identities/hashes.
The frozen scale matrix has 624 repeated route/phase observations, zero declared
blockers and no lost accepted answer hits. The six targeted cases pass in every
cell; all scope, tenant/ACL, threshold, archive, restart and mixed-workload
protections pass.

The final new-test/small-evaluation run passes 49 tests on each framework, including
the added frozen-case assertions. The strict small and scale gates, 12 report-gate
tests, 48 pinned benchmark checks and all three package builds pass. Pinned checks
are not a fresh MSA run.

| Memories | Namespaces | Direct hits | Automatic hits | Broadcast hits | Correct negative abstentions, every route |
|---|---|---:|---:|---:|---:|
| 1,000 | 8 / 32 | 17/22 → 20/22 | 17/22 → 20/22 | 18/22 → 21/22 | 3/4 → 4/4 |
| 5,000 | 8 / 32 | 14/22 → 19/22 | 15/22 → 20/22 | 14/22 → 19/22 | 3/4 → 4/4 |

These are distinct questions within one route/phase; repeated layouts and restart
phases agree. The authored synthetic corpus is regression evidence, not an
estimate of production accuracy. The historical timeout miss and incomplete
broad/compound coverage remain. No fresh MSA, package-upgrade or soak result is
claimed by this correction.

Run the ordinary suite and the separate scale evaluation:

```powershell
$env:ENGRAM_RECALL_EVAL_OUTPUT = Join-Path $PWD 'artifacts/natural-questions/full'
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release --filter 'Category!=MSA&Category!=RecallScale'
$env:ENGRAM_RECALL_EVAL_OUTPUT = Join-Path $PWD 'artifacts/natural-questions/scale'
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release -f net8.0 --filter 'Category=RecallScale'
python scripts/check-noisy-recall.py artifacts/natural-questions/scale --release
```
