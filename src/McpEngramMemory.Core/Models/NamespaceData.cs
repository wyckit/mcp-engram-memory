using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// Serialization container for all data within a single namespace.
/// Edges and clusters are stored in separate global files (_edges.json, _clusters.json).
/// </summary>
public sealed class NamespaceData
{
    [JsonPropertyName("storageVersion")]
    public int StorageVersion { get; set; } = 1;

    [JsonPropertyName("entries")]
    public List<CognitiveEntry> Entries { get; set; } = new();
}
