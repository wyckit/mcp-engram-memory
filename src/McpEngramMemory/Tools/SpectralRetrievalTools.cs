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
    private readonly NamespaceAccess _access;

    public SpectralRetrievalTools(
        CognitiveIndex index,
        IEmbeddingService embedding,
        SpectralRetrievalReranker reranker,
        NamespaceAccess access)
    {
        _index = index;
        _embedding = embedding;
        _reranker = reranker;
        _access = access;
    }

    [McpServerTool(Name = "spectral_recall", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Graph-aware retrieval: gathers vector-search candidates, then re-ranks them through the memory-graph diffusion kernel. Mode 'broad' applies a low-pass filter for conceptual queries and can surface additional cluster-supported memories. Mode 'specific' preserves candidate relevance and adds a bounded specificity bonus (at most 25%) for precise factual queries; isolated entries retain their scores. Mode 'none' disables spectral re-ranking and returns standard search results.")]
    public IReadOnlyList<CognitiveSearchResult> SpectralRecall(
        [Description("Query text. Embedded via the configured embedding model.")] string query,
        [Description("Namespace to search.")] string ns,
        [Description("Spectral filter mode: 'none', 'broad', or 'specific'. Default 'broad'.")] string mode = "broad",
        [Description("Number of results to return. Default 10.")] int k = 10,
        [Description("Minimum cosine score for the upstream candidate pool. Default 0.0.")] float minScore = 0.0f,
        [Description("Heat-kernel diffusion time t. Larger = stronger smoothing toward cluster means. Default 1.0.")] float diffusionTime = 1.0f,
        [Description("Candidate-pool multiplier on top-K. Default 5; larger pools give the reranker more material but cost more.")] int candidateMultiplier = 5)
    {
        if (!_access.CanRead(ns)) return Array.Empty<CognitiveSearchResult>();

        var parsedMode = ParseMode(mode);
        if (k <= 0) k = 10;
        if (candidateMultiplier <= 0) candidateMultiplier = 5;

        // Gather a broader candidate pool than the user wants returned, so the
        // reranker has enough material to redistribute scores meaningfully.
        var queryVector = _embedding.Embed(query);
        var candidates = _index.Search(queryVector, ns, tenantId: _access.TenantId, k: k * candidateMultiplier, minScore: minScore);

        if (candidates.Count == 0) return Array.Empty<CognitiveSearchResult>();

        // Pull (id, score) out for the reranker. Over-fetch slightly so filtering out archived
        // spectrally-surfaced neighbors does not silently shrink the returned top-K.
        var scoreList = new List<(string Id, float Score)>(candidates.Count);
        foreach (var c in candidates) scoreList.Add((c.Id, c.Score));

        int rerankTopK = Math.Max(k * 2, k + 10);
        var reranked = _reranker.Rerank(ns, scoreList, parsedMode, tenantId: _access.TenantId, topK: rerankTopK, diffusionTime: diffusionTime);

        // Resolve the reranked ids back to full search results. Entries that
        // surfaced via spectral redistribution (weren't in the original
        // candidate pool) are fetched fresh from the index and filtered to active states.
        var byId = new Dictionary<string, CognitiveSearchResult>(candidates.Count);
        foreach (var c in candidates) byId[c.Id] = c;

        var results = new List<CognitiveSearchResult>(Math.Min(k, reranked.Count));
        foreach (var (id, score) in reranked)
        {
            if (results.Count >= k) break;

            if (byId.TryGetValue(id, out var existing))
            {
                results.Add(existing with { Score = score });
                continue;
            }

            // Entry surfaced spectrally; fetch it (within this tenant) and exclude archived entries
            // to match the default active-state ('stm', 'ltm') contract of upstream Search.
            var entry = _index.Get(id, ns, tenantId: _access.TenantId);
            if (entry is null || entry.LifecycleState is not ("stm" or "ltm")) continue;
            results.Add(new CognitiveSearchResult(
                entry.Id, entry.Text, score, entry.LifecycleState,
                entry.ActivationEnergy, entry.Category,
                entry.Metadata.Count > 0 ? new Dictionary<string, string>(entry.Metadata) : null,
                entry.IsSummaryNode, entry.SourceClusterId, entry.AccessCount));
        }

        // Record access on returned entries (matching search_memory and recall) so spectrally
        // retrieved memories receive normal activation reinforcement.
        foreach (var r in results)
            _index.RecordAccess(r.Id, ns, _access.TenantId);

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
