# Engram Memory Cheat Sheet

[< Back to README](../README.md)

Think of it this way: **namespaces = folders, memories = documents, search = smart grep.**

## Core Tools

| Tool | What it does | Example use |
|------|-------------|-------------|
| `remember` | Intelligent store: auto-dedup, auto-link | After fixing a bug |
| `recall` | Intelligent search: auto-routes to best namespace | Start of every task |
| `store_memory` | Low-level vector store with full control | When you need specific ID/metadata |
| `store_batch` | Bulk-store multiple entries in one write-lock | Batch imports |
| `search_memory` | Semantic + keyword search in one namespace | Focused namespace search |
| `cross_search` | Search multiple namespaces at once, RRF-merged | Cross-project recall |
| `dispatch_task` | Describe a problem → system finds the right expert | Unknown domain routing |
| `deep_recall` | Search including archived memories; auto-resurrects | Finding forgotten context |

## Which Tool Should I Use?

| If you want to... | Use this tool |
|-------------------|---------------|
| Save what you just learned | `remember` (or `store_memory` for full control) |
| Find context before starting a task | `recall` (or `search_memory` with `hybrid: true`) |
| Search across multiple projects | `cross_search` |
| Find related memories you didn't know existed | `search_memory` with `expandGraph: true` |
| Route a question to the best knowledge domain | `dispatch_task` |
| Recover something you archived months ago | `deep_recall` |
| Check if a memory already exists | `detect_duplicates` |
| Understand conflicting stored knowledge | `find_contradictions` |

## Namespace Conventions

| Namespace | Contents |
|-----------|---------|
| Project directory name (e.g., `my-app`) | Project-specific decisions, bugs, patterns |
| `work` | Cross-project workflow preferences and tooling |
| `synthesis` | Cross-project architectural insights |
| `expert_{id}` | Expert routing namespaces (auto-created by `create_expert`) |

## Categories

`decision` · `pattern` · `bug-fix` · `architecture` · `preference` · `lesson` · `reference` · `retrospective`

## Search Parameters

| Parameter | When to use |
|-----------|-------------|
| `hybrid: true` | Almost always — fuses BM25 keyword matching with vector similarity |
| `expandGraph: true` | When you need related context pulled in automatically |
| `rerank: true` | Precision-critical searches — token-overlap reranking on top of hybrid |
| `diversity: true` | Dense namespaces — cluster-aware MMR spreads results across sub-topics |
| `diversityLambda: 0.5` | Trade-off: 1.0 = pure relevance, 0.0 = pure diversity (default 0.5) |
| `summaryFirst: true` | Large namespaces — searches cluster summaries first, then drills in |

## Lifecycle

```
STM (new) → promote_memory → LTM (stable) → decay_cycle → Archived → deep_recall resurrects
```

Promote a memory to LTM when you've recalled it in 2+ sessions or it captures a stable pattern.

## Tool Profiles

Set `MEMORY_TOOL_PROFILE` env var to control how many tools are exposed to the AI:

| Profile | Tools | What's included |
|---------|-------|----------------|
| `minimal` | 16 | Core CRUD, admin, composite tools, multi-agent sharing |
| `standard` | 35 | Adds graph, lifecycle, clustering, intelligence tools |
| `full` | 55 | Everything: expert routing, debate, synthesis, benchmarks (default) |

Use `minimal` or `standard` to reduce context window pressure on smaller models.

