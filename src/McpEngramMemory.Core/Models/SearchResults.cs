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
/// Result of a sleep-consolidation pass. Reports namespaces processed, lifecycle
/// transitions driven by topology (cluster support / cluster decay), and any
/// namespaces skipped because they didn't qualify for the diffusion kernel.
/// </summary>
public sealed record ConsolidationResult(
    [property: JsonPropertyName("processedNamespaces")] int ProcessedNamespaces,
    [property: JsonPropertyName("skippedNamespaces")] int SkippedNamespaces,
    [property: JsonPropertyName("processedEntries")] int ProcessedEntries,
    [property: JsonPropertyName("stmToLtm")] int StmToLtm,
    [property: JsonPropertyName("ltmToArchived")] int LtmToArchived,
    [property: JsonPropertyName("stmToLtmIds")] IReadOnlyList<string> StmToLtmIds,
    [property: JsonPropertyName("ltmToArchivedIds")] IReadOnlyList<string> LtmToArchivedIds);

/// <summary>
/// Result of an auto-link scan: a periodic background pass that adds
/// <c>similar_to</c> edges between high-cosine-similarity pairs so the diffusion
/// kernel and consolidation operate on a richer graph topology.
/// </summary>
public sealed record AutoLinkResult(
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("scannedEntries")] int ScannedEntries,
    [property: JsonPropertyName("pairsExamined")] int PairsExamined,
    [property: JsonPropertyName("edgesCreated")] int EdgesCreated,
    [property: JsonPropertyName("edgesSkippedExisting")] int EdgesSkippedExisting,
    [property: JsonPropertyName("hitMaxEdgeCap")] bool HitMaxEdgeCap);

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
    [property: JsonPropertyName("newCollapses")] IReadOnlyList<PendingCollapseInfo> NewCollapses,
    [property: JsonPropertyName("autoSummaries")] IReadOnlyList<AutoSummaryInfo>? AutoSummaries = null);

/// <summary>
/// Info about an auto-generated cluster summary (GraphRAG-style).
/// </summary>
public sealed record AutoSummaryInfo(
    [property: JsonPropertyName("clusterId")] string ClusterId,
    [property: JsonPropertyName("summaryId")] string SummaryId,
    [property: JsonPropertyName("memberCount")] int MemberCount);

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

// ── Graph Snapshot (visualization) ──────────────────────────────────────────

/// <summary>
/// A single node in the memory graph snapshot.
/// </summary>
public sealed record GraphSnapshotNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("ns")] string Ns,
    [property: JsonPropertyName("lifecycleState")] string LifecycleState,
    [property: JsonPropertyName("activationEnergy")] float ActivationEnergy,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("accessCount")] int AccessCount,
    [property: JsonPropertyName("isSummaryNode")] bool IsSummaryNode,
    [property: JsonPropertyName("sourceClusterId")] string? SourceClusterId,
    [property: JsonPropertyName("keywords")] string? Keywords);

/// <summary>
/// A single typed edge in the memory graph snapshot.
/// </summary>
public sealed record GraphSnapshotEdge(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("weight")] float Weight);

/// <summary>
/// A cluster grouping in the memory graph snapshot.
/// </summary>
public sealed record GraphSnapshotCluster(
    [property: JsonPropertyName("clusterId")] string ClusterId,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("ns")] string Ns,
    [property: JsonPropertyName("memberIds")] IReadOnlyList<string> MemberIds,
    [property: JsonPropertyName("hasSummary")] bool HasSummary);

/// <summary>
/// Aggregate statistics for a graph snapshot.
/// </summary>
public sealed record GraphSnapshotStats(
    [property: JsonPropertyName("nodeCount")] int NodeCount,
    [property: JsonPropertyName("edgeCount")] int EdgeCount,
    [property: JsonPropertyName("clusterCount")] int ClusterCount,
    [property: JsonPropertyName("stm")] int Stm,
    [property: JsonPropertyName("ltm")] int Ltm,
    [property: JsonPropertyName("archived")] int Archived,
    [property: JsonPropertyName("namespaces")] IReadOnlyList<string> Namespaces);

/// <summary>
/// Full memory graph snapshot for visualization.
/// Pipe this JSON into visualization/memory-graph.html.
/// </summary>
public sealed record GraphSnapshot(
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("capturedAt")] DateTimeOffset CapturedAt,
    [property: JsonPropertyName("nodes")] IReadOnlyList<GraphSnapshotNode> Nodes,
    [property: JsonPropertyName("edges")] IReadOnlyList<GraphSnapshotEdge> Edges,
    [property: JsonPropertyName("clusters")] IReadOnlyList<GraphSnapshotCluster> Clusters,
    [property: JsonPropertyName("stats")] GraphSnapshotStats Stats);
