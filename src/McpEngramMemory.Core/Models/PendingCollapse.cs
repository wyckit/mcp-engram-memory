namespace McpEngramMemory.Core.Models;

/// <summary>
/// Represents a dense cluster of entries detected by the accretion scanner,
/// awaiting LLM-generated summary and collapse.
/// </summary>
public sealed class PendingCollapse
{
    public string CollapseId { get; }
    public string Ns { get; }
    public List<string> MemberIds { get; }
    public float[] Centroid { get; }
    public DateTimeOffset DetectedAt { get; }
    public bool Dismissed { get; set; }

    public PendingCollapse(string collapseId, string ns, List<string> memberIds, float[] centroid)
    {
        CollapseId = collapseId;
        Ns = ns;
        MemberIds = memberIds;
        Centroid = centroid;
        DetectedAt = DateTimeOffset.UtcNow;
    }
}
