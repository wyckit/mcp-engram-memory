using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// T2 intelligence metrics for agent-outcome benchmarks. Each metric is computed from a task's
/// declared requirements, the retrieval-layer <c>contextIds</c> (what the policy fed the model),
/// the model's <c>citedIds</c> (what it actually used), and the dataset graph <c>edges</c>.
///
/// Metric panel (expert-consult 2026-04-17, session t2-benchmark-design-2026-04-17):
/// - <c>ReasoningPathValidity</c>: coverage * coherence. Coverage = fraction of <c>OrderedSteps</c>
///   cited; coherence = fraction of consecutive ordered-step pairs that have a graph edge in
///   either direction. Multiplicative so both must be high. No OrderedSteps ⇒ 1.0.
/// - <c>DependencyCompletionScore</c>: fraction of <c>OrderedSteps</c> present in cited IDs. Simpler
///   than RPV — just "did the chain complete?" No OrderedSteps ⇒ 1.0.
/// - <c>StaleMemoryPenalty</c>: fraction of <c>StaleMemoryIds</c> that were cited. 0 is perfect.
/// - <c>MinimalEvidenceScore</c>: quadratic penalty with +1 grace window over <c>MinEvidence</c>
///   (defaults to RequiredMemoryIds.Count). Under-citation also penalized linearly.
/// - <c>NoiseResistanceScore</c>: 0.6·retrieval_purity + 0.4·citation_purity, where each "purity"
///   is the fraction of <c>DistractorMemoryIds</c> absent from the context or citations.
/// - <c>ContradictionHandlingScore</c>: three-tier rubric — 1.0 if the winner (required IDs) is
///   cited and no stale IDs are cited; 0.5 if both winner and stale are cited (uncertainty
///   acknowledged); 0.0 if stale is cited without the winner. Requires both Required and Stale to
///   be non-empty; otherwise 1.0.
/// </summary>
public static class IntelligenceScoring
{
    public readonly record struct Scores(
        float ReasoningPathValidity,
        float DependencyCompletionScore,
        float StaleMemoryPenalty,
        float MinimalEvidenceScore,
        float NoiseResistanceScore,
        float NoiseResistanceScoreRanked,
        float ContradictionHandlingScore);

    public static Scores Compute(
        AgentOutcomeTask task,
        IReadOnlyCollection<string> contextIds,
        IReadOnlyCollection<string> citedIds,
        IReadOnlyList<OutcomeGraphEdgeSeed> edges)
    {
        var cited = citedIds as HashSet<string> ?? new HashSet<string>(citedIds, StringComparer.Ordinal);
        var context = contextIds as HashSet<string> ?? new HashSet<string>(contextIds, StringComparer.Ordinal);
        var contextOrdered = contextIds as IReadOnlyList<string> ?? contextIds.ToList();

        return new Scores(
            ReasoningPathValidity: ComputeReasoningPathValidity(task, cited, edges),
            DependencyCompletionScore: ComputeDependencyCompletion(task, cited),
            StaleMemoryPenalty: ComputeStalePenalty(task, cited),
            MinimalEvidenceScore: ComputeMinimalEvidence(task, cited),
            NoiseResistanceScore: ComputeNoiseResistance(task, context, cited),
            NoiseResistanceScoreRanked: ComputeNoiseResistanceRanked(task, contextOrdered),
            ContradictionHandlingScore: ComputeContradictionHandling(task, cited));
    }

    private static float ComputeReasoningPathValidity(
        AgentOutcomeTask task,
        HashSet<string> cited,
        IReadOnlyList<OutcomeGraphEdgeSeed> edges)
    {
        var steps = task.OrderedSteps;
        if (steps is null || steps.Count == 0) return 1f;

        float coverage = (float)steps.Count(cited.Contains) / steps.Count;
        if (steps.Count < 2) return coverage;

        var edgeSet = BuildUndirectedEdgeSet(edges);
        int validPairs = 0;
        for (int i = 0; i < steps.Count - 1; i++)
        {
            if (edgeSet.Contains((steps[i], steps[i + 1])))
                validPairs++;
        }

        float coherence = (float)validPairs / (steps.Count - 1);
        return coverage * coherence;
    }

    private static float ComputeDependencyCompletion(AgentOutcomeTask task, HashSet<string> cited)
    {
        var steps = task.OrderedSteps;
        if (steps is null || steps.Count == 0) return 1f;
        return (float)steps.Count(cited.Contains) / steps.Count;
    }

    private static float ComputeStalePenalty(AgentOutcomeTask task, HashSet<string> cited)
    {
        var stale = task.StaleMemoryIds;
        if (stale is null || stale.Count == 0) return 0f;
        return (float)stale.Count(cited.Contains) / stale.Count;
    }

    private static float ComputeMinimalEvidence(AgentOutcomeTask task, HashSet<string> cited)
    {
        int required = task.RequiredMemoryIds.Count;
        int minEvidence = task.MinEvidence ?? required;
        if (minEvidence <= 0) return 1f;

        int citedCount = cited.Count;

        if (citedCount < minEvidence)
            return Math.Clamp((float)citedCount / minEvidence, 0f, 1f);

        // +1 grace window
        int excess = citedCount - minEvidence - 1;
        if (excess <= 0) return 1f;

        float normalizedExcess = (float)excess / Math.Max(1, minEvidence);
        return Math.Clamp(1f - normalizedExcess * normalizedExcess, 0f, 1f);
    }

    private static float ComputeNoiseResistance(
        AgentOutcomeTask task,
        HashSet<string> context,
        HashSet<string> cited)
    {
        var distractors = task.DistractorMemoryIds;
        if (distractors is null || distractors.Count == 0) return 1f;

        int contextHits = distractors.Count(context.Contains);
        int citationHits = distractors.Count(cited.Contains);

        float retrievalPurity = 1f - (float)contextHits / distractors.Count;
        float citationPurity = 1f - (float)citationHits / distractors.Count;
        return 0.6f * retrievalPurity + 0.4f * citationPurity;
    }

    /// <summary>
    /// Rank-weighted NRS (expert panel Q3 follow-up). Unlike the binary NRS, this metric uses
    /// the retrieval order so graph/lifecycle demotions move the needle even in the offline
    /// proxy where <c>context == cited</c>.
    /// <para>
    /// <c>NRS_rw = reciprocal_rank_of_first_required × (1 - outrank_penalty)</c>
    /// where <c>outrank_penalty = distractors_before_first_required / total_distractors</c>.
    /// </para>
    /// <list type="bullet">
    /// <item>Correct answer retrieved at rank 0 with no distractor outranking it → 1.0</item>
    /// <item>Correct at rank 2 with one of two distractors above it → (1/3) × (1 - 1/2) = 0.167</item>
    /// <item>Correct never retrieved → 0.0</item>
    /// </list>
    /// </summary>
    private static float ComputeNoiseResistanceRanked(
        AgentOutcomeTask task,
        IReadOnlyList<string> contextOrdered)
    {
        var distractors = task.DistractorMemoryIds;
        if (distractors is null || distractors.Count == 0) return 1f;

        var requiredSet = new HashSet<string>(task.RequiredMemoryIds, StringComparer.Ordinal);
        var distractorSet = new HashSet<string>(distractors, StringComparer.Ordinal);

        int rankOfFirstCorrect = -1;
        int distractorsOutranking = 0;

        for (int i = 0; i < contextOrdered.Count; i++)
        {
            var id = contextOrdered[i];
            if (rankOfFirstCorrect == -1 && requiredSet.Contains(id))
                rankOfFirstCorrect = i;
            else if (rankOfFirstCorrect == -1 && distractorSet.Contains(id))
                distractorsOutranking++;
        }

        if (rankOfFirstCorrect < 0) return 0f;

        float reciprocalRank = 1f / (1 + rankOfFirstCorrect);
        float outrankPenalty = (float)distractorsOutranking / distractors.Count;
        return Math.Clamp(reciprocalRank * (1f - outrankPenalty), 0f, 1f);
    }

    private static float ComputeContradictionHandling(AgentOutcomeTask task, HashSet<string> cited)
    {
        var stale = task.StaleMemoryIds;
        var required = task.RequiredMemoryIds;
        if (stale is null || stale.Count == 0 || required.Count == 0) return 1f;

        bool citedWinner = required.Any(cited.Contains);
        bool citedStale = stale.Any(cited.Contains);

        return (citedWinner, citedStale) switch
        {
            (true, false) => 1f,   // detected + resolved
            (true, true) => 0.5f,  // uncertainty acknowledged
            (false, true) => 0f,   // blended stale, missed winner
            (false, false) => 0f   // missed both — failed detection
        };
    }

    public static float AdjustSuccessScore(float baseSuccess, in Scores scores, AgentOutcomeTask task)
    {
        // Gentle blend: base success (coverage-driven) stays primary; RPV acts as a gate for
        // ordered-chain tasks and StalePenalty as a deduction. Preserves legacy dataset scores.
        bool hasChain = task.OrderedSteps is { Count: > 1 };
        bool hasStale = task.StaleMemoryIds is { Count: > 0 };

        float gated = hasChain
            ? baseSuccess * (0.6f + 0.4f * scores.ReasoningPathValidity)
            : baseSuccess;

        if (hasStale)
            gated -= 0.25f * scores.StaleMemoryPenalty;

        return Math.Clamp(gated, 0f, 1f);
    }

    private static HashSet<(string, string)> BuildUndirectedEdgeSet(
        IReadOnlyList<OutcomeGraphEdgeSeed> edges)
    {
        var set = new HashSet<(string, string)>();
        foreach (var edge in edges)
        {
            set.Add((edge.SourceId, edge.TargetId));
            set.Add((edge.TargetId, edge.SourceId));
        }
        return set;
    }
}
