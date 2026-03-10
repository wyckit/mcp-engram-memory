using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Retrieval;

/// <summary>
/// Namespace-scoped k-nearest-neighbor vector search with two-stage Int8 screening pipeline.
/// Stateless — operates on data passed to it; caller manages locking.
/// </summary>
public sealed class VectorSearchEngine
{
    /// <summary>Minimum namespace size to activate two-stage Int8 screening.</summary>
    private const int TwoStageThreshold = 30;

    /// <summary>Candidate pool multiplier for Int8 screening pass.</summary>
    private const int ScreeningMultiplier = 5;

    /// <summary>
    /// Search a namespace using cosine similarity with optional two-stage Int8 screening.
    /// </summary>
    /// <param name="query">Query vector.</param>
    /// <param name="entries">Snapshot of namespace entries to search.</param>
    /// <param name="k">Max results to return.</param>
    /// <param name="minScore">Minimum cosine similarity threshold.</param>
    /// <param name="category">Optional category filter.</param>
    /// <param name="includeStates">Lifecycle states to include.</param>
    /// <param name="summaryFirst">Prioritize summary nodes.</param>
    public IReadOnlyList<CognitiveSearchResult> Search(
        float[] query,
        IReadOnlyCollection<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> entries,
        int k = 5,
        float minScore = 0f,
        string? category = null,
        HashSet<string>? includeStates = null,
        bool summaryFirst = false)
    {
        if (query is null || query.Length == 0)
            throw new ArgumentException("Query vector must not be null or empty.", nameof(query));
        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k), "k must be positive.");

        includeStates ??= new HashSet<string> { "stm", "ltm" };
        float queryNorm = VectorMath.Norm(query);
        if (queryNorm == 0f)
            throw new ArgumentException("Query vector must not be zero-magnitude.", nameof(query));

        bool useTwoStage = entries.Count >= TwoStageThreshold;
        var exactScored = new List<(CognitiveEntry entry, float score)>(entries.Count);
        List<(CognitiveEntry entry, float approxScore, float norm)>? quantizedCandidates = null;
        QuantizedVector? queryQuantized = null;

        if (useTwoStage)
            quantizedCandidates = new(entries.Count);

        foreach (var (entry, entryNorm, quantized) in entries)
        {
            if (!includeStates.Contains(entry.LifecycleState))
                continue;
            if (category is not null && entry.Category != category)
                continue;
            if (entry.Vector.Length != query.Length)
                continue;
            if (entryNorm == 0f)
                continue;

            if (useTwoStage && quantized is not null)
            {
                queryQuantized ??= VectorQuantizer.Quantize(query);
                float approxScore = VectorQuantizer.ApproximateCosine(queryQuantized, quantized);
                quantizedCandidates!.Add((entry, approxScore, entryNorm));
            }
            else
            {
                float dot = VectorMath.Dot(query, entry.Vector);
                float score = dot / (queryNorm * entryNorm);
                if (score >= minScore)
                    exactScored.Add((entry, score));
            }
        }

        // Stage 2: Rerank top quantized candidates with exact FP32
        if (quantizedCandidates is { Count: > 0 })
        {
            quantizedCandidates.Sort((a, b) => b.approxScore.CompareTo(a.approxScore));
            int rerankCount = Math.Min(k * ScreeningMultiplier, quantizedCandidates.Count);

            for (int i = 0; i < rerankCount; i++)
            {
                var (entry, _, entryNorm) = quantizedCandidates[i];
                float dot = VectorMath.Dot(query, entry.Vector);
                float exactScore = dot / (queryNorm * entryNorm);
                if (exactScore >= minScore)
                    exactScored.Add((entry, exactScore));
            }
        }

        // Sort all exact scores
        if (summaryFirst)
        {
            exactScored.Sort((a, b) =>
            {
                if (a.entry.IsSummaryNode != b.entry.IsSummaryNode)
                    return a.entry.IsSummaryNode ? -1 : 1;
                return b.score.CompareTo(a.score);
            });
        }
        else
        {
            exactScored.Sort((a, b) => b.score.CompareTo(a.score));
        }

        int take = Math.Min(k, exactScored.Count);
        var results = new CognitiveSearchResult[take];
        for (int i = 0; i < take; i++)
        {
            var e = exactScored[i].entry;
            results[i] = new CognitiveSearchResult(
                e.Id, e.Text, exactScored[i].score, e.LifecycleState,
                e.ActivationEnergy, e.Category, e.Metadata,
                e.IsSummaryNode, e.SourceClusterId, e.AccessCount);
        }
        return results;
    }
}
