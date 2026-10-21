# McpEngramMemory 2.1.0 — Recall quality and reliable upgrades

Release preparation for the `McpEngramMemory.Core`, `McpEngramMemory.Synthesis.Onnx`
and `McpEngramMemory` package set. This PR does not publish a package or tag.

## What changes

- **Recall uses the same evidence rules across routes.** Direct, broadcast and expert-routed
  requests handle natural questions, paraphrases, missing answers and non-answer text consistently.
  Empty or insufficient experts fall back while complete expert answers retain their route.
- **Coverage improves under noise.** Candidate recovery and source selection retain useful evidence
  for broad, compound, backup/recovery and restore-order questions. Summaries no longer crowd out
  the sources needed to answer the question. Historical questions select the requested period and
  reject unrelated evidence; current questions demote explicitly superseded claims.
- **Contradictions remain attributable.** Recall detects conflicting unmarked and paraphrased
  claims, including explicit denials, and exposes unresolved conflicts. Resolution requires
  supporting governed evidence; recency alone does not establish truth.
- **Scope protections remain in force.** Candidate recovery, hybrid filters and route fallback
  retain tenant, namespace permission, project/environment, lifecycle, category and score controls.
- **Persistence and maintenance are more reliable.** Merge no longer holds topology locks against
  a persistence scheduling lock that calls back into the same topology. Timer callbacks cannot
  consume or rearm a newer save request. Lifecycle changes avoid redundant index snapshot writes,
  and context blocks invalidate when their content changes.
- **Installation preserves user data and configuration.** The executable uses a durable user-local
  data directory; migration copies and verifies a stopped store before package replacement. Setup
  preserves unrelated JSON/TOML settings, backs up replacements and refuses malformed files.
  Source setup supports SDK 8+ and Windows PowerShell 5.1; long Windows paths load packaged native
  libraries correctly. Publisher dry runs retain existing evidence.
- **Native telemetry is suppressed before ONNX initializes.** The MCP executable applies the
  policy on validated Windows x64, Linux x64 and Apple Silicon macOS paths. The Windows workaround
  is guarded for ONNX Runtime 1.29 and must be requalified when that dependency changes.
- **Dependencies receive security servicing.** Core explicitly pins patched PKCS dependencies
  (8.0.2 for .NET 8; 9.0.21 for .NET 9/10), and the SQLite bundle is pinned to 3.0.5.

## Compatibility

All three packages move together from 2.0.0 to **2.1.0**. Core and Synthesis.Onnx retain the 2.0
public API signatures. MCP tool names and arguments remain compatible, with the measured profiles
unchanged at **17 minimal / 39 standard / 63 full**. Optional recall conflict diagnostics are
additive; clients should tolerate additional response properties.

Recall ordering, abstention and conflict reporting intentionally improve. A query may now return
the missing source, omit unrelated/non-answer evidence, or report an unresolved disagreement.
These are behavior corrections, not a guarantee that every natural-language question is solved.

The stored entry format remains compatible with 2.0.0. **The executable's default storage location
changes.** Embedding hosts retain the public Core storage constructor defaults and must continue
to supply their own durable paths and process telemetry policy. Hosts coming from pre-2.0 Core
still need the [2.0 API migration](release-notes-2.0.0.md).

## Upgrade from an installed 2.0.0 tool

**Stop every server using the installation. Do not run a normal tool update or uninstall first.**
2.0.0's default data sits inside its versioned package directory. The package manager removes that
directory during replacement; the new server cannot recover data already deleted.

From this checkout, after 2.1.0 is published:

```sh
python scripts/upgrade-tool.py --global --version 2.1.0 --server-stopped
# For an existing tool-path installation:
python scripts/upgrade-tool.py --tool-path /path/to/tools --version 2.1.0 --server-stopped
```

For the unpublished candidate, append `--source /path/to/validated-feed`. The helper stages the
new executable separately, verifies a backup and durable data copy, and only then replaces the
old installation. Keep the printed backup until known memories, metadata, sharing and scoped recall
pass after restart. Preserve explicit `MEMORY_SQLITE_PATH` and `MEMORY_GOVERNANCE_PATH` settings;
back up those external stores separately while stopped. Custom destinations also require the
matching `MEMORY_DATA_PATH` in client configuration. Full instructions, source-checkout migration
and recovery steps are in [Installation and upgrades](installation-and-upgrades.md).

Fresh tool installs and library consumers can use the versioned commands once published:

```sh
dotnet tool install --global McpEngramMemory --version 2.1.0
dotnet add package McpEngramMemory.Core --version 2.1.0
# Optional in-process synthesis backend:
dotnet add package McpEngramMemory.Synthesis.Onnx --version 2.1.0
```

Use one server process per data root/database. Multi-request concurrency inside a server does
not qualify independent processes writing the same store.

## Validation and limits

The implementation baseline merged in [PR #58](https://github.com/wyckit/mcp-engram-memory/pull/58)
passed 2,717 ordinary tests on each of .NET 8/9/10, strict independent and scale recall gates,
six bounded Linux test repetitions, and 39 fresh MSA metric checks. All 1,200 independent,
historical-noise and scale observations matched the preceding recall fix apart from timing.

Simulated-time tests executed 1,620 real maintenance cycles across JSON/SQLite, including actual
promotion, archival, auto-link and accretion effects, cancellation and tenant isolation. Forced
snapshot interleavings exercised scoped reads, writes, maintenance and durable reopening.
A single Windows x64/.NET 8 run completed 30 minutes of real MCP load with 1,000 JSON and 5,000
SQLite memories: 7,196 sustained recalls, 238 measured read/write overlaps and zero quality
failures. Both restart durability and private/foreign/legacy canaries passed. This is **30 minutes
of observed runtime**, not a simulated claim of 48-hour uptime. The earlier stopped diagnostic
and its failures remain preserved separately.

The release PR records validation of the **final 2.1.0 packages**, including their hashes and
actual published-2.0.0 upgrade/restart checks on Windows x64, Linux x64 and Apple Silicon macOS.
Package structure checks enforce aligned versions/dependencies and supported framework assets;
the installed tool must match the candidate archive. See the
[repeatable package procedure](installation-and-upgrades.md#repeatable-validation) and
[reliability protocol](release-soak.md).

This coverage does not establish live SQL Server upgrades, shared-store multi-process writers,
Windows ARM64, Intel macOS, arbitrary embedding hosts, or fresh end-to-end LLM outcomes. MSA's
three historical agent-outcome comparisons are not fresh LLM runs, and its existing physics
`vector_rerank` label follows the vector path. No new native dependency or telemetry policy is
introduced by the version bump.
