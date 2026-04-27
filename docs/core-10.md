# Core-10: The Tools That Handle 80% of Work

[< Back to README](../README.md)

Engram Memory exposes 65 MCP tools. You don't need most of them on day
one. This page lists the 10 tools that cover the typical workflow — from
first memory to multi-agent setups — in the order you'll naturally
encounter them.

## The Core-10

### Every day — composite tools (auto-embed, auto-link, auto-route)

**1. `remember`** — *the default way to store a memory.*
Auto-embeds text, blocks near-duplicates (≥ 0.95 similarity), auto-links
to related entries (≥ 0.65). Use this unless you have a reason not to.
→ Takes: `id`, `ns`, `text`, optional `category` / `metadata` / `lifecycleState`

**2. `recall`** — *the default way to retrieve a memory.*
Hybrid search (BM25 + vector) with graph neighbor expansion, falls back
to `deep_recall` if top results are weak, then graph-aware spectral
re-ranking via the memory-diffusion kernel (default `spectralMode="auto"` —
short conceptual queries get cluster-boost, longer/precise queries get
cluster-mean subtraction; pass `"none"` to opt out). Omit `ns` to
auto-route via the expert dispatcher.
→ Takes: `query`, optional `ns`, `k`, `hybrid`, `rerank`, `expandGraph`, `spectralMode`

**3. `reflect`** — *store a retrospective or lesson.*
Auto-stores as LTM, auto-links to explicitly-mentioned ids plus any
semantically-similar entries, surfaces past reflections on the same
topic. Call at the end of every meaningful work session.
→ Takes: `text`, `ns`, `topic`, optional `relatedIds`

### When you need precision — low-level tools

**4. `search_memory`** — *raw search with every knob.*
Exposes `minScore`, `category`, `diversity`, `expandQuery`, `usePhysics`,
`temperature`, `rerank`, etc. Reach for this when `recall` is too smart
and you want deterministic control.

**5. `store_memory`** — *raw store with vector control.*
Pass your own vector, set lifecycle state explicitly, skip dedup/linking.
Useful for ingesting pre-embedded data.

**6. `get_memory`** — *fetch a specific entry by id.*
Returns the full `CognitiveEntry` record.

**7. `delete_memory`** — *remove an entry by id.*
Cascades to graph edges and cluster memberships.

### When you grow into multi-agent — sharing + routing

**8. `cross_search`** — *search multiple namespaces in one call.*
Permission-aware (filters to namespaces this agent can read),
RRF-merges results. Pair with `AGENT_ID` and `share_namespace` for
multi-agent workflows.

**9. `whoami`** — *identity + accessible namespaces.*
Returns the current agent's id, the namespaces it owns, and the
namespaces other agents have shared with it. Your first diagnostic when
something looks like a permission issue.

**10. `dispatch_task`** — *let the system route your question to an expert.*
Describe a problem without picking a namespace; the system scores your
query against the expert meta-index and returns top-matching expert
contexts. Set `hierarchical: true` to walk the root → branch → leaf tree.

---

## When to reach for what

| You want to... | Use |
|----------------|-----|
| Save something you just learned | `remember` |
| Find something you stored | `recall` |
| Write a session retrospective | `reflect` |
| Tune every search parameter | `search_memory` |
| Ingest pre-embedded data | `store_memory` |
| Look up one entry by id | `get_memory` |
| Delete an entry | `delete_memory` |
| Search across projects | `cross_search` |
| Check who you are / what you can see | `whoami` |
| Auto-route to the right domain | `dispatch_task` |

## The other 55 tools

- **Graph** — `link_memories`, `unlink_memories`, `get_neighbors`,
  `traverse_graph` for explicit relation management.
- **Lifecycle** — `promote_memory`, `decay_cycle`, `configure_decay`,
  `deep_recall` for manual STM→LTM→archived control.
- **Clusters** — `create_cluster`, `get_cluster`, `list_clusters`,
  `collapse_cluster` for hierarchical summarization.
- **Experts** — `create_expert`, `link_to_parent`, `get_domain_tree` for
  meta-index management.
- **Sharing (multi-agent)** — `share_namespace`, `unshare_namespace`,
  `list_shared`. See [docs/multi-agent.md](multi-agent.md).
- **Benchmarks + maintenance** — `run_benchmark`, `rebuild_embeddings`,
  `cognitive_stats`, `compression_stats`.
- **Intelligence** — `detect_duplicates`, `find_contradictions`,
  `merge_memories`, `synthesize_memories`.
- **Advanced** — `consult_expert_panel`, `map_debate_graph`,
  `resolve_debate`, `get_graph_snapshot`, `get_context_block`.

See [docs/mcp-tools-reference.md](mcp-tools-reference.md) for the
complete reference.
