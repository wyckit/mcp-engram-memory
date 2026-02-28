# MCP Vector Memory

An MCP (Model Context Protocol) server that provides an LLM with tools to store, search, and delete vector embeddings using in-memory k-nearest-neighbor search with cosine similarity.

## Tools

| Tool | Description |
|------|-------------|
| `store_memory` | Store a vector embedding with text and optional metadata |
| `search_memory` | Find the most similar stored memories for a query vector |
| `delete_memory` | Remove a stored memory entry by its identifier |

## Usage

Configure the MCP server in your client:

```json
{
  "mcpServers": {
    "vector-memory": {
      "command": "dotnet",
      "args": ["run", "--project", "src/McpVectorMemory"]
    }
  }
}
```

## Build & Test

```bash
dotnet build
dotnet test
```

## Limitations

- Storage is in-memory and ephemeral — all data is lost when the process exits.
- Search uses brute-force linear scan (O(n) per query). For large vector counts, consider an approximate nearest-neighbor index such as HNSW.
