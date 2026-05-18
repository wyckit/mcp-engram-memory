using System.Text.Json;

namespace McpEngramMemory.Tools;

/// <summary>
/// Coerces metadata bags from MCP tool input (any JSON value per key) into the
/// flat string-valued dictionary that <c>CognitiveEntry.Metadata</c> stores.
/// Tool parameters are typed as <c>Dictionary&lt;string, JsonElement&gt;?</c> so the MCP
/// SDK doesn't fail binding before the tool body runs when a caller sends an
/// array or nested object. Scalars pass through as their literal text; arrays
/// and objects are serialized to compact JSON.
/// </summary>
internal static class MetadataNormalizer
{
    public static Dictionary<string, string>? Normalize(Dictionary<string, JsonElement>? input)
    {
        if (input is null || input.Count == 0) return null;

        var result = new Dictionary<string, string>(input.Count);
        foreach (var (key, value) in input)
        {
            result[key] = ToStringValue(value);
        }
        return result;
    }

    private static string ToStringValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        _ => value.GetRawText(),
    };
}
