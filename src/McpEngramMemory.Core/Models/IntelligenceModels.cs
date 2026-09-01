using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// Per-namespace decay configuration.
/// </summary>
public sealed class DecayConfig
{
    [JsonPropertyName("ns")]
    public string Ns { get; }

    /// <summary>Tenant partition this decay config belongs to. Legacy configs default to <c>""</c>.</summary>
    [JsonPropertyName("tenantId")]
    public string TenantId { get; }

    [JsonPropertyName("decayRate")]
    public float DecayRate { get; set; } = 0.1f;

    [JsonPropertyName("reinforcementWeight")]
    public float ReinforcementWeight { get; set; } = 1.0f;

    [JsonPropertyName("stmThreshold")]
    public float StmThreshold { get; set; } = 2.0f;

    [JsonPropertyName("archiveThreshold")]
    public float ArchiveThreshold { get; set; } = -5.0f;

    /// <summary>Decay rate multiplier for STM entries. Higher = faster decay. Default 3.0 (half-life ~3h).</summary>
    [JsonPropertyName("stmDecayMultiplier")]
    public float StmDecayMultiplier { get; set; } = 3.0f;

    /// <summary>Decay rate multiplier for LTM entries. Default 1.0 (baseline).</summary>
    [JsonPropertyName("ltmDecayMultiplier")]
    public float LtmDecayMultiplier { get; set; } = 1.0f;

    /// <summary>Decay rate multiplier for archived entries. Lower = slower decay. Default 0.1.</summary>
    [JsonPropertyName("archivedDecayMultiplier")]
    public float ArchivedDecayMultiplier { get; set; } = 0.1f;

    /// <summary>
    /// Spectral diffusion of decay debt through the memory graph. When true (the
    /// default) and the namespace qualifies for a diffusion kernel (>=32 nodes,
    /// >=8 positive-relation edges), per-entry decay debt is diffused through the
    /// graph heat kernel before being subtracted from activation energy:
    /// tightly-linked clusters share forgetting pressure, isolated entries bear
    /// theirs alone. Set to false to force classical pointwise decay regardless
    /// of graph structure. Namespaces below the qualification threshold silently
    /// fall back to pointwise either way — the kernel self-bypasses.
    /// </summary>
    [JsonPropertyName("useSpectralDecay")]
    public bool UseSpectralDecay { get; set; } = true;

    /// <summary>
    /// Fractional-Laplacian exponent for the heat kernel filter
    /// <c>exp(-lambda^alpha * dt)</c>. Default 1.0 = standard heat kernel. Values
    /// less than 1 implement subdiffusive dynamics (changes how fast modes at
    /// different positions in the spectrum decay); values greater than 1 implement
    /// superdiffusive dynamics. Behavior depends on the eigenvalue range, which
    /// for the normalized Laplacian is [0, 2], so the crossover lambda = 1
    /// determines whether a given mode decays faster or slower than standard.
    /// Tune empirically per namespace.
    /// </summary>
    [JsonPropertyName("subdiffusiveExponent")]
    public float SubdiffusiveExponent { get; set; } = 1.0f;

    /// <summary>
    /// Sleep-consolidation pass: long-time graph diffusion of the activation field
    /// to drive topology-aware lifecycle transitions (STM-&gt;LTM promotion when a
    /// memory has cluster support; LTM-&gt;archived when its cluster has decayed).
    /// Complements access-count-driven transitions of the regular decay cycle.
    /// Default true; namespaces below the diffusion-kernel qualification threshold
    /// silently skip consolidation since the heat kernel needs a graph to diffuse on.
    /// </summary>
    [JsonPropertyName("enableConsolidation")]
    public bool EnableConsolidation { get; set; } = true;

    /// <summary>
    /// Diffusion time t for the sleep-consolidation heat kernel exp(-tL).
    /// Larger t = stronger smoothing toward cluster means. Default 10.0, which
    /// is well into the long-time regime where the smoothed activation closely
    /// reflects each connected component's mean rather than per-entry detail.
    /// </summary>
    [JsonPropertyName("consolidationDiffusionTime")]
    public float ConsolidationDiffusionTime { get; set; } = 10.0f;

    /// <summary>
    /// Smoothed-activation threshold above which STM entries are promoted to LTM
    /// during consolidation. Set on the same scale as raw activation energy.
    /// Default 0.0: any cluster with net-positive collective activation graduates
    /// its STM members.
    /// </summary>
    [JsonPropertyName("consolidationPromotionThreshold")]
    public float ConsolidationPromotionThreshold { get; set; } = 0.0f;

    /// <summary>
    /// Smoothed-activation threshold below which LTM entries are archived during
    /// consolidation. Defaults to the same value as <see cref="ArchiveThreshold"/>
    /// so consolidation and decay-cycle archival use a consistent floor.
    /// </summary>
    [JsonPropertyName("consolidationArchiveThreshold")]
    public float ConsolidationArchiveThreshold { get; set; } = -5.0f;

    /// <summary>
    /// Background auto-link scanner: periodically finds semantically-similar pairs
    /// in the namespace and creates <c>similar_to</c> edges between them, giving
    /// the diffusion kernel and consolidation pass more topology to work with
    /// without requiring explicit <c>link_memories</c> calls. Default true.
    /// </summary>
    [JsonPropertyName("enableAutoLink")]
    public bool EnableAutoLink { get; set; } = true;

    /// <summary>
    /// Cosine-similarity threshold above which auto-link creates a <c>similar_to</c>
    /// edge between a pair. Default 0.85 — high enough to skip noise pairs,
    /// low enough to capture clear semantic neighbors that aren't outright
    /// duplicates (which sit near 0.95).
    /// </summary>
    [JsonPropertyName("autoLinkSimilarityThreshold")]
    public float AutoLinkSimilarityThreshold { get; set; } = 0.85f;

    /// <summary>
    /// Per-scan safety cap on the number of new edges auto-link will create.
    /// Prevents pathological edge explosions on very dense namespaces; the next
    /// scheduled scan will pick up any pairs that hit the cap.
    /// </summary>
    [JsonPropertyName("autoLinkMaxNewEdgesPerScan")]
    public int AutoLinkMaxNewEdgesPerScan { get; set; } = 1000;

    [JsonConstructor]
    public DecayConfig(string ns, float decayRate = 0.1f, float reinforcementWeight = 1.0f,
        float stmThreshold = 2.0f, float archiveThreshold = -5.0f,
        float stmDecayMultiplier = 3.0f, float ltmDecayMultiplier = 1.0f,
        float archivedDecayMultiplier = 0.1f,
        bool useSpectralDecay = true, float subdiffusiveExponent = 1.0f,
        bool enableConsolidation = true, float consolidationDiffusionTime = 10.0f,
        float consolidationPromotionThreshold = 0.0f, float consolidationArchiveThreshold = -5.0f,
        bool enableAutoLink = true, float autoLinkSimilarityThreshold = 0.85f,
        int autoLinkMaxNewEdgesPerScan = 1000,
        string? tenantId = null)
    {
        Ns = ns;
        // This ctor doubles as the JSON (read) constructor, so it must normalize without
        // validating — Tenancy.Normalize throws, and a validating deserializer makes one
        // poisoned row render every stored config unloadable. Tenant VALIDATION for new
        // configs happens at the service boundaries, which normalize before constructing.
        TenantId = string.IsNullOrWhiteSpace(tenantId) ? string.Empty : tenantId.Trim();
        DecayRate = decayRate;
        ReinforcementWeight = reinforcementWeight;
        StmThreshold = stmThreshold;
        ArchiveThreshold = archiveThreshold;
        StmDecayMultiplier = stmDecayMultiplier;
        LtmDecayMultiplier = ltmDecayMultiplier;
        ArchivedDecayMultiplier = archivedDecayMultiplier;
        UseSpectralDecay = useSpectralDecay;
        SubdiffusiveExponent = subdiffusiveExponent;
        EnableConsolidation = enableConsolidation;
        ConsolidationDiffusionTime = consolidationDiffusionTime;
        ConsolidationPromotionThreshold = consolidationPromotionThreshold;
        ConsolidationArchiveThreshold = consolidationArchiveThreshold;
        EnableAutoLink = enableAutoLink;
        AutoLinkSimilarityThreshold = autoLinkSimilarityThreshold;
        AutoLinkMaxNewEdgesPerScan = autoLinkMaxNewEdgesPerScan;
    }
}

/// <summary>
/// A pair of near-duplicate entries detected by similarity analysis.
/// </summary>
public sealed record DuplicatePair(
    [property: JsonPropertyName("entryA")] CognitiveEntryInfo EntryA,
    [property: JsonPropertyName("entryB")] CognitiveEntryInfo EntryB,
    [property: JsonPropertyName("similarity")] float Similarity);

/// <summary>
/// Result of a duplicate detection scan.
/// </summary>
public sealed record DuplicateDetectionResult(
    [property: JsonPropertyName("scannedCount")] int ScannedCount,
    [property: JsonPropertyName("duplicates")] IReadOnlyList<DuplicatePair> Duplicates,
    [property: JsonPropertyName("threshold")] float Threshold);

/// <summary>
/// A known contradiction between two entries.
/// </summary>
public sealed record ContradictionInfo(
    [property: JsonPropertyName("entryA")] CognitiveEntryInfo EntryA,
    [property: JsonPropertyName("entryB")] CognitiveEntryInfo EntryB,
    [property: JsonPropertyName("similarity")] float Similarity,
    [property: JsonPropertyName("source")] string Source);

/// <summary>
/// Result of contradiction surfacing.
/// </summary>
public sealed record ContradictionResult(
    [property: JsonPropertyName("contradictions")] IReadOnlyList<ContradictionInfo> Contradictions,
    [property: JsonPropertyName("graphEdgeCount")] int GraphEdgeCount,
    [property: JsonPropertyName("highSimilarityCount")] int HighSimilarityCount);

/// <summary>
/// Metadata recorded when a collapse is executed, enabling future reversal.
/// </summary>
public sealed class CollapseRecord
{
    [JsonPropertyName("collapseId")]
    public string CollapseId { get; }

    [JsonPropertyName("clusterId")]
    public string ClusterId { get; }

    [JsonPropertyName("summaryEntryId")]
    public string SummaryEntryId { get; }

    [JsonPropertyName("ns")]
    public string Ns { get; }

    /// <summary>Tenant partition this collapse record belongs to. Legacy records default to <c>""</c>.</summary>
    [JsonPropertyName("tenantId")]
    public string TenantId { get; }

    [JsonPropertyName("memberIds")]
    public List<string> MemberIds { get; }

    [JsonPropertyName("previousStates")]
    public Dictionary<string, string> PreviousStates { get; }

    [JsonPropertyName("collapsedAt")]
    public DateTimeOffset CollapsedAt { get; }

    /// <summary>
    /// The archive CLAIMS this collapse holds, keyed by member id — each mapped to the
    /// <see cref="CognitiveEntry.LifecycleRevision"/> reserved for that member's archive
    /// transition and persisted here BEFORE the transition runs. A claim is inert until the
    /// CAS archive actually installs its revision: a reservation whose transition never fired
    /// (crash, CAS refusal) matches no entry and restores nothing. The pair is the ownership
    /// token an undo needs whole: membership says THIS collapse claimed the member, and the
    /// revision says the archived state standing NOW is the very transition this collapse
    /// installed — any later lifecycle work, even one that re-archived the member, moved the
    /// revision and forfeits the restore. Cumulative across attempts (a retry re-claims with a
    /// fresh reservation, overwriting the stale one). Null on records persisted before the
    /// field existed; an undo then falls back to the current-state heuristic.
    /// </summary>
    [JsonPropertyName("appliedLifecycleRevisions")]
    public Dictionary<string, long>? AppliedLifecycleRevisions { get; }

    /// <summary>
    /// The lifecycle witness each claimed member carried WHEN ITS ARCHIVE WAS PLANNED, keyed by
    /// member id — the other half of the claim in <see cref="AppliedLifecycleRevisions"/>. The
    /// archive CAS fires only from this exact witness (and the recorded state), so an entry
    /// that moved through any transition — or was replaced by a fresh upsert landing on the
    /// same state, the ABA a state-only compare absorbs — between the plan and the effect makes
    /// the archive refuse rather than the receipt lie. Persisted with the claim so the durable
    /// record names the FULL transition it authorizes (from state S at witness E, to archived,
    /// installing R); a retry re-plans from fresh reads rather than replaying these, so the
    /// field is evidence for the archive and for audit, never an input to recovery. Null on
    /// records persisted before the field existed.
    /// </summary>
    [JsonPropertyName("expectedLifecycleRevisions")]
    public Dictionary<string, long>? ExpectedLifecycleRevisions { get; }

    /// <summary>
    /// Monotonic per-lineage write counter: every durable write of this record (the intent
    /// record, the claims record) carries the previous generation plus one. It exists for the
    /// UNDO'S FINAL DELETE, which must be a compare-and-delete: an undo that read the record at
    /// generation G may only remove the record still AT G — an executor that persisted new
    /// claims in between moved the generation, and deleting over it would discard receipts the
    /// undo never processed. Zero on records persisted before the field existed (their next
    /// write moves them to one).
    /// </summary>
    [JsonPropertyName("generation")]
    public long Generation { get; }

    /// <summary>
    /// The INCARNATION WITNESS of the cluster this collapse created (or adopted from its own
    /// prior attempt): the <see cref="SemanticCluster.CreationStamp"/> minted when the intent
    /// record fixed the collapse's identity, before the cluster existed. Cleanup compares it
    /// atomically — cluster removal, membership eviction and the summary delete all fire only
    /// against a resident that still carries this stamp — so a SAME-NAMESPACE recreation of
    /// the (public) cluster id is spared: it exists under a different stamp this record never
    /// minted. Null on records persisted before the field existed; those keep the id-only
    /// legacy compares.
    /// </summary>
    [JsonPropertyName("clusterStamp")]
    public string? ClusterStamp { get; }

    /// <summary>
    /// The <see cref="SemanticCluster.InstanceId"/> of the PHYSICAL cluster object THIS
    /// attempt created or adopted — re-persisted per attempt, unlike the lineage-level
    /// <see cref="ClusterStamp"/> which every retry reuses. The record's summary cleanup
    /// compares it: a stale branch operating from an older record view then spares the LIVE
    /// summary a concurrent retry published under the same lineage stamp, because the retry's
    /// intent persist advanced the record with ITS fresh instance first. Null on records
    /// persisted before the field existed; those fall back to the stamp-only compare.
    /// </summary>
    [JsonPropertyName("clusterInstance")]
    public string? ClusterInstance { get; }

    [JsonConstructor]
    public CollapseRecord(
        string collapseId, string clusterId, string summaryEntryId,
        string ns, List<string> memberIds,
        Dictionary<string, string> previousStates,
        DateTimeOffset collapsedAt,
        string? tenantId = null,
        Dictionary<string, long>? appliedLifecycleRevisions = null,
        Dictionary<string, long>? expectedLifecycleRevisions = null,
        long generation = 0,
        string? clusterStamp = null,
        string? clusterInstance = null)
    {
        CollapseId = collapseId;
        ClusterId = clusterId;
        SummaryEntryId = summaryEntryId;
        Ns = ns;
        MemberIds = memberIds;
        PreviousStates = previousStates;
        CollapsedAt = collapsedAt;
        // Read path: normalize only, never validate (see CognitiveEntry's read constructor) —
        // a validating deserializer makes one poisoned row unloadable, and for RECEIPTS the
        // blast radius is worst of all: the lenient loader would drop every valid receipt in
        // the file and the strict reader would refuse all collapse execution and undo.
        TenantId = string.IsNullOrWhiteSpace(tenantId) ? string.Empty : tenantId.Trim();
        AppliedLifecycleRevisions = appliedLifecycleRevisions;
        ExpectedLifecycleRevisions = expectedLifecycleRevisions;
        Generation = generation;
        ClusterStamp = clusterStamp;
        ClusterInstance = clusterInstance;
    }

    public CollapseRecord(
        string collapseId, string clusterId, string summaryEntryId,
        string ns, List<string> memberIds,
        Dictionary<string, string> previousStates,
        string? tenantId = null,
        Dictionary<string, long>? appliedLifecycleRevisions = null,
        Dictionary<string, long>? expectedLifecycleRevisions = null,
        long generation = 0,
        string? clusterStamp = null,
        string? clusterInstance = null)
    {
        CollapseId = collapseId;
        ClusterId = clusterId;
        SummaryEntryId = summaryEntryId;
        Ns = ns;
        MemberIds = memberIds;
        PreviousStates = previousStates;
        CollapsedAt = DateTimeOffset.UtcNow;
        TenantId = Tenancy.Normalize(tenantId);
        AppliedLifecycleRevisions = appliedLifecycleRevisions;
        ExpectedLifecycleRevisions = expectedLifecycleRevisions;
        Generation = generation;
        ClusterStamp = clusterStamp;
        ClusterInstance = clusterInstance;
    }
}

/// <summary>
/// Outcome of the conditional (generation-compared) collapse-record operations — the delete
/// (<see cref="Services.Storage.IStorageProvider.DeleteCollapseRecordSync(string, long)"/>) and
/// the upsert
/// (<see cref="Services.Storage.IStorageProvider.UpsertCollapseRecordSync(CollapseRecord, long?)"/>).
/// Both are compare-and-swap at the store: they take effect only against the exact generation
/// the caller read, which is what lets an executor's terminal commit and an undoer's terminal
/// delete serialize through the record itself rather than through any in-memory gate.
/// </summary>
public enum CollapseRecordCas
{
    /// <summary>The record stood at the expected generation and the operation took effect.</summary>
    Applied,
    /// <summary>No record with the id exists. For a delete, the goal state already holds; for
    /// an upsert expecting a live generation, the record was concurrently removed and nothing
    /// was written.</summary>
    AlreadyAbsent,
    /// <summary>A record exists at a DIFFERENT generation: someone wrote it since the caller
    /// read it. Nothing changed; the caller must re-read and re-process before retrying.</summary>
    GenerationMoved,
    /// <summary>The store could not read, validate, or commit; nothing changed. Retryable.</summary>
    StoreFailed,
}
