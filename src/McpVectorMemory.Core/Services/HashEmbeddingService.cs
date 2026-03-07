namespace McpVectorMemory.Core.Services;

/// <summary>
/// A lightweight deterministic embedding service that produces vectors from text hashing.
/// Useful for testing, CI environments, or scenarios where the ONNX model is unavailable.
/// Produces consistent embeddings for identical text inputs.
/// NOT suitable for real semantic similarity — use OnnxEmbeddingService for production.
/// </summary>
public sealed class HashEmbeddingService : IEmbeddingService
{
    public int Dimensions { get; }

    public HashEmbeddingService(int dimensions = 384)
    {
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Dimensions must be positive.");
        Dimensions = dimensions;
    }

    public float[] Embed(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new float[Dimensions];

        var vector = new float[Dimensions];

        // Use a simple hash-based projection: for each dimension,
        // hash the text with a dimension-specific seed to produce a float.
        // This gives deterministic, roughly uniform output.
        for (int i = 0; i < Dimensions; i++)
        {
            int hash = HashCombine(text.GetHashCode(), i * 31 + 17);
            vector[i] = (hash & 0x7FFFFFFF) / (float)int.MaxValue * 2f - 1f; // [-1, 1]
        }

        // Normalize to unit vector (reuse SIMD-accelerated Norm)
        float norm = CognitiveIndex.Norm(vector);

        if (norm > 0f)
        {
            for (int i = 0; i < Dimensions; i++)
                vector[i] /= norm;
        }

        return vector;
    }

    private static int HashCombine(int h1, int h2)
    {
        // FNV-inspired hash combine
        unchecked
        {
            int hash = h1;
            hash = (hash ^ h2) * 16777619;
            hash ^= hash >> 15;
            hash *= -2048144789;
            hash ^= hash >> 13;
            hash *= -1028477387;
            hash ^= hash >> 16;
            return hash;
        }
    }
}
