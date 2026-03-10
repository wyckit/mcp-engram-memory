using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// Result of a cognitive memory search, enriched with lifecycle state and cluster context.
/// </summary>
public sealed record CognitiveSearchResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("score")] float Score,
    [property: JsonPropertyName("lifecycleState")] string LifecycleState,
    [property: JsonPropertyName("activationEnergy")] float ActivationEnergy,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("metadata")] Dictionary<string, string>? Metadata,
    [property: JsonPropertyName("isSummaryNode")] bool IsSummaryNode,
    [property: JsonPropertyName("sourceClusterId")] string? SourceClusterId,
    [property: JsonPropertyName("accessCount")] int AccessCount = 0);

/// <summary>
/// Result of get_neighbors: an edge paired with the connected entry.
/// </summary>
public sealed record NeighborResult(
    [property: JsonPropertyName("edge")] GraphEdge Edge,
    [property: JsonPropertyName("entry")] CognitiveEntryInfo Entry);

/// <summary>
/// Lightweight entry info for graph traversal results.
/// </summary>
public sealed record CognitiveEntryInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("ns")] string Namespace,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("lifecycleState")] string LifecycleState);

/// <summary>
/// Result of get_neighbors tool.
/// </summary>
public sealed record GetNeighborsResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("neighbors")] IReadOnlyList<NeighborResult> Neighbors);

/// <summary>
/// Result of traverse_graph tool.
/// </summary>
public sealed record TraversalResult(
    [property: JsonPropertyName("startId")] string StartId,
    [property: JsonPropertyName("entries")] IReadOnlyList<CognitiveEntryInfo> Entries,
    [property: JsonPropertyName("edges")] IReadOnlyList<GraphEdge> Edges);

/// <summary>
/// Full cognitive context of a single entry.
/// </summary>
public sealed record GetMemoryResult(
    [property: JsonPropertyName("entry")] CognitiveEntryInfo Entry,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("metadata")] Dictionary<string, string>? Metadata,
    [property: JsonPropertyName("lifecycleState")] string LifecycleState,
    [property: JsonPropertyName("activationEnergy")] float ActivationEnergy,
    [property: JsonPropertyName("accessCount")] int AccessCount,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("lastAccessedAt")] DateTimeOffset LastAccessedAt,
    [property: JsonPropertyName("edges")] IReadOnlyList<GraphEdge> Edges,
    [property: JsonPropertyName("clusterIds")] IReadOnlyList<string> ClusterIds);

/// <summary>
/// Result of get_cluster tool.
/// </summary>
public sealed record GetClusterResult(
    [property: JsonPropertyName("clusterId")] string ClusterId,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("ns")] string Namespace,
    [property: JsonPropertyName("memberCount")] int MemberCount,
    [property: JsonPropertyName("members")] IReadOnlyList<CognitiveEntryInfo> Members,
    [property: JsonPropertyName("summaryEntry")] CognitiveSearchResult? SummaryEntry,
    [property: JsonPropertyName("isStale")] bool IsStale);

/// <summary>
/// Summary info for list_clusters tool.
/// </summary>
public sealed record ClusterSummaryInfo(
    [property: JsonPropertyName("clusterId")] string ClusterId,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("memberCount")] int MemberCount,
    [property: JsonPropertyName("hasSummary")] bool HasSummary);

/// <summary>
/// Result of decay_cycle tool.
/// </summary>
public sealed record DecayCycleResult(
    [property: JsonPropertyName("processedCount")] int ProcessedCount,
    [property: JsonPropertyName("stmToLtm")] int StmToLtm,
    [property: JsonPropertyName("ltmToArchived")] int LtmToArchived,
    [property: JsonPropertyName("stmToLtmIds")] IReadOnlyList<string> StmToLtmIds,
    [property: JsonPropertyName("ltmToArchivedIds")] IReadOnlyList<string> LtmToArchivedIds);

/// <summary>
/// System overview statistics.
/// </summary>
public sealed record LifecycleStats(
    [property: JsonPropertyName("totalEntries")] int TotalEntries,
    [property: JsonPropertyName("stmCount")] int StmCount,
    [property: JsonPropertyName("ltmCount")] int LtmCount,
    [property: JsonPropertyName("archivedCount")] int ArchivedCount,
    [property: JsonPropertyName("clusterCount")] int ClusterCount,
    [property: JsonPropertyName("edgeCount")] int EdgeCount,
    [property: JsonPropertyName("namespaces")] IReadOnlyList<string> Namespaces);

/// <summary>
/// Result of a rebuild_embeddings operation for a single namespace.
/// </summary>
public sealed record RebuildNamespaceResult(
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("updated")] int Updated,
    [property: JsonPropertyName("skipped")] int Skipped);

/// <summary>
/// Aggregate result of a rebuild_embeddings operation.
/// </summary>
public sealed record RebuildEmbeddingsResult(
    [property: JsonPropertyName("totalUpdated")] int TotalUpdated,
    [property: JsonPropertyName("totalSkipped")] int TotalSkipped,
    [property: JsonPropertyName("namespacesProcessed")] int NamespacesProcessed,
    [property: JsonPropertyName("results")] IReadOnlyList<RebuildNamespaceResult> Results,
    [property: JsonPropertyName("embeddingDimensions")] int EmbeddingDimensions);

/// <summary>
/// A search result enriched with physics-based mass and gravitational force.
/// </summary>
public sealed record PhysicsRankedResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("cosineScore")] float CosineScore,
    [property: JsonPropertyName("mass")] float Mass,
    [property: JsonPropertyName("gravityForce")] float GravityForce,
    [property: JsonPropertyName("lifecycleState")] string LifecycleState,
    [property: JsonPropertyName("activationEnergy")] float ActivationEnergy,
    [property: JsonPropertyName("accessCount")] int AccessCount,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("isSummaryNode")] bool IsSummaryNode,
    [property: JsonPropertyName("sourceClusterId")] string? SourceClusterId);

/// <summary>
/// Slingshot output: Asteroid (closest semantic match) and Sun (highest gravitational pull).
/// </summary>
public sealed record SlingshotResult(
    [property: JsonPropertyName("asteroid")] PhysicsRankedResult Asteroid,
    [property: JsonPropertyName("sun")] PhysicsRankedResult Sun,
    [property: JsonPropertyName("allResults")] IReadOnlyList<PhysicsRankedResult> AllResults);

/// <summary>
/// Info about a pending accretion collapse awaiting LLM summarization.
/// </summary>
public sealed record PendingCollapseInfo(
    [property: JsonPropertyName("collapseId")] string CollapseId,
    [property: JsonPropertyName("ns")] string Ns,
    [property: JsonPropertyName("memberCount")] int MemberCount,
    [property: JsonPropertyName("memberPreviews")] IReadOnlyList<CognitiveEntryInfo> MemberPreviews,
    [property: JsonPropertyName("detectedAt")] DateTimeOffset DetectedAt);

/// <summary>
/// Result of an accretion scan cycle.
/// </summary>
public sealed record AccretionScanResult(
    [property: JsonPropertyName("scannedCount")] int ScannedCount,
    [property: JsonPropertyName("clustersDetected")] int ClustersDetected,
    [property: JsonPropertyName("newCollapses")] IReadOnlyList<PendingCollapseInfo> NewCollapses);

/// <summary>
/// Result of a memory feedback operation.
/// </summary>
public sealed record FeedbackResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("previousActivationEnergy")] float PreviousActivationEnergy,
    [property: JsonPropertyName("newActivationEnergy")] float NewActivationEnergy,
    [property: JsonPropertyName("previousState")] string PreviousState,
    [property: JsonPropertyName("newState")] string NewState,
    [property: JsonPropertyName("stateChanged")] bool StateChanged);
