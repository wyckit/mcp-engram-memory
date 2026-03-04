# MCP Vector Memory

A cognitive memory MCP server that provides an LLM with namespace-isolated vector storage, k-nearest-neighbor search (cosine similarity), a knowledge graph, semantic clustering, lifecycle management with activation energy decay, and physics-based re-ranking. Data is persisted to disk as JSON with debounced writes.

## Project Structure

The solution is split into two projects:

| Project | Type | Description |
|---------|------|-------------|
| `McpVectorMemory` | Executable | MCP server with stdio transport — register this in your MCP client |
| `McpVectorMemory.Core` | NuGet Library | Core engine (vector index, graph, clustering, lifecycle, persistence) — use this to embed the memory engine in your own application |

```
src/
  McpVectorMemory/           # MCP server (Program.cs + Tool classes)
  McpVectorMemory.Core/      # Core library (Models + Services)
tests/
  McpVectorMemory.Tests/     # xUnit tests
```

## NuGet Package

The core engine is available as a NuGet package for use in your own .NET applications.

```bash
dotnet add package McpVectorMemory.Core --version 0.1.0
```

### Library Usage

```csharp
using McpVectorMemory.Core.Services;

// Create services
var persistence = new PersistenceManager();
var embedding = new LocalEmbeddingService();
var cognitiveIndex = new CognitiveIndex(persistence, embedding);
var knowledgeGraph = new KnowledgeGraph(persistence);
var clusterManager = new ClusterManager(persistence, cognitiveIndex);
var lifecycleEngine = new LifecycleEngine(cognitiveIndex, persistence);

// Store and search memories
cognitiveIndex.Store("default", "The capital of France is Paris", "facts");
var results = cognitiveIndex.Search("default", "French capital", topK: 5);
```

## Tech Stack

- .NET 8, C#
- [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) 1.0.0
- [SmartComponents.LocalEmbeddings](https://www.nuget.org/packages/SmartComponents.LocalEmbeddings) (384-dimensional vectors)
- Microsoft.Extensions.Hosting 8.0.1
- xUnit (tests)

## MCP Tools (21 total)

### Core Memory (3 tools)

| Tool | Description |
|------|-------------|
| `store_memory` | Store a vector embedding with text, category, and optional metadata. Defaults to STM lifecycle state. |
| `search_memory` | k-NN search within a namespace with optional lifecycle/category filtering, summary-first mode, and physics-based re-ranking. |
| `delete_memory` | Remove a memory entry by ID. Cascades to remove associated graph edges and cluster memberships. |

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

### Lifecycle Management (3 tools)

| Tool | Description |
|------|-------------|
| `promote_memory` | Manually transition a memory between lifecycle states (`stm`, `ltm`, `archived`). |
| `deep_recall` | Search across ALL lifecycle states. Auto-resurrects high-scoring archived entries above the resurrection threshold. |
| `decay_cycle` | Trigger activation energy recomputation and state transitions for a namespace. |

Activation energy formula: `(accessCount x reinforcementWeight) - (hoursSinceLastAccess x decayRate)`

### Admin (2 tools)

| Tool | Description |
|------|-------------|
| `get_memory` | Retrieve full cognitive context for an entry (lifecycle, edges, clusters). Does not count as an access. |
| `cognitive_stats` | System overview: entry counts by state, cluster count, edge count, and namespace list. |

### Accretion (4 tools)

| Tool | Description |
|------|-------------|
| `get_pending_collapses` | List dense LTM clusters detected by the background scanner that are awaiting LLM summarization. |
| `collapse_cluster` | Execute a pending collapse: store a summary entry, archive the source members, and create a cluster. |
| `dismiss_collapse` | Dismiss a detected collapse and exclude its members from future scans. |
| `trigger_accretion_scan` | Manually run a DBSCAN density scan on LTM entries in a namespace. |

`collapse_cluster` reliability behavior:
- If collapse steps complete successfully, the pending collapse is removed.
- If summary storage or any member archival step fails, the tool returns an error and preserves the pending collapse so the same `collapseId` can be retried.

## Architecture

### Services

| Service | Description |
|---------|-------------|
| `CognitiveIndex` | Thread-safe namespace-partitioned vector index with k-NN search, lifecycle filtering, and access tracking |
| `KnowledgeGraph` | In-memory directed graph with adjacency lists and bidirectional edge support |
| `ClusterManager` | Semantic cluster CRUD with automatic centroid computation |
| `LifecycleEngine` | Activation energy computation, decay cycles, and state transitions (STM/LTM/archived) |
| `PhysicsEngine` | Gravitational force re-ranking with "Asteroid" (semantic) + "Sun" (importance) output |
| `AccretionScanner` | DBSCAN-based density scanning of LTM entries for cluster detection |
| `PersistenceManager` | JSON file-based persistence with debounced async writes (default 500ms) |
| `LocalEmbeddingService` | 384-dimensional vector embeddings via SmartComponents.LocalEmbeddings |

### Background Services

| Service | Interval | Description |
|---------|----------|-------------|
| `EmbeddingWarmupService` | Startup | Warms up the embedding model on server start so first queries are fast |
| `DecayBackgroundService` | 15 minutes | Runs activation energy decay on all namespaces |
| `AccretionBackgroundService` | 30 minutes | Scans all namespaces for dense LTM clusters needing summarization |

### Persistence

Data is stored as JSON files in a `data/` directory:
- `{namespace}.json` — entries (per namespace)
- `_edges.json` — graph edges (global)
- `_clusters.json` — semantic clusters (global)

Writes are debounced (500ms default) to avoid excessive disk I/O.

## Usage

### MCP Server

Configure the MCP server in your client (e.g. Claude Desktop, VS Code):

```json
{
  "mcpServers": {
    "vector-memory": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/mcp-vector-memory/src/McpVectorMemory"]
    }
  }
}
```

## Build & Test

```bash
cd mcp-vector-memory
dotnet build
dotnet test
```

### Tests

13 test files with 170 test cases covering:

| Test File | Tests | Focus |
|-----------|-------|-------|
| `CognitiveIndexTests.cs` | 39 | Vector search, lifecycle filtering, persistence |
| `CoreMemoryToolsTests.cs` | 20 | Store, search, delete memory tool endpoints |
| `PhysicsEngineTests.cs` | 19 | Mass computation, gravitational force, slingshot |
| `AccretionScannerTests.cs` | 18 | DBSCAN clustering, pending collapses |
| `KnowledgeGraphTests.cs` | 17 | Edge operations, graph traversal |
| `ClusterManagerTests.cs` | 14 | Cluster CRUD and centroid operations |
| `LifecycleEngineTests.cs` | 12 | State transitions, deep recall, decay cycles |
| `PersistenceManagerTests.cs` | 9 | JSON serialization, debounced saves |
| `RegressionTests.cs` | 9 | Integration and edge-case scenarios |
| `AccretionToolsTests.cs` | 7 | Accretion tool functionality |
| `DecayBackgroundServiceTests.cs` | 2 | Background service decay cycles |
| `AccretionBackgroundServiceTests.cs` | 2 | Background service lifecycle |
| `EmbeddingWarmupServiceTests.cs` | 2 | Embedding warmup startup behavior |
