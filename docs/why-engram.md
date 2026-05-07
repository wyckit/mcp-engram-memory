# Why Engram?

## The problem

Every time your AI coding session ends, context disappears. The agent that helped you
debug a tricky race condition yesterday has no idea what you're talking about today.
You re-explain, re-decide, re-debug — and the cost compounds across every project.

## What Engram does

Engram closes the loop between sessions. The agent recalls relevant history before it
works, and remembers what it learns when it's done. You don't configure this — it just
runs.

```
 ┌─────────────┐     recall()      ┌──────────────────┐
 │  New session│ ───────────────►  │  Engram memory   │
 │  (no context│ ◄───────────────  │  (persists across│
 │   yet)      │   past decisions  │   sessions)      │
 └──────┬──────┘                   └────────┬─────────┘
        │  work                             │
        │                          remember()
        └─────────────────────────────────► │
              new decisions + context       │
```

The three tools the agent uses: `recall` (search before answering),
`remember` (store what was learned), `reflect` (end-of-session summary).
Everything else — decay, consolidation, graph linking — happens in the background.

## The proof

In a head-to-head test with Claude Opus, we ran 6 self-referential coding tasks against the mcp-engram-memory repo itself. The agent answers the same question twice — once with no memory (vanilla baseline), once with Engram enabled.

**On 4 tasks where the baseline agent didn't already know the answer:**

| Arm | Pass rate | Tokens | Time |
|---|---|---|---|
| No memory | 46.8% | 985 | 31s |
| With Engram | **77.5%** | 968 | 62s |

**+30.75pp pass-rate lift · σ 6.2pp · ~equal tokens** _(4 tasks × 3 seeds, claude-cli v2.1.131)_

On 2 additional "control" tasks where the baseline agent already scored 100%, Engram was neutral — no degradation. Engram is safe to enable on tasks the agent can already handle.

[Reproduce this benchmark](../benchmarks/datasets/self-referential-cold-start-v1/manifest.json) · [Raw scorecard JSON](../benchmarks/2026-05-07/cold-start-self-referential-cold-start-v1-opus.json)

## What's under the hood

Engram uses hybrid BM25 + vector search, a knowledge graph with typed edges, and
a background memory-diffusion kernel built on the normalized graph Laplacian — the
same structure that drives decay, sleep-consolidation, and spectral retrieval
re-ranking. For the full story, see [Architecture](architecture.md) and
[Internals](internals.md).

## Get started

→ [Your first 5 minutes](first-5-minutes.md)
