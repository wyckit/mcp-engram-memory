# Installation and upgrades

These instructions apply to **2.1.0** and later versions containing the migration
commands. The published 2.0.0 executable does not provide them. Package commands using 2.1.0 require
that version to be published, or an unpublished local feed supplied with `--source`.

## Durable data

The MCP server now stores data in the operating system's user-local application data directory,
under `engram-memory`. Run `engram-memory data-path` to see the exact location. Set
`MEMORY_DATA_PATH` to an absolute directory to select a different location. JSON namespaces,
sharing metadata, graph/index state and the default governance directory live under this root.
Default SQLite uses `<data-root>/memory.db`. Existing `MEMORY_SQLITE_PATH` and
`MEMORY_GOVERNANCE_PATH` overrides take precedence. SQL Server connection/schema settings are unchanged.

Use one server process per data root/database. The concurrent tests exercise multiple requests within
one server; they do not qualify multiple independent writers. Explicitly partition roots when running
separate servers. Tenant and namespace permissions continue to be enforced within each store.

This default is composed by the MCP executable. The public Core storage constructors and their
defaults are unchanged for embedding hosts; those hosts must supply durable paths themselves.

## Preserve 2.0.0 data before updating a tool installation

**Stop every server using the installation before proceeding. Do not first run a normal
`dotnet tool update` or uninstall 2.0.0.** Its default store is inside the versioned `.store`
directory, which the package manager removes on update. A new executable cannot recover an
already deleted directory. If that happened, restore a pre-update backup.

From a checkout containing the new helper, with Python 3.9+ and a .NET SDK installed:

```sh
python scripts/upgrade-tool.py --global --version 2.1.0 --server-stopped
# Or for an existing --tool-path installation:
python scripts/upgrade-tool.py --tool-path /path/to/tools --version 2.1.0 --server-stopped
```

For an unpublished validation package, also supply `--source /path/to/local-feed`.
Use `--legacy-data /path/to/stopped-store` when automatic discovery finds multiple candidates,
and `--data-directory /path/to/new-durable-store` to select the destination explicitly.
The helper installs the new executable separately, copies and verifies the old store into an external
backup and the durable destination, and only then updates the original installation. It prints both
paths. It preserves the source until the package manager replaces that installation.

Keep existing explicit SQLite/governance paths in the client configuration. When selecting a custom
destination, set `MEMORY_DATA_PATH` in that configuration to the same path before restarting.
An external SQLite database remains in place; any version-local governance files are still migrated.
Back up external stores separately while stopped. The helper never merges a different existing
destination or overwrites its newer writes. If the destination already exists, select a new empty
location or deliberately retain the existing external store after inspecting both stores.

Verify the upgrade over MCP: retrieve known memories, check metadata and namespace sharing, and
test scoped recall. Keep the backup until those checks pass. To recover, stop the server and point
`MEMORY_DATA_PATH` at a fresh verified copy of the backup (restore external SQLite/governance backups
and overrides as applicable). Do not merge files into a running store.

## Source installation

Source setup needs Git and **.NET SDK 8 or later**. Full solution development/tests need SDK 10 plus
the .NET 8/9/10 runtimes. The scripts publish only the server's .NET 8 target to `.engram/server` and
configure clients to run that DLL. Windows PowerShell 5.1 and PowerShell 7 are supported. Bash is used
on Linux and Apple Silicon macOS; the current upstream ONNX package does not ship an Intel macOS binary.

```powershell
./setup.ps1 -For all -Profile minimal -DataPath C:/EngramData
```

```sh
bash setup.sh --for all --profile minimal --data-path /path/to/engram-data
```

Existing profile, backend, agent identity and explicit storage overrides are preserved when their
options are omitted. Fresh configurations default to minimal (17 tools) and JSON. Standard exposes
39 tools and full exposes 63. `-ConfigRoot` / `--config-root` selects an alternate client configuration
root for isolated tests. `-SkipConfig` / `--skip-config` publishes the executable without editing clients.

For an old source checkout, stop its server and rerun with `-ServerStopped` / `--server-stopped`.
The installer detects legacy `src/McpEngramMemory/bin/*/net8.0/data` stores and verifies a copy
before changing client configuration. Select `-LegacyData` / `--legacy-data` if more than one exists.
Keep the old source directory as a backup. An old `--no-build` server must remain stopped throughout.

Both scripts use the same JSON/TOML parser. All selected files are parsed before the first edit;
malformed files stop setup unchanged. Successful replacement creates an adjacent
`.engram-backup-<timestamp>-<id>` file. Unrelated values and similarly named servers are preserved.
Symbolic-link configuration files are refused; update their real targets explicitly.
Most TOML formatting is retained; an inline parent table may require reserialization, with the
original bytes retained in the backup. Close client configuration editors during setup. If a later
filesystem write fails, earlier successful replacements remain backed up and their paths are printed.

## Repeatable validation

Use only generated data and new output directories. Python 3.11+ is required for source validation;
the upgrade helper itself requires 3.9+.

```sh
dotnet pack src/McpEngramMemory.Core/McpEngramMemory.Core.csproj -c Release -o artifacts/feed
dotnet pack src/McpEngramMemory.Synthesis.Onnx/McpEngramMemory.Synthesis.Onnx.csproj -c Release -o artifacts/feed
dotnet pack src/McpEngramMemory/McpEngramMemory.csproj -c Release -o artifacts/feed
python scripts/validate-packages.py --package-source artifacts/feed --output artifacts/package-check.json
python scripts/validate-installation.py --package-source artifacts/feed --output artifacts/install-run-1
python scripts/validate-source-installation.py --sdk 8 --shell powershell --output artifacts/source-run-1
# Use --shell bash on Linux/macOS, or --shell pwsh for PowerShell 7.
```

Package checks verify matching versions, release-note links, framework assets, dependency pins and
the bundled Core assembly. Installation validation derives the candidate version from the project
(unless `--version` is supplied), verifies the installed package hash against the local feed, and
installs the actual published 2.0.0, seeds JSON/default SQLite/external SQLite,
upgrades through the helper, then verifies restart durability, metadata, tenant boundaries, sharing,
read-only access, compound recall and all three profiles. Windows also exercises a deeply nested
installation path to catch native-library loading failures. Source validation pins an installed SDK,
runs fresh and repeated setup, launches the generated command, and verifies malformed-config refusal.
CI runs these checks on Windows, Linux and Apple Silicon macOS and uploads the generated evidence.

These checks do not establish SQL Server upgrades, a 48-hour soak, arbitrary Core hosts, multiple
process writers, or fresh live-LLM outcomes. Those require separate qualification before making such
release claims. Publisher `-WhatIf` packs into a unique subdirectory under `artifacts/packages` and
preserves prior validation evidence; it never publishes packages.
