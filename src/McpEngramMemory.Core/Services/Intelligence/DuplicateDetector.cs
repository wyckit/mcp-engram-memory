using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Retrieval;

namespace McpEngramMemory.Core.Services.Intelligence;

/// <summary>
/// One turn of the pairwise scan, expressed over ANCHORS — the outer index of the triangular
/// (i, j &gt; i) walk, so anchor <c>i</c> stands for every pair whose lower endpoint is candidate
/// <c>i</c>.
///
/// Anchors are the unit because they are the unit that makes resumption honest. A scan that stopped
/// mid-anchor would have to remember where inside the row it stopped for the rest of that row ever
/// to be examined again; a scan that stops on an anchor boundary needs one integer, and the pairs
/// of one anchor are always examined together or not at all. Rotating the anchor order changes
/// nothing about WHICH pairs exist — a pair belongs to exactly one anchor whatever order the
/// anchors are visited in — so successive windows tile the whole pair space and every pair is
/// reached within <c>ceil(candidates / MaxAnchors)</c> scans.
///
/// The anchor space is the CANDIDATE list on every path, including the spectral one, whose own loop
/// runs over the subset it kept. A caller advancing a cursor cannot see which path it got, so two
/// paths counting anchors differently would make that cursor step over pairs it never examined.
/// </summary>
/// <param name="StartAnchor">First anchor to examine; reduced modulo the candidate count, so a
/// cursor may be carried across scans whose candidate count changed without going out of range.</param>
/// <param name="MaxAnchors">How many anchors this window covers before stopping. One anchor is the
/// smallest indivisible unit, so a window never examines fewer than a single full row.</param>
internal readonly record struct PairScanWindow(int StartAnchor, int MaxAnchors)
{
    /// <summary>Every anchor, from the first — what a caller that is not resuming anything wants.</summary>
    internal static PairScanWindow Full => new(0, int.MaxValue);
}

/// <summary>
/// Detects near-duplicate entries by pairwise cosine similarity.
/// Stateless — operates on data snapshots passed by the caller.
/// </summary>
public sealed class DuplicateDetector
{
    /// <summary>
    /// Find near-duplicates for a single entry within a namespace (O(N) scan).
    /// </summary>
    public IReadOnlyList<(string IdA, string IdB, float Similarity)> FindDuplicatesForEntry(
        string entryId,
        (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)? target,
        IEnumerable<KeyValuePair<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>> nsEntries,
        float threshold = 0.95f)
    {
        if (target is null)
            return Array.Empty<(string, string, float)>();

        var t = target.Value;
        if (t.Norm == 0f)
            return Array.Empty<(string, string, float)>();

        var duplicates = new List<(string IdA, string IdB, float Similarity)>();
        foreach (var (id, (entry, norm, _)) in nsEntries)
        {
            if (id == entryId || norm == 0f) continue;
            if (entry.Vector.Length != t.Entry.Vector.Length) continue;

            float dot = VectorMath.Dot(t.Entry.Vector, entry.Vector);
            float sim = dot / (t.Norm * norm);
            if (sim >= threshold)
                duplicates.Add((entryId, id, sim));
        }

        duplicates.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
        return duplicates;
    }

    /// <summary>
    /// Find near-duplicate entries by pairwise cosine similarity scan, highest similarity first.
    /// Above <see cref="LowRankPivot"/> candidates, switches to a spectral
    /// pre-filter (project to a K-dim subspace, scan in projection space at a
    /// widened threshold, confirm survivors with full-FP32 cosine) to amortize
    /// the O(N^2) cost. Below the pivot, the original direct pairwise scan is
    /// preserved — its cost is already bounded.
    ///
    /// <paramref name="maxResults"/> bounds the OUTPUT, and it is applied in scan order before the
    /// sort, so it is a prefix of what was found rather than the globally best pairs. A caller that
    /// filters what it is handed must take <see cref="StreamDuplicates"/> instead — see there for
    /// why a bounded result cannot serve one.
    /// </summary>
    public IReadOnlyList<(string IdA, string IdB, float Similarity)> FindDuplicates(
        IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
        float threshold = 0.95f,
        int maxResults = 100)
    {
        if (maxResults <= 0)
            return Array.Empty<(string, string, float)>();

        // A bounded prefix of the same stream, so there is exactly one pairwise implementation and a
        // caller that filters as it goes cannot drift away from what this returns. Abandoning the
        // enumeration at the bound abandons the comparisons behind it, which is the early exit the
        // old nested-loop form got from testing the bound in its loop conditions.
        var duplicates = new List<(string IdA, string IdB, float Similarity)>();
        foreach (var pair in StreamDuplicates(candidates, threshold, PairScanWindow.Full, CancellationToken.None))
        {
            duplicates.Add(pair);
            if (duplicates.Count >= maxResults) break;
        }
        duplicates.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
        return duplicates;
    }

    /// <summary>
    /// Every above-threshold pair inside <paramref name="window"/>, yielded lazily in scan order and
    /// with no output bound.
    ///
    /// It exists because a bound on the OUTPUT is not a bound a filtering caller can use. A caller
    /// that discards some of what it is handed — auto-link discards pairs that already carry an edge
    /// and pairs whose endpoint is unattributable — has no way to ask a fixed-size result for "the
    /// next one after those": the window it was given is the window it keeps getting, and the pairs
    /// behind that window are never reached on any number of deterministic rescans. Streaming lets
    /// the caller keep pulling until ITS OWN quota is met, and pay for one pairwise pass to do it,
    /// rather than re-running the quadratic scan once per widened guess.
    ///
    /// EACH UNORDERED PAIR IS YIELDED EXACTLY ONCE PER FULL ROTATION, on both paths, and a caller
    /// may rely on it: the walks below are triangular (the inner index runs strictly after the
    /// anchor), so a pair is produced at exactly one position, and entry ids within one
    /// tenant+namespace partition are dictionary keys and so distinct. A consumer therefore needs no
    /// scan-wide set to make a pair unique, and must not build one — such a set is sized by pairs
    /// walked, which is quadratic in the namespace.
    ///
    /// <paramref name="window"/> is what keeps a steady-state namespace — every neighbour already
    /// linked, so nothing the scan finds is usable — from re-walking the entire pair space on every
    /// six-hourly sweep. It bounds COMPARISONS rather than output, and successive windows tile the
    /// anchor space, so bounding one scan never hides a pair from every scan.
    ///
    /// Deferred: no PAIR is compared until the result is enumerated, and enumeration abandoned
    /// partway abandons the remaining comparisons — that is what keeps the bounded caller above at
    /// its old cost. Above the pivot the subspace set-up still runs eagerly, deliberately; see
    /// <see cref="StreamSpectralPrefiltered"/>.
    /// </summary>
    internal IEnumerable<(string IdA, string IdB, float Similarity)> StreamDuplicates(
        IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
        float threshold,
        PairScanWindow window,
        CancellationToken cancellationToken)
    {
        if (candidates.Count < LowRankPivot)
            return StreamDirectPairwise(candidates, threshold, window, cancellationToken);
        return StreamSpectralPrefiltered(candidates, threshold, window, cancellationToken);
    }

    /// <summary>Threshold above which two-pass spectral filtering replaces direct O(N^2) scan.</summary>
    public const int LowRankPivot = 256;

    /// <summary>
    /// Recall safety margin: the projection-space threshold is the requested
    /// threshold minus this value, so true duplicates whose projection cosine
    /// is slightly attenuated by subspace truncation still survive the filter.
    /// </summary>
    public const float ProjectionThresholdSlack = 0.10f;

    /// <summary>
    /// The anchors one window covers, in visit order. Rotation reorders anchors and never filters
    /// them, which is why a partial window can be resumed without losing a pair: anchor <c>i</c>
    /// owns every pair (i, j &gt; i) whenever it is visited, so tiling the anchor space across scans
    /// tiles the pair space exactly once.
    /// </summary>
    private static IEnumerable<int> WindowAnchors(int count, PairScanWindow window)
    {
        if (count <= 0) yield break;
        int rows = Math.Min(window.MaxAnchors, count);
        int start = ((window.StartAnchor % count) + count) % count;
        for (int r = 0; r < rows; r++)
        {
            int i = start + r;
            // start and r are both below count, so one subtraction is the whole modulo.
            if (i >= count) i -= count;
            yield return i;
        }
    }

    private static IEnumerable<(string IdA, string IdB, float Similarity)> StreamDirectPairwise(
        IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
        float threshold,
        PairScanWindow window,
        CancellationToken cancellationToken)
    {
        int n = candidates.Count;
        foreach (int i in WindowAnchors(n, window))
        {
            // Once per anchor and not per pair: an anchor is at most n comparisons, sub-millisecond
            // at the entry cap, so this is responsive enough for a shutdown while keeping a volatile
            // read out of the innermost loop of the hottest thing this class does.
            if (cancellationToken.IsCancellationRequested) yield break;

            var a = candidates[i];
            if (a.Norm == 0f) continue;
            for (int j = i + 1; j < n; j++)
            {
                var b = candidates[j];
                if (b.Norm == 0f) continue;
                if (a.Entry.Vector.Length != b.Entry.Vector.Length) continue;

                float dot = VectorMath.Dot(a.Entry.Vector, b.Entry.Vector);
                float sim = dot / (a.Norm * b.Norm);

                if (sim >= threshold)
                    yield return (a.Entry.Id, b.Entry.Id, sim);
            }
        }
    }

    /// <summary>
    /// The subspace set-up is eager and only the pair scan is deferred: the fallbacks below decide
    /// WHICH stream a caller gets, and a decision hidden behind first-enumeration would make an
    /// unenumerated result silently different from an enumerated one.
    ///
    /// The set-up is charged to the whole scan rather than to the window, and it is the one cost a
    /// window does not bound. It is linear in the candidates rather than quadratic in them, so it is
    /// not the cost windowing exists to contain.
    /// </summary>
    private static IEnumerable<(string IdA, string IdB, float Similarity)> StreamSpectralPrefiltered(
        IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
        float threshold,
        PairScanWindow window,
        CancellationToken cancellationToken)
    {
        // Skip embeddings of inconsistent dimension or zero norm — fall back to
        // direct on whatever's left if too many drop out.
        var keep = new List<int>(candidates.Count);
        int firstLen = -1;
        for (int idx = 0; idx < candidates.Count; idx++)
        {
            var (e, n, _) = candidates[idx];
            if (n == 0f || e.Vector.Length == 0) continue;
            if (firstLen < 0) firstLen = e.Vector.Length;
            else if (e.Vector.Length != firstLen) continue;
            keep.Add(idx);
        }
        if (keep.Count < LowRankPivot)
            return StreamDirectPairwise(candidates, threshold, window, cancellationToken);

        // Build subspace from the kept embeddings, in their original order.
        var embeddings = new float[keep.Count][];
        for (int i = 0; i < keep.Count; i++) embeddings[i] = candidates[keep[i]].Entry.Vector;
        var subspace = EmbeddingSubspace.Build(embeddings, EmbeddingSubspace.DefaultTopK);
        if (subspace is null) return StreamDirectPairwise(candidates, threshold, window, cancellationToken);

        // Projection-space norms are *not* the original norms (truncation drops magnitude
        // orthogonal to col(V)), so they are computed here rather than reused.
        var projNorms = new float[keep.Count];
        for (int i = 0; i < keep.Count; i++)
        {
            float ns = 0f;
            var p = subspace.Projections[i];
            for (int k = 0; k < p.Length; k++) ns += p[k] * p[k];
            projNorms[i] = MathF.Sqrt(ns);
        }

        // Anchors are candidate indices on every path (see PairScanWindow), so a dropped candidate
        // keeps its place and a cursor stepping through anchors means the same thing whichever path
        // produced it. A dropped anchor costs one array read and owns no pairs.
        var keepPos = new int[candidates.Count];
        Array.Fill(keepPos, -1);
        for (int p = 0; p < keep.Count; p++) keepPos[keep[p]] = p;

        return StreamProjectionSurvivors(candidates, keep, keepPos, subspace, projNorms, threshold,
            window, cancellationToken);
    }

    /// <summary>
    /// The widened projection-space filter and the full-FP32 confirmation, interleaved.
    ///
    /// They were two passes with a list of survivor indices between them, and that list was the one
    /// unbounded allocation on this path: it is sized by how many pairs clear the LOOSE threshold,
    /// which no output bound reaches. Confirming each survivor as it is found yields the same pairs
    /// in the same order and holds nothing between the passes.
    /// </summary>
    private static IEnumerable<(string IdA, string IdB, float Similarity)> StreamProjectionSurvivors(
        IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
        List<int> keep, int[] keepPos, SubspaceProjection subspace, float[] projNorms, float threshold,
        PairScanWindow window, CancellationToken cancellationToken)
    {
        float looseThreshold = threshold - ProjectionThresholdSlack;
        foreach (int i in WindowAnchors(candidates.Count, window))
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            int pi = keepPos[i];
            if (pi < 0 || projNorms[pi] == 0f) continue;
            var vi = subspace.Projections[pi];
            // keep is ascending, so a later keep position is a later candidate index: walking
            // forward from pi covers the same triangular half the direct path covers.
            for (int pj = pi + 1; pj < keep.Count; pj++)
            {
                if (projNorms[pj] == 0f) continue;
                var vj = subspace.Projections[pj];
                float projDot = 0f;
                for (int k = 0; k < vi.Length; k++) projDot += vi[k] * vj[k];
                if (projDot / (projNorms[pi] * projNorms[pj]) < looseThreshold) continue;

                var a = candidates[i];
                var b = candidates[keep[pj]];
                float sim = VectorMath.Dot(a.Entry.Vector, b.Entry.Vector) / (a.Norm * b.Norm);
                if (sim >= threshold)
                    yield return (a.Entry.Id, b.Entry.Id, sim);
            }
        }
    }
}
