using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Evaluation;

namespace McpEngramMemory.Core.Services.Retrieval;

/// <summary>
/// Hybrid search combining vector cosine similarity with BM25 keyword matching,
/// fused via Reciprocal Rank Fusion (RRF). Stateless — caller manages locking.
/// </summary>
public sealed class HybridSearchEngine
{
    /// <summary>
    /// Threshold above which vector results are considered high-confidence,
    /// allowing the search to skip BM25 fusion for better P95 latency.
    /// </summary>
    private const float HighConfidenceThreshold = 0.85f;
    private const float LowConfidenceThreshold = 0.50f;
    private const int CascadeThreshold = 100;
    private const float Bm25SemanticGate = 0.30f;
    private readonly MetricsCollector? _metrics;

    public HybridSearchEngine(MetricsCollector? metrics = null)
    {
        _metrics = metrics;
    }

    /// <summary>
    /// Execute a hybrid search combining vector and BM25 results via RRF.
    /// </summary>
    /// <param name="vectorResults">Pre-computed vector search results (broad candidate set).</param>
    /// <param name="queryText">Original query text for BM25.</param>
    /// <param name="ns">Namespace to search.</param>
    /// <param name="k">Max results to return.</param>
    /// <param name="includeStates">Lifecycle states filter.</param>
    /// <param name="category">Category filter.</param>
    /// <param name="rerank">Whether to apply token reranking.</param>
    /// <param name="rrfK">RRF constant (default 60).</param>
    /// <param name="bm25">BM25 index for keyword search.</param>
    /// <param name="reranker">Token reranker.</param>
    /// <param name="getEntry">Delegate to resolve entry by (id, ns) — used for BM25-only results.</param>
    /// <param name="queryVector">Optional query vector for late-fusion scoring.</param>
    /// <param name="entryCount">Optional entry-count hint for cost-based cascading.</param>
    public IReadOnlyList<CognitiveSearchResult> HybridSearch(
        IReadOnlyList<CognitiveSearchResult> vectorResults,
        string queryText,
        string ns,
        int k,
        HashSet<string>? includeStates,
        string? category,
        bool rerank,
        int rrfK,
        BM25Index bm25,
        IReranker reranker,
        Func<string, string, CognitiveEntry?> getEntry,
        float[]? queryVector = null,
        int entryCount = 0)
        => HybridSearch(
            vectorResults, queryText, ns, k, includeStates, category,
            rerank, rrfK, bm25, reranker, getEntry, queryVector, entryCount,
            bm25IncludeIds: null);

    /// <summary>
    /// Execute a hybrid search combining vector and BM25 results via RRF, with an optional
    /// pre-computed set of eligible entry IDs to scope BM25 scoring before top-K truncation.
    /// </summary>
    public IReadOnlyList<CognitiveSearchResult> HybridSearch(
        IReadOnlyList<CognitiveSearchResult> vectorResults,
        string queryText,
        string ns,
        int k,
        HashSet<string>? includeStates,
        string? category,
        bool rerank,
        int rrfK,
        BM25Index bm25,
        IReranker reranker,
        Func<string, string, CognitiveEntry?> getEntry,
        float[]? queryVector,
        int entryCount,
        HashSet<string>? bm25IncludeIds)
        => HybridSearch(
            vectorResults, queryText, ns, k, includeStates, category,
            rerank, rrfK, bm25, reranker, getEntry, queryVector, entryCount,
            bm25IncludeIds, rerankQueryText: null);

    /// <summary>
    /// Execute a hybrid search combining vector and BM25 results via RRF, using
    /// <paramref name="queryText"/> for BM25 retrieval (which may be synonym- or PRF-expanded)
    /// and <paramref name="rerankQueryText"/> (defaulting to <paramref name="queryText"/>) for
    /// token-level reranking so exact user phrasing is not diluted by expansion terms.
    /// </summary>
    public IReadOnlyList<CognitiveSearchResult> HybridSearch(
        IReadOnlyList<CognitiveSearchResult> vectorResults,
        string queryText,
        string ns,
        int k,
        HashSet<string>? includeStates,
        string? category,
        bool rerank,
        int rrfK,
        BM25Index bm25,
        IReranker reranker,
        Func<string, string, CognitiveEntry?> getEntry,
        float[]? queryVector,
        int entryCount,
        HashSet<string>? bm25IncludeIds,
        string? rerankQueryText)
    {
        string effectiveRerankQuery = string.IsNullOrWhiteSpace(rerankQueryText) ? queryText : rerankQueryText;

        // High-confidence early exit: if vector search returned strong results,
        // skip BM25 fusion to reduce P95 latency. BM25 mainly helps when
        // vector search struggles (semantic gap / keyword mismatch).
        if (vectorResults.Count >= k &&
            vectorResults[0].Score >= HighConfidenceThreshold)
        {
            _metrics?.Increment("hybrid.early_exit");
            var highConf = vectorResults.Take(rerank ? k * 2 : k).ToList();
            if (rerank && highConf.Count > 0)
                highConf = reranker.Rerank(effectiveRerankQuery, highConf).Take(k).ToList();
            else if (highConf.Count > k)
                highConf.RemoveRange(k, highConf.Count - k);
            return highConf;
        }

        // Adaptive RRF: modulate BM25 influence based on vector confidence.
        // High vector confidence → increase rrfK to suppress BM25 noise.
        // Low vector confidence → decrease rrfK to amplify BM25 rescue.
        int adaptiveRrfK = rrfK;
        if (vectorResults.Count > 0)
        {
            float topScore = vectorResults[0].Score;
            if (topScore >= 0.80f)
                adaptiveRrfK = Math.Max(rrfK, 120); // suppress BM25
            else if (topScore < LowConfidenceThreshold)
                adaptiveRrfK = Math.Min(rrfK, 30); // amplify BM25
        }

        var states = includeStates ?? new HashSet<string> { "stm", "ltm" };
        bool hasExplicitFilter = category is not null || !IsDefaultStates(includeStates);

        // Cascade mode: for large namespaces, BM25 primarily boosts vector results,
        // but also injects top BM25-only candidates that pass a semantic gate.
        // This prevents BM25 noise while still allowing keyword rescue when HNSW misses.
        if (entryCount >= CascadeThreshold)
        {
            _metrics?.Increment("hybrid.cascade");
            var vectorIds = vectorResults.Select(r => r.Id).ToHashSet();

            // Search BM25 for both overlap and new candidates without allowing filtered-out
            // entries to crowd out eligible hits before top-K truncation.
            int bm25CandidateK = entryCount >= 5000 ? Math.Max(k * 8, 40) : Math.Max(k * 4, 20);
            var cascadeResolved = new Dictionary<string, CognitiveEntry?>();
            var bm25All = SearchEligibleBm25(
                bm25, queryText, ns, bm25CandidateK, vectorIds, states, category,
                hasExplicitFilter, getEntry, cascadeResolved, bm25IncludeIds);
            var bm25ScoreLookup = bm25All.ToDictionary(x => x.Id, x => x.Score);

            // Boost vector results that also score well in BM25
            float maxBm25 = bm25All.Count > 0 ? bm25All.Max(x => x.Score) : 1f;
            if (maxBm25 <= 0f) maxBm25 = 1f;

            var boosted = new List<CognitiveSearchResult>(vectorResults.Count + k);
            foreach (var vr in vectorResults)
            {
                float boost = 1f;
                if (bm25ScoreLookup.TryGetValue(vr.Id, out float bm25Score))
                    boost = 1f + 0.15f * (bm25Score / maxBm25); // Up to 15% boost

                boosted.Add(new CognitiveSearchResult(
                    vr.Id, vr.Text, vr.Score * boost,
                    vr.LifecycleState, vr.ActivationEnergy,
                    vr.Category, vr.Metadata,
                    vr.IsSummaryNode, vr.SourceClusterId, vr.AccessCount));
            }

            // Inject BM25-only candidates that pass semantic gate (keyword rescue)
            if (queryVector is not null)
            {
                float queryNorm = VectorMath.Norm(queryVector);
                int injected = 0;
                // Scale BM25 injection limit with namespace size — larger namespaces
                // have more entries that HNSW may miss in distant neighborhoods
                int maxInject = entryCount >= 5000 ? k : 3;

                foreach (var (id, _) in bm25All)
                {
                    if (injected >= maxInject) break;
                    if (vectorIds.Contains(id)) continue;

                    if (!cascadeResolved.TryGetValue(id, out var entry) || entry is null) continue;

                    float cosine = VectorMath.Dot(queryVector, entry.Vector) /
                        (queryNorm * VectorMath.Norm(entry.Vector) + 1e-9f);
                    if (cosine >= Bm25SemanticGate)
                    {
                        boosted.Add(new CognitiveSearchResult(
                            entry.Id, entry.Text, cosine,
                            entry.LifecycleState, entry.ActivationEnergy,
                            entry.Category, entry.Metadata,
                            entry.IsSummaryNode, entry.SourceClusterId, entry.AccessCount));
                        _metrics?.Increment("hybrid.bm25_injected_candidate");
                        injected++;
                    }
                }
                if (injected > 0)
                    _metrics?.Increment("hybrid.bm25_injection");
            }

            boosted.Sort((a, b) => b.Score.CompareTo(a.Score));

            if (rerank && boosted.Count > 0)
                boosted = reranker.Rerank(effectiveRerankQuery, boosted).Take(k).ToList();
            else if (boosted.Count > k)
                boosted.RemoveRange(k, boosted.Count - k);

            return boosted;
        }

        if (adaptiveRrfK >= 120 && vectorResults.Count > 0)
            _metrics?.Increment("hybrid.adaptive.suppress");
        else if (adaptiveRrfK <= 30 && vectorResults.Count > 0)
            _metrics?.Increment("hybrid.adaptive.rescue");
        else
            _metrics?.Increment("hybrid.adaptive.standard");

        // Build set of vector result IDs (used for both eligibility tracking and semantic gate)
        var vectorIdSet = vectorResults.Select(r => r.Id).ToHashSet();

        // BM25 search without pre-truncation loss when category/state filters are active.
        int candidateK = Math.Max(k * 4, 20);
        var resolvedEntries = new Dictionary<string, CognitiveEntry?>();
        var bm25Eligible = SearchEligibleBm25(
            bm25, queryText, ns, candidateK, vectorIdSet, states, category,
            hasExplicitFilter, getEntry, resolvedEntries, bm25IncludeIds);

        // Semantic gate: filter BM25-only results by cosine similarity to query vector.
        // Prevents low-relevance keyword matches from polluting RRF fusion.
        // Only apply when vector results show reasonable confidence (real embeddings).
        var bm25Gated = new List<(string Id, float Score)>(bm25Eligible.Count);
        bool applyGate = queryVector is not null &&
            vectorResults.Count > 0 && vectorResults[0].Score >= Bm25SemanticGate;

        if (applyGate)
        {
            float queryNorm = VectorMath.Norm(queryVector!);
            foreach (var (id, bm25Score) in bm25Eligible)
            {
                if (vectorIdSet.Contains(id))
                {
                    bm25Gated.Add((id, bm25Score));
                    continue;
                }

                if (resolvedEntries.TryGetValue(id, out var entry) && entry is not null)
                {
                    float cosine = VectorMath.Dot(queryVector!, entry.Vector) /
                        (queryNorm * VectorMath.Norm(entry.Vector) + 1e-9f);
                    if (cosine >= Bm25SemanticGate)
                        bm25Gated.Add((id, bm25Score));
                }
            }
        }
        else
        {
            bm25Gated.AddRange(bm25Eligible);
        }

        // Reciprocal Rank Fusion
        var vectorRanks = new Dictionary<string, int>(vectorResults.Count);
        for (int i = 0; i < vectorResults.Count; i++)
            vectorRanks[vectorResults[i].Id] = i + 1;

        var bm25Ranks = new Dictionary<string, int>(bm25Gated.Count);
        for (int i = 0; i < bm25Gated.Count; i++)
            bm25Ranks[bm25Gated[i].Id] = i + 1;

        // Merge unique IDs from both sources
        var allIds = new HashSet<string>(vectorRanks.Keys);
        foreach (var key in bm25Ranks.Keys)
            allIds.Add(key);

        var rrfScores = new List<(string Id, float RrfScore)>(allIds.Count);
        foreach (var id in allIds)
        {
            float score = 0f;
            if (vectorRanks.TryGetValue(id, out int vRank))
                score += 1f / (adaptiveRrfK + vRank);
            if (bm25Ranks.TryGetValue(id, out int bRank))
                score += 1f / (adaptiveRrfK + bRank);
            rrfScores.Add((id, score));
        }

        rrfScores.Sort((a, b) => b.RrfScore.CompareTo(a.RrfScore));

        // Build result objects with RRF scores (preserving 2.x score scale compatibility
        // with Auto-PRF, CompositeTools archive fallback, and frozen recall baselines;
        // DiversityReranker normalizes relevance scores internally).
        var vectorLookup = vectorResults.ToDictionary(r => r.Id);
        int takeCount = rerank ? k * 2 : k;
        var results = new List<CognitiveSearchResult>(Math.Min(takeCount, rrfScores.Count));

        foreach (var (id, rrfScore) in rrfScores)
        {
            if (results.Count >= takeCount) break;

            if (vectorLookup.TryGetValue(id, out var vr))
            {
                results.Add(new CognitiveSearchResult(
                    vr.Id, vr.Text, rrfScore,
                    vr.LifecycleState, vr.ActivationEnergy,
                    vr.Category, vr.Metadata,
                    vr.IsSummaryNode, vr.SourceClusterId, vr.AccessCount));
            }
            else if (resolvedEntries.TryGetValue(id, out var entry) && entry is not null)
            {
                // Only materialize BM25-only entries admitted by the filter loop.
                results.Add(new CognitiveSearchResult(
                    entry.Id, entry.Text, rrfScore,
                    entry.LifecycleState, entry.ActivationEnergy,
                    entry.Category, entry.Metadata,
                    entry.IsSummaryNode, entry.SourceClusterId, entry.AccessCount));
            }
        }

        // Optional reranking
        if (rerank && results.Count > 0)
        {
            results = reranker.Rerank(effectiveRerankQuery, results).Take(k).ToList();
        }
        else if (results.Count > k)
        {
            results.RemoveRange(k, results.Count - k);
        }

        return results;
    }

    private static bool IsDefaultStates(HashSet<string>? includeStates)
        => includeStates is null || (includeStates.Count == 2 && includeStates.Contains("stm") && includeStates.Contains("ltm"));

    /// <summary>
    /// Query BM25 while preventing category or lifecycle-state filters from losing eligible matches
    /// to pre-truncation. When explicit filters are active (or when an unfiltered top-K pass had
    /// hits rejected by filters), inspects the full ranked BM25 match list until <paramref name="targetK"/>
    /// eligible candidates are collected.
    /// </summary>
    private static List<(string Id, float Score)> SearchEligibleBm25(
        BM25Index bm25,
        string queryText,
        string ns,
        int targetK,
        HashSet<string> vectorIds,
        HashSet<string> states,
        string? category,
        bool hasExplicitFilter,
        Func<string, string, CognitiveEntry?> getEntry,
        Dictionary<string, CognitiveEntry?> resolvedEntries,
        HashSet<string>? bm25IncludeIds)
    {
        if (states.Count == 0 && vectorIds.Count == 0)
            return new List<(string Id, float Score)>();

        // If caller explicitly supplied eligible IDs, scope BM25 directly; otherwise skip the
        // truncated first pass whenever a category/non-default state filter is active.
        int initialK = bm25IncludeIds is null && hasExplicitFilter ? int.MaxValue : targetK;
        var raw = bm25.Search(queryText, ns, initialK, bm25IncludeIds);
        var eligible = FilterEligibleBm25(raw, targetK, ns, vectorIds, states, category, getEntry, resolvedEntries);

        // Even without explicit filters, archived or below-minScore hits can crowd out eligible
        // entries from BM25's top-K. Retry once unbounded only when BM25 actually hit its cap
        // and filtering dropped at least one hit.
        if (initialK != int.MaxValue && raw.Count == initialK && eligible.Count < targetK)
        {
            raw = bm25.Search(queryText, ns, int.MaxValue, bm25IncludeIds);
            eligible = FilterEligibleBm25(raw, targetK, ns, vectorIds, states, category, getEntry, resolvedEntries);
        }

        return eligible;
    }

    private static List<(string Id, float Score)> FilterEligibleBm25(
        IReadOnlyList<(string Id, float Score)> raw,
        int targetK,
        string ns,
        HashSet<string> vectorIds,
        HashSet<string> states,
        string? category,
        Func<string, string, CognitiveEntry?> getEntry,
        Dictionary<string, CognitiveEntry?> resolvedEntries)
    {
        var eligible = new List<(string Id, float Score)>(Math.Min(targetK, raw.Count));
        foreach (var (id, score) in raw)
        {
            if (eligible.Count >= targetK)
                break;

            if (vectorIds.Contains(id))
            {
                eligible.Add((id, score));
                continue;
            }

            if (resolvedEntries.TryGetValue(id, out var cached))
            {
                if (cached is not null)
                    eligible.Add((id, score));
                continue;
            }

            var entry = getEntry(id, ns);
            if (entry is not null &&
                states.Contains(entry.LifecycleState) &&
                (category is null || entry.Category == category))
            {
                resolvedEntries[id] = entry;
                eligible.Add((id, score));
            }
            else
            {
                resolvedEntries[id] = null;
            }
        }

        return eligible;
    }
}
