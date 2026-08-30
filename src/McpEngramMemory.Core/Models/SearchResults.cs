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
/// A cluster membership paired with the namespace that cluster lives in.
/// A bare cluster id is not authorizable: cluster ids reached through topology arrive without
/// their namespace, so a caller filtering by ACL would have to re-resolve each one. Carrying
/// <see cref="Ns"/> alongside the id lets callers apply their namespace permission check directly
/// on what the lookup already knew, instead of round-tripping through a full cluster load.
/// </summary>
public readonly record struct ClusterMembershipInfo(string ClusterId, string Ns);

/// <summary>
/// Result of decay_cycle tool. <see cref="SpectralFallbackNamespaces"/> and
/// <see cref="FailedNamespaces"/> report per-namespace partial failures for
/// telemetry: fallback namespaces still received full non-spectral pointwise
/// decay; failed namespaces were skipped entirely for this cycle.
/// </summary>
public sealed record DecayCycleResult(
    [property: JsonPropertyName("processedCount")] int ProcessedCount,
    [property: JsonPropertyName("stmToLtm")] int StmToLtm,
    [property: JsonPropertyName("ltmToArchived")] int LtmToArchived,
    [property: JsonPropertyName("stmToLtmIds")] IReadOnlyList<string> StmToLtmIds,
    [property: JsonPropertyName("ltmToArchivedIds")] IReadOnlyList<string> LtmToArchivedIds,
    [property: JsonPropertyName("totalNamespaces")] int TotalNamespaces = 0,
    [property: JsonPropertyName("spectralFallbackNamespaces")] IReadOnlyList<string>? SpectralFallbackNamespaces = null,
    [property: JsonPropertyName("failedNamespaces")] IReadOnlyList<string>? FailedNamespaces = null);

/// <summary>
/// Result of a sleep-consolidation pass. Reports namespaces processed, lifecycle
/// transitions driven by topology (cluster support / cluster decay), and any
/// namespaces skipped because they didn't qualify for the diffusion kernel.
/// <see cref="FailedNamespaces"/> reports namespaces whose pass threw and was
/// skipped, for partial-failure telemetry (the total namespace count is derivable
/// as ProcessedNamespaces + SkippedNamespaces + FailedNamespaces.Count).
/// </summary>
public sealed record ConsolidationResult(
    [property: JsonPropertyName("processedNamespaces")] int ProcessedNamespaces,
    [property: JsonPropertyName("skippedNamespaces")] int SkippedNamespaces,
    [property: JsonPropertyName("processedEntries")] int ProcessedEntries,
    [property: JsonPropertyName("stmToLtm")] int StmToLtm,
    [property: JsonPropertyName("ltmToArchived")] int LtmToArchived,
    [property: JsonPropertyName("stmToLtmIds")] IReadOnlyList<string> StmToLtmIds,
    [property: JsonPropertyName("ltmToArchivedIds")] IReadOnlyList<string> LtmToArchivedIds,
    [property: JsonPropertyName("failedNamespaces")] IReadOnlyList<string>? FailedNamespaces = null);

/// <summary>
/// Result of an auto-link scan: a periodic background pass that adds
/// <c>similar_to</c> edges between high-cosine-similarity pairs so the diffusion
/// kernel and consolidation operate on a richer graph topology.
///
/// A scan can stop for three different reasons and all three are separately expressible, because
/// collapsing any two of them produces a report a caller cannot act on.
/// <see cref="HitMaxEdgeCap"/> means the cap was binding — more admissible candidates were found
/// than it would spend. <see cref="PairScanIncomplete"/> means this pass examined only part of the
/// namespace's pair space and the next scan resumes where it stopped. Both false means the scan saw
/// every pair and wrote every one it could: only then does "no edges created" mean "nothing left to
/// link". <see cref="EntriesNotScanned"/> is the fourth and oldest of these bounds, and unlike the
/// other two it is not resumed — those entries wait for the namespace to shrink or for a wider
/// <c>maxScanEntries</c>.
///
/// <see cref="ScanAlreadyInProgress"/> is the fifth, and it is a report about a scan that did not
/// happen rather than about one that stopped early: another scan of this same (tenant, namespace)
/// was already running, so this call loaded no entries, examined no pairs, wrote no edges and left
/// the resume cursor exactly where the running scan will put it. Only one scan per namespace runs
/// at a time — the scanner is a singleton shared by the background sweep and the tool, and its
/// resume cursor is not read-modify-written atomically, so overlapping scans could roll progress
/// backwards and pay for the same quadratic window twice. The loser is told rather than queued:
/// waiting would put an interactive call behind a background sweep and then have it redo the window
/// that sweep just finished. The flag always arrives with <see cref="PairScanIncomplete"/> set, so
/// the rule above survives unchanged — both completeness flags false still means the whole pair
/// space was covered. It names nothing the caller did not name, so it is not an oracle: the caller
/// already had to hold write access to this namespace to ask.
///
/// THREE PAIR COUNTS, AND NONE OF THEM STANDS IN FOR ANOTHER.
///
/// <see cref="PairsExamined"/> is the WORK DONE: pair slots this pass actually walked, in the same
/// unit — and the same type — as the <c>maxPairComparisons</c> budget that bounds it.
///
/// <see cref="PairSlotsPlanned"/> is the WORK BUDGETED: the slots this pass's window covers, which
/// is what the budget bought and what a completed pass spends in full. It is what an operator sizing
/// <c>maxPairComparisons</c> reads, and it is a property of the window rather than of the run —
/// computed before the first anchor, so it is available whether the pass ran or not.
///
/// <see cref="PairsAboveThreshold"/> is the FIND: how many of the pairs walked cleared the
/// similarity threshold.
///
/// For several rounds there was ONE field for all of this. It was named for the work and held the
/// find, because the counter sat in a loop the pair stream only feeds with pairs that already
/// passed; in a steady-state namespace those differ by three to five orders of magnitude — 40
/// neighbours found across 18,000,000 comparisons — and the find does not even move monotonically
/// with the work, since a namespace of near-duplicates reports a large number for the identical walk
/// that reports a tiny one when nothing matches. It was then named for the work and held the PLAN,
/// which is the same class of error one step along: the plan is computed before enumeration, and
/// cancellation can stop a walk before its first anchor, so a pre-cancelled scan over three entries
/// compared nothing at all and reported three pairs examined.
///
/// WHAT "EXAMINED" GUARANTEES. It is exact on completed and cancelled production scans. The detector
/// reports once after each complete anchor row, including rows and row suffixes with no
/// above-threshold yield; cancellation is observed only before the next anchor. A pre-cancelled scan
/// therefore reports zero, while a mid-window cancellation reports every completed logical pair
/// slot and none from the unstarted suffix. <see cref="PairScanIncomplete"/> is always set alongside
/// cancellation.
/// </summary>
public sealed record AutoLinkResult(
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("scannedEntries")] int ScannedEntries,
    [property: JsonPropertyName("pairsExamined")] long PairsExamined,
    [property: JsonPropertyName("edgesCreated")] int EdgesCreated,
    [property: JsonPropertyName("edgesSkippedExisting")] int EdgesSkippedExisting,
    [property: JsonPropertyName("hitMaxEdgeCap")] bool HitMaxEdgeCap,
    [property: JsonPropertyName("entriesNotScanned")] int EntriesNotScanned = 0,
    [property: JsonPropertyName("pairScanIncomplete")] bool PairScanIncomplete = false,
    [property: JsonPropertyName("scanAlreadyInProgress")] bool ScanAlreadyInProgress = false,
    // Appended rather than placed beside PairsExamined where they belong: the positional constructor
    // is public and callers pass the leading parameters by position, so inserting one in the middle
    // silently re-binds their arguments.
    [property: JsonPropertyName("pairsAboveThreshold")] int PairsAboveThreshold = 0,
    [property: JsonPropertyName("pairSlotsPlanned")] long PairSlotsPlanned = 0);

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
    [property: JsonPropertyName("autoSummaries")] IReadOnlyList<AutoSummaryInfo>? AutoSummaries = null,
    [property: JsonPropertyName("entriesNotScanned")] int EntriesNotScanned = 0);

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

// ── Background Worker Status ─────────────────────────────────────────────────

/// <summary>
/// Status snapshot for a single background maintenance worker.
/// </summary>
public sealed record EngramWorkerStatus(
    [property: JsonPropertyName("worker")]               string  Worker,
    [property: JsonPropertyName("lastRunUtc")]           DateTime? LastRunUtc,
    [property: JsonPropertyName("lastDurationMs")]       long    LastDurationMs,
    [property: JsonPropertyName("cyclesCompleted")]      long    CyclesCompleted,
    [property: JsonPropertyName("totalEntriesProcessed")] long   TotalEntriesProcessed,
    [property: JsonPropertyName("lastErrorMessage")]     string? LastErrorMessage);

/// <summary>
/// Aggregate status snapshot returned by the <c>engram_status</c> tool.
/// </summary>
public sealed record EngramStatusOutput(
    [property: JsonPropertyName("decay")]         EngramWorkerStatus Decay,
    [property: JsonPropertyName("consolidation")] EngramWorkerStatus Consolidation,
    [property: JsonPropertyName("autoLink")]      EngramWorkerStatus AutoLink,
    [property: JsonPropertyName("accretion")]     EngramWorkerStatus Accretion);

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
