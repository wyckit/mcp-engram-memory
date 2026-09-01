using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// A directed edge in the knowledge graph connecting two cognitive entries.
///
/// SERIALIZATION SHAPE: the parameterless <see cref="GraphEdge()"/> is the JSON constructor
/// and the properties are init-settable, so the READ path never validates — a stored row
/// whose endpoints bound JSON null must deserialize (the loaders quarantine it afterwards)
/// rather than throw and brick the whole edge file. The parameterized constructor is the
/// WRITE path and keeps the validation; new edges are only ever built through it.
/// </summary>
public sealed class GraphEdge
{
    private readonly string _tenantId = string.Empty;

    [JsonPropertyName("sourceId")]
    public string SourceId { get; init; } = null!;

    [JsonPropertyName("targetId")]
    public string TargetId { get; init; } = null!;

    /// <summary>
    /// Tenant partition this edge belongs to. Both endpoints resolve within this tenant; the graph
    /// never connects entries across tenants. Legacy edges default to <c>""</c>. Normalize-only on
    /// the way in (never validating — see the class remarks): a stored pre-validation tenant must
    /// load; validation for NEW edges happens at the graph's public boundaries.
    /// </summary>
    [JsonPropertyName("tenantId")]
    public string TenantId
    {
        get => _tenantId;
        init => _tenantId = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    [JsonPropertyName("relation")]
    public string Relation { get; init; } = null!;

    private float _weight = 1.0f;

    /// <summary>Clamped on EVERY set — the read path included, since the serializer writes
    /// through this setter: a stored NaN or out-of-range weight would otherwise flow into
    /// spreading-activation scoring and poison every result it touches.</summary>
    [JsonPropertyName("weight")]
    public float Weight
    {
        get => _weight;
        set => _weight = float.IsNaN(value) ? 1.0f : Math.Clamp(value, 0f, 1f);
    }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>The JSON (read) constructor — no validation, see the class remarks.</summary>
    [JsonConstructor]
    public GraphEdge()
    {
    }

    public GraphEdge(
        string sourceId,
        string targetId,
        string relation,
        float weight = 1.0f,
        Dictionary<string, string>? metadata = null,
        string? tenantId = null)
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
        TenantId = tenantId!;
    }
}
