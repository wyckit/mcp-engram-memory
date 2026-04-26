using McpEngramMemory.Core.Services.Graph;

namespace McpEngramMemory.Core.Services.Retrieval;

/// <summary>
/// Builds a low-rank approximation of a set of embedding vectors via randomized SVD.
/// Used as a candidate-prefilter for O(N^2) similarity scans (duplicate detection,
/// contradiction surfacing): projecting each embedding into a K &lt;&lt; d subspace
/// lets pairwise cosine work in the smaller space, with full-fidelity confirmation
/// only on the survivors.
///
/// Note: the K-dim cosine approximation holds best for *similar* vector pairs
/// (whose orthogonal-to-subspace residuals are small). Distinct vectors in the
/// full embedding space can still appear similar in projection, so this surface
/// is intended only as a coarse filter — callers must confirm survivors against
/// the full FP32 vectors. The classic two-pass pattern is:
///
///   1. Project all candidates into the K-dim subspace.
///   2. Find pairs whose projection-space cosine exceeds (threshold - epsilon),
///      where epsilon (typically 0.05-0.1) widens the gate to absorb projection error.
///   3. Confirm survivors with full FP32 cosine; keep only those that still clear
///      the original threshold.
///
/// This decomposes E ≈ U_K Σ_K V_K^T where E is the [N, d] embedding matrix.
/// V_K (the right singular vectors) is the [d, K] basis used to project queries.
/// Projection of entry i: proj_i = V_K^T e_i, an [K]-dim coord vector.
/// </summary>
public static class EmbeddingSubspace
{
    /// <summary>Default subspace dimensionality. 64 captures most variance for a 384-d embedding.</summary>
    public const int DefaultTopK = 64;

    /// <summary>
    /// Build the K-dim subspace and project every input embedding into it. All input
    /// vectors must share the same dimension. Returns null if the inputs cannot
    /// support the requested K (too few embeddings, or zero-dimension input).
    /// </summary>
    public static SubspaceProjection? Build(IReadOnlyList<float[]> embeddings, int topK = DefaultTopK)
    {
        if (embeddings.Count == 0) return null;
        int n = embeddings.Count;
        int d = embeddings[0].Length;
        if (d == 0) return null;
        if (topK > d) topK = d;
        if (topK > n) topK = n;
        if (topK <= 0) return null;

        // Eigendecompose E^T E (a [d, d] symmetric PSD matrix) implicitly via matVec.
        // Two passes through E per matVec is fine — d is small (384) and N is bounded.
        // The eigenvectors of E^T E are the right singular vectors V of E.
        void MatVec(ReadOnlySpan<float> x, Span<float> y)
        {
            // y = E^T E x. First inner: Ex of length n. Then E^T applied to that.
            Span<float> ex = stackalloc float[0];
            var exArr = new float[n];
            for (int i = 0; i < n; i++)
            {
                var ei = embeddings[i];
                float s = 0f;
                for (int j = 0; j < d; j++) s += ei[j] * x[j];
                exArr[i] = s;
            }
            for (int j = 0; j < d; j++) y[j] = 0f;
            for (int i = 0; i < n; i++)
            {
                var ei = embeddings[i];
                float exi = exArr[i];
                for (int j = 0; j < d; j++) y[j] += ei[j] * exi;
            }
        }

        // Deterministic seed so repeated builds on identical inputs are reproducible.
        var rng = new Random(unchecked((int)0xE3A5_D1F7));
        var (_, basisV) = RandomizedEigensolver.SolveTopK(d, topK, oversample: 10, powerIters: 4, MatVec, rng);

        // Project each embedding: proj_i[k] = sum_j V[j, k] * e_i[j].
        var projections = new float[n][];
        for (int i = 0; i < n; i++)
        {
            var ei = embeddings[i];
            var proj = new float[topK];
            for (int k = 0; k < topK; k++)
            {
                float s = 0f;
                for (int j = 0; j < d; j++) s += basisV[j, k] * ei[j];
                proj[k] = s;
            }
            projections[i] = proj;
        }

        return new SubspaceProjection(basisV, projections, d, topK);
    }
}

/// <summary>
/// Concrete output of <see cref="EmbeddingSubspace.Build"/>. Holds the projected
/// coordinates for the inputs plus the basis matrix needed to project future queries.
/// </summary>
public sealed class SubspaceProjection
{
    /// <summary>Right singular vector basis, shape [d, K]. Use to project new queries.</summary>
    public float[,] Basis { get; }

    /// <summary>K-dim projection of each input embedding, in original input order.</summary>
    public IReadOnlyList<float[]> Projections { get; }

    /// <summary>Original embedding dimension.</summary>
    public int OriginalDim { get; }

    /// <summary>Subspace dimension K.</summary>
    public int ReducedDim { get; }

    internal SubspaceProjection(float[,] basis, float[][] projections, int originalDim, int reducedDim)
    {
        Basis = basis;
        Projections = projections;
        OriginalDim = originalDim;
        ReducedDim = reducedDim;
    }

    /// <summary>Project a fresh query embedding into the cached subspace.</summary>
    public float[] Project(ReadOnlySpan<float> embedding)
    {
        if (embedding.Length != OriginalDim)
            throw new ArgumentException(
                $"Query has dimension {embedding.Length} but subspace was built for dimension {OriginalDim}.",
                nameof(embedding));
        var result = new float[ReducedDim];
        for (int k = 0; k < ReducedDim; k++)
        {
            float s = 0f;
            for (int j = 0; j < OriginalDim; j++) s += Basis[j, k] * embedding[j];
            result[k] = s;
        }
        return result;
    }
}
