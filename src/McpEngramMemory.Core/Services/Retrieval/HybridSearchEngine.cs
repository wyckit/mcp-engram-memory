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
        Func<string, string, CognitiveEntry?> getEntry)
    {
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
                score += 1f / (rrfK + vRank);
            if (bm25Ranks.TryGetValue(id, out int bRank))
                score += 1f / (rrfK + bRank);
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
