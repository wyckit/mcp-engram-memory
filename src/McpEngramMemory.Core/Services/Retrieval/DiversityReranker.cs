using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Retrieval;

/// <summary>
/// Cluster-aware Maximal Marginal Relevance (MMR) reranker that balances semantic relevance
/// with result diversity. Prevents top-K results from clustering around a single sub-topic
/// when broad queries hit semantically dense namespaces.
/// </summary>
public static class DiversityReranker
{
    /// <summary>Default trade-off between relevance and diversity. 0.5 = equal weight.</summary>
    public const float DefaultLambda = 0.5f;

    /// <summary>Penalty applied when a candidate shares a cluster with an already-selected result.</summary>
    private const float ClusterPenalty = 0.15f;

    /// <summary>Penalty applied when a candidate shares a category with an already-selected result.</summary>
    private const float CategoryPenalty = 0.05f;

    /// <summary>
    /// Apply MMR-style diversity reranking to search results.
    /// Greedily selects results that maximize: λ·relevance − (1−λ)·redundancy − clusterPenalty.
    /// </summary>
    /// <param name="results">Candidate results sorted by relevance.</param>
    /// <param name="queryVector">Query embedding vector for relevance scoring.</param>
    /// <param name="entryVectors">Lookup from entry ID to its embedding vector.</param>
    /// <param name="k">Number of results to return.</param>
    /// <param name="lambda">Trade-off: 1.0 = pure relevance, 0.0 = pure diversity.</param>
    public static IReadOnlyList<CognitiveSearchResult> Rerank(
        IReadOnlyList<CognitiveSearchResult> results,
        float[] queryVector,
        Func<string, float[]?> entryVectors,
        int k,
        float lambda = DefaultLambda)
    {
        if (results.Count <= 1 || k <= 1)
            return results.Count > k ? results.Take(k).ToList() : results;

        // Precompute norms and vectors for candidates
        float queryNorm = VectorMath.Norm(queryVector);
        var candidates = new List<CandidateInfo>(results.Count);
        foreach (var r in results)
        {
            var vec = entryVectors(r.Id);
            if (vec is null) continue;
            candidates.Add(new CandidateInfo(r, vec, VectorMath.Norm(vec)));
        }

        if (candidates.Count == 0)
            return Array.Empty<CognitiveSearchResult>();

        var selected = new List<CognitiveSearchResult>(Math.Min(k, candidates.Count));
        var selectedVectors = new List<(float[] Vector, float Norm)>(k);
        var selectedClusters = new HashSet<string>();
        var selectedCategories = new HashSet<string>();
        var used = new HashSet<int>();

        // Greedy MMR selection
        for (int pick = 0; pick < k && used.Count < candidates.Count; pick++)
        {
            int bestIdx = -1;
            float bestScore = float.MinValue;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (used.Contains(i)) continue;

                var c = candidates[i];

                // Relevance: cosine similarity to query (already computed as r.Score,
                // but we use it directly to stay consistent with the MMR formula)
                float relevance = c.Result.Score;

                // Redundancy: max cosine similarity to any already-selected result
                float redundancy = 0f;
                foreach (var (selVec, selNorm) in selectedVectors)
                {
                    if (c.Norm == 0f || selNorm == 0f) continue;
                    float sim = VectorMath.Dot(c.Vector, selVec) / (c.Norm * selNorm);
                    if (sim > redundancy) redundancy = sim;
                }

                // Cluster/category penalties for same-group results
                float groupPenalty = 0f;
                if (c.Result.SourceClusterId is not null && selectedClusters.Contains(c.Result.SourceClusterId))
                    groupPenalty += ClusterPenalty;
                if (c.Result.Category is not null && selectedCategories.Contains(c.Result.Category))
                    groupPenalty += CategoryPenalty;

                float mmrScore = lambda * relevance - (1f - lambda) * redundancy - groupPenalty;

                if (mmrScore > bestScore)
                {
                    bestScore = mmrScore;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0) break;

            var winner = candidates[bestIdx];
            selected.Add(winner.Result);
            selectedVectors.Add((winner.Vector, winner.Norm));
            used.Add(bestIdx);

            if (winner.Result.SourceClusterId is not null)
                selectedClusters.Add(winner.Result.SourceClusterId);
            if (winner.Result.Category is not null)
                selectedCategories.Add(winner.Result.Category);
        }

        return selected;
    }

    private readonly record struct CandidateInfo(
        CognitiveSearchResult Result,
        float[] Vector,
        float Norm);
}
