using System.Collections.Concurrent;
using McpEngramMemory.Core.Models;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Graph;

/// <summary>
/// Per-namespace cache of the top-K eigenbasis of the memory-graph normalized
/// Laplacian. Consumed by spectral diffusion of decay debt (LifecycleEngine, PR 2)
/// and any future spectral retrieval / consolidation operators.
///
/// Construction. For namespace <c>ns</c>:
/// 1. Snapshot entry ids in stable order from <see cref="CognitiveIndex.GetAllInNamespace"/>.
/// 2. Snapshot the global edge list from <see cref="KnowledgeGraph.GetAllEdges"/>, filter
///    to edges whose endpoints are both in <c>ns</c> and whose relation is in
///    <see cref="PositiveRelations"/> (parent_child, cross_reference, similar_to,
///    elaborates, depends_on). The <c>contradicts</c> relation is excluded so the
///    weight matrix W stays non-negative and the Laplacian L = I - D^(-1/2) W D^(-1/2)
///    stays positive semi-definite (heat kernel exp(-tL) remains a contraction).
/// 3. Symmetrize: W[i,j] = max(w(i->j), w(j->i)).
/// 4. Find the top-K largest eigenpairs (lambda_M, u) of M = D^(-1/2) W D^(-1/2)
///    via <see cref="RandomizedEigensolver.SolveTopK"/>; convert to the smallest
///    eigenpairs of L via lambda_L = 1 - lambda_M and sort ascending.
///
/// Cache invalidation is revision-based: each cached basis records the
/// <see cref="KnowledgeGraph.Revision"/> at the time of computation; any subsequent
/// edge mutation increments the live revision, and the next <see cref="GetBasis"/>
/// call detects the divergence and recomputes. Recomputation runs synchronously
/// under a per-namespace lock — concurrent calls for the same namespace serialize,
/// but different namespaces compute independently.
/// </summary>
public sealed class GraphLaplacianSpine
{
    /// <summary>Edge relations that contribute positive weight to the Laplacian.</summary>
    public static readonly IReadOnlySet<string> PositiveRelations = new HashSet<string>
    {
        "parent_child", "cross_reference", "similar_to", "elaborates", "depends_on",
    };

    /// <summary>Default top-K. 96 covers the dominant low-frequency modes for typical namespaces.</summary>
    public const int DefaultTopK = 96;

    /// <summary>Below this node count, spectral methods give no benefit and the spine is bypassed.</summary>
    public const int MinimumNodesForSpectral = 32;

    /// <summary>Random sketch oversample (Halko-Martinsson-Tropp typical value).</summary>
    private const int Oversample = 10;

    /// <summary>Power iterations to align the sketch with the dominant subspace.</summary>
    private const int PowerIterations = 5;

    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ILogger<GraphLaplacianSpine>? _logger;

    private readonly ConcurrentDictionary<string, LaplacianBasis> _cache = new();
    private readonly ConcurrentDictionary<string, object> _nsLocks = new();

    public GraphLaplacianSpine(
        CognitiveIndex index,
        KnowledgeGraph graph,
        ILogger<GraphLaplacianSpine>? logger = null)
    {
        _index = index;
        _graph = graph;
        _logger = logger;
    }

    /// <summary>
    /// Return the top-K eigenbasis for <paramref name="ns"/>, recomputing if the cache
    /// is missing, stale (graph revision diverged), or has fewer eigenpairs than requested.
    /// Returns <c>null</c> if the namespace has fewer than <see cref="MinimumNodesForSpectral"/>
    /// nodes — callers should fall back to non-spectral behavior in that case.
    /// </summary>
    public LaplacianBasis? GetBasis(string ns, int topK = DefaultTopK)
    {
        long currentRev = _graph.Revision;
        if (_cache.TryGetValue(ns, out var cached)
            && cached.GraphRevision == currentRev
            && (cached.TopK >= topK || cached.TopK >= cached.NodeCount))
        {
            // Either the cache has enough modes for the request, or it already
            // has the maximum possible (TopK was clamped to NodeCount). Either
            // way, no recomputation needed.
            return cached;
        }

        var nsLock = _nsLocks.GetOrAdd(ns, _ => new object());
        lock (nsLock)
        {
            currentRev = _graph.Revision;
            if (_cache.TryGetValue(ns, out cached)
                && cached.GraphRevision == currentRev
                && cached.TopK >= topK)
            {
                return cached;
            }

            var built = ComputeBasis(ns, topK, currentRev);
            if (built is not null)
                _cache[ns] = built;
            else
                _cache.TryRemove(ns, out _);
            return built;
        }
    }

    /// <summary>
    /// Apply a per-mode spectral filter to a per-entry signal. Entries not present in
    /// the cached basis pass through unchanged (e.g., entries added after basis
    /// computation, on a stale-but-not-yet-rebuilt basis).
    ///
    /// Mechanism: project signal into the basis (sigma_hat[k] = sum_i U[i,k] · signal[i]),
    /// apply <paramref name="modeFilter"/> to each mode, project back. For diffusion of
    /// decay debt with subdiffusive exponent alpha and step dt, pass:
    /// <c>lambda =&gt; MathF.Exp(-MathF.Pow(lambda, alpha) * dt)</c>.
    /// </summary>
    public IReadOnlyDictionary<string, float> ApplySpectralFilter(
        string ns,
        IReadOnlyDictionary<string, float> signal,
        Func<float, float> modeFilter)
    {
        var basis = GetBasis(ns);
        if (basis is null) return signal;

        int n = basis.NodeCount;
        int k = basis.TopK;
        var U = basis.Eigenvectors;

        // Project: sigHat[j] = sum_i U[i,j] * signal[entryIds[i]]
        var sigHat = new float[k];
        for (int i = 0; i < n; i++)
        {
            if (!signal.TryGetValue(basis.EntryIds[i], out var v) || v == 0f) continue;
            for (int j = 0; j < k; j++)
                sigHat[j] += U[i, j] * v;
        }

        // Filter in spectral space.
        for (int j = 0; j < k; j++)
            sigHat[j] *= modeFilter(basis.Eigenvalues[j]);

        // Project back: out[i] = sum_j U[i,j] * sigHat[j].
        var result = new Dictionary<string, float>(signal.Count);
        foreach (var kv in signal) result[kv.Key] = kv.Value; // pass-through for ids outside the basis
        for (int i = 0; i < n; i++)
        {
            float s = 0f;
            for (int j = 0; j < k; j++) s += U[i, j] * sigHat[j];
            result[basis.EntryIds[i]] = s;
        }
        return result;
    }

    /// <summary>Diagnostics view of the cached basis (or a freshly-computed one) for <paramref name="ns"/>.</summary>
    public LaplacianStats? GetStats(string ns)
    {
        var basis = GetBasis(ns);
        if (basis is null) return null;
        bool stale = basis.GraphRevision != _graph.Revision;
        return new LaplacianStats(
            ns,
            basis.NodeCount,
            basis.EdgeCount,
            basis.TopK,
            basis.Eigenvalues[0],
            basis.Eigenvalues[^1],
            basis.GraphRevision,
            basis.ComputedAt,
            stale);
    }

    /// <summary>Drop the cached basis for a namespace. Next <see cref="GetBasis"/> will recompute.</summary>
    public void Invalidate(string ns) => _cache.TryRemove(ns, out _);

    // ── internals ─────────────────────────────────────────────────────────────

    private LaplacianBasis? ComputeBasis(string ns, int topK, long graphRevision)
    {
        var entries = _index.GetAllInNamespace(ns);
        if (entries.Count < MinimumNodesForSpectral)
        {
            _logger?.LogDebug(
                "Spine bypass for ns={Namespace}: {Count} nodes < {Min} minimum.",
                ns, entries.Count, MinimumNodesForSpectral);
            return null;
        }

        var entryIds = entries.Select(e => e.Id).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var indexOf = new Dictionary<string, int>(entryIds.Length);
        for (int i = 0; i < entryIds.Length; i++) indexOf[entryIds[i]] = i;

        // Build symmetric sparse adjacency restricted to this namespace and positive relations.
        // First pass: collect candidate edge weights keyed by ordered (i,j) with i<j.
        var allEdges = _graph.GetAllEdges();
        var weights = new Dictionary<(int Lo, int Hi), float>();
        int edgeCount = 0;
        foreach (var edge in allEdges)
        {
            if (!PositiveRelations.Contains(edge.Relation)) continue;
            if (!indexOf.TryGetValue(edge.SourceId, out var src)) continue;
            if (!indexOf.TryGetValue(edge.TargetId, out var dst)) continue;
            if (src == dst) continue;

            var key = src < dst ? (src, dst) : (dst, src);
            if (!weights.TryGetValue(key, out var existing) || edge.Weight > existing)
                weights[key] = edge.Weight;
        }
        edgeCount = weights.Count;

        if (edgeCount < 8)
        {
            _logger?.LogDebug(
                "Spine bypass for ns={Namespace}: only {EdgeCount} positive-relation edges.",
                ns, edgeCount);
            return null;
        }

        // CSR-style adjacency for fast matVec.
        int n = entryIds.Length;
        var rowStart = new int[n + 1];
        var colIdx = new int[edgeCount * 2];
        var vals = new float[edgeCount * 2];

        var degree = new float[n];
        foreach (var ((lo, hi), w) in weights)
        {
            degree[lo] += w;
            degree[hi] += w;
        }

        // Bucket edges by row to fill CSR. First count, then fill.
        var rowCount = new int[n];
        foreach (var ((lo, hi), _) in weights)
        {
            rowCount[lo]++;
            rowCount[hi]++;
        }
        int cursor = 0;
        for (int i = 0; i < n; i++) { rowStart[i] = cursor; cursor += rowCount[i]; }
        rowStart[n] = cursor;

        var fillCursor = new int[n];
        Array.Copy(rowStart, fillCursor, n);
        foreach (var ((lo, hi), w) in weights)
        {
            colIdx[fillCursor[lo]] = hi; vals[fillCursor[lo]] = w; fillCursor[lo]++;
            colIdx[fillCursor[hi]] = lo; vals[fillCursor[hi]] = w; fillCursor[hi]++;
        }

        // Inverse sqrt degree, used in M = D^(-1/2) W D^(-1/2).
        var invSqrtDeg = new float[n];
        for (int i = 0; i < n; i++)
            invSqrtDeg[i] = degree[i] > 0f ? 1f / MathF.Sqrt(degree[i]) : 0f;

        // (M x)[i] = invSqrtDeg[i] * sum over neighbors j: w_ij * invSqrtDeg[j] * x[j]
        void MatVec(ReadOnlySpan<float> x, Span<float> y)
        {
            for (int i = 0; i < n; i++)
            {
                float acc = 0f;
                int rs = rowStart[i];
                int re = rowStart[i + 1];
                float si = invSqrtDeg[i];
                for (int p = rs; p < re; p++)
                    acc += vals[p] * invSqrtDeg[colIdx[p]] * x[colIdx[p]];
                y[i] = si * acc;
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rng = new Random(unchecked((int)(graphRevision ^ ns.GetHashCode())));
        var (mEig, mVec) = RandomizedEigensolver.SolveTopK(n, topK, Oversample, PowerIterations, MatVec, rng);
        sw.Stop();

        // Convert eigenpairs of M to eigenpairs of L = I - M and sort ascending.
        // Eigenvalues of the normalized Laplacian must lie in [0, 2]; clamp small
        // negative numerical noise (typically the smallest eigenvalue, which is
        // exactly 0 in exact arithmetic for connected components) so callers can
        // safely apply MathF.Pow(lambda, alpha) for fractional-Laplacian filters
        // without producing NaN.
        var lEigsUnsorted = new float[mEig.Length];
        for (int j = 0; j < mEig.Length; j++)
        {
            float lambdaL = 1f - mEig[j];
            if (lambdaL < 0f) lambdaL = 0f;
            lEigsUnsorted[j] = lambdaL;
        }

        var order = new int[mEig.Length];
        for (int j = 0; j < mEig.Length; j++) order[j] = j;
        Array.Sort(order, (a, b) => lEigsUnsorted[a].CompareTo(lEigsUnsorted[b]));

        var lEigs = new float[mEig.Length];
        var lVecs = new float[n, mEig.Length];
        for (int j = 0; j < mEig.Length; j++)
        {
            int src = order[j];
            lEigs[j] = lEigsUnsorted[src];
            for (int i = 0; i < n; i++) lVecs[i, j] = mVec[i, src];
        }

        _logger?.LogInformation(
            "Spine: built basis for ns={Namespace} (n={Nodes}, edges={Edges}, k={TopK}) in {Ms}ms.",
            ns, n, edgeCount, lEigs.Length, sw.ElapsedMilliseconds);

        return new LaplacianBasis(ns, entryIds, lEigs, lVecs, edgeCount, graphRevision);
    }
}
