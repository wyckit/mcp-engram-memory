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
benchmarks/
  baseline-v1.json           # IR quality baseline (MRR 1.0, nDCG@5 0.938, Recall@5 0.867)
  baseline-paraphrase-v1.json
  baseline-multihop-v1.json
  baseline-scale-v1.json
  ideas/                     # Benchmark proposals and analysis
```

## NuGet Package

The core engine is available as a NuGet package for use in your own .NET applications.

```bash
dotnet add package McpVectorMemory.Core --version 0.2.0
```

### Library Usage

```csharp
using McpVectorMemory.Core.Models;
using McpVectorMemory.Core.Services;

// Create services
var persistence = new PersistenceManager();
var embedding = new OnnxEmbeddingService();
var index = new CognitiveIndex(persistence);
var graph = new KnowledgeGraph(persistence, index);
var clusters = new ClusterManager(index, persistence);
var lifecycle = new LifecycleEngine(index, persistence);

// Store a memory
var vector = embedding.Embed("The capital of France is Paris");
var entry = new CognitiveEntry("fact-1", vector, "default", "The capital of France is Paris", "facts");
index.Upsert(entry);

// Search by text
var queryVector = embedding.Embed("French capital");
var results = index.Search(queryVector, "default", k: 5);
```

## Tech Stack

- .NET 8, C#
- [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) 1.0.0
- [FastBertTokenizer](https://www.nuget.org/packages/FastBertTokenizer) 0.4.67 (WordPiece tokenization)
- [Microsoft.ML.OnnxRuntime](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime) 1.17.0 (ONNX model inference)
- [bge-micro-v2](https://huggingface.co/TaylorAI/bge-micro-v2) ONNX model (384-dimensional vectors, MIT license, downloaded at build time)
- Microsoft.Extensions.Hosting 8.0.1
- xUnit (tests)

## MCP Tools (29 total)

### Core Memory (3 tools)

| Tool | Description |
|------|-------------|
| `store_memory` | Store a vector embedding with text, category, and optional metadata. Defaults to STM lifecycle state. Warns if near-duplicates are detected. |
| `search_memory` | k-NN search within a namespace with optional lifecycle/category filtering, summary-first mode, physics-based re-ranking, and `explain` mode for full retrieval diagnostics. |
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

### Lifecycle Management (4 tools)

| Tool | Description |
|------|-------------|
| `promote_memory` | Manually transition a memory between lifecycle states (`stm`, `ltm`, `archived`). |
| `deep_recall` | Search across ALL lifecycle states. Auto-resurrects high-scoring archived entries above the resurrection threshold. |
| `decay_cycle` | Trigger activation energy recomputation and state transitions for a namespace. |
| `configure_decay` | Set per-namespace decay parameters (decayRate, reinforcementWeight, stmThreshold, archiveThreshold). Used by background service and `decay_cycle` with `useStoredConfig=true`. |

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
- If collapse steps complete successfully, the pending collapse is removed and a reversal record is persisted to disk.
- If summary storage or any member archival step fails, the tool returns an error and preserves the pending collapse so the same `collapseId` can be retried.
- Collapse records survive server restarts and can be reversed with `uncollapse_cluster`.

### Intelligence & Safety (4 tools)

| Tool | Description |
|------|-------------|
| `detect_duplicates` | Find near-duplicate entries in a namespace by pairwise cosine similarity above a configurable threshold. |
| `find_contradictions` | Surface contradictions: entries linked with `contradicts` graph edges, plus high-similarity topic-relevant pairs for review. |
| `uncollapse_cluster` | Reverse a previously executed accretion collapse: restore archived members to pre-collapse state, delete summary, clean up cluster. |
| `list_collapse_history` | List all reversible collapse records for a namespace. |

### Benchmarking & Observability (3 tools)

| Tool | Description |
|------|-------------|
| `run_benchmark` | Run an IR quality benchmark. Datasets: `default-v1` (25 seeds, 20 queries), `paraphrase-v1` (25 seeds, 15 queries), `multihop-v1` (25 seeds, 15 queries), `scale-v1` (80 seeds, 30 queries). Computes Recall@K, Precision@K, MRR, nDCG@K, and latency percentiles. |
| `get_metrics` | Get operational metrics: latency percentiles (P50/P95/P99), throughput, and counts for search, store, and other operations. |
| `reset_metrics` | Reset collected operational metrics. Optionally filter by operation type. |

Four benchmark datasets covering programming languages, data structures, ML, databases, networking, systems, security, and DevOps topics. Relevance grades use a 0–3 scale (3 = highly relevant).

## Architecture

### Services

| Service | Description |
|---------|-------------|
| `CognitiveIndex` | Thread-safe namespace-partitioned vector index with k-NN search, lifecycle filtering, duplicate detection, and access tracking |
| `KnowledgeGraph` | In-memory directed graph with adjacency lists, bidirectional edge support, and contradiction surfacing |
| `ClusterManager` | Semantic cluster CRUD with automatic centroid computation |
| `LifecycleEngine` | Activation energy computation, per-namespace decay configs, decay cycles, and state transitions (STM/LTM/archived) |
| `PhysicsEngine` | Gravitational force re-ranking with "Asteroid" (semantic) + "Sun" (importance) output |
| `AccretionScanner` | DBSCAN-based density scanning with reversible collapse history (persisted to disk) |
| `BenchmarkRunner` | IR quality benchmark execution with Recall@K, Precision@K, MRR, nDCG@K scoring |
| `MetricsCollector` | Thread-safe operational metrics with P50/P95/P99 latency percentiles |
| `IStorageProvider` | Storage abstraction interface for persistence backends |
| `PersistenceManager` | JSON file-based `IStorageProvider` implementation with debounced async writes (default 500ms) |
| `OnnxEmbeddingService` | 384-dimensional vector embeddings via bge-micro-v2 ONNX model with FastBertTokenizer |
| `HashEmbeddingService` | Deterministic hash-based embeddings for testing/CI (no model dependency) |

### Background Services

| Service | Interval | Description |
|---------|----------|-------------|
| `EmbeddingWarmupService` | Startup | Warms up the embedding model on server start so first queries are fast |
| `DecayBackgroundService` | 15 minutes | Runs activation energy decay on all namespaces using stored per-namespace configs |
| `AccretionBackgroundService` | 30 minutes | Scans all namespaces for dense LTM clusters needing summarization |

### Persistence

Data is stored as JSON files in a `data/` directory:
- `{namespace}.json` — entries (per namespace)
- `_edges.json` — graph edges (global)
- `_clusters.json` — semantic clusters (global)
- `_collapse_history.json` — reversible collapse records (global)
- `_decay_configs.json` — per-namespace decay configurations (global)

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

16 test files with 250 test cases covering:

| Test File | Tests | Focus |
|-----------|-------|-------|
| `CognitiveIndexTests.cs` | 39 | Vector search, lifecycle filtering, persistence |
| `IntelligenceTests.cs` | 37 | Duplicate detection, contradictions, reversible collapse, decay tuning, hash embeddings, persistence |
| `BenchmarkRunnerTests.cs` | 35 | IR metrics (Recall@K, Precision@K, MRR, nDCG@K), 4 benchmark datasets, validation |
| `CoreMemoryToolsTests.cs` | 20 | Store, search, delete memory tool endpoints |
| `PhysicsEngineTests.cs` | 19 | Mass computation, gravitational force, slingshot |
| `AccretionScannerTests.cs` | 18 | DBSCAN clustering, pending collapses |
| `KnowledgeGraphTests.cs` | 17 | Edge operations, graph traversal |
| `ClusterManagerTests.cs` | 14 | Cluster CRUD and centroid operations |
| `LifecycleEngineTests.cs` | 12 | State transitions, deep recall, decay cycles |
| `PersistenceManagerTests.cs` | 9 | JSON serialization, debounced saves |
| `RegressionTests.cs` | 9 | Integration and edge-case scenarios |
| `MetricsCollectorTests.cs` | 8 | Latency recording, percentile computation, timer pattern |
| `AccretionToolsTests.cs` | 7 | Accretion tool functionality |
| `DecayBackgroundServiceTests.cs` | 2 | Background service decay cycles |
| `AccretionBackgroundServiceTests.cs` | 2 | Background service lifecycle |
| `EmbeddingWarmupServiceTests.cs` | 2 | Embedding warmup startup behavior |
