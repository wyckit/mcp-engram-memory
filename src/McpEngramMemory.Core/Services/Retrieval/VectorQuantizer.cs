using System.Numerics;
using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Retrieval;

/// <summary>
/// Scalar quantization (FP32 → Int8) with SIMD-accelerated Int8 dot product.
/// Provides ~4x memory reduction and hardware-accelerated distance computation.
/// </summary>
public static class VectorQuantizer
{
    /// <summary>
    /// Quantize a float[] vector to Int8 using min/max asymmetric quantization.
    /// Maps the value range [min, max] → [-128, 127].
    /// </summary>
    public static QuantizedVector Quantize(float[] fp32)
    {
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < fp32.Length; i++)
        {
            if (fp32[i] < min) min = fp32[i];
            if (fp32[i] > max) max = fp32[i];
        }

        float range = max - min;
        if (range == 0f) range = 1f; // Avoid division by zero for constant vectors
        float scale = 255f / range;

        var quantized = new sbyte[fp32.Length];
        for (int i = 0; i < fp32.Length; i++)
        {
            quantized[i] = (sbyte)Math.Clamp(
                (int)MathF.Round((fp32[i] - min) * scale) - 128,
                -128, 127);
        }

        return new QuantizedVector(quantized, min, scale);
    }

    /// <summary>
    /// Reconstruct a FP32 vector from its quantized representation (lossy).
    /// </summary>
    public static float[] Dequantize(QuantizedVector qv)
    {
        var result = new float[qv.Data.Length];
        for (int i = 0; i < qv.Data.Length; i++)
            result[i] = (qv.Data[i] + 128f) / qv.Scale + qv.Min;
        return result;
    }

    /// <summary>
    /// SIMD-accelerated Int8 dot product.
    /// Uses System.Numerics.Vector for portable hardware acceleration:
    /// widens sbyte→short for multiply, then short→int for accumulation.
    /// With AVX2, processes 32 elements per iteration (vs 8 for FP32).
    /// </summary>
    public static int Int8DotProduct(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b)
    {
        int length = Math.Min(a.Length, b.Length);
        int dotProduct = 0;
        int i = 0;

        if (Vector.IsHardwareAccelerated && length >= Vector<sbyte>.Count)
        {
            var accLo = Vector<int>.Zero;
            var accHi = Vector<int>.Zero;
            int sbyteCount = Vector<sbyte>.Count;
            int simdEnd = length - (length % sbyteCount);

            for (; i < simdEnd; i += sbyteCount)
            {
                var va = new Vector<sbyte>(a.Slice(i));
                var vb = new Vector<sbyte>(b.Slice(i));

                // Widen sbyte → short (each vector splits into two halves)
                Vector.Widen(va, out var vaShortLo, out var vaShortHi);
                Vector.Widen(vb, out var vbShortLo, out var vbShortHi);

                // Element-wise multiply (short * short → short; safe for sbyte range)
                var prodLo = vaShortLo * vbShortLo;
                var prodHi = vaShortHi * vbShortHi;

                // Widen short → int for safe accumulation
                Vector.Widen(prodLo, out var pLoLo, out var pLoHi);
                Vector.Widen(prodHi, out var pHiLo, out var pHiHi);

                accLo += pLoLo + pLoHi;
                accHi += pHiLo + pHiHi;
            }

            dotProduct = Vector.Sum(accLo) + Vector.Sum(accHi);
        }

        // Scalar fallback for remainder elements
        for (; i < length; i++)
            dotProduct += a[i] * b[i];

        return dotProduct;
    }

    /// <summary>
    /// Compute approximate cosine similarity between two quantized vectors.
    /// Uses Int8 dot product for the numerator and precomputed SelfDot for norms.
    /// </summary>
    public static float ApproximateCosine(QuantizedVector a, QuantizedVector b)
    {
        if (a.SelfDot == 0 || b.SelfDot == 0) return 0f;
        int dot = Int8DotProduct(a.Data, b.Data);
        return dot / (MathF.Sqrt(a.SelfDot) * MathF.Sqrt(b.SelfDot));
    }
}
