namespace McpEngramMemory.Core.Models;

/// <summary>
/// Represents a dense cluster of entries detected by the accretion scanner,
/// awaiting LLM-generated summary and collapse.
/// </summary>
public sealed class PendingCollapse
{
    public string CollapseId { get; }
    public string Ns { get; }
    /// <summary>Tenant partition this pending collapse belongs to. Legacy detections default to <c>""</c>.</summary>
    public string TenantId { get; }
    public List<string> MemberIds { get; }
    public float[] Centroid { get; }
    public DateTimeOffset DetectedAt { get; }
    public bool Dismissed { get; set; }

    /// <summary>
    /// The cluster id the LATEST execution attempt minted (or reused) for this proposal — set
    /// under the scanner's lock the moment the attempt fixes its identity, before any side
    /// effect. In-memory only, like the proposal itself: after a crash the durable collapse
    /// record carries the same id, and dismissal is refused while such a record exists. It
    /// exists so a zero-admission attempt's empty cluster shell can be found by DISMISSAL
    /// without deriving the id — cluster ids carry a per-incarnation nonce precisely so they
    /// cannot be derived. Null until an execution attempt first runs.
    /// </summary>
    public string? ClusterId { get; set; }

    /// <summary>The incarnation stamp minted with <see cref="ClusterId"/> — see
    /// <see cref="CollapseRecord.ClusterStamp"/>. In-memory only, like the id.</summary>
    public string? ClusterStamp { get; set; }

    public PendingCollapse(string collapseId, string ns, List<string> memberIds, float[] centroid, string? tenantId = null)
    {
        CollapseId = collapseId;
        Ns = ns;
        MemberIds = memberIds;
        Centroid = centroid;
        DetectedAt = DateTimeOffset.UtcNow;
        TenantId = Tenancy.Normalize(tenantId);
    }
}
