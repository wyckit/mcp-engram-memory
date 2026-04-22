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

> **Note:** First build downloads a ~5.7 MB embedding model (bge-micro-v2) — subsequent builds are instant.

## 2. Store Your First Memory

Paste into your AI assistant (replace the bracketed parts):

```
Use remember to store that we switched from JSON to SQLite for persistence
because JSON files caused write contention under parallel load. id:
sqlite-switch-rationale, namespace: my-app, category: decision.
```

The AI calls the `remember` tool, which:
- Embeds your text (local ONNX, no network)
- Blocks near-duplicates (> 0.95 cosine similarity)
- Auto-links to related memories (> 0.65 cosine similarity)
- Returns an acknowledgement with the ID

That memory is now persisted to disk.

## 3. Close and Reopen

Close your current session entirely and start a fresh one. The AI has no memory of what you stored — that's the point.

## 4. Recall It

Paste into the new session:

```
Use recall to find what we decided about database persistence in my-app.
```

The AI calls `recall`, which searches with hybrid BM25 + vector similarity,
expands along graph neighbors, and falls back to deep_recall for archived
entries. Semantic search understands meaning, not just keywords — your
wording doesn't have to match what you stored.

## 5. You're Done

That's the core loop: **remember → close → recall**. Everything else
(expert routing, lifecycle promotion, clustering, multi-agent sharing)
builds on top of this.

## What's Next?

- **`reflect`** — at the end of a work session, call `reflect` with a short
  retrospective. It auto-stores as LTM (long-term memory), auto-links to
  related entries, and surfaces past reflections on the same topic.
- **`dispatch_task`** — describe a problem without specifying a namespace;
  the system picks the expert whose domain matches.
- **`cross_search`** — search across multiple namespaces in one call with
  RRF-merged results. Handy when a question spans several projects.
- **Multi-agent sharing** — set `AGENT_ID=my-agent` as an env var on the
  server process, grant access via `share_namespace`, inspect with
  `whoami` / `list_shared`. See [docs/multi-agent.md](multi-agent.md).

## Reference

- **[docs/core-10.md](core-10.md)** — the 10 tools that handle 80% of
  common tasks, with usage examples and when to reach for each.
- **[docs/mcp-tools-reference.md](mcp-tools-reference.md)** — full
  reference for every tool.
- **[docs/prompts.md](prompts.md)** — power prompts you can copy-paste.
