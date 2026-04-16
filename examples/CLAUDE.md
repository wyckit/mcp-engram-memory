# Engram Memory Integration

You have access to a persistent engram memory system via the `engram-memory` MCP server. Use it as your primary long-term memory for storing and recalling knowledge across sessions.

## Model Routing

Route sub-agents by purpose to maximize your subscription:

| Tier | Model | What runs here |
|------|-------|----------------|
| **Main thread** | Opus | Coding, architecture, reasoning, expert creation, retrospective evaluation |
| **Memory sub-agents** | Sonnet (`model: "sonnet"`) | All engram MCP tool calls: `search_memory`, `cross_search`, `store_memory`, `dispatch_task`, `deep_recall`, `link_memories`, `detect_duplicates`, `find_contradictions`, `merge_memories`, etc. |
| **Utility sub-agents** | Haiku (`model: "haiku"`) | Explore agents, codebase searches, file reading/grepping, research lookups, any sub-agent that doesn't need engram tools or complex reasoning |

**Rules**:
- When spawning an Agent for engram memory operations → `model: "sonnet"`
- When spawning an Agent for codebase exploration, file searches, or general research → `model: "haiku"`
- `consult_expert_panel` and `create_expert` may stay in the main Opus thread when they require multi-step orchestration or judgment about persona design
- When in doubt, default to Haiku for read-only tasks, Sonnet for memory tasks

## Recall: Search Before You Work

At the start of every conversation, search engram memory for relevant context using **parallel multi-agent recall**:

1. Identify the project namespace from the working directory name (e.g., `my-project`, `web-app`)
2. Launch **up to 3 parallel Agent subagents using `model: "sonnet"`**, each searching a different angle:
   - **Agent 1 — Broad context**: Use `cross_search` across `[project_namespace, "work", "synthesis"]` with `hybrid: true` and a query describing the current task. This combines multi-namespace search into a single RRF-merged call.
   - **Agent 2 — Related topics**: `search_memory` in the project namespace with alternative phrasings or related keywords (e.g., if the task is about "search performance", also search for "indexing", "SIMD", "benchmarks"). Use `hybrid: true` and `expandGraph: true` for keyword+vector fusion and graph neighbors.
   - **Agent 3 — Expert routing**: `dispatch_task` with a description of the current task to find the best-matching expert namespace. Use `hierarchical: true` if the domain tree is populated.
3. Combine results from all agents before proceeding — this gives you broader recall than a single query
4. If the user references past decisions, patterns, or bugs — search for them before answering
5. For graph-connected knowledge, use `expandGraph: true` to automatically pull in linked memories (neighbors, cluster members)

## Tool Selection: Which Search Tool to Use

Pick the right tool for the situation:

| Situation | Tool | Why |
|-----------|------|-----|
| Starting a task, need broad context | `cross_search` | RRF-merged multi-namespace search in one call |
| Focused search in one namespace | `search_memory` | Direct namespace search, fast and focused |
| Complex cross-domain question | `dispatch_task` | Semantic routing finds the best expert namespace automatically |
| Cross-domain with populated domain tree | `dispatch_task` with `hierarchical: true` | Coarse-to-fine tree walk: root -> branch -> leaf |
| Need perspectives from multiple domains | `consult_expert_panel` | Parallel multi-namespace search with debate tracking |
| Looking for archived/forgotten knowledge | `deep_recall` | Searches ALL lifecycle states including archived; auto-resurrects high-scoring entries |
| Checking for duplicates before storing | `detect_duplicates` | Pairwise cosine similarity scan |
| Suspect conflicting information | `find_contradictions` | Surfaces entries with `contradicts` edges + high-similarity pairs |
| Explore the memory graph visually | `get_graph_snapshot` | Exports JSON for the D3.js visualizer (`full` profile only) |

**Default to `cross_search`** for initial recall (covers multiple namespaces in one call). Use `search_memory` for focused follow-ups. Use `dispatch_task` when you don't know which namespace holds the answer.

### Search Parameters Guide

| Parameter | When to use |
|-----------|------------|
| `hybrid: true` | Most searches — combines BM25 keyword matching with vector similarity via RRF fusion |
| `expandGraph: true` | When you need related context — pulls in graph neighbors and cluster members |
| `rerank: true` | Precision-critical searches — applies token-overlap reranking to refine results |
| `explain: true` | Debugging search quality — returns full retrieval diagnostics |
| `summaryFirst: true` | Large namespaces — searches cluster summaries first, then drills into members |

## Store: Save What You Learn

Store memories into engram memory whenever you:
- **Complete a task** — store the approach, key decisions, and any gotchas discovered
- **Fix a bug** — store the root cause and fix so it's findable next time
- **Learn a project pattern** — architecture conventions, naming rules, file structure
- **Receive a correction** — store the correct approach so you don't repeat the mistake
- **Make an architectural decision** — store the rationale with context

All stores should go through a `model: "sonnet"` sub-agent. Compose the fields in the main Opus thread, then hand off the `store_memory` call to Sonnet.

### How to Store

Use `store_memory` with these conventions:

| Field | Convention |
|-------|-----------|
| **namespace** | Project directory name (e.g., `my-project`, `web-app`) |
| **id** | Descriptive kebab-case (e.g., `fix-dll-lock-issue`, `pattern-lock-ordering`) |
| **text** | Clear, self-contained description — should be understandable without extra context |
| **category** | One of: `decision`, `pattern`, `bug-fix`, `architecture`, `preference`, `lesson`, `reference`, `retrospective` |

### Write for Searchability

The `text` field is embedded and used for semantic search. Write it so **future queries will match**:
- Include the problem domain keywords (e.g., "SIMD", "quantization", "lock contention")
- State both the problem and the solution — a future search might phrase either way
- Avoid vague language like "fixed the issue" — be specific about what was fixed and how

### Handling Duplicates

`store_memory` warns when near-duplicates are detected on ingest. When you see this:
- If the existing memory is still accurate, **skip the store** — don't create duplicates
- If the existing memory is outdated, **update it** by storing with the same ID (upsert)
- If both memories are truly redundant, use **`merge_memories`** to combine them
- If both memories are distinct but related, store the new one and **`link_memories`** between them

### What NOT to Store

- Temporary task state (what file you're currently editing)
- Information already in the codebase (don't duplicate README content)
- Speculative or unverified conclusions

## Expert Routing

The system supports **semantic expert routing** via `dispatch_task`, `create_expert`, and `get_domain_tree`:

- **`dispatch_task`**: Describe a problem -> the system finds the best-matching expert namespace and returns relevant context. Run via `model: "sonnet"` sub-agent.
- **`create_expert`**: Register a new expert or domain node. Keep in main Opus thread — requires judgment about persona design.
- **`get_domain_tree`**: Inspect the routing topology. Run via `model: "sonnet"` sub-agent.

### Lifecycle Management

- New memories default to `stm` (short-term) — this is fine for most things
- **Promote to `ltm`** when a memory proves stable and reusable (recalled 2+ times, documents a stable pattern, captures a user preference, or a recurring bug fix)
- Use `link_memories` when two memories are related: `parent_child`, `cross_reference`, `similar_to`, `contradicts`, `elaborates`, `depends_on`

## Multi-Agent Sharing

When multiple agents share the same engram-memory server, set `AGENT_ID` env var per agent instance:

- **Namespace ownership**: First write to a namespace registers ownership. Only owners can share.
- **Permission control**: `share_namespace` grants read or write access. `unshare_namespace` revokes it.
- **Cross-namespace search**: `cross_search` searches multiple namespaces in one call with RRF merge, respecting permissions.
- **Identity**: `whoami` returns the current agent's ID and accessible namespaces.
- **Backward compatible**: When `AGENT_ID` is not set (default agent), all namespaces are accessible.

## Session Retrospective: Learn From Each Session

At the end of significant work sessions:

1. **Evaluate** (main Opus thread — requires judgment): what went well, what went wrong, what you'd do differently, key decisions made
2. **Store** via `model: "sonnet"` sub-agent: id `retro-YYYY-MM-DD-topic`, category `lesson`, specific actionable lessons
3. **Link** to related bug fixes, patterns, or decisions via `link_memories`
4. **Search past retrospectives** before starting similar work

## Context Compaction Bridge

During long sessions that may hit context limits, store incremental progress memories via `model: "sonnet"` sub-agents:
- Before a complex task, store a `reference` memory with the plan and current state
- After major milestones, store a `decision` or `pattern` memory capturing what was done
- Use descriptive IDs like `wip-YYYY-MM-DD-task-name` for in-progress snapshots

## Tool Profiles

Set `MEMORY_TOOL_PROFILE` in your MCP config env to control which tools are exposed:

| Profile | Tools | Includes |
|---------|-------|---------|
| `minimal` | 16 | Core CRUD, composite (remember/recall/reflect), admin, multi-agent |
| `standard` | 35 | Adds graph, lifecycle, clustering, intelligence |
| `full` | 52 | Everything — expert routing, debate, synthesis, benchmarks, visualization |

`get_graph_snapshot` (graph visualizer) requires `full`. Most daily use works fine on `minimal`.

## Memory Graph Visualizer

To explore your memory visually (requires `full` profile):
1. Call `get_graph_snapshot` from any conversation
2. Save the returned JSON to `visualization/snapshot.json` in the repo
3. Open `visualization/memory-graph.html` in a browser

## Namespace Guide

| Namespace | Purpose |
|-----------|---------|
| `{project-dir}` | Project-specific knowledge (use directory name) |
| `work` | Cross-project workflow preferences and tooling decisions |
| `synthesis` | Cross-project architectural insights and patterns |
| `expert_{id}` | Expert routing namespaces (auto-created by `create_expert`) |
| `domain_{id}` | Domain node namespaces for HMoE tree |
