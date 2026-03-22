namespace McpEngramMemory.Core.Services.Retrieval;

/// <summary>
/// Auto-generates keyword aliases for cognitive entries at store time.
/// Scans entry text for terms that have known synonyms and appends
/// their colloquial equivalents to the Keywords field, enabling
/// BM25 to match informal queries against technical content.
/// </summary>
public sealed class DocumentEnricher
{
    // Reverse synonym map: technical term → colloquial equivalents
    // This is the inverse of SynonymExpander — enriches documents with
    // the informal terms that users might search for.
    private static readonly Dictionary<string, string[]> ReverseMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Cognitive lifecycle — map technical terms to colloquial ones
        ["accretion"] = new[] { "maintenance", "organize", "growth" },
        ["decay"] = new[] { "cleanup", "expire", "maintenance", "aging" },
        ["collapse"] = new[] { "cleanup", "tidy", "consolidation", "merge" },
        ["consolidation"] = new[] { "organize", "cleanup", "tidy", "merge" },
        ["lifecycle"] = new[] { "maintenance", "automatic", "management" },
        ["pruning"] = new[] { "cleanup", "delete", "trim" },
        ["archival"] = new[] { "archive", "store", "backup", "retire" },

        // Retrieval terms
        ["bm25"] = new[] { "keyword", "search", "text-search", "lexical" },
        ["cosine"] = new[] { "similar", "similarity", "match", "distance" },
        ["rerank"] = new[] { "rank", "sort", "reorder", "refine" },
        ["hnsw"] = new[] { "index", "nearest-neighbor", "approximate", "ann" },
        ["quantize"] = new[] { "compress", "compact", "optimize", "reduce" },
        ["quantization"] = new[] { "compression", "compact", "optimize" },

        // Multi-agent terms
        ["namespace"] = new[] { "scope", "partition", "tenant", "space" },
        ["permission"] = new[] { "access", "grant", "allow", "authorize" },
        ["ownership"] = new[] { "owner", "control", "manage" },

        // Graph terms
        ["traverse"] = new[] { "walk", "navigate", "explore", "follow" },
        ["edge"] = new[] { "link", "connection", "relationship", "relation" },
        ["dbscan"] = new[] { "cluster", "grouping", "segmentation" },

        // Time-series terms
        ["timestamped"] = new[] { "timestamp", "time-stamped", "dated", "temporal" },
        ["influxdb"] = new[] { "timeseries", "time-series", "metrics", "telemetry" },

        // Security reverse maps
        ["tls"] = new[] { "encrypt", "encrypting", "security", "ssl", "certificate" },
        ["encryption"] = new[] { "encrypt", "security", "protect", "secure" },
        ["aes"] = new[] { "encrypt", "cipher", "symmetric" },
        ["rsa"] = new[] { "encrypt", "asymmetric", "key" },

        // ML reverse maps
        ["rnn"] = new[] { "sequence", "recurrent", "sequential", "memory" },
        ["lstm"] = new[] { "sequence", "recurrent", "memory", "gate" },
        ["cnn"] = new[] { "image", "convolution", "recognition", "vision" },
        ["backpropagation"] = new[] { "training", "gradient", "learning" },
        ["transformer"] = new[] { "attention", "sequence", "nlp", "bert" },
        ["neuralnet"] = new[] { "neural", "deep-learning", "network" },

        // Systems reverse maps
        ["prometheus"] = new[] { "monitoring", "metrics", "alerting", "observability" },
        ["observability"] = new[] { "monitoring", "logging", "tracing" },
        ["kubernetes"] = new[] { "container", "deploy", "orchestration", "pod" },
        ["cicd"] = new[] { "deploy", "pipeline", "automation", "release" },

        // Networking reverse maps
        ["websocket"] = new[] { "streaming", "realtime", "protocol", "bidirectional" },
        ["kafka"] = new[] { "messaging", "queue", "streaming", "event" },
        ["rabbitmq"] = new[] { "messaging", "queue", "broker" },

        // Data/storage reverse maps
        ["redis"] = new[] { "cache", "caching", "keyvalue", "fast" },
        ["cdn"] = new[] { "cache", "caching", "edge", "content" },
        ["simd"] = new[] { "performance", "optimization", "vectorized", "fast" },
    };

    /// <summary>
    /// Enrich an entry's Keywords field by scanning its text for technical terms
    /// and appending their colloquial equivalents. Idempotent — won't duplicate
    /// keywords that already exist.
    /// </summary>
    /// <param name="text">The entry's text content.</param>
    /// <param name="existingKeywords">Any existing keywords on the entry.</param>
    /// <param name="maxKeywords">Maximum total keywords to generate (default: 20).</param>
    /// <returns>The enriched keywords string, or null if no enrichment needed.</returns>
    public string? Enrich(string? text, string? existingKeywords = null, int maxKeywords = 20)
    {
        if (string.IsNullOrWhiteSpace(text))
            return existingKeywords;

        var tokens = Tokenize(text);
        var tokenSet = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
        var existingSet = string.IsNullOrWhiteSpace(existingKeywords)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(Tokenize(existingKeywords), StringComparer.OrdinalIgnoreCase);

        var newKeywords = new List<string>();

        foreach (var token in tokenSet)
        {
            if (ReverseMap.TryGetValue(token, out var aliases))
            {
                foreach (var alias in aliases)
                {
                    if (!tokenSet.Contains(alias) && !existingSet.Contains(alias) && existingSet.Add(alias))
                    {
                        newKeywords.Add(alias);
                        if (newKeywords.Count >= maxKeywords) break;
                    }
                }
            }
            if (newKeywords.Count >= maxKeywords) break;
        }

        if (newKeywords.Count == 0)
            return existingKeywords;

        var combined = string.IsNullOrWhiteSpace(existingKeywords)
            ? string.Join(" ", newKeywords)
            : existingKeywords + " " + string.Join(" ", newKeywords);

        return combined;
    }

    /// <summary>Get the reverse synonym map for testing/inspection.</summary>
    public static IReadOnlyDictionary<string, string[]> GetReverseMap() => ReverseMap;

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        int start = -1;

        for (int i = 0; i <= text.Length; i++)
        {
            bool isAlpha = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '-' || text[i] == '_');
            if (isAlpha)
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
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
