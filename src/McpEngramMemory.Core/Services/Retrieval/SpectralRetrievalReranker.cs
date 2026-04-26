using McpEngramMemory.Core.Services.Graph;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Retrieval;

/// <summary>
/// Re-ranks search results through the memory-graph diffusion kernel so retrieval
/// becomes graph-aware, mirroring what the decay and consolidation passes already
/// do for lifecycle. Two non-trivial modes (Broad / Specific) plus a passthrough
/// (None) for callers that just want the existing scoring pipeline.
///
/// Mechanism. Each result has a relevance score from upstream (BM25 + ANN + RRF
/// + optional rerank/physics). Treat the per-entry scores as a signal on the
/// graph and apply a spectral filter to get a smoothed signal:
///
/// - <see cref="SpectralRetrievalMode.Broad"/>: low-pass filter <c>exp(-lambda*t)</c>.
///   Damps high-frequency modes; preserves low-frequency (cluster-mean) structure.
///   Boosts memories whose cluster collectively scores well — surfaces themes,
///   cluster summaries, and central concept memories. Best for broad/conceptual
///   queries ("what do we know about consolidation?").
///
/// - <see cref="SpectralRetrievalMode.Specific"/>: high-pass filter <c>1 - exp(-lambda*t)</c>.
///   Damps low-frequency (cluster-mean) modes; preserves high-frequency
///   (per-entry deviation) modes. Boosts memories that score high *relative to
///   their cluster* — surfaces outliers, edge cases, the specific entry that
///   actually answers a precise question. Best for specific factual queries
///   ("what's the exact value we set for foo?").
///
/// - <see cref="SpectralRetrievalMode.None"/>: passthrough; results sorted by
///   their incoming scores with no spectral redistribution. Use as the default
///   while gathering empirical confidence in spectral re-ranking.
///
/// Like all kernel consumers, this falls back gracefully when the namespace
/// doesn't qualify for a diffusion basis (too small / too sparsely linked) —
/// returns the original results sorted by score, no spectral effect.
///
/// One subtlety: passing a sparse score vector into <see cref="MemoryDiffusionKernel.ApplySpectralFilter"/>
/// produces a *dense* output covering every entry in the namespace. So the
/// re-ranked top-K can include entries the upstream pipeline didn't surface,
/// if their cluster scored well (in Broad mode). This is the intended behavior:
/// spectral retrieval can rescue thematically-relevant entries that BM25/ANN
/// missed.
/// </summary>
public sealed class SpectralRetrievalReranker
{
    private readonly MemoryDiffusionKernel _kernel;
    private readonly ILogger<SpectralRetrievalReranker>? _logger;

    public SpectralRetrievalReranker(
        MemoryDiffusionKernel kernel,
        ILogger<SpectralRetrievalReranker>? logger = null)
    {
        _kernel = kernel;
        _logger = logger;
    }

    /// <summary>
    /// Re-rank <paramref name="originalResults"/> in-namespace and return the
    /// top <paramref name="topK"/> by (re-)score.
    /// </summary>
    /// <param name="ns">Namespace the results came from. The diffusion basis is per-namespace.</param>
    /// <param name="originalResults">Upstream search results: id and score per entry.</param>
    /// <param name="mode">Filter shape. <see cref="SpectralRetrievalMode.None"/> short-circuits.</param>
    /// <param name="topK">Result cap on the reranked list.</param>
    /// <param name="diffusionTime">Heat-kernel time t. Larger t = stronger smoothing toward cluster means. Default 1.0.</param>
    public IReadOnlyList<(string Id, float Score)> Rerank(
        string ns,
        IReadOnlyList<(string Id, float Score)> originalResults,
        SpectralRetrievalMode mode,
        int topK = 10,
        float diffusionTime = 1.0f)
    {
        if (originalResults.Count == 0)
            return Array.Empty<(string, float)>();

        // Passthrough fast paths: explicit None, or no qualifying basis.
        if (mode == SpectralRetrievalMode.None || _kernel.GetBasis(ns) is null)
        {
            return SortAndCap(originalResults, topK);
        }

        // Build the score signal as a sparse map keyed by id. Skip non-positive
        // scores so we don't waste eigen-projection on zeros.
        var signal = new Dictionary<string, float>(originalResults.Count);
        foreach (var (id, score) in originalResults)
            if (score > 0f) signal[id] = score;

        if (signal.Count == 0)
            return SortAndCap(originalResults, topK);

        Func<float, float> filter = mode switch
        {
            SpectralRetrievalMode.Broad => lambda => MathF.Exp(-lambda * diffusionTime),
            SpectralRetrievalMode.Specific => lambda => 1f - MathF.Exp(-lambda * diffusionTime),
            _ => _ => 1f,
        };

        var smoothed = _kernel.ApplySpectralFilter(ns, signal, filter);

        // Collect every entry that has any signal (original or spectrally-induced),
        // dedup, sort by smoothed score descending, take top-K.
        var seen = new HashSet<string>(smoothed.Count + originalResults.Count);
        var combined = new List<(string Id, float Score)>(smoothed.Count + originalResults.Count);
        foreach (var kv in smoothed)
        {
            if (seen.Add(kv.Key)) combined.Add((kv.Key, kv.Value));
        }
        // Include any original results whose score was 0 (so they didn't enter
        // signal) — they should still be considered, just at their incoming score.
        foreach (var (id, score) in originalResults)
        {
            if (seen.Add(id)) combined.Add((id, score));
        }

        return SortAndCap(combined, topK);
    }

    private static IReadOnlyList<(string Id, float Score)> SortAndCap(
        IReadOnlyList<(string Id, float Score)> results, int topK)
    {
        var copy = results.ToList();
        copy.Sort((a, b) => b.Score.CompareTo(a.Score));
        return copy.Count <= topK ? copy : copy.GetRange(0, topK);
    }
}

/// <summary>Spectral retrieval modes for <see cref="SpectralRetrievalReranker.Rerank"/>.</summary>
public enum SpectralRetrievalMode
{
    /// <summary>No spectral filtering; sort by incoming scores. Default.</summary>
    None,

    /// <summary>Low-pass filter: boost cluster-supported memories. Best for broad/thematic queries.</summary>
    Broad,

    /// <summary>High-pass filter: boost outliers within cluster. Best for specific/factual queries.</summary>
    Specific,
}
