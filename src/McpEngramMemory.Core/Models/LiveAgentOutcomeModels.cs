using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// Execution settings for a live agent-outcome benchmark against a real model.
/// </summary>
public sealed record LiveAgentOutcomeGenerationOptions(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("endpoint")] string? Endpoint = null,
    [property: JsonPropertyName("contextualPrefix")] bool ContextualPrefix = false,
    [property: JsonPropertyName("maxTokens")] int MaxTokens = 320,
    [property: JsonPropertyName("temperature")] float Temperature = 0.1f,
    [property: JsonPropertyName("runAblations")] bool RunAblations = false);

/// <summary>
/// Structured response schema expected from the benchmarked model.
/// </summary>
public sealed record LiveAgentOutcomeModelResponse(
    [property: JsonPropertyName("answer")] string? Answer,
    [property: JsonPropertyName("evidence_ids")] IReadOnlyList<string>? EvidenceIds,
    [property: JsonPropertyName("insufficient_context")] bool InsufficientContext = false);

/// <summary>
/// Per-task result from a live model run.
/// </summary>
public sealed record LiveAgentOutcomeTaskResult(
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("requiredCoverage")] float RequiredCoverage,
    [property: JsonPropertyName("helpfulCoverage")] float HelpfulCoverage,
    [property: JsonPropertyName("conflictRate")] float ConflictRate,
    [property: JsonPropertyName("successScore")] float SuccessScore,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("latencyMs")] double LatencyMs,
    [property: JsonPropertyName("responseFormatValid")] bool ResponseFormatValid,
    [property: JsonPropertyName("insufficientContext")] bool InsufficientContext,
    [property: JsonPropertyName("contextMemoryIds")] IReadOnlyList<string> ContextMemoryIds,
    [property: JsonPropertyName("citedMemoryIds")] IReadOnlyList<string> CitedMemoryIds,
    [property: JsonPropertyName("answer")] string? Answer,
    [property: JsonPropertyName("rawResponse")] string? RawResponse,
    [property: JsonPropertyName("reasoningPathValidity")] float ReasoningPathValidity = 1f,
    [property: JsonPropertyName("dependencyCompletionScore")] float DependencyCompletionScore = 1f,
    [property: JsonPropertyName("staleMemoryPenalty")] float StaleMemoryPenalty = 0f,
    [property: JsonPropertyName("minimalEvidenceScore")] float MinimalEvidenceScore = 1f,
    [property: JsonPropertyName("noiseResistanceScore")] float NoiseResistanceScore = 1f,
    [property: JsonPropertyName("noiseResistanceScoreRanked")] float NoiseResistanceScoreRanked = 1f,
    [property: JsonPropertyName("contradictionHandlingScore")] float ContradictionHandlingScore = 1f);

/// <summary>
/// Aggregate result for one memory condition in a live model benchmark run.
/// </summary>
public sealed record LiveAgentOutcomeConditionResult(
    [property: JsonPropertyName("condition")] string Condition,
    [property: JsonPropertyName("taskResults")] IReadOnlyList<LiveAgentOutcomeTaskResult> TaskResults,
    [property: JsonPropertyName("meanSuccessScore")] float MeanSuccessScore,
    [property: JsonPropertyName("passRate")] float PassRate,
    [property: JsonPropertyName("meanRequiredCoverage")] float MeanRequiredCoverage,
    [property: JsonPropertyName("meanConflictRate")] float MeanConflictRate,
    [property: JsonPropertyName("meanLatencyMs")] double MeanLatencyMs,
    [property: JsonPropertyName("formatValidityRate")] float FormatValidityRate,
    [property: JsonPropertyName("meanReasoningPathValidity")] float MeanReasoningPathValidity = 1f,
    [property: JsonPropertyName("meanDependencyCompletionScore")] float MeanDependencyCompletionScore = 1f,
    [property: JsonPropertyName("meanStaleMemoryPenalty")] float MeanStaleMemoryPenalty = 0f,
    [property: JsonPropertyName("meanMinimalEvidenceScore")] float MeanMinimalEvidenceScore = 1f,
    [property: JsonPropertyName("meanNoiseResistanceScore")] float MeanNoiseResistanceScore = 1f,
    [property: JsonPropertyName("meanNoiseResistanceScoreRanked")] float MeanNoiseResistanceScoreRanked = 1f,
    [property: JsonPropertyName("meanContradictionHandlingScore")] float MeanContradictionHandlingScore = 1f);

/// <summary>
/// Comparison of a live memory condition against the no-memory baseline.
/// </summary>
public sealed record LiveAgentOutcomeConditionComparison(
    [property: JsonPropertyName("condition")] string Condition,
    [property: JsonPropertyName("result")] LiveAgentOutcomeConditionResult Result,
    [property: JsonPropertyName("successDelta")] float SuccessDelta,
    [property: JsonPropertyName("passRateDelta")] float PassRateDelta,
    [property: JsonPropertyName("requiredCoverageDelta")] float RequiredCoverageDelta,
    [property: JsonPropertyName("conflictDelta")] float ConflictDelta,
    [property: JsonPropertyName("latencyDeltaMs")] double LatencyDeltaMs,
    [property: JsonPropertyName("formatValidityDelta")] float FormatValidityDelta);

/// <summary>
/// Full result of a live agent-outcome benchmark run against a real model provider.
/// </summary>
public sealed record LiveAgentOutcomeBenchmarkResult(
    [property: JsonPropertyName("datasetId")] string DatasetId,
    [property: JsonPropertyName("runAt")] DateTimeOffset RunAt,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("endpoint")] string? Endpoint,
    [property: JsonPropertyName("baselineCondition")] string BaselineCondition,
    [property: JsonPropertyName("baseline")] LiveAgentOutcomeConditionResult Baseline,
    [property: JsonPropertyName("comparisons")] IReadOnlyList<LiveAgentOutcomeConditionComparison> Comparisons,
    [property: JsonPropertyName("notes")] string? Notes = null);

/// <summary>
/// Run metadata for an artifact comparison report.
/// </summary>
public sealed record LiveAgentOutcomeArtifactReference(
    [property: JsonPropertyName("artifactPath")] string? ArtifactPath,
    [property: JsonPropertyName("datasetId")] string DatasetId,
    [property: JsonPropertyName("runAt")] DateTimeOffset RunAt,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("endpoint")] string? Endpoint);

/// <summary>
/// Aggregate delta for a memory condition between two live benchmark runs.
/// </summary>
public sealed record LiveAgentOutcomeConditionDiff(
    [property: JsonPropertyName("condition")] string Condition,
    [property: JsonPropertyName("baselinePassRate")] float BaselinePassRate,
    [property: JsonPropertyName("candidatePassRate")] float CandidatePassRate,
    [property: JsonPropertyName("passRateDelta")] float PassRateDelta,
    [property: JsonPropertyName("baselineMeanSuccessScore")] float BaselineMeanSuccessScore,
    [property: JsonPropertyName("candidateMeanSuccessScore")] float CandidateMeanSuccessScore,
    [property: JsonPropertyName("successDelta")] float SuccessDelta,
    [property: JsonPropertyName("baselineRequiredCoverage")] float BaselineRequiredCoverage,
    [property: JsonPropertyName("candidateRequiredCoverage")] float CandidateRequiredCoverage,
    [property: JsonPropertyName("requiredCoverageDelta")] float RequiredCoverageDelta,
    [property: JsonPropertyName("baselineConflictRate")] float BaselineConflictRate,
    [property: JsonPropertyName("candidateConflictRate")] float CandidateConflictRate,
    [property: JsonPropertyName("conflictDelta")] float ConflictDelta,
    [property: JsonPropertyName("baselineLatencyMs")] double BaselineLatencyMs,
    [property: JsonPropertyName("candidateLatencyMs")] double CandidateLatencyMs,
    [property: JsonPropertyName("latencyDeltaMs")] double LatencyDeltaMs,
    [property: JsonPropertyName("baselineFormatValidityRate")] float BaselineFormatValidityRate,
    [property: JsonPropertyName("candidateFormatValidityRate")] float CandidateFormatValidityRate,
    [property: JsonPropertyName("formatValidityDelta")] float FormatValidityDelta);

/// <summary>
/// Per-task delta between two live benchmark runs.
/// </summary>
public sealed record LiveAgentOutcomeTaskDiff(
    [property: JsonPropertyName("condition")] string Condition,
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("changeType")] string ChangeType,
    [property: JsonPropertyName("baselinePassed")] bool BaselinePassed,
    [property: JsonPropertyName("candidatePassed")] bool CandidatePassed,
    [property: JsonPropertyName("baselineSuccessScore")] float BaselineSuccessScore,
    [property: JsonPropertyName("candidateSuccessScore")] float CandidateSuccessScore,
    [property: JsonPropertyName("successDelta")] float SuccessDelta,
    [property: JsonPropertyName("baselineRequiredCoverage")] float BaselineRequiredCoverage,
    [property: JsonPropertyName("candidateRequiredCoverage")] float CandidateRequiredCoverage,
    [property: JsonPropertyName("requiredCoverageDelta")] float RequiredCoverageDelta,
    [property: JsonPropertyName("baselineResponseFormatValid")] bool BaselineResponseFormatValid,
    [property: JsonPropertyName("candidateResponseFormatValid")] bool CandidateResponseFormatValid,
    [property: JsonPropertyName("baselineEvidenceIds")] IReadOnlyList<string> BaselineEvidenceIds,
    [property: JsonPropertyName("candidateEvidenceIds")] IReadOnlyList<string> CandidateEvidenceIds,
    [property: JsonPropertyName("addedEvidenceIds")] IReadOnlyList<string> AddedEvidenceIds,
    [property: JsonPropertyName("removedEvidenceIds")] IReadOnlyList<string> RemovedEvidenceIds);

/// <summary>
/// Structured diff report between two live benchmark artifacts.
/// </summary>
public sealed record LiveAgentOutcomeBenchmarkDiffReport(
    [property: JsonPropertyName("datasetId")] string DatasetId,
    [property: JsonPropertyName("baseline")] LiveAgentOutcomeArtifactReference Baseline,
    [property: JsonPropertyName("candidate")] LiveAgentOutcomeArtifactReference Candidate,
    [property: JsonPropertyName("conditionDiffs")] IReadOnlyList<LiveAgentOutcomeConditionDiff> ConditionDiffs,
    [property: JsonPropertyName("improvedTasks")] IReadOnlyList<LiveAgentOutcomeTaskDiff> ImprovedTasks,
    [property: JsonPropertyName("regressedTasks")] IReadOnlyList<LiveAgentOutcomeTaskDiff> RegressedTasks,
    [property: JsonPropertyName("unchangedTaskCount")] int UnchangedTaskCount,
    [property: JsonPropertyName("baselineOnlyTasks")] IReadOnlyList<string> BaselineOnlyTasks,
    [property: JsonPropertyName("candidateOnlyTasks")] IReadOnlyList<string> CandidateOnlyTasks,
    [property: JsonPropertyName("summary")] string Summary);
