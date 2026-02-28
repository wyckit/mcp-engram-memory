# MCP Vector Memory

An [MCP](https://modelcontextprotocol.io/) (Model Context Protocol) server that gives an LLM tools to store, search, and delete vector embeddings using cosine-similarity nearest-neighbor search, backed by an HNSW index with optional JSON file persistence.

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

### Persistence

By default, stored vectors are persisted to a JSON file at:

| OS      | Default path |
|---------|-------------|
| Linux   | `~/.local/share/mcp-vector-memory/index.json` |
| macOS   | `~/Library/Application Support/mcp-vector-memory/index.json` |
| Windows | `%LOCALAPPDATA%\mcp-vector-memory\index.json` |

Override with the `VECTOR_MEMORY_DATA_PATH` environment variable:

```json
{
  "mcpServers": {
    "vector-memory": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/src/McpVectorMemory"],
      "env": {
        "VECTOR_MEMORY_DATA_PATH": "/path/to/my/vectors.json"
      }
    }
  }
}
```

Set `VECTOR_MEMORY_DATA_PATH=none` to disable persistence and run fully in-memory.

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
Program.cs              → Host setup, DI, MCP server wiring, persistence config
VectorEntry.cs          → Immutable vector record (id, vector, text, metadata)
VectorIndex.cs          → Thread-safe index: HNSW search + file persistence
HnswGraph.cs            → HNSW approximate nearest-neighbor graph
VectorMath.cs           → SIMD-accelerated dot product, norm, cosine similarity
IndexPersistence.cs     → JSON serialization for durable storage
SearchResult.cs         → Search result DTO (entry + cosine similarity score)
VectorMemoryTools.cs    → MCP tool definitions (store, search, delete)
```

- **HNSW index** — Search uses a Hierarchical Navigable Small World graph (O(log n) per query) instead of brute-force linear scan. Separate graphs are maintained per vector dimension.
- **Persistence** — Entries are saved to a JSON file after every mutation (upsert/delete). The HNSW graph is rebuilt from persisted entries on startup.
- **Thread safety** — `VectorIndex` uses `ReaderWriterLockSlim` for concurrent reads and exclusive writes.
- **SIMD acceleration** — Dot-product computation uses `System.Numerics.Vector<T>` when hardware acceleration is available.

## HNSW Parameters

The HNSW index uses sensible defaults that work well for most workloads:

| Parameter | Default | Description |
|-----------|---------|-------------|
| M | 16 | Max bi-directional connections per node per layer |
| efConstruction | 200 | Search effort during index construction (higher = better recall, slower insert) |
| efSearch | 50 | Minimum search effort at query time (higher = better recall, slower search) |

These can be tuned via the `VectorIndex` constructor for advanced use cases.

## Limitations

- **No dimension enforcement** — Vectors of different dimensions can coexist; each dimension gets its own HNSW graph. Queries only match entries with the same dimension.
- **Approximate search** — HNSW is not exact; for very small datasets it may occasionally miss a result. Increasing `efSearch` improves recall at the cost of speed.
- **Startup cost** — The HNSW graph is rebuilt from the persisted entries on each process start. For very large datasets (>100k vectors), this adds startup latency.

## License

MIT
