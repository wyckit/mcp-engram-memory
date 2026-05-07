# A1 — Lock-Upgrade Audit

**Status:** SAFE — green light for Track A  
**Date:** 2026-05-06  
**Scope:** All four background-maintenance entry points in v1.0.0

---

## Executive Summary

The lock-upgrade pattern is **not present** in any of the four maintenance entry points. Every maintenance pass follows the correct snapshot-then-release discipline: `GetAllInNamespace` acquires a per-namespace read lock, materializes entries into a `List<T>`, releases the lock before returning, and only then does the caller iterate and invoke per-entry write methods (each of which acquires its own independent write lock). No code path holds a `ReaderWriterLockSlim` read lock while attempting to acquire a write lock on the same namespace. The residual risk is low and narrow: a future refactor that inlines the iteration inside the `EnterReadLock`/`ExitReadLock` block of `GetAllInNamespace`, or replaces the per-entry write calls with a bulk write under a held read lock, would re-introduce the hazard. The regression test suite at `tests/McpEngramMemory.Tests/Lifecycle/LockUpgradeRegressionTests.cs.draft` is designed to catch any such regression.

---

## Per-Method Analysis

### 1. `LifecycleEngine.RunDecayCycle`

**File:** `src/McpEngramMemory.Core/Services/Lifecycle/LifecycleEngine.cs`

| Step | Line | Lock state |
|------|------|------------|
| Snapshot acquired | 134 | `GetAllInNamespace(currentNs)` is called — internally enters read lock, snapshots to `List<CognitiveEntry>`, exits read lock, returns snapshot |
| Snapshot returned | 134 | **No lock held.** The returned `List<CognitiveEntry>` is a plain heap object. |
| Iteration over `nonSummary` | 145–156 (debt pass) | Lock-free: pure computation on snapshot data |
| Per-entry write | 192 | `_index.SetActivationEnergyAndState(entry.Id, newActivationEnergy, newState)` — resolves namespace lock-free via `_idToNamespace`, then independently acquires that namespace's write lock, writes, releases. |

**Verdict:** SAFE. The read lock is released at line 134 (inside `GetAllInNamespace`). The write calls at line 192 each acquire and release a fresh write lock with no competing read lock held.

---

### 2. `LifecycleEngine.RunConsolidationPass`

**File:** `src/McpEngramMemory.Core/Services/Lifecycle/LifecycleEngine.cs`

| Step | Line | Lock state |
|------|------|------------|
| Snapshot acquired | 265 | `GetAllInNamespace(currentNs)` — same contract: read lock → snapshot → release |
| Snapshot returned | 265 | **No lock held.** |
| Activation dict build | 287–289 | Lock-free: reads from snapshot |
| Spectral filter (diffusion) | 293–294 | Lock-free: pure math on in-memory dict |
| Per-entry write (STM→LTM) | 305 | `_index.SetLifecycleState(entry.Id, "ltm")` — resolves ns lock-free, acquires write lock, writes, releases |
| Per-entry write (LTM→archived) | 309 | `_index.SetLifecycleState(entry.Id, "archived")` — same pattern |

**Verdict:** SAFE. Both write calls (lines 305 and 309) acquire their own independent write locks, long after the snapshot read lock was released at line 265.

---

### 3. `AutoLinkScanner.Scan`

**File:** `src/McpEngramMemory.Core/Services/Graph/AutoLinkScanner.cs`

| Step | Line | Lock state |
|------|------|------------|
| Snapshot acquired | 59 | `_index.GetAllInNamespace(ns)` — read lock → snapshot → release |
| Snapshot returned | 59 | **No lock held.** |
| DuplicateDetector scan | 86 | Lock-free: pure cosine-similarity computation on in-memory vector data |
| Graph write | 110 | `_graph.AddEdge(...)` — uses `KnowledgeGraph`'s own lock, which is entirely separate from `CognitiveIndex`'s per-namespace RWLS |

**Verdict:** SAFE. After the snapshot at line 59, `AutoLinkScanner` never calls back into `CognitiveIndex` at all. All writes go through `KnowledgeGraph.AddEdge`, which holds only `KnowledgeGraph`'s internal lock — orthogonal to `CognitiveIndex`'s RWLS hierarchy.

---

### 4. `AccretionScanner.ScanNamespace`

**File:** `src/McpEngramMemory.Core/Services/Intelligence/AccretionScanner.cs`

| Step | Line | Lock state |
|------|------|------------|
| Index snapshot | 39 | `_index.GetAllInNamespace(ns)` — read lock → snapshot → release. Comment at line 39 explicitly documents this: "outside _lock — uses _index's own lock" |
| Snapshot returned | 39 | **No CognitiveIndex lock held.** |
| Dismissed-entry filter | 46–51 | Acquires `AccretionScanner._lock` (read) — this is a completely separate RWLS from CognitiveIndex. No CognitiveIndex lock is held concurrently. |
| DBSCAN | 54 | Lock-free: pure computation |
| Pending-collapse registration | 60–85 | Acquires `AccretionScanner._lock` (write). No CognitiveIndex lock held. |
| Auto-summarize path | 88–110 | Calls `clusters.CreateCluster` / `clusters.StoreSummary` — uses `ClusterManager`'s own lock, not CognitiveIndex's RWLS |

**Verdict:** SAFE. The code has two independent lock domains: `CognitiveIndex._nsLocks[ns]` (used only during the brief snapshot at line 39) and `AccretionScanner._lock` (used during pending-collapse mutation). They are never held simultaneously.

---

## Cross-Namespace Independence Verdict

**CONFIRMED INDEPENDENT.** The per-namespace RWLS architecture means operations on namespace A and namespace B never contend for the same lock object. Maintenance passes acquire only the lock for the namespace they are processing. Foreground writes to a different namespace acquire only that namespace's lock. The two lock objects are distinct `ReaderWriterLockSlim` instances stored in `CognitiveIndex._nsLocks` (a `ConcurrentDictionary<string, ReaderWriterLockSlim>`).

Specific evidence:
- `CognitiveIndex.NsLock(ns)` (line 79) performs a keyed lookup and creates a per-namespace instance if absent.
- No global or cross-namespace lock exists anywhere in `CognitiveIndex`.
- Cross-namespace reads (`GetAll`, `Count`, `GetStateCounts(null)`) are documented as lock-free via `ConcurrentDictionary` semantics.

---

## Same-Namespace Foreground-Write Behavior Verdict

**Serializes gracefully; does not stall pathologically.**

When a maintenance pass is running on namespace A, foreground writes to namespace A will contend for A's write lock. The RWLS write-lock protocol serializes them: each per-entry maintenance write (`SetActivationEnergyAndState`, `SetLifecycleState`) is brief (a dictionary lookup, a field mutation, a persistence schedule). Each maintenance write releases the lock before moving to the next entry. Foreground writes will therefore interleave with maintenance writes at per-entry granularity rather than being blocked for the full duration of the maintenance pass.

The critical point: the maintenance pass is **not** holding a read lock across its loop. If it were (the broken pattern), a foreground write attempt would deadlock because `ReaderWriterLockSlim` does not allow a read holder to acquire a write lock (and write-lock requests block behind existing readers while readers can keep re-entering, causing write starvation or deadlock depending on the fairness flag). Since the read lock is released before the loop begins, the maintenance pass and foreground writers simply take turns on each per-entry write — no starvation, no deadlock.

---

## Regression Test Design Rationale

The draft test at `tests/McpEngramMemory.Tests/Lifecycle/LockUpgradeRegressionTests.cs.draft` covers five scenarios:

1. **`ConsolidationPass_OnNsA_DoesNotBlockWritesToNsB`** — the primary cross-ns independence test. Seeds 500 entries in each namespace, spawns a maintenance task (5 consolidation passes on ns-a), 100 writers to ns-b (latency-measured), and 100 writers to ns-a. Asserts: each ns-b write completes in <500 ms; all writes land; total completes within 30 s. A lock-upgrade deadlock would cause the 30 s deadline to fire.

2. **`DecayCycle_OnNsA_DoesNotBlockWritesToNsB`** — same structure for `RunDecayCycle`.

3. **`AutoLinkScan_DoesNotBlockCognitiveIndexWrites`** — covers same-namespace concurrency for `AutoLinkScanner.Scan`, which uses a read lock only briefly for the snapshot.

4. **`AccretionScan_DoesNotBlockCognitiveIndexWrites`** — covers cross-namespace independence for `AccretionScanner.ScanNamespace`, with a DBSCAN-heavy maintenance loop against a namespace with LTM entries.

5. **`AllMaintenancePasses_ConcurrentWithWrites_NoDeadlock`** — broad stress test: all four maintenance pass types run concurrently against two namespaces while 200 foreground writers hammer both. Any deadlock causes the 30 s outer deadline to fire.

The 500 ms per-write threshold for ns-b is conservative (healthy runtime is <5 ms) but gives ample CI headroom. The 30 s outer deadline is the deadlock detector: a real deadlock hangs indefinitely.

---

## Required Code Changes

**None.**

The existing snapshot-then-release pattern is correct throughout. No code changes are required to make the lock protocol safe.

The only recommended action is to **add the regression test to the project file** once it has been reviewed and verified to pass. The `.draft` suffix and the note at the top of the file indicate it is not currently compiled. To activate:

1. Rename `LockUpgradeRegressionTests.cs.draft` → `LockUpgradeRegressionTests.cs`
2. Add it to `tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj` (or rely on the wildcard glob if the project uses `<Compile Include="**/*.cs" />`)

The test requires no new dependencies beyond what `LockHierarchyTests.cs` already uses (`McpEngramMemory.Core`, `McpEngramMemory.Core.Services`, xUnit).
