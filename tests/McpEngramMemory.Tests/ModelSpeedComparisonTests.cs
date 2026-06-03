using System.Diagnostics;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Core.Services.Synthesis;
using Xunit.Abstractions;

namespace McpEngramMemory.Tests;

/// <summary>
/// Head-to-head speed comparison between two staged ONNX GenAI models, driven entirely in-process.
/// Set QWEN05_DIR and QWEN15_DIR to the two model directories. Auto-skips if either is missing.
/// Measures: cold model-load, warm single-generation latency (x3), and full map-reduce synthesis.
/// </summary>
public class ModelSpeedComparisonTests
{
    private readonly ITestOutputHelper _out;
    public ModelSpeedComparisonTests(ITestOutputHelper output) => _out = output;

    private static readonly string[] Notes =
    {
        "Decided to embed the synthesis SLM in-process via ONNX Runtime GenAI instead of Ollama.",
        "OnnxEmbeddingService already hosts the embedding model in-process with zero external deps.",
        "Extracted ITextGenerator so SynthesisEngine is backend-agnostic.",
        "Bumped Microsoft.ML.OnnxRuntime to 1.23.0 to match the GenAI dependency floor.",
        "mcp-epividian consumes engram core as a published NuGet package, so integration needs a publish.",
        "Added a graceful no-throw path when the model directory is absent.",
    };

    private const string Prompt =
        "Summarize these notes in two sentences:\n" +
        "- Migrated Chorus_Portal from Angular 17 to 21.\n" +
        "- Fixed NG0100 errors via signal migration.\n" +
        "- Restored zone change detection with provideZoneChangeDetection().\n" +
        "- Embedded the synthesis model in-process via ONNX Runtime GenAI.";

    [Fact]
    public async Task Compare_0_5B_vs_1_5B()
    {
        var m05 = Environment.GetEnvironmentVariable("QWEN05_DIR");
        var m15 = Environment.GetEnvironmentVariable("QWEN15_DIR");
        if (!Ok(m05) || !Ok(m15))
        {
            _out.WriteLine(">>>SPEED SKIPPED: set QWEN05_DIR and QWEN15_DIR to staged model dirs");
            return;
        }

        var a = await BenchAsync("Qwen2.5-0.5B", m05!);
        var b = await BenchAsync("Qwen2.5-1.5B", m15!);

        _out.WriteLine("");
        _out.WriteLine(">>>SPEED ===== RESULTS =====");
        _out.WriteLine($">>>SPEED {"model",-16} {"load(s)",10} {"gen avg(s)",12} {"~tok/s",8} {"synth(s)",10} {"status",-12}");
        foreach (var r in new[] { a, b })
            _out.WriteLine($">>>SPEED {r.Name,-16} {r.LoadSec,10:F2} {r.GenAvgSec,12:F2} {r.ApproxTokPerSec,8:F1} {r.SynthSec,10:F2} {r.SynthStatus,-12}");
        _out.WriteLine($">>>SPEED 1.5B/0.5B gen-latency ratio: {(b.GenAvgSec / Math.Max(a.GenAvgSec, 0.001)):F2}x, synth ratio: {(b.SynthSec / Math.Max(a.SynthSec, 0.001)):F2}x");
        _out.WriteLine("");
        _out.WriteLine($">>>SPEED 0.5B synthesis:\n{a.SynthText}");
        _out.WriteLine($">>>SPEED 1.5B synthesis:\n{b.SynthText}");

        // Both should produce a real synthesis; we don't assert on speed (informational).
        Assert.Equal("synthesized", a.SynthStatus);
        Assert.Equal("synthesized", b.SynthStatus);
    }

    private static bool Ok(string? d) => !string.IsNullOrWhiteSpace(d) && File.Exists(Path.Combine(d!, "genai_config.json"));

    private async Task<Result> BenchAsync(string name, string dir)
    {
        _out.WriteLine($">>>SPEED benchmarking {name} ({dir})");
        using var gen = new OnnxGenAiTextGenerator(dir);

        // Cold load (first availability check triggers Model() construction).
        var sw = Stopwatch.StartNew();
        Assert.True(await gen.IsAvailableAsync(name), $"{name} should load");
        sw.Stop();
        var loadSec = sw.Elapsed.TotalSeconds;

        // Warm single-generation latency (3 runs, fixed cap, greedy for consistency).
        const int maxTokens = 128;
        var times = new List<double>();
        string last = "";
        for (int i = 0; i < 3; i++)
        {
            sw.Restart();
            last = await gen.GenerateAsync(name, Prompt, maxTokens: maxTokens, temperature: 0.0f) ?? "";
            sw.Stop();
            times.Add(sw.Elapsed.TotalSeconds);
        }
        var genAvg = times.Average();
        // Rough tokens/sec proxy: ~4 chars/token.
        var approxTokPerSec = (last.Length / 4.0) / Math.Max(genAvg, 0.001);

        // Full map-reduce synthesis over identical seeded memories.
        var dataPath = Path.Combine(Path.GetTempPath(), $"speed_{name}_{Guid.NewGuid():N}");
        using var persistence = new PersistenceManager(dataPath, debounceMs: 50);
        using var index = new CognitiveIndex(persistence);
        var clusters = new ClusterManager(index, persistence);
        const string ns = "speed-ns";
        for (int i = 0; i < Notes.Length; i++)
            index.Upsert(new CognitiveEntry($"n{i}", new[] { 0.1f, 0.2f }, ns, text: Notes[i], category: "decision", lifecycleState: "ltm"));

        var engine = new SynthesisEngine(index, clusters, gen, name, name);
        sw.Restart();
        var result = await engine.SynthesizeNamespaceAsync(ns, query: "what backend decision was made and why");
        sw.Stop();

        if (Directory.Exists(dataPath)) Directory.Delete(dataPath, true);

        return new Result(name, loadSec, genAvg, approxTokPerSec, sw.Elapsed.TotalSeconds,
            result.Status, result.Synthesis ?? "");
    }

    private sealed record Result(string Name, double LoadSec, double GenAvgSec, double ApproxTokPerSec,
        double SynthSec, string SynthStatus, string SynthText);
}
