using System.Text.Json;

namespace McpEngramMemory.Tools;

/// <summary>
/// Coerces a "list of ids" tool parameter from MCP tool input (any JSON value) into a
/// string array. Parameters of this shape are typed as <c>JsonElement?</c> so the MCP
/// SDK doesn't fail binding before the tool body runs: a marshalling failure surfaces
/// only as the SDK's generic "An error occurred invoking '{tool}'", which names neither
/// the parameter nor the shape it wanted. Binding tolerantly here lets the tool body
/// return a message that does both.
///
/// Accepted shapes: a JSON array of ids, a comma-separated string, or a single id.
/// Scalars become their literal text; nulls and blanks are skipped. Comma-separated
/// strings match the convention <c>cross_search</c> already uses for its namespace list.
/// Companion to <see cref="MetadataNormalizer"/>, which does the same for metadata bags.
/// </summary>
internal static class StringListNormalizer
{
    /// <summary>
    /// Normalizes <paramref name="input"/> into <paramref name="value"/>. Returns false
    /// only when the JSON shape can't represent a list of ids, in which case
    /// <paramref name="error"/> carries a message naming the parameter and the shapes
    /// it accepts. An absent, null, or empty input is not an error — it yields a null
    /// <paramref name="value"/>, matching an omitted optional parameter.
    /// </summary>
    public static bool TryNormalize(
        JsonElement? input,
        string parameterName,
        out string[]? value,
        out string? error)
    {
        value = null;
        error = null;

        if (input is not { } element) return true;

        switch (element.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return true;

            case JsonValueKind.String:
                value = SplitList(element.GetString());
                return true;

            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                value = new[] { element.GetRawText() };
                return true;

            case JsonValueKind.Array:
                return TryNormalizeArray(element, parameterName, out value, out error);

            default:
                error = Expected(parameterName, $"got a JSON {Describe(element.ValueKind)}");
                return false;
        }
    }

    private static bool TryNormalizeArray(
        JsonElement array, string parameterName, out string[]? value, out string? error)
    {
        value = null;
        error = null;

        var items = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.Null or JsonValueKind.Undefined:
                    continue;

                case JsonValueKind.String:
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) items.Add(text!.Trim());
                    continue;

                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    items.Add(item.GetRawText());
                    continue;

                default:
                    error = Expected(parameterName,
                        $"its array contained a nested {Describe(item.ValueKind)}");
                    return false;
            }
        }

        value = items.Count > 0 ? items.ToArray() : null;
        return true;
    }

    private static string[]? SplitList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts : null;
    }

    private static string Expected(string parameterName, string actual) =>
        $"{parameterName} must be a JSON array of ids, a comma-separated string, " +
        $"or a single id — {actual}.";

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
