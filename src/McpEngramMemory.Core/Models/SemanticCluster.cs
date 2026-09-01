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

    /// <summary>Tenant partition this cluster belongs to. Legacy clusters default to <c>""</c>.</summary>
    [JsonPropertyName("tenantId")]
    public string TenantId { get; }

    [JsonPropertyName("memberIds")]
    public List<string> MemberIds { get; }

    [JsonPropertyName("centroid")]
    public float[]? Centroid { get; set; }

    [JsonPropertyName("summaryEntryId")]
    public string? SummaryEntryId { get; set; }

    /// <summary>
    /// Incarnation witness minted when THIS cluster object's lineage was created — every
    /// creation gets a fresh one (<c>ClusterManager.CreateCluster</c>), so
    /// a cluster recreated under a previously-used id is distinguishable from the incarnation
    /// that id named before. Reversible maintenance (the accretion collapse record) persists
    /// the stamp write-ahead and its cleanup compares it atomically, sparing any same-id
    /// resident it never minted. Null on clusters persisted before the field existed.
    /// </summary>
    [JsonPropertyName("creationStamp")]
    public string? CreationStamp { get; set; }

    /// <summary>
    /// PHYSICAL-INSTANCE witness, distinct from <see cref="CreationStamp"/> on purpose: the
    /// stamp names a LINEAGE and is deliberately REUSED when a collapse retry re-creates its
    /// recorded cluster, while this id is minted fresh on EVERY creation and never reused —
    /// two cluster objects of the same lineage carry the same stamp but different instances.
    /// Writers that must fence "the exact cluster object that admitted me" (the summary
    /// store's CAS and publish) compare the instance; record-driven cleanup that owns a
    /// lineage keeps comparing the stamp it minted. Carried through every edit
    /// (<c>ClusterManager.Replace</c>); null on clusters persisted before the field existed.
    /// </summary>
    [JsonPropertyName("instanceId")]
    public string? InstanceId { get; set; }

    public SemanticCluster(
        string clusterId,
        string ns,
        List<string>? memberIds = null,
        string? label = null,
        string? tenantId = null)
    {
        if (string.IsNullOrWhiteSpace(clusterId))
            throw new ArgumentException("ClusterId must not be empty.", nameof(clusterId));
        if (string.IsNullOrWhiteSpace(ns))
            throw new ArgumentException("Namespace must not be empty.", nameof(ns));

        ClusterId = clusterId;
        Ns = ns;
        MemberIds = memberIds ?? new();
        Label = label;
        TenantId = Tenancy.Normalize(tenantId);
    }

    [JsonConstructor]
    public SemanticCluster(
        string clusterId,
        string? label,
        string ns,
        List<string> memberIds,
        float[]? centroid,
        string? summaryEntryId,
        string? tenantId = null)
    {
        ClusterId = clusterId;
        Label = label;
        Ns = ns;
        MemberIds = memberIds ?? new();
        Centroid = centroid;
        SummaryEntryId = summaryEntryId;
        // Read path: normalize only, never validate (see CognitiveEntry's read constructor).
        // Tenancy.Normalize throws on over-long/control-character tenants, and System.Text.Json
        // does not wrap constructor exceptions — a validating read ctor makes one poisoned row
        // render the whole store unloadable. Validation belongs on the way in only.
        TenantId = string.IsNullOrWhiteSpace(tenantId) ? string.Empty : tenantId.Trim();
    }
}
