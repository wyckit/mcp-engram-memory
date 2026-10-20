#!/usr/bin/env python3
"""Exercise real 2.0.0 -> candidate package updates using generated, isolated data."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
from validation.mcp_client import Client


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--package-source", required=True, type=Path)
    p.add_argument("--version", required=True)
    p.add_argument("--output", required=True, type=Path, help="New directory; existing results are never reused")
    a = p.parse_args()
    output = a.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    repo = Path(__file__).resolve().parent.parent
    source = a.package_source.resolve()
    result = {"platform": platform.platform(), "version": a.version, "backends": []}

    def command(args, name):
        with (output / f"{name}.log").open("w", encoding="utf-8") as log:
            subprocess.run([str(x) for x in args], check=True, stdout=log, stderr=subprocess.STDOUT,
                           text=True, encoding="utf-8")

    for backend in ("json", "sqlite", "sqlite-external"):
        install = output / ("tools-" + backend)
        durable = output / ("data-" + backend)
        backup = output / ("backup-" + backend)
        command(["dotnet", "tool", "install", "McpEngramMemory", "--version", "2.0.0", "--tool-path", install,
                 "--source", "https://api.nuget.org/v3/index.json"], "install-old-" + backend)
        exe = install / ("engram-memory.exe" if os.name == "nt" else "engram-memory")
        env = {"MEMORY_STORAGE": "json" if backend == "json" else "sqlite", "MEMORY_DATA_PATH": str(durable)}
        if backend == "sqlite-external":
            env["MEMORY_SQLITE_PATH"] = str(output / "external" / "memory.db")

        def client(label, agent="owner", tenant="tenant-a", profile="minimal"):
            return Client([str(exe)], output, f"{backend}-{label}", {
                **env, "AGENT_ID": agent, "MEMORY_TENANT_ID": tenant, "MEMORY_TOOL_PROFILE": profile})

        with client("seed") as c:
            assert c.call("whoami")["agentId"] == "owner"
            for identity, ns, text in [
                ("owner", "public", "Citrine production deployment is owned by Nessa Vale."),
                ("backup", "public", "Citrine production backups run every six hours."),
                ("private", "private", "Citrine production private answer is PRIVATE_SCOPE_SENTINEL.")]:
                assert c.call("remember", id=identity, ns=ns, text=text, lifecycleState="ltm",
                              metadata={"validation": "published-2.0.0"})["status"] == "stored"
            c.call("share_namespace", ns="public", agentId="reader", accessLevel="read")
            original = {key: c.call("get_memory", id=key) for key in ("owner", "backup", "private")}
        with client("seed-foreign", tenant="tenant-b") as c:
            assert c.call("remember", id="owner", ns="public", text="Citrine production owner is FOREIGN_TENANT_SENTINEL.",
                          lifecycleState="ltm")["status"] == "stored"

        command([sys.executable, repo / "scripts/upgrade-tool.py", "--tool-path", install,
                 "--version", a.version, "--source", source, "--data-directory", durable,
                 "--backup-directory", backup, "--server-stopped"], "upgrade-" + backend)
        assert not (install / ".store/mcpengrammemory/2.0.0").exists()
        if backend != "sqlite-external":
            assert (durable / "_engram-migration.json").is_file()
            assert (backup / "_engram-migration.json").is_file()

        for phase in ("upgraded", "restart"):
            with client(phase) as c:
                for key, before in original.items():
                    after = c.call("get_memory", id=key)
                    for field in ("entry", "text", "metadata", "lifecycleState", "createdAt", "edges", "clusterIds"):
                        assert before[field] == after[field], (backend, phase, key, field)
                recalled = c.call("recall", query="Who owns Citrine production deployment, and how often are backups taken?", ns="public")
                assert {x["id"] for x in recalled["results"]} == {"owner", "backup"}, recalled
                assert "FOREIGN_TENANT_SENTINEL" not in json.dumps(recalled)
            with client(phase + "-reader", agent="reader") as c:
                assert isinstance(c.call("get_memory", id="owner"), dict)
                assert isinstance(c.call("get_memory", id="private"), str)
                denied = c.call("remember", id="forbidden", ns="public", text="Forbidden reader write")
                assert isinstance(denied, str) and denied.startswith("Error:"), denied
                recalled = c.call("recall", query="Who owns Citrine production deployment?", k=10, minScore=0)
                assert "PRIVATE_SCOPE_SENTINEL" not in json.dumps(recalled)
                assert "FOREIGN_TENANT_SENTINEL" not in json.dumps(recalled)
            with client(phase + "-foreign", tenant="tenant-b") as c:
                assert c.call("get_memory", id="owner")["text"].endswith("FOREIGN_TENANT_SENTINEL.")

        counts = {}
        for profile, count in (("minimal", 17), ("standard", 39), ("full", 63)):
            with client("profile-" + profile, profile=profile) as c:
                actual = c.request("tools/list")["tools"]
                assert len(actual) == count, (profile, len(actual))
                counts[profile] = len(actual)
                if profile == "standard": c.call("list_clusters", ns="public")
                if profile == "full": c.call("get_domain_tree")
        result["backends"].append({"backend": backend, "upgrade": "passed", "restart": "passed",
            "scopeAndMetadata": "passed", "profiles": counts})
        (output / "summary.json").write_text(json.dumps(result, indent=2), encoding="utf-8")

    # Exercise the Windows loader limit explicitly, independently of checkout path.
    fresh = output / "fresh-tool"
    if os.name == "nt":
        fresh = fresh / ("long-installation-path-" * 5)
    command(["dotnet", "tool", "install", "McpEngramMemory", "--version", a.version, "--tool-path", fresh,
             "--add-source", source], "install-fresh")
    executable = fresh / ("engram-memory.exe" if os.name == "nt" else "engram-memory")
    with Client([str(executable)], output, "fresh-start", {"MEMORY_DATA_PATH": str(output / "fresh-data")}) as c:
        assert len(c.request("tools/list")["tools"]) == 17
        assert c.call("remember", id="fresh", ns="fresh", text="Fresh installation retains this fact.")["status"] == "stored"
    with Client([str(executable)], output, "fresh-restart", {"MEMORY_DATA_PATH": str(output / "fresh-data")}) as c:
        assert c.call("get_memory", id="fresh")["text"] == "Fresh installation retains this fact."
    result["freshInstallation"] = "passed"
    package = source / f"McpEngramMemory.{a.version}.nupkg"
    result["packageSha256"] = hashlib.sha256(package.read_bytes()).hexdigest()
    (output / "summary.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
