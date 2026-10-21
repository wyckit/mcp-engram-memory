# Release validation — October 21, 2026

## Current disposition

The two defects documented below were subsequently fixed and merged: persistence
lock ordering in [#59](https://github.com/wyckit/mcp-engram-memory/pull/59), and
historical evidence filtering in [#60](https://github.com/wyckit/mcp-engram-memory/pull/60).
Both PRs passed all 14 CI checks. The original diagnostic soak was stopped at the
user's request on October 21 at 12:22:08 UTC (08:22:08 EDT), and its monitor paused.
Its roughly 84.5 minutes and 86 retained quality failures are preserved as incomplete
historical evidence, not a release pass. Final restart qualification was not run.

The release strategy now uses simulated maintenance time, controlled concurrency
and a single 30-minute real-process stress run. See [the revised protocol](release-soak.md).
The updated PR #58 description records the fixed candidate's results separately;
local evidence is retained under `artifacts/accelerated-validation/`.

## Original investigation at c7c44ce

**The following findings describe the pre-fix revision.** Telemetry PR [#57](https://github.com/wyckit/mcp-engram-memory/pull/57)
was merged after all 14 checks passed at head `4e3ace01af35ad72959283cd5d9e860a54950ad2`.
The merge is `c7c44ce91718d35f3cd7ba26de79606a7831b083`. The subsequent validation
found two concrete blockers. A 48-hour diagnostic soak was started during that investigation.

## Blocker 1: merge and persistence can deadlock

The bounded Linux investigation
[39586427556](https://github.com/wyckit/mcp-engram-memory/actions/runs/39586427556)
reproduced the historical symptom on the third parallel-framework repetition of
`d553b69785d1e8b0cb28a0c9bbac4ec5080c93eb`. .NET 9 and 10 finished all 2,470 tests;
.NET 8 stopped after 2,468 completed tests and was aborted by the five-minute
inactivity detector. The sequence identifies
`TenantStructureIsolationTests.Intelligence_DetectAndMerge_IsolatePerTenant` as
the unfinished test. The first two parallel repetitions and all three .NET
8-only repetitions passed. The merged revision passed all three repetitions
in both modes. Passing repetitions do not rule out this race.

Unlike the original cancelled run, this reproduction captured the managed test
process dump. Linux analysis in
[39588715566](https://github.com/wyckit/mcp-engram-memory/actions/runs/39588715566)
produced the following two blocked stacks:

```text
Thread 0x1fc7:
  Monitor.ReliableEnter
  PersistenceManager.ScheduleSave                         :256
  NamespaceStore.ScheduleEntryUpsert                      :689
  CognitiveIndex.<TryCommitMerge>b__0                      :950
  ClusterManager.TryCommitPreparedMergeMembership          :1301
  KnowledgeGraph.TryCommitPreparedMerge                    :1783
  IntelligenceTools.MergeMemories                         :319

Thread 0x1ff7:
  ReaderWriterLockSlim.EnterReadLock
  KnowledgeGraph.SnapshotEdgesForSave                      :2461
  PersistenceManager.WriteGlobalEdges                     :1377
  PersistenceManager.<ScheduleSaveGlobalEdges>b__1          :397
  PersistenceManager.RunWriteAndRelease                    :322
```

The merge holds the namespace, graph and cluster write locks while scheduling
an entry save, which requires `_timerLock`. The edge-save timer holds
`_timerLock` while invoking the graph snapshot provider, which requires the graph
read lock. Each therefore waits for the other's lock. The involved merge,
graph, cluster and persistence locking paths are unchanged on the merged
revision. This is a production lock-order problem, not evidence that scope
assertions should be removed or test timeouts extended.

The dump analysis obtained the managed stacks successfully. Its initial job
then failed because heap-oriented `syncblk`/`dumpasync` commands lacked data in
the mini dump. The reusable investigation now extracts stacks only. The original
run 39391883137 had no dump, so its exact blocked stack cannot be proven
retrospectively; the new reproduction establishes this concrete defect with
matching .NET 8-only stall symptoms.

Fix direction: remove snapshot-provider calls under the persistence scheduling
lock while preserving namespace-before-topology durability, stale-save rejection,
coherent merge commits, and tenant/namespace isolation. Add a deterministic
merge-versus-debounced-save regression and repeat both storage and recall gates.
No production fix is included in this validation PR.

## Blocker 2: historical recall adds unrelated evidence

The unchanged independent question
`What was Citrine production readiness endpoint before release 27?` returns
`c-endpoint-old` first through unscoped broadcast, followed by unrelated library
entries such as `background-27`, `background-203` and `background-205`.

This reproduces with both 1,000 JSON and 5,000 SQLite memories, ingested through
real MCP tools and production embeddings. Direct recall passes. A short diagnostic
preflight completed concurrent read/write, shutdown, restart and scope checks,
but retained ten quality failures, five per backend. This extends the measurement
beyond the existing Core fixture runs without changing the frozen questions or
expected answers. It must be fixed without loosening scope or negative-evidence
checks.

## Preserved, stopped 48-hour diagnostic

- Revision: `c7c44ce91718d35f3cd7ba26de79606a7831b083`, Windows x64, .NET 8.
- Started: **2026-10-21 10:57:38 UTC** (6:57:38 a.m. Eastern).
- Originally planned interval end: **2026-10-23 10:57:38 UTC**, followed by restart checks.
- Output: `artifacts/release-soak/soak-48h-20261021-v2/` on the initiating host.
- Separate JSON/SQLite processes, 1,000/5,000 memories, direct/broadcast recalls,
  concurrent writes, original background timers and scope canaries.
- Stopped by the user on October 21 at 12:22:08 UTC; hourly monitoring is paused.
  The original artifacts and user-stop annotation are retained.

An earlier launch stopped immediately on a Windows status-file sharing error.
Its evidence is retained under `soak-48h-20261021/`; none of that time counts.
The harness now retries that transient reporting error with a bounded deadline,
and the active `-v2` run starts a fresh full interval.

The diagnostic run continued reliability measurements after recording
the known recall quality failure. Scope, protocol and reliability failures still
stop immediately. **Any recorded quality failure makes the final release verdict
fail**, even if all 48 hours and restart checks complete. The revised accelerated protocol requires fresh fixed-candidate evidence; these
failed diagnostic observations cannot become a pass.

See [the soak protocol](release-soak.md) for workload, thresholds and limits.
This run does not qualify registered-expert routing, live SQL Server, shared-file
multi-process writers, Windows ARM64, Intel macOS or fresh live-LLM outcomes.

## Validation and evidence

- Nine harness regression tests pass, including out-of-order responses, blocked
  stdin deadlines, scope rejection and transient/permanent status-write errors.
- Local smoke completes the original 22-entry corpus, overlap, restart durability
  and unchanged archived canaries for both storage backends.
- [CI 39588445659](https://github.com/wyckit/mcp-engram-memory/actions/runs/39588445659)
  passes all 16 jobs, including Windows/Linux harness smoke, ordinary tests,
  unchanged strict independent/scale gates, telemetry and installation/upgrade.
  This is validation of commit `4fba675b1060e3d36d94b7907f29969c43d55e79`;
  subsequent report/workflow edits have their own CI runs.
- Frozen independent fixture SHA-256 remains
  `11D86B86DF1629FC9931DE7DC846E4A7B317A9C31F15FB6363D73323CC4F6553`.
- Local historical evidence: `artifacts/release-soak/hang-original/`,
  `hang-historical-parallel/` and `hang-stacks/`. The latter contains
  `McpEngramMemory.Tests_8074_20261021T105822_hangdump.dmp.txt`.
- Recall evidence: `artifacts/release-soak/preflight-full/historical-recall-failure.json`,
  `preflight-diagnostic/quality-failures.jsonl`, and the active run's retained logs.

Before release: complete the revised fixed-candidate validation, retain passing
focused and unchanged scope/recall gates, then align the three package versions
and finish release/migration notes.
No release has been published by this validation work.
