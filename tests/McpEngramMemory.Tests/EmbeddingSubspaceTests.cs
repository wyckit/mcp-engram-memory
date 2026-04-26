using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Retrieval;

namespace McpEngramMemory.Tests;

public class EmbeddingSubspaceTests
{
    /// <summary>
    /// Property test for full-rank random embeddings (matches the realistic case):
    /// the truncated reconstruction V V^T e captures most of the energy of e when
    /// the data has a clear spectral fall-off. We verify that average reconstruction
    /// error stays below a generous 50% bound — the point is to confirm the
    /// projection isn't garbage, not to bound truncation precisely.
    /// </summary>
    [Fact]
    public void ProjectionRetainsBulkOfEmbeddingEnergy()
    {
        const int n = 200;
        const int d = 32;
        const int topK = 16;
        var rng = new Random(7);
        var embeddings = new float[n][];
        for (int i = 0; i < n; i++) embeddings[i] = RandomUnit(rng, d);

        var subspace = EmbeddingSubspace.Build(embeddings, topK: topK);
        Assert.NotNull(subspace);

        // Average ||V V^T e - e||^2 / ||e||^2 across all inputs.
        double totalRatio = 0;
        for (int i = 0; i < n; i++)
        {
            var ei = embeddings[i];
            var pi = subspace!.Projections[i];
            var recon = new float[d];
            for (int j = 0; j < d; j++)
                for (int k = 0; k < topK; k++)
                    recon[j] += subspace.Basis[j, k] * pi[k];

            float resid = 0f, orig = 0f;
            for (int j = 0; j < d; j++)
            {
                float r = ei[j] - recon[j];
                resid += r * r;
                orig += ei[j] * ei[j];
            }
            totalRatio += resid / orig;
        }
        double avg = totalRatio / n;
        Assert.True(avg < 0.5,
            $"Average projection residual ratio {avg:F4} too high; subspace is not retaining bulk of input energy.");
    }

    /// <summary>
    /// The DuplicateDetector spectral path (active above LowRankPivot) must not
    /// regress recall versus the direct pairwise scan. Synthesize 300 random
    /// embeddings plus 5 known duplicate pairs (each pair: a base vector and a
    /// near-copy with small additive noise). Both paths must surface all 5 pairs.
    /// </summary>
    [Fact]
    public void SpectralPathPreservesDuplicateRecall()
    {
        const int total = 300;
        const int duplicatePairs = 5;
        const int d = 64;
        const float threshold = 0.95f;

        var rng = new Random(13);
        var entries = new List<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>(total);

        // 5 duplicate pairs first, then random fillers up to `total`.
        for (int p = 0; p < duplicatePairs; p++)
        {
            var basis = RandomUnit(rng, d);
            var noisy = (float[])basis.Clone();
            for (int i = 0; i < d; i++) noisy[i] += (float)(rng.NextDouble() - 0.5) * 0.05f;
            entries.Add(MakeEntry($"a_{p}", basis));
            entries.Add(MakeEntry($"b_{p}", noisy));
        }
        while (entries.Count < total)
        {
            entries.Add(MakeEntry($"r_{entries.Count}", RandomUnit(rng, d)));
        }

        var detector = new DuplicateDetector();
        // Direct path baseline (bypass pivot by passing a sub-list smaller than 256).
        var firstPivotMinusOne = entries.Take(DuplicateDetector.LowRankPivot - 1).ToList();
        // Top up with the duplicate pairs not already included if any are beyond the pivot.
        var directBaseline = detector.FindDuplicates(firstPivotMinusOne, threshold, maxResults: 1000);

        // Spectral path: full list, triggers SpectralPrefilteredScan.
        var spectralResults = detector.FindDuplicates(entries, threshold, maxResults: 1000);

        // All 5 known pairs (a_p, b_p) must be in the spectral results regardless
        // of order. The direct baseline run is just defensive — it should also catch them.
        var spectralPairSet = new HashSet<string>();
        foreach (var (a, b, _) in spectralResults)
            spectralPairSet.Add(NormalizePair(a, b));
        for (int p = 0; p < duplicatePairs; p++)
            Assert.Contains(NormalizePair($"a_{p}", $"b_{p}"), spectralPairSet);

        // Cross-check: direct (small N) baseline finds the duplicate pairs that
        // appear in its window — confirms the test fixture is correct.
        var directPairSet = new HashSet<string>();
        foreach (var (a, b, _) in directBaseline)
            directPairSet.Add(NormalizePair(a, b));
        for (int p = 0; p < duplicatePairs; p++)
        {
            // a_p and b_p are at index 2p and 2p+1 — both inside the first 255 entries.
            Assert.Contains(NormalizePair($"a_{p}", $"b_{p}"), directPairSet);
        }
    }

    private static (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized) MakeEntry(string id, float[] vec)
    {
        var entry = new CognitiveEntry(id, vec, "test", text: id);
        float ns = 0f;
        for (int i = 0; i < vec.Length; i++) ns += vec[i] * vec[i];
        return (entry, MathF.Sqrt(ns), null);
    }

    private static float[] RandomUnit(Random rng, int d)
    {
        var v = new float[d];
        float ns = 0f;
        for (int i = 0; i < d; i++)
        {
            v[i] = (float)(rng.NextDouble() - 0.5);
            ns += v[i] * v[i];
        }
        float inv = 1f / MathF.Sqrt(ns);
        for (int i = 0; i < d; i++) v[i] *= inv;
        return v;
    }

    private static string NormalizePair(string a, string b) =>
        string.CompareOrdinal(a, b) < 0 ? $"{a}|{b}" : $"{b}|{a}";
}
