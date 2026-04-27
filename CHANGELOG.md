# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]

## [0.9.0] - 2026-04-27

Memory-diffusion subsystem. The memory graph's connectivity now actively
shapes how the system forgets, consolidates, and retrieves — not just how
it traverses on demand. One precomputed structure (the per-namespace
eigenbasis of the graph Laplacian) drives four subsystems: graph-aware
decay, sleep-consolidation lifecycle transitions, low-rank duplicate
detection, and spectral retrieval re-ranking. The same brain-style
spreading-activation primitive serves all four.

This release also closes the loop on automation. Storing memories and
linking them is now the entire LLM-facing contract; everything else
(decay, consolidation, graph density, basis warmup, similarity-based
linking) runs as ambient background infrastructure.

### Added

- **`MemoryDiffusionKernel`** (`Services.Graph`). Per-namespace cache
  and operator for diffusing per-entry signals through the memory graph.
  Holds the top-K eigenbasis of the normalized Laplacian
  `L = I - D^(-1/2) W D^(-1/2)` built from positive-relation edges only
  (`parent_child`, `cross_reference`, `similar_to`, `elaborates`,
  `depends_on`; `contradicts` excluded so `L` stays PSD and the heat
  kernel `exp(-tL)` stays a contraction). `ApplySpectralFilter` is the
  primary verb. Cache invalidation is revision-based via the new
  `KnowledgeGraph.Revision` counter — any edge mutation increments the
  live revision and the next read rebuilds. Computed lazily on first
  read; pre-warmed every 30 minutes by the new
  `DiffusionKernelWarmupService`.
- **`RandomizedEigensolver`** (`Services.Graph`). Halko-Martinsson-Tropp
  randomized subspace iteration for top-K largest-magnitude eigenpairs of
  symmetric matrices supplied via a matrix-vector callback. Falls
  through to direct dense Jacobi when `m >= n`. Internal Modified
  Gram-Schmidt + cyclic Jacobi; no external numerical dependency.
- **Spectral graph-diffusion decay.** When a namespace qualifies for the
  diffusion kernel (≥32 nodes, ≥8 positive-relation edges), per-entry
  decay debt is diffused through the heat kernel `exp(-lambda^alpha · t)`
  before being subtracted from activation energy. Tightly-linked clusters
  share forgetting pressure; isolated entries fade alone. Default on;
  opt-out per-namespace via `configure_decay(useSpectralDecay=false)`.
  Subdiffusive exponent `α` configurable via
  `DecayConfig.SubdiffusiveExponent` (default 1.0 = standard heat kernel).
- **Sleep-consolidation pass.** New `LifecycleEngine.RunConsolidationPass`
  runs a long-time heat-kernel diffusion of the activation field and
  drives lifecycle transitions on the smoothed (cluster-aware) values:
  STM→LTM when a memory's cluster collectively supports it, LTM→archived
  when its surrounding cluster has decayed. Topology-driven, complementing
  the access-count-driven decay cycle. New
  `ConsolidationBackgroundService` runs every 24 hours after a 10-minute
  startup delay. New MCP tool `run_consolidation` for explicit invocation.
  New `DecayConfig` fields: `EnableConsolidation` (default true),
  `ConsolidationDiffusionTime` (default 10.0),
  `ConsolidationPromotionThreshold` (default 0.0),
  `ConsolidationArchiveThreshold` (default -5.0).
- **Auto-link scanner.** Periodic background pass (`AutoLinkScanner` +
  `AutoLinkBackgroundService`, 6-hour cadence) scans each namespace for
  high-cosine-similarity entry pairs and creates `similar_to` edges
  between them. Reuses `DuplicateDetector`'s spectral pre-filter at a
  looser threshold (default 0.85 vs. 0.95 for duplicate detection).
  Pairs with any pre-existing edge between them are skipped — auto-link
  never overwrites manually-curated structure and re-scans are
  idempotent. Per-scan edge cap prevents runaway densification on
  pathological corpora. New MCP tool `auto_link_namespace` for explicit
  invocation. New `DecayConfig` fields: `EnableAutoLink` (default true),
  `AutoLinkSimilarityThreshold` (default 0.85),
  `AutoLinkMaxNewEdgesPerScan` (default 1000).
- **Spectral retrieval re-ranking** (`SpectralRetrievalReranker`,
  `Services.Retrieval`). Re-ranks search results through the same
  diffusion kernel applied to relevance scores instead of decay debt.
  Three modes plus passthrough:
  - `Broad`: cluster-dominance gate + max-neighbor boost. When top-K
    candidates concentrate in one connected component (≥3 of top-5),
    cluster mates get lifted above lexical false positives, and any
    dominant-cluster members not yet in the candidate pool surface
    via graph BFS. When top-K is split across clusters, pass through
    (the query is genuinely ambiguous).
  - `Specific`: spectral high-pass via the kernel. Subtracts cluster
    mean from each entry's score so outliers — the precise entry that
    actually answers a precision query — outrank cluster mates that
    came in via graph expansion.
  - `Auto`: word-count + digit/quote heuristic picks Broad or Specific
    locally (no LLM/embedding calls). Default for the `recall` tool.
  - `None`: passthrough.
  New MCP tool `spectral_recall` and a new `spectralMode` parameter on
  the existing `recall` composite tool (default `"auto"`).
- **Low-rank duplicate detection.** `DuplicateDetector.FindDuplicates`
  switches to a three-pass spectral pre-filter above 256 candidates:
  project to a 64-dim subspace via randomized SVD over `E^T E`, scan in
  projection space at a widened threshold, confirm survivors with full
  FP32 cosine. New `EmbeddingSubspace` service (`Services.Retrieval`)
  for the projection primitive. Recall preserved against the original
  pairwise scan; cost grows much more gracefully on large namespaces.
- **6 new MCP tools.** `compute_diffusion_basis`, `diffusion_stats`,
  `invalidate_diffusion` (`MemoryDiffusionTools`); `run_consolidation`
  (added to `LifecycleTools`); `auto_link_namespace` (added to
  `GraphTools`); `spectral_recall` (`SpectralRetrievalTools`). Total
  surface: 65 tools (was 55).
- **3 new background services.** `DiffusionKernelWarmupService`,
  `ConsolidationBackgroundService`, `AutoLinkBackgroundService`.

### Changed

- **`recall` defaults to `spectralMode="auto"`.** The auto heuristic
  inspects query characteristics and picks Broad for short conceptual
  queries (e.g., "memory consolidation") or Specific for longer or
  precision-marked queries (digits, quoted phrases). Local-only
  inference, no extra LLM/embedding calls. Resolves to passthrough on
  namespaces below the diffusion-kernel qualification threshold. Pass
  `spectralMode="none"` to opt out.
- **`configure_decay` accepts new params.** `useSpectralDecay`,
  `subdiffusiveExponent` for spectral decay control. Defaults preserve
  the new auto-on behavior.
- **`KnowledgeGraph.Revision` counter.** Monotonic counter incremented
  by every edge mutator (`AddEdge`, `AddEdges`, `RemoveEdges`,
  `RemoveAllEdgesForEntry`, `TransferEdges`). Consumers that cache
  derived structures (the diffusion kernel, future spectral subsystems)
  compare against this to detect staleness without taking the graph lock.
- **`LifecycleEngine` constructor.** Optional third parameter
  `MemoryDiffusionKernel? diffusion`. Backward-compatible — all existing
  call sites work unchanged.
- **`CompositeTools` constructor.** Required new parameter
  `SpectralRetrievalReranker spectral`. Test sites (and any custom
  callers that construct this manually) need to provide it; production
  DI is automatic.

### Performance (synthetic 40-entry, 8-topic benchmark)

Spectral retrieval (auto mode) vs. existing pipeline:

| metric | none | auto | delta |
|---|---|---|---|
| Overall Recall@5 | 0.820 | **0.920** | +12% |
| Overall Precision@5 | 0.587 | **0.753** | +28% |
| Overall nDCG@5 | 0.847 | **0.907** | +7% |
| Specific-query Precision@5 | 0.533 | **0.667** | +25% |
| Broad-query Recall@5 | 0.640 | **0.840** | +31% |
| Broad-query nDCG@5 | 0.694 | **0.817** | +18% |

MRR holds at 0.95 — spectral re-ranking improves discovery of relevant
cluster mates without reordering the top-1 answer. Genuinely ambiguous
queries (top-K split across multiple clusters) correctly bypass the
boost via the dominance gate and remain at baseline. Forced-Broad on
specific queries does not regress on Recall, MRR, or nDCG (the
`max(original, boosted)` rule preserves strong individual hits).

### Naming

The internal vocabulary uses "memory-diffusion kernel" throughout —
`MemoryDiffusionKernel` (was `GraphLaplacianSpine` during early
development), `DiffusionBasis` (was `LaplacianBasis`), `DiffusionStats`,
`MemoryDiffusionTools`. The math underlying the implementation is still
the graph Laplacian eigenbasis — class XML docs make the bridge
explicit — but the exposed surface uses the behavioral name so cold
readers reach the right mental model immediately.

### Tests

12 new test classes covering diffusion-kernel construction and
filtering, randomized eigensolver correctness, spectral decay debt
diffusion, consolidation lifecycle transitions, auto-link
edge-creation idempotency, embedding subspace projection,
spectral-retrieval mode behaviors, and the spectral-retrieval
benchmark harness. 202/202 tests pass across affected suites; no
regressions in CompositeTools, GraphTools, LifecycleEngine,
KnowledgeGraph, Decay, Feedback, or Intelligence.

## [0.8.1] - 2026-04-22

Post-v0.8.0 stabilisation. Two bodies of work land under this patch: a
full per-namespace lock-hierarchy refactor that retires the global
`ReaderWriterLockSlim` on `CognitiveIndex`, and a focused hygiene pass
(MSA CI signal, resurrection coverage, error-wrapping on the composite
tools, Core-10 documentation). No public API removals or renames; the
sharing surface declared stable in v0.8.0 is unchanged.

### Changed
- **Per-namespace lock hierarchy on `CognitiveIndex`.** The single
  process-wide `ReaderWriterLockSlim` is retired and replaced with a
  `ConcurrentDictionary<string, ReaderWriterLockSlim>` keyed by
  namespace. Writers to different namespaces run in parallel; readers
  on namespace A are no longer blocked by writers on namespace B.
  Id→namespace resolvers (`Get(id)`, `Delete(id)`, `RecordAccess(id)`,
  `SetLifecycleState`, `SetActivationEnergyAndState`,
  `SetLifecycleStateBatch`) resolve lock-free via
  `NamespaceStore._idToNamespace` then acquire only the resolved
  namespace's lock. `UpsertBatch` groups entries by namespace and
  locks each once per sub-batch. Cross-namespace reads (`Count`,
  `GetNamespaces`, `GetAll`, `GetStateCounts(null)`) are now lock-free
  and rely on ConcurrentDictionary snapshot semantics plus the new
  Interlocked `TotalCount` counter.
- **`BM25Index._namespaces` upgraded to `ConcurrentDictionary`** so the
  outer map is safe against concurrent writers under per-namespace
  locking. Inner `NamespaceIndex` state remains bounded by the
  caller's per-namespace write lock.
- **`NamespaceStore.TotalCount`** is now an `O(1)` Interlocked read
  backed by an atomic counter maintained by `TrackEntry` /
  `UntrackEntry` / `RemoveNamespace` / `LoadEntries`.
  `RemoveNamespace` only removes locator entries that still point at
  the namespace being deleted — orphaned ids re-upserted to a
  different namespace keep their locator and count.

### Added
- **`CognitiveIndex.Dispose` is now idempotent and guarded.** Atomic
  once-and-only-once transition via `Interlocked.Exchange`; `NsLock`
  throws `ObjectDisposedException` up front if the index has been
  disposed, with a create-then-publish pattern so a lock we created
  doesn't leak if Dispose races between pre-check and publication.
- **LockHierarchyTests (9 tests).** Structural assertions that prove
  cross-namespace parallelism, same-namespace serialisation, reader
  independence, cross-namespace count consistency, deadlock-freedom
  under 2000-op random stress, UpsertBatch per-group parallelism,
  post-Dispose `ObjectDisposedException` on every touched method, and
  the `RemoveNamespace` orphan-locator invariant.
- **LifecycleEngineTests resurrection coverage (4 new tests).**
  Resurrection increments `AccessCount`; returned
  `CognitiveSearchResult` reflects the new `stm` state; multiple
  archived entries above threshold all resurrect in one call;
  non-archived entries above threshold stay put.
- **`docs/core-10.md`** — the 10 tools that cover the typical workflow
  grouped by usage stage (composite → low-level → multi-agent), plus a
  "when to reach for what" lookup. Fulfils the P4 adoption item from
  `priorities-2026-03-25-toward-1.0`.

### Fixed
- **`LiveAgentOutcomeRegressionTests` was comparing output from two
  different LLMs.** The hard-coded candidate glob
  `*-qwen2.5-7b.json` matched the 2026-04-17 qwen run, but the pinned
  baselines were all `phi3.5:3.8b`. Every "regressed by X%" failure in
  CI was pure model-variance. Fix derives the candidate model slug
  from the baseline's `model` field and asserts `baseline.model ==
  candidate.model` so future drift is caught immediately. With
  matched models, all three `agent-outcome-*-baseline.json`
  comparisons pass with zero delta.
- **`remember` / `recall` / `reflect` composite tools** now validate
  inputs up front and catch `ArgumentException` /
  `InvalidOperationException` inside the body, returning
  `"Error: {message}"` strings consistent with the pattern already in
  `MultiAgentTools` / `CoreMemoryTools`. Previously these leaked raw
  stack traces to MCP clients on empty inputs or degenerate
  embeddings.
- **Parallel-TFM ONNX model download race (MSB3677).** When a
  multi-targeted project (net8.0;net9.0;net10.0) built for the first
  time, three parallel inner builds all downloaded the same model to
  separate temp files, then raced at `<Move>`. Fix adds `<MakeDir>`,
  a `Condition="!Exists(dest)"` guard, and
  `ContinueOnError="WarnAndContinue"` — losing racers no-op silently.
  Applied to both `build/` and `buildMultiTargeting/` targets so
  downstream multi-TFM consumers of `McpEngramMemory.Core` get the
  fix too.
- **Flaky `T2BenchmarkRun.Dispose`.** Debounced persistence writer
  could race with the recursive `Directory.Delete` during teardown
  under ablation mode. Added a short retry loop (5 attempts,
  50ms→250ms backoff) on `IOException` / `UnauthorizedAccessException`
  matching the pattern every other test class in the repo uses.

### Documentation
- `docs/first-5-minutes.md` updated to use the v0.8.0-recommended
  composite tools (`remember`, `recall`) instead of `store_memory` +
  low-level hybrid-search. "What's Next" block points to `reflect`,
  `dispatch_task`, `cross_search`, and multi-agent setup.
- `NamespaceRegistry.Share` / `Unshare` / `EnsureOwnership` XML docs
  explicitly state the per-namespace serialisation guarantee from the
  lost-update race fix in v0.8.0.
- `CognitiveIndex` class-level XML doc expanded to describe the
  per-namespace locking contract (single-ns operations hold only the
  target ns's lock; cross-ns reads are lock-free; events fire after
  lock release).
- `CliExecutableResolver` `Process.Start` cref disambiguated to the
  `(ProcessStartInfo)` overload; `HybridSearchEngine` constants moved
  out from between `<param>` tags so the XML parser associates them
  with the right method — 12 compiler warnings eliminated.

### Known gaps (tracked for 0.9.0+)
- **`BM25Index.NamespaceIndex` inner-state race during out-of-lock
  `Search`.** Pre-existing HIGH-severity race flagged during PR #3
  review: `CognitiveIndex.Search` releases the per-ns read lock
  before calling `_hybridSearch.HybridSearch` → `_bm25.Search`, which
  reads inner `Dictionary` / `HashSet` / `int` state a concurrent
  writer can mutate. Not a regression of this release (same pattern
  existed pre-refactor). Fix deserves a focused PR with before/after
  concurrent-hybrid-search tests.
- **Cross-process cache invalidation.** SQLite WAL prevents on-disk
  corruption, but `NamespaceStore._loadedNamespaces` is a one-shot
  cache with no cross-process refresh. Design captured in
  `design-2026-04-21-lock-hierarchy-epoch-protocol` (per-namespace
  `Epoch` counter persisted to SQLite, evict-on-stale).

## [0.8.0] - 2026-04-21

Headline: **multi-agent memory sharing is now officially supported.** The
sharing API surface (`AGENT_ID`, `share_namespace`, `unshare_namespace`,
`list_shared`, `whoami`, `cross_search`) is declared stable under SemVer
for the 0.x line — breaking changes to these tools will bump the minor
version. See `docs/multi-agent.md` for the quick-start and the explicit
boundaries (single server process per data directory, global write-lock
throughput ceiling).

### Added
- **`CognitiveIndex.EntryUpserted` / `EntryDeleted` events**: real-time
  notification for parallel agents. Events fire **after** the internal
  write lock is released, so handlers can call back into the index
  safely. `UpsertBatch` raises one `EntryUpserted` per accepted entry.
  Enables zero-poll fan-in patterns (see
  `tests/McpEngramMemory.Tests/ParallelAgentTests.cs::RealtimeSharing_*`).
- **`cross_search` parameter parity (partial)**: new optional params
  `minScore`, `category`, `diversity`, `diversityLambda` match
  `search_memory` defaults. `SearchMultiple` routes through the
  `SearchRequest` path when `diversity` is requested so cluster-aware
  MMR reranking applies per namespace before RRF merge.
  Known gap: `expand_graph`, `expand_query`, `use_physics`, and
  `temperature` are single-namespace orchestration features that remain
  exclusive to `search_memory` — documented in `docs/multi-agent.md`.
- **`docs/multi-agent.md`**: quick-start, permission semantics, live
  event subscription pattern, explicit v0.8.0 boundaries and
  troubleshooting.
- **Parallel-agent test suite** (`ParallelAgentTests.cs`, 9 tests):
  `ConcurrentShare_PreservesAllGrants` (regression for the lost-update
  race), `ConcurrentEnsureOwnership_FirstWriterWins`,
  `ConcurrentCrossSearch_WithShareChurn_NoExceptions`,
  `ParallelAgents_ConcurrentStoresToOwnNamespaces_NoLostWrites`,
  `ParallelAgents_ConcurrentWritesToSharedNamespace_AllVisible`,
  `ConcurrentDuplicateIdInsert_NoTornWrite`,
  `RealtimeSharing_ReaderObservesWriterEvent`,
  `RealtimeSharing_FanInFromMultipleWriters_NoDroppedEvents`,
  `ConcurrentAccessCheck_ConsistentVisibility`.
- **`tests/McpEngramMemory.Tests/xunit.runner.json`**: enables
  `parallelizeTestCollections=true` with `maxParallelThreads=4` for
  deterministic parallel test execution.

### Fixed
- **Lost-update race in `NamespaceRegistry.Share` / `Unshare` /
  `EnsureOwnership`**: concurrent grants to the same namespace could
  silently drop prior grants because the read-modify-write on the
  permission entry was unsynchronized. Fixed with a per-namespace
  `ConcurrentDictionary<string, object>` of monitors. Grants to
  different namespaces stay parallel; grants to the same namespace
  serialize. Double-checked locking keeps `EnsureOwnership` lock-free
  on the registered-path. Regression test:
  `ConcurrentShare_PreservesAllGrants` (32 concurrent shares).

### Documentation
- **XML doc disambiguation**: `CliExecutableResolver` points its
  `Process.Start` cref at the `(ProcessStartInfo)` overload (fixes
  CS0419). `HybridSearchEngine` constants moved out from between the
  `HybridSearch` method's `<param>` tags and its signature so the 11
  CS1572 warnings go away; `queryVector` and `entryCount` parameter
  docs added.
- **API stability declaration** for the multi-agent sharing surface
  in `docs/multi-agent.md` (v0.8.0 boundary note).

### Known gaps (tracked for 0.8.1 / 0.9.0)
- **Namespace-partitioned write locks on `CognitiveIndex`**: the
  existing single `ReaderWriterLockSlim` gates all writers across all
  namespaces. Research for the refactor (BM25Index ConcurrentDictionary
  upgrade, `_idToNamespace` coordination, per-ns RWL) is complete but
  out of scope for 0.8.0; a future release will lift the
  single-process-writer ceiling.
- **Cross-process sharing**: SQLite WAL prevents on-disk corruption
  across processes, but `NamespaceStore._loadedNamespaces` is a
  one-shot cache with no cross-process invalidation. One
  `mcp-engram-memory` server process per data directory remains the
  supported topology; a later release will add a version-counter-based
  refresh protocol.

## [0.7.1] - 2026-04-20

### Added
- **`OllamaClient.GenerateStreamAsync`**: NDJSON token-by-token streaming via `IAsyncEnumerable<string>`, with stop-token array threaded through to Ollama for early termination. Enables incremental UI updates without waiting for a full generation.
- **`OllamaClient.KeepAlive`**: public property serialized as Ollama's `keep_alive` on every `/api/generate`. Set to `"24h"` to prevent Ollama from unloading models after its default 5-minute idle timeout — eliminates the 10–40 s reload tax on the first call after a pause.
- **`OllamaOptions.Stop`**: stop-token array plumbed through the request payload.
- **Cross-CLI MRCR benchmark drivers**: `CodexCliModelClient` (spawns `codex exec -o <tempfile>`) and `GeminiCliModelClient` (spawns `gemini -p ""` with stdin prompt) join the existing `ClaudeCliModelClient`. Each charges against the vendor subscription, not API keys. New `CliExecutableResolver` probes PATH for `.exe`/`.cmd`/`.bat` variants so npm-installed CLI shims on Windows resolve correctly. UTF-8 stdin/stdout encoding is now forced across all three clients (fixes codex's "input is not valid UTF-8 at offset N" failure on prompts containing emoji).
- **Cross-CLI 8-needle comparison (2026-04-19, n=25 stratified)**:
    - gemini-cli/gemini-2.5-pro: **0.994 sim / 100% pass**
    - codex-cli/gpt-5.4-mini:    **0.993 sim / 100% pass**
    - claude-cli/opus:            0.987 sim /  96% pass
    - codex-cli/gpt-5.4:          0.973 sim /  96% pass
    - gemini-cli/gemini-2.5-flash:0.965 sim /  88% pass
    - claude-cli/sonnet:          0.912 sim /  72% pass
  All at 99.7% prompt-token reduction. Flagships don't always win: gpt-5.4-mini beats gpt-5.4 on ordinal engram; Gemini 2.5 Pro ties for best overall.
- Tightened ordinal prompt to explicitly ask for verbatim snippet reproduction (needed for Gemini 2.5 Pro to stop truncating long needles; Claude and Codex were already interpreting the earlier phrasing correctly).
- **Ordinal-aware engram retrieval mode for MRCR**: `MrcrGenerationOptions.EngramMode = "ordinal"` enables pair-wise ingest that tags each assistant turn with its user-ask category signature and within-category 1-based ordinal (stored on `CognitiveEntry.Metadata["ordinal"]`). A new `MrcrProbeParser` extracts `(RandomString, Ordinal, Category)` from the "Prepend X to the Nth (1 indexed) Y" probe template, and retrieval resolves to an exact category+ordinal lookup via `CognitiveIndex.GetAllInNamespace`. Probes that don't match the template fall back to hybrid search.
  - 25-probe stratified run (2026-04-19, contexts 18K–571K approx tokens):
    - **Opus ordinal engram = 0.987 sim / 96% pass / 14,556 prompt tokens (n=25)**; on the matched set where full_context also ran (n=14), Opus ordinal engram (0.979) beats Opus full_context (0.936).
    - Sonnet ordinal engram = 0.912 sim / 72% pass across n=25; on the matched set Sonnet full_context (0.993) beats ordinal engram (0.898) — Sonnet is very strong at long-context recall when the prompt fits.
    - 11/25 probes exceed Claude's 200K limit — full_context cannot run there, ordinal engram hits 0.997 sim / 100% pass for Opus on the oversized set.
    - Prompt-token reduction: **99.7% (320× fewer tokens, 14,556 vs 4,657,645)**.
  - 2/4/8-needle scaling run (2026-04-19): same harness, 25 stratified probes per variant, `openai/mrcr` parquet source. Ordinal engram holds **sim ≥ 0.912 with 72-96% pass** across every variant at the constant **99.7% (320×) prompt-token reduction**. Opus + ordinal engram beats Opus + full_context on the matched set for both 4-needle (0.975 vs 0.944) and 8-needle (0.979 vs 0.936); 2-needle is saturated (both arms ≥0.955).
  - New MRCR engram expert `mrcr_benchmark_methodologist` registered via `create_expert` (auto-classified under `information_retrieval` → `ai_and_knowledge`, 0.796 confidence). Seeded with 6 linked memories documenting probe grammar, ingest pattern, retrieval policy, Claude CLI driver, scoring, and the context-ceiling regime finding.
  - Artifact filenames now include the engram mode: `{dataset}-mrcr-{provider}-{model}-{mode}.json`.
  - 12 new probe-parser unit tests.
- **MRCR v2 (8-needle) long-context benchmark**: A/B harness that drives the Claude Code CLI (`claude -p`) via the user's subscription — no Anthropic API key required.
  - `run_mrcr_benchmark` runs two arms on the same probes: `full_context` (entire conversation in prompt) vs. `engram_retrieval` (hybrid BM25+vector search returns top-K chunks).
  - `compare_mrcr_artifacts` reports per-arm similarity / pass-rate deltas and the change in prompt-token reduction ratio.
  - Scoring uses local `bge-micro-v2` cosine similarity — matches the MRCR paper's metric and stays API-cost-free.
  - New `claude-cli` provider in `AgentOutcomeModelClientFactory` shells out to `claude -p --model <name>` with prompts piped over stdin (bypasses shell-argument-length limits on 128K contexts).
  - Dataset loader + HF download recipe in `benchmarks/datasets/mrcr-v2/README.md` (dataset is gitignored).
  - Methodology, usage, and interpretation guide in `docs/benchmarks-mrcr.md`.

### Fixed
- **`publish-nuget.ps1` test filter** now excludes `LiveBenchmark` and `T2Benchmark` traits alongside `MSA`, so live-Ollama benchmark tests (`DeepSeekBenchmarkRun`, `ReasoningBenchmarkRun`, `T2LiveBenchmarkRun`, `T2BenchmarkRun`) no longer run on every publish and hit 120 s HttpClient timeouts.

## [0.7.0] - 2026-04-16

### Added
- **Memory Graph Visualizer**: `get_graph_snapshot` MCP tool returns a `GraphSnapshot` with nodes, typed edges, cluster memberships, and corpus stats. Companion D3.js viewer (`visualization/memory-graph.html`) renders a force-directed graph with Obsidian/Nexus dark aesthetic: gold entry points, cyan LTM, teal STM, gray archived.
- **Visualizer Features**: Zoom 0.01–6×, right-click drag to rotate, filter pills (STM / LTM / Archived / Connected-only), fractal density overlay below zoom k=0.20 (leaf-only rendering anchored to node bounding box), search bar with live node highlighting (matches pulse gold, non-matches dim to 4%), ›/‹ buttons + Enter/Shift+Enter to cycle through results with a 1/N counter, Esc or × to clear.
- `get_graph_snapshot` registered in `full` profile only (`VisualizationTools.cs`). 10 new unit tests.
- Benchmark suite expanded to 34 result files across 12 datasets in `benchmarks/2026-04-16/`.

### Changed
- **Architecture Cleanup**: Removed 12 dead-code and bug items — duplicate `CosineSimilarity` / `DotProduct` overloads on `VectorMath`, stale `ScaleTunedTests.cs` test infrastructure, obsolete lifecycle fields (`_lastDecay`, `_lastSave` serialization remnants), and corrected `DeleteMemory` method signature mismatch.
- Total tests: **850** (up from 842). All 850 pass across net8.0.

## [0.6.1] - 2026-03-31

### Fixed
- **SQLite Multi-Instance Lock Contention**: Added `PRAGMA busy_timeout=5000` to `SqliteStorageProvider.OpenConnection()` and `DeleteNamespaceAsync()`. When multiple MCP server instances share the same `memory.db`, the second instance's schema initialization DDL would fail with `SQLITE_BUSY` after a 30-second hang because the first instance held the WAL write lock. SQLite now retries for up to 5 seconds on lock contention instead of immediately failing. Also fixed `DeleteNamespaceAsync()` bypassing `OpenConnection()` and missing the `busy_timeout` and `synchronous=NORMAL` pragmas.

## [0.6.0] - 2026-03-31

### Added
- **Cluster-Aware MMR Diversity Reranking**: New `DiversityReranker` applies Maximal Marginal Relevance with cluster and category penalties to spread search results across sub-topics. Activated via `diversity: true` on `search_memory`. Configurable lambda trade-off (0.0 = pure diversity, 1.0 = pure relevance, default 0.5).
- **GitHub Actions CI/CD**: `ci.yml` pipeline on push/PR to main — builds, tests (excluding MSA), and packs NuGet on all 3 TFMs. `benchmark.yml` runs nightly MSA benchmarks at 6 AM UTC with manual dispatch.
- **`disambiguation-v1` benchmark**: Dense-domain disambiguation dataset — 4 clusters × 5 seeds + 4 distractors, 10 queries (broad/cross-cluster/narrow) measuring result diversity across semantically dense namespaces.
- **Spreading Activation Service**: Collins & Loftus spreading activation model for graph-coupled energy transfer with depth-3 recursive propagation and cluster-based pre-warming.
- **SLM Synthesis Engine**: Map-reduce synthesis via Ollama for dense reasoning over large memory sets without expanding context windows.
- **`get_context_block` tool**: Prompt-cache-aware context assembly for LLM consumption.
- **`synthesize_memories` tool**: Map-reduce synthesis via Ollama for dense reasoning over large memory sets.
- **`physics-v1` in MSA benchmark suite**: 4 modes (vector, vector_rerank, hybrid, hybrid_rerank) added to nightly MSA benchmarks.
- 108 new tests (total: 842 across 3 frameworks, up from 734).
- Total tools: 52 (up from 50).

### Changed
- **ONNX Concurrent Inference**: Removed `SemaphoreSlim(1,1)` bottleneck. All scratch buffers (input IDs, attention mask, token type IDs, shape) moved to per-call `ArrayPool` allocations. `InferenceSession.Run()` now runs fully concurrent.
- **Namespace-Partitioned Locking**: `NamespaceStore` converted from `Dictionary` to `ConcurrentDictionary` for all 4 internal collections. Per-namespace load locks with double-check pattern for safe lazy loading. `CognitiveIndex` read paths switched from `UpgradeableReadLock` (1 thread) to `ReadLock` (N concurrent threads) at 10 call sites.
- **Expert System Consolidation**: Linked 6 orphaned high-use experts to domain tree parent nodes. Deleted 36 dead/duplicate expert entries. Broadened `information_retrieval` branch node persona description for improved hierarchical routing.
- **MCP Tool Test Coverage**: 8 previously untested tool classes now have dedicated test files (AdminTools, BenchmarkTools, ClusterTools, GraphTools, IntelligenceTools, LifecycleTools, MultiAgentTools, SynthesisTools) with 85 new tests.

## [0.5.5] - 2026-03-25

### Added
- **HNSW Graph Persistence**: HNSW indices are now serialized to disk as topology-only snapshots (`{ns}.hnsw.json` for JSON backend, `global_data` for SQLite). Vectors are reconstructed from namespace entries on load, avoiding the O(N log N) rebuild cost on cold start for namespaces with 200+ entries. Snapshots are saved alongside namespace data and cleaned up on namespace deletion.
- **`store_batch` tool**: Bulk-store multiple entries in a single write-lock. Faster than repeated `store_memory` calls for batch imports. Each entry gets contextual prefix embedding. Returns stored count and duplicate warnings.
- **Storage Version Validation (JSON)**: `PersistenceManager` now validates `storageVersion` on load — rejects files from newer server versions (forward-compatibility guard), runs sequential migrations for older versions. Follows the same `RunMigrations()` pattern as `SqliteStorageProvider`. Current version bumped to v2.
- **`ToolError` structured error type**: Standard `{ status, error }` record for consistent MCP tool error responses. `FromException()` hides internal stack traces.
- **BM25 Semantic Gate**: Hybrid search now gates BM25 candidates through semantic similarity before RRF fusion, eliminating noise from keyword-only matches that are semantically irrelevant.
- **52-combination Regression Baseline**: Theory test covering all 13 IR benchmark datasets x 4 search modes (vector, hybrid, vector_rerank, hybrid_rerank) with minimum thresholds for Recall@K >= 0.20, MRR >= 0.20, nDCG@K >= 0.15.
- 7 new IR benchmark datasets: ambiguity-v1, distractor-v1, specificity-v1, scale-v1, compound-v1, contamination-v1, cluster-summary-v1.
- 125 new tests (total: 734 across 3 frameworks, up from 609).

### Changed
- **Tool Description Audit**: All 50 MCP tool descriptions rewritten for better LLM routing — shorter top-level descriptions (under 200 chars), "when to use" guidance, routing hints between similar tools (e.g., `remember` vs `store_memory`, `recall` vs `search_memory`).
- **Scale Recall Tuning**: Hybrid search cascade and RRF fusion parameters retuned for improved recall on large namespaces (500+ entries).
- Fixed stack trace leak in `ClusterTools.CreateCluster` error responses.
- Total tools: 50 (up from 49).

## [0.5.4] - 2026-03-22

### Added
- **Porter Stemming for BM25**: Lightweight Porter stemmer (steps 1-3) normalizes morphological variants in the BM25 tokenizer. "encrypting", "encryption", and "encrypted" all stem to "encrypt", dramatically improving keyword recall. Handles plurals (-s/-es/-ies), verb forms (-ed/-ing), and derivational suffixes (-tion/-ation/-ize/-ness/-ful/-ive/-al).
- **Expanded Synonym Maps**: 60+ new synonym mappings covering security (encrypt→TLS/cipher), ML (sequence→RNN/LSTM), systems (monitoring→observability/Prometheus), networking (protocol→HTTP/TCP), data/storage (cache→Redis/CDN), and general CS vocabulary bridges. Corresponding reverse maps added to DocumentEnricher for bidirectional vocabulary bridging.
- **Two-Stage Cascade Retrieval**: For namespaces ≥50 entries, hybrid search switches from parallel RRF fusion to cascade mode — BM25 boosts vector results (up to 15%) instead of introducing new candidates, eliminating BM25 noise at scale. Smaller namespaces retain full RRF fusion.
- **Auto-PRF Engagement**: Pseudo-Relevance Feedback automatically activates when hybrid search top score is low (<0.015 RRF). Extracts key terms from initial results and re-searches for improved recall. Only used if PRF improves the top score.
- **Category-Aware Score Boost**: 8% score boost when query tokens overlap with entry categories, improving disambiguation at scale (e.g., "security" query boosts entries categorized as "security").
- 19 new tests (Porter stemmer, BM25 stemming integration, expanded synonyms, expanded enrichment, category boost, auto-PRF).
- Total tests: 609 (up from 590).

## [0.5.3] - 2026-03-22

### Added
- **Adaptive RRF Fusion**: Confidence-gated hybrid search — dynamically adjusts RRF k parameter based on vector search confidence. High confidence (>0.70) suppresses BM25 noise, low confidence (<0.50) amplifies BM25 rescue. Eliminates hybrid regression on clean vector matches.
- **Synonym Expansion Layer**: Query-time domain synonym mapping bridges vocabulary gaps between colloquial user queries and technical terminology. Directly fixes rw-q15 and c-q07 semantic gaps (e.g., "maintenance cleanup" → "accretion decay collapse").
- **Document Enrichment**: Auto-generates keyword aliases at store time using reverse synonym mapping. BM25 indexes both entry text and enrichment keywords for improved recall on informal queries.
- **Auto-Escalation Search**: Vector-only searches with low confidence (top score <0.50) automatically escalate to hybrid+synonym search, picking the best strategy without caller configuration.
- **`Keywords` field on `CognitiveEntry`**: Searchable keyword aliases for document enrichment. BM25 indexes keywords alongside main text. Auto-populated on upsert via `DocumentEnricher`.
- 21 new retrieval improvement tests (synonym expansion, document enrichment, adaptive RRF, auto-escalation, Keywords integration).
- Total tests: 590 (up from 569).

## [0.5.2] - 2026-03-22

### Added
- **Multi-Agent Memory Sharing (Phase 6)**: Cross-agent namespace sharing with permission control. Set `AGENT_ID` env var per agent instance to enable namespace ownership and access control.
- **`cross_search` tool**: Search across multiple namespaces in a single call with Reciprocal Rank Fusion (RRF) merge. Results annotated with source namespace. Supports hybrid search and reranking.
- **`share_namespace` tool**: Grant another agent read or write access to a namespace you own.
- **`unshare_namespace` tool**: Revoke an agent's access to a namespace you own.
- **`list_shared` tool**: List all namespaces shared with the current agent.
- **`whoami` tool**: Return current agent identity and accessible namespaces summary.
- **`NamespaceRegistry` service**: Manages namespace ownership and sharing permissions. Stores permission data in `_system_sharing` namespace. Backward compatible — default agent has unrestricted access.
- **`SearchMultiple` method**: Cross-namespace search with per-namespace retrieval and RRF fusion in `CognitiveIndex`.
- 26 new multi-agent tests (registry permissions, cross-search, sharing/unsharing, agent identity, backward compatibility).
- Total tools: 49 (up from 44). Multi-agent tools included in all tool profiles.

## [0.5.1] - 2026-03-21

### Added
- **Hierarchical Expert Routing (HMoE)**: 3-level domain tree (root → branch → leaf) with coarse-to-fine semantic routing via cosine similarity. Supports 2-level and 3-level trees with automatic flat fallback. Zero LLM API calls — all routing uses local ONNX embeddings + SIMD dot products.
- **`get_domain_tree` tool**: Inspect the full expert hierarchy showing root domains, branches, and leaf experts.
- **`purge_debates` tool**: Clean up stale `active-debate-*` namespaces older than a configurable age with dry-run support.
- **Namespace cleanup infrastructure**: `DeleteNamespaceAsync` with cascade removal of entries, graph edges, and cluster memberships across JSON and SQLite backends.
- **`create_expert` enhancements**: `level` parameter (`root`, `branch`, `leaf`) and `parentNodeId` for hierarchical tree construction. **Auto-classification**: when `parentNodeId` is omitted for leaf experts, the system automatically scores the persona against all root and branch nodes and places the expert into the best-matching domain (`auto_linked` >= 0.82, `suggested` 0.60–0.82, `unclassified` < 0.60). Placement result is included in the response.
- **`dispatch_task` enhancements**: `hierarchical` parameter for tree-walk routing through domain nodes.
- **`link_to_parent` tool**: Link existing leaf experts to a parent node in the domain tree.
- 49 new tests (27 hierarchical routing + 9 auto-classification + 13 namespace cleanup), bringing total to 534.

## [0.4.1] - 2026-03-10

### Changed
- **NuGet Build Optimizations**: Embedded debug `.pdb` symbols internally inside the distributed DLL.
- **Embedded Sources**: Added embedded source link mapping so consuming code can cleanly step into `McpEngramMemory.Core` logic directly during debug sessions, offering an unparalleled developer experience.

## [0.4.0] - 2026-03-10

### Added
- **McpEngramMemory.Core NuGet Package**: Split the core memory engine into an independent `net8.0` library, extractable via the `McpEngramMemory.Core.csproj` target build. Allows consumers to integrate the vector index natively in-process without relying on MCP RPC endpoints.

## [0.3.0] - 2026-03-09

### Added
- **Tool profiles**: `MEMORY_TOOL_PROFILE` environment variable to control which tools are exposed (`minimal`, `standard`, `full`). Default: `full` for backward compatibility.
- **Docker support**: Dockerfile for containerized deployment.
- **Examples directory**: Ready-to-use MCP config files for Claude Code, VS Code/Copilot, Gemini CLI, and Codex.
- **Architecture diagram**: Mermaid diagram in README showing system layers.
- **Quickstart section**: 30-second setup guide at the top of README.
- This CHANGELOG.

## [0.2.0] - 2026-03-09

### Added
- Expert routing with `dispatch_task` and `create_expert` tools
- Debate workflow with `consult_expert_panel`, `map_debate_graph`, `resolve_debate`
- Intelligence tools: `detect_duplicates`, `find_contradictions`, `merge_memories`
- Reversible cluster collapse with `uncollapse_cluster` and `list_collapse_history`
- SQLite storage backend (`MEMORY_STORAGE=sqlite`)
- Memory limits via `MEMORY_MAX_NAMESPACE_SIZE` and `MEMORY_MAX_TOTAL_COUNT`
- Per-namespace decay configuration with `configure_decay`
- NuGet package `McpEngramMemory.Core` v0.2.0

### Changed
- Architecture decomposition: CognitiveIndex refactored to thin facade delegating to stateless engines
- Vector serialization switched to Base64 (60% disk reduction), with backward-compatible JSON array reading

## [0.1.0] - Initial Release

### Added
- Core memory storage with namespace isolation
- Vector search with cosine similarity (k-NN)
- Hybrid search: BM25 + vector via Reciprocal Rank Fusion
- Token-overlap reranking
- Knowledge graph with directed edges and BFS traversal
- Semantic clustering with DBSCAN-based accretion scanning
- Memory lifecycle management (STM → LTM → archived)
- Activation energy decay with background service
- Physics-based gravitational re-ranking
- Int8 scalar quantization with SIMD acceleration
- Two-stage search pipeline (Int8 screening → FP32 reranking)
- JSON file persistence with debounced writes and SHA-256 checksums
- IR quality benchmarks (Recall@K, Precision@K, MRR, nDCG@K)
- Operational metrics with P50/P95/P99 percentiles
- 397 test cases across 26 test files
