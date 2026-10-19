# Testing

[< Back to README](../README.md)

The xUnit suite targets net8.0, net9.0, and net10.0. It covers the legacy cognitive-memory engine,
the governed Core substrate, MCP 2.1 conformance, and adversarial identity/tenant boundaries.

The per-file table below is a guide to coverage, not a release test-count ledger. Theory data and
new regression cases make exact counts change frequently; use the test runner for the current total.

## Running Tests

```bash
cd mcp-engram-memory
dotnet test
```

For a fast governance pass on one target framework:

```bash
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj \
  -f net8.0 \
  --filter "FullyQualifiedName~Constitution|FullyQualifiedName~Knowledge|FullyQualifiedName~Provenance|FullyQualifiedName~LearningPipeline|FullyQualifiedName~PlanningPipeline|FullyQualifiedName~SemanticAsset|FullyQualifiedName~GovernancePersistence|FullyQualifiedName~TenantStructureIsolation|FullyQualifiedName~McpSdkConformance"
```

Use a separate artifacts directory when a live stdio server may hold the default build output:

```bash
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj \
  -f net8.0 --artifacts-path artifacts/governance-tests
```

## Concurrency and storage timer regressions

Run the focused reliability checks across all three target frameworks:

```bash
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release \
  --filter "FullyQualifiedName~StorageTimerOwnershipTests|FullyQualifiedName~ConcurrentTestWorkersTests|FullyQualifiedName~CognitiveIndexSearchConcurrencyTests|FullyQualifiedName~CognitiveIndexDisposalTests|FullyQualifiedName~LockUpgradeRegressionTests|FullyQualifiedName~GraphTimerDeferral|FullyQualifiedName~SqliteTimers_FullSaveSubsumes" \
  --logger "trx;LogFilePrefix=reliability"
```

The timer tests explicitly deliver callbacks that were queued before cancellation. JSON and SQLite
must ignore an old callback after replacement or flush, retain failed writes, drain on disposal,
and defer graph writes until entry work commits. SQLite also checks both full-save/incremental
callback orders and tenant-scoped incremental deletes. The internal clock defaults to
`TimeProvider.System`; public constructors and storage formats are unchanged. SQL Server uses the
same ownership checks; its live storage tests require `ENGRAM_TEST_SQLSERVER_CONNECTION`.

Concurrency tests use a small number of dedicated workers and synchronized batches, with exact
mutation/search counts and maintenance-progress assertions. They retain their 30/60-second work
deadlines and cancel and join workers on failure. A worker that cannot drain is reported as a
failure; its fixture is retained rather than disposed underneath it. Disposal tests wait for an
actual queued writer. The real SQLite timer smoke test polls durable state within a deadline
without calling `Flush`; it does not assume a callback ran after a fixed sleep.

For release validation, also run the complete non-MSA suite with the default concurrent target
framework execution and compare the recall reports described in [recall-evaluation.md](recall-evaluation.md).
Do not hide a timeout by disabling concurrency or retrying until green. A timeout alone does not
establish a production deadlock: inspect progress and cleanup evidence. Use `LogFilePrefix` so
concurrent frameworks retain separate test-result files; CI uploads each one.

## Noisy recall and scale evaluation

The [noisy recall protocol](recall-noisy-evaluation.md) adds frozen questions, real
ONNX distractors, 1,000/5,000-memory stores and 8/32-namespace layouts. Ordinary CI
runs its small case on net8/net9/net10; a separate `RecallScale` job runs the large
matrix on net8. Use `Category!=MSA&Category!=RecallScale` for the ordinary suite and
`Category=RecallScale` for the scale lane. Both are required when validating recall.
The report distinguishes hard tenant/ACL/durability checks from measured quality
gaps; `scripts/check-noisy-recall.py <fresh-output-directory> --release` fails on
predeclared release blockers even when the evaluation tests themselves pass.
Scale CI now requires that strict gate. See [scoped candidate recovery](recall-candidate-recovery.md)
for the fix, regression contract and remaining limitations.

## Tool profile count regression check

For a quick check of the published tool counts:

```bash
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj \
  -f net8.0 --filter FullyQualifiedName~ToolSurfaceCountTests
```

`ToolSurfaceCountTests` counts the MCP SDK registrations from `ToolProfileRegistration`,
the same registration path used by `Program.cs`. It checks both installer summaries,
README tables and prose, service configuration docs, assistant setup guides, the cheat sheet,
the tools reference (including every tool name), the banner, and other current total-count claims. It requires
no model initialization, tool calls, network access, or execution of the installers.
The normal push/PR CI test step runs these checks on all supported frameworks; the nightly
MSA-only benchmark filter does not.

When adding or removing tools, update the counts and reference entries in the same change.
Historical release notes and planning documents retain their original counts. The GitHub
repository description is external metadata, so check it separately when releasing:

```bash
gh repo view wyckit/mcp-engram-memory --json description --jq .description
```

## Governed substrate coverage

| Test File | Invariant exercised |
|-----------|---------------------|
| `ConstitutionCoreTests.cs` | Canonical hashes, immutable versions, Root/overlay monotonicity, deterministic rule order |
| `ConstitutionKernelTests.cs` | Pre/post decisions, append-only audit, evaluator failures that deny and audit |
| `ConstitutionMcpFilterTests.cs` | Global MCP filter registration, operation envelopes, canonical argument hashing, and deny behavior |
| `KnowledgeAssetPrimitivesTests.cs` | Memory/knowledge separation, maturity and validity, evidence, calibrated epistemic fields, permission inheritance |
| `ProvenanceStoreTests.cs` | Exact versioned lineage, append-only assertions, tenant boundaries, source-permission intersection |
| `LearningPipelineTests.cs` | Quarantined Teacher output, deterministic-first verification, independence, approval and promotion denial paths |
| `GovernedKnowledgeStoreTests.cs` | Atomic reference commit, active-version conflicts, authorization recheck, idempotency, provenance/audit visibility |
| `PlanningPipelineTests.cs` | Authorization before relevance, monotone loadouts, deterministic budgets, stale authorization removal, citations, fail-closed adapters |
| `SemanticAssetFamilyTests.cs` | Skill, Documentation, CodeGraph, and Curriculum canonical publication and validation |
| `AssetRuntimeTests.cs` | Host-sandbox Skill execution, deterministic verifier veto, curriculum training gates, incremental CodeGraph contracts |
| `GovernancePersistenceTests.cs` | Checksummed snapshots, fsync journals, replay, corrupt-tail recovery, earlier-corruption failure, tenant separation |
| `McpSdkConformanceTests.cs` | ModelContextProtocol 2.2.0 package, stdio builder registration, protocol negotiation options, generated tool schema/metadata |
| `TenantStructureIsolationTests.cs` | Same bare IDs across tenant/legacy partitions; tenant fail-closed graph/cluster/lifecycle paths and legacy positive controls |

### Security-test pattern

Isolation tests require a positive control. An empty result is not evidence of enforcement when the
fixture itself is empty or a spectral threshold was never met. The tenant suites seed data that the
legacy-unisolated control can read/mutate, then prove an identified non-empty-tenant principal cannot
observe or change the same global bare-ID structures.

The current limitation is intentional and tested: tenant-aware entry CRUD works, but graph,
cluster, lifecycle support, collapse, and diffusion structures are not yet tenant-qualified. Their
affected MCP tools must fail closed rather than silently use the legacy global structures.

## Test Files

| Test File | Tests | Focus |
|-----------|-------|-------|
| BenchmarkRunnerTests.cs | 106 | IR metrics, 17 benchmark datasets, 56-combination regression baseline, ONNX benchmarks, ablation study |
| CognitiveIndexTests.cs | 43 | Vector search, lifecycle filtering, persistence, memory limits |
| RetrievalImprovementTests.cs | 40 | Synonym expansion, document enrichment, Porter stemming, BM25 semantic gate, category boost, auto-PRF, adaptive RRF |
| IntelligenceTests.cs | 39 | Duplicate detection, contradictions, reversible collapse, decay tuning, merge |
| HierarchicalRoutingTests.cs | 36 | HMoE domain tree: node creation, parent-child linking, tree walk routing, flat fallback, auto-classification |
| InvariantTests.cs | 27 | Structural invariants across JSON and SQLite backends |
| MultiAgentTests.cs | 26 | Namespace registry, ownership, permissions, cross-search, sharing |
| KnowledgeGraphTests.cs | 20 | Edge operations, graph traversal, batch edge creation, edge transfer |
| CoreMemoryToolsTests.cs | 20 | Store, search, delete memory tool endpoints |
| SqliteStorageProviderTests.cs | 19 | SQLite backend: CRUD, persistence, concurrent access, WAL mode |
| PhysicsEngineTests.cs | 19 | Mass computation, gravitational force, slingshot |
| AccretionScannerTests.cs | 18 | DBSCAN clustering, pending collapses |
| DebateToolsTests.cs | 17 | Debate tools: validation, cold-start, expert retrieval, resolve lifecycle, E2E |
| ClusterManagerTests.cs | 16 | Cluster CRUD, centroid operations, membership transfer |
| ExpertToolsTests.cs | 15 | dispatch_task/create_expert: validation, routing, context retrieval, E2E |
| ExpertDispatcherTests.cs | 15 | Expert creation, routing hits/misses, threshold handling |
| HnswIndexTests.cs | 20 | HNSW index: add/search/remove, high-dimensional recall, rebuild, snapshot/restore round-trip |
| DebateSessionManagerTests.cs | 14 | Session management: alias registration, resolution, TTL purge |
| QueryExpanderTests.cs | 14 | IDF-based query expansion, BM25 compound tokenization |
| VectorQuantizerTests.cs | 13 | Int8 quantization/dequantization, SIMD dot product, cosine preservation |
| NamespaceCleanupTests.cs | 13 | Namespace deletion cascade, purge_debates, JSON and SQLite backends |
| CompositeToolsTests.cs | 12 | Composite tools: remember, recall, reflect — auto-dedup, auto-linking |
| LifecycleEngineTests.cs | 12 | State transitions, deep recall, decay cycles |
| FeedbackTests.cs | 11 | Agent feedback: energy boost/suppress, state transitions |
| AutoSummarizerTests.cs | 9 | Auto-summarization and cluster summary generation |
| RegressionTests.cs | 9 | Integration and edge-case scenarios |
| PersistenceManagerTests.cs | 19 | JSON serialization, debounced saves, checksums, storage version validation, HNSW snapshot persistence |
| FloatArrayBase64ConverterTests.cs | 9 | Base64 serialization roundtrip, legacy JSON array reading |
| QuantizedSearchTests.cs | 8 | Two-stage search pipeline, lifecycle-driven quantization |
| MetricsCollectorTests.cs | 8 | Latency recording, percentile computation |
| MaintenanceToolsTests.cs | 7 | Rebuild embeddings, compression stats |
| ChecksumTests.cs | 7 | SHA-256 persistence checksums, crash recovery, storage version persistence |
| AccretionToolsTests.cs | 7 | Accretion tool functionality |
| AutoSummarizeIntegrationTests.cs | 5 | Auto-summarize integration with clustering pipeline |
| DecayBackgroundServiceTests.cs | 2 | Background service decay cycles |
| AccretionBackgroundServiceTests.cs | 2 | Background service lifecycle |
| EmbeddingWarmupServiceTests.cs | 2 | Embedding warmup startup behavior |
| AdminToolsTests.cs | 9 | GetMemory, CognitiveStats, PurgeDebates tool endpoints |
| AgentOutcomeBenchmarkRunnerTests.cs | 6 | Task-style memory condition benchmark including the harder synonym-gap/graph-expansion dataset |
| LiveAgentOutcomeBenchmarkRunnerTests.cs | 5 | Live-model memory condition benchmark with structured JSON grading, split-fragment recovery, and hard-dataset coverage |
| BenchmarkToolsTests.cs | 14 | RunBenchmark, RunAgentOutcomeBenchmark, RunLiveAgentOutcomeBenchmark, compare_live_agent_outcome_artifacts, GetMetrics, ResetMetrics tool endpoints |
| ClusterToolsTests.cs | 12 | CreateCluster, UpdateCluster, StoreClusterSummary, GetCluster, ListClusters tool endpoints |
| GraphToolsTests.cs | 15 | LinkMemories, UnlinkMemories, GetNeighbors, TraverseGraph tool endpoints |
| IntelligenceToolsTests.cs | 14 | DetectDuplicates, FindContradictions, UncollapseCluster, ListCollapseHistory, MergeMemories tool endpoints |
| LifecycleToolsTests.cs | 13 | PromoteMemory, DeepRecall, MemoryFeedback, DecayCycle, ConfigureDecay tool endpoints |
| MultiAgentToolsTests.cs | 12 | CrossSearch, ShareNamespace, UnshareNamespace, ListShared, WhoAmI tool endpoints |
| SynthesisToolsTests.cs | 2 | SynthesizeMemories tool endpoints |
| DiversityRerankerTests.cs | 11 | Cluster-aware MMR diversity reranking: selection, cluster/category penalties, edge cases |
| VisualizationToolsTests.cs | 10 | GetGraphSnapshot tool: node/edge serialization, filter options, stats accuracy |
