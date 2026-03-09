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

## MCP Tools (36 total)

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

### Panel of Experts / Debate (3 tools)

| Tool | Description |
|------|-------------|
| `consult_expert_panel` | Consult a panel of experts by running parallel searches across multiple expert namespaces. Stores each perspective in an active-debate namespace and returns integer-aliased results so the LLM can reference nodes without managing UUIDs. Replaces multiple `search_memory` + `store_memory` calls with a single macro-command. |
| `map_debate_graph` | Map logical relationships between debate nodes using integer aliases from `consult_expert_panel`. Translates aliases to UUIDs internally and batch-creates knowledge graph edges. Replaces multiple `link_memories` calls with a single macro-command. |
| `resolve_debate` | Resolve a debate by storing a consensus summary as LTM, linking it to the winning perspective, and batch-archiving all raw debate nodes. Cleans up session state. Replaces manual `store_memory` + `link_memories` + `promote_memory` calls with a single macro-command. |

Debate workflow: `consult_expert_panel` (gather perspectives) → `map_debate_graph` (define relationships) → `resolve_debate` (store consensus). Sessions use integer aliases (1, 2, 3...) so the LLM never handles UUIDs. Sessions auto-expire after 1 hour.

### Benchmarking & Observability (3 tools)

| Tool | Description |
|------|-------------|
| `run_benchmark` | Run an IR quality benchmark. Datasets: `default-v1` (25 seeds, 20 queries), `paraphrase-v1` (25 seeds, 15 queries), `multihop-v1` (25 seeds, 15 queries), `scale-v1` (80 seeds, 30 queries). Computes Recall@K, Precision@K, MRR, nDCG@K, and latency percentiles. |
| `get_metrics` | Get operational metrics: latency percentiles (P50/P95/P99), throughput, and counts for search, store, and other operations. |
| `reset_metrics` | Reset collected operational metrics. Optionally filter by operation type. |

Four benchmark datasets covering programming languages, data structures, ML, databases, networking, systems, security, and DevOps topics. Relevance grades use a 0–3 scale (3 = highly relevant).

### Maintenance (2 tools)

| Tool | Description |
|------|-------------|
| `rebuild_embeddings` | Re-embed all entries in one or all namespaces using the current embedding model. Use after upgrading the embedding model to regenerate vectors from stored text. Entries without text are skipped. Preserves all metadata, lifecycle state, and timestamps. |
| `compression_stats` | Show vector compression statistics for a namespace or all namespaces. Reports FP32 vs Int8 disk savings, quantization coverage, and memory footprint estimates. |

### Expert Routing (2 tools)

| Tool | Description |
|------|-------------|
| `dispatch_task` | Route a query to the most relevant expert namespace via semantic similarity against the meta-index. Returns the expert profile and top memories from that namespace as context, or `needs_expert` status if no qualified expert is found. |
| `create_expert` | Instantiate a new expert namespace and register it in the semantic routing meta-index. The persona description is embedded for future query routing. |

Expert routing workflow: `dispatch_task` (route query) → if miss: `create_expert` (define specialist) → `dispatch_task` (retry). The system maintains a hidden `_system_experts` meta-index that maps queries to specialized namespaces via cosine similarity (default threshold: 0.75). Experts within a 5% score margin of the top match are returned as candidates.

## Architecture

### Services

| Service | Description |
|---------|-------------|
| `CognitiveIndex` | Thread-safe namespace-partitioned vector index with two-stage search (Int8 screening + FP32 reranking), lifecycle filtering, duplicate detection, and access tracking |
| `KnowledgeGraph` | In-memory directed graph with adjacency lists, bidirectional edge support, and contradiction surfacing |
| `ClusterManager` | Semantic cluster CRUD with automatic centroid computation |
| `LifecycleEngine` | Activation energy computation, per-namespace decay configs, decay cycles, and state transitions (STM/LTM/archived) |
| `PhysicsEngine` | Gravitational force re-ranking with "Asteroid" (semantic) + "Sun" (importance) output |
| `AccretionScanner` | DBSCAN-based density scanning with reversible collapse history (persisted to disk) |
| `BenchmarkRunner` | IR quality benchmark execution with Recall@K, Precision@K, MRR, nDCG@K scoring |
| `MetricsCollector` | Thread-safe operational metrics with P50/P95/P99 latency percentiles |
| `DebateSessionManager` | Volatile in-memory session state for debate workflows with integer alias mapping and 1-hour TTL auto-purge |
| `ExpertDispatcher` | Semantic routing engine that maps queries to specialized expert namespaces via a hidden meta-index with JIT expert instantiation |
| `VectorQuantizer` | Static Int8 scalar quantization: `Quantize`, `Dequantize`, SIMD-accelerated `Int8DotProduct`, `ApproximateCosine` |
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

### Models

| Model | Description |
|-------|-------------|
| `CognitiveEntry` | Core memory entry with vector, text, metadata, lifecycle state, and activation energy |
| `QuantizedVector` | Int8 quantized vector with `sbyte[]` data, min/scale for reconstruction, and precomputed self-dot product |
| `FloatArrayBase64Converter` | JSON converter for `float[]` — writes Base64 strings, reads both Base64 and legacy JSON arrays for backwards compatibility |

### Searchable Compression

Vectors use a lifecycle-driven compression pipeline:

- **STM entries**: Full FP32 precision for maximum search accuracy
- **LTM/archived entries**: Auto-quantized to Int8 (asymmetric min/max → [-128, 127]) on state transition
- **Two-stage search**: Namespaces with 30+ entries use Int8 screening (top k×5 candidates) followed by FP32 exact cosine reranking
- **SIMD acceleration**: `Int8DotProduct` uses `System.Numerics.Vector<T>` for portable hardware-accelerated dot products (sbyte→short→int widening pipeline)
- **Base64 persistence**: Vectors are serialized as Base64 strings instead of JSON number arrays, reducing disk usage by ~60%. Legacy JSON arrays are still readable for backwards compatibility.

### Persistence

Data is stored as JSON files in a `data/` directory:
- `{namespace}.json` — entries with Base64-encoded vectors (per namespace)
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

## Claude Setup

To get the most out of this memory system, add the following to your `CLAUDE.md` (global or per-project). This teaches Claude Code how to recall, store, route, and manage memory lifecycle automatically.

### Recall: Parallel Multi-Agent Search

At conversation start, Claude should search vector memory from multiple angles simultaneously:

```
1. Agent 1 — Project context: search_memory in the project namespace with a task-relevant query
2. Agent 2 — Cross-project patterns: search_memory in "work" and "synthesis" namespaces
3. Agent 3 — Related topics: search_memory with alternative phrasings/keywords
```

### Tool Selection Guide

Tell Claude which tool to use in each situation:

| Situation | Tool | Why |
|-----------|------|-----|
| Starting a task, need project context | `search_memory` | Direct namespace search, fast and focused |
| Complex cross-domain question | `dispatch_task` | Semantic routing finds the best expert namespace automatically |
| Need multiple knowledge perspectives | `consult_expert_panel` | Parallel multi-namespace search with debate tracking |
| Looking for archived/forgotten knowledge | `deep_recall` | Searches ALL lifecycle states; auto-resurrects high-scoring entries |
| Checking for duplicate memories | `detect_duplicates` | Pairwise cosine similarity scan |
| Suspect conflicting information | `find_contradictions` | Surfaces `contradicts` edges + high-similarity pairs |

**Default to `search_memory`** for most recall tasks. Use `dispatch_task` when unsure which namespace holds the answer. Use `consult_expert_panel` for multi-perspective analysis.

### Expert Routing

The semantic routing system (`dispatch_task` / `create_expert`) works best when Claude:

1. **Uses `dispatch_task` for open-ended questions** that span multiple domains — the system automatically finds the most relevant expert namespace
2. **Creates experts for distinct knowledge domains** the user works in repeatedly (e.g., `rust_systems_engineer`, `database_architect`) — avoid over-fragmenting closely related topics
3. **Seeds expert namespaces** after creation by storing domain knowledge into the expert's target namespace (e.g., `expert_rust_systems_engineer`)
4. **Follows the JIT pattern**: `dispatch_task` → if `needs_expert` → `create_expert` → `dispatch_task` again

### Store Quality

Instruct Claude to write memory text for **future searchability**:
- Include problem domain keywords (e.g., "SIMD", "quantization", "lock contention")
- State both the problem and the solution — future queries might phrase either way
- Be specific: "Fixed DLL lock by killing McpVectorMemory.exe before build" not "fixed the issue"

### Duplicate and Contradiction Handling

- When `store_memory` warns about near-duplicates: skip if existing is accurate, upsert (same ID) if outdated, or store + `link_memories` if both are distinct
- Use `find_contradictions` periodically or before storing information that might conflict with existing knowledge

### Lifecycle Promotion Triggers

Tell Claude when to promote STM → LTM:
- Memory has been recalled in 2+ separate conversations
- Documents a stable architectural pattern or user preference
- Captures a bug-fix that could recur
- Records a user correction

### Relationship Types

When using `link_memories`, choose the right relation:

| Relation | Use When |
|----------|----------|
| `parent_child` | Hierarchical: architecture → implementation detail |
| `cross_reference` | Bidirectional relevance (auto-creates both directions) |
| `similar_to` | Same topic, different angle |
| `contradicts` | Conflicting information — flag for resolution |
| `elaborates` | One memory expands on another |
| `depends_on` | Prerequisite knowledge |

### Namespace Conventions

| Namespace | Purpose |
|-----------|---------|
| `{project-dir}` | Project-specific knowledge (e.g., `mcp-vector-memory`) |
| `work` | Cross-project workflow preferences and tooling decisions |
| `synthesis` | Cross-project architectural insights and patterns |
| `expert_{id}` | Expert routing namespaces (auto-created by `create_expert`) |

## Copilot Setup

### MCP Server Configuration

Add the vector memory server to your workspace at `.vscode/mcp.json`:

```json
{
  "inputs": [],
  "servers": {
    "vector-memory": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/mcp-vector-memory/src/McpVectorMemory"]
    }
  }
}
```

### Custom Instructions

Create `.github/copilot-instructions.md` in your repo root to teach Copilot how to use the memory tools:

```markdown
# Vector Memory

You have access to a persistent vector memory system via the `vector-memory` MCP server.

## Recall

Before starting any task, search vector memory for relevant context:
- Use `search_memory` with the project namespace and a query describing the current task
- If the user references past decisions or bugs, search for them before answering

## Store

Store memories whenever you complete a task, fix a bug, learn a pattern, or receive a correction:
- **namespace**: Use the project directory name (e.g., `mcp-vector-memory`)
- **id**: Descriptive kebab-case (e.g., `fix-dll-lock-issue`)
- **text**: Clear, self-contained description with domain keywords for future searchability
- **category**: One of: `decision`, `pattern`, `bug-fix`, `architecture`, `preference`, `lesson`, `reference`

## Tool Selection

| Situation | Tool |
|-----------|------|
| Need project context | `search_memory` |
| Cross-domain question, unsure which namespace | `dispatch_task` |
| Need multiple expert perspectives | `consult_expert_panel` |
| Looking for archived/forgotten knowledge | `deep_recall` |
| Checking for duplicates | `detect_duplicates` |

## Expert Routing

- `dispatch_task` routes queries to the best expert namespace automatically
- If it returns `needs_expert`, use `create_expert` to define a specialist
- After creating an expert, populate its namespace with `store_memory`

## Lifecycle

- New memories default to STM; promote to LTM when stable and reused
- Use `link_memories` to connect related memories with relation types:
  `parent_child`, `cross_reference`, `similar_to`, `contradicts`, `elaborates`, `depends_on`
```

You can also create path-specific instructions at `.github/instructions/memory.instructions.md` for scoped behavior.

## Antigravity Setup

### MCP Server Configuration

Add the vector memory server via **Settings > MCP > View raw config**, or edit `~/.gemini/antigravity/mcp_config.json` directly:

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

### Custom Instructions

Add a `GEMINI.md` file in your workspace root (or `~/.gemini/GEMINI.md` for global rules) to teach Antigravity how to use the memory tools:

```markdown
# Vector Memory

You have access to a persistent vector memory system via the `vector-memory` MCP server.

## Recall

Before starting any task, search vector memory for relevant context:
- Use `search_memory` with the project namespace and a query describing the current task
- If the user references past decisions or bugs, search for them before answering

## Store

Store memories whenever you complete a task, fix a bug, learn a pattern, or receive a correction:
- **namespace**: Use the project directory name (e.g., `mcp-vector-memory`)
- **id**: Descriptive kebab-case (e.g., `fix-dll-lock-issue`)
- **text**: Clear, self-contained description with domain keywords for future searchability
- **category**: One of: `decision`, `pattern`, `bug-fix`, `architecture`, `preference`, `lesson`, `reference`

## Tool Selection

| Situation | Tool |
|-----------|------|
| Need project context | `search_memory` |
| Cross-domain question, unsure which namespace | `dispatch_task` |
| Need multiple expert perspectives | `consult_expert_panel` |
| Looking for archived/forgotten knowledge | `deep_recall` |
| Checking for duplicates | `detect_duplicates` |

## Expert Routing

- `dispatch_task` routes queries to the best expert namespace automatically
- If it returns `needs_expert`, use `create_expert` to define a specialist
- After creating an expert, populate its namespace with `store_memory`

## Lifecycle

- New memories default to STM; promote to LTM when stable and reused
- Use `link_memories` to connect related memories with relation types:
  `parent_child`, `cross_reference`, `similar_to`, `contradicts`, `elaborates`, `depends_on`
```

You can also create Antigravity Skills for more structured memory workflows. Place a `SKILL.md` in a dedicated directory under `.antigravity/skills/` to define reusable memory operations.

## Build & Test

```bash
cd mcp-vector-memory
dotnet build
dotnet test
```

### Tests

23 test files with 355 test cases covering:

| Test File | Tests | Focus |
|-----------|-------|-------|
| `CognitiveIndexTests.cs` | 39 | Vector search, lifecycle filtering, persistence |
| `IntelligenceTests.cs` | 37 | Duplicate detection, contradictions, reversible collapse, decay tuning, hash embeddings, persistence |
| `BenchmarkRunnerTests.cs` | 35 | IR metrics (Recall@K, Precision@K, MRR, nDCG@K), 4 benchmark datasets, ONNX benchmarks |
| `CoreMemoryToolsTests.cs` | 20 | Store, search, delete memory tool endpoints |
| `PhysicsEngineTests.cs` | 19 | Mass computation, gravitational force, slingshot |
| `AccretionScannerTests.cs` | 18 | DBSCAN clustering, pending collapses |
| `DebateToolsTests.cs` | 17 | Debate tools: validation, cold-start, expert retrieval, edge creation, resolve lifecycle, full E2E pipeline |
| `KnowledgeGraphTests.cs` | 17 | Edge operations, graph traversal, batch edge creation |
| `DebateSessionManagerTests.cs` | 14 | Session management: alias registration, resolution, TTL purge, namespace generation |
| `ClusterManagerTests.cs` | 14 | Cluster CRUD and centroid operations |
| `VectorQuantizerTests.cs` | 12 | Int8 quantization, dequantization roundtrip, SIMD dot product, cosine preservation, edge cases |
| `LifecycleEngineTests.cs` | 12 | State transitions, deep recall, decay cycles |
| `QuantizedSearchTests.cs` | 9 | Two-stage search pipeline, lifecycle-driven quantization, mixed-state ranking, large namespace accuracy |
| `FloatArrayBase64ConverterTests.cs` | 9 | Base64 serialization roundtrip, legacy JSON array reading, CognitiveEntry/NamespaceData roundtrip |
| `PersistenceManagerTests.cs` | 9 | JSON serialization, debounced saves |
| `RegressionTests.cs` | 9 | Integration and edge-case scenarios |
| `MetricsCollectorTests.cs` | 8 | Latency recording, percentile computation, timer pattern |
| `ExpertDispatcherTests.cs` | 15 | Expert creation, routing hits/misses, threshold handling, access tracking, meta-index management |
| `ExpertToolsTests.cs` | 15 | dispatch_task/create_expert tools: validation, routing pipeline, context retrieval, full E2E workflows |
| `MaintenanceToolsTests.cs` | 7 | Rebuild embeddings, compression stats, vector update, metadata preservation |
| `AccretionToolsTests.cs` | 7 | Accretion tool functionality |
| `DecayBackgroundServiceTests.cs` | 2 | Background service decay cycles |
| `AccretionBackgroundServiceTests.cs` | 2 | Background service lifecycle |
| `EmbeddingWarmupServiceTests.cs` | 2 | Embedding warmup startup behavior |
