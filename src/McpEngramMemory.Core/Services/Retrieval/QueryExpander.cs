namespace McpEngramMemory.Core.Services.Retrieval;

/// <summary>
/// Pseudo-relevance feedback (PRF) query expansion.
/// Extracts key terms from initial top results and appends them to the query
/// to improve recall on a second-pass search.
/// </summary>
public sealed class QueryExpander
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did", "will", "would", "could",
        "should", "may", "might", "shall", "can", "need", "dare", "ought",
        "used", "to", "of", "in", "for", "on", "with", "at", "by", "from",
        "as", "into", "through", "during", "before", "after", "above", "below",
        "between", "out", "off", "over", "under", "again", "further", "then",
        "once", "here", "there", "when", "where", "why", "how", "all", "both",
        "each", "few", "more", "most", "other", "some", "such", "no", "nor",
        "not", "only", "own", "same", "so", "than", "too", "very", "just",
        "because", "but", "and", "or", "if", "while", "about", "up", "it",
        "its", "this", "that", "these", "those", "i", "me", "my", "we", "our",
        "you", "your", "he", "him", "his", "she", "her", "they", "them", "their",
        "what", "which", "who", "whom"
    };

    /// <summary>
    /// Expand a query using terms extracted from pseudo-relevant documents.
    /// Returns the original query with top expansion terms appended.
    /// </summary>
    /// <param name="originalQuery">The original search query text.</param>
    /// <param name="topResults">Top results from an initial search pass.</param>
    /// <param name="maxTerms">Maximum number of expansion terms to add (default: 5).</param>
    /// <param name="minDocFreq">Minimum number of top docs a term must appear in (default: 2).</param>
    public string Expand(
        string originalQuery,
        IReadOnlyList<Models.CognitiveSearchResult> topResults,
        int maxTerms = 5,
        int minDocFreq = 2)
    {
        if (topResults.Count < minDocFreq || string.IsNullOrWhiteSpace(originalQuery))
            return originalQuery;

        var queryTerms = new HashSet<string>(TokenizeRaw(originalQuery), StringComparer.OrdinalIgnoreCase);

        // Count term frequency across top documents
        var termDocFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var termTotalFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in topResults.Take(5))
        {
            if (string.IsNullOrWhiteSpace(result.Text)) continue;

            var docTokens = TokenizeRaw(result.Text);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var term in docTokens)
            {
                if (queryTerms.Contains(term)) continue;
                if (StopWords.Contains(term)) continue;
                if (term.Length < 3) continue;

                // Count doc frequency (unique per doc)
                if (seen.Add(term))
                {
                    termDocFreq.TryGetValue(term, out int docFreq);
                    termDocFreq[term] = docFreq + 1;
                }

                // Count total frequency (all occurrences)
                termTotalFreq.TryGetValue(term, out int freq);
                termTotalFreq[term] = freq + 1;
            }
        }

        // Score terms: prefer terms appearing in multiple docs with higher total frequency
        var expansionTerms = termDocFreq
            .Where(kv => kv.Value >= minDocFreq)
            .OrderByDescending(kv => kv.Value * 10 + termTotalFreq.GetValueOrDefault(kv.Key, 0))
            .Take(maxTerms)
            .Select(kv => kv.Key)
            .ToList();

        if (expansionTerms.Count == 0)
            return originalQuery;

        return originalQuery + " " + string.Join(" ", expansionTerms);
    }

    private static List<string> TokenizeRaw(string text)
    {
        var tokens = new List<string>();
        int start = -1;

        for (int i = 0; i <= text.Length; i++)
        {
            bool isLetterOrDigit = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '-' || text[i] == '_');

            if (isLetterOrDigit && start < 0)
            {
                start = i;
            }
            else if (!isLetterOrDigit && start >= 0)
            {
                var token = text[start..i].ToLowerInvariant().Trim('-', '_');
                if (token.Length >= 2)
                    tokens.Add(token);
                start = -1;
            }
        }

        return tokens;
    }
}
