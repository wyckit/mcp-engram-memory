# Security Policy

## Supported versions

Security fixes are provided for the latest minor release on the current major
version line. While the project is on the `0.x` series, this means the most
recent `0.y.z` tag (currently the `v0.8.x` line).

| Version  | Supported          |
|----------|--------------------|
| 0.8.x    | :white_check_mark: |
| < 0.8    | :x:                |

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
- Published NuGet packages `McpEngramMemory` and `McpEngramMemory.Core`.
- MCP tool surfaces (input handling, serialization, persistence).

Out of scope:

- Issues in third-party dependencies — please report those upstream. We will
  pick up fixes as dependency updates.
- Denial-of-service via resource exhaustion in a process you control (e.g.
  feeding the server a 10 GB prompt). The server trusts its operator.
- Local filesystem or SQLite access by someone who already has write access
  to the data directory.
