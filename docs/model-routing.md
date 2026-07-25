# Model and Reasoning Routing

Last reviewed: 2026-07-12

Engram separates memory routing from model execution. The MCP server does not start an LLM when it routes a task to an expert or consults an expert panel:

- An **expert** is a persona plus an `expert_{id}` memory namespace.
- `dispatch_task` selects an expert namespace and retrieves relevant memories.
- `consult_expert_panel` retrieves perspectives from several expert namespaces.
- The **host agent** reads that context and performs the actual reasoning, synthesis, tool use, and final response.
- `synthesize_memories` is the exception: it can invoke the separately configured local synthesis backend. Its model is not the host agent model.

This boundary matters because changing an Engram expert does not change the model or reasoning effort. Model selection belongs to the client harness (Codex, Claude Code, Copilot, Gemini, or another MCP host).

## Canonical Work Tiers

Use the strongest model only where judgment changes the result. Retrieval itself is deterministic and does not benefit from expensive reasoning.

| Work tier | Examples | Model class | Reasoning effort |
|-----------|----------|-------------|------------------|
| Memory I/O | `recall`, `cross_search`, `store_memory`, graph lookup, namespace inspection | Fast tool-capable model | `low` |
| Routing and extraction | `dispatch_task`, selecting evidence, drafting a self-contained memory, dedup/contradiction triage | Fast capable model | `medium` |
| Expert synthesis | Creating an expert persona, combining panel evidence, architecture or implementation decisions | Frontier model | `high` |
| Adjudication | Irreconcilable expert conflict, security/safety decisions, high-impact cross-domain tradeoffs | Frontier model | `xhigh`, then return to `high` |

Do not use `xhigh` for ordinary recall, storage, or file exploration. Escalate one work unit at a time, and preserve the retrieved memory IDs so the final synthesis remains traceable.

## OpenAI Codex Mapping

The current OpenAI mapping is:

| Harness role | Model | Reasoning |
|--------------|-------|-----------|
| Main implementation and expert synthesis | `gpt-5.6-sol` | `high` |
| Deep expert adjudication, only when triggered | `gpt-5.6-sol` | `xhigh` |
| Memory routing, evidence extraction, and routine sub-agent work | `gpt-5.4-mini` | `medium` |
| Mechanical lookup and utility work | `gpt-5.4-mini` | `low` |

`gpt-5.6-sol` is the frontier choice for complex professional work. `gpt-5.4-mini` remains the cost-efficient model intended for sub-agent and focused tool work. Availability can differ by Codex account or surface, so preserve the tier semantics if a model slug is unavailable: use the strongest available frontier model for expert synthesis and the strongest available mini model for memory/utility work.

Codex applies `model` and `model_reasoning_effort` to a process/profile. An Engram MCP tool call cannot change them. Use the example profiles with separate Codex processes when explicit per-role enforcement is needed:

```powershell
codex -p engram-expert
codex -p engram-expert-deep
codex -p engram-memory
codex -p engram-utility
```

The corresponding files are in `examples/codex-engram-*.config.toml`. Install them in `$CODEX_HOME` (normally `~/.codex`) without the leading `codex-`; for example, copy `codex-engram-expert.config.toml` as `~/.codex/engram-expert.config.toml`. If the host cannot override the model per sub-agent, keep expert synthesis in the main frontier-model thread and use sub-agents only when their inherited model is appropriate.

## Expert and Agent Workflow

1. Recall broad project context with low reasoning.
2. Use `dispatch_task` with medium reasoning when the correct namespace is unknown.
3. Treat returned expert memories as evidence, not as a generated expert answer.
4. For a single-domain task, synthesize the evidence in the main thread at high reasoning only when judgment is required.
5. For a genuine cross-domain conflict, use `consult_expert_panel`, retain each perspective and memory ID, then adjudicate at high reasoning.
6. Escalate to `xhigh` only when the high-reasoning pass exposes a consequential unresolved conflict.
7. Store the resulting decision and rationale with provenance; do not store raw chain-of-thought or speculative conclusions.

Creating more experts does not create more compute. Spawn parallel agents only for independent work that benefits from concurrency; use Engram expert routing to find specialized knowledge. A panel can be useful without spawning one live agent per expert.

## Provider-Neutral Harness Rule

Other clients should map their current models to the same four work tiers instead of copying OpenAI model names. The durable policy is capability-based: fast/low for memory I/O, capable/medium for routing, frontier/high for synthesis, and frontier/deep only for exceptional adjudication.

Current OpenAI references:

- [OpenAI model selection](https://developers.openai.com/api/docs/models)
- [GPT-5.6 Sol](https://developers.openai.com/api/docs/models/gpt-5.6-sol)
- [GPT-5.4 mini](https://developers.openai.com/api/docs/models/gpt-5.4-mini)
- [Codex configuration reference](https://developers.openai.com/codex/config-reference)
