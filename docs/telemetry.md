# ONNX telemetry policy

This policy applies to the 2.1.0 release candidate's **MCP executable**,
including source installs, the packaged tool, Docker and benchmark subcommands. It does not
retroactively change published 2.0.0 or install process-wide hooks into Core library consumers.

## Startup enforcement

The first executable statement establishes telemetry suppression, before native library
resolution, dependency injection, workers, `RunOptions`, `OrtEnv` or `InferenceSession`.
Inherited missing, `0`, `false` and `1` values all result in suppression. There is no server
telemetry opt-in. Failure to set the native environment stops startup; it is not ignored.
The server writes no telemetry status messages to the MCP stdout stream.

- Linux and macOS: set `ORT_DISABLE_TELEMETRY=1` in **both libc and .NET's environment**.
  .NET's Unix environment setter alone only changes its managed cache. ONNX reads native
  `getenv` and latches this flag before creating its uploader or persistent device identifier.
- Windows: the pinned ONNX 1.29.0 ETW provider **ignores that public flag**. Engram also sets
  `ORT_RUNNING_UNIT_TESTS=1` in this server process before native load. Despite its upstream
  name, in the reviewed runtime this signal only suppresses telemetry; it skips ETW provider
  registration entirely. It does not change inference, optimization, model loading or storage.
  This is a **private upstream compatibility workaround**, not a public ONNX opt-out contract.
  Windows startup refuses a managed ONNX version other than 1.29.0.0 until the policy is reviewed.
- The Docker image additionally supplies `ORT_DISABLE_TELEMETRY=1` at process creation.
  Executable enforcement still applies if a caller overrides the image environment.

Calling `OrtEnv.Instance().DisableTelemetryEvents()` alone is insufficient: creating `OrtEnv`
can emit startup telemetry before the call. We do not use null native handles, patch native
binaries, impersonate CI, or change operating-system telemetry settings.

The policy is scoped to the executable. A host embedding `McpEngramMemory.Core` or the optional
ONNX synthesis library controls its own process and may have initialized ONNX already.
Such hosts must establish their own suppression before any ONNX use; preferably set the native
process environment in their launcher. This server change cannot revoke events emitted earlier
by another component. Optional remote storage, synthesis and live benchmark services retain
their configured networking behavior.

## Evidence and regression checks

`scripts/validate-telemetry.py` runs in native Windows x64, Linux x64 and macOS arm64 CI jobs:

1. Remove all 13 CI variables consulted by ONNX, all inherited ORT flags, and Engram settings
   from the validation subprocess environment. This prevents the CI runner itself from hiding a bug.
2. Copy a published executable into a disposable directory and temporarily replace its ONNX
   library with a test library. Its native load constructor snapshots the native environment;
   its first API reports that snapshot and exits with a sentinel. Assert suppression for unset,
   disabled and already-enabled inherited values. Restore the real binary in a `finally` block.
3. Use the restored, real ONNX model to store, recall and restart under minimal, standard and full
   profiles with deliberately conflicting inherited opt-out values. Check the 17/39/63 tool counts
   and fail on non-protocol stdout or abnormal shutdown.
4. On Windows, start a local ETW collector before any control or server process. An unsuppressed
   synthetic native `OrtEnv` must emit events; a suppressed control and all six MCP server processes
   must emit zero ONNX events. The control loads no user memory or model. Trace files stay in the
   validation output. An unelevated collector failure fails validation instead of silently skipping it.

Run locally with Python 3.11+, CMake, a C compiler and .NET SDK 8+ (Windows ETW requires elevation):

```sh
dotnet publish src/McpEngramMemory/McpEngramMemory.csproj -c Release -f net8.0 -p:TargetFrameworks=net8.0 -o artifacts/telemetry-server
python scripts/validate-telemetry.py --server artifacts/telemetry-server/McpEngramMemory.dll --output artifacts/telemetry-check
```

The output directory must be new. `summary.json`, native probe logs, MCP wire logs and Windows
ETW traces are uploaded by CI. Existing installer/upgrade and frozen strict recall gates remain
required. No MCP schema, retrieval ranking, tenant/namespace scope, data format or Core public API
is changed by this policy.

These checks establish native initialization ordering, real inference behavior and Windows ETW
suppression. They are **not a general packet capture** or a guarantee about every optional network
backend. Windows ARM64, Intel macOS and other runtime identifiers are not qualified by this matrix.

## Dependency upgrade checklist

Before changing ONNX, review the pinned upstream suppression paths and every consumer of the
private Windows signal, replace it with a public full suppression mechanism when available,
update the version guard, and pass the native/ETW and recall gates. Do not merely loosen the guard.
A telemetry-free upstream build is an alternative if the private signal ceases to be suitable.

Sources reviewed at ONNX tag `v1.29.0`, commit `2e2543fbe9fae542f921d47a72d21d5a4ef0b710`:

- [ONNX privacy and opt-out documentation](https://github.com/microsoft/onnxruntime/blob/v1.29.0/docs/Privacy.md)
- [Native environment and CI suppression](https://github.com/microsoft/onnxruntime/blob/v1.29.0/onnxruntime/core/platform/telemetry_environment.h)
- [Windows provider registration and event guards](https://github.com/microsoft/onnxruntime/blob/v1.29.0/onnxruntime/core/platform/windows/telemetry.cc)
- [POSIX uploader initialization](https://github.com/microsoft/onnxruntime/blob/v1.29.0/onnxruntime/core/platform/posix/telemetry.cc)
- [.NET 8 Unix managed environment implementation](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Private.CoreLib/src/System/Environment.Variables.Unix.cs)
