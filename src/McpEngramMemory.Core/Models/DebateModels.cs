using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// A single expert's perspective retrieved during panel consultation.
/// </summary>
public sealed record ExpertPerspective(
    [property: JsonPropertyName("nodeAlias")] int NodeAlias,
    [property: JsonPropertyName("expertNamespace")] string ExpertNamespace,
    [property: JsonPropertyName("entryId")] string EntryId,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("score")] float Score,
    [property: JsonPropertyName("hadPriorContext")] bool HadPriorContext);

/// <summary>
/// Result of consult_expert_panel: integer-mapped perspectives from each expert namespace.
/// </summary>
public sealed record ConsultPanelResult(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("problemStatement")] string ProblemStatement,
    [property: JsonPropertyName("perspectives")] IReadOnlyList<ExpertPerspective> Perspectives,
    [property: JsonPropertyName("debateNamespace")] string DebateNamespace,
    [property: JsonPropertyName("totalExperts")] int TotalExperts,
    [property: JsonPropertyName("expertsWithContext")] int ExpertsWithContext);

/// <summary>
/// An edge definition using integer node aliases instead of raw UUIDs.
/// </summary>
public sealed record DebateEdge(
    [property: JsonPropertyName("sourceNode")] int SourceNode,
    [property: JsonPropertyName("targetNode")] int TargetNode,
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("weight")] float Weight = 1.0f);

/// <summary>
/// Result of map_debate_graph: edges created between debate nodes.
/// </summary>
public sealed record MapDebateGraphResult(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("edgesCreated")] int EdgesCreated,
    [property: JsonPropertyName("edgeDetails")] IReadOnlyList<string> EdgeDetails);

/// <summary>
/// Result of resolve_debate: consensus stored, debate archived.
/// </summary>
public sealed record ResolveDebateResult(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("consensusEntryId")] string ConsensusEntryId,
    [property: JsonPropertyName("consensusNamespace")] string ConsensusNamespace,
    [property: JsonPropertyName("winningNodeId")] string WinningNodeId,
    [property: JsonPropertyName("archivedCount")] int ArchivedCount,
    [property: JsonPropertyName("summary")] string Summary);
