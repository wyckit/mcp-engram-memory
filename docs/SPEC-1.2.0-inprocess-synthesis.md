# Dev Spec — McpEngramMemory.Core 1.2.0: in-process synthesis backend

**Goal:** Let `synthesize_memories` run a local SLM (Qwen2.5-Instruct) **fully in-process via ONNX Runtime GenAI**, removing the hard dependency on an external Ollama daemon. Mirrors how `OnnxEmbeddingService` already hosts the embedding model in-process.

**Status:** A complete reference implementation exists on branch **`feature/inprocess-synthesis-1.2.0`**, based on **`origin/main` @ `dec2e94`** (current tip as of 2026-06-03). This spec lets an engineer review/finalize it (or rebuild from scratch), then pack + push the NuGet. **Additive, non-breaking** — no existing public API removed; the legacy Ollama path is preserved.

**Base note:** the reference work was authored against tag `v1.1.0` then re-applied onto `origin/main @ dec2e94` (65 commits ahead of v1.1.0). All edited tracked files were byte-identical between v1.1.0 and that tip, so it transplanted with zero conflicts (only README.md had a trivial 6-line upstream drift, reconciled). `origin/main` is still version 1.1.0 with no `ITextGenerator`/GenAI, so 1.2.0 and this feature are not duplicated upstream.

**Version:** 1.1.0 → **1.2.0** (minor; additive public surface).

---

## 1. Dependency changes (`src/McpEngramMemory.Core/McpEngramMemory.Core.csproj`)

| Package | From | To | Why |
|---|---|---|---|
| `Microsoft.ML.OnnxRuntimeGenAI` | — | **0.14.1** (new) | In-process SLM generation (CPU). |
| `Microsoft.ML.OnnxRuntime` | 1.17.0 | **1.23.0** | GenAI 0.14.1 floor is ≥1.23.0; share one native onnxruntime with embeddings. |
| `System.Numerics.Tensors` | 8.0.0 | **9.0.0** | Transitive floor from OnnxRuntime 1.23.0. |

Embeddings API (`InferenceSession`/`OrtValue`/`RunOptions`/`TensorPrimitives`) is unchanged across these bumps — `OnnxEmbeddingService` needs no edits. `<Version>` set to `1.2.0`.

> GPU is opt-in at the **app layer**: a consumer can add `Microsoft.ML.OnnxRuntimeGenAI.Cuda`/`.DirectML`. The Core package stays CPU.

---

## 2. New files (`src/McpEngramMemory.Core/Services/Synthesis/`)

### `ITextGenerator.cs` (new public interface)
The seam that makes synthesis backend-agnostic.
```csharp
public interface ITextGenerator : IDisposable
{
    // MUST NOT throw — return false when backend/model unavailable (graceful degradation).
    Task<bool> IsAvailableAsync(string model, CancellationToken ct = default);
    Task<string?> GenerateAsync(string model, string prompt, int maxTokens = 512,
        float temperature = 0.1f, CancellationToken ct = default);
}
```

### `OnnxGenAiTextGenerator.cs` (new public class : ITextGenerator)
In-process generator over ONNX Runtime GenAI. Requirements:
- **Lazy load** `Model` + `Tokenizer` once (double-checked lock), reused for process lifetime; `Dispose` releases both.
- **Serialize generation** with a `SemaphoreSlim(1,1)` — a GenAI `Generator` is not concurrency-safe; the 2 synthesis map workers take turns on one model.
- **Model dir resolution order:** ctor arg → `SYNTHESIS_ONNX_MODEL_DIR` env → `{AppContext.BaseDirectory}/LocalSynthesisModel/qwen2.5-1.5b`.
- **Graceful unavailable:** if the dir or `genai_config.json` is missing, `IsAvailableAsync` returns **false** (never throws) and caches the reason; `GenerateAsync` throws `InvalidOperationException` with staging guidance.
- **Generation:** wrap prompt in the Qwen chat template (`<|im_start|>system…<|im_end|>\n<|im_start|>user\n{prompt}<|im_end|>\n<|im_start|>assistant\n`); `GeneratorParams.SetSearchOption("max_length", inputLen+maxTokens)`, `"temperature"`, `"do_sample" = temperature>0`; `Generator.AppendTokenSequences` → loop `GenerateNextToken()` decoding via `Tokenizer.CreateStream()`; honor `CancellationToken`; offload the CPU loop with `Task.Run`.

> Chat template note: Qwen2.5 family (0.5B/1.5B) all use the same `<|im_start|>` template, so swapping sizes is drop-in. A non-Qwen model (Llama/Phi) would need the template made configurable.

---

## 3. Modified files

### `Services/Synthesis/OllamaClient.cs`
Add `ITextGenerator` to the declaration: `public sealed class OllamaClient : IDisposable, ITextGenerator`. Existing `IsAvailableAsync`/`GenerateAsync` signatures already match — no body changes.

### `Services/Synthesis/SynthesisEngine.cs`
- Replace `private readonly OllamaClient _ollama;` with `private readonly ITextGenerator _generator;`.
- **Keep** the existing ctor `(CognitiveIndex, ClusterManager, string mapModel, string reduceModel, string? ollamaUrl)` for back-compat — it now delegates to the new ctor with `new OllamaClient(...)`.
- **Add** ctor `(CognitiveIndex, ClusterManager, ITextGenerator generator, string mapModel, string reduceModel)`.
- Replace the 3 `_ollama.` call sites with `_generator.`; generalize the unavailable-error string (no longer Ollama-specific).

### `src/McpEngramMemory/Program.cs` (standalone server DI)
Select backend via `SYNTHESIS_BACKEND` (default `onnx`); register `ITextGenerator` as singleton; build `SynthesisEngine` with it:
```csharp
var synthesisBackend = (Environment.GetEnvironmentVariable("SYNTHESIS_BACKEND") ?? "onnx").Trim().ToLowerInvariant();
var synthesisMapModel = Environment.GetEnvironmentVariable("SYNTHESIS_MAP_MODEL")
    ?? (synthesisBackend == "ollama" ? "qwen2.5:1.5b" : "qwen2.5-1.5b");
var synthesisReduceModel = Environment.GetEnvironmentVariable("SYNTHESIS_REDUCE_MODEL") ?? synthesisMapModel;
builder.Services.AddSingleton<ITextGenerator>(_ => synthesisBackend == "ollama"
    ? new OllamaClient(Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434")
    : new OnnxGenAiTextGenerator(Environment.GetEnvironmentVariable("SYNTHESIS_ONNX_MODEL_DIR")));
builder.Services.AddSingleton(sp => new SynthesisEngine(
    sp.GetRequiredService<CognitiveIndex>(), sp.GetRequiredService<ClusterManager>(),
    sp.GetRequiredService<ITextGenerator>(), synthesisMapModel, synthesisReduceModel));
```

---

## 4. Default model = Qwen2.5-1.5B-Instruct

Per a 2026-06-03 CPU benchmark (0.5B vs 1.5B): the 1.5B gives clearly better synthesis quality at ~the same end-to-end speed (it finished the full map-reduce *faster* than 0.5B by being more concise). Default dir = `LocalSynthesisModel/qwen2.5-1.5b`; 0.5B remains an opt-in lighter tier via `SYNTHESIS_ONNX_MODEL_DIR`.

| | load (cold) | warm gen (128 tok) | full map-reduce synth | disk |
|---|---|---|---|---|
| Qwen2.5-0.5B | 0.93 s | 3.20 s | 29.70 s | ~0.82 GB |
| Qwen2.5-1.5B | 3.70 s | 3.68 s | **23.98 s** | ~1.9 GB |

---

## 5. Model is NOT bundled in the package — staged separately

The synthesis model (~1.9 GB) is **too large to embed** (unlike the ~30 MB embeddings model, which is MSBuild-downloaded into output via `build/McpEngramMemory.Core.targets`). It is staged out-of-band:
- `scripts/fetch-synthesis-model.ps1` — defaults to a **no-Python pre-built HF download** (1.5B = `elbruno/Qwen2.5-1.5B-Instruct-onnx`, 0.5B = `hazemmabbas/Qwen2.5-0.5B-int4-block-32-acc-3-Instruct-onnx-cpu`); `-UseBuilder` opt-in uses the Python `onnxruntime-genai` model builder; `-Size 0.5B` for the light tier.
- `.gitignore` ignores `LocalSynthesisModel/` (never commit the model).
- Runtime points at it via `SYNTHESIS_ONNX_MODEL_DIR` or the default search path.

**Packaging action for the engineer:** confirm the GenAI **native assets** flow transitively to a consuming executable's publish output (the `runtimes/**/native/*onnxruntime-genai*` + `*onnxruntime*` DLLs). Embeddings already rely on transitive native flow of `Microsoft.ML.OnnxRuntime` through this package, so the mechanism is proven; just verify GenAI's native bits land too (see §7).

---

## 6. Tests added (`tests/McpEngramMemory.Tests/`)

- `OnnxGenAiTextGeneratorTests.cs` — graceful-degradation contract with no model (3 tests): `IsAvailableAsync`→false (no throw), `GenerateAsync`→`InvalidOperationException` with staging guidance, and `SynthesisEngine` over the ONNX backend returns `Status=="error"` (not an exception).
- `LiveSynthesisInferenceTests.cs` — **auto-skips/passes** when no model staged; with a model (`SYNTHESIS_ONNX_MODEL_DIR`) runs a direct generate + full map-reduce and asserts `synthesized`.
- `ModelSpeedComparisonTests.cs` — 0.5B vs 1.5B benchmark; auto-skips unless `QWEN05_DIR` + `QWEN15_DIR` set.

CHANGELOG.md + README.md updated.

---

## 7. Build & test verification (commands + expected)

```powershell
# Restore + build (multi-target net8/9/10)
dotnet build src/McpEngramMemory.Core/McpEngramMemory.Core.csproj -c Release   # expect 0/0
dotnet build src/McpEngramMemory/McpEngramMemory.csproj -c Release             # expect 0/0

# Fast unit tests for this change
dotnet test tests/McpEngramMemory.Tests -c Release -f net9.0 `
  --filter "FullyQualifiedName~SynthesisToolsTests|FullyQualifiedName~OnnxGenAiTextGeneratorTests"   # 5/5 pass

# Optional live inference (needs a staged model)
./scripts/fetch-synthesis-model.ps1
$env:SYNTHESIS_ONNX_MODEL_DIR = "<staged dir>"
dotnet test tests/McpEngramMemory.Tests -c Release -f net9.0 --filter "FullyQualifiedName~LiveSynthesisInference"
```

**⚠ Pre-existing test failures (not caused by this change):** **8 failures** — `LockUpgradeRegressionTests` (concurrency/timing) and `BenchmarkRunnerTests` "scale-v2" (quality threshold). Re-baselined on **current `origin/main`**: they fail identically (8 fail / 204 pass) *with and without* this change, on this dev box. Upstream has deflake/threshold-tolerance commits targeting them (`bump per-write latency thresholds … for CI tolerance`, `deflake timing-sensitive tests`), so they are timing/environment-sensitive — **verify in CI**, where they may pass. They are **not** in the `publish-nuget.ps1` exclusion filter (`Category!=MSA&Category!=LiveBenchmark&Category!=T2Benchmark`), so the publish script's test gate will trip on them on a loaded machine. Engineer options: confirm green in CI, run `publish-nuget.ps1 -SkipTests`, or address separately. Orthogonal to 1.2.0.

---

## 8. Publish (existing `publish-nuget.ps1`)

```powershell
# Version is read from the csproj (1.2.0). Default source is nuget.org.
./publish-nuget.ps1 -ApiKey <NUGET_API_KEY> -SkipTests   # -SkipTests to bypass the pre-existing failures
```
This packs `McpEngramMemory.Core.1.2.0.nupkg` and pushes it. (The native runtime assets ship inside the `Microsoft.ML.OnnxRuntimeGenAI`/`OnnxRuntime` dependencies — they are NOT in our nupkg; consumers resolve them transitively.)

---

## 9. Consumer update — `mcp-epividian` (separate repo, gated on this publish)

1. Bump `src/McpEpividian/McpEpividian.csproj`: `<PackageReference Include="McpEngramMemory.Core" Version="0.9.0" />` → **`1.2.0`**.
2. (Optional, recommended) Mirror the backend-switch DI in `mcp-epividian`'s `Program.cs` — register `ITextGenerator` (default `OnnxGenAiTextGenerator`) and pass it to `SynthesisEngine`. *Until then, the existing legacy ctor keeps working on Ollama.*
3. Set env on the host: `SYNTHESIS_BACKEND=onnx` (default) and stage a model (`fetch-synthesis-model.ps1`) or set `SYNTHESIS_ONNX_MODEL_DIR`.
4. **Verify native flow:** `dotnet publish` mcp-epividian and confirm `onnxruntime-genai` native DLLs are in the output `runtimes/` (they should, transitively).
5. Rebuild + **restart** the MCP server to expose the change.

---

## 10. Risks / open items
- **Native asset transitive flow** for GenAI — verify in mcp-epividian publish output (§5/§9).
- **RAM:** 1.5B int4 ≈ 1.5–2 GB resident while loaded; acceptable for a server, note for constrained hosts.
- **Pre-existing 8 test failures** — decide handling for the publish gate (§7).
- Model files are **not** in source control or the nupkg — deployment must stage them.
