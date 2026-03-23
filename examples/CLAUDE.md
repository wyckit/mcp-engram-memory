# Engram Memory Integration

You have access to a persistent engram memory system via the `engram-memory` MCP server. Use it as your primary long-term memory for storing and recalling knowledge across sessions.

## Recall: Search Before You Work

At the start of every conversation, search engram memory for relevant context using **parallel multi-agent recall**:

1. Identify the project namespace from the working directory name (e.g., `my-project`, `web-app`)
2. Launch **up to 2 parallel Agent subagents using `model: "haiku"`**, each searching a different angle:
   - **Agent 1 — Broad context**: Use `cross_search` across `[project_namespace, "work", "synthesis"]` with `hybrid: true` and a query describing the current task. This combines multi-namespace search into a single RRF-merged call.
   - **Agent 2 — Related topics**: `search_memory` in the project namespace with alternative phrasings or related keywords (e.g., if the task is about "search performance", also search for "indexing", "SIMD", "benchmarks"). Use `hybrid: true` and `expandGraph: true` for keyword+vector fusion and graph neighbors.
3. Combine results from all agents before proceeding — this gives you broader recall than a single query
4. If the user references past decisions, patterns, or bugs — search for them before answering
5. For graph-connected knowledge, use `expandGraph: true` to automatically pull in linked memories

> **Cost optimization**: Recall agents MUST use `model: "haiku"` — they only call MCP tools and relay results, no complex reasoning needed. This uses the separate Sonnet/Haiku usage bucket and is ~60x cheaper than Opus per token.

## Model Routing for Cost Efficiency

Route engram memory operations to the cheapest model that can handle them. **Opus costs ~5x Sonnet and ~60x Haiku** per token, and Sonnet has its own separate usage bucket on Pro/Max plans.

### Routing Tiers

| Tier | Model | Operations | Why |
|------|-------|-----------|-----|
| **Tier 1 — Haiku** | `model: "haiku"` | `search_memory`, `cross_search`, `whoami`, `list_shared`, `get_neighbors`, `traverse_graph`, `get_domain_tree`, `store_memory`, `delete_memory`, `link_memories`, `detect_duplicates` | Simple MCP tool calls — no reasoning, just relay results |
| **Tier 2 — Sonnet** | `model: "sonnet"` | `dispatch_task`, `deep_recall`, `find_contradictions`, `merge_memories` | Moderate analysis of search results needed |
| **Tier 3 — Opus** | Main thread (no sub-agent) | `consult_expert_panel`, `create_expert`, session retrospectives, complex multi-step memory workflows | Requires deep reasoning or multi-step orchestration |

### How to Apply

- **Recall sub-agents** (session start): Always `model: "haiku"` — they just call search tools and return text
- **Mid-task lookups** (user asks about past decisions): Spawn a `model: "haiku"` sub-agent to search, process results in the main Opus thread
- **Store after task completion**: Spawn a `model: "haiku"` sub-agent with the memory payload — it just calls `store_memory` and confirms
- **Retrospectives and expert creation**: Keep in the main Opus thread — these require judgment about what to store and how to structure it
- **dispatch_task / deep_recall**: Use `model: "sonnet"` sub-agents — these return richer results that benefit from moderate analysis before relaying

### Anti-Patterns to Avoid

- **Never launch Opus sub-agents for simple searches** — this is the #1 waste
- **Never process large search result sets in the main Opus thread** — delegate to a cheaper sub-agent and have it return only the relevant findings
- **Don't skip sub-agents for store operations** just because they seem simple — the Opus tokens spent formatting the store_memory call and processing the response add up across sessions

## Tool Selection: Which Search Tool to Use

Pick the right tool for the situation:

| Situation | Tool | Why |
|-----------|------|-----|
| Starting a task, need project context | `cross_search` | RRF-merged multi-namespace search in one call |
| Focused search in one namespace | `search_memory` | Direct namespace search, fast and focused |
| Complex cross-domain question | `dispatch_task` | Semantic routing finds the best expert namespace automatically |
| Cross-domain with populated domain tree | `dispatch_task` with `hierarchical: true` | Coarse-to-fine tree walk: root -> branch -> leaf |
| Need perspectives from multiple domains | `consult_expert_panel` | Parallel multi-namespace search with debate tracking |
| Looking for archived/forgotten knowledge | `deep_recall` | Searches ALL lifecycle states including archived; auto-resurrects high-scoring entries |
| Checking for duplicates before storing | `detect_duplicates` | Pairwise cosine similarity scan |
| Suspect conflicting information | `find_contradictions` | Surfaces entries linked with `contradicts` edges |

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

> **Cost optimization**: For routine stores, spawn a `model: "haiku"` sub-agent with all the fields pre-composed. The sub-agent just calls `store_memory` and confirms success. Only keep stores in the main Opus thread when you need to reason about *what* to store (e.g., retrospectives, deciding between update vs new entry).

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

- **`dispatch_task`**: Describe a problem -> the system finds the best-matching expert namespace and returns relevant context. Use via `model: "sonnet"` sub-agent.
- **`create_expert`**: Register a new expert or domain node. Keep in main Opus thread — requires judgment about persona design.
- **`get_domain_tree`**: Inspect the routing topology. Use via `model: "haiku"` sub-agent.

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

> **Cost note**: Retrospectives are Tier 3 (Opus) — keep in the main thread. The reasoning about what went well/wrong requires judgment. Only the final `store_memory` call can be delegated to a Haiku sub-agent once the text is composed.

At the end of significant work sessions:

1. **Evaluate** — what went well, what went wrong, what you'd do differently
2. **Store** — id `retro-YYYY-MM-DD-topic`, category `lesson`, specific actionable lessons
3. **Link** — connect to related bug fixes, patterns, or decisions via `link_memories`
4. **Search past retrospectives** before starting similar work

## Context Compaction Bridge

During long sessions that may hit context limits, store incremental progress memories:
- Before a complex task, store a `reference` memory with the plan and current state
- After major milestones, store a `decision` or `pattern` memory capturing what was done
- Use descriptive IDs like `wip-YYYY-MM-DD-task-name` for in-progress snapshots
- **Use `model: "haiku"` sub-agents** for all compaction bridge stores — pre-composed payloads with no reasoning needed

## Namespace Guide

| Namespace | Purpose |
|-----------|---------|
| `{project-dir}` | Project-specific knowledge (use directory name) |
| `work` | Cross-project workflow preferences and tooling decisions |
| `synthesis` | Cross-project architectural insights and patterns |
| `expert_{id}` | Expert routing namespaces (auto-created by `create_expert`) |
| `domain_{id}` | Domain node namespaces for HMoE tree |
