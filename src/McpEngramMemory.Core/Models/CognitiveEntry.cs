using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// A cognitive memory entry with namespace isolation, categorical metadata, and lifecycle tracking.
/// </summary>
public sealed class CognitiveEntry
{
    [JsonPropertyName("id")]
    public string Id { get; }

    [JsonPropertyName("vector")]
    public float[] Vector { get; }

    [JsonPropertyName("text")]
    public string? Text { get; }

    // Layer 1: Categorical Storage
    [JsonPropertyName("ns")]
    public string Ns { get; }

    /// <summary>
    /// Optional tenant isolation key. Defaults to <c>""</c> (empty string) which denotes the
    /// legacy single-tenant partition — fully backward-compatible for existing consumers that
    /// never supply a tenant. When set, the entry is scoped to that tenant in the storage layer.
    /// Max length 64. See <c>docs/tenant-isolation-design.md</c>.
    /// </summary>
    [JsonPropertyName("tenantId")]
    public string TenantId { get; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; }

    /// <summary>
    /// Searchable keyword aliases for document enrichment.
    /// BM25 indexes these alongside the main text to bridge vocabulary gaps.
    /// </summary>
    [JsonPropertyName("keywords")]
    public string? Keywords { get; set; }

    // Layer 4: Cognitive Lifecycle
    [JsonPropertyName("lifecycleState")]
    public string LifecycleState { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("lastAccessedAt")]
    public DateTimeOffset LastAccessedAt { get; set; }

    [JsonPropertyName("accessCount")]
    public int AccessCount { get; set; }

    [JsonPropertyName("activationEnergy")]
    public float ActivationEnergy { get; set; }

    // Layer 3: Summary node flag
    [JsonPropertyName("isSummaryNode")]
    public bool IsSummaryNode { get; set; }

    [JsonPropertyName("sourceClusterId")]
    public string? SourceClusterId { get; set; }

    public CognitiveEntry(
        string id,
        float[] vector,
        string ns,
        string? text = null,
        string? category = null,
        Dictionary<string, string>? metadata = null,
        string lifecycleState = "stm",
        string? keywords = null,
        string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id must not be empty.", nameof(id));
        if (vector is null || vector.Length == 0)
            throw new ArgumentException("Vector must not be null or empty.", nameof(vector));
        if (string.IsNullOrWhiteSpace(ns))
            throw new ArgumentException("Namespace must not be empty.", nameof(ns));

        Id = id;
        Vector = (float[])vector.Clone();
        Ns = ns;
        TenantId = NormalizeTenant(tenantId);
        Text = text;
        Category = category;
        Metadata = metadata is not null ? new Dictionary<string, string>(metadata) : new();
        LifecycleState = lifecycleState;
        CreatedAt = DateTimeOffset.UtcNow;
        LastAccessedAt = DateTimeOffset.UtcNow;
        AccessCount = 1;
        ActivationEnergy = 0f;
        Keywords = keywords;
    }

    [JsonConstructor]
    public CognitiveEntry(
        string id,
        float[] vector,
        string ns,
        string? text,
        string? category,
        Dictionary<string, string> metadata,
        string lifecycleState,
        DateTimeOffset createdAt,
        DateTimeOffset lastAccessedAt,
        int accessCount,
        float activationEnergy,
        bool isSummaryNode,
        string? sourceClusterId,
        string? keywords = null,
        string? tenantId = null)
    {
        Id = id;
        Vector = vector;
        Ns = ns;
        TenantId = NormalizeTenant(tenantId);
        Text = text;
        Category = category;
        Metadata = metadata ?? new();
        LifecycleState = lifecycleState;
        CreatedAt = createdAt;
        LastAccessedAt = lastAccessedAt;
        AccessCount = accessCount;
        ActivationEnergy = activationEnergy;
        IsSummaryNode = isSummaryNode;
        SourceClusterId = sourceClusterId;
        Keywords = keywords;
    }

    /// <summary>Maximum length of a tenant identifier (matches the storage column width).</summary>
    public const int MaxTenantIdLength = 64;

    /// <summary>
    /// Normalizes a tenant identifier: null/whitespace collapses to the legacy empty-string
    /// tenant, otherwise the value is trimmed. Throws when the value exceeds
    /// <see cref="MaxTenantIdLength"/> so tenant keys never silently truncate.
    /// </summary>
    private static string NormalizeTenant(string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return string.Empty;

        var trimmed = tenantId.Trim();
        if (trimmed.Length > MaxTenantIdLength)
            throw new ArgumentException(
                $"TenantId must be at most {MaxTenantIdLength} characters.", nameof(tenantId));
        return trimmed;
    }
}
