# AI Assistant Setup

[< Back to README](../README.md)

Detailed setup instructions for each AI assistant tool. For quick start, use the reference harness files in [`examples/`](../examples/).

## Claude Code

**Quick start**: Copy [`examples/CLAUDE.md`](../examples/CLAUDE.md) to `~/.claude/CLAUDE.md` and [`examples/claude-code.json`](../examples/claude-code.json) to your MCP config.

**Or** open Claude Code in your project directory and paste:

```
Set up mcp-engram-memory as my persistent memory system. Do the following:

1. Add the MCP server to my Claude Code config. The server runs via:
   command: dotnet
   args: run --project /path/to/mcp-engram-memory/src/McpEngramMemory

2. Create or update my CLAUDE.md (global at ~/.claude/CLAUDE.md) with these sections:

   ## Model Routing
   Route sub-agents by purpose to maximize your subscription:
   - Main thread (Opus): Coding, architecture, reasoning, expert creation, retrospectives
   - Memory sub-agents (model: "sonnet"): All engram MCP tool calls — search, store,
     dispatch_task, deep_recall, link, merge, detect_duplicates, etc.
   - Utility sub-agents (model: "haiku"): Explore agents, codebase searches, file
     reading/grepping, research lookups — anything that doesn't need engram tools or
     complex reasoning
   Rules: engram operations → Sonnet, codebase exploration/research → Haiku,
   consult_expert_panel and create_expert may stay in the main Opus thread.

   ## Recall: Search Before You Work
   - At conversation start, search vector memory using up to 3 parallel agents
     with model: "sonnet":
     Agent 1: cross_search across [project_namespace, "work", "synthesis"] with
       hybrid: true — combines multi-namespace search into a single RRF-merged call
     Agent 2: search_memory in the project namespace with alternative phrasings/keywords
       (use hybrid: true and expandGraph: true for keyword+vector fusion and graph neighbors)
     Agent 3: dispatch_task with a description of the current task to find the best
       expert namespace (use hierarchical: true if domain tree is populated)
   - For graph-connected knowledge, use expandGraph: true to pull in linked memories
   - Tool selection: cross_search for broad context, search_memory for focused lookups,
     dispatch_task for cross-domain questions, consult_expert_panel for multi-perspective
     analysis, deep_recall for archived knowledge

   ## Store: Save What You Learn
   - Store memories after completing tasks, fixing bugs, learning patterns, or receiving
     corrections. Use the project directory name as namespace, kebab-case IDs, include
     domain keywords in text for searchability, and categorize as one of: decision, pattern,
     bug-fix, architecture, preference, lesson, reference
   - All stores go through model: "sonnet" sub-agents — compose fields in main thread,
     hand off the store_memory call to Sonnet

   ## Expert Routing
   - Use dispatch_task via model: "sonnet" sub-agent for open-ended questions.
     If it returns needs_expert, call create_expert with a detailed persona in the main
     Opus thread, then seed that expert's namespace

   ## Multi-Agent Sharing
   - Set AGENT_ID env var per agent instance to enable namespace ownership and permissions
   - Use cross_search to search across multiple namespaces in one call (RRF merge)
   - Use share_namespace / unshare_namespace to grant/revoke read or write access

   ## Session Retrospective
   - At the end of significant sessions, self-evaluate: what went well, what went wrong
   - Store retrospective via model: "sonnet" sub-agent with: id "retro-YYYY-MM-DD-topic",
     category "lesson"

Confirm each file you create and show me the final contents.
```

## GitHub Copilot

**Quick start**: Copy [`examples/copilot-instructions.md`](../examples/copilot-instructions.md) to `.github/copilot-instructions.md` and [`examples/vscode-copilot.json`](../examples/vscode-copilot.json) to `.vscode/mcp.json`.

**Or** open VS Code with Copilot and paste in chat:

```
Set up mcp-engram-memory as my persistent memory system. Do the following:

1. Create .vscode/mcp.json with a stdio server entry:
   name: engram-memory
   command: dotnet
   args: ["run", "--project", "/path/to/mcp-engram-memory/src/McpEngramMemory"]

2. Create .github/copilot-instructions.md with vector memory instructions:

   ## Recall
   - Before starting any task, use cross_search across [project_namespace, "work",
     "synthesis"] with hybrid: true to recall context.
   - Tool selection: search_memory for project context, dispatch_task for cross-domain
     questions, consult_expert_panel for multiple perspectives, deep_recall for archived knowledge.

   ## Store
   - Store memories after completing tasks, fixing bugs, learning patterns, or receiving
     corrections. Use project directory name as namespace, kebab-case IDs, categorize as:
     decision, pattern, bug-fix, architecture, preference, lesson, reference.

   ## Expert Routing
   - dispatch_task routes to experts automatically. If needs_expert is returned, use
     create_expert with a detailed persona description.

Confirm each file you create and show me the final contents.
```

## Google Gemini CLI

**Quick start**: Copy [`GEMINI.md`](../GEMINI.md) to your workspace root.

**Or** open Gemini CLI and paste in chat:

```
Set up mcp-engram-memory as my persistent memory system. Do the following:

1. Add the MCP server to my Gemini CLI config (edit ~/.gemini/settings.json):
   name: engram-memory
   command: dotnet
   args: ["run", "--project", "/path/to/mcp-engram-memory/src/McpEngramMemory"]

2. Create GEMINI.md in my workspace root with vector memory instructions
   following the Recall / Store / Expert Routing pattern.

Confirm each file you create and show me the final contents.
```

## OpenAI Codex

**Quick start**: Copy [`examples/AGENTS.md`](../examples/AGENTS.md) to your project root.

**Or** open Codex CLI in your project directory and paste:

```
Set up mcp-engram-memory as my persistent memory system. Do the following:

1. Add the MCP server to my Codex config:
   codex mcp add engram-memory -- dotnet run --project /path/to/mcp-engram-memory/src/McpEngramMemory

2. Create AGENTS.md in the project root with vector memory instructions
   following the Recall / Store / Expert Routing pattern.

Confirm each file you create and show me the final contents.
```
