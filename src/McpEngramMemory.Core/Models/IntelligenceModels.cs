using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// Per-namespace decay configuration.
/// </summary>
public sealed class DecayConfig
{
    [JsonPropertyName("ns")]
    public string Ns { get; }

    [JsonPropertyName("decayRate")]
    public float DecayRate { get; set; } = 0.1f;

    [JsonPropertyName("reinforcementWeight")]
    public float ReinforcementWeight { get; set; } = 1.0f;

    [JsonPropertyName("stmThreshold")]
    public float StmThreshold { get; set; } = 2.0f;

    [JsonPropertyName("archiveThreshold")]
    public float ArchiveThreshold { get; set; } = -5.0f;

    /// <summary>Decay rate multiplier for STM entries. Higher = faster decay. Default 3.0 (half-life ~3h).</summary>
    [JsonPropertyName("stmDecayMultiplier")]
    public float StmDecayMultiplier { get; set; } = 3.0f;

    /// <summary>Decay rate multiplier for LTM entries. Default 1.0 (baseline).</summary>
    [JsonPropertyName("ltmDecayMultiplier")]
    public float LtmDecayMultiplier { get; set; } = 1.0f;

    /// <summary>Decay rate multiplier for archived entries. Lower = slower decay. Default 0.1.</summary>
    [JsonPropertyName("archivedDecayMultiplier")]
    public float ArchivedDecayMultiplier { get; set; } = 0.1f;

    [JsonConstructor]
    public DecayConfig(string ns, float decayRate = 0.1f, float reinforcementWeight = 1.0f,
        float stmThreshold = 2.0f, float archiveThreshold = -5.0f,
        float stmDecayMultiplier = 3.0f, float ltmDecayMultiplier = 1.0f,
        float archivedDecayMultiplier = 0.1f)
    {
        Ns = ns;
        DecayRate = decayRate;
        ReinforcementWeight = reinforcementWeight;
        StmThreshold = stmThreshold;
        ArchiveThreshold = archiveThreshold;
        StmDecayMultiplier = stmDecayMultiplier;
        LtmDecayMultiplier = ltmDecayMultiplier;
        ArchivedDecayMultiplier = archivedDecayMultiplier;
    }
}

/// <summary>
/// A pair of near-duplicate entries detected by similarity analysis.
/// </summary>
public sealed record DuplicatePair(
    [property: JsonPropertyName("entryA")] CognitiveEntryInfo EntryA,
    [property: JsonPropertyName("entryB")] CognitiveEntryInfo EntryB,
    [property: JsonPropertyName("similarity")] float Similarity);

/// <summary>
/// Result of a duplicate detection scan.
/// </summary>
public sealed record DuplicateDetectionResult(
    [property: JsonPropertyName("scannedCount")] int ScannedCount,
    [property: JsonPropertyName("duplicates")] IReadOnlyList<DuplicatePair> Duplicates,
    [property: JsonPropertyName("threshold")] float Threshold);

/// <summary>
/// A known contradiction between two entries.
/// </summary>
public sealed record ContradictionInfo(
    [property: JsonPropertyName("entryA")] CognitiveEntryInfo EntryA,
    [property: JsonPropertyName("entryB")] CognitiveEntryInfo EntryB,
    [property: JsonPropertyName("similarity")] float Similarity,
    [property: JsonPropertyName("source")] string Source);

/// <summary>
/// Result of contradiction surfacing.
/// </summary>
public sealed record ContradictionResult(
    [property: JsonPropertyName("contradictions")] IReadOnlyList<ContradictionInfo> Contradictions,
    [property: JsonPropertyName("graphEdgeCount")] int GraphEdgeCount,
    [property: JsonPropertyName("highSimilarityCount")] int HighSimilarityCount);

/// <summary>
/// Metadata recorded when a collapse is executed, enabling future reversal.
/// </summary>
public sealed class CollapseRecord
{
    [JsonPropertyName("collapseId")]
    public string CollapseId { get; }

    [JsonPropertyName("clusterId")]
    public string ClusterId { get; }

    [JsonPropertyName("summaryEntryId")]
    public string SummaryEntryId { get; }

    [JsonPropertyName("ns")]
    public string Ns { get; }

    [JsonPropertyName("memberIds")]
    public List<string> MemberIds { get; }

    [JsonPropertyName("previousStates")]
    public Dictionary<string, string> PreviousStates { get; }

    [JsonPropertyName("collapsedAt")]
    public DateTimeOffset CollapsedAt { get; }

    [JsonConstructor]
    public CollapseRecord(
        string collapseId, string clusterId, string summaryEntryId,
        string ns, List<string> memberIds,
        Dictionary<string, string> previousStates,
        DateTimeOffset collapsedAt)
    {
        CollapseId = collapseId;
        ClusterId = clusterId;
        SummaryEntryId = summaryEntryId;
        Ns = ns;
        MemberIds = memberIds;
        PreviousStates = previousStates;
        CollapsedAt = collapsedAt;
    }

    public CollapseRecord(
        string collapseId, string clusterId, string summaryEntryId,
        string ns, List<string> memberIds,
        Dictionary<string, string> previousStates)
    {
        CollapseId = collapseId;
        ClusterId = clusterId;
        SummaryEntryId = summaryEntryId;
        Ns = ns;
        MemberIds = memberIds;
        PreviousStates = previousStates;
        CollapsedAt = DateTimeOffset.UtcNow;
    }
}
