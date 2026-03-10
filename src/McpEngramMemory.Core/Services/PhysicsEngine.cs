using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services;

/// <summary>
/// Physics-based re-ranking engine that computes gravitational force for memory retrieval.
/// Provides "Slingshot" output: Asteroid (closest semantic match) and Sun (highest gravitational pull).
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

    /// <summary>Get the tier weight for a lifecycle state.</summary>
    public static float GetTierWeight(string lifecycleState)
        => TierWeights.GetValueOrDefault(lifecycleState, 1.0f);

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
    /// Re-rank cosine search results using gravitational physics and produce a slingshot output.
    /// </summary>
    public SlingshotResult Slingshot(IReadOnlyList<CognitiveSearchResult> cosineResults)
    {
        if (cosineResults.Count == 0)
            throw new ArgumentException("Cannot slingshot empty results.", nameof(cosineResults));

        var ranked = new List<PhysicsRankedResult>(cosineResults.Count);

        foreach (var r in cosineResults)
        {
            float mass = ComputeMass(r.AccessCount, r.LifecycleState);
            float gravity = ComputeGravity(mass, r.Score);

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

        // Sort all by gravity descending
        ranked.Sort((a, b) => b.GravityForce.CompareTo(a.GravityForce));

        return new SlingshotResult(asteroid, sun, ranked);
    }
}
