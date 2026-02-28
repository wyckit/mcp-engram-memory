# MCP Vector Memory

An [MCP](https://modelcontextprotocol.io/) (Model Context Protocol) server that gives an LLM tools to store, search, and delete vector embeddings in-memory using cosine-similarity k-nearest-neighbor search.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Quick Start

```bash
# build
dotnet build

# run tests
dotnet test

# run the server (stdio transport)
dotnet run --project src/McpVectorMemory
```

## MCP Client Configuration

Add the server to your MCP client (e.g. Claude Desktop, VS Code, etc.):

```json
{
  "mcpServers": {
    "vector-memory": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/src/McpVectorMemory"]
    }
  }
}
```

## Tools

### `store_memory`

Store a vector embedding together with its text and optional metadata. If an entry with the same `id` already exists it is replaced.

| Parameter  | Type                | Required | Description |
|------------|---------------------|----------|-------------|
| `id`       | `string`            | yes      | Unique identifier for the entry |
| `vector`   | `float[]`           | yes      | The embedding vector as an array of numbers |
| `text`     | `string`            | no       | The original text the vector was derived from |
| `metadata` | `object<string,string>` | no  | Arbitrary key-value metadata |

### `search_memory`

Find the most similar stored memories for a query vector using cosine similarity.

| Parameter  | Type     | Required | Default | Description |
|------------|----------|----------|---------|-------------|
| `vector`   | `float[]`| yes      | —       | The query embedding vector |
| `k`        | `int`    | no       | `5`     | Maximum number of results |
| `minScore` | `float`  | no       | `0`     | Minimum cosine-similarity threshold (-1 to 1) |

Returns an array of results, each containing the matched entry (`id`, `text`, `metadata`) and its `score`.

### `delete_memory`

Delete a stored memory entry by its unique identifier.

| Parameter | Type     | Required | Description |
|-----------|----------|----------|-------------|
| `id`      | `string` | yes      | The identifier of the entry to delete |

## Architecture

```
Program.cs              → Host setup, DI, MCP server wiring (stdio transport)
VectorEntry.cs          → Immutable vector record (id, vector, text, metadata)
VectorIndex.cs          → Thread-safe in-memory index (upsert, delete, search)
SearchResult.cs         → Search result DTO (entry + cosine similarity score)
VectorMemoryTools.cs    → MCP tool definitions (store, search, delete)
```

- **Thread safety** — `VectorIndex` uses `ReaderWriterLockSlim` for concurrent reads and exclusive writes.
- **SIMD acceleration** — Dot-product computation uses `System.Numerics.Vector<T>` when hardware acceleration is available.
- **Norm caching** — Entry norms are computed once at upsert time and reused across searches.

## Limitations

- **Ephemeral storage** — All data lives in-memory and is lost when the process exits.
- **Linear scan** — Search is O(n) per query. For large vector counts (>10k), consider an approximate nearest-neighbor index such as HNSW.
- **No dimension enforcement** — Vectors of different dimensions can coexist; mismatched entries are silently skipped during search.

## License

MIT
