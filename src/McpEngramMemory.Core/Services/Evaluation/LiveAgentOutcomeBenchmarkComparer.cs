using System.Text.Json;
using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// Loads and compares live agent-outcome benchmark artifacts.
/// </summary>
public static class LiveAgentOutcomeBenchmarkComparer
{
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] PreferredConditionOrder =
    [
        "no_memory",
        "transcript_replay",
        "vector_memory",
        "full_engram"
    ];

    public static LiveAgentOutcomeBenchmarkDiffReport CompareArtifacts(
        string baselineArtifactPath,
        string candidateArtifactPath)
    {
        if (string.IsNullOrWhiteSpace(baselineArtifactPath))
            throw new ArgumentException("Baseline artifact path is required.", nameof(baselineArtifactPath));
        if (string.IsNullOrWhiteSpace(candidateArtifactPath))
            throw new ArgumentException("Candidate artifact path is required.", nameof(candidateArtifactPath));

        string resolvedBaselinePath = Path.GetFullPath(baselineArtifactPath);
        string resolvedCandidatePath = Path.GetFullPath(candidateArtifactPath);

        if (!File.Exists(resolvedBaselinePath))
            throw new FileNotFoundException($"Baseline artifact not found: {resolvedBaselinePath}", resolvedBaselinePath);
        if (!File.Exists(resolvedCandidatePath))
            throw new FileNotFoundException($"Candidate artifact not found: {resolvedCandidatePath}", resolvedCandidatePath);

        var baseline = LoadArtifact(resolvedBaselinePath);
        var candidate = LoadArtifact(resolvedCandidatePath);
        return Compare(baseline, candidate, resolvedBaselinePath, resolvedCandidatePath);
    }

    public static LiveAgentOutcomeBenchmarkDiffReport Compare(
        LiveAgentOutcomeBenchmarkResult baseline,
        LiveAgentOutcomeBenchmarkResult candidate,
        string? baselineArtifactPath = null,
        string? candidateArtifactPath = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        if (!string.Equals(baseline.DatasetId, candidate.DatasetId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Artifacts target different datasets: '{baseline.DatasetId}' vs '{candidate.DatasetId}'.");
        }

        if (!string.Equals(baseline.BaselineCondition, candidate.BaselineCondition, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Artifacts use different baselines: '{baseline.BaselineCondition}' vs '{candidate.BaselineCondition}'.");
        }

        var baselineConditions = ToConditionMap(baseline);
        var candidateConditions = ToConditionMap(candidate);
        var sharedConditions = baselineConditions.Keys
            .Intersect(candidateConditions.Keys, StringComparer.Ordinal)
            .OrderBy(GetConditionOrder)
            .ToList();

        var conditionDiffs = new List<LiveAgentOutcomeConditionDiff>(sharedConditions.Count);
        var improvedTasks = new List<LiveAgentOutcomeTaskDiff>();
        var regressedTasks = new List<LiveAgentOutcomeTaskDiff>();
        var baselineOnlyTasks = new List<string>();
        var candidateOnlyTasks = new List<string>();
        int unchangedTaskCount = 0;

        foreach (string condition in sharedConditions)
        {
            var baselineCondition = baselineConditions[condition];
            var candidateCondition = candidateConditions[condition];
            conditionDiffs.Add(CompareCondition(baselineCondition, candidateCondition));

            var baselineTasks = baselineCondition.TaskResults.ToDictionary(t => t.TaskId, StringComparer.Ordinal);
            var candidateTasks = candidateCondition.TaskResults.ToDictionary(t => t.TaskId, StringComparer.Ordinal);

            foreach (string taskId in baselineTasks.Keys.Except(candidateTasks.Keys, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal))
                baselineOnlyTasks.Add($"{condition}:{taskId}");

            foreach (string taskId in candidateTasks.Keys.Except(baselineTasks.Keys, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal))
                candidateOnlyTasks.Add($"{condition}:{taskId}");

            foreach (string taskId in baselineTasks.Keys.Intersect(candidateTasks.Keys, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal))
            {
                var diff = CompareTask(condition, baselineTasks[taskId], candidateTasks[taskId]);
                switch (diff.ChangeType)
                {
                    case "improved":
                        improvedTasks.Add(diff);
                        break;
                    case "regressed":
                        regressedTasks.Add(diff);
                        break;
                    default:
                        unchangedTaskCount++;
                        break;
                }
            }
        }

        foreach (string condition in baselineConditions.Keys.Except(candidateConditions.Keys, StringComparer.Ordinal).OrderBy(GetConditionOrder))
            baselineOnlyTasks.AddRange(baselineConditions[condition].TaskResults.Select(t => $"{condition}:{t.TaskId}"));

        foreach (string condition in candidateConditions.Keys.Except(baselineConditions.Keys, StringComparer.Ordinal).OrderBy(GetConditionOrder))
            candidateOnlyTasks.AddRange(candidateConditions[condition].TaskResults.Select(t => $"{condition}:{t.TaskId}"));

        improvedTasks = improvedTasks
            .OrderByDescending(GetTaskImpact)
            .ThenBy(t => GetConditionOrder(t.Condition))
            .ThenBy(t => t.TaskId, StringComparer.Ordinal)
            .ToList();
        regressedTasks = regressedTasks
            .OrderByDescending(GetTaskImpact)
            .ThenBy(t => GetConditionOrder(t.Condition))
            .ThenBy(t => t.TaskId, StringComparer.Ordinal)
            .ToList();

        string summary = BuildSummary(candidate, conditionDiffs, improvedTasks.Count, regressedTasks.Count, unchangedTaskCount);

        return new LiveAgentOutcomeBenchmarkDiffReport(
            baseline.DatasetId,
            new LiveAgentOutcomeArtifactReference(
                baselineArtifactPath,
                baseline.DatasetId,
                baseline.RunAt,
                baseline.Provider,
                baseline.Model,
                baseline.Endpoint),
            new LiveAgentOutcomeArtifactReference(
                candidateArtifactPath,
                candidate.DatasetId,
                candidate.RunAt,
                candidate.Provider,
                candidate.Model,
                candidate.Endpoint),
            conditionDiffs,
            improvedTasks,
            regressedTasks,
            unchangedTaskCount,
            baselineOnlyTasks,
            candidateOnlyTasks,
            summary);
    }

    private static LiveAgentOutcomeBenchmarkResult LoadArtifact(string path)
    {
        var result = JsonSerializer.Deserialize<LiveAgentOutcomeBenchmarkResult>(File.ReadAllText(path), ArtifactJsonOptions);
        return result ?? throw new InvalidDataException($"Artifact '{path}' does not contain a valid live benchmark result.");
    }

    private static Dictionary<string, LiveAgentOutcomeConditionResult> ToConditionMap(LiveAgentOutcomeBenchmarkResult result)
    {
        var map = new Dictionary<string, LiveAgentOutcomeConditionResult>(StringComparer.Ordinal)
        {
            [result.Baseline.Condition] = result.Baseline
        };

        foreach (var comparison in result.Comparisons)
            map[comparison.Condition] = comparison.Result;

        return map;
    }

    private static LiveAgentOutcomeConditionDiff CompareCondition(
        LiveAgentOutcomeConditionResult baseline,
        LiveAgentOutcomeConditionResult candidate)
        => new(
            baseline.Condition,
            baseline.PassRate,
            candidate.PassRate,
            candidate.PassRate - baseline.PassRate,
            baseline.MeanSuccessScore,
            candidate.MeanSuccessScore,
            candidate.MeanSuccessScore - baseline.MeanSuccessScore,
            baseline.MeanRequiredCoverage,
            candidate.MeanRequiredCoverage,
            candidate.MeanRequiredCoverage - baseline.MeanRequiredCoverage,
            baseline.MeanConflictRate,
            candidate.MeanConflictRate,
            candidate.MeanConflictRate - baseline.MeanConflictRate,
            baseline.MeanLatencyMs,
            candidate.MeanLatencyMs,
            candidate.MeanLatencyMs - baseline.MeanLatencyMs,
            baseline.FormatValidityRate,
            candidate.FormatValidityRate,
            candidate.FormatValidityRate - baseline.FormatValidityRate);

    private static LiveAgentOutcomeTaskDiff CompareTask(
        string condition,
        LiveAgentOutcomeTaskResult baseline,
        LiveAgentOutcomeTaskResult candidate)
    {
        float successDelta = candidate.SuccessScore - baseline.SuccessScore;
        float requiredCoverageDelta = candidate.RequiredCoverage - baseline.RequiredCoverage;
        bool improved = successDelta > 0.0001f
            || (!baseline.Passed && candidate.Passed)
            || requiredCoverageDelta > 0.0001f
            || (!baseline.ResponseFormatValid && candidate.ResponseFormatValid);
        bool regressed = successDelta < -0.0001f
            || (baseline.Passed && !candidate.Passed)
            || requiredCoverageDelta < -0.0001f
            || (baseline.ResponseFormatValid && !candidate.ResponseFormatValid);

        string changeType = improved && !regressed
            ? "improved"
            : regressed && !improved
                ? "regressed"
                : "unchanged";

        var baselineEvidence = baseline.CitedMemoryIds.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var candidateEvidence = candidate.CitedMemoryIds.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var addedEvidence = candidateEvidence.Except(baselineEvidence, StringComparer.Ordinal).ToArray();
        var removedEvidence = baselineEvidence.Except(candidateEvidence, StringComparer.Ordinal).ToArray();

        return new LiveAgentOutcomeTaskDiff(
            condition,
            baseline.TaskId,
            changeType,
            baseline.Passed,
            candidate.Passed,
            baseline.SuccessScore,
            candidate.SuccessScore,
            successDelta,
            baseline.RequiredCoverage,
            candidate.RequiredCoverage,
            requiredCoverageDelta,
            baseline.ResponseFormatValid,
            candidate.ResponseFormatValid,
            baselineEvidence,
            candidateEvidence,
            addedEvidence,
            removedEvidence);
    }

    private static double GetTaskImpact(LiveAgentOutcomeTaskDiff diff)
        => Math.Abs(diff.SuccessDelta)
            + Math.Abs(diff.RequiredCoverageDelta)
            + (diff.BaselinePassed == diff.CandidatePassed ? 0.0 : 1.0)
            + (diff.BaselineResponseFormatValid == diff.CandidateResponseFormatValid ? 0.0 : 0.5);

    private static int GetConditionOrder(string condition)
    {
        int index = Array.IndexOf(PreferredConditionOrder, condition);
        return index >= 0 ? index : int.MaxValue;
    }

    private static string BuildSummary(
        LiveAgentOutcomeBenchmarkResult candidate,
        IReadOnlyList<LiveAgentOutcomeConditionDiff> conditionDiffs,
        int improvedCount,
        int regressedCount,
        int unchangedTaskCount)
    {
        var fullEngramDiff = conditionDiffs.FirstOrDefault(diff => string.Equals(diff.Condition, "full_engram", StringComparison.Ordinal));
        if (fullEngramDiff is null)
        {
            return $"Compared {candidate.Model} on {candidate.DatasetId}: {improvedCount} improved tasks, {regressedCount} regressed tasks, {unchangedTaskCount} unchanged.";
        }

        return
            $"Compared {candidate.Model} on {candidate.DatasetId}: {improvedCount} improved tasks, {regressedCount} regressed tasks, {unchangedTaskCount} unchanged. " +
            $"full_engram success delta {fullEngramDiff.SuccessDelta:+0.###;-0.###;0.000}, pass-rate delta {fullEngramDiff.PassRateDelta:+0.###;-0.###;0.000}.";
    }
}
