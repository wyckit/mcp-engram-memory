using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Core.Services.Synthesis;
using Xunit.Abstractions;

namespace McpEngramMemory.Tests;

/// <summary>
/// Live, in-process inference against a staged Qwen2.5-0.5B ONNX GenAI model.
/// Auto-skips (passes) when no model is staged, so CI without a model stays green.
/// Stage with scripts/fetch-synthesis-model.ps1, or set SYNTHESIS_ONNX_MODEL_DIR.
/// </summary>
public class LiveSynthesisInferenceTests
{
    private readonly ITestOutputHelper _out;
    public LiveSynthesisInferenceTests(ITestOutputHelper output) => _out = output;

    private static string? ResolveModelDir()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("SYNTHESIS_ONNX_MODEL_DIR"),
            // Repo-local staged path used during development.
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "McpEngramMemory.Core", "LocalSynthesisModel", "qwen2.5-0.5b"),
        };
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c!, "genai_config.json")))
                return Path.GetFullPath(c!);
        }
        return null;
    }

    [Fact]
    public async Task LiveGenerate_ProducesNonEmptyOutput()
    {
        var dir = ResolveModelDir();
        if (dir is null) { _out.WriteLine(">>>LIVE SKIPPED: no model staged"); return; }
        _out.WriteLine($">>>LIVE model dir: {dir}");

        using var gen = new OnnxGenAiTextGenerator(dir);
        Assert.True(await gen.IsAvailableAsync("qwen2.5-0.5b"), "model should load");

        var prompt =
            "Summarize these notes in one sentence:\n" +
            "- Migrated Chorus_Portal from Angular 17 to 21.\n" +
            "- Fixed NG0100 errors via signal migration.\n" +
            "- Restored zone change detection with provideZoneChangeDetection().";
        var output = await gen.GenerateAsync("qwen2.5-0.5b", prompt, maxTokens: 120, temperature: 0.2f);

        _out.WriteLine($">>>LIVE generate output:\n{output}");
        Assert.False(string.IsNullOrWhiteSpace(output), "generation should produce text");
    }

    [Fact]
    public async Task LiveSynthesisEngine_MapReduce_OverSeededMemories()
    {
        var dir = ResolveModelDir();
        if (dir is null) { _out.WriteLine(">>>LIVE SKIPPED: no model staged"); return; }

        var dataPath = Path.Combine(Path.GetTempPath(), $"live_synth_{Guid.NewGuid():N}");
        using var persistence = new PersistenceManager(dataPath, debounceMs: 50);
        using var index = new CognitiveIndex(persistence);
        var clusters = new ClusterManager(index, persistence);

        const string ns = "synth-live";
        var notes = new[]
        {
            "Decided to embed the synthesis SLM in-process via ONNX Runtime GenAI instead of Ollama.",
            "OnnxEmbeddingService already hosts the embedding model in-process with zero external deps.",
            "Extracted ITextGenerator so SynthesisEngine is backend-agnostic.",
            "Bumped Microsoft.ML.OnnxRuntime to 1.23.0 to match the GenAI dependency floor.",
            "mcp-epividian consumes engram core as a published NuGet package, so integration needs a publish.",
            "Added a graceful no-throw path when the model directory is absent.",
        };
        for (int i = 0; i < notes.Length; i++)
            index.Upsert(new CognitiveEntry($"n{i}", new[] { 0.1f, 0.2f }, ns,
                text: notes[i], category: "decision", lifecycleState: "ltm"));

        using var gen = new OnnxGenAiTextGenerator(dir);
        var engine = new SynthesisEngine(index, clusters, gen, "qwen2.5-0.5b", "qwen2.5-0.5b");

        var result = await engine.SynthesizeNamespaceAsync(ns, query: "what backend decision was made and why");

        _out.WriteLine($">>>LIVE status: {result.Status}, entries: {result.EntriesProcessed}, chunks: {result.ChunksProcessed}");
        _out.WriteLine($">>>LIVE synthesis:\n{result.Synthesis}");

        Assert.Equal("synthesized", result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Synthesis));
        Assert.Equal(notes.Length, result.EntriesProcessed);

        if (Directory.Exists(dataPath)) Directory.Delete(dataPath, true);
    }
}
