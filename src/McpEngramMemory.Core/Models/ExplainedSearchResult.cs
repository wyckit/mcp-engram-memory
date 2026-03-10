using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// Detailed explanation of why a search result was returned and how it was ranked.
/// </summary>
public sealed record RetrievalExplanation(
    [property: JsonPropertyName("rank")] int Rank,
    [property: JsonPropertyName("cosineScore")] float CosineScore,
    [property: JsonPropertyName("physicsMass")] float? PhysicsMass,
    [property: JsonPropertyName("gravityForce")] float? GravityForce,
    [property: JsonPropertyName("lifecycleState")] string LifecycleState,
    [property: JsonPropertyName("lifecycleTierWeight")] float LifecycleTierWeight,
    [property: JsonPropertyName("activationEnergy")] float ActivationEnergy,
    [property: JsonPropertyName("accessCount")] int AccessCount,
    [property: JsonPropertyName("isSummaryNode")] bool IsSummaryNode,
    [property: JsonPropertyName("summaryBoosted")] bool SummaryBoosted);

/// <summary>
/// A search result enriched with a full retrieval explanation.
/// </summary>
public sealed record ExplainedSearchResult(
    [property: JsonPropertyName("result")] CognitiveSearchResult Result,
    [property: JsonPropertyName("explanation")] RetrievalExplanation Explanation);

/// <summary>
/// Full explained search response with pipeline metadata.
/// </summary>
public sealed record ExplainedSearchResponse(
    [property: JsonPropertyName("results")] IReadOnlyList<ExplainedSearchResult> Results,
    [property: JsonPropertyName("totalInNamespace")] int TotalInNamespace,
    [property: JsonPropertyName("searchLatencyMs")] double SearchLatencyMs,
    [property: JsonPropertyName("embeddingLatencyMs")] double EmbeddingLatencyMs,
    [property: JsonPropertyName("appliedCategoryFilter")] string? AppliedCategoryFilter,
    [property: JsonPropertyName("appliedStateFilter")] IReadOnlyList<string> AppliedStateFilter,
    [property: JsonPropertyName("usePhysics")] bool UsePhysics,
    [property: JsonPropertyName("summaryFirst")] bool SummaryFirst);
