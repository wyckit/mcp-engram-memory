using System.Numerics;

namespace McpVectorMemory;

/// <summary>
/// SIMD-accelerated vector math utilities shared across the codebase.
/// </summary>
internal static class VectorMath
{
    /// <summary>
    /// Computes the dot product of two float arrays using SIMD when available.
    /// </summary>
    public static float Dot(float[] a, float[] b)
    {
        float sum = 0f;
        int i = 0;

        if (Vector.IsHardwareAccelerated)
        {
            int simdLength = Vector<float>.Count;
            int simdEnd = a.Length - (a.Length % simdLength);
            for (; i < simdEnd; i += simdLength)
                sum += Vector.Dot(new Vector<float>(a, i), new Vector<float>(b, i));
        }

        for (; i < a.Length; i++)
            sum += a[i] * b[i];

        return sum;
    }

    /// <summary>
    /// Computes the L2 norm (magnitude) of a float array.
    /// </summary>
    public static float Norm(float[] v)
    {
        float dot = Dot(v, v);
        return dot == 0f ? 0f : MathF.Sqrt(dot);
    }

    /// <summary>
    /// Computes cosine similarity between two vectors (range: -1 to 1).
    /// Returns 0 if either vector has zero magnitude.
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = Dot(a, b);
        float normA = Norm(a);
        float normB = Norm(b);
        if (normA == 0f || normB == 0f)
            return 0f;
        return dot / (normA * normB);
    }

    /// <summary>
    /// Computes cosine distance (1 - cosine_similarity) between two vectors.
    /// Returns 1 if either vector has zero magnitude.
    /// </summary>
    public static float CosineDistance(float[] a, float[] b)
    {
        return 1f - CosineSimilarity(a, b);
    }
}
