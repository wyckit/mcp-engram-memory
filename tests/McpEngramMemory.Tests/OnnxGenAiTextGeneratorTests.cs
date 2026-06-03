using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Core.Services.Synthesis;

namespace McpEngramMemory.Tests;

/// <summary>
/// Verifies the graceful-degradation contract of the in-process ONNX synthesis backend
/// when no model is staged. Live inference is exercised separately once a model dir is present.
/// </summary>
public class OnnxGenAiTextGeneratorTests
{
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
        var result = await engine.SynthesizeNamespaceAsync("work");

        // Unavailable backend surfaces as a descriptive error result, not an exception.
        Assert.Equal("error", result.Status);
        Assert.Contains("unavailable", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        if (Directory.Exists(dataPath))
            Directory.Delete(dataPath, true);
    }
}
