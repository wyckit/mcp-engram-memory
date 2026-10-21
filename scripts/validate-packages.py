#!/usr/bin/env python3
"""Verify the actual versioned package set before installation or publication."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET
import zipfile

PACKAGES = ("McpEngramMemory.Core", "McpEngramMemory.Synthesis.Onnx", "McpEngramMemory")
FRAMEWORKS = ("net8.0", "net9.0", "net10.0")


def package_version(repo):
    versions = {name: ET.parse(repo / "src" / name / (name + ".csproj")).findtext(".//Version")
                for name in PACKAGES}
    assert all(versions.values()) and len(set(versions.values())) == 1, versions
    return versions[PACKAGES[0]]


def verify_packages(source, version):
    results, core_binary = [], None
    for name in PACKAGES:
        package = source / f"{name}.{version}.nupkg"
        assert package.is_file(), f"Missing package: {package}"
        assert package.stat().st_size < 250 * 1024**2, f"Oversized package: {package}"
        with zipfile.ZipFile(package) as archive:
            files = archive.namelist()
            nuspecs = [f for f in files if f.endswith(".nuspec")]
            assert len(nuspecs) == 1, (name, nuspecs)
            root = ET.fromstring(archive.read(nuspecs[0]))
            metadata = root.find("{*}metadata")
            assert metadata.findtext("{*}id") == name, name
            assert metadata.findtext("{*}version") == version, (name, "version mismatch")
            assert f"docs/release-notes-{version}.md" in metadata.findtext("{*}releaseNotes", ""), (name, "stale release notes")
            assert {"README.md", "icon.png"} <= set(files), (name, "missing package documentation")
            dependencies = {}
            if name != "McpEngramMemory":
                groups = metadata.findall("{*}dependencies/{*}group")
                assert {g.get("targetFramework") for g in groups} == set(FRAMEWORKS), (name, "framework dependencies")
                for group in groups:
                    framework = group.get("targetFramework")
                    assert {f"lib/{framework}/{name}.dll", f"lib/{framework}/{name}.xml"} <= set(files), (name, framework)
                    deps = {d.get("id"): d.get("version") for d in group}
                    dependencies[framework] = deps
                    if name == "McpEngramMemory.Synthesis.Onnx":
                        assert deps.get("McpEngramMemory.Core") == version, (name, "Core dependency mismatch")
                    else:
                        pkcs = "8.0.2" if framework == "net8.0" else "9.0.21"
                        assert deps.get("System.Security.Cryptography.Pkcs") == pkcs, (name, "PKCS servicing pin")
                        assert deps.get("SQLitePCLRaw.bundle_e_sqlite3") == "3.0.5", (name, "SQLite servicing pin")
                if name == "McpEngramMemory.Core":
                    assert {f"{folder}/{name}.targets" for folder in ("build", "buildMultiTargeting")} <= set(files)
                    core_binary = archive.read(f"lib/net8.0/{name}.dll")
            else:
                prefix = "tools/net8.0/any/"
                settings = ET.fromstring(archive.read(prefix + "DotnetToolSettings.xml"))
                command = settings.find(".//Command")
                assert command is not None and command.get("Name") == "engram-memory" and command.get("EntryPoint") == "McpEngramMemory.dll", "Tool command changed"
                assert archive.read(prefix + "McpEngramMemory.Core.dll") == core_binary, "Bundled Core differs from the library package"
                runtime = json.loads(archive.read(prefix + "McpEngramMemory.runtimeconfig.json"))
                assert runtime["runtimeOptions"]["tfm"] == "net8.0", "Tool runtime changed"
                assert not any(re.search(r"/runtimes/(ios|android|maccatalyst|browser)[^/]*/", f) for f in files), "Mobile native runtime in tool"
                for rid in ("win-x64", "linux-x64", "osx-arm64"):
                    assert any(f.startswith(prefix + f"runtimes/{rid}/native/") and "onnxruntime" in f for f in files), (rid, "ONNX runtime missing")
            results.append({"package": name, "version": version, "bytes": package.stat().st_size,
                            "sha256": hashlib.sha256(package.read_bytes()).hexdigest(), "dependencies": dependencies})
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parent.parent
    result = {"status": "passed", "version": package_version(repo),
              "packages": verify_packages(args.package_source, package_version(repo))}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    # A report must belong to this invocation, never silently replace old evidence.
    with args.output.open("x", encoding="utf-8") as report:
        json.dump(result, report, indent=2)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
