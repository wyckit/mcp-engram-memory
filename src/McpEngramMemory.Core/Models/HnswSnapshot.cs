using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// Serializable snapshot of an HNSW graph topology.
/// Vectors are NOT stored — they are reconstructed from namespace entries on load.
/// This avoids duplication and ensures vectors stay in sync with entry data.
/// </summary>
public sealed class HnswSnapshot
{
    [JsonPropertyName("m")]
    public int M { get; set; }

    [JsonPropertyName("efConstruction")]
    public int EfConstruction { get; set; }

    [JsonPropertyName("entryPoint")]
    public int EntryPoint { get; set; } = -1;

    [JsonPropertyName("maxLevel")]
    public int MaxLevel { get; set; } = -1;

    /// <summary>Entry IDs in index order. Index position = internal node index.</summary>
    [JsonPropertyName("nodeIds")]
    public List<string> NodeIds { get; set; } = new();

    /// <summary>HNSW layer assignment per node (parallel to NodeIds).</summary>
    [JsonPropertyName("nodeLevels")]
    public List<int> NodeLevels { get; set; } = new();

    /// <summary>Adjacency lists: Connections[node][layer] = list of neighbor node indices.</summary>
    [JsonPropertyName("connections")]
    public List<List<List<int>>> Connections { get; set; } = new();

    /// <summary>Indices of soft-deleted nodes.</summary>
    [JsonPropertyName("deleted")]
    public List<int> Deleted { get; set; } = new();
}
