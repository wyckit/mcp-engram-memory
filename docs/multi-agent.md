# Multi-Agent Memory Sharing

[< Back to README](../README.md)

> **Status — v0.8.0.** The multi-agent sharing surface is now officially
> supported. Read this page in full before putting it into production — the
> failure modes and boundaries matter.

## What it is

Multi-agent sharing lets several AI agents talk to one `mcp-engram-memory`
server and cooperate on a shared knowledge graph while still keeping private
namespaces private. The building blocks:

| Piece | Role |
|---|---|
| `AGENT_ID` env var | Identity of the agent process |
| `share_namespace` / `unshare_namespace` | Grant / revoke read or write access |
| `whoami` / `list_shared` | Inspect identity + what other agents shared with you |
| `cross_search` | Permission-aware search across multiple namespaces |
| `CognitiveIndex.EntryUpserted` / `EntryDeleted` events | Real-time notification after a write commits |

All of this is **in-process**: one `mcp-engram-memory` server, many agent
connections. Cross-process sharing (two server processes pointing at the
same data directory) is not yet supported — see
[Boundaries](#boundaries-v080) below.

## Quick start

### 1. Give each agent a distinct identity

Launch each agent with its own `AGENT_ID`:

```bash
# Agent A (terminal 1)
AGENT_ID=alice dotnet run --project src/McpEngramMemory

# Agent B (terminal 2)
AGENT_ID=bob dotnet run --project src/McpEngramMemory
```

If `AGENT_ID` is unset, the agent runs as the built-in `default` identity,
which has unrestricted access to all namespaces (backward-compatible single-user
mode). Once you set any non-default `AGENT_ID` in your deployment, the default
agent loses its implicit bypass — every non-default agent must be granted access
explicitly.

### 2. Register ownership (implicit on first write)

The first write to a namespace claims ownership:

```text
# As alice
store_memory(ns="team-decisions", id="pick-rust", text="we went with rust",
             category="decision")
```

After this, `alice` is the owner of `team-decisions`. Subsequent writes by
other agents to the same namespace are denied unless `alice` has shared it
with them.

### 3. Share with another agent

```text
# As alice
share_namespace(ns="team-decisions", agentId="bob", accessLevel="read")
```

`bob` can now `search_memory` and see entries in `team-decisions`. Grant
`"write"` instead of `"read"` to let `bob` also `store_memory` there.

### 4. Search across namespaces you own + were shared

```text
# As bob
cross_search(namespaces="bob-private,team-decisions",
             text="did we pick a language",
             hybrid=true)
```

`cross_search` filters the namespace list down to the ones you can actually
read, then fans out, runs one search per namespace, and merges the results
with Reciprocal Rank Fusion. If nothing in the provided list is accessible,
it returns `Error: no accessible namespaces in the provided list.`

### 5. Inspect identity

```text
whoami()
# → { AgentId: "bob", OwnedNamespaces: ["bob-private"], SharedNamespaces: [...] }

list_shared()
# → just the namespaces OTHER agents shared with you (not your own)
```

## Real-time notification (in-process consumers)

`CognitiveIndex` raises two events so consumers can observe writes
without polling:

```csharp
var index = serviceProvider.GetRequiredService<CognitiveIndex>();

index.EntryUpserted += (_, entry) =>
{
    Console.WriteLine($"[{entry.Ns}] {entry.Id} upserted");
};

index.EntryDeleted += (_, removed) =>
{
    Console.WriteLine($"[{removed.Namespace}] {removed.Id} deleted");
};
```

**Semantics:**

- Events fire **after** the internal write lock is released, so handlers
  can call back into the index (including `Search`) without deadlock.
- Handlers run **synchronously on the writer's thread**. Keep them cheap,
  or offload work to a `Channel<T>` / `ThreadPool.QueueUserWorkItem`.
- `UpsertBatch` raises one `EntryUpserted` per accepted entry.
- Subscriber lifetimes are your responsibility — always `-=` on dispose.

The `RealtimeSharing_ReaderObservesWriterEvent` and
`RealtimeSharing_FanInFromMultipleWriters_NoDroppedEvents` tests in
`tests/McpEngramMemory.Tests/ParallelAgentTests.cs` show the
`SemaphoreSlim`-based pattern for "writer publishes, reader awakes
without polling" and the fan-in pattern for aggregating N writers.

## Concurrency guarantees

As of v0.8.0:

- **`NamespaceRegistry` Share / Unshare / EnsureOwnership** are serialized
  per-namespace by a `ConcurrentDictionary<string, object>` of monitors.
  Concurrent grants to the same namespace will not lose updates; grants
  to different namespaces stay parallel.
- **`CognitiveIndex` Upsert / Delete / Search / etc.** are gated by a
  single process-wide `ReaderWriterLockSlim`. Readers parallelize;
  writers are exclusive against all other work. This means a high-rate
  writer can briefly stall concurrent readers across all namespaces —
  known throughput ceiling, tracked for v0.8.1 as "namespace-partitioned
  write locks."
- **Permission metadata survives process restart.** `NamespaceRegistry`
  persists its state in the `_system_sharing` namespace via the same
  `CognitiveIndex` write path, so SQLite (or JSON) persistence carries
  ownership and grants across restarts.

The parallel-agent test suite
(`tests/McpEngramMemory.Tests/ParallelAgentTests.cs`, 9 tests) covers:
concurrent Share grant-preservation, parallel ownership contention,
share-churn against concurrent `cross_search`, duplicate-id writer
races, and fan-in event aggregation.

## Boundaries (v0.8.0)

Read this section before wiring anything to production.

### Single process per data directory

The current release assumes **one `mcp-engram-memory` server process per
data directory.** SQLite is configured with WAL mode
(`PRAGMA journal_mode=WAL`), which prevents on-disk corruption even if
two processes open the same file — but the server holds **in-memory
caches** (`NamespaceStore._namespaces`, `BM25Index`, HNSW indices) that
are populated on first read and never invalidated by external writes.

Concretely: if Process A and Process B both point at `./data/engram/`,
Process A's `search_memory` will **not** see entries Process B wrote
after Process A loaded the namespace. Permissions stored in
`_system_sharing` have the same caching behaviour.

For v0.8.x the supported pattern is:

- **One server process** per data directory
- **Many agent connections (many `AGENT_ID`s)** into that one server
  over stdio MCP

Cross-process sharing (shared WAL + cache-invalidation protocol) is
tracked for a later release.

### `cross_search` feature parity

`cross_search` supports: `k`, `hybrid`, `rerank`, `includeStates`,
`summaryFirst`, `minScore`, `category`, `diversity`, `diversityLambda`.

It does **not yet** support `expand_graph`, `expand_query`, `use_physics`,
or `temperature` — those are single-namespace orchestration features of
`search_memory`. If you need them, call `search_memory` per namespace
and merge client-side, or fall back to the single most relevant namespace.

### No live audit stream (v0.8.0)

Events fire in-process. There is no per-agent audit log of "who shared
what with whom at what time" beyond the persisted `_system_sharing`
entries themselves. For now, subscribe to `EntryUpserted` and log events
yourself if you need an audit trail.

## Troubleshooting

**"Error: no accessible namespaces in the provided list."** You passed
`cross_search` a list where your current `AGENT_ID` has access to zero
entries. Check `whoami()` and `list_shared()`.

**"error_not_owner" from share_namespace.** Only the namespace owner
(or `default`) can grant access. Call `whoami()` to confirm what you
own, or have the real owner run the `share_namespace` call.

**A grant silently disappears.** If you're on < 0.8.0, upgrade — the
lost-update race on concurrent `Share` calls was fixed in this release.

**Events don't fire on a reader.** `EntryUpserted` / `EntryDeleted` are
in-process events. A separate OS process cannot subscribe to them.
Stay in one process, or wait for cross-process pub-sub.

## See also

- `docs/architecture.md` — where CognitiveIndex, NamespaceStore, and the
  storage layer sit
- `docs/mcp-tools-reference.md` — full parameter reference for every MCP
  tool
- `tests/McpEngramMemory.Tests/ParallelAgentTests.cs` — executable
  examples of the patterns above
- `CHANGELOG.md` `[0.8.0]` — API stability declaration
