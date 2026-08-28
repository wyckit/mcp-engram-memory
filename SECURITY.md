# Security Policy

## Supported versions

Security fixes are provided for the latest minor release on the current major
version line — currently the `v1.5.x` line.

| Version  | Supported          |
|----------|--------------------|
| 1.5.x    | :white_check_mark: |
| < 1.5    | :x:                |

## Reporting a vulnerability

Please **do not open a public GitHub issue** for security vulnerabilities.
Instead, use one of these private channels:

- **GitHub Security Advisories** (preferred):
  <https://github.com/wyckit/mcp-engram-memory/security/advisories/new>
- Email the maintainer at the address listed on the
  [`wyckit` GitHub profile](https://github.com/wyckit).

### What to include

- A clear description of the vulnerability and the impact you can demonstrate.
- Affected versions (e.g. `v0.8.1` or `main@<sha>`).
- A minimal reproduction — code snippet, MCP tool invocation, or failing
  test — that demonstrates the issue.
- Your assessment of severity if you have one (optional).

### What to expect

- **Acknowledgement** within 3 business days.
- **Initial assessment** within 10 business days, with a target remediation
  timeline if the report is accepted.
- **Coordinated disclosure**: we'll work with you on a fix and a public
  advisory. Please give us a reasonable window before public disclosure —
  typically 30 days, longer if the fix is non-trivial.
- Credit in the advisory and CHANGELOG if you'd like.

## Scope

In scope:

- Code in `src/McpEngramMemory` and `src/McpEngramMemory.Core`.
- The `McpEngramMemory.Core` package published to nuget.org and GitHub
  Packages, and the `McpEngramMemory` server package on GitHub Packages.
- MCP tool surfaces (input handling, serialization, persistence).

Out of scope:

- Issues in third-party dependencies — please report those upstream. We will
  pick up fixes as dependency updates.
- Denial-of-service via resource exhaustion in a process you control (e.g.
  feeding the server a 10 GB prompt). The server trusts its operator.
- Local filesystem or SQLite access by someone who already has write access
  to the data directory.

## Memory poisoning / stored prompt injection

Engram stores whatever text the connected agent writes and returns it verbatim
on retrieval. If an attacker can influence what an agent stores — a poisoned
web page, a malicious issue comment, a compromised co-agent — they can plant
memories that later steer the agent. This is a structural risk class for any
persistent-memory system, catalogued as OWASP Agentic Security ASI06
(Memory & Context Poisoning) and demonstrated by PoisonedRAG, AgentPoison,
and MINJA.

The server sees only the final string — it cannot distinguish a genuine lesson
from an injected one — and automatic graph linking and consolidation can
amplify a poisoned entry's reach into neighboring retrievals. The server-side
levers are audit and quarantine: inspect suspect namespaces (`search_memory`,
`get_memory`, `get_graph_snapshot`) and delete or archive poisoned entries
(`delete_memory`). Content-level vetting is the host agent's responsibility.

## `AGENT_ID` is a cooperative label, not a security boundary

Multi-agent ownership and ACLs key off the `AGENT_ID` environment variable,
configured once at startup by `src/McpEngramMemory/Program.cs`. Any process
that can set an environment variable can claim any identity, and MCP hosts
today cannot inject a verifiable per-subagent identity into a shared server
(tracked upstream as anthropics/claude-code#32514, closed not-planned).

Treat `share_namespace`/`unshare_namespace` ACLs as protection against
accidents, not malice. Real isolation between mutually untrusted agents means
separate server processes with separate data directories or per-tenant
database files — process boundaries are the security boundary.

### What the ACLs actually do

Ownership is claimed by the first write from an agent that has an `AGENT_ID`
set. Once a namespace is owned, other identified agents are refused reads and
writes unless the owner shares it.

Core receives identity through immutable `IPrincipalContext`, which carries the
tenant, agent/principal, system flag, and explicit legacy status. That interface
makes the trust boundary visible and lets authenticated embedding hosts supply
verified claims. It does not turn the stdio server's environment variables into
credentials. `MEMORY_TENANT_ID` and `AGENT_ID` remain operator-controlled,
process-wide bootstrap values.

Two consequences worth stating plainly:

- **Servers that never set `AGENT_ID` are unaffected.** They run as the default
  identity, which has unrestricted access and never claims ownership of
  anything. This is the single-user case and the common deployment. Claiming
  ownership for the default identity would lock an operator out of their own
  data the moment they later set an `AGENT_ID`.
- **A namespace with no owner is open.** Namespaces created before this
  behaviour existed, or by a server running without an `AGENT_ID`, stay
  readable and writable by everyone until an identified agent writes to them
  and claims them. Upgrading does not retroactively protect existing data.

All MCP tool profiles pass through the global Constitution pre/post filter and
the namespace-scoped tools enforce the host principal. Tool-profile selection
does not disable governance. Benchmark tools remain operator capabilities:
their executable and artifact-path inputs are not tenant data-isolation APIs,
and `OpenWorld` metadata tells clients when a tool may invoke an external model.

## Tenant isolation

Multi-tenant isolation is full and applies to every cognitive structure. Memory
entries, index partitions, and namespace ownership records were already
tenant-aware; the association graph (keyed by `(tenant, id)`), semantic clusters
(`(tenant, clusterId)`), lifecycle/collapse support records, decay configs, and
diffusion/spectral bases (`(tenant, ns)`) now are as well. Each `GraphEdge`,
`SemanticCluster`, `CollapseRecord`, and `DecayConfig` carries its own `TenantId`,
persisted inside the serialized blob, so nothing crosses tenants and the same bare
`(ns, id)` may exist independently under two tenants.

A non-empty-tenant principal sees and mutates only its own partition across the
graph, cluster, lifecycle, intelligence, accretion, diffusion, spectral,
maintenance, synthesis, and visualization tools. Graph edges never connect entries
in different tenants (both endpoints are interpreted within the edge's tenant);
cross-namespace association *within* a tenant is preserved. Reads that resolve an
id the caller cannot see return empty/not-found rather than a distinct denial, so
no operation reports existence directly — with one measured exception, documented
under *Known residual* below. The global bare `get_memory`/`delete`
by-id path stays legacy-only, so a bare id can never be probed across tenants.
Background decay, consolidation, auto-link, and accretion run for every tenant.

An identified principal must own or be granted a namespace to reach it — an
unregistered namespace is closed to identified agents, and a write may atomically
claim only a genuinely empty namespace. The empty-tenant/default principal retains
historical behavior as explicit `PrincipalContext.LegacyUnisolated` mode.

`AGENT_ID` and `MEMORY_TENANT_ID` remain host-supplied process configuration, not
authentication: they are an isolation boundary between cooperating identities, not
a defense against a hostile process that can set its own env. For mutually
untrusted tenants, still run separate server processes and data directories.

### Known residual: bare-id topology suppression

An entry's identity is `(tenant, namespace, id)`, and ids are unique only per
`(tenant, namespace)`. Graph adjacency and cluster membership, however, are keyed
`(tenant, id)` with no namespace component, so two entries that share an id in
different namespaces of the **same tenant** share one graph node and one
membership bucket.

Because of that, resolving an id to the twin a caller *can* see is sufficient to
authorize an entry-scoped operation (promote, feedback, a namespace-qualified
delete — each acts on the qualified entry that was authorized) but is **not**
sufficient to authorize a topology operation, which would reach the shared node
and therefore the twin the caller cannot see. Topology operations
(`link_memories`, `unlink_memories`, `get_neighbors`, `traverse_graph`, the edge
and cluster projections of `get_memory` and `cognitive_stats`, `reflect`'s
`relatedIds`, search/recall graph expansion, and the visualization projections)
therefore apply a deliberately **ACL-blind, tenant-wide** duplicate test and fail
closed — refusing writes and withholding topology — when the bare id is duplicated
anywhere in the tenant. This matches the cascade posture already used by
`delete_memory` and `purge_debates`.

**The residual.** Failing closed is itself observable: topology disappearing tells
a caller that *some* entry with that id exists somewhere in its tenant that it
cannot see. Precisely scoped, that leak is:

- **One bit, existence only.** It never discloses the namespace, the content, the
  owning agent, or the edges themselves.
- **Intra-tenant only.** The duplicate test is tenant-scoped, so it cannot be
  observed across a tenant boundary. Where the tenant boundary is the customer
  boundary, this never crosses it.
- **Strictly preferable to the alternative.** Without the guard, the caller would
  obtain the other principal's actual edge ids, relations, weights and metadata,
  and could mutate them.

The cheaper-looking alternative — refusing to create a second entry with the same
id elsewhere in the tenant — was evaluated and rejected: a write rejected because
an id is taken in a namespace the caller cannot see is a *stronger* existence
oracle than the one it would replace, and it would not repair collisions that
already exist.

**Remediation.** Namespace-qualifying persisted graph endpoints and cluster
members removes the shared node, and with it this residual. Tracked as issue #19.
Until then, treat namespace ACLs *within* a single tenant as a separation-of-duty
mechanism between cooperating agents rather than a barrier between mutually
distrusting ones; put mutually distrusting principals in separate tenants, and
mutually untrusted tenants in separate processes.

## Constitutional MCP boundary

The server uses the `ModelContextProtocol` 2.2.0 SDK and registers
`ConstitutionMcpFilter` globally for all call-tool requests. The filter builds a
content-hashed `OperationEnvelope`, evaluates and audits the precondition,
invokes only on allow, and evaluates/audits the postcondition. Every tool also
declares read-only, destructive, idempotent, and open-world metadata.

Preconditions are the authorization boundary. Postconditions run after the tool and
are detection/audit only: a denial is recorded, but is not returned as a false failure
after state may already have committed. Operations needing rollback semantics must use
the governed transactional store, not rely on the adapter filter.

Neither mechanism authenticates the caller: metadata is advisory to MCP clients,
and the Constitution consumes the principal supplied by the host. The shipped
server currently uses an in-memory Constitution provider and a durable file-backed
audit store. Overlay activation is host-managed; an embedding host must load/publish
persisted Constitution versions into the provider. Direct Core callers must invoke `ConstitutionKernel` around
their own governed operations; the MCP filter cannot protect code that bypasses
the MCP adapter.

> **Prior versions.** Through v1.4.0 the ACL model did not function at all. The
> permission check was reached from exactly one tool, nothing ever registered
> namespace ownership, and an unregistered namespace was treated as open — so
> every check passed. `share_namespace` recorded a grant that nothing consulted.
> If you relied on it before this release, assume the data was readable by any
> agent connected to that server.

## Known dependency advisories

- **GHSA-2m69-gcr7-jv3q / CVE-2025-6965 — resolved in 1.5.0.**
  `SQLitePCLRaw` is now pinned to 3.0.5, which is outside the advisory's affected
  range (`<= 2.1.11`). Earlier releases shipped 2.1.6 transitively and documented
  this as accepted risk on the grounds that no patched release existed; a fixed
  release has since shipped, so that reasoning is obsolete.

  Upgrading `Microsoft.Data.Sqlite` does not fix it on its own — 8.0.29 still pulls
  2.1.6 and 9.0.18 pulls only 2.1.10, both affected — so `McpEngramMemory.Core`
  names `SQLitePCLRaw.bundle_e_sqlite3` directly to override the transitive pin.

  For the record, the original reachability argument still holds and is worth
  keeping: exploiting the flaw requires attacker-controlled SQL, and this server
  has none. `SqliteStorageProvider` builds no SQL by interpolation at all;
  `SqlServerStorageProvider` interpolates only an operator-configured schema name
  validated against `^[A-Za-z_][A-Za-z0-9_]*$`, and every value is parameterized.
