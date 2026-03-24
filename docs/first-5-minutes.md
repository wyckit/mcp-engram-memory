# Your First 5 Minutes with Engram Memory

[< Back to README](../README.md)

## What You'll Do

Store a memory in one session, close it, open a new session, and recall it with different wording — that's the whole loop.

## 1. Install

```bash
git clone https://github.com/wyckit/mcp-engram-memory.git
cd mcp-engram-memory
dotnet build
```

Add to your MCP client config:

```json
{
  "mcpServers": {
    "engram-memory": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/mcp-engram-memory/src/McpEngramMemory"]
    }
  }
}
```

> **Note:** First build downloads a ~50MB embedding model — subsequent builds are instant.

## 2. Store Your First Memory

Paste into your AI assistant (replace the bracketed parts):

```
Store a memory about switching from JSON to SQLite for persistence because JSON
files caused write contention under parallel load in the my-app namespace with
category decision.
```

The AI calls `store_memory` and returns an ID. That memory is now persisted.

## 3. Close and Reopen

Close your current session entirely and start a fresh one. The AI has no memory of what you stored — that's the point.

## 4. Recall It

Paste into the new session:

```
Search engram memory for database persistence in the my-app namespace using hybrid search.
```

The AI will find what you stored even if your wording doesn't exactly match — semantic search understands meaning, not just keywords.

## 5. You're Done!

This is the core loop: **store → close → recall**. Everything else (expert routing, lifecycle promotion, clustering, graph links) builds on top of this.

See [docs/mcp-tools-reference.md](mcp-tools-reference.md) for the full tool list.

## What's Next?

- Add `hybrid: true` to search prompts for better keyword + semantic fusion
- Use `cross_search` to search multiple namespaces at once
- Try `dispatch_task` for expert routing — describe a problem and let the system find the right knowledge domain
- See [docs/prompts.md](prompts.md) for power prompts you can copy-paste directly
