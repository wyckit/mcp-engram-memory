using System.Text.Json;
using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Tests;

public class FloatArrayBase64ConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new FloatArrayBase64Converter() }
    };

    [Fact]
    public void Write_ProducesBase64String()
    {
        var vector = new float[] { 1.0f, -2.5f, 0.0f, 3.14f };
        var json = JsonSerializer.Serialize(vector, Options);

        // Should be a JSON string (Base64), not an array
        Assert.StartsWith("\"", json);
        Assert.DoesNotContain("[", json);
    }

    [Fact]
    public void RoundTrip_Base64_ExactMatch()
    {
        var original = new float[] { 1.0f, -2.5f, 0.0f, float.MaxValue, float.MinValue, float.Epsilon };
        var json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<float[]>(json, Options);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void RoundTrip_LargeVector_ExactMatch()
    {
        var rng = new Random(42);
        var original = new float[384];
        for (int i = 0; i < 384; i++)
            original[i] = (float)(rng.NextDouble() * 2 - 1);

        var json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<float[]>(json, Options);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void Read_LegacyJsonArray_Works()
    {
        // Simulate old format: JSON number array
        var legacyJson = "[1.0, -2.5, 0.0, 3.14]";
        var restored = JsonSerializer.Deserialize<float[]>(legacyJson, Options);

        Assert.NotNull(restored);
        Assert.Equal(4, restored!.Length);
        Assert.Equal(1.0f, restored[0]);
        Assert.Equal(-2.5f, restored[1]);
        Assert.Equal(0.0f, restored[2]);
        Assert.Equal(3.14f, restored[3], 0.001f);
    }

    [Fact]
    public void Read_NullToken_ReturnsNull()
    {
        var json = "null";
        var result = JsonSerializer.Deserialize<float[]>(json, Options);
        Assert.Null(result);
    }

    [Fact]
    public void Base64_IsSmallerThanJsonArray()
    {
        var rng = new Random(42);
        var vector = new float[384];
        for (int i = 0; i < 384; i++)
            vector[i] = (float)(rng.NextDouble() * 2 - 1);

        var base64Json = JsonSerializer.Serialize(vector, Options);

        // Compare with default JSON array format
        var defaultOptions = new JsonSerializerOptions();
        var arrayJson = JsonSerializer.Serialize(vector, defaultOptions);

        // Base64 should be significantly smaller
        Assert.True(base64Json.Length < arrayJson.Length,
            $"Base64 ({base64Json.Length}) should be smaller than array ({arrayJson.Length})");
    }

    [Fact]
    public void CognitiveEntry_RoundTrip_WithBase64()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new FloatArrayBase64Converter() }
        };

        var vector = new float[] { 0.1f, 0.2f, 0.3f };
        var entry = new CognitiveEntry("test-id", vector, "test-ns", "test text");
        var json = JsonSerializer.Serialize(entry, options);
        var restored = JsonSerializer.Deserialize<CognitiveEntry>(json, options);

        Assert.NotNull(restored);
        Assert.Equal(entry.Id, restored!.Id);
        Assert.Equal(entry.Vector, restored.Vector);
        Assert.Equal(entry.Ns, restored.Ns);
        Assert.Equal(entry.Text, restored.Text);
    }

    [Fact]
    public void CognitiveEntry_LegacyFormat_StillLoads()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new FloatArrayBase64Converter() }
        };

        // Simulate a legacy JSON file with float array format
        var legacyJson = """
        {
            "id": "legacy-entry",
            "vector": [0.1, 0.2, 0.3],
            "ns": "test-ns",
            "text": "legacy text",
            "category": null,
            "metadata": {},
            "lifecycleState": "stm",
            "createdAt": "2024-01-01T00:00:00+00:00",
            "lastAccessedAt": "2024-01-01T00:00:00+00:00",
            "accessCount": 1,
            "activationEnergy": 0,
            "isSummaryNode": false,
            "sourceClusterId": null
        }
        """;

        var entry = JsonSerializer.Deserialize<CognitiveEntry>(legacyJson, options);

        Assert.NotNull(entry);
        Assert.Equal("legacy-entry", entry!.Id);
        Assert.Equal(3, entry.Vector.Length);
        Assert.Equal(0.1f, entry.Vector[0], 0.001f);
    }

    [Fact]
    public void NamespaceData_RoundTrip_WithBase64()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new FloatArrayBase64Converter() }
        };

        var data = new NamespaceData
        {
            Entries = new()
            {
                new CognitiveEntry("e1", new float[] { 0.1f, 0.2f }, "ns1", "text1"),
                new CognitiveEntry("e2", new float[] { 0.3f, 0.4f }, "ns1", "text2")
            }
        };

        var json = JsonSerializer.Serialize(data, options);
        var restored = JsonSerializer.Deserialize<NamespaceData>(json, options);

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Entries.Count);
        Assert.Equal(data.Entries[0].Vector, restored.Entries[0].Vector);
        Assert.Equal(data.Entries[1].Vector, restored.Entries[1].Vector);
    }
}
