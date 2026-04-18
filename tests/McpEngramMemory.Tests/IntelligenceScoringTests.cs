using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Evaluation;
using Xunit;

namespace McpEngramMemory.Tests;

public class IntelligenceScoringTests
{
    private static readonly IReadOnlyList<OutcomeGraphEdgeSeed> NoEdges = Array.Empty<OutcomeGraphEdgeSeed>();

    [Fact]
    public void LegacyTask_AllMetricsDefaultToIdentity()
    {
        var task = new AgentOutcomeTask(
            TaskId: "legacy",
            QueryText: "q",
            RequiredMemoryIds: new[] { "a", "b" });

        var scores = IntelligenceScoring.Compute(task, new[] { "a", "b" }, new[] { "a", "b" }, NoEdges);

        Assert.Equal(1f, scores.ReasoningPathValidity);
        Assert.Equal(1f, scores.DependencyCompletionScore);
        Assert.Equal(0f, scores.StaleMemoryPenalty);
        Assert.Equal(1f, scores.MinimalEvidenceScore);
        Assert.Equal(1f, scores.NoiseResistanceScore);
        Assert.Equal(1f, scores.ContradictionHandlingScore);
    }

    [Fact]
    public void ReasoningPathValidity_MultiplicativeCoverageAndCoherence()
    {
        var edges = new[]
        {
            new OutcomeGraphEdgeSeed("s1", "s2", "depends_on"),
            new OutcomeGraphEdgeSeed("s2", "s3", "depends_on"),
            new OutcomeGraphEdgeSeed("s3", "s4", "depends_on")
        };
        var task = new AgentOutcomeTask(
            TaskId: "chain",
            QueryText: "q",
            RequiredMemoryIds: new[] { "s1", "s2", "s3", "s4" },
            OrderedSteps: new[] { "s1", "s2", "s3", "s4" });

        // Full chain cited + fully edge-connected → 1.0
        var full = IntelligenceScoring.Compute(task, new[] { "s1", "s2", "s3", "s4" }, new[] { "s1", "s2", "s3", "s4" }, edges);
        Assert.Equal(1f, full.ReasoningPathValidity);

        // Coverage 0.5, coherence 1.0 (remaining pair is edge-connected) → 0.5
        var half = IntelligenceScoring.Compute(task, new[] { "s1", "s2" }, new[] { "s1", "s2" }, edges);
        Assert.Equal(0.5f, half.ReasoningPathValidity, precision: 3);

        // No coherence (no edges provided) → 0
        var noEdges = IntelligenceScoring.Compute(task, new[] { "s1", "s2", "s3", "s4" }, new[] { "s1", "s2", "s3", "s4" }, NoEdges);
        Assert.Equal(0f, noEdges.ReasoningPathValidity);
    }

    [Fact]
    public void ContradictionHandlingScore_ThreeTierRubric()
    {
        var task = new AgentOutcomeTask(
            TaskId: "contra",
            QueryText: "q",
            RequiredMemoryIds: new[] { "new" },
            StaleMemoryIds: new[] { "old" });

        // Detected + resolved (winner only) → 1.0
        var resolved = IntelligenceScoring.Compute(task, new[] { "new" }, new[] { "new" }, NoEdges);
        Assert.Equal(1f, resolved.ContradictionHandlingScore);

        // Both cited (uncertainty acknowledged) → 0.5
        var both = IntelligenceScoring.Compute(task, new[] { "new", "old" }, new[] { "new", "old" }, NoEdges);
        Assert.Equal(0.5f, both.ContradictionHandlingScore);

        // Only stale cited → 0.0
        var stale = IntelligenceScoring.Compute(task, new[] { "old" }, new[] { "old" }, NoEdges);
        Assert.Equal(0f, stale.ContradictionHandlingScore);
        Assert.Equal(1f, stale.StaleMemoryPenalty);
    }

    [Fact]
    public void NoiseResistance_SplitsRetrievalAndCitationPurity()
    {
        var task = new AgentOutcomeTask(
            TaskId: "noise",
            QueryText: "q",
            RequiredMemoryIds: new[] { "real" },
            DistractorMemoryIds: new[] { "d1", "d2" });

        // Clean: no distractors in context, none cited → 1.0
        var clean = IntelligenceScoring.Compute(task, new[] { "real" }, new[] { "real" }, NoEdges);
        Assert.Equal(1f, clean.NoiseResistanceScore);

        // Both distractors in context + cited → 0.0
        var worst = IntelligenceScoring.Compute(task, new[] { "real", "d1", "d2" }, new[] { "d1", "d2" }, NoEdges);
        Assert.Equal(0f, worst.NoiseResistanceScore);

        // Distractors in context but model didn't cite them → 0.6 * 0 + 0.4 * 1 = 0.4
        var ignored = IntelligenceScoring.Compute(task, new[] { "real", "d1", "d2" }, new[] { "real" }, NoEdges);
        Assert.Equal(0.4f, ignored.NoiseResistanceScore, precision: 3);
    }

    [Fact]
    public void MinimalEvidence_QuadraticPenaltyWithGraceWindow()
    {
        var task = new AgentOutcomeTask(
            TaskId: "min-evidence",
            QueryText: "q",
            RequiredMemoryIds: new[] { "a", "b" },
            MinEvidence: 2);

        // Exactly minimum → 1.0
        Assert.Equal(1f, IntelligenceScoring.Compute(task, new[] { "a", "b" }, new[] { "a", "b" }, NoEdges).MinimalEvidenceScore);

        // Minimum + 1 grace → 1.0
        Assert.Equal(1f, IntelligenceScoring.Compute(task, new[] { "a", "b", "c" }, new[] { "a", "b", "c" }, NoEdges).MinimalEvidenceScore);

        // Over by 2 (excess=1 after grace): 1 - (1/2)^2 = 0.75
        Assert.Equal(0.75f, IntelligenceScoring.Compute(task, new[] { "a", "b", "c", "d" }, new[] { "a", "b", "c", "d" }, NoEdges).MinimalEvidenceScore, precision: 3);

        // Under-citation: 1 cited / 2 required → 0.5 linear
        Assert.Equal(0.5f, IntelligenceScoring.Compute(task, new[] { "a" }, new[] { "a" }, NoEdges).MinimalEvidenceScore, precision: 3);
    }

    [Fact]
    public void NoiseResistanceRanked_UsesRetrievalOrder()
    {
        var task = new AgentOutcomeTask(
            TaskId: "ranked",
            QueryText: "q",
            RequiredMemoryIds: new[] { "real" },
            DistractorMemoryIds: new[] { "d1", "d2" });

        // Correct at rank 0, no distractors outrank → 1.0
        var best = IntelligenceScoring.Compute(task, new[] { "real", "d1", "d2" }, new[] { "real" }, NoEdges);
        Assert.Equal(1f, best.NoiseResistanceScoreRanked, precision: 3);

        // Correct at rank 1 with 1/2 distractors outranking → (1/2) * (1 - 1/2) = 0.25
        var mid = IntelligenceScoring.Compute(task, new[] { "d1", "real", "d2" }, new[] { "real" }, NoEdges);
        Assert.Equal(0.25f, mid.NoiseResistanceScoreRanked, precision: 3);

        // Correct at rank 2 with 2/2 distractors outranking → (1/3) * 0 = 0.0
        var worst = IntelligenceScoring.Compute(task, new[] { "d1", "d2", "real" }, new[] { "real" }, NoEdges);
        Assert.Equal(0f, worst.NoiseResistanceScoreRanked, precision: 3);

        // Correct never retrieved → 0
        var missed = IntelligenceScoring.Compute(task, new[] { "d1", "d2" }, Array.Empty<string>(), NoEdges);
        Assert.Equal(0f, missed.NoiseResistanceScoreRanked, precision: 3);
    }

    [Fact]
    public void AdjustSuccessScore_PreservesLegacyScore()
    {
        var legacy = new AgentOutcomeTask(
            TaskId: "legacy",
            QueryText: "q",
            RequiredMemoryIds: new[] { "a" });
        var scores = new IntelligenceScoring.Scores(1f, 1f, 0f, 1f, 1f, 1f, 1f);

        Assert.Equal(0.8f, IntelligenceScoring.AdjustSuccessScore(0.8f, scores, legacy), precision: 3);
    }

    [Fact]
    public void AdjustSuccessScore_GatesChainTaskByRPV()
    {
        var chain = new AgentOutcomeTask(
            TaskId: "chain",
            QueryText: "q",
            RequiredMemoryIds: new[] { "a", "b" },
            OrderedSteps: new[] { "a", "b" });

        // RPV=1 → pass-through
        var full = new IntelligenceScoring.Scores(1f, 1f, 0f, 1f, 1f, 1f, 1f);
        Assert.Equal(1f, IntelligenceScoring.AdjustSuccessScore(1f, full, chain), precision: 3);

        // RPV=0 → base * 0.6
        var broken = new IntelligenceScoring.Scores(0f, 0f, 0f, 1f, 1f, 1f, 1f);
        Assert.Equal(0.6f, IntelligenceScoring.AdjustSuccessScore(1f, broken, chain), precision: 3);
    }

    [Fact]
    public void AdjustSuccessScore_PenalizesStaleCitation()
    {
        var contra = new AgentOutcomeTask(
            TaskId: "contra",
            QueryText: "q",
            RequiredMemoryIds: new[] { "new" },
            StaleMemoryIds: new[] { "old" });

        var scores = new IntelligenceScoring.Scores(1f, 1f, StaleMemoryPenalty: 1f, 1f, 1f, 1f, 0f);
        Assert.Equal(0.75f, IntelligenceScoring.AdjustSuccessScore(1f, scores, contra), precision: 3);
    }
}
