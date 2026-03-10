using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// A directed edge in the knowledge graph connecting two cognitive entries.
/// </summary>
public sealed class GraphEdge
{
    [JsonPropertyName("sourceId")]
    public string SourceId { get; }

    [JsonPropertyName("targetId")]
    public string TargetId { get; }

    [JsonPropertyName("relation")]
    public string Relation { get; }

    [JsonPropertyName("weight")]
    public float Weight { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; }

    [JsonConstructor]
    public GraphEdge(
        string sourceId,
        string targetId,
        string relation,
        float weight = 1.0f,
        Dictionary<string, string>? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("SourceId must not be empty.", nameof(sourceId));
        if (string.IsNullOrWhiteSpace(targetId))
            throw new ArgumentException("TargetId must not be empty.", nameof(targetId));
        if (string.IsNullOrWhiteSpace(relation))
            throw new ArgumentException("Relation must not be empty.", nameof(relation));

        SourceId = sourceId;
        TargetId = targetId;
        Relation = relation;
        Weight = Math.Clamp(weight, 0f, 1f);
        Metadata = metadata is not null ? new Dictionary<string, string>(metadata) : new();
    }
}
