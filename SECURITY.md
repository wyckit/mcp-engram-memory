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

## Tenant isolation and global bare-ID structures

Memory entries, index partitions, namespace ownership records, and provider CRUD
are tenant-aware. This does **not** yet extend to every cognitive support
structure. The association graph, semantic clusters, lifecycle/collapse support
records, and diffusion caches still use global bare entry IDs. Treating those
structures as tenant-qualified would allow collisions and cross-tenant reads or
mutations.

The current server therefore fails closed for non-empty-tenant principals before
affected graph, cluster, lifecycle, intelligence, accretion, diffusion, spectral,
maintenance, synthesis, and visualization operations reach global structures.
Read-shaped operations return empty/not-found results; mutations return an
unavailable/error result. Tenant-scoped debate purge deletes tenant entries but
intentionally skips global graph and cluster cascades. The empty-tenant/default
principal retains historical behavior as explicit
`PrincipalContext.LegacyUnisolated` mode.

This is a containment boundary, not full tenant-qualified graph support. If a
tenant needs graph/lifecycle behavior today, use a dedicated server process and
data directory for that tenant. Do not disable or bypass the fail-closed guard.

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
  `SQLitePCLRaw` is now pinned to 2.1.12, which is outside the advisory's affected
  range (`<= 2.1.11`). Earlier releases shipped 2.1.6 transitively and documented
  this as accepted risk on the grounds that no patched release existed; 2.1.12 has
  since shipped, so that reasoning is obsolete.

  Upgrading `Microsoft.Data.Sqlite` does not fix it on its own — 8.0.29 still pulls
  2.1.6 and 9.0.18 pulls only 2.1.10, both affected — so `McpEngramMemory.Core`
  names `SQLitePCLRaw.bundle_e_sqlite3` directly to override the transitive pin.

  For the record, the original reachability argument still holds and is worth
  keeping: exploiting the flaw requires attacker-controlled SQL, and this server
  has none. `SqliteStorageProvider` builds no SQL by interpolation at all;
  `SqlServerStorageProvider` interpolates only an operator-configured schema name
  validated against `^[A-Za-z_][A-Za-z0-9_]*$`, and every value is parameterized.
