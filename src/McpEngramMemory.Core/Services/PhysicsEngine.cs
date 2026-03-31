using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services;

/// <summary>
/// Physics-based re-ranking engine that computes gravitational force for memory retrieval.
/// Provides "Slingshot" output: Asteroid (closest semantic match) and Sun (highest gravitational pull).
/// Supports temperature-blended scoring for MSA-competitive cognitive retrieval.
/// </summary>
public sealed class PhysicsEngine
{
    private const float MinDistance = 0.001f;

    private static readonly Dictionary<string, float> TierWeights = new()
    {
        ["stm"] = 1.0f,
        ["ltm"] = 2.0f,
        ["archived"] = 0.5f
    };

    /// <summary>
    /// Edge-type weights for spreading activation energy transfer.
    /// Higher weight = more energy transferred along this edge type.
    /// </summary>
    public static readonly Dictionary<string, float> EdgeTypeWeights = new()
    {
        ["depends_on"] = 0.9f,
        ["parent_child"] = 0.8f,
        ["elaborates"] = 0.7f,
        ["cross_reference"] = 0.6f,
        ["similar_to"] = 0.5f,
        ["contradicts"] = 0.3f
    };

    /// <summary>
    /// Asymmetric decay multipliers per lifecycle state.
    /// STM decays fastest, LTM at baseline, archived barely decays.
    /// </summary>
    public static readonly Dictionary<string, float> DefaultDecayMultipliers = new()
    {
        ["stm"] = 3.0f,
        ["ltm"] = 1.0f,
        ["archived"] = 0.1f
    };

    /// <summary>Get the tier weight for a lifecycle state.</summary>
    public static float GetTierWeight(string lifecycleState)
        => TierWeights.GetValueOrDefault(lifecycleState, 1.0f);

    /// <summary>Get the edge-type weight for spreading activation. Defaults to 0.5 for unknown types.</summary>
    public static float GetEdgeTypeWeight(string relation)
        => EdgeTypeWeights.GetValueOrDefault(relation, 0.5f);

    /// <summary>Get the asymmetric decay multiplier for a lifecycle state.</summary>
    public static float GetDecayMultiplier(string lifecycleState)
        => DefaultDecayMultipliers.GetValueOrDefault(lifecycleState, 1.0f);

    /// <summary>
    /// Compute dynamic mass for an entry.
    /// Formula: mass = log(1 + accessCount) * tierWeight
    /// </summary>
    public static float ComputeMass(int accessCount, string lifecycleState)
    {
        float tierWeight = GetTierWeight(lifecycleState);
        return MathF.Log(1 + accessCount) * tierWeight;
    }

    /// <summary>
    /// Compute gravitational force.
    /// Formula: F_g = mass / distance²  where distance = 1 - cosineScore (clamped to min 0.001)
    /// </summary>
    public static float ComputeGravity(float mass, float cosineScore)
    {
        float distance = MathF.Max(1.0f - cosineScore, MinDistance);
        return mass / (distance * distance);
    }

    /// <summary>
    /// Compute energy boost for a graph neighbor during spreading activation.
    /// Formula: boost = baseEnergy × edgeTypeWeight / sqrt(nodeDegree)
    /// Fan-out attenuation prevents hub nodes from dominating.
    /// </summary>
    public static float ComputeSpreadingEnergy(float baseEnergy, string edgeRelation, int nodeDegree)
    {
        float edgeWeight = GetEdgeTypeWeight(edgeRelation);
        float fanOutAttenuation = 1.0f / MathF.Sqrt(MathF.Max(nodeDegree, 1));
        return baseEnergy * edgeWeight * fanOutAttenuation;
    }

    /// <summary>
    /// Compute temperature-blended score combining semantic similarity and physics gravity.
    /// Temperature T ∈ [0.0, 1.0]: T=0 pure semantic, T=1 pure physics.
    /// </summary>
    public static float ComputeBlendedScore(float cosineScore, float normalizedGravity, float temperature)
    {
        temperature = Math.Clamp(temperature, 0f, 1f);
        return (1f - temperature) * cosineScore + temperature * normalizedGravity;
    }

    /// <summary>
    /// Re-rank cosine search results using gravitational physics and produce a slingshot output.
    /// </summary>
    public SlingshotResult Slingshot(IReadOnlyList<CognitiveSearchResult> cosineResults, float temperature = 0f)
    {
        if (cosineResults.Count == 0)
            throw new ArgumentException("Cannot slingshot empty results.", nameof(cosineResults));

        var ranked = new List<PhysicsRankedResult>(cosineResults.Count);

        // First pass: compute mass and gravity for all results
        float maxGravity = 0f;
        foreach (var r in cosineResults)
        {
            float mass = ComputeMass(r.AccessCount, r.LifecycleState);
            float gravity = ComputeGravity(mass, r.Score);
            if (gravity > maxGravity) maxGravity = gravity;

            ranked.Add(new PhysicsRankedResult(
                r.Id, r.Text, r.Score, mass, gravity,
                r.LifecycleState, r.ActivationEnergy, r.AccessCount,
                r.Category, r.IsSummaryNode, r.SourceClusterId));
        }

        // Asteroid = highest cosine score (input is already sorted by cosine desc, so first)
        var asteroid = ranked[0];

        // Sun = highest gravitational force
        var sun = ranked[0];
        for (int i = 1; i < ranked.Count; i++)
        {
            if (ranked[i].GravityForce > sun.GravityForce)
                sun = ranked[i];
        }

        // Sort by temperature-blended score when temperature > 0, otherwise by gravity
        if (temperature > 0f && maxGravity > 0f)
        {
            ranked.Sort((a, b) =>
            {
                float normalizedA = a.GravityForce / maxGravity;
                float normalizedB = b.GravityForce / maxGravity;
                float blendedA = ComputeBlendedScore(a.CosineScore, normalizedA, temperature);
                float blendedB = ComputeBlendedScore(b.CosineScore, normalizedB, temperature);
                return blendedB.CompareTo(blendedA);
            });
        }
        else
        {
            ranked.Sort((a, b) => b.GravityForce.CompareTo(a.GravityForce));
        }

        return new SlingshotResult(asteroid, sun, ranked);
    }
}
