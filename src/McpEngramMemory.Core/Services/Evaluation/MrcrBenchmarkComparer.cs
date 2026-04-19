using System.Text;
using System.Text.Json;
using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// Loads two MRCR artifact files and emits a structured diff: similarity and pass-rate deltas
/// for each arm, plus the change in the engram arm's prompt-token reduction ratio.
/// </summary>
public static class MrcrBenchmarkComparer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static MrcrBenchmarkDiffReport CompareArtifacts(string baselinePath, string candidatePath)
    {
        var baseline = LoadArtifact(baselinePath);
        var candidate = LoadArtifact(candidatePath);

        if (!string.Equals(baseline.DatasetId, candidate.DatasetId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Dataset mismatch: baseline '{baseline.DatasetId}' vs candidate '{candidate.DatasetId}'.");

        float fullSimDelta = (candidate.FullContext?.MeanSimilarity ?? 0f) - (baseline.FullContext?.MeanSimilarity ?? 0f);
        float engramSimDelta = (candidate.EngramRetrieval?.MeanSimilarity ?? 0f) - (baseline.EngramRetrieval?.MeanSimilarity ?? 0f);
        float fullPassDelta = (candidate.FullContext?.PassRate ?? 0f) - (baseline.FullContext?.PassRate ?? 0f);
        float engramPassDelta = (candidate.EngramRetrieval?.PassRate ?? 0f) - (baseline.EngramRetrieval?.PassRate ?? 0f);
        float reductionDelta = candidate.PromptTokenReductionRatio - baseline.PromptTokenReductionRatio;

        var summary = new StringBuilder();
        summary.Append("full_context Δsim=").Append(fullSimDelta.ToString("F3"));
        summary.Append(", engram Δsim=").Append(engramSimDelta.ToString("F3"));
        summary.Append(", Δtoken-reduction=").Append(reductionDelta.ToString("P1"));

        return new MrcrBenchmarkDiffReport(
            baseline.DatasetId,
            ToReference(baselinePath, baseline),
            ToReference(candidatePath, candidate),
            fullSimDelta,
            engramSimDelta,
            fullPassDelta,
            engramPassDelta,
            reductionDelta,
            summary.ToString());
    }

    private static MrcrBenchmarkResult LoadArtifact(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"MRCR artifact not found: '{path}'.", path);

        var json = File.ReadAllText(path);
        var result = JsonSerializer.Deserialize<MrcrBenchmarkResult>(json, JsonOptions)
            ?? throw new InvalidDataException($"Failed to deserialize MRCR artifact: '{path}'.");
        return result;
    }

    private static MrcrArtifactReference ToReference(string path, MrcrBenchmarkResult result)
        => new(path, result.DatasetId, result.RunAt, result.Provider, result.Model, result.Endpoint);
}
