using McpEngramMemory.Core.Services.Evaluation;

namespace McpEngramMemory;

/// <summary>
/// Standalone CLI entrypoint for the cold-start self-referential benchmark.
/// Usage: dotnet run --project src/McpEngramMemory -- cold-start [options]
/// </summary>
internal static class ColdStartCommand
{
    private const string DefaultDatasetPath =
        "benchmarks/datasets/self-referential-cold-start-v1/manifest.json";

    public static async Task<int> RunAsync(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts is null)
        {
            PrintUsage();
            return 0;
        }

        Console.Error.WriteLine($"[cold-start] dataset={opts.DatasetPath}");
        Console.Error.WriteLine($"[cold-start] model={opts.Model} seeds={opts.Seeds} maxTasks={opts.MaxTasks}");

        ColdStartManifest manifest;
        try
        {
            manifest = ColdStartBenchmarkRunner.LoadManifest(opts.DatasetPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cold-start] manifest load failed: {ex.Message}");
            return 2;
        }

        int effectiveTaskCount = opts.MaxTasks > 0
            ? Math.Min(opts.MaxTasks, manifest.Tasks.Count)
            : manifest.Tasks.Count;

        Console.Error.WriteLine($"[cold-start] loaded {manifest.Tasks.Count} tasks (running {effectiveTaskCount})");

        var runnerOptions = new ColdStartBenchmarkOptions(
            Model: opts.Model,
            DatasetPath: opts.DatasetPath,
            Provider: opts.Provider,
            MaxTasks: opts.MaxTasks,
            Seeds: opts.Seeds,
            MaxTokens: opts.MaxTokens,
            Temperature: opts.Temperature,
            OutputDir: opts.OutputDir,
            ClaudeExecutable: opts.ClaudeExecutable);

        var runner = new ColdStartBenchmarkRunner(
            executable: opts.ClaudeExecutable);

        ColdStartBenchmarkResult result;
        try
        {
            result = await runner.RunAsync(runnerOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cold-start] run failed: {ex.Message}");
            return 4;
        }

        string artifactPath = ColdStartBenchmarkRunner.PersistArtifact(result, opts.OutputDir);
        Console.Error.WriteLine($"[cold-start] artifact written: {artifactPath}");

        PrintScorecard(result, effectiveTaskCount, artifactPath);
        return 0;
    }

    private static CommandOptions? ParseArgs(string[] args)
    {
        if (args.Length == 0)
            return null;

        var opts = new CommandOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--model": opts.Model = Next() ?? opts.Model; break;
                case "--dataset": opts.DatasetPath = Next() ?? opts.DatasetPath; break;
                case "--tasks": if (int.TryParse(Next(), out var t)) opts.MaxTasks = t; break;
                case "--seeds": if (int.TryParse(Next(), out var s)) opts.Seeds = s; break;
                case "--output-dir": opts.OutputDir = Next() ?? string.Empty; break;
                case "--max-tokens": if (int.TryParse(Next(), out var mt)) opts.MaxTokens = mt; break;
                case "--temperature": if (float.TryParse(Next(), out var tp)) opts.Temperature = tp; break;
                case "--claude-exe": opts.ClaudeExecutable = Next(); break;
                case "--provider": opts.Provider = Next() ?? opts.Provider; break;
                case "-h":
                case "--help": return null;
            }
        }

        if (string.IsNullOrWhiteSpace(opts.Model))
        {
            Console.Error.WriteLine("[cold-start] error: --model is required.");
            return null;
        }

        return opts;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
@"Usage: dotnet run --project src/McpEngramMemory -- cold-start [options]

Required:
  --model <name>               Model name (e.g. 'opus', 'sonnet', 'haiku').

Optional:
  --dataset <path>             Path to manifest.json
                               (default: benchmarks/datasets/self-referential-cold-start-v1/manifest.json).
  --tasks <n>                  Max tasks to run. 0 = all tasks (default: 0).
  --seeds <n>                  Runs per task per arm (default: 3).
  --output-dir <dir>           Artifact root directory (default: BENCHMARK_ARTIFACTS_PATH or ./benchmarks).
  --max-tokens <n>             Max completion tokens (default: 1024).
  --temperature <f>            Sampling temperature (default: 0.0).
  --claude-exe <path>          Override the `claude` CLI executable path.
  --provider <name>            Provider name (default: claude-cli).
  -h, --help                   Print this help and exit.

Description:
  Runs the cold-start self-referential benchmark (v1.1).
  Phase 1 primes Engram with a session transcript per task.
  Phase 2 measures no_memory vs full_engram on a fresh cold prompt.
  Pass-rate lift on full_engram arm = the v1.1 marketing artifact.

  Scoring: pattern-match-v1 (keyword/phrase extraction from rubric criteria).
  LLM judge will replace this in v1.2.

Example:
  dotnet run --project src/McpEngramMemory -- cold-start --model opus --tasks 2 --seeds 1
");
    }

    private static void PrintScorecard(ColdStartBenchmarkResult r, int taskCount, string artifactPath)
    {
        var nm = r.NoMemory;
        var fe = r.FullEngram;

        string header = $"Engram Cold-Start Benchmark v1.1 ({taskCount} tasks x {r.Seeds} seeds, " +
                        $"claude-cli v{r.ClaudeCliVersion}, model={r.Model})";
        string divider = new string('─', Math.Max(header.Length, 70));

        Console.WriteLine();
        Console.WriteLine(header);
        Console.WriteLine(divider);
        Console.WriteLine($"{"Arm",-14}| {"Pass",7}  | {"Tokens",8} | {"Tools",5} | {"Time",6}");
        Console.WriteLine(divider);
        Console.WriteLine($"{"no_memory",-14}| {nm.MeanPassRate * 100,6:F1}%  | {nm.MeanTokens,8:F0} | {nm.MeanToolCalls,5:F1} | {nm.MeanLatencyMs / 1000,5:F1}s");
        Console.WriteLine($"{"full_engram",-14}| {fe.MeanPassRate * 100,6:F1}%  | {fe.MeanTokens,8:F0} | {fe.MeanToolCalls,5:F1} | {fe.MeanLatencyMs / 1000,5:F1}s");
        Console.WriteLine(divider);
        double liftPp = r.PassRateLift * 100;
        double tokenPct = r.TokenSavingsRatio * 100;
        double toolDelta = fe.MeanToolCalls - nm.MeanToolCalls;
        string liftSign = liftPp >= 0 ? "+" : "";
        string tokenSign = tokenPct >= 0 ? "-" : "+";
        Console.WriteLine($"Δ pass rate: {liftSign}{liftPp:F1}pp   Δ tokens: {tokenSign}{Math.Abs(tokenPct):F1}%   Δ tool calls: {(toolDelta >= 0 ? "+" : "")}{toolDelta:F1}");
        Console.WriteLine($"Sigma (full_engram pass rate): {fe.SigmaPassRate * 100:F1}pp");
        Console.WriteLine($"Scoring: {r.ScoringMethod} (LLM judge in v1.2)");
        Console.WriteLine($"Artifact: {artifactPath}");
        Console.WriteLine();
    }

    private sealed class CommandOptions
    {
        public string Model { get; set; } = string.Empty;
        public string DatasetPath { get; set; } = DefaultDatasetPath;
        public string Provider { get; set; } = "claude-cli";
        public int MaxTasks { get; set; } = 0;
        public int Seeds { get; set; } = 3;
        public int MaxTokens { get; set; } = 1024;
        public float Temperature { get; set; } = 0.0f;
        public string? OutputDir { get; set; }
        public string? ClaudeExecutable { get; set; }
    }
}
