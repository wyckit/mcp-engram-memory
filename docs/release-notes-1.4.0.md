Fixes a decay/consolidation outage that had been failing silently, replaces the benchmark
regression gate's fixed tolerance with a statistical test, and splits the ONNX synthesis backend
into its own package so the server tool fits on nuget.org at all.

**Rolls up v1.3.0**, which was tagged but never published to any feed.

## The headline fix: lifecycle maintenance was silently dead

`engram_status` reported **179 completed decay cycles that processed zero entries**, and
consolidation had run twice in two days. Both carried the same exception:

```
Q after final power iteration: <col 0, col 22> = -0.09042045, expected 0.
```

Two independent defects, diagnosed against real production graphs rather than synthetic fixtures:

**1. Exactly rank-deficient panels in the eigensolver.** Entries with no positive-relation edges get
`invSqrtDeg = 0`, making their rows and columns of `M = D^-1/2 W D^-1/2` exactly zero. When a
namespace's *linked* node count falls below the sketch width (106), the panel is exactly rank `r`,
and Gram-Schmidt's axis-replacement fallback normalized float32 cancellation noise (~1e-7) into a
corrupt column — because the degenerate-column threshold was an absolute `1e-10`, four orders of
magnitude below the noise floor. Both thresholds are now rank-revealing, and
`MemoryDiffusionKernel` structurally deflates isolated entries out of the eigenproblem entirely.

**2. No fault isolation in the lifecycle passes.** One throwing namespace aborted the entire cycle,
silently skipping every namespace after it. Decay and consolidation now catch per namespace and
continue; decay falls back to pointwise when the spectral step fails, and partial failures are
reported in the cycle summary rather than collapsed into a single error string.

A latent bug fell out of the same root cause: isolated entries have zero rows in every eigenvector,
so their decay debt was being attenuated — or zeroed outright in large namespaces — instead of
passing through. An isolated node is its own graph component with `λ_L = 0`, so identity is the
correct filter response.

Verified in production: decay now processes 9,434 entries per cycle and consolidation 4,474, with
**0 of 778 namespaces failing** basis computation.

## Statistical benchmark regression gate

The CI gate compared aggregate point estimates against pinned baselines with a flat 0.02 absolute
tolerance. On an 18-query dataset a single query is worth 0.056 of Recall@5 — the threshold was
**smaller than the metric's own quantization step**, so one flipped query failed the build.

It now fails only when a Holm-Bonferroni-corrected one-sided paired t-test over per-query deltas is
significant **and** the mean drop exceeds a minimum detectable effect. Absolute floors are unchanged.
Per Urbano et al. (SIGIR 2019), the t-test tracks nominal alpha where bootstrap-shift is biased and
Wilcoxon is unreliable.

## Packaging: 367 MB → 75 MB

The `McpEngramMemory` tool packed to **367 MB**, over nuget.org's 250 MB ceiling — it could not be
published at all. Two causes:

- **`Microsoft.ML.OnnxRuntimeGenAI` was a `Core` dependency (~500 MB of natives).** It backs exactly
  one class, used only when `SYNTHESIS_BACKEND=onnx`, which is not the default. Every consumer of
  `McpEngramMemory.Core` was silently downloading a local LLM inference runtime.
- **Mobile natives in a desktop tool.** ONNX Runtime ships iOS xcframeworks, Android `.aar`s, Mac
  Catalyst, and browser-wasm — ~81 MB that a .NET 8 console process cannot execute.

## Packages

| Package | Size | Purpose |
|---|---|---|
| `McpEngramMemory` | 75 MB | MCP server, installable as a `dotnet` global tool |
| `McpEngramMemory.Core` | 2.2 MB | The engine, embeddable in your own .NET app |
| `McpEngramMemory.Synthesis.Onnx` | 86 KB | **New** — optional in-process ONNX synthesis backend |

```bash
dotnet tool install --global McpEngramMemory --version 1.4.0
dotnet add package McpEngramMemory.Core --version 1.4.0
```

First nuget.org release of the server package; previously it went only to GitHub Packages.

## Breaking changes

- **Binary-breaking for `McpEngramMemory.Core` consumers.** `DecayCycleResult` gained three trailing
  optional positional parameters and `ConsolidationResult` gained one, so partial failures can be
  reported. Source-compatible — code recompiles unchanged — but assemblies built against 1.2.0 must
  be rebuilt.
- **`Core` no longer references `Microsoft.ML.OnnxRuntimeGenAI`.** Add
  `McpEngramMemory.Synthesis.Onnx` if you use `OnnxGenAiTextGenerator`; it keeps its
  `McpEngramMemory.Core.Services.Synthesis` namespace, so your code compiles unchanged.
- **The server no longer accepts `SYNTHESIS_BACKEND=onnx`.** It fails at startup naming the
  replacement package rather than silently falling back to Ollama. Applies to source builds, Docker,
  and the published tool alike. `SYNTHESIS_BACKEND=ollama` (the default) is unchanged.
- **Namespaces whose *linked* core is below 32 nodes now bypass spectral processing** even when total
  entry count qualifies. Decay falls back to pointwise, consolidation skips them.

## Also fixed

- **`reflect` rejected two of its three documented `relatedIds` shapes.** The parameter was typed
  `string[]?`, so a single id or comma-separated list failed MCP model binding *before the tool body
  ran*, surfacing only as the SDK's generic `An error occurred invoking 'reflect'.` It now binds
  tolerantly via `StringListNormalizer` and reports the parameter and expected shapes on a genuine
  mismatch.
- **Tool-surface documentation drift.** The documented count of 65 dated to v0.9.0; the real number
  is **62** (profiles: `minimal` 17 / `standard` 39 / `full` 62 — `standard` was documented as 41).
  The README listed four tools removed in v1.1 and omitted five that exist. A new
  `ToolSurfaceCountTests` asserts the counts by reflection so they cannot silently rot again.
- **`SECURITY.md`** now documents memory poisoning as a structural risk class (OWASP ASI06), the
  `AGENT_ID` trust boundary (a cooperative label, not a security boundary), and GHSA-2m69-gcr7-jv3q
  as accepted risk with the reachability argument.
- All three packages now ship an embedded icon.

## Verification

1,138 tests pass on each of net8.0, net9.0, and net10.0. The packed tool was verified by running it —
not only by unit tests, which build from project references and would not catch a broken publish
output.
