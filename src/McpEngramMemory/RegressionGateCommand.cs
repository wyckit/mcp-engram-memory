using System.Globalization;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Evaluation;

namespace McpEngramMemory;

/// <summary>
/// Standalone CLI entrypoint for the statistical benchmark regression gate — invoked as
/// `dotnet McpEngramMemory.dll regression-gate ...` by scripts/check-benchmark-regression.sh
/// (and .ps1). Prints the same pass/fail table as the legacy shell gate and uses the same
/// exit codes: 0 pass, 1 regression detected, 2 usage/path/no-artifacts error.
/// </summary>
internal static class RegressionGateCommand
{
    public static int Run(string[] args)
    {
        // The notes column uses Δ/α/—; default Windows console codepages garble them.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* non-console stdout */ }

        var opts = ParseArgs(args);
        if (opts is null)
        {
            PrintUsage();
            return 2;
        }

        string repoRoot = ResolveRepoRoot();
        string baselineDir = opts.BaselineDir ?? Path.Combine(repoRoot, "benchmarks", "baselines");

        string? path = opts.Path;
        if (string.IsNullOrEmpty(path))
        {
            path = FindNewestDatedBenchmarkFolder(Path.Combine(repoRoot, "benchmarks"));
            if (path is null)
            {
                Console.Error.WriteLine("ERROR: no PATH supplied and no dated benchmark folder found.");
                return 2;
            }
            Console.WriteLine($"No PATH supplied; defaulting to newest dated folder: {path}");
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Console.Error.WriteLine($"ERROR: path not found: {path}");
            return 2;
        }

        var options = new RegressionGateOptions(
            Path: path,
            BaselineDir: baselineDir,
            Tolerance: opts.Tolerance,
            Alpha: opts.Alpha,
            MinimumDetectableEffect: opts.Mde,
            RecallFloor: opts.RecallFloor,
            MrrFloor: opts.MrrFloor,
            NdcgFloor: opts.NdcgFloor,
            OutcomeFloor: opts.OutcomeFloor,
            Recurse: opts.Recurse);

        RegressionGateReport report;
        try
        {
            report = RegressionGateRunner.Evaluate(options);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 2;
        }

        PrintReport(report, options);

        if (report.Total == 0)
        {
            Console.Error.WriteLine("WARN: no metric-bearing benchmark artifacts were evaluated.");
            return 2;
        }

        if (report.Failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("REGRESSION DETECTED - failing the build.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("All benchmark metrics within floors and statistical drift bounds.");
        return 0;
    }

    private static void PrintReport(RegressionGateReport report, RegressionGateOptions options)
    {
        foreach (string warning in report.Warnings)
            Console.Error.WriteLine(warning);

        Console.WriteLine();
        Console.WriteLine(Inv(
            $"Benchmark regression gate  (tolerance={options.Tolerance}, alpha={options.Alpha}, mde={options.MinimumDetectableEffect}, baselines={options.BaselineDir})"));
        Console.WriteLine("=============================================================================");
        Console.WriteLine(Inv(
            $"{"Dataset",-28} {"Metric",-16} {"Value",-8} {"Floor",-8} {"Baseline",-9} {"Status",-6} Notes"));
        Console.WriteLine("-----------------------------------------------------------------------------");

        foreach (var check in report.Checks)
        {
            string baseline = check.BaselineValue is double b ? b.ToString("F3", CultureInfo.InvariantCulture) : "-";
            Console.WriteLine(Inv(
                $"{check.Label,-28} {check.Metric,-16} {check.Value,-8:F3} {check.Floor,-8:F3} {baseline,-9} {check.Status,-6} {check.Notes}"));
        }

        Console.WriteLine("-----------------------------------------------------------------------------");
        Console.WriteLine(Inv(
            $"Checks: {report.Total}   Passed: {report.Total - report.Failed}   Failed: {report.Failed}   Files skipped: {report.SkippedFiles}"));
    }

    private static GateCliOptions? ParseArgs(string[] args)
    {
        var opts = new GateCliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--baseline-dir": opts.BaselineDir = Next(); break;
                case "--tolerance": if (!TryParseDouble(Next(), out opts.Tolerance)) return null; break;
                case "--alpha": if (!TryParseDouble(Next(), out opts.Alpha)) return null; break;
                case "--mde": if (!TryParseDouble(Next(), out opts.Mde)) return null; break;
                case "--recall-floor": if (!TryParseDouble(Next(), out opts.RecallFloor)) return null; break;
                case "--mrr-floor": if (!TryParseDouble(Next(), out opts.MrrFloor)) return null; break;
                case "--ndcg-floor": if (!TryParseDouble(Next(), out opts.NdcgFloor)) return null; break;
                case "--outcome-floor": if (!TryParseDouble(Next(), out opts.OutcomeFloor)) return null; break;
                case "--recurse": opts.Recurse = true; break;
                case "-h": case "--help": return null;
                default:
                    if (a.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Unknown option: {a}");
                        return null;
                    }
                    opts.Path = a;
                    break;
            }
        }
        return opts;
    }

    private static bool TryParseDouble(string? value, out double result)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
@"Usage: dotnet McpEngramMemory.dll regression-gate [PATH] [options]

Statistical CI regression gate for benchmark artifacts. PATH is a result .json
file or a directory of them; defaults to the newest dated folder under
benchmarks/. Enforces absolute floors, then gates baseline drift with a
Holm-Bonferroni-corrected one-sided paired t-test over per-query deltas
(falling back to the legacy point comparison when per-query data is missing).

Options:
  --baseline-dir <dir>   Pinned baseline dir (default: <repo>/benchmarks/baselines).
  --tolerance <n>        Legacy-fallback drift tolerance (default: 0.02).
  --alpha <n>            Family-wise significance level (default: 0.05).
  --mde <n>              Minimum detectable effect - mean drop required to fail
                         even when statistically significant (default: 0.02).
  --recall-floor <n>     Absolute Recall@K floor (default: 0.20).
  --mrr-floor <n>        Absolute MRR floor (default: 0.20).
  --ndcg-floor <n>       Absolute nDCG@K floor (default: 0.15).
  --outcome-floor <n>    Absolute agent-outcome floor (default: 0.20).
  --recurse              Recurse into subdirectories when PATH is a directory.
");
    }

    /// <summary>
    /// Repo root discovery: prefer the current working directory when it contains
    /// McpEngramMemory.slnx (CI runs from the workspace root), else walk up from
    /// AppContext.BaseDirectory.
    /// </summary>
    private static string ResolveRepoRoot()
    {
        string cwd = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(cwd, "McpEngramMemory.slnx"))) return cwd;

        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "McpEngramMemory.slnx")) && Path.GetDirectoryName(root) != null)
        {
            root = Path.GetDirectoryName(root)!;
        }
        return File.Exists(Path.Combine(root, "McpEngramMemory.slnx")) ? root : cwd;
    }

    private static string? FindNewestDatedBenchmarkFolder(string benchmarksRoot)
    {
        if (!Directory.Exists(benchmarksRoot)) return null;
        return Directory.GetDirectories(benchmarksRoot)
            .Where(d => IsDatedFolder(Path.GetFileName(d)))
            .OrderBy(d => d, StringComparer.Ordinal)
            .LastOrDefault();
    }

    private static bool IsDatedFolder(string name)
        => name.Length >= 10
           && name[4] == '-' && name[7] == '-'
           && char.IsAsciiDigit(name[0]) && char.IsAsciiDigit(name[1])
           && char.IsAsciiDigit(name[2]) && char.IsAsciiDigit(name[3])
           && char.IsAsciiDigit(name[5]) && char.IsAsciiDigit(name[6])
           && char.IsAsciiDigit(name[8]) && char.IsAsciiDigit(name[9]);

    private static string Inv(FormattableString s) => FormattableString.Invariant(s);

    private sealed class GateCliOptions
    {
        public string? Path;
        public string? BaselineDir;
        public double Tolerance = 0.02;
        public double Alpha = 0.05;
        public double Mde = 0.02;
        public double RecallFloor = 0.20;
        public double MrrFloor = 0.20;
        public double NdcgFloor = 0.15;
        public double OutcomeFloor = 0.20;
        public bool Recurse;
    }
}
