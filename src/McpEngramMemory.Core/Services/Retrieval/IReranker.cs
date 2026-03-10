using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Retrieval;

/// <summary>
/// Interface for search result rerankers. Allows plugging in different strategies
/// (token-overlap, cross-encoder, etc.).
/// </summary>
public interface IReranker
{
    IReadOnlyList<CognitiveSearchResult> Rerank(
        string queryText, IReadOnlyList<CognitiveSearchResult> results);
}
