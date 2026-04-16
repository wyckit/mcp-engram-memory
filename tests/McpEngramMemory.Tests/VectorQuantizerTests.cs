using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Retrieval;

namespace McpEngramMemory.Tests;

public class VectorQuantizerTests
{
    [Fact]
    public void Quantize_ProducesCorrectLength()
    {
        var fp32 = CreateRandomVector(384);
        var qv = VectorQuantizer.Quantize(fp32);

        Assert.Equal(384, qv.Data.Length);
        Assert.NotEqual(0f, qv.Scale);
    }

    [Fact]
    public void Quantize_MinMaxMappedCorrectly()
    {
        // A vector with known min/max
        var fp32 = new float[] { -1f, 0f, 0.5f, 1f };
        var qv = VectorQuantizer.Quantize(fp32);

        // Min value should map to -128, max to +127
        Assert.Equal(-128, qv.Data[0]); // min
        Assert.Equal(127, qv.Data[3]);  // max
    }

    [Fact]
    public void Int8DotProduct_ScalarMatchesSIMD()
    {
        var a = VectorQuantizer.Quantize(CreateRandomVector(384));
        var b = VectorQuantizer.Quantize(CreateRandomVector(384));

        // Compute expected scalar result
        int expected = 0;
        for (int i = 0; i < a.Data.Length; i++)
            expected += a.Data[i] * b.Data[i];

        int actual = VectorQuantizer.Int8DotProduct(a.Data, b.Data);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Int8DotProduct_EmptyVectors_ReturnsZero()
    {
        Assert.Equal(0, VectorQuantizer.Int8DotProduct(
            ReadOnlySpan<sbyte>.Empty, ReadOnlySpan<sbyte>.Empty));
    }

    [Fact]
    public void Int8DotProduct_SingleElement()
    {
        ReadOnlySpan<sbyte> a = stackalloc sbyte[] { 10 };
        ReadOnlySpan<sbyte> b = stackalloc sbyte[] { 5 };
        Assert.Equal(50, VectorQuantizer.Int8DotProduct(a, b));
    }

    [Fact]
    public void Int8DotProduct_NegativeValues()
    {
        ReadOnlySpan<sbyte> a = stackalloc sbyte[] { -128, 127, -1, 1 };
        ReadOnlySpan<sbyte> b = stackalloc sbyte[] { 127, -128, 1, -1 };

        int expected = (-128 * 127) + (127 * -128) + (-1 * 1) + (1 * -1);
        Assert.Equal(expected, VectorQuantizer.Int8DotProduct(a, b));
    }

    [Fact]
    public void Int8DotProduct_LargeVector_CorrectResult()
    {
        // 384 dimensions — tests SIMD path thoroughly
        var aFp32 = CreateRandomVector(384);
        var bFp32 = CreateRandomVector(384);
        var a = VectorQuantizer.Quantize(aFp32);
        var b = VectorQuantizer.Quantize(bFp32);

        int expected = 0;
        for (int i = 0; i < 384; i++)
            expected += a.Data[i] * b.Data[i];

        Assert.Equal(expected, VectorQuantizer.Int8DotProduct(a.Data, b.Data));
    }

    [Fact]
    public void ApproximateCosine_PreservesRanking()
    {
        // Create a query and two targets where one is clearly more similar
        var query = CreateRandomVector(384, seed: 42);
        var similar = AddNoise(query, 0.05f); // Small noise
        var different = CreateRandomVector(384, seed: 99); // Different random vector

        var qQuery = VectorQuantizer.Quantize(query);
        var qSimilar = VectorQuantizer.Quantize(similar);
        var qDifferent = VectorQuantizer.Quantize(different);

        float approxSimilar = VectorQuantizer.ApproximateCosine(qQuery, qSimilar);
        float approxDifferent = VectorQuantizer.ApproximateCosine(qQuery, qDifferent);

        // The ranking should be preserved: similar > different
        Assert.True(approxSimilar > approxDifferent,
            $"Similar ({approxSimilar}) should score higher than different ({approxDifferent})");
    }

    [Fact]
    public void SelfDot_ComputedCorrectly()
    {
        var fp32 = new float[] { -1f, 0f, 0.5f, 1f };
        var qv = VectorQuantizer.Quantize(fp32);

        int expected = 0;
        for (int i = 0; i < qv.Data.Length; i++)
            expected += qv.Data[i] * qv.Data[i];

        Assert.Equal(expected, qv.SelfDot);
    }

    [Fact]
    public void Quantize_ConstantVector_HandlesGracefully()
    {
        var fp32 = new float[] { 0.5f, 0.5f, 0.5f, 0.5f };
        var qv = VectorQuantizer.Quantize(fp32);

        // All elements should be the same quantized value
        Assert.All(qv.Data, d => Assert.Equal(qv.Data[0], d));
    }

    [Fact]
    public void Quantize_ZeroVector_HandlesGracefully()
    {
        var fp32 = new float[384];
        var qv = VectorQuantizer.Quantize(fp32);

        Assert.Equal(384, qv.Data.Length);
        Assert.All(qv.Data, d => Assert.Equal(qv.Data[0], d));
    }

    // ── Helpers ──

    private static float[] CreateRandomVector(int dim, int seed = 42)
    {
        var rng = new Random(seed);
        var v = new float[dim];
        for (int i = 0; i < dim; i++)
            v[i] = (float)(rng.NextDouble() * 2 - 1);
        return v;
    }

    private static float[] AddNoise(float[] v, float magnitude)
    {
        var rng = new Random(123);
        var result = new float[v.Length];
        for (int i = 0; i < v.Length; i++)
            result[i] = v[i] + (float)(rng.NextDouble() * 2 - 1) * magnitude;
        return result;
    }

    private static float ExactCosine(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }
}
