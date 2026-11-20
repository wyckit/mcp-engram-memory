#!/usr/bin/env python3
"""Exercise prebuilt publication with tiny fixtures and a mocked dotnet; never upload."""
import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import uuid

PACKAGES = ("McpEngramMemory.Core", "McpEngramMemory.Synthesis.Onnx", "McpEngramMemory")
TEST_KEY = "publisher-test-key-not-a-credential"

# This child process has no real dotnet on PATH. Even the success/failure upload
# branches invoke only this function; no registry credentials or network are used.
WRAPPER = r"""
param([string]$CaseRoot)
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath (Join-Path $CaseRoot 'case.json') -Raw | ConvertFrom-Json
$global:MockCalls = 0
$global:MockConfig = $config
$global:MockRoot = $CaseRoot
$global:LASTEXITCODE = 0
function global:dotnet {
    $call = @($args)
    if ($call.Count -ne 8 -or $call[0] -ne 'nuget' -or $call[1] -ne 'push') {
        throw 'Unexpected command; building and packing are forbidden in the publisher.'
    }
    $global:MockCalls++
    if ($global:MockConfig.checkLocks) {
        $locked = $false
        $probe = $null
        try {
            $probe = [IO.File]::Open($call[2], 'Open', 'Write', 'None')
        } catch [IO.IOException] {
            $locked = $true
        } finally {
            if ($null -ne $probe) { $probe.Dispose() }
        }
        if (-not $locked) { throw 'Verified package can be overwritten during upload.' }
    }
    Add-Content -LiteralPath (Join-Path $global:MockRoot 'calls.jsonl') -Encoding UTF8 -Value (
        ConvertTo-Json -InputObject @{ arguments = $call } -Compress)
    $global:LASTEXITCODE = if ($global:MockCalls -eq $global:MockConfig.failAt) { 7 } else { 0 }
    Write-Output 'Simulated dotnet output; no network operation occurred.'
}
function global:Read-Host {
    param([string]$Prompt, [switch]$AsSecureString)
    if (-not $AsSecureString) { throw 'Credential prompt must hide input.' }
    Add-Content -LiteralPath (Join-Path $global:MockRoot 'prompts.txt') -Value 'hidden-key-prompt'
    if ($global:MockConfig.emptyKey) { return [Security.SecureString]::new() }
    $secure = [Security.SecureString]::new()
    foreach ($character in 'publisher-test-key-not-a-credential'.ToCharArray()) {
        $secure.AppendChar($character)
    }
    $secure.MakeReadOnly()
    return $secure
}
$publisherArguments = @{}
foreach ($property in $config.arguments.PSObject.Properties) {
    $publisherArguments[$property.Name] = $property.Value
}
& (Join-Path $CaseRoot 'publish-nuget.ps1') @publisherArguments
exit $LASTEXITCODE
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True, type=Path)
    parser.add_argument("--output", type=Path, help="New evidence directory; never overwritten")
    args = parser.parse_args()
    repo = args.repo.resolve()
    evidence = (args.output or repo / "artifacts/publisher-check" / uuid.uuid4().hex).resolve()
    evidence.mkdir(parents=True, exist_ok=False)
    shells = [Path(p) for name in ("powershell", "pwsh") if (p := shutil.which(name))]
    if not shells:
        raise RuntimeError("PowerShell is required for publisher regression tests")
    wrapper = evidence / "mock-publisher.ps1"
    wrapper.write_text(WRAPPER, encoding="utf-8")
    cases = [
        "dry-run-default", "dry-run-explicit", "upload-plan", "hidden-prompt",
        "environment-key", "empty-key", "failed-first", "failed-second",
        "missing-last", "tampered-last", "wrong-size", "unpassed-manifest",
        "wrong-version", "missing-record", "duplicate-record", "invalid-hash",
        "reordered-manifest", "extra-package-file", "reject-github", "reject-source",
    ]
    results = []
    for shell in shells:
        for case in cases:
            case_root = evidence / shell.stem / case
            case_root.mkdir(parents=True)
            shutil.copyfile(repo / "publish-nuget.ps1", case_root / "publish-nuget.ps1")
            feed = case_root / ("artifacts/release-2.1.0/publication/packages"
                                if case == "dry-run-default" else "input feed [2.1]")
            feed.mkdir(parents=True)
            manifest_path = case_root / "scripts/release-packages.json"
            manifest_path.parent.mkdir()
            records = []
            for name in PACKAGES:
                # Tiny opaque archives test publication orchestration only. The real
                # release's full package contents are checked by validate-packages.py.
                path = feed / f"{name}.2.1.0.nupkg"
                path.write_bytes((name + " validated fixture").encode())
                records.append({"package": name, "version": "2.1.0",
                                "bytes": path.stat().st_size,
                                "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
            manifest = {"status": "passed", "version": "2.1.0", "packages": records}
            if case == "missing-last":
                (feed / "McpEngramMemory.2.1.0.nupkg").rename(feed / "missing.nupkg")
            elif case == "tampered-last":
                path = feed / "McpEngramMemory.2.1.0.nupkg"
                content = path.read_bytes()
                path.write_bytes(bytes([content[0] ^ 1]) + content[1:])
            elif case == "wrong-size":
                records[-1]["bytes"] += 1
            elif case == "unpassed-manifest":
                manifest["status"] = "failed"
            elif case == "wrong-version":
                records[-1]["version"] = "2.0.0"
            elif case == "missing-record":
                records.pop()
            elif case == "duplicate-record":
                records[-1] = copy.deepcopy(records[0])
            elif case == "invalid-hash":
                records[-1]["sha256"] = "invalid"
            elif case == "reordered-manifest":
                records.reverse()
            elif case == "extra-package-file":
                (feed / "Other.99.0.0.nupkg").write_bytes(b"must never upload")
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            sentinel = case_root / "existing-evidence.txt"
            sentinel.write_text("Preserve existing evidence.\n", encoding="utf-8")
            protected = [sentinel, manifest_path, *feed.glob("*.nupkg")]
            before = {p: hashlib.sha256(p.read_bytes()).hexdigest() for p in protected}

            parameters = {"PackageDirectory": str(feed), "ValidationManifest": str(manifest_path)}
            if case == "dry-run-default":
                parameters = {"WhatIf": True}
            elif case == "dry-run-explicit":
                parameters["WhatIf"] = True
            elif case not in ("hidden-prompt", "environment-key", "empty-key"):
                parameters["ApiKey"] = TEST_KEY
            if case == "reject-github":
                parameters["GitHubToken"] = "not-a-credential"
            elif case == "reject-source":
                parameters["Source"] = "https://example.invalid/not-nuget"
            config = {"arguments": parameters, "emptyKey": case == "empty-key",
                      "checkLocks": os.name == "nt",
                      "failAt": {"failed-first": 1, "failed-second": 2}.get(case, 0)}
            (case_root / "case.json").write_text(json.dumps(config), encoding="utf-8")
            env = os.environ.copy()
            env.pop("NUGET_API_KEY", None)
            env["PATH"] = ""  # The mock function is the only available dotnet.
            if case == "environment-key":
                env["NUGET_API_KEY"] = TEST_KEY
            completed = subprocess.run(
                [str(shell), "-NoProfile", "-NonInteractive", "-File", str(wrapper),
                 "-CaseRoot", str(case_root)],
                cwd=evidence, env=env, capture_output=True, text=True,
                encoding="utf-8", errors="replace", timeout=30)
            output = completed.stdout + completed.stderr
            (case_root / "publisher.log").write_text(output, encoding="utf-8")
            call_log = case_root / "calls.jsonl"
            calls = ([json.loads(line)["arguments"] for line in
                      call_log.read_text(encoding="utf-8-sig").splitlines()]
                     if call_log.exists() else [])
            success = case in ("dry-run-default", "dry-run-explicit", "upload-plan",
                               "hidden-prompt", "environment-key", "reordered-manifest",
                               "extra-package-file")
            expected_calls = (3 if success and not case.startswith("dry-run") else
                              {"failed-first": 1, "failed-second": 2}.get(case, 0))
            assert (completed.returncode == 0) == success, (shell, case, output)
            assert len(calls) == expected_calls, (shell, case, calls, output)
            for i, call in enumerate(calls):
                assert call == ["nuget", "push", str(feed / f"{PACKAGES[i]}.2.1.0.nupkg"),
                                "--api-key", TEST_KEY, "--source",
                                "https://api.nuget.org/v3/index.json", "--skip-duplicate"], call
            assert (case_root / "prompts.txt").exists() == (case in ("hidden-prompt", "empty-key"))
            assert TEST_KEY not in output, (shell, case, "key leaked to console")
            if not success:
                assert "upload commands completed" not in output, (case, output)
            if case.startswith("dry-run"):
                assert "nothing built or uploaded" in output, output
            if case == "failed-second":
                assert "Earlier uploads completed or already existed: McpEngramMemory.Core." in output
            assert before == {p: hashlib.sha256(p.read_bytes()).hexdigest() for p in protected}
            for path in feed.glob("*.nupkg"):
                with path.open("r+b"):  # Every handle must be released, including error exits.
                    pass
            results.append({"shell": shell.stem, "case": case, "status": "passed",
                            "mock_upload_calls": len(calls)})
            print(f"{shell.stem}: {case} passed", flush=True)

    summary = {"whatIf": "passed", "existingEvidencePreserved": True,
               "uploads": "none; dotnet mocked and real dotnet absent from child PATH",
               "casesPassed": len(results), "results": results}
    (evidence / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(f"Evidence: {evidence}")


if __name__ == "__main__":
    main()
