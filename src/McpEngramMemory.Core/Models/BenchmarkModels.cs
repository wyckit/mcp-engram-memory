using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// A seed entry for a benchmark dataset.
/// </summary>
public sealed record BenchmarkSeedEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("category")] string? Category = null);

/// <summary>
/// A benchmark query with expected results and graded relevance judgments.
/// Grade scale: 3 = highly relevant, 2 = relevant, 1 = marginally relevant, 0 = not relevant.
/// </summary>
public sealed record BenchmarkQuery(
    [property: JsonPropertyName("queryId")] string QueryId,
    [property: JsonPropertyName("queryText")] string QueryText,
    [property: JsonPropertyName("relevanceGrades")] Dictionary<string, int> RelevanceGrades,
    [property: JsonPropertyName("k")] int K = 5);

/// <summary>
/// A complete benchmark dataset with seed entries and queries.
/// </summary>
public sealed record BenchmarkDataset(
    [property: JsonPropertyName("datasetId")] string DatasetId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("seedEntries")] IReadOnlyList<BenchmarkSeedEntry> SeedEntries,
    [property: JsonPropertyName("queries")] IReadOnlyList<BenchmarkQuery> Queries);

/// <summary>
/// Scoring result for a single benchmark query.
/// </summary>
public sealed record QueryScore(
    [property: JsonPropertyName("queryId")] string QueryId,
    [property: JsonPropertyName("recallAtK")] float RecallAtK,
    [property: JsonPropertyName("precisionAtK")] float PrecisionAtK,
    [property: JsonPropertyName("mrr")] float MRR,
    [property: JsonPropertyName("ndcgAtK")] float NdcgAtK,
    [property: JsonPropertyName("latencyMs")] double LatencyMs,
    [property: JsonPropertyName("actualResultIds")] IReadOnlyList<string> ActualResultIds);

/// <summary>
/// Aggregate result of a full benchmark run.
/// </summary>
public sealed record BenchmarkRunResult(
    [property: JsonPropertyName("datasetId")] string DatasetId,
    [property: JsonPropertyName("runAt")] DateTimeOffset RunAt,
    [property: JsonPropertyName("queryScores")] IReadOnlyList<QueryScore> QueryScores,
    [property: JsonPropertyName("meanRecallAtK")] float MeanRecallAtK,
    [property: JsonPropertyName("meanPrecisionAtK")] float MeanPrecisionAtK,
    [property: JsonPropertyName("meanMrr")] float MeanMRR,
    [property: JsonPropertyName("meanNdcgAtK")] float MeanNdcgAtK,
    [property: JsonPropertyName("meanLatencyMs")] double MeanLatencyMs,
    [property: JsonPropertyName("p95LatencyMs")] double P95LatencyMs,
    [property: JsonPropertyName("totalEntries")] int TotalEntries,
    [property: JsonPropertyName("totalQueries")] int TotalQueries);

/// <summary>
/// Comparison of a single ablation mode against the baseline.
/// Positive deltas mean the mode improved over baseline.
/// </summary>
public sealed record AblationComparison(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("result")] BenchmarkRunResult Result,
    [property: JsonPropertyName("recallDelta")] float RecallDelta,
    [property: JsonPropertyName("precisionDelta")] float PrecisionDelta,
    [property: JsonPropertyName("mrrDelta")] float MrrDelta,
    [property: JsonPropertyName("ndcgDelta")] float NdcgDelta,
    [property: JsonPropertyName("latencyDeltaMs")] double LatencyDeltaMs);

/// <summary>
/// Result of an ablation study: baseline + per-mode comparisons.
/// </summary>
public sealed record AblationResult(
    [property: JsonPropertyName("datasetId")] string DatasetId,
    [property: JsonPropertyName("baseline")] BenchmarkRunResult Baseline,
    [property: JsonPropertyName("comparisons")] IReadOnlyList<AblationComparison> Comparisons);
