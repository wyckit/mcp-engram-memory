namespace McpEngramMemory.Core.Services.Graph;

/// <summary>
/// Randomized subspace iteration for the top-K largest-magnitude eigenpairs of a
/// symmetric matrix M, supplied implicitly via a matrix-vector product callback.
/// Used by <see cref="MemoryDiffusionKernel"/> to find the top-K eigenpairs of the
/// normalized adjacency D^(-1/2) W D^(-1/2); these convert to the smallest
/// eigenpairs of the normalized Laplacian via lambda_L = 1 - lambda_M.
///
/// Method (Halko/Martinsson/Tropp, 2011): draw a Gaussian sketch Omega in R^(N x m)
/// with m = K + oversample, run q power iterations Y &lt;- M Omega; Q &lt;- orth(Y);
/// Omega &lt;- Q to align Q with the dominant invariant subspace, then form the
/// small projected matrix B = Q^T M Q, eigendecompose B with Jacobi, and lift the
/// resulting eigenvectors back to R^N via U = Q V_B.
///
/// Internal numerical kernels (modified Gram-Schmidt QR, cyclic-Jacobi
/// eigendecomposition) are scalar — they are <i>only</i> applied to the small
/// projected matrix or to a thin tall matrix with O(K) columns, so SIMD is unnecessary
/// at the spine's working scale. The hot path (the matVec) is supplied by the caller
/// and is the only place worth tuning for performance.
/// </summary>
public static class RandomizedEigensolver
{
    /// <summary>
    /// Symmetric matrix-vector product. Implementations must read <paramref name="x"/>
    /// and write the result to <paramref name="y"/>; both spans have length N.
    /// </summary>
    public delegate void MatVecFn(ReadOnlySpan<float> x, Span<float> y);

    /// <summary>
    /// Compute the top-K largest-magnitude eigenpairs of a symmetric N×N matrix M
    /// supplied via <paramref name="matVec"/>. Returns eigenvalues sorted descending
    /// (largest first) and eigenvectors as a row-major dense matrix of shape [N, K].
    /// </summary>
    /// <param name="n">Matrix dimension.</param>
    /// <param name="topK">Number of eigenpairs to return.</param>
    /// <param name="oversample">Extra dimensions in the random sketch (typical: 5-10).</param>
    /// <param name="powerIters">Power iterations to align the sketch with the dominant subspace (typical: 5-7).</param>
    /// <param name="matVec">Symmetric matrix-vector product callback.</param>
    /// <param name="rng">RNG source for the Gaussian sketch.</param>
    public static (float[] Eigenvalues, float[,] Eigenvectors) SolveTopK(
        int n, int topK, int oversample, int powerIters, MatVecFn matVec, Random rng)
    {
        if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n));
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));
        if (topK > n) topK = n;

        int m = Math.Min(topK + Math.Max(oversample, 0), n);

        // When the random sketch would be the same size as the matrix, randomization
        // buys nothing and actually hurts: power iteration amplifies dominant
        // eigenvectors and suppresses small-magnitude ones, driving the trailing
        // columns into the same dominant subspace as the leading ones. MGS in float
        // precision then cannot recover orthogonality. Fall through to a direct
        // dense eigendecomposition: build M explicitly via n matVec calls, then
        // run Jacobi. Cost is O(n * matVecCost) for construction + O(n^3) for
        // Jacobi, which is acceptable for small n (the only regime this triggers in).
        if (m >= n)
            return DirectDenseEigendecomposition(n, topK, matVec);

        // Q starts as a random Gaussian sketch of shape [n, m], then is orthonormalized
        // and refined by `powerIters` rounds of Y <- M Q; Q <- orth(Y).
        var Q = new float[n, m];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < m; j++)
                Q[i, j] = (float)NextGaussian(rng);
        OrthonormalizeColumnsInPlace(Q, n, m);

        var Y = new float[n, m];
        var colBuf = new float[n];
        var yBuf = new float[n];

        for (int iter = 0; iter < powerIters; iter++)
        {
            ApplyMatVecToColumns(Q, Y, n, m, matVec, colBuf, yBuf);
            // Q <- orth(Y). Copy Y into Q first; OrthonormalizeColumnsInPlace mutates.
            Buffer.BlockCopy(Y, 0, Q, 0, sizeof(float) * n * m);
            OrthonormalizeColumnsInPlace(Q, n, m);
        }

        // Sanity guard: Q must be orthonormal for the Rayleigh-Ritz projection
        // B = Q^T M Q to inherit M's eigenvalue bounds. A drift here breaks
        // every downstream identity.
        AssertOrthonormal(Q, n, m, 1e-3f, "Q after final power iteration");

        // One last Y <- M Q so we can form B = Q^T Y = Q^T M Q.
        ApplyMatVecToColumns(Q, Y, n, m, matVec, colBuf, yBuf);

        // B = Q^T Y, an m x m matrix that is symmetric in exact arithmetic.
        // Symmetrize numerically to suppress floating-point asymmetry.
        var B = new float[m, m];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < m; j++)
            {
                float s = 0f;
                for (int k = 0; k < n; k++) s += Q[k, i] * Y[k, j];
                B[i, j] = s;
            }
        }
        for (int i = 0; i < m; i++)
            for (int j = i + 1; j < m; j++)
            {
                float avg = 0.5f * (B[i, j] + B[j, i]);
                B[i, j] = avg;
                B[j, i] = avg;
            }

        var (smallEigs, smallVecs) = JacobiEigendecomposition(B, m);
        AssertOrthonormal(smallVecs, m, m, 1e-3f, "V_B from Jacobi");

        // Order by eigenvalue descending and lift the top-K eigenvectors back to R^n.
        var order = new int[m];
        for (int i = 0; i < m; i++) order[i] = i;
        Array.Sort(order, (a, b) => smallEigs[b].CompareTo(smallEigs[a]));

        var eigenvalues = new float[topK];
        var eigenvectors = new float[n, topK];
        for (int kk = 0; kk < topK; kk++)
        {
            int j = order[kk];
            eigenvalues[kk] = smallEigs[j];
            for (int i = 0; i < n; i++)
            {
                float s = 0f;
                for (int p = 0; p < m; p++) s += Q[i, p] * smallVecs[p, j];
                eigenvectors[i, kk] = s;
            }
        }

        return (eigenvalues, eigenvectors);
    }

    private static (float[] Eigenvalues, float[,] Eigenvectors) DirectDenseEigendecomposition(
        int n, int topK, MatVecFn matVec)
    {
        var M = new float[n, n];
        var x = new float[n];
        var y = new float[n];
        for (int j = 0; j < n; j++)
        {
            Array.Clear(x, 0, n);
            x[j] = 1f;
            matVec(x, y);
            for (int i = 0; i < n; i++) M[i, j] = y[i];
        }
        // Symmetrize to scrub any asymmetric noise from the matVec impl.
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                float avg = 0.5f * (M[i, j] + M[j, i]);
                M[i, j] = avg;
                M[j, i] = avg;
            }

        var (eigs, vecs) = JacobiEigendecomposition(M, n);

        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) => eigs[b].CompareTo(eigs[a]));

        var topEigs = new float[topK];
        var topVecs = new float[n, topK];
        for (int kk = 0; kk < topK; kk++)
        {
            int j = order[kk];
            topEigs[kk] = eigs[j];
            for (int i = 0; i < n; i++) topVecs[i, kk] = vecs[i, j];
        }
        return (topEigs, topVecs);
    }

    private static void AssertOrthonormal(float[,] A, int n, int m, float tol, string label)
    {
        for (int a = 0; a < m; a++)
        {
            float diag = 0f;
            for (int i = 0; i < n; i++) diag += A[i, a] * A[i, a];
            if (MathF.Abs(diag - 1f) > tol)
                throw new InvalidOperationException($"{label}: column {a} has norm^2 {diag}, expected 1.");
            for (int b = a + 1; b < m; b++)
            {
                float off = 0f;
                for (int i = 0; i < n; i++) off += A[i, a] * A[i, b];
                if (MathF.Abs(off) > tol)
                    throw new InvalidOperationException($"{label}: <col {a}, col {b}> = {off}, expected 0.");
            }
        }
    }

    /// <summary>For each column j of <paramref name="src"/>, write M·src[:,j] into <paramref name="dst"/>[:,j].</summary>
    private static void ApplyMatVecToColumns(
        float[,] src, float[,] dst, int n, int m,
        MatVecFn matVec, float[] colBuf, float[] yBuf)
    {
        for (int j = 0; j < m; j++)
        {
            for (int i = 0; i < n; i++) colBuf[i] = src[i, j];
            matVec(colBuf, yBuf);
            for (int i = 0; i < n; i++) dst[i, j] = yBuf[i];
        }
    }

    /// <summary>
    /// Modified Gram-Schmidt orthonormalization of the columns of <paramref name="A"/> in place,
    /// with a second pass ("MGS2") for numerical stability. Power iteration on a matrix with
    /// clustered eigenvalues (e.g., a graph with k connected components has eigenvalue 1 with
    /// multiplicity k) drives multiple columns toward the same subspace; a single MGS pass
    /// in float precision then fails to fully orthogonalize them, which corrupts the
    /// downstream Rayleigh-Ritz projection. MGS2 reliably restores orthonormality at twice
    /// the cost — cheap at the dimensions the spine works at.
    ///
    /// Degenerate (nearly-zero) columns are replaced with a unit vector along an unused axis
    /// so the result is always full-rank.
    /// </summary>
    private static void OrthonormalizeColumnsInPlace(float[,] A, int n, int m)
    {
        // Iterate MGS until column orthonormality holds to a tight tolerance,
        // up to a small bounded number of passes. Two passes (MGS2) suffice for
        // generic inputs but pathological seeds — particularly when power
        // iteration drives many columns toward a low-dimensional dominant
        // subspace — can require a third pass to fully scrub residual drift.
        const int MaxPasses = 4;
        const float Tol = 1e-5f;
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            DoMGSPass(A, n, m);
            if (ColumnsAreOrthonormal(A, n, m, Tol)) return;
        }
    }

    private static bool ColumnsAreOrthonormal(float[,] A, int n, int m, float tol)
    {
        for (int a = 0; a < m; a++)
        {
            float diag = 0f;
            for (int i = 0; i < n; i++) diag += A[i, a] * A[i, a];
            if (MathF.Abs(diag - 1f) > tol) return false;
            for (int b = a + 1; b < m; b++)
            {
                float off = 0f;
                for (int i = 0; i < n; i++) off += A[i, a] * A[i, b];
                if (MathF.Abs(off) > tol) return false;
            }
        }
        return true;
    }

    private static void DoMGSPass(float[,] A, int n, int m)
    {
        const float Eps = 1e-10f;
        int axisFallback = 0;
        for (int j = 0; j < m; j++)
        {
            for (int k = 0; k < j; k++)
            {
                float dot = 0f;
                for (int i = 0; i < n; i++) dot += A[i, k] * A[i, j];
                for (int i = 0; i < n; i++) A[i, j] -= dot * A[i, k];
            }

            float norm = 0f;
            for (int i = 0; i < n; i++) norm += A[i, j] * A[i, j];
            norm = MathF.Sqrt(norm);

            if (norm < Eps)
            {
                while (axisFallback < n)
                {
                    for (int i = 0; i < n; i++) A[i, j] = i == axisFallback ? 1f : 0f;
                    axisFallback++;
                    for (int k = 0; k < j; k++)
                    {
                        float dot = 0f;
                        for (int i = 0; i < n; i++) dot += A[i, k] * A[i, j];
                        for (int i = 0; i < n; i++) A[i, j] -= dot * A[i, k];
                    }
                    norm = 0f;
                    for (int i = 0; i < n; i++) norm += A[i, j] * A[i, j];
                    norm = MathF.Sqrt(norm);
                    if (norm >= Eps) break;
                }
                if (norm < Eps) throw new InvalidOperationException("Column rank deficient beyond fallback recovery.");
            }

            float inv = 1f / norm;
            for (int i = 0; i < n; i++) A[i, j] *= inv;
        }
    }

    /// <summary>
    /// Cyclic-Jacobi eigendecomposition for a small symmetric matrix. Returns
    /// eigenvalues (length n) and eigenvectors as columns of a [n, n] matrix.
    /// Mutates a local copy; <paramref name="A"/> is not modified.
    /// </summary>
    private static (float[] Eigenvalues, float[,] Eigenvectors) JacobiEigendecomposition(float[,] A, int n)
    {
        var D = (float[,])A.Clone();
        var V = new float[n, n];
        for (int i = 0; i < n; i++) V[i, i] = 1f;

        const int MaxSweeps = 60;
        for (int sweep = 0; sweep < MaxSweeps; sweep++)
        {
            float off = 0f;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    off += D[i, j] * D[i, j];
            if (off < 1e-14f) break;

            for (int p = 0; p < n - 1; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    float apq = D[p, q];
                    if (MathF.Abs(apq) < 1e-14f) continue;

                    float app = D[p, p];
                    float aqq = D[q, q];
                    float theta = (aqq - app) / (2f * apq);

                    float t;
                    if (float.IsInfinity(theta) || MathF.Abs(theta) > 1e10f)
                        t = 0.5f / theta;
                    else
                        t = (theta >= 0f ? 1f : -1f) / (MathF.Abs(theta) + MathF.Sqrt(theta * theta + 1f));

                    float c = 1f / MathF.Sqrt(t * t + 1f);
                    float s = t * c;

                    D[p, p] = app - t * apq;
                    D[q, q] = aqq + t * apq;
                    D[p, q] = 0f;
                    D[q, p] = 0f;

                    for (int r = 0; r < n; r++)
                    {
                        if (r != p && r != q)
                        {
                            float drp = D[r, p];
                            float drq = D[r, q];
                            D[r, p] = c * drp - s * drq;
                            D[p, r] = D[r, p];
                            D[r, q] = s * drp + c * drq;
                            D[q, r] = D[r, q];
                        }
                        float vrp = V[r, p];
                        float vrq = V[r, q];
                        V[r, p] = c * vrp - s * vrq;
                        V[r, q] = s * vrp + c * vrq;
                    }
                }
            }
        }

        var eigs = new float[n];
        for (int i = 0; i < n; i++) eigs[i] = D[i, i];
        return (eigs, V);
    }

    /// <summary>Standard normal sample via Box-Muller.</summary>
    private static double NextGaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
