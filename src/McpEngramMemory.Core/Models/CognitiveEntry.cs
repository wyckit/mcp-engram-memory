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

    /// <summary>
    /// Occupancy witness, stamped by <c>CognitiveIndex</c> on every upsert — an EQUALITY-ONLY
    /// token, never ordered. Two occupations of the same (tenant, ns, id) slot always carry
    /// different values — unlike <see cref="CreatedAt"/>, which a same-tick replacement can
    /// repeat — so destructive maintenance can target exactly the version it staged. The
    /// stamping counter is seeded from a random 62-bit value at index start (see the rationale
    /// at the counters in <c>CognitiveIndex</c>): uniqueness holds across restarts AND across
    /// live processes sharing a store, but values are monotonic only within one process
    /// lifetime — nothing may compare them for order. In-place mutations (lifecycle, access
    /// counts, energy) deliberately do NOT move it: it witnesses replacement, not activity.
    /// 0 marks data persisted before the field existed.
    ///
    /// The setter is internal: a witness a caller can rewrite is not a witness. Only
    /// <c>CognitiveIndex</c> stamps it (and the serializer restores it); a caller that needs
    /// the value later must copy the NUMBER at observation time — the object in the index map
    /// is live, and holding it does not freeze this property.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("revision")]
    public long Revision { get; internal set; }

    /// <summary>
    /// Lifecycle witness, moved by <c>CognitiveIndex</c> whenever
    /// <see cref="LifecycleState"/> actually TRANSITIONS (never on energy or access updates,
    /// and never on a set to the state already held) — an EQUALITY-ONLY token like
    /// <see cref="Revision"/>, randomly seeded per process and never ordered.
    /// <see cref="Revision"/> deliberately ignores in-place activity, so reversal logic needs
    /// this second witness: an undo that archived an entry may restore it only while this
    /// value still equals the one its own archive installed — any later transition, manual or
    /// automatic, means the archived state belongs to newer work. Internal setter for the same
    /// reason as <see cref="Revision"/>; 0 marks data persisted before the field existed.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("lifecycleRevision")]
    public long LifecycleRevision { get; internal set; }

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

    /// <summary>
    /// The <see cref="SemanticCluster.CreationStamp"/> of the cluster incarnation this summary
    /// was stored FOR — set by <c>ClusterManager.StoreSummary</c> from the resident cluster.
    /// A conditional summary delete compares it so a replacement summary, stored by a
    /// recreated same-id cluster, is never taken down by the old incarnation's cleanup. Null
    /// on non-summary entries and on summaries persisted before the field existed.
    /// </summary>
    [JsonPropertyName("sourceClusterStamp")]
    public string? SourceClusterStamp { get; set; }

    /// <summary>
    /// The <see cref="SemanticCluster.InstanceId"/> of the exact PHYSICAL cluster object this
    /// summary was admitted by — set by <c>ClusterManager.StoreSummary</c> under the cluster
    /// lock. The lineage stamp above is REUSED across a collapse retry's re-created cluster,
    /// so stamp equality cannot distinguish "the object that admitted me" from "a later object
    /// of my lineage"; the instance can, and the summary CAS, the publish fence and the
    /// ownership read screens compare it. Null on non-summary entries and on summaries
    /// persisted before the field existed.
    /// </summary>
    [JsonPropertyName("sourceClusterInstance")]
    public string? SourceClusterInstance { get; set; }

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
        if (ns.Length > MaxNamespaceLength)
            throw new ArgumentException(
                $"Namespace must be at most {MaxNamespaceLength} characters (got {ns.Length}).", nameof(ns));

        // The namespace is concatenated with the tenant into a single storage partition key, so a
        // control character in it could forge a key that resolves to another tenant's partition.
        // Rejected here, on the way in, for the same reason as the length limit above.
        Tenancy.ValidatePartitionComponent(ns, nameof(ns));

        Id = id;
        Vector = (float[])vector.Clone();
        Ns = ns;
        // Single normalizer for every tenant-scoped model; it validates the trimmed value as a
        // partition component, so the tenant half of the key gets the same guarantee as ns above.
        TenantId = Tenancy.Normalize(tenantId);
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
        // Read path: normalize only, never validate — for the same reason MaxNamespaceLength is
        // enforced on ingest only. Tightening a tenant rule must never make already-stored data
        // unloadable. Tenancy.Normalize is deliberately NOT used here: it rejects over-long and
        // control-character tenants, and that rejection belongs on the way in, not on the way out.
        TenantId = string.IsNullOrWhiteSpace(tenantId) ? string.Empty : tenantId.Trim();
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

    /// <summary>
    /// Maximum namespace length, enforced on ingest only.
    ///
    /// The JSON storage backend maps a namespace to a filename, so an over-long name produces
    /// a path the OS rejects. That write happens on a debounced background timer, long after
    /// the tool call returned success, and the resulting IOException is only logged — so the
    /// entry, and every later write to that namespace, was silently never persisted and lost
    /// on restart. Rejecting at ingest turns silent data loss into an immediate, actionable
    /// error. 128 leaves ample room for the data directory within a conventional path budget
    /// while being far longer than any real namespace name.
    ///
    /// Deliberately NOT enforced in the JSON constructor: validation belongs on the way in,
    /// not on the way out, so tightening this limit can never make already-stored data
    /// unloadable.
    /// </summary>
    public const int MaxNamespaceLength = 128;

    /// <summary>
    /// Maximum length of a tenant identifier (matches the storage column width). Retained as an
    /// alias of <see cref="Tenancy.MaxTenantIdLength"/> for source compatibility — the limit itself
    /// lives with the normalizer that enforces it, so the two can never disagree.
    /// </summary>
    public const int MaxTenantIdLength = Tenancy.MaxTenantIdLength;
}
