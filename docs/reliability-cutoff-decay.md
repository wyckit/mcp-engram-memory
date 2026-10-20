# Retirement cutoff and lifecycle persistence reliability

This change addresses two findings retained after the recall repair series:
incomplete round accounting in the candidate-index stress harness, and excessive
synchronous persistence work on the lifecycle path exercised by a recurring CI
timeout.

## Retirement cutoff reproduction

The storm has a barrier at the start of each round. The remover can finish round
0 and publish the cutoff for round 1 before a delayed adder checks whether to
perform its round-0 write. The old `stopFromRound >= 0` check then skipped that
write, although the returned completed-round count still included round 0.

The new regression holds one adder after the barrier until the remover publishes
the next cutoff. Before the fix it consistently returned only the other adder's
placement. It now requires both placements, the exact live-entry count, and no
entry for the skipped round. Additional cases cover stopping before the first
round, after one round, and completing all requested rounds.

Workers now compare the cutoff with their own round number. The original stress
round counts, minimum completed-round floor, publication/retirement contention
and duplicate-ID checks are unchanged. The forced interleaving uses events, not
sleeps or a lucky wall-clock expiry.

## Lifecycle persistence cost

`SetActivationEnergyAndState` schedules an ordinary entry save. On the JSON
backend that previously also serialized and synchronously wrote the entire HNSW
snapshot, under the partition write lock, for every entry updated. HNSW stores
vector-search topology; activation energy and lifecycle state do not change it.

A new test using the real JSON provider measured **2,000 synchronous HNSW saves**
for four decay passes over 500 entries. It failed before the fix. The same passes
now make **zero HNSW saves**, while still scheduling every memory-data update.
The existing incremental SQLite/SQL Server persistence branch is unchanged.

The narrow production change tells the internal persistence path to omit the
unchanged derived index for both energy/state update overloads. Insertions,
vector replacements, deletions and other existing save callers keep their prior
index-persistence behavior. Public Core APIs, MCP tools and storage formats are
unchanged.

Tests flush and reload the real JSON store, verify every updated lifecycle state
and energy, and check that a same-ID entry in another tenant remains unchanged.
Separate cases cover legacy and explicit tenant calls, energy-only updates,
vector replacement, deletion and the saved HNSW topology.

## Timeout diagnosis and limits

The previously recorded .NET 8 CI failure reported that lifecycle workers
exceeded 60 seconds and then cancelled and joined. It did not contain worker
stacks or per-lane progress, so it cannot establish a deadlock or prove that
HNSW I/O was its only cause.

The excessive synchronous writes are a demonstrated production cost on that
exact decay path. Removing them addresses that cost without reducing the
fixture's 500-entry namespaces, four decay passes or 100 concurrent writes.
The original 60-second worker limit and 10-second per-write bound remain intact.

Lifecycle concurrency tests now report named lanes, batch progress and elapsed
time. Deadline diagnostics capture that progress before cancellation, and a
regression verifies that workers are drained and the pre-cancellation phase is
reported. Any further CI timeout should be investigated using these details,
not dismissed because a rerun passes.

## Validation

Local validation passes **2,478 tests on each of .NET 8, 9 and 10**, including
eight new regression cases. The unchanged 1,000/5,000-memory evaluation passes
all 624 repeated observations and its strict gate. All 27 earlier recall reports
(2,976 observations) and all scale observations match #50 after excluding timing
and validating generated promotion identities/hashes. Small-corpus gates, 12
report-gate tests, 48 pinned benchmark checks and all three package builds pass.
Pinned checks are not a fresh MSA run.

In the concurrent full-suite run, the decay/writer worker pairs completed their
original workloads within 0.5–8.7 seconds across the three frameworks. This
includes barrier waits and contention, excludes fixture setup, and is a local
observation rather than a production latency guarantee.

Local red/green results, full-suite recall comparisons and scale reports are
retained under `artifacts/reliability-cutoff-decay/`. See the PR checks for CI
status.

```powershell
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release --filter 'FullyQualifiedName~LifecyclePersistenceCostTests|FullyQualifiedName~CandidateIndexConcurrencyTests|FullyQualifiedName~LockUpgradeRegressionTests|FullyQualifiedName~ConcurrentTestWorkersTests'

$env:ENGRAM_RECALL_EVAL_OUTPUT = Join-Path $PWD 'artifacts/reliability-check/full'
dotnet test McpEngramMemory.slnx -c Release --filter 'Category!=MSA&Category!=RecallScale'

$env:ENGRAM_RECALL_EVAL_OUTPUT = Join-Path $PWD 'artifacts/reliability-check/scale'
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release -f net8.0 --no-build --filter 'Category=RecallScale'
python scripts/check-noisy-recall.py artifacts/reliability-check/scale --release
```

Frozen recall cases, labels, thresholds and scope protections are not changed.
This fix does not replace release-candidate installation, upgrade, load or soak
validation.
