namespace McpEngramMemory.Core.Models;

/// <summary>
/// Int8 scalar-quantized representation of a float[] vector.
/// Stores 384 dimensions in 384 bytes (vs 1,536 bytes for FP32), a 4x reduction.
/// Uses min/max asymmetric quantization mapping [min, max] → [-128, 127].
/// </summary>
public sealed class QuantizedVector
{
    /// <summary>Quantized sbyte data (one per dimension).</summary>
    public sbyte[] Data { get; }

    /// <summary>Minimum value in the original float[] (for dequantization).</summary>
    public float Min { get; }

    /// <summary>Scale factor: 255 / (max - min) (for dequantization).</summary>
    public float Scale { get; }

    /// <summary>Precomputed self dot product (sum of Data[i]²) for fast norm calculation.</summary>
    public int SelfDot { get; }

    public QuantizedVector(sbyte[] data, float min, float scale)
    {
        Data = data;
        Min = min;
        Scale = scale;

        int selfDot = 0;
        for (int i = 0; i < data.Length; i++)
            selfDot += data[i] * data[i];
        SelfDot = selfDot;
    }
}
