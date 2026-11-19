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
        if (fp32.Length == 0)
            return new QuantizedVector(Array.Empty<sbyte>(), 0f, 1f);

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
    /// SIMD-accelerated sum of Int8 elements used to de-offset asymmetric min/max quantization.
    /// </summary>
    private static int Int8Sum(ReadOnlySpan<sbyte> a)
    {
        int length = a.Length;
        int sum = 0;
        int i = 0;

        if (Vector.IsHardwareAccelerated && length >= Vector<sbyte>.Count)
        {
            var acc = Vector<int>.Zero;
            int sbyteCount = Vector<sbyte>.Count;
            int simdEnd = length - (length % sbyteCount);

            for (; i < simdEnd; i += sbyteCount)
            {
                var va = new Vector<sbyte>(a.Slice(i));
                Vector.Widen(va, out var vaShortLo, out var vaShortHi);
                Vector.Widen(vaShortLo, out var v0, out var v1);
                Vector.Widen(vaShortHi, out var v2, out var v3);
                acc += v0 + v1 + v2 + v3;
            }

            sum = Vector.Sum(acc);
        }

        for (; i < length; i++)
            sum += a[i];

        return sum;
    }

    /// <summary>
    /// Compute approximate cosine similarity between two asymmetrically quantized vectors.
    /// Reconstructs the exact de-offsetted dot product and L2 norms from <see cref="QuantizedVector.Min"/>,
    /// <see cref="QuantizedVector.Scale"/>, <see cref="QuantizedVector.SelfDot"/>, and SIMD Int8 dot/sum
    /// so skewed or non-zero-centered vectors accurately approximate FP32 cosine similarity.
    /// </summary>
    public static float ApproximateCosine(QuantizedVector a, QuantizedVector b)
    {
        int d = Math.Min(a.Data.Length, b.Data.Length);
        if (d == 0 || a.Scale <= 0f || b.Scale <= 0f) return 0f;

        // Quantize maps x[i] -> q[i] = round((x[i] - min) * scale) - 128, so the dequantized
        // coordinate is x_hat[i] = (p[i] + m) / scale where p[i] = q[i] + 128 in [0, 255] and
        // m = min * scale. Cosine similarity is invariant to positive scaling (1/scale), so we
        // evaluate cosine(p_a + m_a, p_b + m_b). Performing the +128 shift in exact 64-bit integer
        // arithmetic avoids catastrophic cancellation on zero and constant vectors.
        var spanA = a.Data.AsSpan(0, d);
        var spanB = b.Data.AsSpan(0, d);

        long dotQ = Int8DotProduct(spanA, spanB);
        long sumQA = Int8Sum(spanA);
        long sumQB = Int8Sum(spanB);
        long selfDotQA = a.Data.Length == d ? a.SelfDot : Int8DotProduct(spanA, spanA);
        long selfDotQB = b.Data.Length == d ? b.SelfDot : Int8DotProduct(spanB, spanB);

        long ld = d;
        long sumPA = sumQA + 128L * ld;
        long sumPB = sumQB + 128L * ld;
        long selfDotPA = selfDotQA + 256L * sumQA + 16384L * ld;
        long selfDotPB = selfDotQB + 256L * sumQB + 16384L * ld;
        long dotP = dotQ + 128L * sumQA + 128L * sumQB + 16384L * ld;

        double mA = (double)a.Min * a.Scale;
        double mB = (double)b.Min * b.Scale;

        double normSqA = selfDotPA + 2.0 * mA * sumPA + ld * mA * mA;
        double normSqB = selfDotPB + 2.0 * mB * sumPB + ld * mB * mB;
        if (normSqA <= 0.0 || normSqB <= 0.0) return 0f;

        double num = dotP + mB * sumPA + mA * sumPB + ld * mA * mB;
        double cosine = num / (Math.Sqrt(normSqA) * Math.Sqrt(normSqB));
        if (!double.IsFinite(cosine)) return 0f;
        return (float)Math.Clamp(cosine, -1.0, 1.0);
    }
}
