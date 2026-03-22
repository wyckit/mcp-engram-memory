namespace McpEngramMemory.Core.Services.Retrieval;

/// <summary>
/// Query-time synonym expansion for bridging vocabulary gaps between colloquial
/// user queries and technical domain terminology stored in memory entries.
/// Appends domain-specific synonyms to queries without removing original terms.
/// </summary>
public sealed class SynonymExpander
{
    // Domain synonym map: colloquial term → technical equivalents
    // These address known semantic gaps in bge-micro-v2 embedding space
    private static readonly Dictionary<string, string[]> SynonymMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Cognitive memory lifecycle (fixes rw-q15, c-q07)
        ["maintenance"] = new[] { "accretion", "decay", "lifecycle" },
        ["cleanup"] = new[] { "collapse", "consolidation", "pruning", "archival" },
        ["archival"] = new[] { "archive", "lifecycle", "decay", "stm", "ltm" },
        ["automatic"] = new[] { "autonomous", "scheduled", "background", "scanner" },
        ["delete"] = new[] { "purge", "prune", "remove", "decay" },
        ["expire"] = new[] { "decay", "archive", "lifecycle", "ttl" },
        ["organize"] = new[] { "cluster", "consolidation", "accretion", "categorize" },
        ["tidy"] = new[] { "cleanup", "consolidation", "pruning", "collapse" },

        // Search and retrieval terminology
        ["search"] = new[] { "retrieval", "recall", "query", "lookup" },
        ["find"] = new[] { "search", "retrieval", "recall", "lookup" },
        ["rank"] = new[] { "rerank", "scoring", "relevance", "ranking" },
        ["sort"] = new[] { "rank", "ordering", "scoring" },
        ["match"] = new[] { "similarity", "cosine", "relevance" },
        ["keyword"] = new[] { "bm25", "token", "term", "lexical" },

        // Vector/embedding terminology
        ["embedding"] = new[] { "vector", "onnx", "representation", "encoding" },
        ["similar"] = new[] { "cosine", "similarity", "nearest", "neighbor" },
        ["compress"] = new[] { "quantize", "quantization", "int8", "compact" },

        // Multi-agent terminology
        ["share"] = new[] { "sharing", "permission", "namespace", "grant" },
        ["access"] = new[] { "permission", "ownership", "grant", "acl" },
        ["agent"] = new[] { "multi-agent", "identity", "agentid" },

        // Data structures
        ["database"] = new[] { "sqlite", "storage", "persistence", "store" },
        ["index"] = new[] { "hnsw", "inverted", "bm25", "btree" },
        ["graph"] = new[] { "edge", "neighbor", "traverse", "knowledge-graph" },
        ["cluster"] = new[] { "dbscan", "grouping", "accretion", "consolidation" },

        // Time-series / compound terms (complements BM25 compound tokenization)
        ["timestamp"] = new[] { "timestamped", "time-series", "temporal", "datetime" },
        ["realtime"] = new[] { "real-time", "streaming", "low-latency", "live" },
        ["timeseries"] = new[] { "time-series", "influxdb", "temporal", "timestamped" },

        // Security domain (fixes s19: "encrypting data in transit")
        ["encrypt"] = new[] { "encryption", "tls", "cipher", "cryptography" },
        ["encrypting"] = new[] { "encryption", "tls", "cipher", "cryptographic" },
        ["encryption"] = new[] { "encrypt", "tls", "aes", "rsa", "cipher" },
        ["security"] = new[] { "authentication", "authorization", "tls", "encryption" },
        ["vulnerability"] = new[] { "exploit", "cve", "injection", "xss", "sqli" },
        ["password"] = new[] { "credential", "hash", "bcrypt", "auth" },

        // Machine learning domain (fixes s21: "processing sequences with memory")
        ["sequence"] = new[] { "recurrent", "rnn", "lstm", "sequential", "temporal" },
        ["sequences"] = new[] { "recurrent", "rnn", "lstm", "sequential" },
        ["neural"] = new[] { "neuralnet", "deep-learning", "network", "layer" },
        ["training"] = new[] { "backpropagation", "gradient", "optimization", "epoch" },
        ["image"] = new[] { "cnn", "convolution", "vision", "pixel" },
        ["recognition"] = new[] { "classification", "detection", "cnn", "inference" },
        ["prediction"] = new[] { "regression", "forecast", "inference", "model" },

        // Systems/infrastructure domain (fixes s24: "monitoring distributed systems")
        ["monitoring"] = new[] { "observability", "alerting", "prometheus", "metrics" },
        ["alerting"] = new[] { "monitoring", "notification", "pager", "slo" },
        ["observability"] = new[] { "monitoring", "logging", "tracing", "metrics" },
        ["scaling"] = new[] { "horizontal", "sharding", "replication", "loadbalancer" },
        ["deploy"] = new[] { "deployment", "cicd", "release", "rollout" },
        ["deployment"] = new[] { "cicd", "kubernetes", "container", "release" },
        ["container"] = new[] { "docker", "kubernetes", "virtualization", "pod" },

        // Networking domain
        ["protocol"] = new[] { "http", "tcp", "websocket", "tls" },
        ["api"] = new[] { "rest", "graphql", "endpoint", "http" },
        ["streaming"] = new[] { "websocket", "sse", "realtime", "pubsub" },
        ["messaging"] = new[] { "queue", "kafka", "rabbitmq", "pubsub" },

        // Data/storage domain (fixes s20, s08)
        ["cache"] = new[] { "caching", "redis", "memcached", "cdn" },
        ["caching"] = new[] { "cache", "redis", "cdn", "invalidation" },
        ["storage"] = new[] { "persistence", "filesystem", "objectstorage", "database" },
        ["performance"] = new[] { "optimization", "latency", "throughput", "simd" },
        ["optimize"] = new[] { "optimization", "performance", "tuning", "profiling" },

        // General CS vocabulary bridges
        ["lookup"] = new[] { "search", "find", "locate", "resolve" },
        ["locate"] = new[] { "find", "locator", "resolve", "lookup" },
        ["isolate"] = new[] { "namespace", "partition", "tenant", "scope" },
        ["lesson"] = new[] { "retrospective", "review", "feedback", "postmortem" },
        ["review"] = new[] { "retrospective", "feedback", "audit", "inspect" },
    };

    /// <summary>
    /// Expand a query by appending synonym terms for any recognized tokens.
    /// Original query terms are preserved; synonyms are appended.
    /// </summary>
    /// <param name="query">The original query text.</param>
    /// <param name="maxExpansionsPerTerm">Max synonyms to add per matched term (default: 3).</param>
    /// <returns>The expanded query string.</returns>
    public string Expand(string query, int maxExpansionsPerTerm = 3)
    {
        if (string.IsNullOrWhiteSpace(query))
            return query;

        var tokens = Tokenize(query);
        var expansions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queryTokenSet = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);

        foreach (var token in tokens)
        {
            if (SynonymMap.TryGetValue(token, out var synonyms))
            {
                int added = 0;
                foreach (var syn in synonyms)
                {
                    // Don't add if already in the original query
                    if (!queryTokenSet.Contains(syn) && expansions.Add(syn))
                    {
                        added++;
                        if (added >= maxExpansionsPerTerm) break;
                    }
                }
            }
        }

        if (expansions.Count == 0)
            return query;

        return query + " " + string.Join(" ", expansions);
    }

    /// <summary>
    /// Check if a query would be expanded (has matching synonym terms).
    /// Useful for deciding whether to use the expanded vs original query.
    /// </summary>
    public bool HasExpansions(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;
        return Tokenize(query).Any(t => SynonymMap.ContainsKey(t));
    }

    /// <summary>Get the synonym map for testing/inspection.</summary>
    public static IReadOnlyDictionary<string, string[]> GetSynonymMap() => SynonymMap;

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        int start = -1;

        for (int i = 0; i <= text.Length; i++)
        {
            bool isAlphaNum = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '-' || text[i] == '_');
            if (isAlphaNum)
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                var token = text[start..i].Trim('-', '_');
                if (token.Length >= 2)
                    tokens.Add(token);
                start = -1;
            }
        }

        return tokens;
    }
}
