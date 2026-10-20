#!/usr/bin/env python3
"""Upgrade an installed Engram tool after preserving its version-local data.

Stop every Engram process using this installation first. Requires Python 3.9+
and a .NET SDK; uses argument arrays, never a shell or the user's MCP config.
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import uuid


def run(arguments, capture=False):
    result = subprocess.run(arguments, check=True, text=True, encoding="utf-8",
                            stdout=subprocess.PIPE if capture else None)
    return result.stdout if capture else None


def inside(child, parent):
    try:
        child.resolve().relative_to(parent.resolve())
        return True
    except ValueError:
        return False


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    target = parser.add_mutually_exclusive_group(required=True)
    target.add_argument("--global", dest="global_tool", action="store_true")
    target.add_argument("--tool-path", type=Path)
    parser.add_argument("--version", required=True, help="Exact new package version")
    parser.add_argument("--source", action="append", default=[], help="Additional NuGet feed; repeatable")
    parser.add_argument("--data-directory", type=Path, help="Durable target; defaults to MEMORY_DATA_PATH or user app data")
    parser.add_argument("--legacy-data", type=Path, help="Explicit stopped-server data directory or pre-update backup")
    parser.add_argument("--backup-directory", type=Path, help="New backup directory outside the installation")
    parser.add_argument("--server-stopped", action="store_true", required=True,
                        help="Confirm all processes using the old data have been stopped")
    args = parser.parse_args()
    install = (Path(os.environ.get("DOTNET_CLI_HOME", str(Path.home()))) / ".dotnet" / "tools"
               if args.global_tool else args.tool_path).resolve()
    store = install / ".store" / "mcpengrammemory"
    if not store.is_dir():
        raise ValueError(f"No McpEngramMemory installation found at {install}. Use the matching --tool-path or --global.")
    candidates = [p for p in store.glob("*/mcpengrammemory/*/tools/net8.0/any/data")
                  if p.is_dir() and any(f.is_file() for f in p.rglob("*"))]
    if args.legacy_data:
        legacy = args.legacy_data.resolve()
        if not legacy.is_dir():
            raise ValueError(f"Legacy data directory does not exist: {legacy}")
    elif len(candidates) > 1:
        raise ValueError("Multiple version-local stores found. Select the correct stopped store with --legacy-data; nothing was updated.")
    else:
        legacy = candidates[0] if candidates else None

    feeds = [item for source in args.source for item in ("--add-source", source)]
    # Installing the new executable separately leaves the old executable/data intact
    # while its migration implementation verifies both the backup and durable copy.
    with tempfile.TemporaryDirectory(prefix="engram-upgrade-") as staging:
        run(["dotnet", "tool", "install", "McpEngramMemory", "--tool-path", staging,
             "--version", args.version, *feeds])
        executable = str(Path(staging) / ("engram-memory.exe" if os.name == "nt" else "engram-memory"))
        destination = (args.data_directory or Path(json.loads(run([executable, "data-path"], True))["dataPath"])).resolve()
        if inside(destination, install):
            raise ValueError("The durable data directory must be outside the installation directory.")
        backup = None
        if legacy:
            suffix = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
            backup = (args.backup_directory or (destination.parent / (destination.name + "-backups") / suffix)).resolve()
            if inside(backup, install) or inside(backup, destination) or inside(destination, backup):
                raise ValueError("The backup must be outside both the installation and the live data directory.")
            run([executable, "migrate-data", "--source", str(legacy), "--destination", str(backup), "--server-stopped"])
            run([executable, "migrate-data", "--source", str(legacy), "--destination", str(destination), "--server-stopped"])
        else:
            print("No version-local data found; existing external storage is left in place.", flush=True)

        selector = ["--global"] if args.global_tool else ["--tool-path", str(install)]
        run(["dotnet", "tool", "update", "McpEngramMemory", *selector, "--version", args.version, *feeds])
        print(json.dumps({"status": "upgraded", "version": args.version,
                          "dataPath": str(destination), "backup": str(backup) if backup else None}))
        print(f"Keep MEMORY_DATA_PATH={destination} in the server configuration when using a custom location.")
        print("Keep any existing MEMORY_SQLITE_PATH and MEMORY_GOVERNANCE_PATH overrides unchanged.")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"Upgrade stopped: {error}", file=sys.stderr)
        sys.exit(1)
