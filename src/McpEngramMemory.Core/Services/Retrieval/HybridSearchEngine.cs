using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Retrieval;

/// <summary>
/// Hybrid search combining vector cosine similarity with BM25 keyword matching,
/// fused via Reciprocal Rank Fusion (RRF). Stateless — caller manages locking.
/// </summary>
public sealed class HybridSearchEngine
{
    /// <summary>
    /// Execute a hybrid search combining vector and BM25 results via RRF.
    /// </summary>
    /// <param name="vectorResults">Pre-computed vector search results (broad candidate set).</param>
    /// <param name="queryText">Original query text for BM25.</param>
    /// <param name="ns">Namespace to search.</param>
    /// <param name="k">Max results to return.</param>
    /// <param name="includeStates">Lifecycle states filter.</param>
    /// <param name="category">Category filter.</param>
    /// <param name="rerank">Whether to apply token reranking.</param>
    /// <param name="rrfK">RRF constant (default 60).</param>
    /// <param name="bm25">BM25 index for keyword search.</param>
    /// <param name="reranker">Token reranker.</param>
    /// <param name="getEntry">Delegate to resolve entry by (id, ns) — used for BM25-only results.</param>
    /// <summary>
    /// Threshold above which vector results are considered high-confidence,
    /// allowing the search to skip BM25 fusion for better P95 latency.
    /// </summary>
    private const float HighConfidenceThreshold = 0.85f;
    private const float LowConfidenceThreshold = 0.50f;
    private const int CascadeThreshold = 50;

    public IReadOnlyList<CognitiveSearchResult> HybridSearch(
        IReadOnlyList<CognitiveSearchResult> vectorResults,
        string queryText,
        string ns,
        int k,
        HashSet<string>? includeStates,
        string? category,
        bool rerank,
        int rrfK,
        BM25Index bm25,
        IReranker reranker,
        Func<string, string, CognitiveEntry?> getEntry,
        int entryCount = 0)
    {
        // High-confidence early exit: if vector search returned strong results,
        // skip BM25 fusion to reduce P95 latency. BM25 mainly helps when
        // vector search struggles (semantic gap / keyword mismatch).
        if (vectorResults.Count >= k &&
            vectorResults[0].Score >= HighConfidenceThreshold)
        {
            var highConf = vectorResults.Take(rerank ? k * 2 : k).ToList();
            if (rerank && highConf.Count > 0)
                highConf = reranker.Rerank(queryText, highConf).Take(k).ToList();
            else if (highConf.Count > k)
                highConf.RemoveRange(k, highConf.Count - k);
            return highConf;
        }

        // Adaptive RRF: modulate BM25 influence based on vector confidence.
        // High vector confidence → increase rrfK to suppress BM25 noise.
        // Low vector confidence → decrease rrfK to amplify BM25 rescue.
        int adaptiveRrfK = rrfK;
        if (vectorResults.Count > 0)
        {
            float topScore = vectorResults[0].Score;
            if (topScore >= 0.70f)
                adaptiveRrfK = Math.Max(rrfK, 120); // suppress BM25
            else if (topScore < LowConfidenceThreshold)
                adaptiveRrfK = Math.Min(rrfK, 30); // amplify BM25
        }

        // Cascade mode: for large namespaces, use BM25 as a precision booster
        // instead of parallel fusion, to avoid BM25 noise diluting vector results.
        if (entryCount >= CascadeThreshold)
        {
            // Get BM25 scores for vector results only (no new candidates)
            var vectorIds = vectorResults.Select(r => r.Id).ToHashSet();
            var bm25Scores = bm25.Search(queryText, ns, vectorResults.Count * 2, vectorIds);
            var bm25ScoreLookup = bm25Scores.ToDictionary(x => x.Id, x => x.Score);

            // Boost vector results that also score well in BM25
            float maxBm25 = bm25Scores.Count > 0 ? bm25Scores.Max(x => x.Score) : 1f;
            if (maxBm25 <= 0f) maxBm25 = 1f;

            var boosted = new List<CognitiveSearchResult>(vectorResults.Count);
            foreach (var vr in vectorResults)
            {
                float boost = 1f;
                if (bm25ScoreLookup.TryGetValue(vr.Id, out float bm25Score))
                    boost = 1f + 0.15f * (bm25Score / maxBm25); // Up to 15% boost

                boosted.Add(new CognitiveSearchResult(
                    vr.Id, vr.Text, vr.Score * boost,
                    vr.LifecycleState, vr.ActivationEnergy,
                    vr.Category, vr.Metadata,
                    vr.IsSummaryNode, vr.SourceClusterId, vr.AccessCount));
            }

            boosted.Sort((a, b) => b.Score.CompareTo(a.Score));

            if (rerank && boosted.Count > 0)
                boosted = reranker.Rerank(queryText, boosted).Take(k).ToList();
            else if (boosted.Count > k)
                boosted.RemoveRange(k, boosted.Count - k);

            return boosted;
        }

        // Build set of eligible IDs from vector results
        var eligibleIds = vectorResults.Select(r => r.Id).ToHashSet();

        // BM25 search
        int candidateK = Math.Max(k * 4, 20);
        var bm25Unfiltered = bm25.Search(queryText, ns, candidateK);

        // Add BM25-only results that pass filters, caching resolved entries
        var states = includeStates ?? new HashSet<string> { "stm", "ltm" };
        var resolvedEntries = new Dictionary<string, CognitiveEntry>();
        foreach (var (id, _) in bm25Unfiltered)
        {
            if (!eligibleIds.Contains(id))
            {
                var entry = getEntry(id, ns);
                if (entry is not null &&
                    states.Contains(entry.LifecycleState) &&
                    (category is null || entry.Category == category))
                {
                    eligibleIds.Add(id);
                    resolvedEntries[id] = entry;
                }
            }
        }

        // Reciprocal Rank Fusion
        var vectorRanks = new Dictionary<string, int>(vectorResults.Count);
        for (int i = 0; i < vectorResults.Count; i++)
            vectorRanks[vectorResults[i].Id] = i + 1;

        var bm25Ranks = new Dictionary<string, int>(bm25Unfiltered.Count);
        for (int i = 0; i < bm25Unfiltered.Count; i++)
            bm25Ranks[bm25Unfiltered[i].Id] = i + 1;

        // Merge unique IDs from both sources
        var allIds = new HashSet<string>(vectorRanks.Keys);
        foreach (var key in bm25Ranks.Keys)
            allIds.Add(key);

        var rrfScores = new List<(string Id, float RrfScore)>(allIds.Count);
        foreach (var id in allIds)
        {
            float score = 0f;
            if (vectorRanks.TryGetValue(id, out int vRank))
                score += 1f / (adaptiveRrfK + vRank);
            if (bm25Ranks.TryGetValue(id, out int bRank))
                score += 1f / (adaptiveRrfK + bRank);
            rrfScores.Add((id, score));
        }

        rrfScores.Sort((a, b) => b.RrfScore.CompareTo(a.RrfScore));

        // Build result objects with RRF scores
        var vectorLookup = vectorResults.ToDictionary(r => r.Id);
        int takeCount = rerank ? k * 2 : k;
        var results = new List<CognitiveSearchResult>(Math.Min(takeCount, rrfScores.Count));

        foreach (var (id, rrfScore) in rrfScores)
        {
            if (results.Count >= takeCount) break;

            if (vectorLookup.TryGetValue(id, out var vr))
            {
                results.Add(new CognitiveSearchResult(
                    vr.Id, vr.Text, rrfScore,
                    vr.LifecycleState, vr.ActivationEnergy,
                    vr.Category, vr.Metadata,
                    vr.IsSummaryNode, vr.SourceClusterId, vr.AccessCount));
            }
            else
            {
                // Use cached entry from filter loop, or resolve if needed
                if (!resolvedEntries.TryGetValue(id, out var entry))
                    entry = getEntry(id, ns);
                if (entry is not null)
                {
                    results.Add(new CognitiveSearchResult(
                        entry.Id, entry.Text, rrfScore,
                        entry.LifecycleState, entry.ActivationEnergy,
                        entry.Category, entry.Metadata,
                        entry.IsSummaryNode, entry.SourceClusterId, entry.AccessCount));
                }
            }
        }

        // Optional reranking
        if (rerank && results.Count > 0)
        {
            results = reranker.Rerank(queryText, results).Take(k).ToList();
        }
        else if (results.Count > k)
        {
            results.RemoveRange(k, results.Count - k);
        }

        return results;
    }
}
