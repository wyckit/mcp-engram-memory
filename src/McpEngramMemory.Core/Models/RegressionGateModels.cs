using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// Result of a one-sided paired Student t-test over per-query metric deltas
/// (candidate − baseline). H1 is "mean delta &lt; 0" (regression), so a small
/// <see cref="PValue"/> means the drop is statistically significant.
/// </summary>
public sealed record PairedTTestResult(
    [property: JsonPropertyName("n")] int N,
    [property: JsonPropertyName("meanDelta")] double MeanDelta,
    [property: JsonPropertyName("stdDevDelta")] double StdDevDelta,
    [property: JsonPropertyName("tStatistic")] double TStatistic,
    [property: JsonPropertyName("degreesOfFreedom")] double DegreesOfFreedom,
    [property: JsonPropertyName("pValue")] double PValue);

/// <summary>
/// Options for the benchmark regression gate. Defaults mirror
/// scripts/check-benchmark-regression.sh and docs/benchmarks.md.
/// </summary>
public sealed record RegressionGateOptions(
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("baselineDir")] string BaselineDir,
    [property: JsonPropertyName("tolerance")] double Tolerance = 0.02,
    [property: JsonPropertyName("alpha")] double Alpha = 0.05,
    [property: JsonPropertyName("minimumDetectableEffect")] double MinimumDetectableEffect = 0.02,
    [property: JsonPropertyName("recallFloor")] double RecallFloor = 0.20,
    [property: JsonPropertyName("mrrFloor")] double MrrFloor = 0.20,
    [property: JsonPropertyName("ndcgFloor")] double NdcgFloor = 0.15,
    [property: JsonPropertyName("outcomeFloor")] double OutcomeFloor = 0.20,
    [property: JsonPropertyName("recurse")] bool Recurse = false);

/// <summary>
/// A single gated (dataset, metric) check row. <see cref="Method"/> is
/// "floor-only" (no baseline), "legacy" (point comparison against the baseline
/// aggregate), or "paired-t" (Holm-corrected one-sided paired t-test over
/// per-query deltas).
/// </summary>
public sealed record MetricCheck(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("metric")] string Metric,
    [property: JsonPropertyName("value")] double Value,
    [property: JsonPropertyName("floor")] double Floor,
    [property: JsonPropertyName("baselineValue")] double? BaselineValue,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("tTest")] PairedTTestResult? TTest,
    [property: JsonPropertyName("holmThreshold")] double? HolmThreshold,
    [property: JsonPropertyName("holmSignificant")] bool? HolmSignificant);

/// <summary>
/// Full report of a regression-gate run: every check row, gate-level warnings
/// (legacy fallbacks, unparseable files), and summary counts.
/// </summary>
public sealed record RegressionGateReport(
    [property: JsonPropertyName("checks")] IReadOnlyList<MetricCheck> Checks,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("skippedFiles")] int SkippedFiles,
    [property: JsonPropertyName("passed")] bool Passed);
