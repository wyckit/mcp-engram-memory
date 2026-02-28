using McpVectorMemory;

namespace McpVectorMemory.Tests;

public class VectorMathTests
{
    // ── Dot ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Dot_IdenticalVectors_ReturnsSquaredMagnitude()
    {
        float[] v = { 3f, 4f };
        Assert.Equal(25f, VectorMath.Dot(v, v), precision: 5);
    }

    [Fact]
    public void Dot_OrthogonalVectors_ReturnsZero()
    {
        Assert.Equal(0f, VectorMath.Dot(new float[] { 1f, 0f }, new float[] { 0f, 1f }), precision: 5);
    }

    [Fact]
    public void Dot_OppositeVectors_ReturnsNegative()
    {
        Assert.Equal(-1f, VectorMath.Dot(new float[] { 1f, 0f }, new float[] { -1f, 0f }), precision: 5);
    }

    [Fact]
    public void Dot_DimensionMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            VectorMath.Dot(new float[] { 1f, 0f }, new float[] { 1f, 0f, 0f }));
    }

    [Fact]
    public void Dot_SingleDimension_Works()
    {
        Assert.Equal(6f, VectorMath.Dot(new float[] { 2f }, new float[] { 3f }), precision: 5);
    }

    [Fact]
    public void Dot_LargeVector_MatchesScalarResult()
    {
        // Use a vector large enough to exercise the SIMD path
        var rng = new Random(42);
        var a = new float[256];
        var b = new float[256];
        float expected = 0f;
        for (int i = 0; i < 256; i++)
        {
            a[i] = (float)(rng.NextDouble() * 2 - 1);
            b[i] = (float)(rng.NextDouble() * 2 - 1);
            expected += a[i] * b[i];
        }

        Assert.Equal(expected, VectorMath.Dot(a, b), precision: 1);
    }

    [Fact]
    public void Dot_AllZeros_ReturnsZero()
    {
        Assert.Equal(0f, VectorMath.Dot(new float[] { 0f, 0f, 0f }, new float[] { 0f, 0f, 0f }));
    }

    // ── Norm ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Norm_UnitVector_ReturnsOne()
    {
        Assert.Equal(1f, VectorMath.Norm(new float[] { 1f, 0f }), precision: 5);
    }

    [Fact]
    public void Norm_345Triangle_ReturnsFive()
    {
        Assert.Equal(5f, VectorMath.Norm(new float[] { 3f, 4f }), precision: 5);
    }

    [Fact]
    public void Norm_ZeroVector_ReturnsZero()
    {
        Assert.Equal(0f, VectorMath.Norm(new float[] { 0f, 0f }));
    }

    [Fact]
    public void Norm_NegativeComponents_SameAsMagnitude()
    {
        float n1 = VectorMath.Norm(new float[] { 3f, 4f });
        float n2 = VectorMath.Norm(new float[] { -3f, -4f });
        Assert.Equal(n1, n2, precision: 5);
    }

    [Fact]
    public void Norm_SingleDimension_ReturnsAbsoluteValue()
    {
        Assert.Equal(5f, VectorMath.Norm(new float[] { -5f }), precision: 5);
    }

    // ── CosineSimilarity ────────────────────────────────────────────────────

    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        Assert.Equal(1f, VectorMath.CosineSimilarity(new float[] { 1f, 2f, 3f }, new float[] { 1f, 2f, 3f }), precision: 5);
    }

    [Fact]
    public void CosineSimilarity_OppositeVectors_ReturnsNegativeOne()
    {
        Assert.Equal(-1f, VectorMath.CosineSimilarity(new float[] { 1f, 0f }, new float[] { -1f, 0f }), precision: 5);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        Assert.Equal(0f, VectorMath.CosineSimilarity(new float[] { 1f, 0f }, new float[] { 0f, 1f }), precision: 5);
    }

    [Fact]
    public void CosineSimilarity_ScaledVectors_ReturnsSameValue()
    {
        float sim1 = VectorMath.CosineSimilarity(new float[] { 1f, 2f }, new float[] { 3f, 4f });
        float sim2 = VectorMath.CosineSimilarity(new float[] { 10f, 20f }, new float[] { 30f, 40f });
        Assert.Equal(sim1, sim2, precision: 5);
    }

    [Fact]
    public void CosineSimilarity_ZeroVectorA_ReturnsZero()
    {
        Assert.Equal(0f, VectorMath.CosineSimilarity(new float[] { 0f, 0f }, new float[] { 1f, 2f }));
    }

    [Fact]
    public void CosineSimilarity_ZeroVectorB_ReturnsZero()
    {
        Assert.Equal(0f, VectorMath.CosineSimilarity(new float[] { 1f, 2f }, new float[] { 0f, 0f }));
    }

    [Fact]
    public void CosineSimilarity_BothZero_ReturnsZero()
    {
        Assert.Equal(0f, VectorMath.CosineSimilarity(new float[] { 0f, 0f }, new float[] { 0f, 0f }));
    }

    [Fact]
    public void CosineSimilarity_DimensionMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            VectorMath.CosineSimilarity(new float[] { 1f }, new float[] { 1f, 2f }));
    }

    [Fact]
    public void CosineSimilarity_ResultInRange()
    {
        // Arbitrary vectors — result must be in [-1, 1]
        var rng = new Random(42);
        for (int trial = 0; trial < 50; trial++)
        {
            var a = new float[10];
            var b = new float[10];
            for (int i = 0; i < 10; i++)
            {
                a[i] = (float)(rng.NextDouble() * 2 - 1);
                b[i] = (float)(rng.NextDouble() * 2 - 1);
            }
            float sim = VectorMath.CosineSimilarity(a, b);
            Assert.InRange(sim, -1f, 1f);
        }
    }

    // ── CosineDistance ───────────────────────────────────────────────────────

    [Fact]
    public void CosineDistance_IdenticalVectors_ReturnsZero()
    {
        Assert.Equal(0f, VectorMath.CosineDistance(new float[] { 1f, 2f }, new float[] { 1f, 2f }), precision: 5);
    }

    [Fact]
    public void CosineDistance_OppositeVectors_ReturnsTwo()
    {
        Assert.Equal(2f, VectorMath.CosineDistance(new float[] { 1f, 0f }, new float[] { -1f, 0f }), precision: 5);
    }

    [Fact]
    public void CosineDistance_OrthogonalVectors_ReturnsOne()
    {
        Assert.Equal(1f, VectorMath.CosineDistance(new float[] { 1f, 0f }, new float[] { 0f, 1f }), precision: 5);
    }

    [Fact]
    public void CosineDistance_ZeroVector_ReturnsOne()
    {
        Assert.Equal(1f, VectorMath.CosineDistance(new float[] { 0f, 0f }, new float[] { 1f, 2f }));
    }

    [Fact]
    public void CosineDistance_IsSimilarityComplement()
    {
        var a = new float[] { 1f, 2f, 3f };
        var b = new float[] { 4f, 5f, 6f };
        float sim = VectorMath.CosineSimilarity(a, b);
        float dist = VectorMath.CosineDistance(a, b);
        Assert.Equal(1f, sim + dist, precision: 5);
    }
}
