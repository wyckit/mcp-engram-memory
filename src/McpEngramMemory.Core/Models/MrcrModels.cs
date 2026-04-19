using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// A single MRCR v2 (8-needle) probe: one long conversation plus a question
/// whose gold answer is one of the needles planted in the conversation.
/// </summary>
public sealed record MrcrTask(
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("contextTokens")] int ContextTokens,
    [property: JsonPropertyName("turns")] IReadOnlyList<MrcrTurn> Turns,
    [property: JsonPropertyName("probe")] string Probe,
    [property: JsonPropertyName("goldAnswer")] string GoldAnswer,
    [property: JsonPropertyName("needleIndex")] int NeedleIndex = 0,
    [property: JsonPropertyName("bucket")] string? Bucket = null);

/// <summary>
/// One turn in an MRCR conversation.
/// </summary>
public sealed record MrcrTurn(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

/// <summary>
/// Execution settings for an MRCR v2 benchmark run.
///
/// <c>EngramMode</c> selects the retrieval policy for the engram arm:
/// <list type="bullet">
///   <item><c>"hybrid"</c> — dense BM25+vector search, flat ingest (default, matches the pilot baseline).</item>
///   <item><c>"ordinal"</c> — pair-wise ingest tags each assistant turn with a category signature
///     (normalized user ask) and within-category 1-based ordinal. Probes that match the
///     "Nth X about Y" template resolve to an exact category+ordinal lookup; anything else
///     falls back to hybrid.</item>
/// </list>
/// </summary>
public sealed record MrcrGenerationOptions(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("endpoint")] string? Endpoint = null,
    [property: JsonPropertyName("limit")] int Limit = 25,
    [property: JsonPropertyName("topK")] int TopK = 8,
    [property: JsonPropertyName("maxTokens")] int MaxTokens = 512,
    [property: JsonPropertyName("temperature")] float Temperature = 0.0f,
    [property: JsonPropertyName("maxContextTokens")] int MaxContextTokens = 131072,
    [property: JsonPropertyName("runFullContextArm")] bool RunFullContextArm = true,
    [property: JsonPropertyName("runEngramArm")] bool RunEngramArm = true,
    [property: JsonPropertyName("engramMode")] string EngramMode = "hybrid");

/// <summary>
/// Per-task result within a single arm of the MRCR benchmark.
/// </summary>
public sealed record MrcrTaskResult(
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("contextTokens")] int ContextTokens,
    [property: JsonPropertyName("bucket")] string? Bucket,
    [property: JsonPropertyName("promptTokens")] int PromptTokens,
    [property: JsonPropertyName("similarity")] float Similarity,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("latencyMs")] double LatencyMs,
    [property: JsonPropertyName("answer")] string? Answer,
    [property: JsonPropertyName("goldAnswer")] string GoldAnswer,
    [property: JsonPropertyName("error")] string? Error = null);

/// <summary>
/// Aggregate result for one arm (full_context or engram_retrieval).
/// </summary>
public sealed record MrcrArmResult(
    [property: JsonPropertyName("arm")] string Arm,
    [property: JsonPropertyName("taskResults")] IReadOnlyList<MrcrTaskResult> TaskResults,
    [property: JsonPropertyName("meanSimilarity")] float MeanSimilarity,
    [property: JsonPropertyName("passRate")] float PassRate,
    [property: JsonPropertyName("meanLatencyMs")] double MeanLatencyMs,
    [property: JsonPropertyName("meanPromptTokens")] float MeanPromptTokens,
    [property: JsonPropertyName("totalPromptTokens")] long TotalPromptTokens,
    [property: JsonPropertyName("errorCount")] int ErrorCount,
    [property: JsonPropertyName("bucketMeans")] IReadOnlyDictionary<string, float> BucketMeans);

/// <summary>
/// Full MRCR benchmark run result, comparing the engram arm to the full-context baseline.
/// </summary>
public sealed record MrcrBenchmarkResult(
    [property: JsonPropertyName("datasetId")] string DatasetId,
    [property: JsonPropertyName("runAt")] DateTimeOffset RunAt,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("endpoint")] string? Endpoint,
    [property: JsonPropertyName("taskCount")] int TaskCount,
    [property: JsonPropertyName("topK")] int TopK,
    [property: JsonPropertyName("fullContext")] MrcrArmResult? FullContext,
    [property: JsonPropertyName("engramRetrieval")] MrcrArmResult? EngramRetrieval,
    [property: JsonPropertyName("similarityDelta")] float SimilarityDelta,
    [property: JsonPropertyName("promptTokenReductionRatio")] float PromptTokenReductionRatio,
    [property: JsonPropertyName("notes")] string? Notes = null,
    [property: JsonPropertyName("engramMode")] string EngramMode = "hybrid");

/// <summary>
/// Artifact reference used by the MRCR comparer.
/// </summary>
public sealed record MrcrArtifactReference(
    [property: JsonPropertyName("artifactPath")] string? ArtifactPath,
    [property: JsonPropertyName("datasetId")] string DatasetId,
    [property: JsonPropertyName("runAt")] DateTimeOffset RunAt,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("endpoint")] string? Endpoint);

/// <summary>
/// Structured diff between two MRCR benchmark runs.
/// </summary>
public sealed record MrcrBenchmarkDiffReport(
    [property: JsonPropertyName("datasetId")] string DatasetId,
    [property: JsonPropertyName("baseline")] MrcrArtifactReference Baseline,
    [property: JsonPropertyName("candidate")] MrcrArtifactReference Candidate,
    [property: JsonPropertyName("fullContextSimilarityDelta")] float FullContextSimilarityDelta,
    [property: JsonPropertyName("engramSimilarityDelta")] float EngramSimilarityDelta,
    [property: JsonPropertyName("fullContextPassRateDelta")] float FullContextPassRateDelta,
    [property: JsonPropertyName("engramPassRateDelta")] float EngramPassRateDelta,
    [property: JsonPropertyName("promptTokenReductionDelta")] float PromptTokenReductionDelta,
    [property: JsonPropertyName("summary")] string Summary);
