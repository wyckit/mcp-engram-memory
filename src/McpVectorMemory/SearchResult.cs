using System.Text.Json.Serialization;

namespace McpVectorMemory;

/// <summary>
/// Result of a nearest-neighbor search query.
/// </summary>
public sealed record SearchResult(
    [property: JsonPropertyName("entry")] VectorEntry Entry,
    [property: JsonPropertyName("score")] float Score);
