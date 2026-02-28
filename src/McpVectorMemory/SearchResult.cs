namespace McpVectorMemory;

/// <summary>
/// Result of a nearest-neighbor search query.
/// </summary>
public sealed record SearchResult(VectorEntry Entry, float Score);
