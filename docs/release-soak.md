# Sustained release validation

The soak exercises a published server through real stdio MCP calls. It does not
change production code, the independent recall fixture, scope filters or timers.
Short CI runs validate the harness; only an uninterrupted 48-hour run qualifies
the sustained workload. A passing soak covers the recorded OS, runtime, storage
backends and routing paths, not every supported deployment.

## Run

Use Python 3.11+, .NET 8 and `psutil==7.0.0`. Publish from the revision being
qualified, then run in a new output directory:

```sh
dotnet publish src/McpEngramMemory/McpEngramMemory.csproj -c Release -f net8.0 -p:TargetFrameworks=net8.0 -o artifacts/soak-server
python -m pip install psutil==7.0.0
python -m unittest discover -s scripts/tests -p test_soak_validation.py
python scripts/run-release-soak.py --server artifacts/soak-server/McpEngramMemory.dll --fixture tests/McpEngramMemory.Tests/Fixtures/recall-independent-v1.json --revision YOUR_COMMIT_SHA --output artifacts/soak-48h --hours 48
```

Optionally pass `--dump-tool /absolute/path/to/dotnet-dump` to collect mini dumps
before shutting down a failed server. The harness snapshots the published files,
fixture and harness sources, and records hashes. Run from a stable copy when
continuing other development. Each output directory contains isolated test data;
the user's configured data path, tenant and agent identity are not inherited.

On Windows the process requests that the system remain awake while the soak is
active, without changing the power plan. Closing the host or suspending it can
invalidate the run. Never resume a stopped run and call it continuous evidence.
Atomic status replacement retries a transient Windows reader lock for at most
one second; a permanent write failure still stops the run. The initial launch
exposed this harness issue and was discarded as qualification evidence. The
subsequent launch starts its own complete 48-hour interval.

## Workload and gates

- One JSON server with 1,000 memories and one SQLite server with 5,000 memories.
  Each has a single owning process; this does not qualify shared-file writers.
- The 22 original entries and 24 questions from `recall-independent-v1.json`,
  with its SHA-256 pinned. Additional memories describe unrelated library books.
  Entries are ingested through MCP with the production embedding path.
- Direct and unscoped recall every five seconds, with concurrent upsert, get,
  store and delete operations every 30 seconds. Actual read/write overlap is
  required. The writer reuses IDs to keep corpus size bounded.
- Full questions before load, after load and after restart; original expected
  IDs, top-one answers, compound facets, negative cases, project and environment
  restrictions are checked. Direct-route facets remain limited to the requested
  namespace, as in the independent evaluation.
- Archived same-ID canaries belonging to another agent, another tenant and the
  legacy unscoped tenant. Every response is checked for canary disclosure; denied
  namespace writes and score thresholds are checked each minute. Final reopening
  under each canary's original identity verifies its content, lifecycle and
  access count. Any scope failure stops the run immediately.
- Default timers remain unchanged: decay 15 minutes, accretion 30 minutes,
  auto-link six hours and consolidation 24 hours. Worker errors, missing cycles
  or a last-run age exceeding the interval plus five minutes fail qualification.
- Request deadline 30 seconds; a workload gap of 90 seconds invalidates
  continuity. Graceful shutdown has a 20-second deadline.
- Per-process resident memory below 2 GiB, growth below 512 MiB from the
  ten-minute sample, and handle/file-descriptor growth below 512. Recall p95
  above two seconds for three consecutive minute samples fails qualification.
  These are explicit qualification thresholds, not a general performance SLA.
- Final restart verifies the latest committed writer value and deleted entry's
  absence. The 48-hour interval excludes seeding and final restart checks.

The fixture has expert-named namespaces but no registered expert profiles.
Unscoped calls currently exercise broadcast routing; the observed strategy is
recorded. This run does not establish populated-expert routing coverage.

`status.json` is replaced atomically each minute; `samples.jsonl` records resources,
latencies and workers. Failure diagnostics retain recent requests, stderr and
optional dumps. A monitor must also check process existence and heartbeat age:
an abruptly killed harness may leave its last status as running.

## Quality failures during diagnostic load

The default is to stop on any failed criterion. With the explicit
`--collect-after-quality-failure` option, recall quality failures are retained in
`quality-failures.jsonl`, and reliability measurements continue. Scope, protocol,
durability and resource failures still stop immediately. The final state remains
`failed`, with exit code 1, if any quality failures occurred, even when
`reliabilityCompleted` is true. This option cannot turn a failure into a release
pass.

The initial 1,000/5,000-memory preflight on merged revision
`c7c44ce91718d35f3cd7ba26de79606a7831b083` exposed a new blocker: the unscoped
`previous-endpoint` question returns the correct historical endpoint followed by
unrelated library entries. The short diagnostic run completed reliability and
restart checks but retained ten such quality failures, five per backend. The
48-hour run uses diagnostic continuation to collect reliability evidence while
preserving this blocker. No fixture labels or production filters were changed.

CI's 36-second harness smoke uses the original 22 entries per backend. It checks
the protocol, oracle, concurrent load and restart mechanics; it does not claim
to resolve the noisy-corpus failure or satisfy 48-hour qualification. The existing
strict independent and scale evaluation gates remain unchanged.

## Historical CI hang

Run [39391883137](https://github.com/wyckit/mcp-engram-memory/actions/runs/39391883137)
on revision `d553b69785d1e8b0cb28a0c9bbac4ec5080c93eb` was cancelled after the test
step ran for about four hours and 22 minutes. .NET 9 and 10 each recorded 2,470
passing tests; .NET 8 did not produce a result file. There was no sequence or dump
to identify the blocked test or establish a cause.

The bounded investigation workflow builds the unchanged historical revision and
the merged telemetry revision on Ubuntu 24.04. It repeats each three times with
all frameworks together and with .NET 8 alone. Each run retains console output,
VSTest diagnostics, TRX results and available sequences/dumps. VSTest has a
five-minute inactivity dump deadline and the outer process group a ten-minute
deadline. The regular-suite filter excludes MSA, scale and independent categories
because those have separate evaluation jobs; the independent category did not
exist at the historical revision.

A successful repetition is evidence that the hang did not recur under that
configuration. It is not a root-cause finding or a fix for the original hang.
