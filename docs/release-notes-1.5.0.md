_Security release. The namespace ACL model did not function in any prior version — see **Security** below. Also fixes a shutdown data-hygiene bug, a dry-run miscount on a destructive operation, and two input-validation gaps, and bounds the automatic background scans._

_Minor rather than patch: two result records gained trailing parameters, and namespace access control now actually refuses cross-agent access. Source-compatible; assemblies built against 1.4.0 must be rebuilt._

### Security

- **Namespace ACLs are now enforced. In every prior version they were not.**
  Three things compounded: the permission check was reached from exactly one tool
  (`cross_search`), nothing ever registered namespace ownership, and an unregistered
  namespace was treated as open — so every check passed. `share_namespace` recorded a
  grant that nothing consulted.

  Ownership is now claimed on first write by an agent with an `AGENT_ID`, and every
  namespace-scoped tool checks access: reads, writes, `recall`'s broadcast across the whole
  store, graph and cluster expansion (which cross namespaces because edges are global), and
  the namespace list returned by `cognitive_stats`. Read denials are shaped like empty
  results so they cannot be used to test whether a namespace or entry exists.

  **If you used `share_namespace` before this release, assume that data was readable by any
  agent connected to that server.**

- **Two related fixes found while implementing the above.** `delete_memory` stripped an
  entry's graph edges and cluster memberships *before* checking the entry existed, so an
  unauthorized caller could destroy them regardless. `get_graph_snapshot` exported the
  permission records themselves — they live in a system namespace that is always readable
  and embed the namespace name in their id, so the snapshot disclosed the name of every
  private namespace in the store.

- **Model downloads are checksum-verified.** The embedding model and vocab are fetched from
  Hugging Face at build time and packed into the published packages, previously with no
  integrity check of any kind — a tampered download on a release build would have shipped to
  every consumer undetected. Both files now have a pinned SHA-256 verified before use, and a
  mismatch fails the build. A malicious `.onnx` is native-code attack surface in ONNX
  Runtime, not merely a bad-embeddings risk.

### Fixed

- **Silent data loss on over-long namespace names.** The JSON backend maps a namespace to a
  filename, so a long name produced a path the OS rejects. The write happens on a debounced
  timer after the tool already reported success, and the error was only logged — the entry
  and every later write to that namespace were never persisted and lost on restart.
  Namespaces are now rejected above 128 characters, at ingest.
- **Uncaught crash from a mismatched query vector.** A caller-supplied vector of the wrong
  length reached the dot-product loop and threw `IndexOutOfRangeException` from inside the
  search path. It is now rejected by name against the embedding model's dimensionality.
- **`purge_debates` over-reported edges in dry run by roughly 2×.** It summed edges per
  entry, double-counting every edge whose endpoints both sat inside the namespace. This is
  the one operation whose entire purpose is to let you check before deleting something
  irreversible.
- **The write-ahead log survived shutdown.** `Dispose` flushed pending writes but never
  checkpointed, and connection pooling means SQLite's "remove the WAL on last close" never
  fires, so a multi-megabyte `-wal` sidecar was left next to the database. `Dispose` now
  truncates it. Note this is shutdown hygiene, not unbounded growth: the WAL plateaus at
  ~4 MB during normal operation via SQLite's automatic checkpoint.

### Changed

- **The automatic background scans are bounded.** Auto-link (every 6 hours) and accretion
  (every 30 minutes) are both quadratic in the candidate count and previously had no size
  limit. Measured at ~1–2s for a 4,200-entry namespace and ~3–5s at 8,000, reaching minutes
  per sweep in the tens of thousands. Both now bound a single pass at 10,000 entries and
  **report** what they skipped via `EntriesNotScanned`, rather than silently scanning a
  subset — a truncated scan that looks complete is the worse failure.
- **`cross_search` accepts `ns` as an alias** for `namespaces`, and either may be a JSON
  array, a comma-separated string, or a single namespace.
- **`cognitive_stats` and `purge_debates` cap their list output** (100 namespaces and 25
  detail records, both overridable, `0` for uncapped). Counts and totals are never capped.

### Compatibility

- **Binary-breaking for `McpEngramMemory.Core`.** `AutoLinkResult` and `AccretionScanResult`
  gained a trailing `EntriesNotScanned`; `AutoLinkScanner.Scan` and the `AccretionScanner`
  constructor gained trailing optional parameters. Source-compatible — recompile.
- **Servers that do not set `AGENT_ID` are unaffected by the ACL change.** They run as the
  default identity, which has unrestricted access and never claims ownership. This is the
  single-user case and the common deployment.
- **Upgrading does not retroactively protect existing data.** A namespace with no ownership
  record stays open until an identified agent writes to it and claims it.
- ACL enforcement currently covers the default `minimal` profile and the namespace-scoped
  `standard`/`full` tools. `ExpertDispatcher`'s hierarchical routing path and the benchmark
  tools' file-path and executable parameters are not yet covered.


## Upgrading

```bash
dotnet tool update --global McpEngramMemory --version 1.5.0
dotnet add package McpEngramMemory.Core --version 1.5.0
```

If you set `AGENT_ID` on more than one server against the same store, read the
**Compatibility** notes above before upgrading — namespace access is now actually
enforced, so calls that previously succeeded across agents will start being refused.

Verified: 1155 tests passing on each of net8.0, net9.0 and net10.0. The embedding model
embedded in the published package was checksum-verified against its pinned SHA-256 at
build time.
