# B1 — Cold-Start Self-Referential Benchmark: Task Curation

**Status**: Draft — v1.1 planning  
**Benchmark name**: `self-referential-cold-start-v1`  
**Benchmark protocol summary**: Phase 1 (priming) — agent reads a transcript and stores memories via Engram. Phase 2 (cold) — fresh agent process answers a single `coldPrompt` under two conditions: `no_memory` (vanilla) vs `full_engram` (Engram available). Both arms are scored against `goldRubric`. Lift on `full_engram` demonstrates the value of persistent memory.

---

## Task Escrow

> **How to prevent post-hoc tuning**: Before any benchmark run, record the git commit hash of the manifest file and pin it in the run metadata (e.g., `"manifestCommit": "abc1234"`). Any modification to `manifest.json.draft` after a run has started invalidates that run's results. The benchmark harness MUST read the manifest from the pinned commit, not HEAD. Baseline scores for the `no_memory` arm must be collected in the same run batch as `full_engram` scores — never retrofit a new baseline against an existing memory-arm result. Treat the manifest commit hash as the escrow seal: if the task changes, the run number increments and both arms re-run from scratch.

---

## Task Mix

| # | Task ID | Type | Why It Is Contamination-Resistant |
|---|---------|------|----------------------------------|
| 1 | `tool-profile-stale-comment` | convention | Program.cs comment says "55 tools" but actual count is 65 — divergence is undocumented and invisible to training data |
| 2 | `reflect-id-generation` | internals | `reflect` auto-generates IDs using `retro-{UTC-date}-{topic}` — a non-obvious contract with LTM default |
| 3 | `flush-debounce-race` | bug-fix | Root cause of PersistenceManager teardown race (`_inFlightWrites` counter + `ManualResetEventSlim`) is repo-specific |
| 4 | `nslock-dispose-ordering` | bug-fix | Volatile `_disposed` flag must be set BEFORE loop tears down `ReaderWriterLockSlim` instances — subtle ordering invariant |
| 5 | `diffusion-kernel-rename` | decision | `GraphLaplacianSpine` → `MemoryDiffusionKernel` rename rationale ("behavior, not metaphor") is an internal decision |
| 6 | `spectral-auto-mode-heuristic` | internals | `InferSpectralMode` word-count rule (≥5 words → Specific; digit/quote → Specific; else → Broad) is a local in-code heuristic |
| 7 | `msa-regression-apples-to-oranges` | bug-fix | MSA regression test compared phi3.5 baseline vs qwen2.5 candidates — the fix derives model from baseline `model` field |

---

## Task 1 — Tool Profile Comment vs Actual Count

**taskId**: `tool-profile-stale-comment`  
**Tags**: `convention`

### Why it is contamination-resistant

Program.cs contains inline comments claiming "minimal → 16 tools, standard → 35 tools, full → 55 tools". The actual live counts documented in `docs/services.md` and the README badge are 16 / 41 / 65. The discrepancy between the code comments and the true counts is a repo-internal inconsistency not derivable from any public documentation or model training. An agent who guesses "65 total" from the README but doesn't know the stale comment exists (or doesn't know that `standard` is documented as 41 in services.md while the comment says 35) cannot fully answer the review task correctly.

### primingTranscript

```
Session: Code review of Program.cs tool profile wiring
Date: 2026-04-29

Dev: We audited the tool profile counts today. The inline comments in 
Program.cs at the tool profile block (around line 105-108) still say:
  "minimal → 16 tools, standard → 35 tools, full → 55 tools"
But those are stale. After adding SpectralRetrievalTools, MemoryDiffusionTools,
and VisualizationTools in v0.9.0, the real counts are now:
  minimal  → 16 tools  (CoreMemoryTools + AdminTools + CompositeTools + MultiAgentTools)
  standard → 41 tools  (adds GraphTools, ClusterTools, LifecycleTools, IntelligenceTools, 
                         MemoryDiffusionTools, SpectralRetrievalTools)
  full     → 65 tools  (adds AccretionTools, BenchmarkTools, MrcrBenchmarkTools, 
                         DebateTools, MaintenanceTools, ExpertTools, SynthesisTools,
                         VisualizationTools)

We did NOT fix the comments yet. This is a known TODO. The README badge and 
docs/services.md are authoritative; the Program.cs comment block is stale.
The MEMORY_TOOL_PROFILE env var still works correctly — only the comment 
counts are wrong.

Decision: leave the stale comments for now, fix in a dedicated hygiene commit. 
Do not update services.md or README to match the comment — the comment is wrong,
not the docs.
```

### coldPrompt

```
You are reviewing a PR that adds a new MCP tool class `SpreadingActivationTools` 
to the `full` profile only in Program.cs. The author's PR description says: 
"This keeps the minimal/standard/full tool counts accurate per the existing 
Program.cs comments."

Review this claim. Are the existing Program.cs comments on tool counts accurate? 
What are the actual tool counts per profile? If the comments are wrong, what 
should the PR author do — fix the comments, fix the docs, or both?

Relevant code (excerpt from Program.cs, around line 105):
  // "minimal"  → 16 tools: core CRUD + admin + composite + multi-agent
  // "standard" → 35 tools: minimal + graph, lifecycle, clusters, intelligence
  // "full"     → 55 tools: everything (default for backward compatibility)
```

### goldRubric

1. Agent identifies that the Program.cs comments (16/35/55) are stale and do not match the actual counts.
2. Agent states the correct counts: minimal=16, standard=41, full=65 (or provides evidence that these are the authoritative figures from docs/services.md or README).
3. Agent recommends fixing the Program.cs comments to match the actual counts, NOT updating the docs to match the stale comment.
4. Agent correctly identifies the discrepancy source: v0.9.0 additions (SpectralRetrievalTools, MemoryDiffusionTools, etc.) were not reflected in the comment.
5. Agent does NOT suggest the PR author's premise ("per the existing comments") is valid.

### expectedMemoryIds

- `tool-profile-count-discrepancy` — stored in priming with the actual counts and stale-comment context
- `program-cs-tool-profile-wiring` — stored with the minimal/standard/full class-to-profile mapping

**Rationale**: These IDs would be natural outputs of any agent who read the priming transcript and stored the key fact (stale comment vs. live counts) using `remember`.

### Estimated token cost per arm

- no_memory: ~800 tokens (prompt + response)
- full_engram: ~1,200 tokens (prompt + memory retrieval + response)

---

## Task 2 — `reflect` ID Generation Contract

**taskId**: `reflect-id-generation`  
**Tags**: `internals`

### Why it is contamination-resistant

The `reflect` MCP tool auto-generates its memory ID using the pattern `retro-{DateTimeOffset.UtcNow:yyyy-MM-dd}-{topic}` and always stores as `lifecycleState: "ltm"` (not "stm"). Neither of these behaviors appears in any README or public doc in a searchable form — the lifecycle default is buried in the implementation. An agent implementing a feature that calls `reflect` could easily assume IDs are caller-provided or that the lifecycle defaults to "stm". The 0.92-similarity duplicate guard (same topic searched at category:"lesson") is a further internal detail.

### primingTranscript

```
Session: Implementing reflect-based session logging for a new CI integration
Date: 2026-04-22

Dev: I just read through CompositeTools.cs — the reflect tool has some 
non-obvious behaviors we need to document for the CI harness.

1. ID auto-generation: reflect does NOT accept a caller-provided ID.
   It generates the ID internally as:
     id = $"retro-{DateTimeOffset.UtcNow:yyyy-MM-dd}-{topic}"
   So if topic="lock-debugging", the stored ID will be something like
   "retro-2026-04-22-lock-debugging". If you call reflect twice on the
   same day with the same topic, the second call will get a near-duplicate
   warning and abort (score threshold 0.92 on existing "lesson" entries).

2. Lifecycle state: reflect ALWAYS stores as "ltm", regardless of the 
   lifecycleState parameter — there is no such parameter on reflect.
   This is by design: retrospectives are long-lived knowledge. Do NOT 
   try to store a reflect entry as "stm".

3. relatedIds: these are linked with "elaborates" edge type (not "similar_to").
   Auto-linked semantically-similar entries get "cross_reference" edges instead.

4. The duplicate guard: reflect does a category-filtered search (category:"lesson")
   at minScore 0.85, and aborts (status="duplicate_warning") if any existing 
   entry scores ≥ 0.92. The caller gets the conflicting ID back in the message.

We need to make sure our CI harness uses a unique topic suffix (e.g. include 
a run ID) to avoid duplicate_warning on repeated runs.
```

### coldPrompt

```
You are implementing a CI integration that calls the Engram `reflect` tool at 
the end of each build to store build lessons. You need to write a helper function 
that:
  1. Calls reflect with a consistent topic name (e.g. "ci-build-outcome")
  2. Retrieves the stored memory by its ID to verify it was saved
  3. Handles the case where a reflection on the same topic already exists today

Write pseudocode (or real C#) for this helper. Be specific about:
  - What ID will the stored memory have?
  - What lifecycle state will it be in?
  - What happens if you call this twice in the same day with the same topic?
  - How would you handle that duplicate scenario in CI?
```

### goldRubric

1. Agent correctly states that the ID will be `retro-{today's UTC date}-ci-build-outcome` (auto-generated, not caller-provided).
2. Agent correctly states the lifecycle state is always `ltm` (not stm, not configurable via reflect).
3. Agent correctly describes the duplicate behavior: second call same day same topic → `status="duplicate_warning"`, returns the existing entry's ID.
4. Agent provides a concrete CI strategy (e.g., unique topic suffix with run ID, or check-before-call) that avoids duplicate_warning.
5. Agent does NOT claim that reflect accepts an `id` parameter or a `lifecycleState` parameter.

### expectedMemoryIds

- `reflect-id-generation-contract` — stored from priming: ID format, LTM-always behavior
- `reflect-duplicate-guard` — stored from priming: 0.92 threshold, "lesson" category filter, duplicate_warning status

### Estimated token cost per arm

- no_memory: ~900 tokens
- full_engram: ~1,400 tokens

---

## Task 3 — PersistenceManager Flush() Debounce Race

**taskId**: `flush-debounce-race`  
**Tags**: `bug-fix`

### Why it is contamination-resistant

The root cause of the teardown race in `PersistenceManager.Flush()` is a non-obvious concurrency pattern: debounced timer callbacks capture a data provider, exit the `_timerLock`, and run file I/O outside the lock. `Flush()` drained all _pending_ providers but had no way to know a callback had already escaped the lock and was mid-write. The fix requires incrementing `_inFlightWrites` inside the timer lock (before exiting it) so `Flush()` can observe it. This ordering invariant — increment must happen while holding the same lock that Flush inspects — is not derivable from any public C# concurrency documentation; it is specific to this codebase's debounce pattern.

### primingTranscript

```
Session: Debugging flaky test teardown — "Directory not empty" error
Date: 2026-04-29

Dev: We had a persistent flaky test failure: after Flush(), our tests 
deleted the temp data directory but got "DirectoryNotFoundException: 
Directory not empty" sporadically. Root cause analysis:

THE BUG: PersistenceManager.Flush() correctly drains all pending timer 
callbacks by locking _timerLock, capturing and disposing each timer, then 
calling WriteX synchronously. BUT — the debounced callback for a namespace 
save has this flow:
  1. Lock _timerLock → capture provider → dispose timer → remove from dict
  2. EXIT lock
  3. Call WriteNamespace() ← file I/O happens outside the lock

If step 1-2 happened just BEFORE Flush() locked, Flush() would see the dict 
empty (timer already removed), declare "nothing pending", and return. 
Meanwhile, step 3 was still running — the WriteNamespace call was in flight. 
Test teardown then deleted the directory while that write was happening.

THE FIX: Added _inFlightWrites (int) and _writesIdle (ManualResetEventSlim).
The critical constraint: BeginTrackedWrite() (which increments _inFlightWrites 
and resets _writesIdle) MUST be called while still holding _timerLock. If you 
call it after exiting the lock, you can race Flush() — Flush() could pass 
through _timerLock empty, then observe _writesIdle already set, and return 
before the increment happens. The fix appended _writesIdle.Wait() at the 
end of Flush() to block until all in-flight writes complete.

The fix was applied identically to ScheduleSave, ScheduleSaveGlobalEdges,
ScheduleSaveClusters, ScheduleSaveCollapseHistory, and ScheduleSaveDecayConfigs
— all 5 debounced code paths.
```

### coldPrompt

```
A colleague is adding a new debounced save path to PersistenceManager for 
a new "search_index_snapshots" file. They've written this code:

  public void ScheduleSaveSearchIndex(Func<SearchIndexData> dataProvider)
  {
      lock (_timerLock)
      {
          _pendingSearchIndexTimer?.Dispose();
          _pendingSearchIndexProvider = dataProvider;
          _pendingSearchIndexTimer = new Timer(_ =>
          {
              Func<SearchIndexData>? provider;
              lock (_timerLock)
              {
                  provider = _pendingSearchIndexProvider;
                  _pendingSearchIndexProvider = null;
                  _pendingSearchIndexTimer?.Dispose();
                  _pendingSearchIndexTimer = null;
              }
              if (provider is not null)
                  WriteSearchIndex(provider);   // ← file I/O outside lock
          }, null, _debounceDelay, Timeout.InfiniteTimeSpan);
      }
  }

Review this implementation. Is it correct with respect to Flush()? If not, 
describe the exact race condition and provide the corrected code.
```

### goldRubric

1. Agent identifies the race: if the timer callback exits `_timerLock` before Flush() enters it, Flush() sees the pending dict empty and returns, while WriteSearchIndex() is still running.
2. Agent correctly states the fix: call `BeginTrackedWrite()` (or equivalent increment of `_inFlightWrites`) INSIDE the timer lock, before exiting it.
3. Agent provides corrected code that adds `BeginTrackedWrite()` (or equivalent) before the lock exit AND wraps `WriteSearchIndex(provider)` in `RunWriteAndRelease()` (or equivalent try/finally decrement).
4. Agent correctly explains why the increment must be inside the lock (ordering invariant: Flush reads _writesIdle after taking the same lock, so the increment must be observable before Flush exits the lock).
5. Agent does NOT suggest a simpler but incorrect fix like calling BeginTrackedWrite() after exiting the lock or using a separate synchronization mechanism.

### expectedMemoryIds

- `flush-debounce-race-root-cause` — the _inFlightWrites/BeginTrackedWrite ordering invariant
- `persistence-manager-debounce-pattern` — the five debounced code paths and their common fix shape

### Estimated token cost per arm

- no_memory: ~1,100 tokens
- full_engram: ~1,600 tokens

---

## Task 4 — NsLock Dispose Ordering Bug

**taskId**: `nslock-dispose-ordering`  
**Tags**: `bug-fix`

### Why it is contamination-resistant

The fix to the `CognitiveIndex.Dispose()` race is a specific ordering constraint: `_disposed = true` (volatile write) must happen BEFORE iterating `_nsLocks` and calling `Dispose()` on each `ReaderWriterLockSlim`. The reason is that a racing caller who has already fetched an `ReaderWriterLockSlim` reference via `NsLock(ns)` but not yet called `Enter*Lock` could proceed into a disposed lock if `_disposed` is set after the loop. This is a multi-step reasoning chain about lock lifecycle that requires knowing (a) ConcurrentDictionary.GetOrAdd can return a reference that survives the lock-table teardown, and (b) the volatile flag must be the first observable mutation. No public .NET documentation or training data describes this exact pattern in this codebase.

### primingTranscript

```
Session: PR #3 review — NsLock Dispose race
Date: 2026-04-21

Reviewer (code-reviewer agent): Found a HIGH severity issue in CognitiveIndex.Dispose().
The original code:
  public void Dispose()
  {
      foreach (var kv in _nsLocks)
          kv.Value.Dispose();
      _nsLocks.Clear();
  }
There was no _disposed flag at all. A thread that called NsLock("ns-a") and got 
a RWLS reference back could be preempted before calling EnterWriteLock(). If 
Dispose() ran in that window, the RWLS would be disposed, and EnterWriteLock() 
would throw ObjectDisposedException from deep inside the CLR.

THE FIX: Added `private volatile bool _disposed;`. In Dispose():
  if (_disposed) return;   // idempotent
  _disposed = true;        // BEFORE tearing down locks — critical ordering
  foreach (var kv in _nsLocks)
      kv.Value.Dispose();
  _nsLocks.Clear();

In NsLock():
  if (_disposed)
      throw new ObjectDisposedException(nameof(CognitiveIndex));
  return _nsLocks.GetOrAdd(ns, _ => new ReaderWriterLockSlim());

The volatile keyword is essential: without it, the CPU or JIT might reorder 
the _disposed write after the loop, eliminating the protection window. The 
volatile write creates a memory barrier so the flag is immediately visible 
to all threads that check it in NsLock.

The test Operations_AfterDispose_ThrowObjectDisposedException verifies that 
Upsert, CountInNamespace, Get, Delete, and GetAllInNamespace all throw 
ObjectDisposedException after Dispose(). Dispose_ReleasesAllPerNsLocks_NoThrow 
was updated to call Dispose() twice (idempotency check).
```

### coldPrompt

```
You are reviewing this proposed Dispose() implementation for a class that 
manages a ConcurrentDictionary of ReaderWriterLockSlim objects (one per 
namespace):

  private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> _nsLocks = new();
  private bool _disposed;

  private ReaderWriterLockSlim NsLock(string ns)
      => _nsLocks.GetOrAdd(ns, _ => new ReaderWriterLockSlim());

  public void Dispose()
  {
      foreach (var kv in _nsLocks)
          kv.Value.Dispose();
      _nsLocks.Clear();
      _disposed = true;   // set flag AFTER tearing down locks
  }

Identify all correctness issues with this implementation. Then provide the 
correct version with a brief explanation of each fix.
```

### goldRubric

1. Agent identifies that `_disposed = true` must be set BEFORE the foreach loop, not after, to prevent races with concurrent NsLock() callers.
2. Agent identifies that `_disposed` must be `volatile` (or use `Interlocked`/`Volatile.Write`) to ensure the write is immediately visible to other threads without CPU/JIT reordering.
3. Agent adds an early-return idempotency check (`if (_disposed) return;`) to make Dispose() safe to call multiple times.
4. Agent adds a `_disposed` check in `NsLock()` and throws `ObjectDisposedException` to prevent callers from racing into a disposed lock table.
5. Agent correctly explains the race window: NsLock() caller holds the RWLS reference but hasn't called Enter*Lock yet; Dispose() tears down the RWLS; caller proceeds into a disposed lock.

### expectedMemoryIds

- `nslock-dispose-race-root-cause` — volatile flag, ordering before teardown loop
- `nslocks-dispose-idempotent-contract` — double-dispose safety + NsLock guard

### Estimated token cost per arm

- no_memory: ~900 tokens
- full_engram: ~1,400 tokens

---

## Task 5 — `GraphLaplacianSpine` → `MemoryDiffusionKernel` Rename Decision

**taskId**: `diffusion-kernel-rename`  
**Tags**: `decision`

### Why it is contamination-resistant

This rename was an explicit architectural decision made in a specific commit with a documented rationale: "The 'spine' metaphor required readers to know graph spectral theory before they could understand what the class did. The 'memory diffusion kernel' name names the behavior." The full coordinate rename mapping (7 renamed identifiers, including MCP tool names that changed on the external surface) is repo-internal. No external source documents why the rename happened or what the old names were. The key non-obvious detail is that the MCP tool names `compute_laplacian_basis`, `laplacian_stats`, and `invalidate_laplacian` were renamed on the external API surface — a consumer who stored the old names in their config would have broken.

### primingTranscript

```
Session: Architecture review — memory-diffusion kernel naming
Date: 2026-04-26

Dev: We landed a coordinated rename in commit 64bfdc8. The full mapping:
  Classes/Records:
    GraphLaplacianSpine  → MemoryDiffusionKernel
    LaplacianBasis       → DiffusionBasis
    LaplacianStats       → DiffusionStats
    LaplacianTools       → MemoryDiffusionTools  (file deleted, new file created)
  
  MCP tool names (EXTERNAL API surface):
    compute_laplacian_basis → compute_diffusion_basis
    laplacian_stats         → diffusion_stats
    invalidate_laplacian    → invalidate_diffusion

RATIONALE (from commit message):
  "The 'spine' metaphor required readers to know graph spectral theory 
  before they could understand what the class did. The 'memory diffusion 
  kernel' name names the behavior — diffuses signals through the memory 
  graph — and pairs naturally with the cognitive-science framing 
  (spreading activation, heat-kernel propagation across associative networks)."

LifecycleEngine constructor parameter was also renamed: `spine` → `diffusion`.

The rename was safe because the Laplacian tools had only shipped in the 
PREVIOUS commit (cd3aa05), so no external consumers had taken a dependency 
on the old MCP tool names yet.

WHAT DID NOT CHANGE: the math. DiffusionBasis still stores Eigenvalues, 
Eigenvectors, and EdgeCount. The class doc explicitly bridges "diffusion 
kernel" back to "graph Laplacian eigenbasis" for math-aware readers.
PositiveRelations (parent_child, cross_reference, similar_to, elaborates, 
depends_on — contradicts excluded) unchanged.
```

### coldPrompt

```
A user filed an issue: "I configured my MCP client to call 
`compute_laplacian_basis` but I get 'tool not found'. I'm on v0.9.0.
Did this tool get removed?"

Write a response that:
1. Explains what happened and why
2. Provides the correct tool name they should use
3. Lists any other tool names that changed in the same rename
4. Explains the reasoning behind the rename (one sentence)
5. Notes what did NOT change (i.e., the underlying math/behavior)
```

### goldRubric

1. Agent correctly identifies that `compute_laplacian_basis` was renamed to `compute_diffusion_basis` (not removed).
2. Agent lists all three renamed MCP tools: `compute_laplacian_basis→compute_diffusion_basis`, `laplacian_stats→diffusion_stats`, `invalidate_laplacian→invalidate_diffusion`.
3. Agent states the rename rationale: "spine" metaphor required spectral theory knowledge; "diffusion kernel" names the behavior.
4. Agent correctly states the underlying math (eigenbasis, Laplacian, positive-relation edges) did NOT change.
5. Agent does NOT claim the tool was removed or deprecated; makes clear it was purely a rename.

### expectedMemoryIds

- `diffusion-kernel-rename-mapping` — the 7-identifier rename table and external API impact
- `diffusion-kernel-rename-rationale` — behavior-naming rationale and cognitive-science framing

### Estimated token cost per arm

- no_memory: ~700 tokens
- full_engram: ~1,100 tokens

---

## Task 6 — `InferSpectralMode` Word-Count Heuristic

**taskId**: `spectral-auto-mode-heuristic`  
**Tags**: `internals`

### Why it is contamination-resistant

The `InferSpectralMode` static method in `CompositeTools` is a local heuristic with specific numeric thresholds: ≥5 words OR presence of a digit OR a matched quoted phrase → `Specific` mode; otherwise → `Broad` mode. These exact numbers and the quoted-phrase detection logic (requires a matching closer, not just any quote character) are implementation details that an LLM cannot derive from public documentation. The method is intentionally "zero external calls" — it runs in microseconds and has no dependency on embeddings or LLMs. A developer implementing a new spectral mode or debugging why a query gets the "wrong" mode needs to know these exact rules.

### primingTranscript

```
Session: Debugging spectral recall behavior on short vs. long queries
Date: 2026-04-27

Dev: I traced through InferSpectralMode in CompositeTools.cs. Here's the 
exact decision logic (public static method, no external calls):

INPUTS: query string
OUTPUTS: SpectralRetrievalMode.Broad or SpectralRetrievalMode.Specific

RULES (applied in order):
1. Empty/whitespace query → Broad (degenerate case)
2. Scan for any digit (0-9) → if found, return Specific
3. Scan for a quote (single ' or double ") that has a matching closing 
   quote later in the string → if found, return Specific
   NOTE: a lone quote without a closer does NOT trigger this — the code 
   explicitly requires IndexOf(ch, i+1) > i.
4. Split on whitespace → count words → if word count >= 5, return Specific
5. Otherwise → return Broad

EXAMPLES:
  "memory consolidation"           → 2 words, no digit, no quote → Broad
  "auth flow"                      → 2 words → Broad
  "lock ordering fix in v0.8.1"    → digit "0" found → Specific
  "what is the best search method" → 7 words → Specific
  "fix 'the lock bug'"             → quoted phrase found → Specific
  "fix 'unclosed quote"            → lone quote, no closer → check words (4) → Broad

This is the ONLY place spectral mode is auto-inferred. If a caller passes 
spectralMode="broad" or "specific" explicitly, InferSpectralMode is never called.
If spectralMode="none" or unknown string, the spectral step is skipped entirely.
```

### coldPrompt

```
A user reports: "I'm calling recall with spectralMode='auto' and I'm getting 
different results than expected. For short queries like 'auth flow' I get 
cluster-boosted results (good). But for my query 'fix the auth bug' I'm also 
getting cluster-boosted results — I expected the high-pass filter since it's 
a more targeted query."

Explain exactly why 'fix the auth bug' gets Broad mode. Then explain what 
the user would need to change in their query to get Specific mode. 
Be specific about the rules that govern this decision.
```

### goldRubric

1. Agent correctly states "fix the auth bug" has 4 words, which is below the ≥5 word threshold, so it gets Broad mode.
2. Agent correctly states there are no digits or quoted phrases in "fix the auth bug", so those fast-path rules don't fire.
3. Agent correctly identifies at least two ways to get Specific mode: add a 5th word (e.g., "fix the auth session bug") OR include a digit (e.g., "fix auth bug in v2") OR use a quoted phrase (e.g., "fix 'auth bug'").
4. Agent correctly explains that spectralMode="auto" resolves by calling InferSpectralMode; Broad means cluster-dominance boost (low-pass), Specific means high-pass cluster-mean subtraction.
5. Agent does NOT claim the mode selection involves embeddings, LLM calls, or any external computation.

### expectedMemoryIds

- `infer-spectral-mode-rules` — the exact word-count/digit/quote rules with thresholds
- `spectral-mode-broad-vs-specific-semantics` — what Broad vs Specific actually does to scores

### Estimated token cost per arm

- no_memory: ~800 tokens
- full_engram: ~1,200 tokens

---

## Task 7 — MSA Regression Test Apples-to-Oranges Bug

**taskId**: `msa-regression-apples-to-oranges`  
**Tags**: `bug-fix`

### Why it is contamination-resistant

The `LiveAgentOutcomeRegressionTests` bug is highly specific: the test hard-coded the candidate glob pattern to `*-qwen2.5-7b.json` while the pinned baselines were all generated with `phi3.5:3.8b`. Every "regression" it reported was pure model-variance noise. The fix — read the baseline's `model` field, map `:` → `-` to construct the glob pattern, and also assert that the candidate model matches the baseline model — is a domain-specific fix that requires knowing (a) the baseline JSON format has a `model` field, (b) the colon-to-hyphen translation is necessary for filename matching, and (c) the expert panel guidance was "do NOT refresh the baseline to fit current output without a holdout split." This last point is a project-specific testing philosophy.

### primingTranscript

```
Session: MSA regression test was reporting false regressions for weeks
Date: 2026-04-22

Dev: Root cause identified in LiveAgentOutcomeRegressionTests.cs.

THE BUG: The test had a hard-coded candidate glob:
  string pattern = $"{datasetId}-live-agent-outcome-ollama-qwen2.5-7b.json";
But ALL three baseline files (agent-outcome-v1, agent-outcome-hard-v1, 
agent-outcome-repo-v1) were generated with phi3.5:3.8b. We were measuring:
  phi3.5 baseline score  vs.  qwen2.5-7b candidate score
Concrete false regressions observed in benchmarks/2026-04-17/:
  agent-outcome-v1:      phi3.5 full_engram 0.833 vs qwen2.5 0.767 → "6.67% regression"
  agent-outcome-hard-v1: phi3.5 full_engram 1.000 vs qwen2.5 0.833 → "16.67% regression"
  agent-outcome-repo-v1: phi3.5 full_engram 0.900 vs qwen2.5 0.900 → 0% (lucky)

THE FIX (commit 90beee7):
1. Read the baseline JSON's "model" field at test time.
2. Map ':' → '-' to build the glob: "phi3.5:3.8b" → "phi3.5-3.8b"
3. Also Assert.Equal(baselineModel, candidateModel) — apples-to-apples guarantee.
4. Include baseline model in failure messages for faster triage.

EXPERT PANEL GUIDANCE (engram expert panel, concurrent_dispatch_table_engineer):
"Do NOT refresh the baseline to fit current output without a holdout split — 
that creates sprint-over-sprint illusion of progress." We kept the original 
baselines; we fixed the comparison logic.

The test is now self-correcting: if someone regenerates baselines with a 
different model in the future, the glob auto-adapts.
```

### coldPrompt

```
You are reviewing a PR that adds a new benchmark dataset 
`agent-outcome-synthesis-v1` to LiveAgentOutcomeBenchmarkRunner. The 
author has also added a new baseline file at:
  benchmarks/baselines/agent-outcome-synthesis-v1-live-agent-outcome-ollama-phi3.5-3.8b.json

The PR description says: "I updated LiveAgentOutcomeRegressionTests to add 
this dataset. I hard-coded the candidate pattern to *-qwen2.5-7b.json since 
that's what we use for candidate runs."

Review this change. What is wrong with hard-coding the candidate pattern? 
What is the correct approach? What regression has this project had with 
this exact pattern of error before?
```

### goldRubric

1. Agent identifies that hard-coding the candidate model in the glob creates a cross-model comparison (phi3.5 baseline vs qwen2.5 candidates) which produces model-variance noise, not engine quality signal.
2. Agent describes the correct fix: derive the candidate glob from the baseline JSON's `model` field (map `:` → `-` for filename construction).
3. Agent correctly states this project has had this exact bug before (the MSA regression test was comparing phi3.5 baseline vs qwen2.5 candidates for weeks, producing false 16.67% "regressions").
4. Agent mentions or implies the expert panel / project philosophy: do not refresh baselines to match candidate output; fix the comparison.
5. Agent recommends adding an assertion that the candidate model matches the baseline model.

### expectedMemoryIds

- `msa-regression-apples-to-oranges-bug` — the cross-model comparison bug, specific numbers, fix shape
- `benchmark-baseline-refresh-policy` — expert panel guidance on not refreshing baselines

### Estimated token cost per arm

- no_memory: ~900 tokens
- full_engram: ~1,400 tokens

---

## Tasks Considered and Rejected

### Rejected: `deep-recall-resurrection-threshold`

**Considered**: Task asking about the `resurrectionThreshold=0.7f` parameter in `LifecycleEngine.DeepRecall()` — the fact that archived entries are auto-promoted back to STM if their score meets this threshold.

**Rejected because**: The `recall` tool description in core-10.md explicitly documents "falls back to deep_recall if top results are weak" and the README mentions resurrection. The threshold value (0.7f) is an implementation detail but the conceptual behavior is public enough that a careful reader of the docs could infer it. Risk of partial contamination from training on similar "LTM/STM/archived state machine" patterns in memory system literature.

### Rejected: `hnsw-two-stage-threshold`

**Considered**: Task asking when the HNSW index kicks in vs. the two-stage Int8 screening (≥200 entries for HNSW, ≥30 for Int8 screening). These thresholds are documented in `docs/services.md`.

**Rejected because**: The thresholds appear verbatim in services.md which is a public file. Not contamination-resistant enough — a no_memory agent who reads the docs URL or has services.md in training data could answer correctly.

### Rejected: `sqlite-wal-busy-timeout`

**Considered**: Task about the SQLite storage backend using `PRAGMA busy_timeout=5000` — the exact timeout value and why it enables multi-process safety.

**Rejected because**: While specific, the SQLite WAL + busy_timeout pattern is widely documented in SQLite literature. A general-knowledge LLM could plausibly produce the correct answer without Engram. Not sufficiently repo-specific.

### Rejected: `bge-micro-v2-dimensions`

**Considered**: Task about the embedding model (bge-micro-v2, 384 dimensions, bge-micro git revision 72908b7).

**Rejected because**: The model name and dimensions appear in the README and services.md. The git revision pinning is interesting but verifiable from the build targets — too close to public documentation.

---

## v1.1 Manifest Refinement (post-first-run)

### Dropped Tasks

- **`nslock-dispose-ordering`**: 100%-baseline no-headroom. Opus identifies the `volatile`/`_disposed`-before-teardown pattern from general .NET concurrency knowledge alone. The task tests well-known .NET best practice, not a repo-specific invariant.

- **`spectral-auto-mode-heuristic`**: 100%-baseline no-headroom. Opus guesses ≥5-word / digit / quoted-phrase rules cold, likely because word-count heuristics for query classification are a common NLP pattern. The InferSpectralMode logic is too close to generic NLP intuition.

- **`diffusion-kernel-rename`**: -40pp regression (engram retrieval pollution). The priming transcript is a dense rename table (7 identifiers × 2 names each = 14 facts). When retrieved, the memory likely introduced noise that confused the agent on the simple lookup. Simple rename tables are high contamination risk because retrieved context adds load without providing causal reasoning.

---

### New Tasks (v1.1)

| # | taskId | Tag | Predicted no_mem% | Contamination-resistance rationale |
|---|--------|-----|-------------------|-------------------------------------|
| 1 | `feedback-archived-resurrection-routing` | internals | 30–45% | Natural LLM default is "positive feedback on archived → back to STM". The actual routing is asymmetric: only clears stmThreshold (2.0) gets STM; below-threshold lands in LTM. This counterintuitive two-branch switch requires knowing the exact thresholds and the asymmetric code path. |
| 2 | `spreading-activation-contradicts-weight` | decision | 25–45% | Natural LLM default is "contradicts = 0 weight, no propagation". The codebase explicitly sets weight=0.3 with documented rationale (pre-warm contradictions as contextual context). Combined with the fan-out attenuation formula (sqrt division), this is a multi-step reasoning chain over two files (PhysicsEngine + SpreadingActivationService). |
| 3 | `spectral-decay-default-on-without-config` | internals | 20–40% | Natural LLM default is "no stored config → method parameter defaults → spectral off". The actual code: the `else` branch in the `useStoredConfig` path explicitly sets `useSpectral = _diffusion is not null` — spectral is ON by default when a kernel is available. This is explicitly commented as counterintuitive in the source code itself. The reasoning chain is: "no config" → falls through → kernel-available check → ON. |

---

### Tasks Considered and Rejected in v1.1 Brainstorm

- **`accretion-scan-ltm-only`**: DBSCAN scan only runs on `ltm` entries (not `stm`). Rejected because this is a simple lifecycle-filter fact, lookup-risk. A no_mem agent reading the function signature `var ltmEntries = allEntries.Where(e => e.LifecycleState == "ltm")` would likely guess correctly.

- **`diffusion-kernel-minimum-thresholds`**: Kernel bypasses for namespaces below 32 nodes or 8 edges. Rejected because these are numeric thresholds — lookup-contamination risk. The priming would need to state the exact numbers, which is what hurt diffusion-kernel-rename.

- **`auto-link-canonical-direction`**: AutoLinkScanner uses lexicographic ordering to pick edge direction. Rejected because lex-order canonicalization is a well-known engineering pattern (Opus knows this), so baseline would be too high.

- **`spectral-reranker-dense-output`**: SpectralRetrievalReranker produces dense output from a sparse signal — entries not in the upstream results can appear in the reranked output (Broad mode). This is interesting but the mechanism is described verbatim in the class-level XML doc comment, which is a public doc risk.

- **`consolidation-pass-skips-system-namespaces`**: RunConsolidationPass skips namespaces starting with `_`. Rejected because the `StartsWith('_')` check is a simple pattern — too likely to be guessed correctly cold (baseline ~70%+).

---

### Token Cost Estimates — New Tasks

| taskId | no_mem arm | full_engram arm |
|--------|-----------|-----------------|
| `feedback-archived-resurrection-routing` | ~800 tokens | ~1,300 tokens |
| `spreading-activation-contradicts-weight` | ~900 tokens | ~1,400 tokens |
| `spectral-decay-default-on-without-config` | ~850 tokens | ~1,350 tokens |

**Highest-confidence lift prediction**: `spectral-decay-default-on-without-config` is the task most likely to deliver ≥30pp lift. The "no stored config → spectral ON" behavior is explicitly noted as counterintuitive in the source code comment itself, the natural default assumption is definitively wrong ("no config = use method defaults = spectral off"), and the priming is tight (one key code block + the rationale). The fact is non-derivable from any public doc. Expected no_mem ~25%, full_engram ~70%+.
