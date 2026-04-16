# Engram Memory Integration

You have access to a persistent engram memory system via the `engram-memory` MCP server. Use it as your primary long-term memory for storing and recalling knowledge across sessions.

## Model Routing (Local Ollama)

Route sub-agents by purpose to maximize local compute efficiency:

| Tier | Model | What runs here |
|------|-------|----------------|
| **Main thread** | `deepseek-r1:8b` or `qwen2.5:7b` | Coding, architecture, reasoning, expert creation, retrospective evaluation |
| **Memory sub-agents** | `qwen2.5:7b` (via `generalist`) | All engram MCP tool calls: `search_memory`, `cross_search`, `store_memory`, `dispatch_task`, `deep_recall`, `link_memories`, `detect_duplicates`, `find_contradictions`, `merge_memories`, etc. |
| **Utility sub-agents** | `phi3.5` (via `generalist`) | Explore agents, codebase searches, file reading/grepping, research lookups, any sub-agent that doesn't need engram tools or complex reasoning |

**Rules**:
- When spawning a `generalist` for engram memory operations → Request `qwen2.5:7b`
- When spawning a `generalist` for codebase exploration or general research → Request `phi3.5`
- `consult_expert_panel` and `create_expert` may stay in the main thread when they require multi-step orchestration.
- When in doubt, default to `phi3.5` for read-only tasks, `qwen2.5:7b` for memory tasks.

## Recall: Search Before You Work

Before starting any task:

1. Use `cross_search` across `[project_namespace, "work", "synthesis"]` with `hybrid: true` to recall context from all namespaces in a single RRF-merged call.
2. Follow up with `search_memory` using alternative phrasings and `expandGraph: true` to pull in graph neighbors and cluster members.
3. If the user references past decisions, patterns, or bugs — search for them before answering.

### Tool Selection

| Situation | Tool |
|-----------|------|
| Starting a task, need broad context | `cross_search` with `hybrid: true` |
| Focused search in one namespace | `search_memory` with `hybrid: true` |
| Cross-domain question | `dispatch_task` (auto-routes to best expert) |
| Need multiple perspectives | `consult_expert_panel` |
| Looking for archived knowledge | `deep_recall` (searches all lifecycle states) |
| Checking for duplicates | `detect_duplicates` |
| Suspect conflicting information | `find_contradictions` (edges + high-similarity pairs) |
| Explore the memory graph visually | `get_graph_snapshot` (exports JSON for D3.js viewer; `full` profile) |

### Search Parameters

| Parameter | When to use |
|-----------|------------|
| `hybrid: true` | Most searches — BM25 + vector fusion |
| `expandGraph: true` | When you need related context — graph neighbors and cluster members |
| `rerank: true` | Precision-critical searches |
| `summaryFirst: true` | Large namespaces — search cluster summaries first |

## Store: Save What You Learn

Store memories whenever you complete a task, fix a bug, learn a pattern, receive a correction, or make an architectural decision.

| Field | Convention |
|-------|-----------|
| **namespace** | Project directory name |
| **id** | Descriptive kebab-case (e.g., `fix-auth-race-condition`) |
| **text** | Self-contained description with domain keywords for searchability |
| **category** | `decision`, `pattern`, `bug-fix`, `architecture`, `preference`, `lesson`, `reference`, `retrospective` |

### Duplicate Handling

When `store_memory` warns about duplicates:
- Existing is accurate: skip
- Existing is outdated: upsert with same ID
- Both distinct but related: store and `link_memories`
- Both redundant: `merge_memories`

### What NOT to Store

- Temporary task state
- Information already in the codebase
- Speculative or unverified conclusions

## Expert Routing

- `dispatch_task` routes to the best expert automatically. If `needs_expert` is returned, use `create_expert` with a detailed persona, then populate the expert namespace.
- Lifecycle: promote STM to LTM when stable and reused across sessions.
- Link related memories using: `parent_child`, `cross_reference`, `similar_to`, `contradicts`, `elaborates`, `depends_on`.

## Multi-Agent Sharing

- Set `AGENT_ID` env var per agent instance for namespace ownership and permissions.
- `cross_search` searches multiple namespaces in one call (RRF merge).
- `share_namespace` / `unshare_namespace` to grant/revoke access.
- `whoami` to verify identity, `list_shared` to see accessible namespaces.

## Session Retrospective

At the end of significant sessions:
1. Self-evaluate: what went well, what went wrong, lessons learned
2. Store with id `retro-YYYY-MM-DD-topic`, category `lesson`
3. Link to related memories
4. Search past retrospectives before starting similar work

## Tool Profiles

Set `MEMORY_TOOL_PROFILE` in your MCP config to control exposed tools:

| Profile | Tools | Includes |
|---------|-------|---------|
| `minimal` | 16 | Core CRUD, composite (remember/recall/reflect), admin, multi-agent |
| `standard` | 35 | Adds graph, lifecycle, clustering, intelligence |
| `full` | 52 | Everything — expert routing, debate, synthesis, benchmarks, visualization |

`get_graph_snapshot` requires `full`. Most daily use works on `minimal`.

## Memory Graph Visualizer

To explore your memory visually (requires `full` profile):
1. Call `get_graph_snapshot` from any conversation
2. Save the returned JSON to `visualization/snapshot.json` in the repo
3. Open `visualization/memory-graph.html` in a browser
