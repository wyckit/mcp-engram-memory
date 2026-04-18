using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// Canonical graph edge seed for an agent-outcome benchmark dataset.
/// IDs refer to canonical seed entry IDs and are localized per condition at run time.
/// </summary>
public sealed record OutcomeGraphEdgeSeed(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("targetId")] string TargetId,
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("weight")] float Weight = 1.0f);

/// <summary>
/// A task-level benchmark query scored by outcome quality rather than pure retrieval metrics.
/// Optional fields after <c>Notes</c> drive T2 intelligence metrics:
/// <list type="bullet">
/// <item><c>StaleMemoryIds</c> — older/superseded facts that must not be cited (StaleMemoryPenalty)</item>
/// <item><c>DistractorMemoryIds</c> — high-similarity decoys (NoiseResistanceScore)</item>
/// <item><c>OrderedSteps</c> — ordered chain of IDs that must appear; consecutive pairs must be
/// edge-connected in <see cref="AgentOutcomeDataset.Edges"/> (ReasoningPathValidity, DependencyCompletionScore)</item>
/// <item><c>ReasoningHops</c> — declared graph hop-depth (ladder reporting only)</item>
/// <item><c>TaskFamily</c> — family label (e.g. "reasoning-ladder", "contradiction-arena")</item>
/// <item><c>MinEvidence</c>/<c>MaxEvidence</c> — bounds for MinimalEvidenceScore (MaxEvidence is the
/// quadratic-penalty cap; unset defaults to <c>RequiredMemoryIds.Count + 1</c> grace window)</item>
/// </list>
/// </summary>
public sealed record AgentOutcomeTask(
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("queryText")] string QueryText,
    [property: JsonPropertyName("requiredMemoryIds")] IReadOnlyList<string> RequiredMemoryIds,
    [property: JsonPropertyName("helpfulMemoryIds")] IReadOnlyList<string>? HelpfulMemoryIds = null,
    [property: JsonPropertyName("forbiddenMemoryIds")] IReadOnlyList<string>? ForbiddenMemoryIds = null,
    [property: JsonPropertyName("k")] int K = 3,
    [property: JsonPropertyName("minScore")] float MinScore = 0.35f,
    [property: JsonPropertyName("notes")] string? Notes = null,
    [property: JsonPropertyName("staleMemoryIds")] IReadOnlyList<string>? StaleMemoryIds = null,
    [property: JsonPropertyName("distractorMemoryIds")] IReadOnlyList<string>? DistractorMemoryIds = null,
    [property: JsonPropertyName("orderedSteps")] IReadOnlyList<string>? OrderedSteps = null,
    [property: JsonPropertyName("reasoningHops")] int? ReasoningHops = null,
    [property: JsonPropertyName("taskFamily")] string? TaskFamily = null,
    [property: JsonPropertyName("minEvidence")] int? MinEvidence = null,
    [property: JsonPropertyName("maxEvidence")] int? MaxEvidence = null);

/// <summary>
/// A benchmark dataset for comparing memory conditions on simulated agent outcomes.
/// </summary>
public sealed record AgentOutcomeDataset(
    [property: JsonPropertyName("datasetId")] string DatasetId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("seedEntries")] IReadOnlyList<BenchmarkSeedEntry> SeedEntries,
    [property: JsonPropertyName("edges")] IReadOnlyList<OutcomeGraphEdgeSeed> Edges,
    [property: JsonPropertyName("tasks")] IReadOnlyList<AgentOutcomeTask> Tasks,
    [property: JsonPropertyName("transcriptChunkSize")] int TranscriptChunkSize = 2);

/// <summary>
/// Score for a single task under one memory condition.
/// Fields ending in *Score/Penalty are T2 intelligence metrics (1.0 = perfect, 0.0 = worst except
/// StaleMemoryPenalty where 1.0 = full penalty). All default to 1.0 (or 0.0 for penalties) when the
/// task omits the driving field, so they never degrade scores for legacy datasets.
/// </summary>
public sealed record AgentOutcomeTaskScore(
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("requiredCoverage")] float RequiredCoverage,
    [property: JsonPropertyName("helpfulCoverage")] float HelpfulCoverage,
    [property: JsonPropertyName("conflictRate")] float ConflictRate,
    [property: JsonPropertyName("successScore")] float SuccessScore,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("latencyMs")] double LatencyMs,
    [property: JsonPropertyName("retrievedMemoryIds")] IReadOnlyList<string> RetrievedMemoryIds,
    [property: JsonPropertyName("reasoningPathValidity")] float ReasoningPathValidity = 1f,
    [property: JsonPropertyName("dependencyCompletionScore")] float DependencyCompletionScore = 1f,
    [property: JsonPropertyName("staleMemoryPenalty")] float StaleMemoryPenalty = 0f,
    [property: JsonPropertyName("minimalEvidenceScore")] float MinimalEvidenceScore = 1f,
    [property: JsonPropertyName("noiseResistanceScore")] float NoiseResistanceScore = 1f,
    [property: JsonPropertyName("noiseResistanceScoreRanked")] float NoiseResistanceScoreRanked = 1f,
    [property: JsonPropertyName("contradictionHandlingScore")] float ContradictionHandlingScore = 1f);

/// <summary>
/// Aggregate result for one memory condition across all tasks in a dataset.
/// </summary>
public sealed record AgentOutcomeConditionResult(
    [property: JsonPropertyName("condition")] string Condition,
    [property: JsonPropertyName("taskScores")] IReadOnlyList<AgentOutcomeTaskScore> TaskScores,
    [property: JsonPropertyName("meanSuccessScore")] float MeanSuccessScore,
    [property: JsonPropertyName("passRate")] float PassRate,
    [property: JsonPropertyName("meanRequiredCoverage")] float MeanRequiredCoverage,
    [property: JsonPropertyName("meanConflictRate")] float MeanConflictRate,
    [property: JsonPropertyName("meanLatencyMs")] double MeanLatencyMs,
    [property: JsonPropertyName("meanReasoningPathValidity")] float MeanReasoningPathValidity = 1f,
    [property: JsonPropertyName("meanDependencyCompletionScore")] float MeanDependencyCompletionScore = 1f,
    [property: JsonPropertyName("meanStaleMemoryPenalty")] float MeanStaleMemoryPenalty = 0f,
    [property: JsonPropertyName("meanMinimalEvidenceScore")] float MeanMinimalEvidenceScore = 1f,
    [property: JsonPropertyName("meanNoiseResistanceScore")] float MeanNoiseResistanceScore = 1f,
    [property: JsonPropertyName("meanNoiseResistanceScoreRanked")] float MeanNoiseResistanceScoreRanked = 1f,
    [property: JsonPropertyName("meanContradictionHandlingScore")] float MeanContradictionHandlingScore = 1f);

/// <summary>
/// Comparison of a memory condition against the no-memory baseline.
/// </summary>
public sealed record AgentOutcomeConditionComparison(
    [property: JsonPropertyName("condition")] string Condition,
    [property: JsonPropertyName("result")] AgentOutcomeConditionResult Result,
    [property: JsonPropertyName("successDelta")] float SuccessDelta,
    [property: JsonPropertyName("passRateDelta")] float PassRateDelta,
    [property: JsonPropertyName("requiredCoverageDelta")] float RequiredCoverageDelta,
    [property: JsonPropertyName("conflictDelta")] float ConflictDelta,
    [property: JsonPropertyName("latencyDeltaMs")] double LatencyDeltaMs);

/// <summary>
/// Full result of an agent-outcome benchmark run.
/// </summary>
public sealed record AgentOutcomeBenchmarkResult(
    [property: JsonPropertyName("datasetId")] string DatasetId,
    [property: JsonPropertyName("runAt")] DateTimeOffset RunAt,
    [property: JsonPropertyName("baselineCondition")] string BaselineCondition,
    [property: JsonPropertyName("baseline")] AgentOutcomeConditionResult Baseline,
    [property: JsonPropertyName("comparisons")] IReadOnlyList<AgentOutcomeConditionComparison> Comparisons,
    [property: JsonPropertyName("embeddingModel")] string? EmbeddingModel = null,
    [property: JsonPropertyName("embeddingDimensions")] int? EmbeddingDimensions = null,
    [property: JsonPropertyName("notes")] string? Notes = null);
