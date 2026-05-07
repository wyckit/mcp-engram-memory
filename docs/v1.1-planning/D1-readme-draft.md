# D1 — README & Docs Restructure Draft (v1.1)

---

## Section 1 — New README Hero (proposed verbatim)

```
<p align="center">
  <img src="images/banner.svg?v=1.1.0" alt="MCP Engram Memory" width="900"/>
</p>

<p align="center">
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512BD4" alt=".NET"/></a>
  <a href="https://opensource.org/licenses/MIT"><img src="https://img.shields.io/badge/License-MIT-yellow.svg" alt="License: MIT"/></a>
  <a href="https://www.nuget.org/packages/McpEngramMemory.Core"><img src="https://img.shields.io/nuget/v/McpEngramMemory.Core" alt="NuGet"/></a>
  <img src="https://img.shields.io/badge/tests-972%20non--MSA-brightgreen" alt="Tests"/>
</p>

**Give your AI agent persistent memory that survives across sessions.**

- Recall past decisions and context, by topic or by project
- Remember new information automatically — no manual saving needed
- Search across all your projects with one query

→ [See the cold-start scorecard](docs/why-engram.md#the-proof) · [Get started in 5 minutes](docs/first-5-minutes.md)
```

---

## Section 2 — Diff Against Current README Hero

Lines to **delete** (lines 16–20 of current README.md):

```
-  Line 16: A cognitive memory engine exposed as an [MCP](...) server with hybrid
-           search (BM25 + vector), knowledge graph, lifecycle management, hierarchical
-           expert routing, and a graph-aware **memory-diffusion subsystem** (v0.9.0)
-           that drives spreading-activation decay, sleep-style consolidation, and
-           spectral retrieval re-ranking — all from a single per-namespace eigenbasis
-           of the graph Laplacian.
-
-  Lines 18–20: <p align="center">
-                 <img src="images/features.svg?v=1.0.0" .../>
-               </p>
```

Lines to **keep** (lines 1–14):

```
  Lines 1–12: banner SVG + badge row — keep as-is (drop the "MCP Tools: 65" badge
              or move it to Section 3 / At a Glance; it signals complexity before value)
  Line 14:    "Give your AI agent persistent memory that survives across sessions." — KEEP
              (strong anchor; do not replace, only append bullets below it)
```

Lines to **add** (after line 14, before current Quickstart):

```
+  - Recall past decisions and context, by topic or by project
+  - Remember new information automatically — no manual saving needed
+  - Search across all your projects with one query
+
+  → [See the cold-start scorecard](docs/why-engram.md#the-proof) · [Get started in 5 minutes](docs/first-5-minutes.md)
```

---

## Section 3 — Install Section Restructure (proposed verbatim)

Replace lines 22–76 of current README.md with:

```markdown
## Quickstart

```powershell
# Windows — clones, builds, and wires up your AI assistant automatically
irm https://raw.githubusercontent.com/wyckit/mcp-engram-memory/main/setup.ps1 | iex
```

```bash
# macOS / Linux
curl -fsSL https://raw.githubusercontent.com/wyckit/mcp-engram-memory/main/setup.sh | bash
```

> First run downloads a ~5.7 MB embedding model (bge-micro-v2) — subsequent starts are instant.

<details>
<summary>Other install options (manual clone · Docker · NuGet)</summary>

**Manual clone**

```bash
git clone https://github.com/wyckit/mcp-engram-memory.git
cd mcp-engram-memory && dotnet restore
```

Add to your MCP client config:

```json
{
  "mcpServers": {
    "engram-memory": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/mcp-engram-memory/src/McpEngramMemory"],
      "env": { "MEMORY_TOOL_PROFILE": "minimal" }
    }
  }
}
```

**Docker**

```bash
docker build -t mcp-engram-memory .
docker run -i -v memory-data:/app/data mcp-engram-memory
```

**NuGet library** (embed the engine in your own .NET app)

```bash
dotnet add package McpEngramMemory.Core --version 1.0.0
```

See [`examples/`](examples/) for ready-to-use config files.

</details>
```

---

## Section 4 — docs/why-engram.md (proposed full content)

```markdown
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

![cold-start scorecard](images/cold-start-scorecard.png)

_Scorecard coming in v1.1 — measures recall accuracy on a fresh agent with no
injected context vs. full conversation history._

## What's under the hood

Engram uses hybrid BM25 + vector search, a knowledge graph with typed edges, and
a background memory-diffusion kernel built on the normalized graph Laplacian — the
same structure that drives decay, sleep-consolidation, and spectral retrieval
re-ranking. For the full story, see [Architecture](architecture.md) and
[Internals](internals.md).

## Get started

→ [Your first 5 minutes](first-5-minutes.md)
```

---

## Section 5 — first-5-minutes.md Change List

No references to `decay_cycle`, `run_consolidation`, `auto_link_namespace`, or
`trigger_accretion_scan` were found in `docs/first-5-minutes.md`. The file is clean
with respect to soon-to-be-background tools.

Two items to update for v1.1 clarity:

1. **Line 62–64 — `recall` description mentions `deep_recall` as a named fallback:**

   Current:
   > `expands along graph neighbors, and falls back to deep_recall for archived entries`

   Proposed:
   > `expands along graph neighbors, and automatically resurfaces archived entries when they score highly`

   Rationale: `deep_recall` is an implementation detail, not something the user
   invokes in the first-5-minutes flow. Removing the tool name keeps the doc
   at the right level of abstraction.

2. **Lines 75–92 — "What's Next?" section references `dispatch_task`, `cross_search`,
   multi-agent sharing — all standard/full profile tools:**

   Current: lists 4 advanced tools as "next steps" with no profile caveat.

   Proposed: add a single line before the bullet list:

   > _These tools are available in the `standard` and `full` profiles
   > (set `MEMORY_TOOL_PROFILE=standard` in your MCP config)._

   Rationale: a user who started with `minimal` (16 tools, the recommended
   starting point per README) will hit "tool not found" errors if they follow
   these steps without knowing they need a different profile.

---

## Section 6 — Quick Blind-Readability Check

**After 30 seconds, what does this product do?**
It gives an AI assistant memory that persists between sessions — so it can recall past
decisions and context instead of starting from scratch every time.

**What's the next thing I'd want to click?**
The "Get started in 5 minutes" link, to see how fast I can try the recall-then-remember
loop without reading about graph Laplacians first.

---

## Section 7 — Anti-Pattern Audit on Current README

1. **Architecture before benefit (line 16):** The second sentence names graph-Laplacian,
   eigenbasis, spectral retrieval, and diffusion before showing what the user gets. Loses
   the "IDE developer" reader in line 2.

2. **65-tool dump before one tool demo (lines 171–191):** The MCP Tools table lists all
   65 tools with dense descriptions before the reader has seen a single example of one
   tool working. Most tools will mean nothing at this point.

3. **Four install paths before the user has seen value (lines 24–75):** Option 1 through
   Option 4 appear before any screenshot, benchmark, or "here's what it looks like"
   moment. The reader hasn't committed yet.

4. **Feature SVG with no caption anchor (lines 18–20):** The `features.svg` image
   drops in with no surrounding prose — the reader has to decode six pillars plus a
   diffusion subsystem from a graphic before any narrative context is set.

5. **"MCP Tools: 65" badge in the hero (line 11):** Signals complexity and completeness
   to the builder, but reads as "this is overwhelming" to the evaluator. The badge is
   correct — just wrong placement for a hero that's trying to earn 30 seconds of
   attention.
