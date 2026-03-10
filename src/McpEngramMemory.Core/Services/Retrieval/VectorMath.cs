using System.Numerics;

namespace McpEngramMemory.Core.Services.Retrieval;

/// <summary>
/// SIMD-accelerated vector math utilities (dot product, norm, cosine similarity).
/// </summary>
public static class VectorMath
{
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

    public static float Norm(float[] v)
    {
        float dot = Dot(v, v);
        return dot == 0f ? 0f : MathF.Sqrt(dot);
    }
}
