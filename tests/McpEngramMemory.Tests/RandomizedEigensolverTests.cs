using McpEngramMemory.Core.Services.Graph;

namespace McpEngramMemory.Tests;

public class RandomizedEigensolverTests
{
    /// <summary>
    /// REGRESSION WITNESS — exactly rank-deficient operator through the randomized path.
    ///
    /// Failure mechanism (verified 2026-08-09 against production graphs; this exact
    /// construction reproduced the failure 20/20 across seeds on every production
    /// namespace with fewer than m = topK + oversample linked nodes): when the operator
    /// M has exactly-zero rows/columns (isolated graph nodes), the first Y = M·Q
    /// confines the panel to an r-dimensional subspace with r &lt; m. Columns 0..r-1
    /// orthonormalize fine and span exactly the non-zero coordinate subspace; column r
    /// has zero residual and triggers the axis-replacement fallback. The fallback walks
    /// axes 0,1,2,... in order — and when the chosen axis coordinate belongs to a
    /// NON-zero row, that unit axis lies entirely inside span(cols 0..r-1), so after
    /// orthogonalization its residual is pure float32 cancellation noise (~1e-7 scale).
    /// The old acceptance check <c>norm &gt;= Eps</c> with Eps = 1e-10 — four orders of
    /// magnitude below float32 noise — ACCEPTED the noise vector and normalized it with
    /// ~1e7x error amplification into a garbage column whose inner products with earlier
    /// columns are O(0.05-0.5). No subsequent MGS pass can repair it, and
    /// AssertOrthonormal throws InvalidOperationException ("... expected 0.").
    ///
    /// Shape: n = 140, a connected 24-node weighted subgraph spread across the index
    /// range (every 6th coordinate; ring + chords, normalized D^-1/2 W D^-1/2 within
    /// the subgraph) and all other 116 coordinates exactly zero rows/columns.
    /// m = min(96 + 10, 140) = 106 &gt; rank ~ 24 with n &gt; m, so the randomized path
    /// (not the direct dense path) runs.
    /// </summary>
    [Fact]
    public void SolveTopKSucceedsOnExactlyRankDeficientOperator()
    {
        const int n = 140;
        const int subCount = 24;
        var subIdx = new int[subCount];
        for (int a = 0; a < subCount; a++) subIdx[a] = a * 6; // 0, 6, ..., 138

        // Ring + a few chords among the 24 subgraph nodes, weights 1.0.
        var w = new float[subCount, subCount];
        for (int a = 0; a < subCount; a++)
        {
            int b = (a + 1) % subCount;
            w[a, b] = 1f;
            w[b, a] = 1f;
        }
        foreach (int a in new[] { 0, 4, 8, 12, 16, 20 })
        {
            int b = (a + 5) % subCount;
            w[a, b] = 1f;
            w[b, a] = 1f;
        }

        var deg = new float[subCount];
        for (int a = 0; a < subCount; a++)
            for (int b = 0; b < subCount; b++)
                deg[a] += w[a, b];

        // Normalized adjacency M = D^-1/2 W D^-1/2 restricted to the subgraph.
        var mSub = new float[subCount, subCount];
        for (int a = 0; a < subCount; a++)
            for (int b = 0; b < subCount; b++)
                if (w[a, b] > 0f)
                    mSub[a, b] = w[a, b] / MathF.Sqrt(deg[a] * deg[b]);

        void MatVec(ReadOnlySpan<float> x, Span<float> y)
        {
            y.Clear(); // all non-subgraph coordinates are exactly zero rows
            for (int a = 0; a < subCount; a++)
            {
                float acc = 0f;
                for (int b = 0; b < subCount; b++)
                    acc += mSub[a, b] * x[subIdx[b]];
                y[subIdx[a]] = acc;
            }
        }

        for (int seed = 0; seed < 5; seed++)
        {
            var (eigenvalues, eigenvectors) =
                RandomizedEigensolver.SolveTopK(n, 96, 10, 5, MatVec, new Random(seed));

            foreach (float e in eigenvalues)
            {
                Assert.True(float.IsFinite(e), $"seed {seed}: non-finite eigenvalue {e}.");
                Assert.InRange(e, -1f - 1e-3f, 1f + 1e-3f);
            }
            AssertColumnsOrthonormal(eigenvectors, n, eigenvalues.Length, 1e-3f, $"seed {seed}");
        }
    }

    /// <summary>
    /// Guard: the rank-revealing axis-fallback threshold must not disturb the healthy
    /// randomized path. Diagonal operator with well-separated leading spectrum:
    /// d[i] = 2^-i for i &lt; 10, else 1e-4; topK = 5, oversample = 5 (m = 10 &lt; n = 140,
    /// randomized path). Eigenvalues must land within 1e-3 of {1, 0.5, 0.25, 0.125,
    /// 0.0625} with orthonormal eigenvectors.
    /// </summary>
    [Fact]
    public void SolveTopKStillCorrectOnWellConditionedOperator()
    {
        const int n = 140;
        var d = new float[n];
        for (int i = 0; i < n; i++)
            d[i] = i < 10 ? MathF.Pow(2f, -i) : 1e-4f;

        void MatVec(ReadOnlySpan<float> x, Span<float> y)
        {
            for (int i = 0; i < n; i++) y[i] = d[i] * x[i];
        }

        var (eigenvalues, eigenvectors) =
            RandomizedEigensolver.SolveTopK(n, 5, 5, 5, MatVec, new Random(42));

        float[] expected = { 1f, 0.5f, 0.25f, 0.125f, 0.0625f };
        Assert.Equal(expected.Length, eigenvalues.Length);
        for (int k = 0; k < expected.Length; k++)
            Assert.True(MathF.Abs(eigenvalues[k] - expected[k]) < 1e-3f,
                $"Eigenvalue {k}: expected {expected[k]}, got {eigenvalues[k]}.");

        AssertColumnsOrthonormal(eigenvectors, n, eigenvalues.Length, 1e-3f, "well-conditioned");
    }

    private static void AssertColumnsOrthonormal(float[,] u, int n, int k, float tol, string label)
    {
        for (int a = 0; a < k; a++)
        {
            float diag = 0f;
            for (int i = 0; i < n; i++) diag += u[i, a] * u[i, a];
            Assert.True(MathF.Abs(diag - 1f) <= tol,
                $"{label}: column {a} has norm^2 {diag}, expected 1.");
            for (int b = a + 1; b < k; b++)
            {
                float off = 0f;
                for (int i = 0; i < n; i++) off += u[i, a] * u[i, b];
                Assert.True(MathF.Abs(off) <= tol,
                    $"{label}: <col {a}, col {b}> = {off}, expected 0.");
            }
        }
    }
}
