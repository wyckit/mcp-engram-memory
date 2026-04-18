using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// Retrieval-policy adjustments for the agent-outcome benchmark runners, validated by the
/// T2 expert panel (session t2-benchmark-design-2026-04-17). Kept in one place so the
/// offline and live runners stay in sync.
///
/// <list type="bullet">
/// <item><see cref="ShouldUseHybrid"/> — gates BM25 fusion at corpus size 50. On smaller
/// synthetic benchmark corpora, IDF weights collapse and RRF fusion pollutes vector rank.
/// This guard keeps <c>full_engram_no_hybrid</c> as a meaningful ablation rather than a
/// calibration artifact. Production retrieval on populated namespaces still gets BM25.</item>
/// <item><see cref="ResolveLifecycleContradictions"/> — when an archived entry is joined
/// by a <c>contradicts</c> edge to a live entry that is also in the candidate list, drop
/// the archived side. Prevents deep_recall resurrection of superseded facts from
/// competing with the authoritative LTM fact in contradiction-pair datasets.</item>
/// </list>
/// </summary>
internal static class BenchmarkPolicyPatches
{
    public const int HybridMinCorpusSize = 50;

    public static bool ShouldUseHybrid(AgentOutcomeDataset dataset)
        => dataset.SeedEntries.Count >= HybridMinCorpusSize;

    public static IReadOnlyList<string> ResolveLifecycleContradictions(
        IReadOnlyList<string> canonicalIds,
        AgentOutcomeDataset dataset)
    {
        if (canonicalIds.Count < 2) return canonicalIds;

        var archivedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in dataset.SeedEntries)
        {
            if (string.Equals(seed.LifecycleState, "archived", StringComparison.Ordinal))
                archivedIds.Add(seed.Id);
        }
        if (archivedIds.Count == 0) return canonicalIds;

        var present = new HashSet<string>(canonicalIds, StringComparer.Ordinal);
        var toDrop = new HashSet<string>(StringComparer.Ordinal);

        foreach (var edge in dataset.Edges)
        {
            if (!string.Equals(edge.Relation, "contradicts", StringComparison.Ordinal))
                continue;

            bool sourceArchived = archivedIds.Contains(edge.SourceId);
            bool targetArchived = archivedIds.Contains(edge.TargetId);
            if (sourceArchived == targetArchived) continue; // both live or both archived — no resolution

            bool bothPresent = present.Contains(edge.SourceId) && present.Contains(edge.TargetId);
            if (!bothPresent) continue;

            toDrop.Add(sourceArchived ? edge.SourceId : edge.TargetId);
        }

        if (toDrop.Count == 0) return canonicalIds;

        var filtered = new List<string>(canonicalIds.Count);
        foreach (var id in canonicalIds)
        {
            if (!toDrop.Contains(id))
                filtered.Add(id);
        }
        return filtered;
    }
}
