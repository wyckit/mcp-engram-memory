using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Core.Services.Synthesis;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace McpEngramMemory.Tests;

/// <summary>
/// Verifies the graceful-degradation contract of the in-process ONNX synthesis backend
/// when no model is staged, plus a native ABI smoke test proving onnxruntime-genai
/// actually loads and executes against the pinned Microsoft.ML.OnnxRuntime 1.29.0 runtime.
/// </summary>
public class OnnxGenAiTextGeneratorTests
{
    [Fact]
    public void NativeAbiSmokeTest_OnnxRuntimeGenAiLoadsAlongsideOnnxRuntime129()
    {
        // Confirm ORT 1.29.x is loaded and functional in the same process.
        var ortVersion = typeof(InferenceSession).Assembly.GetName().Version;
        Assert.NotNull(ortVersion);
        Assert.Equal(1, ortVersion!.Major);
        Assert.Equal(29, ortVersion.Minor);
        using (var sessionOptions = new SessionOptions())
        {
            sessionOptions.IntraOpNumThreads = 1;
        }

        // Exercise a real native P/Invoke into onnxruntime-genai (OgaCreateConfig via new Config).
        // Passing an empty temp directory has no genai_config.json, so native onnxruntime-genai
        // must load cleanly, bind against ORT 1.29.0, run native C++ code, and raise a managed
        // OnnxRuntimeGenAIException — NOT DllNotFoundException, EntryPointNotFoundException,
        // BadImageFormatException, or a native crash.
        var emptyDir = Path.Combine(Path.GetTempPath(), $"oga_abi_smoke_{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDir);
        try
        {
            var ex = Assert.Throws<OnnxRuntimeGenAIException>(() =>
            {
                using var cfg = new Config(emptyDir);
            });
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }
        finally
        {
            if (Directory.Exists(emptyDir))
                Directory.Delete(emptyDir, recursive: true);
        }
    }

    private static string MissingModelDir()
        => Path.Combine(Path.GetTempPath(), $"onnx_synth_missing_{Guid.NewGuid():N}");

    [Fact]
    public async Task IsAvailable_ReturnsFalse_WhenModelMissing_DoesNotThrow()
    {
        using var gen = new OnnxGenAiTextGenerator(MissingModelDir());

        var available = await gen.IsAvailableAsync("qwen2.5-0.5b");

        Assert.False(available);
    }

    [Fact]
    public async Task Generate_Throws_WithStagingGuidance_WhenModelMissing()
    {
        using var gen = new OnnxGenAiTextGenerator(MissingModelDir());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gen.GenerateAsync("qwen2.5-0.5b", "summarize this"));

        Assert.Contains("SYNTHESIS_ONNX_MODEL_DIR", ex.Message);
    }

    [Fact]
    public async Task SynthesisEngine_WithOnnxBackend_DegradesGracefully_WhenModelMissing()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), $"onnx_synth_engine_{Guid.NewGuid():N}");
        using var persistence = new PersistenceManager(dataPath, debounceMs: 50);
        using var index = new CognitiveIndex(persistence);
        var clusters = new ClusterManager(index, persistence);
        using var generator = new OnnxGenAiTextGenerator(MissingModelDir());

        // Exercises the new backend-injection constructor.
        var engine = new SynthesisEngine(index, clusters, generator, "qwen2.5-0.5b", "qwen2.5-0.5b");
        var result = await engine.SynthesizeNamespaceAsync("work", query: null, tenantId: "");

        // Unavailable backend surfaces as a descriptive error result, not an exception.
        Assert.Equal("error", result.Status);
        Assert.Contains("unavailable", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        if (Directory.Exists(dataPath))
            Directory.Delete(dataPath, true);
    }
}
