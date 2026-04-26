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
    /// Find near-duplicate entries by pairwise cosine similarity scan. Above
    /// <see cref="LowRankPivot"/> candidates, switches to a two-pass spectral
    /// pre-filter (project to a K-dim subspace, scan in projection space at a
    /// widened threshold, confirm survivors with full-FP32 cosine) to amortize
    /// the O(N^2) cost. Below the pivot, the original direct pairwise scan is
    /// preserved — its cost is already bounded.
    /// </summary>
    public IReadOnlyList<(string IdA, string IdB, float Similarity)> FindDuplicates(
        IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
        float threshold = 0.95f,
        int maxResults = 100)
    {
        if (candidates.Count < LowRankPivot)
            return DirectPairwiseScan(candidates, threshold, maxResults);
        return SpectralPrefilteredScan(candidates, threshold, maxResults);
    }

    /// <summary>Threshold above which two-pass spectral filtering replaces direct O(N^2) scan.</summary>
    public const int LowRankPivot = 256;

    /// <summary>
    /// Recall safety margin: the projection-space threshold is the requested
    /// threshold minus this value, so true duplicates whose projection cosine
    /// is slightly attenuated by subspace truncation still survive the filter.
    /// </summary>
    public const float ProjectionThresholdSlack = 0.10f;

    private static IReadOnlyList<(string IdA, string IdB, float Similarity)> DirectPairwiseScan(
        IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
        float threshold, int maxResults)
    {
        var duplicates = new List<(string IdA, string IdB, float Similarity)>();
        for (int i = 0; i < candidates.Count && duplicates.Count < maxResults; i++)
        {
            for (int j = i + 1; j < candidates.Count && duplicates.Count < maxResults; j++)
            {
                var a = candidates[i];
                var b = candidates[j];
                if (a.Norm == 0f || b.Norm == 0f) continue;
                if (a.Entry.Vector.Length != b.Entry.Vector.Length) continue;

                float dot = VectorMath.Dot(a.Entry.Vector, b.Entry.Vector);
                float sim = dot / (a.Norm * b.Norm);

                if (sim >= threshold)
                    duplicates.Add((a.Entry.Id, b.Entry.Id, sim));
            }
        }
        duplicates.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
        return duplicates;
    }

    private static IReadOnlyList<(string IdA, string IdB, float Similarity)> SpectralPrefilteredScan(
        IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
        float threshold, int maxResults)
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
            return DirectPairwiseScan(candidates, threshold, maxResults);

        // Build subspace from the kept embeddings, in their original order.
        var embeddings = new float[keep.Count][];
        for (int i = 0; i < keep.Count; i++) embeddings[i] = candidates[keep[i]].Entry.Vector;
        var subspace = EmbeddingSubspace.Build(embeddings, EmbeddingSubspace.DefaultTopK);
        if (subspace is null) return DirectPairwiseScan(candidates, threshold, maxResults);

        // Pass 2: scan in projection space with widened threshold. Norms in the
        // projection space are *not* the same as the original norms (truncation
        // drops magnitude orthogonal to col(V)), so compute them locally.
        var projNorms = new float[keep.Count];
        for (int i = 0; i < keep.Count; i++)
        {
            float ns = 0f;
            var p = subspace.Projections[i];
            for (int k = 0; k < p.Length; k++) ns += p[k] * p[k];
            projNorms[i] = MathF.Sqrt(ns);
        }

        float looseThreshold = threshold - ProjectionThresholdSlack;
        var pairCandidates = new List<(int LocalA, int LocalB)>();
        for (int i = 0; i < keep.Count; i++)
        {
            if (projNorms[i] == 0f) continue;
            var pi = subspace.Projections[i];
            for (int j = i + 1; j < keep.Count; j++)
            {
                if (projNorms[j] == 0f) continue;
                var pj = subspace.Projections[j];
                float dot = 0f;
                for (int k = 0; k < pi.Length; k++) dot += pi[k] * pj[k];
                float sim = dot / (projNorms[i] * projNorms[j]);
                if (sim >= looseThreshold)
                    pairCandidates.Add((i, j));
            }
        }

        // Pass 3: confirm survivors with full FP32 cosine on original vectors.
        var duplicates = new List<(string IdA, string IdB, float Similarity)>();
        foreach (var (la, lb) in pairCandidates)
        {
            if (duplicates.Count >= maxResults) break;
            var a = candidates[keep[la]];
            var b = candidates[keep[lb]];
            float dot = VectorMath.Dot(a.Entry.Vector, b.Entry.Vector);
            float sim = dot / (a.Norm * b.Norm);
            if (sim >= threshold)
                duplicates.Add((a.Entry.Id, b.Entry.Id, sim));
        }
        duplicates.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
        return duplicates;
    }
}
