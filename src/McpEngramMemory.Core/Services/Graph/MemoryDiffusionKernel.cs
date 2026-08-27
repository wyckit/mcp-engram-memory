using System.Collections.Concurrent;
using McpEngramMemory.Core.Models;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Graph;

/// <summary>
/// Per-namespace cache and operator for diffusing per-entry signals through the
/// memory graph. Internally holds the top-K eigenbasis of the normalized
/// Laplacian; externally exposes <see cref="ApplySpectralFilter"/> as the
/// primary verb. Consumed today by spectral diffusion of decay debt
/// (LifecycleEngine) and reserved for future spectral retrieval and
/// sleep-consolidation operators.
///
/// What "diffusion" means here. Each memory entry has a position on the graph
/// (its node) and a scalar associated with it (decay debt, activation pressure,
/// retrieval relevance — whatever the caller needs to spread). Applying the
/// heat-kernel filter <c>exp(-tL)</c> to this scalar field moves it from each
/// node to its neighbors weighted by edge connectivity, exactly the way
/// activation spreads in classical cognitive-science models of associative memory.
///
/// Construction. For namespace <c>ns</c>:
/// 1. Snapshot entry ids in stable order from <see cref="CognitiveIndex.GetAllInNamespace(string)"/>.
/// 2. Snapshot the edge list from <see cref="KnowledgeGraph.GetAllEdges()"/>, filter
///    to edges whose endpoints are both in <c>ns</c> and whose relation is in
///    <see cref="PositiveRelations"/> (parent_child, cross_reference, similar_to,
///    elaborates, depends_on). The <c>contradicts</c> relation is excluded so the
///    weight matrix W stays non-negative and the Laplacian L = I - D^(-1/2) W D^(-1/2)
///    stays positive semi-definite (heat kernel exp(-tL) remains a contraction).
/// 3. Symmetrize: W[i,j] = max(w(i->j), w(j->i)).
/// 4. Structurally deflate isolated nodes: entries with no positive-relation
///    edges are excluded from the eigenproblem entirely (they would contribute
///    exactly-zero rows/columns to M, making it exactly rank-deficient — the
///    regime where randomized subspace iteration breaks down). Each isolated
///    node is its own connected component with Laplacian eigenvalue 0, so every
///    spectral filter treats it as identity (exp(-t·0) = 1); the filter's
///    out-of-basis pass-through in <see cref="ApplySpectralFilter"/> implements
///    exactly that. If the remaining linked core is smaller than
///    <see cref="MinimumNodesForSpectral"/>, the kernel bypasses entirely.
/// 5. Find the top-K largest eigenpairs (lambda_M, u) of M = D^(-1/2) W D^(-1/2)
///    over the linked subgraph via <see cref="RandomizedEigensolver.SolveTopK"/>;
///    convert to the smallest eigenpairs of L via lambda_L = 1 - lambda_M and
///    sort ascending.
///
/// Cache invalidation is revision-based: each cached basis records the
/// <see cref="KnowledgeGraph.Revision"/> at the time of computation; any subsequent
/// edge mutation increments the live revision, and the next <see cref="GetBasis"/>
/// call detects the divergence and recomputes. Recomputation runs synchronously
/// under a per-namespace lock — concurrent calls for the same namespace serialize,
/// but different namespaces compute independently.
/// </summary>
public class MemoryDiffusionKernel
{
    /// <summary>Edge relations that contribute positive weight to the Laplacian.</summary>
    public static readonly IReadOnlySet<string> PositiveRelations = new HashSet<string>
    {
        "parent_child", "cross_reference", "similar_to", "elaborates", "depends_on",
    };

    /// <summary>Default top-K. 96 covers the dominant low-frequency modes for typical namespaces.</summary>
    public const int DefaultTopK = 96;

    /// <summary>Below this node count, spectral methods give no benefit and the kernel is bypassed.</summary>
    public const int MinimumNodesForSpectral = 32;

    /// <summary>Minimum positive-relation edges required to construct a meaningful basis.</summary>
    public const int MinimumEdgesForSpectral = 8;

    /// <summary>Random sketch oversample (Halko-Martinsson-Tropp typical value).</summary>
    private const int Oversample = 10;

    /// <summary>Power iterations to align the sketch with the dominant subspace.</summary>
    private const int PowerIterations = 5;

    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ILogger<MemoryDiffusionKernel>? _logger;

    private readonly ConcurrentDictionary<string, DiffusionBasis> _cache = new();
    private readonly ConcurrentDictionary<string, object> _nsLocks = new();

    /// <summary>
    /// Negative cache: namespaces whose basis computation threw, keyed by the
    /// <see cref="KnowledgeGraph.Revision"/> at failure time. The eigensolver RNG
    /// is seeded from <c>graphRevision ^ ns.GetHashCode()</c>, so a failure is
    /// deterministic per (namespace, revision) — re-running the expensive
    /// eigensolve before the graph changes would repay the full cost for a
    /// guaranteed-identical failure. Instead <see cref="GetBasis"/> rethrows a
    /// cheap exception until the revision moves, which re-arms exactly one retry.
    /// </summary>
    private readonly ConcurrentDictionary<string, (long Revision, string Message)> _failedRevisions = new();

    public MemoryDiffusionKernel(
        CognitiveIndex index,
        KnowledgeGraph graph,
        ILogger<MemoryDiffusionKernel>? logger = null)
    {
        _index = index;
        _graph = graph;
        _logger = logger;
    }

    /// <summary>
    /// Return the top-K diffusion basis for <paramref name="ns"/>, recomputing if
    /// the cache is missing, stale (graph revision diverged), or has fewer
    /// eigenpairs than requested. Returns <c>null</c> if the namespace is too
    /// small or sparsely linked to qualify (see <see cref="MinimumNodesForSpectral"/>
    /// and <see cref="MinimumEdgesForSpectral"/>) — callers should fall back to
    /// non-spectral behavior in that case.
    ///
    /// Failure handling: if computation throws, the failure is negative-cached
    /// per graph revision and cheaply rethrown until the graph changes, keeping
    /// the failure visible to callers every cycle without repaying the
    /// eigensolve for a deterministic re-failure. Rethrowing (rather than
    /// returning <c>null</c>) is deliberate — <c>null</c> would be
    /// indistinguishable from a legitimate too-small-namespace bypass.
    /// </summary>
    public DiffusionBasis? GetBasis(string ns, int topK = DefaultTopK, string tenantId = "")
    {
        // Cache/lock/failure keys are the (tenant, ns) partition key so a tenant's basis never
        // collides with another's. For the legacy tenant "" the partition key is exactly ns, so
        // legacy cache keys are unchanged.
        string pk = NamespaceStore.PartitionKey(tenantId, ns);
        long currentRev = _graph.Revision;
        if (_cache.TryGetValue(pk, out var cached)
            && cached.GraphRevision == currentRev
            && (cached.TopK >= topK || cached.TopK >= cached.NodeCount))
        {
            // Either the cache has enough modes for the request, or it already
            // has the maximum possible (TopK was clamped to NodeCount). Either
            // way, no recomputation needed.
            return cached;
        }

        if (_failedRevisions.TryGetValue(pk, out var failed) && failed.Revision == currentRev)
            throw new InvalidOperationException(FailureMessage(ns, currentRev, failed.Message));

        var nsLock = _nsLocks.GetOrAdd(pk, _ => new object());
        lock (nsLock)
        {
            currentRev = _graph.Revision;
            if (_cache.TryGetValue(pk, out cached)
                && cached.GraphRevision == currentRev
                && cached.TopK >= topK)
            {
                return cached;
            }

            if (_failedRevisions.TryGetValue(pk, out failed) && failed.Revision == currentRev)
                throw new InvalidOperationException(FailureMessage(ns, currentRev, failed.Message));

            DiffusionBasis? built;
            try
            {
                built = ComputeBasis(ns, topK, currentRev, tenantId);
            }
            catch (Exception ex)
            {
                _failedRevisions[pk] = (currentRev, ex.Message);
                _logger?.LogWarning(ex,
                    "Diffusion basis computation failed for ns={Namespace} at revision {Revision}; caching failure until the graph changes.",
                    ns, currentRev);
                throw;
            }

            _failedRevisions.TryRemove(pk, out _);
            if (built is not null)
                _cache[pk] = built;
            else
                _cache.TryRemove(pk, out _);
            return built;
        }
    }

    private static string FailureMessage(string ns, long rev, string inner) =>
        $"Diffusion basis computation for namespace '{ns}' previously failed at graph revision {rev} and the graph has not changed since: {inner}";

    /// <summary>
    /// Apply a per-mode spectral filter to a per-entry signal — the kernel's
    /// primary verb. Entries not present in the cached basis pass through
    /// unchanged (e.g., isolated entries deflated out of the eigenproblem — for
    /// which identity is the exact filter value, since a singleton component has
    /// lambda_L = 0 — or entries added after basis computation, on a
    /// stale-but-not-yet-rebuilt basis).
    ///
    /// Mechanism: project signal into the basis (sigma_hat[k] = sum_i U[i,k] · signal[i]),
    /// apply <paramref name="modeFilter"/> to each mode, project back. For diffusion of
    /// decay debt with subdiffusive exponent alpha and step dt, pass:
    /// <c>lambda =&gt; MathF.Exp(-MathF.Pow(lambda, alpha) * dt)</c>.
    /// </summary>
    public IReadOnlyDictionary<string, float> ApplySpectralFilter(
        string ns,
        IReadOnlyDictionary<string, float> signal,
        Func<float, float> modeFilter,
        string tenantId = "")
    {
        var basis = GetBasis(ns, DefaultTopK, tenantId);
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
    public DiffusionStats? GetStats(string ns, string tenantId = "")
    {
        var basis = GetBasis(ns, DefaultTopK, tenantId);
        if (basis is null) return null;
        bool stale = basis.GraphRevision != _graph.Revision;
        return new DiffusionStats(
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

    /// <summary>Drop the cached basis (and any negative-cached failure) for a namespace. Next <see cref="GetBasis"/> will recompute.</summary>
    public void Invalidate(string ns, string tenantId = "")
    {
        string pk = NamespaceStore.PartitionKey(tenantId, ns);
        _cache.TryRemove(pk, out _);
        _failedRevisions.TryRemove(pk, out _);
    }

    // ── internals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the eigenbasis for <paramref name="ns"/>, or <c>null</c> when the
    /// namespace doesn't qualify. Virtual purely as a test seam so fault-isolation
    /// tests can inject deterministic failures — not intended as an extension point.
    /// </summary>
    protected virtual DiffusionBasis? ComputeBasis(string ns, int topK, long graphRevision, string tenantId = "")
    {
        var entries = _index.GetAllInNamespace(ns, tenantId);
        if (entries.Count < MinimumNodesForSpectral)
        {
            _logger?.LogDebug(
                "Diffusion kernel bypass for ns={Namespace}: {Count} nodes < {Min} minimum.",
                ns, entries.Count, MinimumNodesForSpectral);
            return null;
        }

        var entryIds = entries.Select(e => e.Id).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var indexOf = new Dictionary<string, int>(entryIds.Length);
        for (int i = 0; i < entryIds.Length; i++) indexOf[entryIds[i]] = i;

        // Build symmetric sparse adjacency restricted to this namespace and positive relations.
        // First pass: collect candidate edge weights keyed by ordered (i,j) with i<j.
        var allEdges = _graph.GetAllEdges(tenantId);
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

        if (edgeCount < MinimumEdgesForSpectral)
        {
            _logger?.LogDebug(
                "Diffusion kernel bypass for ns={Namespace}: only {EdgeCount} positive-relation edges (< {Min}).",
                ns, edgeCount, MinimumEdgesForSpectral);
            return null;
        }

        // ── Structural deflation of isolated nodes ────────────────────────────
        // Entries with no positive-relation edges contribute exactly-zero
        // rows/columns to M = D^(-1/2) W D^(-1/2), making the operator exactly
        // rank-deficient — the regime where the randomized eigensolver's panel
        // collapses and orthonormality cannot be maintained in float32.
        // Mathematically, each isolated node is its own connected component with
        // Laplacian eigenvalue 0, so every spectral filter is identity on it
        // (exp(-t·0) = 1). We therefore exclude isolated nodes from the
        // eigenproblem and let ApplySpectralFilter's out-of-basis pass-through
        // handle them — exact and numerically safe.
        var nonIsolated = new bool[entryIds.Length];
        foreach (var ((lo, hi), _) in weights)
        {
            nonIsolated[lo] = true;
            nonIsolated[hi] = true;
        }
        int compactCount = 0;
        for (int i = 0; i < nonIsolated.Length; i++)
            if (nonIsolated[i]) compactCount++;
        int isolatedCount = entryIds.Length - compactCount;

        if (compactCount < MinimumNodesForSpectral)
        {
            _logger?.LogDebug(
                "Diffusion kernel bypass for ns={Namespace}: {Count} linked nodes < {Min} minimum ({Total} total, {Isolated} isolated).",
                ns, compactCount, MinimumNodesForSpectral, entryIds.Length, isolatedCount);
            return null;
        }

        // Compact index map over non-isolated nodes only, preserving relative
        // (ordinal) order so basis construction stays deterministic.
        var compactOf = new int[entryIds.Length];
        var basisEntryIds = new string[compactCount];
        int next = 0;
        for (int i = 0; i < entryIds.Length; i++)
        {
            if (nonIsolated[i])
            {
                compactOf[i] = next;
                basisEntryIds[next] = entryIds[i];
                next++;
            }
            else
            {
                compactOf[i] = -1;
            }
        }

        // Remap edge weights into compact index space. Order preservation keeps
        // lo < hi invariant intact after remapping.
        var compactWeights = new Dictionary<(int Lo, int Hi), float>(weights.Count);
        foreach (var ((lo, hi), w) in weights)
            compactWeights[(compactOf[lo], compactOf[hi])] = w;

        // CSR-style adjacency for fast matVec, at compact (linked-only) dimension.
        int n = compactCount;
        var rowStart = new int[n + 1];
        var colIdx = new int[edgeCount * 2];
        var vals = new float[edgeCount * 2];

        var degree = new float[n];
        foreach (var ((lo, hi), w) in compactWeights)
        {
            degree[lo] += w;
            degree[hi] += w;
        }

        // Bucket edges by row to fill CSR. First count, then fill.
        var rowCount = new int[n];
        foreach (var ((lo, hi), _) in compactWeights)
        {
            rowCount[lo]++;
            rowCount[hi]++;
        }
        int cursor = 0;
        for (int i = 0; i < n; i++) { rowStart[i] = cursor; cursor += rowCount[i]; }
        rowStart[n] = cursor;

        var fillCursor = new int[n];
        Array.Copy(rowStart, fillCursor, n);
        foreach (var ((lo, hi), w) in compactWeights)
        {
            colIdx[fillCursor[lo]] = hi; vals[fillCursor[lo]] = w; fillCursor[lo]++;
            colIdx[fillCursor[hi]] = lo; vals[fillCursor[hi]] = w; fillCursor[hi]++;
        }

        // Inverse sqrt degree, used in M = D^(-1/2) W D^(-1/2). Isolated nodes
        // were deflated above, so every remaining node has at least one
        // positive-weight edge and degree > 0 by construction — the 0 sentinel
        // (and the exactly-zero rows it produced) can no longer occur.
        var invSqrtDeg = new float[n];
        for (int i = 0; i < n; i++)
        {
            System.Diagnostics.Debug.Assert(degree[i] > 0f,
                "Deflation invariant violated: linked node with zero degree.");
            invSqrtDeg[i] = 1f / MathF.Sqrt(degree[i]);
        }

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
            "Diffusion kernel: built basis for ns={Namespace} (n={Nodes} linked of {Total} total, {Isolated} isolated deflated, edges={Edges}, k={TopK}) in {Ms}ms.",
            ns, n, entryIds.Length, isolatedCount, edgeCount, lEigs.Length, sw.ElapsedMilliseconds);

        return new DiffusionBasis(ns, basisEntryIds, lEigs, lVecs, edgeCount, graphRevision);
    }
}
