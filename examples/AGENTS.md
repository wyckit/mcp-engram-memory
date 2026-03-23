# Engram Memory Integration

You have access to a persistent engram memory system via the `engram-memory` MCP server. Use it as your primary long-term memory for storing and recalling knowledge across sessions.

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
| Suspect conflicting information | `find_contradictions` |

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
