[< Back to README](../README.md)

# MCP Tools Reference (50 tools)

### Core Memory (4 tools)

| Tool | Description |
|------|-------------|
| `store_memory` | Store a vector embedding with text, category, and optional metadata. Defaults to STM lifecycle state. Warns if near-duplicates are detected. |
| `store_batch` | Bulk-store multiple entries in a single write-lock. Faster than repeated `store_memory` calls for batch imports. Each entry gets contextual prefix embedding. Returns stored count and duplicate warnings. |
| `search_memory` | k-NN search within a namespace with optional hybrid mode (BM25+vector fusion with synonym expansion, cascade retrieval, BM25 semantic gate, and auto-PRF), lifecycle/category filtering, summary-first mode, physics-based re-ranking, and `explain` mode for full retrieval diagnostics. |
| `delete_memory` | Remove a memory entry by ID. Cascades to remove associated graph edges and cluster memberships. |

### Composite Tools (3 tools)

| Tool | Description |
|------|-------------|
| `remember` | Intelligent store: saves a memory with auto-generated embedding, duplicate detection, and auto-linking to related existing memories. Use instead of store_memory + detect_duplicates + link_memories. |
| `recall` | Intelligent search: searches with auto-routing to the best namespace via expert dispatch, with fallback to direct search. Combines search_memory + dispatch_task in one call. |
| `reflect` | Store a lesson or retrospective with auto-linking to related memories. Wraps store_memory + link_memories for end-of-session knowledge capture. |

### Knowledge Graph (4 tools)

| Tool | Description |
|------|-------------|
| `link_memories` | Create a directed edge between two entries with a relation type and weight. `cross_reference` auto-creates bidirectional edges. |
| `unlink_memories` | Remove edges between entries, optionally filtered by relation type. |
| `get_neighbors` | Get directly connected entries with edges. Supports direction filtering (outgoing/incoming/both). |
| `traverse_graph` | Multi-hop BFS traversal with configurable depth (max 5), relation filter, minimum weight, and max results. |

Supported relation types: `parent_child`, `cross_reference`, `similar_to`, `contradicts`, `elaborates`, `depends_on`, `custom`.

### Semantic Clustering (5 tools)

| Tool | Description |
|------|-------------|
| `create_cluster` | Create a named cluster from member entry IDs. Centroid is computed automatically. |
| `update_cluster` | Add/remove members and update the label. Centroid is recomputed. |
| `store_cluster_summary` | Store an LLM-generated summary as a searchable entry linked to the cluster. |
| `get_cluster` | Retrieve full cluster details including members and summary info. |
| `list_clusters` | List all clusters in a namespace with summary status. |

### Lifecycle Management (5 tools)

| Tool | Description |
|------|-------------|
| `promote_memory` | Manually transition a memory between lifecycle states (`stm`, `ltm`, `archived`). |
| `memory_feedback` | Provide agent feedback on a memory's usefulness. Positive feedback boosts activation energy and records an access; negative feedback suppresses it. Triggers state transitions when thresholds are crossed. Closes the agent reinforcement loop. |
| `deep_recall` | Search across ALL lifecycle states. Auto-resurrects high-scoring archived entries above the resurrection threshold. |
| `decay_cycle` | Trigger activation energy recomputation and state transitions for a namespace. |
| `configure_decay` | Set per-namespace decay parameters (decayRate, reinforcementWeight, stmThreshold, archiveThreshold). Used by background service and `decay_cycle` with `useStoredConfig=true`. |

Activation energy formula: `(accessCount x reinforcementWeight) - (hoursSinceLastAccess x decayRate)`

### Admin (3 tools)

| Tool | Description |
|------|-------------|
| `get_memory` | Retrieve full cognitive context for an entry (lifecycle, edges, clusters). Does not count as an access. |
| `cognitive_stats` | System overview: entry counts by state, cluster count, edge count, namespace list, and HNSW index status. |
| `purge_debates` | Delete stale `active-debate-*` namespaces older than a configurable age (default: 24 hours). Supports dry-run mode. |

### Accretion (4 tools)

| Tool | Description |
|------|-------------|
| `get_pending_collapses` | List dense LTM clusters detected by the background scanner that are awaiting LLM summarization. |
| `collapse_cluster` | Execute a pending collapse: store a summary entry, archive the source members, and create a cluster. |
| `dismiss_collapse` | Dismiss a detected collapse and exclude its members from future scans. |
| `trigger_accretion_scan` | Manually run a DBSCAN density scan on LTM entries in a namespace. |

`collapse_cluster` reliability behavior:
- If collapse steps complete successfully, the pending collapse is removed and a reversal record is persisted to disk.
- If summary storage or any member archival step fails, the tool returns an error and preserves the pending collapse so the same `collapseId` can be retried.
- Collapse records survive server restarts and can be reversed with `uncollapse_cluster`.

### Intelligence & Safety (5 tools)

| Tool | Description |
|------|-------------|
| `detect_duplicates` | Find near-duplicate entries in a namespace by pairwise cosine similarity above a configurable threshold. |
| `find_contradictions` | Surface contradictions: entries linked with `contradicts` graph edges, plus high-similarity topic-relevant pairs for review. |
| `merge_memories` | Merge two duplicate entries: keeps the first entry's vector, combines metadata and access counts, transfers graph edges and cluster memberships, and archives the second entry. |
| `uncollapse_cluster` | Reverse a previously executed accretion collapse: restore archived members to pre-collapse state, delete summary, clean up cluster. |
| `list_collapse_history` | List all reversible collapse records for a namespace. |

### Panel of Experts / Debate (3 tools)

| Tool | Description |
|------|-------------|
| `consult_expert_panel` | Consult a panel of experts by running parallel searches across multiple expert namespaces. Stores each perspective in an active-debate namespace and returns integer-aliased results so the LLM can reference nodes without managing UUIDs. Replaces multiple `search_memory` + `store_memory` calls with a single macro-command. |
| `map_debate_graph` | Map logical relationships between debate nodes using integer aliases from `consult_expert_panel`. Translates aliases to UUIDs internally and batch-creates knowledge graph edges. Replaces multiple `link_memories` calls with a single macro-command. |
| `resolve_debate` | Resolve a debate by storing a consensus summary as LTM, linking it to the winning perspective, and batch-archiving all raw debate nodes. Cleans up session state. Replaces manual `store_memory` + `link_memories` + `promote_memory` calls with a single macro-command. |

Debate workflow: `consult_expert_panel` (gather perspectives) → `map_debate_graph` (define relationships) → `resolve_debate` (store consensus). Sessions use integer aliases (1, 2, 3...) so the LLM never handles UUIDs. Sessions auto-expire after 1 hour.

### Benchmarking & Observability (3 tools)

| Tool | Description |
|------|-------------|
| `run_benchmark` | Run an IR quality benchmark. 13 datasets including `default-v1`, `paraphrase-v1`, `multihop-v1`, `scale-v1`, `realworld-v1`, `compound-v1`, `ambiguity-v1`, `distractor-v1`, `specificity-v1`, `contamination-v1`, `cluster-summary-v1`. Computes Recall@K, Precision@K, MRR, nDCG@K, and latency percentiles. |
| `get_metrics` | Get operational metrics: latency percentiles (P50/P95/P99), throughput, and counts for search, store, and other operations. |
| `reset_metrics` | Reset collected operational metrics. Optionally filter by operation type. |

Thirteen benchmark datasets: six core datasets (default, paraphrase, multihop, scale, realworld, compound) plus seven stress-test datasets (ambiguity, distractor, specificity, scale extended, contamination, cluster-summary). All use a 0–3 relevance grade scale (3 = highly relevant). A 52-combination regression baseline (13 datasets x 4 modes) enforces minimum thresholds: Recall@K >= 0.20, MRR >= 0.20, nDCG@K >= 0.15.

### Maintenance (2 tools)

| Tool | Description |
|------|-------------|
| `rebuild_embeddings` | Re-embed all entries in one or all namespaces using the current embedding model. Use after upgrading the embedding model to regenerate vectors from stored text. Entries without text are skipped. Preserves all metadata, lifecycle state, and timestamps. |
| `compression_stats` | Show vector compression statistics for a namespace or all namespaces. Reports FP32 vs Int8 disk savings, quantization coverage, and memory footprint estimates. |

### Expert Routing (4 tools)

| Tool | Description |
|------|-------------|
| `dispatch_task` | Route a query to the most relevant expert namespace via semantic similarity. Supports flat comparison or hierarchical tree routing (`hierarchical=true`) through root → branch → leaf domain nodes. Returns expert profile and context, or `needs_expert` if no match. |
| `create_expert` | Instantiate a new expert namespace and register it in the semantic routing meta-index. Supports `level` parameter (`root`, `branch`, `leaf`) and `parentNodeId` for hierarchical domain tree construction. **Auto-classifies** leaf experts into the domain tree when `parentNodeId` is omitted — returns `auto_linked`, `suggested`, or `unclassified` placement. |
| `link_to_parent` | Link an existing leaf expert to a parent node (root or branch) in the domain tree. Use to manually adjust auto-classification placement or organize existing experts into the hierarchy. |
| `get_domain_tree` | Show the full hierarchical expert domain tree with root domains, branches, and leaf experts. Useful for understanding the routing topology. |

Expert routing workflow: `dispatch_task` (route query) → if miss: `create_expert` (define specialist) → `dispatch_task` (retry). The system maintains a hidden `_system_experts` meta-index that maps queries to specialized namespaces via cosine similarity (default threshold: 0.75). Experts within a 5% score margin of the top match are returned as candidates.

**Hierarchical routing (HMoE)**: Use `create_expert` with `level="root"` or `level="branch"` to build a domain tree. Set `hierarchical=true` on `dispatch_task` to enable coarse-to-fine tree walk: score roots → narrow to branches → select leaf experts. Falls back to flat routing if no tree exists. All routing uses local ONNX embeddings + SIMD dot products — zero LLM API calls.

**Auto-classification**: When creating a leaf expert without specifying `parentNodeId`, the system automatically scores the persona description against all root and branch nodes to find the best placement. Results: `auto_linked` (>= 0.82 confidence — automatically placed), `suggested` (0.60–0.82 — placed but flagged for review), or `unclassified` (< 0.60 — left as orphan). Use `link_to_parent` to manually adjust placement.

### Multi-Agent Sharing (5 tools)

| Tool | Description |
|------|-------------|
| `cross_search` | Search across multiple namespaces in a single call. Results are merged using Reciprocal Rank Fusion (RRF) and annotated with their source namespace. Supports hybrid search and reranking. |
| `share_namespace` | Grant another agent read or write access to a namespace you own. |
| `unshare_namespace` | Revoke an agent's access to a namespace you own. |
| `list_shared` | List all namespaces shared with the current agent, showing owner and access level. |
| `whoami` | Return the current agent identity and accessible namespaces summary. |

Multi-agent workflow: Set `AGENT_ID` environment variable per agent instance. Namespace ownership is established on first write. Use `share_namespace` to grant cross-agent access, `cross_search` to query across shared namespaces. The default agent (`AGENT_ID` not set) has unrestricted access for backward compatibility.
