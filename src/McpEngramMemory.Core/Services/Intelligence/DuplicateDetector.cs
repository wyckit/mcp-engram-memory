using System.Numerics;
using System.Runtime.InteropServices;
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
/// Progress from the pairwise walk, reported once after a complete anchor row. The callback is
/// deliberately outside the inner pair loop: it adds no per-pair branch or allocation, while still
/// making cancellation accounting exact because cancellation is observed only between anchors.
/// </summary>
/// <param name="Anchor">Candidate index whose complete triangular row was visited.</param>
/// <param name="PairSlotsCompleted">Logical pair slots in that row, including slots that failed a
/// norm/dimension check or either similarity threshold.</param>
/// <param name="Spectral">True when the row ran through the projection prefilter; false for the
/// direct path, including spectral setup fallbacks.</param>
internal readonly record struct PairScanProgress(int Anchor, long PairSlotsCompleted, bool Spectral);

/// <summary>
/// What one spectral scan COST, as opposed to what it returned.
///
/// The spectral path exists for exactly one reason: a 64-dim projection dot is cheaper than the
/// full-dimension FP32 cosine, so gating on the projection buys back the quadratic. That bargain
/// is invisible in the result — a healthy prefilter and a prefilter that has stopped filtering
/// yield the SAME pairs, because the full-fidelity confirmation behind the gate makes the final
/// decision either way. Only the number of confirmations differs, and nothing in the result, in
/// the graph, or in <c>AutoLinkScanProbe</c> (pairs examined, retention, edges materialized) moves
/// when it changes. That is why the innermost loop of this subsystem could be lowered, or
/// mis-lowered, with no property any test could state.
///
/// The RATIO of the two counters below is the prefilter's selectivity, and it is what moves when
/// the projection dot is computed wrongly. A kernel that OVER-counts (a tail loop re-walking
/// elements the main loop already consumed) inflates every dot, every pair clears the widened gate,
/// and the ratio goes to 1 while the yielded pairs stay perfectly correct — visible here and
/// nowhere else. A kernel that UNDER-counts (a stride that skips a block) shrinks every dot toward
/// zero and the ratio goes to 0; that one also costs recall, so it is caught by the pair-set
/// assertion as well. Only the first failure is invisible without these counters, and it is the
/// failure that gives the whole dimensional reduction back.
/// </summary>
/// <param name="ProjectionDots">Pairs this window actually compared in projection space — the
/// cheap side of the bargain.</param>
/// <param name="ConfirmationDots">The subset that cleared the widened gate and paid for a
/// full-dimension FP32 cosine — the expensive side the gate exists to suppress.</param>
internal readonly record struct ProjectionScanProbe(long ProjectionDots, long ConfirmationDots);

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
    /// filters what it is handed must take <c>StreamDuplicates</c> instead — see there for
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
        => StreamDuplicates(candidates, threshold, window, cancellationToken,
            onAnchorCompleted: null, onProjectionProbe: null);

    /// <summary>
    /// The production auto-link stream, with one progress notification per completed anchor.
    /// </summary>
    internal IEnumerable<(string IdA, string IdB, float Similarity)> StreamDuplicates(
        IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
        float threshold,
        PairScanWindow window,
        CancellationToken cancellationToken,
        Action<PairScanProgress>? onAnchorCompleted)
        => StreamDuplicates(candidates, threshold, window, cancellationToken,
            onAnchorCompleted, onProjectionProbe: null);

    /// <summary>
    /// The same stream as the four-argument overload above — identical pairs, identical order —
    /// plus the cost seam.
    ///
    /// <paramref name="onProjectionProbe"/> is handed one <see cref="ProjectionScanProbe"/> when the
    /// spectral enumeration ends — including when a bounded caller abandons it early, because the
    /// counters describe the work actually done rather than the work a full pass would have done.
    /// The direct path never reports one: it has no projection stage to measure, and a probe that
    /// silently reported zeros for it would read as "the prefilter suppressed everything".
    ///
    /// The extra parameter is REQUIRED rather than optional on purpose. The four-argument overload
    /// above is bound as a method group to <c>AutoLinkScanner.PairStream</c>, and an optional
    /// parameter is not filled in by a method group conversion — making it optional would break
    /// that binding rather than extending it.
    /// </summary>
    internal IEnumerable<(string IdA, string IdB, float Similarity)> StreamDuplicates(
        IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
        float threshold,
        PairScanWindow window,
        CancellationToken cancellationToken,
        Action<ProjectionScanProbe>? onProjectionProbe)
        => StreamDuplicates(candidates, threshold, window, cancellationToken,
            onAnchorCompleted: null, onProjectionProbe);

    private IEnumerable<(string IdA, string IdB, float Similarity)> StreamDuplicates(
        IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
        float threshold,
        PairScanWindow window,
        CancellationToken cancellationToken,
        Action<PairScanProgress>? onAnchorCompleted,
        Action<ProjectionScanProbe>? onProjectionProbe)
    {
        if (candidates.Count < LowRankPivot)
            return StreamDirectPairwise(candidates, threshold, window, cancellationToken, onAnchorCompleted);
        return StreamSpectralPrefiltered(candidates, threshold, window, cancellationToken,
            onAnchorCompleted, onProjectionProbe);
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
        CancellationToken cancellationToken,
        Action<PairScanProgress>? onAnchorCompleted)
    {
        int n = candidates.Count;
        foreach (int i in WindowAnchors(n, window))
        {
            // Once per anchor and not per pair: an anchor is at most n comparisons, sub-millisecond
            // at the entry cap, so this is responsive enough for a shutdown while keeping a volatile
            // read out of the innermost loop of the hottest thing this class does.
            if (cancellationToken.IsCancellationRequested) yield break;

            var a = candidates[i];
            if (a.Norm != 0f)
            {
                // candidates[j] below is an interface-indexer read per pair, and it stays one. This
                // path is chosen only under LowRankPivot (256 candidates, <32k pairs) or when the
                // subspace could not be built at all, its per-pair work is already a SIMD
                // full-dimension VectorMath.Dot that dwarfs one dispatch, and materializing the list
                // into an array would be an O(N) copy of a three-field tuple on the one path whose
                // justification is that its cost is already bounded. The dispatch that mattered was
                // in the spectral inner loop, where the arithmetic beside it is 64-dimensional.
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

            // Cancellation is checked only before the next anchor, so reaching here attests to the
            // complete logical row, including any suffix that produced no above-threshold yield.
            onAnchorCompleted?.Invoke(new PairScanProgress(i, n - 1L - i, Spectral: false));
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
        CancellationToken cancellationToken,
        Action<PairScanProgress>? onAnchorCompleted,
        Action<ProjectionScanProbe>? onProjectionProbe)
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
            return StreamDirectPairwise(candidates, threshold, window, cancellationToken, onAnchorCompleted);

        // Build subspace from the kept embeddings, in their original order.
        var embeddings = new float[keep.Count][];
        for (int i = 0; i < keep.Count; i++) embeddings[i] = candidates[keep[i]].Entry.Vector;
        var subspace = EmbeddingSubspace.Build(embeddings, EmbeddingSubspace.DefaultTopK);
        if (subspace is null)
            return StreamDirectPairwise(candidates, threshold, window, cancellationToken, onAnchorCompleted);

        // THE CONCRETE ROWS, RESOLVED ONCE PER SCAN AND NEVER PER PAIR.
        //
        // SubspaceProjection.Projections is declared IReadOnlyList<float[]> over the float[][] that
        // EmbeddingSubspace builds, and an interface indexer is a dispatch the JIT cannot fold into
        // an array load. The loop below this one reads a row per PAIR — ~18M reads in one default
        // background window over a 10k-entry namespace — so paying the dispatch there is paying it
        // 18M times for a value whose concrete type never changes within a scan.
        //
        // The cast succeeds on every value this type can hold today, since the only constructor
        // takes float[][]. The fallback is not dead defensiveness: it keeps a future
        // SubspaceProjection that returned some other IReadOnlyList from making this path WRONG
        // rather than merely slower, and it is linear set-up cost charged to the scan, not per-pair
        // cost charged to the window.
        float[][] projections = subspace.Projections as float[][] ?? subspace.Projections.ToArray();

        // Projection-space norms are *not* the original norms (truncation drops magnitude
        // orthogonal to col(V)), so they are computed here rather than reused. Same kernel as the
        // pair loop below, so the norm and the dot that divides by it are associated identically.
        var projNorms = new float[keep.Count];
        for (int i = 0; i < keep.Count; i++)
        {
            var p = projections[i];
            projNorms[i] = MathF.Sqrt(ProjectionDot(p, p));
        }

        // Anchors are candidate indices on every path (see PairScanWindow), so a dropped candidate
        // keeps its place and a cursor stepping through anchors means the same thing whichever path
        // produced it. A dropped anchor resolves its logical row during this setup and costs no
        // projection dots in the quadratic walk.
        var keepPos = new int[candidates.Count];
        Array.Fill(keepPos, -1);
        for (int p = 0; p < keep.Count; p++) keepPos[keep[p]] = p;

        return StreamProjectionSurvivors(candidates, keep, keepPos, projections, projNorms, threshold,
            window, cancellationToken, onAnchorCompleted, onProjectionProbe);
    }

    /// <summary>
    /// The widened projection-space filter and the full-FP32 confirmation, interleaved.
    ///
    /// They were two passes with a list of survivor indices between them, and that list was the one
    /// unbounded allocation on this path: it is sized by how many pairs clear the LOOSE threshold,
    /// which no output bound reaches. Confirming each survivor as it is found yields the same pairs
    /// in the same order and holds nothing between the passes.
    ///
    /// THIS IS THE INNERMOST LOOP OF THE SUBSYSTEM — on the order of 18 million iterations in one
    /// default background window over a 10k-entry namespace. Everything loop-invariant is hoisted
    /// out of it in the source rather than left to the JIT, because the one hoist that mattered is
    /// one the JIT could NOT do: the projection rows arrive as <c>float[][]</c> (resolved once at
    /// the call site) instead of through an <c>IReadOnlyList</c> indexer, which is a dispatch no
    /// amount of loop analysis folds into an array load.
    /// </summary>
    private static IEnumerable<(string IdA, string IdB, float Similarity)> StreamProjectionSurvivors(
        IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
        List<int> keep, int[] keepPos, float[][] projections, float[] projNorms, float threshold,
        PairScanWindow window, CancellationToken cancellationToken,
        Action<PairScanProgress>? onAnchorCompleted,
        Action<ProjectionScanProbe>? onProjectionProbe)
    {
        long projectionDots = 0;
        long confirmationDots = 0;
        try
        {
            float looseThreshold = threshold - ProjectionThresholdSlack;
            int keepCount = keep.Count;
            foreach (int i in WindowAnchors(candidates.Count, window))
            {
                if (cancellationToken.IsCancellationRequested) yield break;

                int pi = keepPos[i];
                if (pi >= 0 && projNorms[pi] != 0f)
                {
                    var vi = projections[pi];

                    // Hoisted above the inner loop because it does not depend on pj. It used to be
                    // read per SURVIVOR, through the IReadOnlyList indexer, inside the loop body.
                    var a = candidates[i];
                    float normI = projNorms[pi];

                    // keep is ascending, so a later keep position is a later candidate index:
                    // walking forward from pi covers the same triangular half the direct path.
                    for (int pj = pi + 1; pj < keepCount; pj++)
                    {
                        float normJ = projNorms[pj];
                        if (normJ == 0f) continue;

                        projectionDots++;
                        if (ProjectionDot(vi, projections[pj]) / (normI * normJ) < looseThreshold) continue;

                        confirmationDots++;
                        var b = candidates[keep[pj]];
                        float sim = VectorMath.Dot(a.Entry.Vector, b.Entry.Vector) / (a.Norm * b.Norm);
                        if (sim >= threshold)
                            yield return (a.Entry.Id, b.Entry.Id, sim);
                    }
                }

                onAnchorCompleted?.Invoke(new PairScanProgress(
                    i, candidates.Count - 1L - i, Spectral: true));
            }
        }
        finally
        {
            // In a finally so an abandoned enumeration still reports. FindDuplicates stops at its
            // output bound and disposes the enumerator mid-scan; a probe that only fired on natural
            // completion would report nothing for exactly the caller whose cost is most bounded.
            onProjectionProbe?.Invoke(new ProjectionScanProbe(projectionDots, confirmationDots));
        }
    }

    /// <summary>
    /// The projection-space dot product — the single hottest line in this subsystem.
    ///
    /// It runs once per candidate PAIR inside a window: a default background sweep over a
    /// 10,000-entry namespace covers 2,000 anchors, which is on the order of 18 million calls in
    /// one pass, at a six-hourly cadence, on a job nobody watches. The scalar form this replaces
    /// accumulated into one float with a serial FP-add chain — at roughly four cycles of add
    /// latency and K=64 elements that is ~256 cycles per pair with no instruction-level parallelism
    /// available — while the full-dimension confirmation two lines below it was already SIMD. The
    /// 6x dimensional reduction that justifies <see cref="LowRankPivot"/> and the randomized-SVD
    /// set-up was being handed straight back by the loop that exists to collect it.
    ///
    /// FOUR INDEPENDENT ACCUMULATORS, not one, and no per-chunk horizontal reduce: the point of the
    /// change is to break the dependency chain, and <c>Vector.Dot</c> per chunk (what
    /// <see cref="VectorMath.Dot"/> does) reduces horizontally every chunk and so rebuilds it. One
    /// reduce at the end is the whole cost of the reduction.
    ///
    /// THE RESULT IS NOT BIT-IDENTICAL to the scalar form, and it must not be described as though
    /// it were: vectorizing re-associates the additions, so the last bits differ, and they differ
    /// by SIMD width across hosts. That is safe here for a stated reason rather than by hope — this
    /// value feeds a gate deliberately WIDENED by <see cref="ProjectionThresholdSlack"/> (0.10) to
    /// absorb subspace-truncation error many orders of magnitude larger than an ULP, and the gate
    /// admits nothing on its own: every survivor is re-decided by a full-dimension FP32 cosine
    /// against the unmodified threshold. <see cref="VectorMath.Dot"/>, which computes that
    /// confirming cosine, already carries the same width-dependent association.
    ///
    /// The length equality is tested here rather than asserted by the caller so this stays safe to
    /// call on its own: it is what makes the unchecked loads below provably in range, and it is one
    /// perfectly-predicted branch per pair against ~25 cycles of vector work. It is the ONLY
    /// argument check, deliberately — the same line dereferences both arrays, so a null argument
    /// still fails immediately, and an explicit null guard would add two more branches per pair to
    /// an internal kernel whose callers are all in this file.
    /// </summary>
    internal static float ProjectionDot(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException(
                $"Projection vectors must share a length; got {a.Length} and {b.Length}.", nameof(b));

        int n = a.Length;
        int i = 0;
        float sum = 0f;

        int width = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && n >= width)
        {
            // Unchecked loads: n >= width is established, every offset below is bounded by the loop
            // conditions, and the length equality above makes the same offsets valid in b. This is
            // the bounds check the old form could not elide at all, because it indexed vj by
            // vi.Length rather than by vj's own.
            ref float ra = ref MemoryMarshal.GetArrayDataReference(a);
            ref float rb = ref MemoryMarshal.GetArrayDataReference(b);

            var acc0 = Vector<float>.Zero;
            var acc1 = Vector<float>.Zero;
            var acc2 = Vector<float>.Zero;
            var acc3 = Vector<float>.Zero;

            int quad = width * 4;
            for (; i <= n - quad; i += quad)
            {
                acc0 += Vector.LoadUnsafe(ref ra, (nuint)i) * Vector.LoadUnsafe(ref rb, (nuint)i);
                acc1 += Vector.LoadUnsafe(ref ra, (nuint)(i + width)) * Vector.LoadUnsafe(ref rb, (nuint)(i + width));
                acc2 += Vector.LoadUnsafe(ref ra, (nuint)(i + (2 * width))) * Vector.LoadUnsafe(ref rb, (nuint)(i + (2 * width)));
                acc3 += Vector.LoadUnsafe(ref ra, (nuint)(i + (3 * width))) * Vector.LoadUnsafe(ref rb, (nuint)(i + (3 * width)));
            }

            // Whole vectors the quad loop could not reach. At the K=64 this class actually runs
            // (see EmbeddingSubspace.DefaultTopK) both of these are empty on every SIMD width from
            // 4 to 16 lanes; they exist so the kernel is correct for any K, which is what the
            // length-sweep regression test asserts.
            for (; i <= n - width; i += width)
                acc0 += Vector.LoadUnsafe(ref ra, (nuint)i) * Vector.LoadUnsafe(ref rb, (nuint)i);

            sum = Vector.Sum(acc0 + acc1 + acc2 + acc3);
        }

        for (; i < n; i++)
            sum += a[i] * b[i];

        return sum;
    }
}
