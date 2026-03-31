namespace McpEngramMemory.Core.Models;

/// <summary>
/// Structured search request replacing positional parameters.
/// Supports vector-only, hybrid (vector + BM25), and deep recall search modes.
/// </summary>
public sealed record SearchRequest
{
    /// <summary>The query vector embedding.</summary>
    public required float[] Query { get; init; }

    /// <summary>Namespace to search within.</summary>
    public required string Namespace { get; init; }

    /// <summary>Original query text (required for hybrid search and reranking).</summary>
    public string? QueryText { get; init; }

    /// <summary>Maximum number of results to return.</summary>
    public int K { get; init; } = 5;

    /// <summary>Minimum cosine-similarity score threshold.</summary>
    public float MinScore { get; init; }

    /// <summary>Filter results by category.</summary>
    public string? Category { get; init; }

    /// <summary>Lifecycle states to include (default: stm, ltm).</summary>
    public HashSet<string>? IncludeStates { get; init; }

    /// <summary>Use hybrid search (BM25 + vector via Reciprocal Rank Fusion). Requires QueryText.</summary>
    public bool Hybrid { get; init; }

    /// <summary>Apply token-level reranking for improved precision.</summary>
    public bool Rerank { get; init; }

    /// <summary>Reciprocal Rank Fusion constant (default: 60).</summary>
    public int RrfK { get; init; } = 60;

    /// <summary>Prioritize cluster summary entries in results.</summary>
    public bool SummaryFirst { get; init; }

    /// <summary>Apply cluster-aware MMR diversity reranking to prevent results clustering around one sub-topic.</summary>
    public bool Diversity { get; init; }

    /// <summary>MMR lambda trade-off: 1.0 = pure relevance, 0.0 = pure diversity (default: 0.5).</summary>
    public float DiversityLambda { get; init; } = 0.5f;
}
