using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Retrieval;

/// <summary>
/// Lightweight reranker that re-scores top-K search results using token-level analysis.
/// Combines cosine similarity with term overlap, exact match boosting, and position awareness
/// to improve precision without requiring a cross-encoder model.
/// </summary>
public sealed class TokenReranker : IReranker
{
    // Weight for blending cosine score with reranker score
    private const float CosineWeight = 0.6f;
    private const float RerankWeight = 0.4f;

    /// <summary>
    /// Re-rank search results by blending cosine similarity with a token-overlap score.
    /// </summary>
    public IReadOnlyList<CognitiveSearchResult> Rerank(
        string queryText,
        IReadOnlyList<CognitiveSearchResult> results)
    {
        if (results.Count <= 1 || string.IsNullOrWhiteSpace(queryText))
            return results;

        var queryTokens = BM25Index.Tokenize(queryText);
        if (queryTokens.Length == 0) return results;

        var querySet = queryTokens.ToHashSet();
        var queryBigrams = BuildBigrams(queryTokens);

        var scored = new List<(CognitiveSearchResult Result, float FinalScore)>(results.Count);

        foreach (var r in results)
        {
            float rerankScore = ComputeRerankScore(r.Text, queryTokens, querySet, queryBigrams);
            float finalScore = CosineWeight * r.Score + RerankWeight * rerankScore;
            scored.Add((r, finalScore));
        }

        scored.Sort((a, b) => b.FinalScore.CompareTo(a.FinalScore));

        return scored.Select(s => new CognitiveSearchResult(
            s.Result.Id, s.Result.Text, s.FinalScore,
            s.Result.LifecycleState, s.Result.ActivationEnergy,
            s.Result.Category, s.Result.Metadata,
            s.Result.IsSummaryNode, s.Result.SourceClusterId,
            s.Result.AccessCount)).ToList();
    }

    private static float ComputeRerankScore(
        string? docText,
        string[] queryTokens,
        HashSet<string> querySet,
        HashSet<string> queryBigrams)
    {
        if (string.IsNullOrWhiteSpace(docText)) return 0f;

        var docTokens = BM25Index.Tokenize(docText);
        if (docTokens.Length == 0) return 0f;

        var docSet = docTokens.ToHashSet();

        // 1. Unigram overlap (Jaccard-like)
        int unigramOverlap = 0;
        foreach (var qt in querySet)
        {
            if (docSet.Contains(qt))
                unigramOverlap++;
        }
        float unigramScore = (float)unigramOverlap / querySet.Count;

        // 2. Bigram overlap for phrase matching
        float bigramScore = 0f;
        if (queryBigrams.Count > 0)
        {
            var docBigrams = BuildBigrams(docTokens);
            int bigramOverlap = 0;
            foreach (var qb in queryBigrams)
            {
                if (docBigrams.Contains(qb))
                    bigramOverlap++;
            }
            bigramScore = (float)bigramOverlap / queryBigrams.Count;
        }

        // 3. Coverage: fraction of query terms found in doc
        float coverage = (float)unigramOverlap / queryTokens.Length;

        // 4. Early mention bonus: if query terms appear in first 20% of doc tokens
        int earlyWindow = Math.Max(docTokens.Length / 5, 5);
        int earlyHits = 0;
        for (int i = 0; i < Math.Min(earlyWindow, docTokens.Length); i++)
        {
            if (querySet.Contains(docTokens[i]))
                earlyHits++;
        }
        float earlyBonus = (float)earlyHits / queryTokens.Length;

        // Weighted combination
        return 0.40f * unigramScore
             + 0.25f * bigramScore
             + 0.20f * coverage
             + 0.15f * earlyBonus;
    }

    private static HashSet<string> BuildBigrams(string[] tokens)
    {
        var bigrams = new HashSet<string>();
        for (int i = 0; i < tokens.Length - 1; i++)
            bigrams.Add(tokens[i] + " " + tokens[i + 1]);
        return bigrams;
    }
}
