using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Retrieval;

namespace McpEngramMemory.Tools;

/// <summary>Relevance calibration for a bounded broad-query candidate pool.</summary>
internal static class BroadRecallRanking
{
    private static readonly HashSet<string> ContextTerms = Tokens("production staging development testing overview summary");

    public static bool IsTopicQuery(string query)
    {
        string first = query.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
        return first is not ("who" or "what" or "which" or "when" or "where" or "why" or "how" or
            "is" or "are" or "was" or "were" or "do" or "does" or "did" or "can" or "could" or "should" or "would");
    }

    public static int CandidateLimit(int k) => k + Math.Min(Math.Max(k, 15), Math.Min(32, int.MaxValue - k));

    public static bool HasTopicEvidence(string query, IReadOnlyList<CognitiveSearchResult> candidates)
    {
        var sources = candidates.Where(c => !c.IsSummaryNode && !RecallFreshness.IsSuperseded(c.Text))
            .DistinctBy(c => c.Text, StringComparer.Ordinal).Select(c => Tokens(c.Text ?? "")).ToList();
        // A name shared by the namespace or a word found only in an overview
        // cannot distinguish source topics. Keep the existing overview selection.
        return Tokens(query).Except(ContextTerms).Any(term =>
        {
            int count = sources.Count(tokens => tokens.Contains(term));
            return count > 0 && count * 2 <= sources.Count;
        });
    }

    public static IReadOnlyList<(T Candidate, float Score)> Rank<T>(string query, float[] vector,
        IReadOnlyList<T> candidates, Func<T, CognitiveSearchResult> getResult, Func<T, float[]?> getVector)
    {
        var terms = Tokens(query);
        var tokens = candidates.Select(c => Tokens(getResult(c).Text ?? "")).ToArray();
        // Repeated copies are not independent evidence that a query term is common.
        var documents = candidates.Select((c, i) => (Text: getResult(c).Text, Tokens: tokens[i]))
            .DistinctBy(c => c.Text, StringComparer.Ordinal).Select(c => c.Tokens).ToList();
        var weights = terms.ToDictionary(term => term,
            term => 1f + MathF.Log((documents.Count + 1f) / (documents.Count(d => d.Contains(term)) + 1f)));
        float total = weights.Values.Sum();
        float norm = VectorMath.Norm(vector);
        var output = new List<(T Candidate, float Score)>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var embedding = getVector(candidate);
            if (embedding is null || embedding.Length != vector.Length) continue;
            float cosine = VectorMath.Dot(vector, embedding) / (norm * VectorMath.Norm(embedding) + 1e-9f);
            float coverage = total > 0 ? weights.Where(t => tokens[i].Contains(t.Key)).Sum(t => t.Value) / total : 0;
            // Use the original query, without pseudo-feedback or domain expansions.
            // A project name appearing throughout the pool must not drown its topic.
            output.Add((candidate, total > 0 ? .6f * cosine + .4f * coverage : cosine));
        }
        return output.OrderByDescending(c => c.Score).ToList();
    }

    // Complete the common inflection pairs left by the index's deliberately
    // partial stemmer, locally to broad coverage: cache/caching, retry/retries,
    // retrieval/retrieved. Do not change indexed terms or raw search semantics.
    internal static HashSet<string> Tokens(string text) => BM25Index.Tokenize(text).Select(term =>
    {
        if (term.Length > 5 && term.EndsWith("al", StringComparison.Ordinal)) term = term[..^2];
        if (term.Length > 3 && term.EndsWith('e')) term = term[..^1];
        if (term.Length > 3 && term.EndsWith('y')) term = term[..^1] + "i";
        return term;
    }).ToHashSet(StringComparer.Ordinal);
}
