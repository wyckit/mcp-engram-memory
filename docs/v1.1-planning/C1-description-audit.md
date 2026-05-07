# C1 — Minimal-16 Tool Description Audit

**Date:** 2026-05-06
**Author:** Audit agent (Claude Code)
**Scope:** v1.1 minimal profile (16 tools). Source edits deferred to Phase 2.

---

## Executive Summary

The current tool descriptions are scenario-led and broadly accurate, but they systematically under-invest in *negative guidance* — the phrases that tell an LLM agent which tool **not** to reach for. The result is predictable mis-selection at five specific boundaries: `recall` vs `search_memory`, `remember` vs `store_memory`, `recall` vs `cross_search`, `dispatch_task` vs `recall`, and `reflect` vs `remember`. The single worst offender is `recall`, whose description mentions `search_memory` only once in a subordinate clause ("Use search_memory only for low-level queries without fallback") that an agent scanning for the right tool will frequently miss. The `cross_search` description never mentions `recall` at all, leaving the multi-vs-single-namespace distinction entirely implicit. `engram_status` does not yet exist. The proposed rewrites enforce a two-sentence maximum, lead with the primary use-case verb (find / save / route / check), and place negative guidance in the second sentence with an explicit alternative tool named.

---

## Section 1: Per-Tool Audit Table

| # | Tool | File | Current Description (verbatim) | Proposed Rewrite | Rationale |
|---|------|------|-------------------------------|-----------------|-----------|
| 1 | `recall` | CompositeTools.cs | "Default search tool. Searches a namespace with hybrid+graph expansion and falls back to deep_recall for archived entries. Omit namespace to auto-route via expert dispatcher. Use search_memory only for low-level queries without fallback." | "Find memories in one namespace (or auto-route across all) — the default search for any retrieval task. Don't use it to search across multiple specific namespaces simultaneously; use `cross_search` instead." | Current description buries the negative in a trailing clause. The biggest confusion is recall vs cross_search (multi-ns), not recall vs search_memory (low-level). Negative must name cross_search explicitly in position 2. |
| 2 | `remember` | CompositeTools.cs | "Default way to store memories. Auto-embeds text, blocks near-duplicates, and links to related entries. Use this instead of store_memory unless you need raw vector control." | "Save a new memory with automatic duplicate detection and graph linking — the default way to store anything. Don't use `store_memory` directly unless you need to supply a raw embedding vector or skip duplicate checking." | Current is good but inverts the negative: it says "use X instead of Y" rather than "don't use Y unless Z." Flipping puts the guard on the wrong tool. Rewrite anchors the negative on store_memory and narrows the exception to concrete cases. |
| 3 | `reflect` | CompositeTools.cs | "Store a lesson or retrospective as LTM with auto-linking. Use at the end of work sessions to capture what went well, what went wrong, and key decisions." | "Save an end-of-session lesson or retrospective — always stored as long-term memory with automatic cross-linking to related work. Don't use it for general notes or decisions; use `remember` for those." | Missing negative guidance entirely. Agents frequently reach for `reflect` whenever they want to store anything important (LTM), because LTM sounds like "important." Must explicitly redirect general saves to `remember`. |
| 4 | `search_memory` | CoreMemoryTools.cs | "Low-level namespace search with full parameter control. Use recall instead for auto-routing and fallback. Supports hybrid BM25+vector, reranking, query expansion, graph expansion, physics re-ranking, and explain mode." | "Find memories in one namespace with full parameter control (hybrid search, physics ranking, explain mode). Don't use this as your default search; use `recall` instead — it adds auto-routing, fallback to archived entries, and spectral re-ranking automatically." | Current description correctly points to `recall` but leads with "low-level" — a term that does not map to any user intent. Rewrite leads with what it does (full parameter control) and explains *why* `recall` is preferred (the concrete features you get for free). |
| 5 | `store_memory` | CoreMemoryTools.cs | "Low-level store with explicit vector/text control. Use remember instead for auto-dedup and auto-linking. Supports namespace isolation, categorical metadata, and lifecycle tracking." | "Save a memory when you need to supply a pre-computed embedding vector or control lifecycle state exactly. Don't use this by default; use `remember` instead — it auto-embeds, blocks duplicates, and links related entries without extra steps." | Same "low-level" problem as search_memory. Rewrite replaces the jargon with the *only two* concrete reasons to prefer store_memory over remember, making the fallback to remember feel like the obvious default. |
| 6 | `store_batch` | CoreMemoryTools.cs | "Bulk-store multiple entries in one write-lock. Faster than repeated store_memory calls. Each entry gets contextual prefix embedding. Returns stored count and duplicate warnings." | "Save many memories at once in a single write operation — use this when storing 5 or more entries to avoid repeated round-trips. Don't use it for one or two entries; use `remember` for those to get per-entry duplicate blocking and auto-linking." | Current description is functional but has no negative guidance and implies store_batch is an alternative to store_memory rather than to remember. Rewrite sets the threshold (5+) and redirects small stores to `remember`. |
| 7 | `delete_memory` | CoreMemoryTools.cs | "Delete a memory entry by ID. Cascades to remove graph edges and cluster memberships." | "Permanently remove a single memory and all its graph edges and cluster memberships by ID. Don't use this to archive old memories; change the lifecycle state to 'archived' via `store_memory` or `remember` to preserve them for deep recall." | Current description is accurate but omits the most important negative: delete is irreversible and the user probably wants archiving instead. Adds explicit alternative pathway. |
| 8 | `get_memory` | AdminTools.cs | "Inspect a single entry's full context: lifecycle state, graph edges, cluster memberships. Does not count as an access." | "Look up one memory's full metadata — lifecycle state, graph edges, cluster memberships, access count — without triggering an access-count increment. Don't use it to search by topic; use `recall` or `search_memory` for that." | Current description is accurate but has no negative. Agents sometimes call get_memory speculatively (trying IDs they aren't sure of) when they should be searching. Negative redirects to search tools. |
| 9 | `cognitive_stats` | AdminTools.cs | "Get system overview: entry counts by lifecycle state, cluster count, edge count, and namespace list." | "Check how many memories exist across lifecycle states (STM/LTM/archived), plus cluster and edge counts and the full namespace list. Don't use it to check background worker health; use `engram_status` for that." | Current description is clear but will collide with engram_status once that tool ships. Negative future-proofs the description by explicitly redirecting health/worker questions. |
| 10 | `cross_search` | MultiAgentTools.cs | "Search multiple namespaces in one call with RRF-merged results. Use when information may span multiple knowledge domains. Supports hybrid search, reranking, cluster-aware MMR diversity, min-score filtering, and category filtering. Note: expand_graph, expand_query, use_physics, and temperature are single-namespace features of search_memory and are not yet supported by cross_search." | "Find memories across multiple specific namespaces in one call, merging results by relevance rank. Don't use it when you know which single namespace to search — use `recall` for that, which also adds graph expansion and archived-entry fallback." | Current description is the most technically complete but never names `recall` as the alternative and leads with implementation jargon ("RRF-merged"). The note about unsupported features belongs in parameter docs, not the primary description. |
| 11 | `share_namespace` | MultiAgentTools.cs | "Grant another agent read or write access to a namespace you own." | "Grant another agent read or write access to a namespace you own. Don't use it to check what's already shared; use `list_shared` for that." | Current is crisp but has no negative. Agents occasionally call share_namespace to inspect sharing state. Single negative sentence fixes this without bloating the description. |
| 12 | `unshare_namespace` | MultiAgentTools.cs | "Revoke an agent's access to a namespace you own." | "Revoke another agent's access to a namespace you own. Don't use it to check current sharing state first; call `list_shared` to confirm what to revoke before calling this." | Same pattern as share_namespace. Adds a gentle workflow hint: inspect before revoking. |
| 13 | `list_shared` | MultiAgentTools.cs | "List all namespaces that OTHER agents have shared with the current agent, showing owner and access level for each." | "List every namespace other agents have shared with you, showing owner and access level. Don't use it to check your own namespaces or identity; use `whoami` for the full picture of what you own and can access." | Capitalizing "OTHER" is doing the disambiguation work that a proper negative sentence should do. Rewrite replaces the typographic trick with an explicit redirect to `whoami`. |
| 14 | `whoami` | MultiAgentTools.cs | "Return current agent identity and accessible namespaces (owned + shared with this agent). Use to verify multi-agent configuration." | "Check this agent's ID and the full list of namespaces it owns or has access to. Don't use it only to see shared namespaces; use `list_shared` when you specifically want the inbound-sharing view with owner attribution." | Current description is accurate but the negative helps agents choose between whoami and list_shared based on intent: ownership/identity vs. inbound-share attribution. |
| 15 | `dispatch_task` | ExpertTools.cs | "Route a query to the best-matching expert namespace. Returns expert profile and top memories as context. Use when you need domain-specific knowledge but don't know which namespace holds it. Set hierarchical=true for tree routing (root → branch → leaf)." | "Find which expert namespace best matches your question, then retrieve relevant memories from it — use this when you don't know which namespace holds the answer. Don't use it as a general search; if you already know the namespace, use `recall` directly." | Current description is the best in the set but still mentions hierarchical routing inline. The negative must clearly separate dispatch_task (unknown namespace) from recall (known namespace). |
| 16 | `engram_status` | NEW (AdminTools.cs) | *(tool does not exist yet)* | "Check the last-run timestamps, cycle counts, and error counts for every background worker (decay, consolidation, diffusion, accretion). Don't use it to see memory counts or namespace lists; use `cognitive_stats` for that." | New tool. Description written from spec. Negative immediately disambiguates from cognitive_stats, the tool agents will most naturally confuse it with. |

---

## Section 2: 20-Prompt Selection Matrix

> "Correct tool" is the tool the system designer intends. "Wrong but tempting" is the tool a description-confused agent would reach for instead. "Disambiguating phrase" is the specific phrase that must appear in the winning tool's description to steer correctly.

| # | User Prompt | Correct Tool | Wrong-but-Tempting Tool | Disambiguating Phrase That Must Appear |
|---|-------------|-------------|------------------------|---------------------------------------|
| 1 | "I want to store a decision we just made about logging" | `remember` | `store_memory` | "the default way to store anything" — establishes remember as the fallback, not store_memory |
| 2 | "I want to know what we discussed about caching last week" | `recall` | `search_memory` | "the default search for any retrieval task" — agents must see recall as the first tool to reach for |
| 3 | "I want to find related memories across all my projects" | `cross_search` | `recall` | "across multiple specific namespaces" — single vs multi namespace is the line |
| 4 | "I want to see what background workers have been doing" | `engram_status` | `cognitive_stats` | "background worker … last-run timestamps" — worker-health intent vs. entry-count intent |
| 5 | "I don't know which namespace has the answer — route me to the right expert" | `dispatch_task` | `recall` | "when you don't know which namespace holds the answer" — the routing-uncertainty signal |
| 6 | "Save a lesson learned from today's debugging session" | `reflect` | `remember` | "end-of-session lesson or retrospective" — time/session framing distinguishes reflect |
| 7 | "Store a batch of 20 bug-fix notes I have ready" | `store_batch` | `remember` | "5 or more entries" — numeric threshold makes the decision rule explicit |
| 8 | "Remove the memory with ID 'old-auth-pattern'" | `delete_memory` | *(no ambiguity, but validate)* | "Permanently remove" — should not be confused with archiving; negative for archive path matters |
| 9 | "How many memories are in the 'work' namespace right now?" | `cognitive_stats` | `get_memory` | "how many memories exist across lifecycle states" — count intent vs. single-entry lookup |
| 10 | "What is the full metadata for the entry 'fix-dll-lock-issue'?" | `get_memory` | `recall` | "Look up one memory's full metadata … by ID" — ID-lookup vs. topic search |
| 11 | "Search the 'synthesis' and 'work' namespaces for anything about retry logic" | `cross_search` | `recall` | "multiple specific namespaces … in one call" — the plural namespace specification is the trigger |
| 12 | "What can I recall about authentication from last month?" | `recall` | `cross_search` | "one namespace" — if a namespace is implied/provided, recall beats cross_search |
| 13 | "I need to write a note that captures our architecture decision on event sourcing" | `remember` | `reflect` | "general notes or decisions" in reflect's negative — redirects architectural decisions to remember |
| 14 | "End of sprint — let me record what went wrong with the deployment pipeline" | `reflect` | `remember` | "end-of-session lesson or retrospective" — session-boundary framing is the key signal |
| 15 | "Check if agent-B can see our shared 'synthesis' namespace" | `list_shared` | `whoami` | "other agents have shared with you, showing owner" — inbound-sharing view vs. identity |
| 16 | "What namespaces does this agent own?" | `whoami` | `list_shared` | "namespaces it owns or has access to" — ownership intent vs. inbound-share attribution |
| 17 | "I have a pre-computed 768-dimension embedding I want to store directly" | `store_memory` | `remember` | "supply a pre-computed embedding vector" — the only concrete reason to prefer store_memory |
| 18 | "I want to see total edge count and cluster count in the system" | `cognitive_stats` | `engram_status` | "cluster and edge counts" — structural-count intent vs. worker-health intent |
| 19 | "Grant agent-A write access to the 'mcp-engram-memory' namespace" | `share_namespace` | `list_shared` | "Grant another agent read or write access" — action verb distinguishes from inspection tool |
| 20 | "Show me what the expert routing system thinks is the best domain for 'database schema migration'" | `dispatch_task` | `recall` | "which expert namespace best matches your question" — domain-routing intent vs. direct memory search |

---

## Section 3: Cross-Tool Relationship Pairs with Overlap Risk

### 3.1 `recall` vs `search_memory` — high-level vs. low-level

**Overlap risk: Medium.** Both search a single namespace with hybrid+vector options. An agent that sees `search_memory`'s parameter richness may prefer it as "more powerful." The disambiguating invariant is: `recall` is always correct unless you need physics ranking, explain mode, query expansion, or a raw vector as input — all of which require `search_memory`. The proposed rewrite for `recall` leads with "the default search" and the proposed rewrite for `search_memory` leads with "full parameter control (hybrid search, physics ranking, explain mode)" so the functional difference is front-loaded in both descriptions.

**Required phrase in `recall`:** "the default search for any retrieval task"
**Required phrase in `search_memory`:** "Don't use this as your default search; use `recall` instead"

### 3.2 `remember` vs `store_memory` — auto vs. manual

**Overlap risk: High.** Both save a memory entry. The difference (auto-embed + dedup + auto-link vs. raw control) is invisible at the intent level — "I want to save something" could match either. The current descriptions both point at each other, creating a circular negative ("use X instead of Y" in both directions). The fix is asymmetric authority: `remember`'s description must claim ownership of all default saves, while `store_memory` must enumerate its *only* two valid use-cases (pre-computed vector, exact lifecycle control) and name `remember` as the correct default.

**Required phrase in `remember`:** "the default way to store anything"
**Required phrase in `store_memory`:** "Don't use this by default; use `remember` instead"

### 3.3 `recall` vs `cross_search` — single namespace vs. multi-namespace

**Overlap risk: High.** This is the most common mis-selection in the current set. Both are search tools that accept a query and return results. The current `cross_search` description never mentions `recall` as an alternative, and `recall`'s description never mentions `cross_search`. An agent trying to "find something from several projects" could pick either. The disambiguating invariant is purely whether multiple namespaces are being searched simultaneously. The proposed rewrites add this line to `recall`: "Don't use it to search across multiple specific namespaces simultaneously; use `cross_search` instead." And `cross_search` gains: "Don't use it when you know which single namespace to search — use `recall` for that."

**Required phrase in `recall`:** "Don't use it to search across multiple specific namespaces"
**Required phrase in `cross_search`:** "Don't use it when you know which single namespace to search"

### 3.4 `dispatch_task` vs `recall` — route-to-expert vs. direct search

**Overlap risk: Medium-High.** When an agent does not know where an answer lives, both `recall` (with no namespace, triggering broadcast mode) and `dispatch_task` can be plausible choices. The difference is intent: `dispatch_task` returns *which expert* to consult plus context from that expert, while `recall` without a namespace does a scored broadcast across all namespaces. The `dispatch_task` description must make "unknown namespace" the primary trigger and warn against using it as a general search. `recall` should surface its broadcast behavior only as a fallback, not a selling point.

**Required phrase in `dispatch_task`:** "when you don't know which namespace holds the answer"
**Required phrase in `recall`:** The ns=null broadcast behavior should remain implicit (not promoted) — the description should not advertise it as a first-class use-case.

### 3.5 `reflect` vs `remember` — lessons vs. general

**Overlap risk: Medium.** Both save memories. `reflect` is distinguished by: (a) forced LTM lifecycle, (b) auto-topic-tagging under a dated id, (c) implicit "end of session" framing. Agents correctly reach for `reflect` at session boundaries, but also incorrectly reach for it whenever they want to store something "important" (since LTM sounds like "important memories"). The fix: `reflect` must explicitly exclude non-retrospective content ("Don't use it for general notes or decisions"), and `remember` must not imply it is only for unimportant/temporary content.

**Required phrase in `reflect`:** "Don't use it for general notes or decisions; use `remember` for those"
**Required phrase in `remember`:** No lifecycle qualifier — must not say "temporary" or imply STM is lesser

---

## Section 4: Hardest Tools to Disambiguate (Ranked)

### Rank 1: `recall` vs `cross_search`

This is the hardest pair because the user intent — "find memories" — is identical, and the difference (one namespace vs. many) is not a property of the goal but of how much context the agent has. When an agent is at the start of a session and doesn't know where relevant memories live, both tools look equally valid. The current descriptions compound this by not naming each other at all. The disambiguating phrase in both descriptions must encode the decision rule *as a namespace-count check*, not as a qualitative difference in sophistication or power.

**Why it is hardest:** The namespace-count distinction is *invisible in the user's intent* and must be inferred from what the agent already knows. No amount of description length helps unless the decision rule is stated as a crisp conditional: "if you have one namespace → recall; if you have multiple → cross_search."

### Rank 2: `remember` vs `store_memory`

This pair is nearly symmetric at the intent level ("save a memory") with the only difference being whether auto-embedding and dedup are desired. The problem is that an LLM agent doing code-level reasoning often wants *control*, which makes `store_memory` appear more professional or precise even when `remember` is correct. The fix requires `store_memory`'s description to enumerate its narrowly valid use-cases concretely (pre-computed vector, exact lifecycle state) so that any time those conditions don't apply, `remember` wins by elimination.

**Why it is hard:** "Low-level" and "raw vector control" are implementation vocabulary that don't map to observable user intent. The negative guidance must translate these into user-observable conditions.

### Rank 3: `dispatch_task` vs `recall` (no namespace)

When namespace is unknown, `recall` quietly broadcasts across all namespaces (strategy 3 in the implementation). `dispatch_task` does semantic routing to the best expert. Both return memories. The difference is precision vs. recall: `dispatch_task` returns a targeted expert hit; `recall` without a namespace returns a noisy ranked broadcast. This difference is hard to surface in a two-sentence description without using technical vocabulary. The proposed fix for `dispatch_task` — "when you don't know which namespace holds the answer" — works because it claims the "unknown namespace" intent as its own, implicitly leaving `recall` for the known-namespace case.

**Why it is hard:** The broadcast fallback in `recall` is undocumented in the description, so agents don't know it exists and can't weigh it against `dispatch_task`. The long-term fix is to remove or de-emphasize the broadcast fallback from `recall`'s description entirely, making `dispatch_task` the sole recommended path when namespace is unknown.

---

*End of audit. Proposed edits to source files are deferred to Phase 2.*
