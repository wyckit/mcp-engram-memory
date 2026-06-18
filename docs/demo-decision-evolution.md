# Killer Demo: "Ask an Agent Why a Project Decision Changed Over Time"

[< Back to README](../README.md)

This is a reproducible walkthrough that exercises the **full cognitive stack** of
Engram Memory on a single, relatable story: a project that started with one
persistence decision and later pivoted to another. By the end, an agent can
answer **"why did this decision change?"** — citing the current truth, the prior
truth, and the reason for the change — entirely from memory it consolidated on
its own.

## Why this matters (vs. document-and-synthesize tools)

Most "memory" layers are a document store with a summarizer bolted on: you dump
text in, and at query time an LLM re-reads everything and writes a summary. That
story has **no sense of time and no self-consolidation**. It cannot tell you that
fact B *superseded* fact A, because both are just rows it re-summarizes on every
call.

Engram is **temporal, self-consolidating memory**:

- It **links** a new memory to the older ones it relates to or contradicts.
- Its background lifecycle **promotes** the reinforced memory toward long-term
  memory (LTM) and lets the superseded one **decay toward archive** — automatically,
  with no LLM in the loop.
- Retrieval is **graph-aware and spectral**: recalling the current decision pulls
  in the contradicting predecessor and related context.
- `find_contradictions` **surfaces the conflict explicitly**.
- Synthesis can then explain *current truth, prior truth, and WHY it changed* —
  because the structure (edges, lifecycle states, timestamps) is already there.

That is the thing a documents-then-synthesis pipeline does not own.

---

## Prerequisites

The full demo touches graph, lifecycle, intelligence, and synthesis tools, which
live in the `standard` and `full` tool profiles. Run the server with the **`full`**
profile so every step is available:

```jsonc
{
  "mcpServers": {
    "engram-memory": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/mcp-engram-memory/src/McpEngramMemory"],
      "env": { "MEMORY_TOOL_PROFILE": "full" }
    }
  }
}
```

Profile coverage of the tools used here:

| Tool | Lowest profile that exposes it |
|------|-------------------------------|
| `remember`, `recall`, `cognitive_stats`, `engram_status` | `minimal` |
| `link_memories`, `get_neighbors`, `promote_memory`, `memory_feedback`, `find_contradictions`, `detect_duplicates` | `standard` |
| `synthesize_memories` | `full` |

> **Note on the lifecycle steps.** Decay, auto-linking, and consolidation run as
> **background workers** (consolidation every 24h, auto-link every 6h, diffusion
> warmup every 30m on `standard`+). They are **not** MCP tools you can call
> on demand. To make the demo reproducible in minutes instead of hours, the
> walkthrough drives the *same state transitions* explicitly with
> `memory_feedback` (reinforce / suppress) and `promote_memory` (force a state).
> `engram_status` lets you observe that the background workers exist and are
> running. This is called out honestly at each step so nothing here is
> "aspirational."

The demo uses the namespace **`demo-decision-evolution`** so it never collides
with your real project memory.

---

## The story

1. **Early intent.** The team decides to persist data as **JSON files** — simple, human-readable, no dependencies.
2. **The pivot.** Months later, parallel load causes write contention and corruption. The team switches to **SQLite with WAL mode**. This *contradicts* the original decision.
3. The agent should later be able to explain: *"We use SQLite now; we used to use JSON files; we changed because JSON caused write contention and corruption under parallel load."*

---

## Step 1 — Store the early project intent (`remember`)

Store the original decision. `remember` embeds locally, blocks near-duplicates,
and auto-links to related entries.

**Tool call:**

```json
{
  "tool": "remember",
  "arguments": {
    "id": "persistence-json-files",
    "ns": "demo-decision-evolution",
    "text": "Decision: persist application data as JSON files on disk. Rationale: simplest possible approach, human-readable, zero external dependencies, easy to diff in git. We expect low write volume.",
    "category": "decision",
    "lifecycleState": "stm"
  }
}
```

**Expected result shape** (`RememberResult`):

```json
{
  "status": "stored",
  "id": "persistence-json-files",
  "ns": "demo-decision-evolution",
  "message": "Remembered 'persistence-json-files' in 'demo-decision-evolution'. Actions: stored.",
  "actions": ["stored"]
}
```

At this point the namespace has one STM memory and no edges yet.

---

## Step 2 — Store the contradicting design pivot (`remember`)

Months later, store the pivot. Because the text is on the same topic
(persistence) it will be **semantically near** the first memory, so `remember`'s
auto-link step fires.

**Tool call:**

```json
{
  "tool": "remember",
  "arguments": {
    "id": "persistence-sqlite-wal",
    "ns": "demo-decision-evolution",
    "text": "Decision REVERSED: stop using JSON files for persistence; switch to SQLite with WAL mode. Reason: under parallel load, concurrent writers corrupted the JSON files and caused write contention. SQLite gives us atomic transactions and safe concurrent access. This supersedes the earlier JSON-files decision.",
    "category": "decision",
    "lifecycleState": "stm"
  }
}
```

**Expected result shape** — note the auto-link action. `remember` adds a
`similar_to` edge when cosine >= 0.85, otherwise `cross_reference` for the
0.65–0.85 band:

```json
{
  "status": "stored",
  "id": "persistence-sqlite-wal",
  "ns": "demo-decision-evolution",
  "message": "Remembered 'persistence-sqlite-wal' in 'demo-decision-evolution'. Actions: stored, linked to 1 related memory.",
  "actions": ["stored", "linked to 1 related memory"]
}
```

> If the two texts land below 0.65 cosine (unlikely for this pair), no edge is
> auto-created — Step 3 adds the precise relationship explicitly anyway.

---

## Step 3 — Make the relationship explicit (`link_memories`)

Auto-linking captures *relatedness* (`similar_to` / `cross_reference`). The
*semantic* relationship here is stronger and directional: the new decision
**contradicts** the old one. Add that edge explicitly so `find_contradictions`
(Step 6) surfaces it from the graph, not just from similarity heuristics.

**Tool call:**

```json
{
  "tool": "link_memories",
  "arguments": {
    "sourceId": "persistence-sqlite-wal",
    "targetId": "persistence-json-files",
    "relation": "contradicts",
    "weight": 1.0
  }
}
```

**Expected result:** a short confirmation string from `KnowledgeGraph.AddEdge`,
e.g. `Edge added: persistence-sqlite-wal --contradicts--> persistence-json-files`.

**Verify the edge exists (`get_neighbors`):**

```json
{
  "tool": "get_neighbors",
  "arguments": {
    "id": "persistence-sqlite-wal",
    "relation": "contradicts",
    "direction": "both"
  }
}
```

This returns the `persistence-json-files` entry as a neighbor over the
`contradicts` relation.

---

## Step 4 — Reinforce the new decision; let the old one decay toward archive

This is the **self-consolidating** behavior. In normal operation the background
decay and consolidation workers do this over hours/days as the new decision gets
recalled and the old one stops being accessed. To reproduce it deterministically
in the demo, drive the same activation-energy transitions explicitly.

**4a. Reinforce the current decision** (`memory_feedback`, positive delta). A
positive delta raises activation energy and pushes the memory up the lifecycle:

```json
{
  "tool": "memory_feedback",
  "arguments": {
    "id": "persistence-sqlite-wal",
    "delta": 3.0,
    "ns": "demo-decision-evolution"
  }
}
```

**Expected result:** an object describing the new activation energy and any
lifecycle transition triggered by crossing a threshold.

**4b. Promote the current decision to LTM** (`promote_memory`) — the reinforced,
stable truth becomes long-term:

```json
{
  "tool": "promote_memory",
  "arguments": {
    "id": "persistence-sqlite-wal",
    "targetState": "ltm"
  }
}
```

**4c. Suppress the superseded decision** (`memory_feedback`, negative delta) —
this is what repeated non-recall would do over time:

```json
{
  "tool": "memory_feedback",
  "arguments": {
    "id": "persistence-json-files",
    "delta": -3.0,
    "ns": "demo-decision-evolution"
  }
}
```

**4d. Archive the superseded decision** (`promote_memory` to `archived`) — it is
no longer the active truth, but it is **not deleted**: it stays recoverable for
exactly the "why did this change?" question:

```json
{
  "tool": "promote_memory",
  "arguments": {
    "id": "persistence-json-files",
    "targetState": "archived"
  }
}
```

**Observe the result with `cognitive_stats`:**

```json
{ "tool": "cognitive_stats", "arguments": { "ns": "demo-decision-evolution" } }
```

**Expected:** `ltmCount: 1` (SQLite), `archivedCount: 1` (JSON), `edgeCount >= 1`
(the contradicts edge). The reinforced memory is now LTM; the superseded one has
decayed to archived — exactly the steady state the background workers converge to.

**See the background workers themselves (`engram_status`):**

```json
{ "tool": "engram_status", "arguments": {} }
```

Returns last-run timestamps and cycle counts for the `decay`, `consolidation`,
`auto_link`, and `accretion` workers — proof the consolidation we forced by hand
is the same process the server runs continuously.

---

## Step 5 — Graph-aware / spectral recall pulls both + related (`recall`)

Now ask the question the demo is named for. `recall` does hybrid (BM25 + vector)
search, **expands along graph neighbors** (`expandGraph: true`, default), applies
**spectral re-ranking** (`spectralMode: "auto"`, default), and — because the
predecessor is archived — falls back to `deep_recall` to resurface it.

**Tool call:**

```json
{
  "tool": "recall",
  "arguments": {
    "query": "How do we persist application data and why?",
    "ns": "demo-decision-evolution",
    "k": 5,
    "expandGraph": true,
    "spectralMode": "auto"
  }
}
```

**Expected result shape** (`RecallResult`) — the current LTM decision ranks first,
and the contradicting predecessor comes back too (via the graph edge and/or the
deep-recall archived fallback):

```json
{
  "strategy": "direct",
  "ns": "demo-decision-evolution",
  "results": [
    {
      "id": "persistence-sqlite-wal",
      "text": "Decision REVERSED: stop using JSON files ... switch to SQLite with WAL mode ...",
      "score": 0.71,
      "lifecycleState": "ltm",
      "category": "decision"
    },
    {
      "id": "persistence-json-files",
      "text": "Decision: persist application data as JSON files ...",
      "score": 0.57,
      "lifecycleState": "archived",
      "category": "decision"
    }
  ]
}
```

> If the top hit scores below 0.5, `recall` automatically swaps in `deep_recall`
> and the `strategy` field reads `"deep_recall"` instead of `"direct"` — either
> way the archived predecessor is resurfaced. (`deep_recall` auto-resurrects
> archived entries scoring above its resurrection threshold back to STM.)

A documents-then-summarize store would have shown you only the latest blob, or a
flat list with no notion that one entry replaced the other.

---

## Step 6 — Surface the conflict (`find_contradictions`)

Ask the system to name the conflict directly. It returns explicit `contradicts`
graph edges **plus** high-similarity pairs that may disagree.

**Tool call:**

```json
{
  "tool": "find_contradictions",
  "arguments": {
    "ns": "demo-decision-evolution",
    "topic": "data persistence storage format",
    "similarityThreshold": 0.8
  }
}
```

**Expected result shape** (`ContradictionResult`) — our explicit edge appears
with source `"graph_edge"`:

```json
{
  "contradictions": [
    {
      "entryA": {
        "id": "persistence-sqlite-wal",
        "text": "Decision REVERSED: ... switch to SQLite with WAL mode ...",
        "lifecycleState": "ltm"
      },
      "entryB": {
        "id": "persistence-json-files",
        "text": "Decision: persist application data as JSON files ...",
        "lifecycleState": "archived"
      },
      "similarity": 0.74,
      "source": "graph_edge"
    }
  ],
  "graphContradictionCount": 1,
  "highSimilarityCount": 0
}
```

The conflict is now **first-class data**, not something an LLM had to infer by
re-reading prose.

> Optional: `detect_duplicates` on the same namespace will report **no**
> duplicates (the two entries are related but distinct, well below the 0.95
> duplicate threshold) — confirming the system correctly treats this as
> *evolution*, not redundancy.

---

## Step 7 — Synthesis explains current truth, prior truth, and WHY it changed (`synthesize_memories`)

Finally, let the local SLM synthesize the namespace with a focus query. Because
the structure already encodes which decision is current (LTM), which is
superseded (archived), and how they relate (the `contradicts` edge), the
synthesis can narrate the evolution rather than just concatenating facts.

> Requires a local Ollama instance (or the in-process ONNX synthesis backend if
> enabled). If neither is available, this step degrades gracefully — the
> structured answer from Steps 5–6 already contains current truth, prior truth,
> and the edge that connects them, so an agent can compose the explanation
> directly.

**Tool call:**

```json
{
  "tool": "synthesize_memories",
  "arguments": {
    "ns": "demo-decision-evolution",
    "query": "Why did the data persistence decision change over time? What is the current approach, what was the previous one, and what caused the switch?",
    "maxEntries": 50
  }
}
```

**Expected synthesis (shape):** a dense narrative that distinguishes the timeline,
for example:

> **Current decision:** Data is persisted in **SQLite with WAL mode** (long-term
> memory). **Previous decision:** Data was persisted as **JSON files** (now
> archived). **Why it changed:** Under parallel load, concurrent writers
> corrupted the JSON files and caused write contention; SQLite's atomic
> transactions and safe concurrent access resolved this. The two decisions are
> linked by a `contradicts` edge, marking the second as superseding the first.

That is the payoff: **temporal, self-consolidating memory** answering *why a
decision changed over time* — current truth, prior truth, and the reason —
something a document-store-plus-summarizer cannot structurally produce.

---

## Reproduce it

A copy-pasteable driver lives in:

- **`examples/demo-decision-evolution.ps1`** (PowerShell)
- **`examples/demo-decision-evolution.sh`** (bash)

Both emit the exact tool-call sequence above as an **agent prompt script** you
paste into a Claude Code (or any MCP client) session connected to the engram
server. See the header comments in each script for why a fully self-contained
MCP wire driver is not shipped here.

## Cleanup

To reset between runs, delete the demo namespace's entries (full profile exposes
`delete_memory`), or simply re-run with the same IDs — `remember` upserts on
matching IDs, and `promote_memory` resets lifecycle state.

## See also

- [docs/core-10.md](core-10.md) — the 10 most-used tools
- [docs/mcp-tools-reference.md](mcp-tools-reference.md) — every tool's full signature
- [docs/why-engram.md](why-engram.md) — the positioning behind this demo
