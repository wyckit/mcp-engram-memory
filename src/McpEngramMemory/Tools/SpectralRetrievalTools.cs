using System.ComponentModel;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Retrieval;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for graph-aware retrieval. The diffusion kernel rebuilt for
/// decay/consolidation is applied here to relevance scores instead of decay
/// debt — same primitive, different signal.
/// </summary>
[McpServerToolType]
public sealed class SpectralRetrievalTools
{
    private readonly CognitiveIndex _index;
    private readonly IEmbeddingService _embedding;
    private readonly SpectralRetrievalReranker _reranker;

    public SpectralRetrievalTools(
        CognitiveIndex index,
        IEmbeddingService embedding,
        SpectralRetrievalReranker reranker)
    {
        _index = index;
        _embedding = embedding;
        _reranker = reranker;
    }

    [McpServerTool(Name = "spectral_recall")]
    [Description("Graph-aware retrieval: runs the standard ANN/hybrid search to gather candidates, then re-ranks them through the memory-graph diffusion kernel. Mode 'broad' applies a low-pass filter that boosts cluster-supported memories (themes, summaries) — best for conceptual queries. Mode 'specific' applies a high-pass filter that boosts entries whose score exceeds their cluster mean — best for precise factual queries. Mode 'none' disables spectral re-ranking and returns standard search results. Spectral re-ranking can surface entries the upstream search didn't return if their cluster scored well; this is intentional.")]
    public IReadOnlyList<CognitiveSearchResult> SpectralRecall(
        [Description("Query text. Embedded via the configured embedding model.")] string query,
        [Description("Namespace to search.")] string ns,
        [Description("Spectral filter mode: 'none', 'broad', or 'specific'. Default 'broad'.")] string mode = "broad",
        [Description("Number of results to return. Default 10.")] int k = 10,
        [Description("Minimum cosine score for the upstream candidate pool. Default 0.0.")] float minScore = 0.0f,
        [Description("Heat-kernel diffusion time t. Larger = stronger smoothing toward cluster means. Default 1.0.")] float diffusionTime = 1.0f,
        [Description("Candidate-pool multiplier on top-K. Default 5; larger pools give the reranker more material but cost more.")] int candidateMultiplier = 5)
    {
        var parsedMode = ParseMode(mode);
        if (k <= 0) k = 10;
        if (candidateMultiplier <= 0) candidateMultiplier = 5;

        // Gather a broader candidate pool than the user wants returned, so the
        // reranker has enough material to redistribute scores meaningfully.
        var queryVector = _embedding.Embed(query);
        var candidates = _index.Search(queryVector, ns, k * candidateMultiplier, minScore);

        if (candidates.Count == 0) return Array.Empty<CognitiveSearchResult>();

        // Pull (id, score) out for the reranker.
        var scoreList = new List<(string Id, float Score)>(candidates.Count);
        foreach (var c in candidates) scoreList.Add((c.Id, c.Score));

        var reranked = _reranker.Rerank(ns, scoreList, parsedMode, k, diffusionTime);

        // Resolve the reranked ids back to full search results. Entries that
        // surfaced via spectral redistribution (weren't in the original
        // candidate pool) are fetched fresh from the index.
        var byId = new Dictionary<string, CognitiveSearchResult>(candidates.Count);
        foreach (var c in candidates) byId[c.Id] = c;

        var results = new List<CognitiveSearchResult>(reranked.Count);
        foreach (var (id, score) in reranked)
        {
            if (byId.TryGetValue(id, out var existing))
            {
                results.Add(existing with { Score = score });
                continue;
            }

            // Entry surfaced spectrally; fetch it.
            var entry = _index.Get(id, ns);
            if (entry is null) continue;
            results.Add(new CognitiveSearchResult(
                entry.Id, entry.Text, score, entry.LifecycleState,
                entry.ActivationEnergy, entry.Category,
                entry.Metadata.Count > 0 ? new Dictionary<string, string>(entry.Metadata) : null,
                entry.IsSummaryNode, entry.SourceClusterId, entry.AccessCount));
        }

        return results;
    }

    private static SpectralRetrievalMode ParseMode(string raw) =>
        raw?.ToLowerInvariant() switch
        {
            "broad" => SpectralRetrievalMode.Broad,
            "specific" => SpectralRetrievalMode.Specific,
            "none" => SpectralRetrievalMode.None,
            _ => SpectralRetrievalMode.Broad,
        };
}
