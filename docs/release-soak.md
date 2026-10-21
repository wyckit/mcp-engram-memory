# Accelerated release reliability validation

Release validation combines simulated maintenance time, deterministic concurrency
interleavings and a bounded real MCP stress run. A 48-hour wait is no longer a
mandatory release gate. These checks establish specific coverage; simulated days
must never be reported as continuous uptime or proof that every leak/race is absent.

## Simulated time and concurrency

```sh
dotnet test tests/McpEngramMemory.Tests/McpEngramMemory.Tests.csproj -c Release -f net8.0 --filter "FullyQualifiedName~SimulatedMaintenanceTests|FullyQualifiedName~MaintenanceSnapshotConcurrencyTests|FullyQualifiedName~MergePersistenceDeadlockTests|FullyQualifiedName~StorageTimerOwnershipTests"
```

The simulated clock drives the real hosted workers at their unchanged schedules.
An internal clock on CognitiveIndex is shared by access timestamps, decay-age
calculations and the workers. Production uses TimeProvider.System; no public
constructor, MCP argument, configuration option or maintenance cadence changes.

For JSON and SQLite, the tests verify:

- 672 decay passes over seven simulated days: the exact archive boundary, daily
  reinforcement, restart persistence and separate configurations for colliding IDs
  in two named tenants and the legacy tenant.
- 14 daily consolidation passes over qualifying 32-member graphs: real promotion
  and archival, processed-entry counts and a tenant-specific consolidation opt-out.
- 28 auto-link passes at the six-hour cadence: real edges, no duplicate accumulation
  and a tenant-specific opt-out.
- 96 accretion passes over two simulated days: partition-local summaries without
  repeated collapse or cross-tenant source contamination.
- Cancellation before each worker's first deadline removes its pending delay.

The clock advances one scheduled deadline at a time and waits for completion and
registration of the next delay. It does not skip from day zero to day fourteen
and pretend the intervening work ran. Assertion deadlines use real time only to
bound broken tests. A mutation restoring DateTimeOffset.UtcNow for decay debt
fails both backend tests; the mutation is not part of the implementation.

Controlled concurrency tests hold a persistence snapshot open while scoped recall,
a write and decay must complete on real threads. JSON/SQLite, successful/failed
capture, private/foreign/legacy canaries and durable reopening are covered. The
existing deterministic merge/deadlock, stale callback, retry and shutdown tests
remain required, as do the unchanged independent and noisy recall gates.

## Real MCP stress

Use Python 3.11+, .NET 8 and psutil==7.0.0. Publish the exact recorded candidate,
then run from a stable copy in a fresh output directory:

```sh
dotnet publish src/McpEngramMemory/McpEngramMemory.csproj -c Release -f net8.0 -p:TargetFrameworks=net8.0 -o artifacts/stress-server
python -m pip install psutil==7.0.0
python -m unittest discover -s scripts/tests -p test_soak_validation.py
python scripts/run-release-soak.py --server artifacts/stress-server/McpEngramMemory.dll --fixture tests/McpEngramMemory.Tests/Fixtures/recall-independent-v1.json --revision YOUR_COMMIT_SHA --output artifacts/stress-30m --hours 0.5 --interval 1
```

The script retains its existing filename. Its default duration is now 30 minutes;
use the explicit one-second interval above for the release stress run. Run only
one sustained workload at a time. The duration is a bounded observation window,
not an equivalence claim to 48 hours. Extend or add targeted tests when evidence
identifies an uncovered risk.

The run uses separate JSON/1,000 and SQLite/5,000 servers, production embeddings,
the unchanged 22 entries/24 questions plus library noise, and isolated test data.
It snapshots binary, fixture and harness files with hashes. It cannot inherit the
user's data directory, tenant or agent identity. The host must remain awake.

- Direct and unscoped recalls run concurrently every second. Writers upsert, get,
  store and delete every 30 seconds; observed read/write overlap is required.
- All questions run before load, after load and after restart. Original expected
  answers, top-one requirements, compound facets, negatives and project/environment
  scope remain unchanged. The fixture hash is pinned.
- Private, foreign-tenant and legacy archived canaries must never be disclosed or
  mutated. Denied writes and minimum-score controls run each minute. Final reopening
  under each canary's own identity verifies text, state and access count.
- Production worker timers stay unchanged. Worker errors and missed/stalled expected
  cycles fail. The simulated suite supplies repeated daily/six-hour coverage that
  the short real interval does not establish; completed cycles alone do not prove
  useful maintenance, so the simulated tests also assert real effects.
- Existing limits remain: request deadline 30 seconds, workload gap below 90 seconds,
  graceful shutdown within 20 seconds, RSS below 2 GiB per server, growth below
  512 MiB/512 handles from the ten-minute baseline, and no three consecutive sampled
  minutes with recall p95 over two seconds. These are test thresholds, not a capacity SLA.
- Final restart must retain the last committed writer value and deleted-entry absence.
  `workloadSeconds` excludes seeding and restart checks; final worker status and
  per-backend restart/canary verification are recorded separately.

The default stops on any failed criterion. The optional diagnostic
`--collect-after-quality-failure` retains failures while continuing reliability
measurements, but can never yield a clean pass. Do not enable it for the release
stress run. Preserve failed runs; never overwrite, resume or relabel them as passing.

CI's 36-second Windows/Linux smoke covers protocol/oracle/restart mechanics with
22 entries per backend; it is not the full-size 30-minute stress run. Ordinary CI
runs the simulated and controlled concurrency tests on .NET 8, 9 and 10.

## Evidence and limits

`status.json`, `samples.jsonl`, manifests, stderr and failure diagnostics retain
the result. Monitoring must verify process identity and heartbeat age, not merely
a stale running status. Final acceptance requires a successful exit, zero quality
failures, successful scope/durability checks, observed overlap, and passing
simulated/concurrency/unchanged recall suites. Report these separately.

The original 48-hour diagnostic was stopped at the user's request on October 21
at 12:22:08 UTC after about 84.5 minutes, with 86 known historical recall quality
failures. All original processes exited and the hourly monitor was paused.
`artifacts/release-soak/soak-48h-20261021-v2/` retains its original status, samples,
data, manifests and dumps, plus `user-stop.json`, the pre-stop status and a SHA-256
manifest of 176 preserved files. It is incomplete, not a 48-hour pass, and final
restart qualification was not performed. The earlier non-v2 aborted launch is also
preserved. Neither run is reused by the new stress test.

The short MCP run qualifies only its recorded OS/runtime, single-owner JSON/SQLite
stores and direct/broadcast routes. It does not establish registered-expert uptime,
live SQL Server, shared-file multi-process writers, Windows ARM64, Intel macOS or
fresh end-to-end LLM outcomes. Simulated maintenance is in-process and uses fixed
vectors; the separate MCP stress uses the production embedding path.

## Linux hang verification

The old bounded investigation captured the merge/persistence lock inversion on
historical revisions. Those artifacts remain historical evidence; #59 fixed the
locking and #60 fixed historical recall noise. The workflow now tests its current
candidate, with three parallel-framework and three .NET 8-only repetitions,
unchanged scope assertions, five-minute inactivity dump capture and ten-minute
outer deadlines. New candidate failures fail the workflow. No old known-failing
revision is used as a mandatory current-candidate pass criterion.
