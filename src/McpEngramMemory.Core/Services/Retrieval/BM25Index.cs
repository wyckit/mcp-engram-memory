using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Retrieval;

/// <summary>
/// BM25 keyword index for hybrid search alongside vector similarity.
/// Maintains an inverted index per namespace with TF-IDF-like scoring.
/// Thread-safety: callers must hold appropriate locks on the CognitiveIndex.
/// </summary>
public sealed class BM25Index
{
    private readonly Dictionary<string, NamespaceIndex> _namespaces = new();

    // BM25 parameters (standard defaults)
    private const float K1 = 1.2f;
    private const float B = 0.75f;

    /// <summary>Index or re-index an entry's text.</summary>
    public void Index(CognitiveEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Text) && string.IsNullOrWhiteSpace(entry.Keywords)) return;

        var nsIndex = GetOrCreateNamespace(entry.Ns);
        // Index both text and keywords for document enrichment
        var indexableText = entry.Text ?? "";
        if (!string.IsNullOrWhiteSpace(entry.Keywords))
            indexableText += " " + entry.Keywords;
        var tokens = Tokenize(indexableText);

        // Remove old posting if exists
        Remove(entry.Id, entry.Ns);

        nsIndex.DocLengths[entry.Id] = tokens.Length;
        nsIndex.TotalDocLength += tokens.Length;
        nsIndex.DocCount++;

        // Build term frequencies for this document
        var tf = new Dictionary<string, int>();
        foreach (var token in tokens)
        {
            tf[token] = tf.GetValueOrDefault(token) + 1;
        }

        nsIndex.DocTermFreqs[entry.Id] = tf;

        // Update inverted index
        foreach (var term in tf.Keys)
        {
            if (!nsIndex.InvertedIndex.TryGetValue(term, out var postings))
            {
                postings = new HashSet<string>();
                nsIndex.InvertedIndex[term] = postings;
            }
            postings.Add(entry.Id);
        }
    }

    /// <summary>Remove an entry from the index.</summary>
    public void Remove(string id, string ns)
    {
        if (!_namespaces.TryGetValue(ns, out var nsIndex)) return;
        if (!nsIndex.DocTermFreqs.TryGetValue(id, out var oldTf)) return;

        // Remove from inverted index
        foreach (var term in oldTf.Keys)
        {
            if (nsIndex.InvertedIndex.TryGetValue(term, out var postings))
            {
                postings.Remove(id);
                if (postings.Count == 0)
                    nsIndex.InvertedIndex.Remove(term);
            }
        }

        if (nsIndex.DocLengths.TryGetValue(id, out var docLen))
        {
            nsIndex.TotalDocLength -= docLen;
            nsIndex.DocCount--;
            nsIndex.DocLengths.Remove(id);
        }

        nsIndex.DocTermFreqs.Remove(id);
    }

    /// <summary>Score all documents in a namespace against a query using BM25.</summary>
    public IReadOnlyList<(string Id, float Score)> Search(
        string queryText, string ns, int k = 50,
        HashSet<string>? includeIds = null)
    {
        if (string.IsNullOrWhiteSpace(queryText)) return Array.Empty<(string, float)>();
        if (!_namespaces.TryGetValue(ns, out var nsIndex)) return Array.Empty<(string, float)>();
        if (nsIndex.DocCount == 0) return Array.Empty<(string, float)>();

        var queryTokens = Tokenize(queryText);
        float avgDl = (float)nsIndex.TotalDocLength / nsIndex.DocCount;

        var scores = new Dictionary<string, float>();

        foreach (var token in queryTokens.Distinct())
        {
            if (!nsIndex.InvertedIndex.TryGetValue(token, out var postings)) continue;

            // IDF: log((N - df + 0.5) / (df + 0.5) + 1)
            float df = postings.Count;
            float idf = MathF.Log((nsIndex.DocCount - df + 0.5f) / (df + 0.5f) + 1f);

            foreach (var docId in postings)
            {
                if (includeIds is not null && !includeIds.Contains(docId)) continue;

                var docTf = nsIndex.DocTermFreqs[docId];
                float tf = docTf.GetValueOrDefault(token);
                float dl = nsIndex.DocLengths[docId];

                // BM25 score for this term-document pair
                float numerator = tf * (K1 + 1f);
                float denominator = tf + K1 * (1f - B + B * dl / avgDl);
                float termScore = idf * numerator / denominator;

                scores[docId] = scores.GetValueOrDefault(docId) + termScore;
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(k)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>Clear a namespace index.</summary>
    public void ClearNamespace(string ns)
    {
        _namespaces.Remove(ns);
    }

    /// <summary>Check if a namespace has been indexed.</summary>
    public bool HasNamespace(string ns) => _namespaces.ContainsKey(ns);

    /// <summary>Rebuild BM25 index for a namespace from entries.</summary>
    public void RebuildNamespace(string ns, IEnumerable<CognitiveEntry> entries)
    {
        ClearNamespace(ns);
        foreach (var entry in entries)
            Index(entry);
    }

    private NamespaceIndex GetOrCreateNamespace(string ns)
    {
        if (!_namespaces.TryGetValue(ns, out var nsIndex))
        {
            nsIndex = new NamespaceIndex();
            _namespaces[ns] = nsIndex;
        }
        return nsIndex;
    }

    /// <summary>
    /// Tokenize text into lowercase terms. Splits on non-alphanumeric characters,
    /// filters short tokens, and removes common stop words.
    /// Also handles compound tokens: hyphenated words like "time-stamped" emit
    /// both the sub-parts ("time", "stamped") and the joined compound ("timestamped"),
    /// improving recall for queries where hyphenation varies.
    /// </summary>
    public static string[] Tokenize(string text)
    {
        var tokens = new List<string>();
        int start = -1;

        for (int i = 0; i <= text.Length; i++)
        {
            bool isAlphaOrHyphen = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '-');
            if (isAlphaOrHyphen)
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                var rawSpan = text.AsSpan(start, i - start);
                var raw = rawSpan.ToString().ToLowerInvariant();
                start = -1;

                // Check if token contains internal hyphens (compound word)
                if (raw.Contains('-'))
                {
                    // Emit sub-parts
                    var parts = raw.Split('-', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        if (part.Length >= 2 && !IsStopWord(part))
                            tokens.Add(PorterStemmer.Stem(part));
                    }
                    // Emit joined compound form (e.g., "timestamped" from "time-stamped")
                    var joined = string.Concat(parts);
                    if (joined.Length >= 2 && !IsStopWord(joined))
                        tokens.Add(PorterStemmer.Stem(joined));
                }
                else
                {
                    var token = raw.Trim('-');
                    if (token.Length >= 2 && !IsStopWord(token))
                        tokens.Add(PorterStemmer.Stem(token));
                }
            }
        }

        return tokens.ToArray();
    }

    private static bool IsStopWord(string token)
    {
        return token switch
        {
            "a" or "an" or "and" or "are" or "as" or "at" or "be" or "by" or
            "for" or "from" or "has" or "he" or "in" or "is" or "it" or "its" or
            "of" or "on" or "or" or "that" or "the" or "to" or "was" or "were" or
            "will" or "with" => true,
            _ => false
        };
    }

    private sealed class NamespaceIndex
    {
        public Dictionary<string, HashSet<string>> InvertedIndex { get; } = new();
        public Dictionary<string, Dictionary<string, int>> DocTermFreqs { get; } = new();
        public Dictionary<string, int> DocLengths { get; } = new();
        public int DocCount { get; set; }
        public long TotalDocLength { get; set; }
    }
}
