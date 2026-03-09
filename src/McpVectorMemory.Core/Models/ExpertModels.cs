using System.Text.Json.Serialization;

namespace McpVectorMemory.Core.Models;

/// <summary>
/// A matched expert from the semantic routing meta-index.
/// </summary>
public sealed record ExpertMatch(
    [property: JsonPropertyName("expertId")] string ExpertId,
    [property: JsonPropertyName("personaDescription")] string PersonaDescription,
    [property: JsonPropertyName("targetNamespace")] string TargetNamespace,
    [property: JsonPropertyName("score")] float Score,
    [property: JsonPropertyName("taskCount")] int TaskCount);

/// <summary>
/// Result of dispatch_task when an expert is found (status = "routed").
/// </summary>
public sealed record DispatchRoutedResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expert")] ExpertMatch Expert,
    [property: JsonPropertyName("candidateExperts")] IReadOnlyList<ExpertMatch> CandidateExperts,
    [property: JsonPropertyName("context")] IReadOnlyList<CognitiveSearchResult> Context);

/// <summary>
/// Result of dispatch_task when no expert matches (status = "needs_expert").
/// </summary>
public sealed record DispatchMissResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("closestExperts")] IReadOnlyList<ExpertMatch> ClosestExperts,
    [property: JsonPropertyName("suggestion")] string Suggestion);

/// <summary>
/// Result of create_expert tool.
/// </summary>
public sealed record CreateExpertResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expertId")] string ExpertId,
    [property: JsonPropertyName("targetNamespace")] string TargetNamespace);
