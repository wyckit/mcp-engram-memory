using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// JSON converter that serializes float[] as Base64 strings for compact disk I/O.
/// Reads both Base64 strings (new format) and JSON number arrays (legacy format)
/// for backwards compatibility with existing data files.
///
/// Space savings: a 384-dim float vector is ~2,500 chars as JSON numbers
/// vs ~2,048 chars as Base64 — plus faster parse/write since no float↔text conversion.
/// </summary>
public sealed class FloatArrayBase64Converter : JsonConverter<float[]>
{
    public override float[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        // New format: Base64-encoded raw bytes
        if (reader.TokenType == JsonTokenType.String)
        {
            var base64 = reader.GetString()!;
            var bytes = Convert.FromBase64String(base64);
            var result = new float[bytes.Length / sizeof(float)];
            MemoryMarshal.Cast<byte, float>(bytes.AsSpan()).CopyTo(result);
            return result;
        }

        // Legacy format: JSON number array [0.123, -0.456, ...]
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<float>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                    break;
                list.Add(reader.GetSingle());
            }
            return list.ToArray();
        }

        throw new JsonException($"Unexpected token {reader.TokenType} when reading float[]");
    }

    public override void Write(Utf8JsonWriter writer, float[] value, JsonSerializerOptions options)
    {
        var bytes = MemoryMarshal.AsBytes(value.AsSpan());
        writer.WriteBase64StringValue(bytes);
    }
}
