# Security Policy

## Supported versions

Security fixes are provided for the latest minor release on the current major
version line — currently the `v1.3.x` line.

| Version  | Supported          |
|----------|--------------------|
| 1.3.x    | :white_check_mark: |
| < 1.3    | :x:                |

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
read once at startup (`src/McpEngramMemory/Program.cs`, line 118). Any process
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

Enforcement currently covers the tools in the default `minimal` profile.
Extending it to the `standard` and `full` profiles is in progress; until that
lands, enabling those profiles widens the surface beyond what these checks
cover.

> **Prior versions.** Through v1.4.0 the ACL model did not function at all. The
> permission check was reached from exactly one tool, nothing ever registered
> namespace ownership, and an unregistered namespace was treated as open — so
> every check passed. `share_namespace` recorded a grant that nothing consulted.
> If you relied on it before this release, assume the data was readable by any
> agent connected to that server.

## Known dependency advisories

- **GHSA-2m69-gcr7-jv3q / CVE-2025-6965 — `SQLitePCLRaw.lib.e_sqlite3` 2.1.6**
  (transitive via `Microsoft.Data.Sqlite` 8.0.11,
  `McpEngramMemory.Core.csproj` line 50). Documented **accepted risk**: no
  patched release exists (all versions ≤ 2.1.11 ship an affected e_sqlite3
  build), and the vulnerable path is not reachable — exploiting it requires
  attacker-controlled SQL, and the server has zero string-interpolated SQL
  reachable by agent content: `SqliteStorageProvider` interpolates nothing at
  all; `SqlServerStorageProvider` interpolates only an operator-configured
  schema name validated against `^[A-Za-z_][A-Za-z0-9_]*$`
  (`src/McpEngramMemory.Core/Services/Storage/SqlServerStorageProvider.cs`,
  line 25); all values are parameterized. Will be picked up via a
  `Microsoft.Data.Sqlite` bump when a fixed SQLitePCLRaw ships.
