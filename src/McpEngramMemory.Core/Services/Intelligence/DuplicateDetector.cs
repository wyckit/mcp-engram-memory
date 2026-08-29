using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Retrieval;

namespace McpEngramMemory.Core.Services.Intelligence;

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
        foreach (var pair in StreamDuplicates(candidates, threshold))
        {
            duplicates.Add(pair);
            if (duplicates.Count >= maxResults) break;
        }
        duplicates.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
        return duplicates;
    }

    /// <summary>
    /// Every above-threshold pair, yielded lazily in scan order and with no output bound.
    ///
    /// It exists because a bound on the OUTPUT is not a bound a filtering caller can use. A caller
    /// that discards some of what it is handed — auto-link discards pairs that already carry an edge
    /// and pairs whose endpoint is unattributable — has no way to ask a fixed-size result for "the
    /// next one after those": the window it was given is the window it keeps getting, and the pairs
    /// behind that window are never reached on any number of deterministic rescans. Streaming lets
    /// the caller keep pulling until ITS OWN quota is met, and pay for one pairwise pass to do it,
    /// rather than re-running the quadratic scan once per widened guess.
    ///
    /// Deferred: no PAIR is compared until the result is enumerated, and enumeration abandoned
    /// partway abandons the remaining comparisons — that is what keeps the bounded caller above at
    /// its old cost. Above the pivot the subspace set-up still runs eagerly, deliberately; see
    /// <see cref="StreamSpectralPrefiltered"/>.
    /// </summary>
    internal IEnumerable<(string IdA, string IdB, float Similarity)> StreamDuplicates(
        IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
        float threshold)
    {
        if (candidates.Count < LowRankPivot)
            return StreamDirectPairwise(candidates, threshold);
        return StreamSpectralPrefiltered(candidates, threshold);
    }

    /// <summary>Threshold above which two-pass spectral filtering replaces direct O(N^2) scan.</summary>
    public const int LowRankPivot = 256;

    /// <summary>
    /// Recall safety margin: the projection-space threshold is the requested
    /// threshold minus this value, so true duplicates whose projection cosine
    /// is slightly attenuated by subspace truncation still survive the filter.
    /// </summary>
    public const float ProjectionThresholdSlack = 0.10f;

    private static IEnumerable<(string IdA, string IdB, float Similarity)> StreamDirectPairwise(
        IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
        float threshold)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            for (int j = i + 1; j < candidates.Count; j++)
            {
                var a = candidates[i];
                var b = candidates[j];
                if (a.Norm == 0f || b.Norm == 0f) continue;
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
    /// </summary>
    private static IEnumerable<(string IdA, string IdB, float Similarity)> StreamSpectralPrefiltered(
        IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
        float threshold)
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
            return StreamDirectPairwise(candidates, threshold);

        // Build subspace from the kept embeddings, in their original order.
        var embeddings = new float[keep.Count][];
        for (int i = 0; i < keep.Count; i++) embeddings[i] = candidates[keep[i]].Entry.Vector;
        var subspace = EmbeddingSubspace.Build(embeddings, EmbeddingSubspace.DefaultTopK);
        if (subspace is null) return StreamDirectPairwise(candidates, threshold);

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

        return StreamProjectionSurvivors(candidates, keep, subspace, projNorms, threshold);
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
        List<int> keep, SubspaceProjection subspace, float[] projNorms, float threshold)
    {
        float looseThreshold = threshold - ProjectionThresholdSlack;
        for (int i = 0; i < keep.Count; i++)
        {
            if (projNorms[i] == 0f) continue;
            var pi = subspace.Projections[i];
            for (int j = i + 1; j < keep.Count; j++)
            {
                if (projNorms[j] == 0f) continue;
                var pj = subspace.Projections[j];
                float projDot = 0f;
                for (int k = 0; k < pi.Length; k++) projDot += pi[k] * pj[k];
                if (projDot / (projNorms[i] * projNorms[j]) < looseThreshold) continue;

                var a = candidates[keep[i]];
                var b = candidates[keep[j]];
                float sim = VectorMath.Dot(a.Entry.Vector, b.Entry.Vector) / (a.Norm * b.Norm);
                if (sim >= threshold)
                    yield return (a.Entry.Id, b.Entry.Id, sim);
            }
        }
    }
}
