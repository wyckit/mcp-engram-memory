# Sample Prompts

[< Back to README](../README.md)

Once the memory system is configured with your AI assistant, these sample prompts demonstrate how to leverage the full tool suite effectively.

## Quick Reference Prompts

**Search before you work** — recall context at the start of any task:
```
Search engram memory for anything related to [topic] in the [project] namespace.
Use hybrid search with graph expansion to pull in connected knowledge.
```

**Store what you learn** — after completing a task or fixing a bug:
```
Store a memory about [what you did and why] in the [project] namespace.
Category: [decision|pattern|bug-fix|architecture|lesson]. Link it to related memories.
```

**Check system health** — quick status overview:
```
Run cognitive_stats, get_metrics, and compression_stats to show me the current
state of our engram memory system.
```

**Run benchmarks** — verify IR quality hasn't regressed:
```
Run all 13 benchmark datasets and compare results against our stored baselines in benchmarks/.
```

## Power Prompts

**Strategic planning with expert panel:**
```
Utilizing our engram memory and knowledge of the current state of our project,
using our panel of experts, what do you think we should focus on next?
```

This triggers: `search_memory`, `consult_expert_panel`, `dispatch_task`, and synthesizes cross-domain perspectives into prioritized recommendations.

**Full autonomous engineering session:**
```
Go through P1, P2, P3, P4. Research to see if we need to upsert any experts
with relevant subject area information. If our memories are getting too big,
take some agents to prune and reorganize. Use up to 20 agents to accomplish
these priority tasks. Use the engram panel of experts to resolve questions.
Make sure everything builds and tests pass.
```

This exercises the full tool suite: expert management, memory maintenance, parallel execution, quality gates, and build validation.

## Prompt Patterns

| Pattern | What it does | Key tools |
|---------|-------------|-----------|
| "Search memory for X" | Direct recall | `search_memory` |
| "What do experts think about X" | Multi-perspective analysis | `consult_expert_panel`, `map_debate_graph`, `resolve_debate` |
| "Route this question to the right expert" | Semantic routing | `dispatch_task`, `create_expert` |
| "Clean up / prune memories in X namespace" | Memory maintenance | `detect_duplicates`, `merge_memories`, `trigger_accretion_scan` |
| "Store what we just learned" | Knowledge capture | `store_memory`, `link_memories`, `promote_memory` |
| "Run benchmarks and compare to baseline" | Quality validation | `run_benchmark`, `get_metrics` |
| "Deep search including archived" | Full recall | `deep_recall` |
