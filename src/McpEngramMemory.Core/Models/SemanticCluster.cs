using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// A semantic cluster grouping related cognitive entries with a computed centroid.
/// </summary>
public sealed class SemanticCluster
{
    [JsonPropertyName("clusterId")]
    public string ClusterId { get; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("ns")]
    public string Ns { get; }

    [JsonPropertyName("memberIds")]
    public List<string> MemberIds { get; }

    [JsonPropertyName("centroid")]
    public float[]? Centroid { get; set; }

    [JsonPropertyName("summaryEntryId")]
    public string? SummaryEntryId { get; set; }

    public SemanticCluster(
        string clusterId,
        string ns,
        List<string>? memberIds = null,
        string? label = null)
    {
        if (string.IsNullOrWhiteSpace(clusterId))
            throw new ArgumentException("ClusterId must not be empty.", nameof(clusterId));
        if (string.IsNullOrWhiteSpace(ns))
            throw new ArgumentException("Namespace must not be empty.", nameof(ns));

        ClusterId = clusterId;
        Ns = ns;
        MemberIds = memberIds ?? new();
        Label = label;
    }

    [JsonConstructor]
    public SemanticCluster(
        string clusterId,
        string? label,
        string ns,
        List<string> memberIds,
        float[]? centroid,
        string? summaryEntryId)
    {
        ClusterId = clusterId;
        Label = label;
        Ns = ns;
        MemberIds = memberIds ?? new();
        Centroid = centroid;
        SummaryEntryId = summaryEntryId;
    }
}
