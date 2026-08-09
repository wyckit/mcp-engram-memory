using System.Text.Json;
using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// Statistically sound benchmark regression gate. Mirrors the artifact parsing and
/// table semantics of scripts/check-benchmark-regression.sh, but replaces the flat
/// point-estimate drift check with a Holm-Bonferroni-corrected one-sided paired
/// t-test over per-query deltas whenever both candidate and baseline carry pairable
/// per-query vectors. Absolute floors are always enforced regardless of statistics.
/// Falls back per-comparison to the legacy point comparison (with a loud warning)
/// when per-query data is missing, mismatched, or too small (n &lt; 2).
/// </summary>
public static class RegressionGateRunner
{
    /// <summary>Evaluates the gate for <see cref="RegressionGateOptions.Path"/> against the pinned baselines.</summary>
    /// <exception cref="ArgumentException">Thrown when the path does not exist or no candidate .json files are found.</exception>
    public static RegressionGateReport Evaluate(RegressionGateOptions options)
    {
        string path = options.Path ?? throw new ArgumentException("A candidate path is required.", nameof(options));
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new ArgumentException($"Path not found: {path}", nameof(options));

        var files = CollectCandidateFiles(path, options.Recurse);
        if (files.Count == 0)
            throw new ArgumentException($"No .json benchmark files found at: {path}", nameof(options));

        var baselineIndex = BuildBaselineIndex(options.BaselineDir);
        return EvaluateFiles(files, baselineIndex, options);
    }

    /// <summary>
    /// Internal seam for unit tests: evaluate explicit candidate files against a
    /// pre-built baseline index ((datasetId, mode) → baseline file path).
    /// </summary>
    internal static RegressionGateReport EvaluateFiles(
        IReadOnlyList<string> candidateFiles,
        IReadOnlyDictionary<(string ds, string mode), string> baselineIndex,
        RegressionGateOptions options)
    {
        var pending = new List<PendingCheck>();
        var warnings = new List<string>();
        int skipped = 0;

        foreach (string file in candidateFiles)
        {
            ParsedArtifact? artifact = TryParseArtifact(file, out bool unparseable);
            if (artifact is null)
            {
                if (unparseable)
                    warnings.Add($"WARN: skipping unparseable JSON: {Path.GetFileName(file)}");
                skipped++;
                continue;
            }

            string label = artifact.Mode.Length > 0 ? $"{artifact.DatasetId}/{artifact.Mode}" : artifact.DatasetId;

            // Resolve the pinned baseline: exact (ds, mode), then (ds, "") fallback.
            ParsedArtifact? baseline = null;
            if (baselineIndex.TryGetValue((artifact.DatasetId, artifact.Mode), out string? bf)
                || baselineIndex.TryGetValue((artifact.DatasetId, ""), out bf))
            {
                baseline = TryParseArtifact(bf!, out _);
            }

            foreach (var metric in artifact.Metrics)
            {
                double floor = FloorFor(metric.Name, options);
                var check = new PendingCheck
                {
                    Label = label,
                    Metric = metric.Name,
                    Value = metric.Value,
                    Floor = floor,
                    Method = "floor-only"
                };

                // STEP 1: floor check — always enforced, regardless of statistics.
                if (metric.Value < floor)
                {
                    check.FloorFailed = true;
                    check.Notes.Add(Inv($"below floor {floor:F3}"));
                }

                // STEP 2: baseline drift.
                ParsedMetric? baseMetric = baseline?.Metrics.FirstOrDefault(m => m.Name == metric.Name);
                if (baseMetric is not null)
                {
                    check.BaselineValue = baseMetric.Value;

                    if (TryPairDeltas(metric, baseMetric, label, warnings, out var deltas))
                    {
                        // Buffer into the run-wide t-test family; decided after all files (STEP 3).
                        check.Method = "paired-t";
                        check.TTest = PairedStatistics.OneSidedPairedTTest(deltas);
                    }
                    else
                    {
                        // Legacy point comparison: value < baseline − tolerance ⇒ FAIL.
                        check.Method = "legacy";
                        if (metric.Value < baseMetric.Value - options.Tolerance)
                        {
                            check.DriftFailed = true;
                            check.Notes.Add(Inv(
                                $"drift {metric.Value - baseMetric.Value:+0.000;-0.000} vs baseline {baseMetric.Value:F3} (tol {options.Tolerance})"));
                        }
                    }
                }

                pending.Add(check);
            }
        }

        // STEP 3: Holm-Bonferroni across the buffered paired-t family for the whole run.
        var family = pending.Where(c => c.TTest is not null).ToList();
        if (family.Count > 0)
        {
            var pValues = family.Select(c => c.TTest!.PValue).ToList();
            bool[] significant = PairedStatistics.HolmBonferroni(pValues, options.Alpha);

            // Per-check Holm threshold: alpha/(m − rank) at its position in the sorted order.
            var order = Enumerable.Range(0, family.Count).OrderBy(i => pValues[i]).ToArray();
            var thresholds = new double[family.Count];
            for (int rank = 0; rank < order.Length; rank++)
                thresholds[order[rank]] = options.Alpha / (family.Count - rank);

            for (int i = 0; i < family.Count; i++)
            {
                var check = family[i];
                var t = check.TTest!;
                check.HolmThreshold = thresholds[i];
                check.HolmSignificant = significant[i];

                double meanDrop = -t.MeanDelta;
                bool fails = significant[i] && meanDrop > options.MinimumDetectableEffect;
                check.DriftFailed = fails;

                string verdict = fails
                    ? "significant regression"
                    : significant[i]
                        ? Inv($"significant but drop {meanDrop:0.000} <= mde {options.MinimumDetectableEffect}")
                        : "not significant";
                check.Notes.Add(Inv(
                    $"Δmean={t.MeanDelta:+0.000;-0.000;0.000} n={t.N} t={t.TStatistic:0.000} p={t.PValue:0.000} holm-α={thresholds[i]:0.0000} — {verdict}"));
            }
        }

        var checks = new List<MetricCheck>(pending.Count);
        int failed = 0;
        foreach (var c in pending)
        {
            bool fail = c.FloorFailed || c.DriftFailed;
            if (fail) failed++;
            checks.Add(new MetricCheck(
                Label: c.Label,
                Metric: c.Metric,
                Value: c.Value,
                Floor: c.Floor,
                BaselineValue: c.BaselineValue,
                Status: fail ? "FAIL" : "PASS",
                Notes: string.Join("; ", c.Notes),
                Method: c.Method,
                TTest: c.TTest,
                HolmThreshold: c.HolmThreshold,
                HolmSignificant: c.HolmSignificant));
        }

        return new RegressionGateReport(
            Checks: checks,
            Warnings: warnings,
            Total: checks.Count,
            Failed: failed,
            SkippedFiles: skipped,
            Passed: failed == 0 && checks.Count > 0);
    }

    /// <summary>
    /// Attempts to build per-query deltas (candidate − baseline) paired by query/task id.
    /// Requires both vectors present, identical id sets, and at least 2 pairs; otherwise
    /// appends the appropriate fallback warning and returns false (legacy comparison).
    /// </summary>
    private static bool TryPairDeltas(
        ParsedMetric candidate, ParsedMetric baseline, string label, List<string> warnings, out List<double> deltas)
    {
        deltas = [];

        if (baseline.PerQuery is null)
        {
            string warning =
                $"WARNING: baseline for {label} has no per-query scores — statistical gate degraded to legacy point comparison; regenerate the pinned baseline (see docs/benchmarks.md)";
            if (!warnings.Contains(warning)) warnings.Add(warning);
            return false;
        }
        if (candidate.PerQuery is null)
        {
            string warning =
                $"WARNING: candidate for {label} has no per-query scores — falling back to legacy comparison";
            if (!warnings.Contains(warning)) warnings.Add(warning);
            return false;
        }

        if (candidate.PerQuery.Count != baseline.PerQuery.Count
            || !candidate.PerQuery.Keys.All(baseline.PerQuery.ContainsKey))
        {
            string warning = Inv(
                $"WARNING: query id sets differ between candidate and baseline for {label} ({candidate.PerQuery.Count} vs {baseline.PerQuery.Count}) — falling back to legacy comparison");
            if (!warnings.Contains(warning)) warnings.Add(warning);
            return false;
        }

        if (candidate.PerQuery.Count < 2)
        {
            string warning = Inv(
                $"WARNING: fewer than 2 paired queries for {label} (n={candidate.PerQuery.Count}) — falling back to legacy comparison");
            if (!warnings.Contains(warning)) warnings.Add(warning);
            return false;
        }

        // Deltas ordered by candidate id order (insertion order of the candidate vector).
        foreach (var (id, value) in candidate.PerQuery)
            deltas.Add(value - baseline.PerQuery[id]);
        return true;
    }

    private static double FloorFor(string metric, RegressionGateOptions options) => metric switch
    {
        "Recall@K" => options.RecallFloor,
        "MRR" => options.MrrFloor,
        "nDCG@K" => options.NdcgFloor,
        "SuccessScore" or "PassRate" or "RequiredCoverage" => options.OutcomeFloor,
        _ => 0.0
    };

    private static List<string> CollectCandidateFiles(string path, bool recurse)
    {
        if (File.Exists(path)) return [path];
        var option = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.GetFiles(path, "*.json", option).OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    /// <summary>Builds the pinned-baseline index: (datasetId, mode) → file, plus a (datasetId, "") first-seen fallback.</summary>
    internal static IReadOnlyDictionary<(string ds, string mode), string> BuildBaselineIndex(string baselineDir)
    {
        var index = new Dictionary<(string ds, string mode), string>();
        if (!Directory.Exists(baselineDir)) return index;

        foreach (string bf in Directory.GetFiles(baselineDir, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(bf));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                string? ds = root.TryGetProperty("datasetId", out var dsEl) && dsEl.ValueKind == JsonValueKind.String
                    ? dsEl.GetString() : null;
                if (string.IsNullOrEmpty(ds)) continue;
                string mode = root.TryGetProperty("mode", out var modeEl) && modeEl.ValueKind == JsonValueKind.String
                    ? modeEl.GetString() ?? "" : "";
                index[(ds, mode)] = bf;
                if (!index.ContainsKey((ds, ""))) index[(ds, "")] = bf;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Unreadable baseline file — skip, matching the shell script's tolerance.
            }
        }
        return index;
    }

    /// <summary>
    /// Shape-tolerant artifact parse. Recognizes the flat IR shape (meanRecallAtK/meanMrr/
    /// meanNdcgAtK with optional queryScores[]) and the agent-outcome shape (comparisons[]
    /// with condition == "full_engram"; per-task vectors under taskScores[] (offline) or
    /// taskResults[] (live) — both property names are accepted). Returns null when the file
    /// is unparseable (out flag set) or carries no recognized metrics.
    /// </summary>
    private static ParsedArtifact? TryParseArtifact(string file, out bool unparseable)
    {
        unparseable = false;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            unparseable = true;
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { unparseable = true; return null; }

            var metrics = new List<ParsedMetric>();

            // Flat IR-quality shape.
            var queryScores = GetArray(root, "queryScores");
            AddIfPresent(root, "meanRecallAtK", "Recall@K", queryScores, "recallAtK", metrics);
            AddIfPresent(root, "meanMrr", "MRR", queryScores, "mrr", metrics);
            AddIfPresent(root, "meanNdcgAtK", "nDCG@K", queryScores, "ndcgAtK", metrics);

            // Agent-outcome nested shape: gate the full_engram condition.
            if (root.TryGetProperty("comparisons", out var comparisons) && comparisons.ValueKind == JsonValueKind.Array)
            {
                foreach (var comparison in comparisons.EnumerateArray())
                {
                    if (comparison.ValueKind != JsonValueKind.Object) continue;
                    if (!comparison.TryGetProperty("condition", out var cond)
                        || cond.ValueKind != JsonValueKind.String
                        || cond.GetString() != "full_engram") continue;
                    if (!comparison.TryGetProperty("result", out var result)
                        || result.ValueKind != JsonValueKind.Object) continue;

                    // Offline artifacts serialize taskScores; live artifacts serialize taskResults.
                    var tasks = GetArray(result, "taskScores") ?? GetArray(result, "taskResults");
                    AddIfPresent(result, "meanSuccessScore", "SuccessScore", tasks, "successScore", metrics, idField: "taskId");
                    AddIfPresent(result, "passRate", "PassRate", tasks, "passed", metrics, idField: "taskId");
                    AddIfPresent(result, "meanRequiredCoverage", "RequiredCoverage", tasks, "requiredCoverage", metrics, idField: "taskId");
                    break;
                }
            }

            if (metrics.Count == 0) return null;

            string ds = root.TryGetProperty("datasetId", out var dsEl) && dsEl.ValueKind == JsonValueKind.String
                ? dsEl.GetString() ?? "(unknown)" : "(unknown)";
            string mode = root.TryGetProperty("mode", out var modeEl) && modeEl.ValueKind == JsonValueKind.String
                ? modeEl.GetString() ?? "" : "";
            return new ParsedArtifact(ds, mode, metrics);
        }
    }

    private static JsonElement? GetArray(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array ? el : null;

    /// <summary>
    /// Adds a metric when the aggregate field exists, extracting the optional per-query
    /// vector (id → value) from <paramref name="perQueryArray"/>. Booleans (PassRate's
    /// per-task "passed") map to 1/0.
    /// </summary>
    private static void AddIfPresent(
        JsonElement obj, string aggregateField, string metricName,
        JsonElement? perQueryArray, string perQueryField, List<ParsedMetric> metrics, string idField = "queryId")
    {
        if (!obj.TryGetProperty(aggregateField, out var aggEl) || aggEl.ValueKind != JsonValueKind.Number) return;
        double value = aggEl.GetDouble();

        Dictionary<string, double>? perQuery = null;
        if (perQueryArray is { } array)
        {
            perQuery = [];
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty(idField, out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                if (!item.TryGetProperty(perQueryField, out var vEl)) continue;
                double v = vEl.ValueKind switch
                {
                    JsonValueKind.Number => vEl.GetDouble(),
                    JsonValueKind.True => 1.0,
                    JsonValueKind.False => 0.0,
                    _ => double.NaN
                };
                if (double.IsNaN(v)) continue;
                perQuery[idEl.GetString()!] = v;
            }
            if (perQuery.Count == 0) perQuery = null;
        }

        metrics.Add(new ParsedMetric(metricName, value, perQuery));
    }

    private static string Inv(FormattableString s) => FormattableString.Invariant(s);

    /// <summary>A parsed candidate or baseline artifact: identity plus its gated metrics.</summary>
    private sealed record ParsedArtifact(string DatasetId, string Mode, List<ParsedMetric> Metrics);

    /// <summary>One gated metric: aggregate value plus the optional per-query vector (id → score).</summary>
    private sealed record ParsedMetric(string Name, double Value, Dictionary<string, double>? PerQuery);

    /// <summary>Mutable working row; materialized into an immutable <see cref="MetricCheck"/> at the end.</summary>
    private sealed class PendingCheck
    {
        public required string Label { get; init; }
        public required string Metric { get; init; }
        public double Value { get; init; }
        public double Floor { get; init; }
        public double? BaselineValue { get; set; }
        public string Method { get; set; } = "floor-only";
        public bool FloorFailed { get; set; }
        public bool DriftFailed { get; set; }
        public PairedTTestResult? TTest { get; set; }
        public double? HolmThreshold { get; set; }
        public bool? HolmSignificant { get; set; }
        public List<string> Notes { get; } = [];
    }
}
