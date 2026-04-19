using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory;

/// <summary>
/// Standalone CLI entrypoint for the MRCR v2 benchmark — bypasses the MCP server loop so
/// we can run a pilot directly with `dotnet run -- mrcr-pilot ...`. Reuses the same runner,
/// scorer, and storage primitives as the MCP tool.
/// </summary>
internal static class MrcrPilotCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts is null)
        {
            PrintUsage();
            return 1;
        }

        Console.Error.WriteLine($"[mrcr-pilot] dataset={opts.DatasetPath}");
        Console.Error.WriteLine($"[mrcr-pilot] provider={opts.Provider} model={opts.Model} limit={opts.Limit} topK={opts.TopK}");

        IReadOnlyList<MrcrTask> tasks;
        try
        {
            tasks = MrcrDatasetLoader.Load(opts.DatasetPath, opts.Limit > 0 ? opts.Limit : null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mrcr-pilot] dataset load failed: {ex.Message}");
            return 2;
        }
        Console.Error.WriteLine($"[mrcr-pilot] loaded {tasks.Count} probes");

        // Minimal DI — only the services the runner actually needs.
        var persistencePath = Path.Combine(Path.GetTempPath(), $"mrcr_pilot_{Guid.NewGuid():N}");
        var persistence = new PersistenceManager(persistencePath, debounceMs: 100);
        var index = new CognitiveIndex(persistence);
        var embedding = new OnnxEmbeddingService();
        var scorer = new MrcrScorer(embedding);
        var runner = new MrcrBenchmarkRunner(index, embedding, scorer);

        var factory = new AgentOutcomeModelClientFactory();
        IAgentOutcomeModelClient client;
        try
        {
            client = factory.Create(opts.Provider, opts.ClaudeExecutable);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Console.Error.WriteLine($"[mrcr-pilot] {ex.Message}");
            return 3;
        }

        if (!await client.IsAvailableAsync(opts.Model))
        {
            Console.Error.WriteLine($"[mrcr-pilot] provider '{opts.Provider}' CLI not available — is it on PATH?");
            client.Dispose();
            return 3;
        }

        using var _disposableClient = client;

        var runOptions = new MrcrGenerationOptions(
            Provider: opts.Provider,
            Model: opts.Model,
            Endpoint: null,
            Limit: opts.Limit,
            TopK: opts.TopK,
            MaxTokens: opts.MaxTokens,
            Temperature: opts.Temperature,
            MaxContextTokens: opts.MaxContextTokens,
            RunFullContextArm: opts.RunFullContextArm,
            RunEngramArm: opts.RunEngramArm,
            EngramMode: opts.EngramMode);

        MrcrBenchmarkResult result;
        try
        {
            result = await runner.RunAsync(opts.DatasetId, tasks, runOptions, client);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mrcr-pilot] run failed: {ex.Message}");
            return 4;
        }
        finally
        {
            try { if (Directory.Exists(persistencePath)) Directory.Delete(persistencePath, recursive: true); } catch { }
        }

        string artifactPath = PersistArtifact(result, opts.OutputDirectory);
        Console.Error.WriteLine($"[mrcr-pilot] wrote artifact: {artifactPath}");
        PrintSummary(result);
        return 0;
    }

    private static PilotOptions? ParseArgs(string[] args)
    {
        var opts = new PilotOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--dataset": opts.DatasetPath = Next() ?? opts.DatasetPath; break;
                case "--dataset-id": opts.DatasetId = Next() ?? opts.DatasetId; break;
                case "--provider": opts.Provider = Next() ?? opts.Provider; break;
                case "--model": opts.Model = Next() ?? opts.Model; break;
                case "--limit": if (int.TryParse(Next(), out var lim)) opts.Limit = lim; break;
                case "--top-k": if (int.TryParse(Next(), out var tk)) opts.TopK = tk; break;
                case "--max-tokens": if (int.TryParse(Next(), out var mt)) opts.MaxTokens = mt; break;
                case "--temperature": if (float.TryParse(Next(), out var tp)) opts.Temperature = tp; break;
                case "--max-context-tokens": if (int.TryParse(Next(), out var mc)) opts.MaxContextTokens = mc; break;
                case "--no-full-context": opts.RunFullContextArm = false; break;
                case "--no-engram": opts.RunEngramArm = false; break;
                case "--engram-mode": opts.EngramMode = Next() ?? opts.EngramMode; break;
                case "--output-dir": opts.OutputDirectory = Next() ?? opts.OutputDirectory; break;
                case "--claude-exe": opts.ClaudeExecutable = Next(); break;
                case "-h": case "--help": return null;
            }
        }

        if (string.IsNullOrWhiteSpace(opts.DatasetPath))
            opts.DatasetPath = MrcrDatasetLoader.ResolveDefaultPath(null);
        if (string.IsNullOrWhiteSpace(opts.Model)) return null;
        return opts;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
@"Usage: dotnet run --project src/McpEngramMemory -- mrcr-pilot [options]

Required:
  --model <name>               Model name for the provider (e.g. 'opus', 'sonnet', 'haiku').

Optional:
  --dataset <path>             JSONL dataset path (default: MRCR_DATASET_PATH env or
                               benchmarks/datasets/mrcr-v2/mrcr_v2_8needle.jsonl).
  --dataset-id <id>            Artifact dataset identifier (default: 'mrcr-v2-8needle').
  --provider <name>            'claude-cli' (default) or 'ollama'.
  --limit <n>                  Max probes (default: 3). 0 = full dataset.
  --top-k <k>                  Engram arm top-K (default: 8).
  --max-tokens <n>             Completion tokens (default: 512).
  --temperature <f>            Sampling temperature (default: 0.0).
  --max-context-tokens <n>     Skip probes whose prompt exceeds this (default: 131072).
  --no-full-context            Skip the full_context arm.
  --no-engram                  Skip the engram_retrieval arm.
  --engram-mode <mode>         'hybrid' (default) or 'ordinal' — pair-wise ingest with
                               category+within-category ordinal; fallback to hybrid on probes
                               that don't match the 'Nth X about Y' template.
  --output-dir <dir>           Artifact root (default: BENCHMARK_ARTIFACTS_PATH or ./benchmarks).
  --claude-exe <path>          Override the `claude` CLI path.
");
    }

    private static string PersistArtifact(MrcrBenchmarkResult result, string outputDir)
    {
        string root = string.IsNullOrWhiteSpace(outputDir)
            ? (Environment.GetEnvironmentVariable("BENCHMARK_ARTIFACTS_PATH")
               ?? Path.Combine(Directory.GetCurrentDirectory(), "benchmarks"))
            : outputDir;

        string datedDir = Path.Combine(root, $"{result.RunAt:yyyy-MM-dd}");
        Directory.CreateDirectory(datedDir);
        string path = Path.Combine(datedDir,
            $"{result.DatasetId}-mrcr-{Sanitize(result.Provider)}-{Sanitize(result.Model)}-{Sanitize(result.EngramMode)}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
        return Path.GetFullPath(path);
    }

    private static string Sanitize(string v)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string((v ?? "default").Trim().ToLowerInvariant()
            .Select(c => invalid.Contains(c) || c == ':' || char.IsWhiteSpace(c) ? '-' : c).ToArray())
            .Trim('-');
    }

    private static void PrintSummary(MrcrBenchmarkResult r)
    {
        Console.WriteLine();
        Console.WriteLine("=== MRCR v2 pilot summary ===");
        Console.WriteLine($"dataset:  {r.DatasetId}");
        Console.WriteLine($"model:    {r.Provider}/{r.Model}");
        Console.WriteLine($"probes:   {r.TaskCount}   topK={r.TopK}   engramMode={r.EngramMode}");
        Console.WriteLine();

        if (r.FullContext is { } fc)
            Console.WriteLine($"full_context      sim={fc.MeanSimilarity:F3}  pass={fc.PassRate:P0}  latency={fc.MeanLatencyMs:F0}ms  tokens={fc.TotalPromptTokens}  errors={fc.ErrorCount}");
        else
            Console.WriteLine("full_context      (skipped)");

        if (r.EngramRetrieval is { } er)
            Console.WriteLine($"engram_retrieval  sim={er.MeanSimilarity:F3}  pass={er.PassRate:P0}  latency={er.MeanLatencyMs:F0}ms  tokens={er.TotalPromptTokens}  errors={er.ErrorCount}");
        else
            Console.WriteLine("engram_retrieval  (skipped)");

        Console.WriteLine();
        Console.WriteLine($"Δsim (engram − full):  {r.SimilarityDelta:+0.000;-0.000;0.000}");
        Console.WriteLine($"prompt-token reduction (engram vs full): {r.PromptTokenReductionRatio:P1}");
    }

    private sealed class PilotOptions
    {
        public string DatasetPath { get; set; } = string.Empty;
        public string DatasetId { get; set; } = "mrcr-v2-8needle";
        public string Provider { get; set; } = "claude-cli";
        public string Model { get; set; } = string.Empty;
        public int Limit { get; set; } = 3;
        public int TopK { get; set; } = 8;
        public int MaxTokens { get; set; } = 512;
        public float Temperature { get; set; } = 0.0f;
        public int MaxContextTokens { get; set; } = 131072;
        public bool RunFullContextArm { get; set; } = true;
        public bool RunEngramArm { get; set; } = true;
        public string EngramMode { get; set; } = "hybrid";
        public string OutputDirectory { get; set; } = string.Empty;
        public string? ClaudeExecutable { get; set; }
    }
}
