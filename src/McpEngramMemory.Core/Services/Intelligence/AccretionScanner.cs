using System.Security.Cryptography;
using System.Text;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Intelligence;

/// <summary>
/// Manages DBSCAN density scanning of LTM-tier entries and pending collapse state.
/// Detects dense vector clusters and proposes them for LLM-driven summarization and collapse.
/// </summary>
public sealed class AccretionScanner
{
    /// <summary>
    /// Upper bound on LTM entries fed to DBSCAN in one pass.
    ///
    /// RangeQuery is a brute-force scan per point with no spatial index, so the pass is
    /// quadratic in the candidate count — and it runs automatically on every namespace every
    /// 30 minutes. Measured on 384-dim vectors: ~0.35s at 2,000 entries, ~2.1s at 4,200,
    /// ~5.3s at 8,000 for the dense-blob case the epsilon threshold is tuned to find.
    ///
    /// Truncation is logged and reported in the result rather than applied silently: a scan
    /// that quietly examined a subset is indistinguishable from one that found nothing.
    /// </summary>
    public const int DefaultMaxScanEntries = 10_000;

    private readonly CognitiveIndex _index;
    private readonly ILogger<AccretionScanner>? _logger;
    private readonly IStorageProvider? _persistence;
    // Pending collapses and history are keyed by their globally-unique collapseId (contains a Guid);
    // each object carries its own TenantId, and every query/mutation below is tenant-filtered so a
    // caller never sees or acts on another tenant's collapse. Dismissed ids are (tenant, id) so the
    // same id can be dismissed independently per tenant.
    private readonly Dictionary<string, PendingCollapse> _pendingCollapses = new();
    private readonly Dictionary<string, CollapseRecord> _collapseHistory = new();
    private readonly HashSet<(string Tenant, string Id)> _dismissedEntryIds = new();

    // Collapse ids with an execution, undo or dismissal in flight — keyed by the DURABLE
    // STORE's identity and nothing else. Two actors over the same store share the same durable
    // records and the same durable entries, so they must contend HOWEVER their in-memory
    // stacks were constructed: keying by index instance as well once let two independently
    // loaded stacks over one store bypass the gate entirely, and their in-memory indexes are
    // not coherent with each other — one stack's witness CAS cannot fence the other's.
    // Process-wide by nature (it is an in-memory set): within a process this closes every
    // execute/undo/dismiss interleaving over a store. ACROSS OS processes it cannot reach, and
    // neither can the entry stores themselves — the debounced full-snapshot writers assume ONE
    // writing index/provider STACK per store: a second stack is a second full-snapshot writer
    // that clobbers entries wholesale, collapse or no collapse, whether it lives in another
    // process or in this one. The record layer alone is safe across stacks and processes
    // (transactions, the interprocess lock file, generation CAS); the entry-level guarantees
    // are scoped to the single writing stack the storage design already requires. This gate
    // exists for the OPERATION layer: even read-mostly secondary stacks (recovery tooling, a
    // restarted scanner) must not interleave a collapse operation with a live one.
    private static readonly HashSet<(string Store, string CollapseId)> s_inFlightCollapses = new();
    private static readonly object s_inFlightLock = new();
    private readonly string _inFlightStoreKey;

    private bool TryEnterInFlight(string collapseId)
    {
        lock (s_inFlightLock) return s_inFlightCollapses.Add((_inFlightStoreKey, collapseId));
    }

    private void ExitInFlight(string collapseId)
    {
        lock (s_inFlightLock) s_inFlightCollapses.Remove((_inFlightStoreKey, collapseId));
    }

    /// <summary>
    /// TEST SEAM: invoked by execution after its claims are durable and immediately before the
    /// archive CAS loop — the exact interval where an entry can still change out from under a
    /// persisted plan, and where a competing actor can still act before any archive lands. A
    /// deterministic test injects a lifecycle move (proving the CAS refuses a stale plan) or a
    /// competing scanner's call (proving the store-scoped gate refuses it) at the last instant
    /// either can matter. Null in production.
    /// </summary>
    internal Action? OnBeforeArchiveCas;

    /// <summary>
    /// TEST SEAM: invoked after the archive CAS loop and immediately before the durable phase
    /// commit — the executor's last instant before its terminal write. A test that THROWS here
    /// simulates the executor crashing with archives (if any fired) already applied but no
    /// commit and no rollback — the exact crash the undoer's claim-disarming exists to make
    /// harmless. Null in production.
    /// </summary>
    internal Action? OnBeforeDurableCommit;

    /// <summary>
    /// Generation-compared persist — the store-side CAS every durable record WRITE this
    /// scanner makes goes through (there are no unconditional record writes left here), so two
    /// actors' terminal operations serialize through the record itself. Record-level on
    /// purpose: full-snapshot saves from a scanner's own in-memory map silently erase records
    /// other components persisted but this instance never loaded. A null provider makes the
    /// process the whole durability domain and every compare trivially true.
    /// </summary>
    private CollapseRecordCas PersistRecordIf(CollapseRecord record, long? expectedGeneration)
        => _persistence is null
            ? CollapseRecordCas.Applied
            : _persistence.UpsertCollapseRecordSync(record, expectedGeneration);

    /// <summary>
    /// Release every claim a record holds, by the same CAS discipline the undo's restore pass
    /// uses: a member standing archived at its installed witness is RESTORED to its recorded
    /// previous state; a member still sitting at its planned (pre-archive) witness holds an
    /// ARMED claim and is DISARMED (witness bumped, state untouched) so any pending archive
    /// CAS refuses forever. Refusals of both CASes mean the claim is already inert (the
    /// member's witness belongs to newer work). Returns FALSE — release the record must NOT
    /// proceed — only for the shape that cannot be released: a member archived under this
    /// record's installed witness whose restore target is missing from
    /// <see cref="CollapseRecord.PreviousStates"/>; restoring it would mean guessing a state,
    /// and retracting over it would strand it. Our own writes always pair the two maps, so a
    /// false here indicates a damaged or foreign record and the caller keeps it.
    /// </summary>
    private bool ReleaseClaims(CollapseRecord record, string tenantId)
    {
        if (record.AppliedLifecycleRevisions is null)
            return true;

        bool allReleased = true;
        foreach (var (memberId, installRevision) in record.AppliedLifecycleRevisions)
        {
            if (record.PreviousStates.TryGetValue(memberId, out var previousState))
            {
                if (_index.TryTransitionLifecycle(
                        memberId, record.Ns, tenantId,
                        fromState: "archived", toState: previousState,
                        installRevision: _index.ReserveLifecycleRevision(),
                        expectedRevision: installRevision))
                {
                    continue;
                }
            }
            else
            {
                // No restore target recorded. If the member stands archived under OUR
                // installed witness, the claim is live and unreleasable — fail closed.
                var current = _index.Get(memberId, record.Ns, tenantId: tenantId);
                if (current is not null
                    && current.LifecycleState == "archived"
                    && current.LifecycleRevision == installRevision)
                {
                    allReleased = false;
                    continue;
                }
            }

            if (record.ExpectedLifecycleRevisions is not null
                && record.ExpectedLifecycleRevisions.TryGetValue(memberId, out var plannedWitness))
            {
                _index.TryBumpLifecycleWitness(
                    memberId, record.Ns, tenantId,
                    expectedRevision: plannedWitness,
                    install: _index.ReserveLifecycleRevision());
            }
        }
        return allReleased;
    }

    /// <summary>
    /// Re-sync the in-memory record with the store's current truth after a CAS refusal told us
    /// memory was stale. Strict read; an unreadable store changes nothing (memory keeps its
    /// last-known view rather than adopting a guess).
    /// </summary>
    private void RefreshRecordFromStore(string collapseId)
    {
        if (_persistence is null) return;
        if (_persistence.TryReadCollapseRecord(collapseId, out var fresh))
            RestoreRecordInMemory(collapseId, fresh);
    }

    // Roll the in-memory map back to what stood before an attempt's write: the prior record
    // when there was one — a retry must never destroy the receipt of archives that already
    // happened — and nothing when there was not.
    private void RestoreRecordInMemory(string collapseId, CollapseRecord? prior)
    {
        _lock.EnterWriteLock();
        try
        {
            if (prior is null) _collapseHistory.Remove(collapseId);
            else _collapseHistory[collapseId] = prior;
        }
        finally { _lock.ExitWriteLock(); }
    }
    private readonly ReaderWriterLockSlim _lock = new();
    private bool _historyLoaded;

    public AccretionScanner(CognitiveIndex index, IStorageProvider? persistence = null,
        ILogger<AccretionScanner>? logger = null)
    {
        _index = index;
        _persistence = persistence;
        _logger = logger;
        // The durable store's identity ALONE — see the field remarks: two actors over one
        // store must contend regardless of how their in-memory stacks were built. A provider
        // that does not override StoreIdentity reports a per-instance identity, which reduces
        // to "same provider instance"; no provider at all makes this scanner its own domain.
        _inFlightStoreKey = persistence?.StoreIdentity ?? $"scanner:{Guid.NewGuid():N}";
    }

    /// <summary>
    /// Scan a namespace for dense clusters among LTM-tier entries using DBSCAN.
    /// When autoSummarize is true, automatically creates clusters and summary entries
    /// without archiving members (GraphRAG-style hierarchical summaries).
    /// </summary>
    public AccretionScanResult ScanNamespace(
        string ns, string tenantId, float epsilon = 0.15f, int minPoints = 3,
        bool autoSummarize = false, ClusterManager? clusters = null,
        IEmbeddingService? embedding = null,
        int maxScanEntries = DefaultMaxScanEntries)
    {
        // Normalized at the boundary, like every public surface that stores or compares the
        // tenant: stored state is normalized at construction, so a raw comparand (" acme ")
        // would create proposals one spelling can see and another cannot.
        tenantId = Tenancy.Normalize(tenantId);
        // Get all LTM entries in the (tenant, namespace) partition (outside _lock — uses _index's own lock)
        var allEntries = _index.GetAllInNamespace(ns, tenantId: tenantId);
        var ltmEntries = allEntries
            .Where(e => e.LifecycleState == "ltm" && !e.IsSummaryNode)
            .ToList();

        // Filter out dismissed entries (dismissal is per-tenant)
        List<CognitiveEntry> candidates;
        _lock.EnterReadLock();
        try
        {
            candidates = ltmEntries.Where(e => !_dismissedEntryIds.Contains((tenantId, e.Id))).ToList();
        }
        finally { _lock.ExitReadLock(); }

        // DBSCAN's RangeQuery is a brute-force scan per point, so the pass is quadratic in
        // the candidate count and runs automatically on every namespace every 30 minutes.
        // Bound it, and report what was left out rather than quietly scanning a subset.
        int notScanned = 0;
        if (maxScanEntries > 0 && candidates.Count > maxScanEntries)
        {
            notScanned = candidates.Count - maxScanEntries;
            _logger?.LogWarning(
                "Accretion scan for ns={Namespace} bounded to {Max} of {Total} LTM entries; {Skipped} not examined this pass.",
                ns, maxScanEntries, candidates.Count, notScanned);
            candidates.RemoveRange(maxScanEntries, notScanned);
        }

        // Run DBSCAN (pure computation, no locks needed)
        var detectedClusters = Dbscan(candidates, epsilon, minPoints);

        // Convert clusters to pending collapses
        var newCollapses = new List<PendingCollapseInfo>();
        var autoSummaries = new List<AutoSummaryInfo>();

        _lock.EnterWriteLock();
        try
        {
            foreach (var cluster in detectedClusters)
            {
                var memberIds = cluster.Select(e => e.Id).ToList();

                // Skip if this exact set of members already has a pending collapse (within this tenant)
                if (IsAlreadyPending(memberIds, tenantId: tenantId))
                    continue;

                var centroid = ComputeCentroid(cluster);
                var collapseId = $"collapse:{ns}:{Guid.NewGuid():N}";
                var collapse = new PendingCollapse(collapseId, ns, memberIds, centroid, tenantId: tenantId);
                _pendingCollapses[collapseId] = collapse;

                // Build member previews from the entries we already have (no _index call needed)
                var previews = cluster.Select(e =>
                    new CognitiveEntryInfo(e.Id, e.Text, e.Ns, e.Category, e.LifecycleState))
                    .ToList();

                newCollapses.Add(new PendingCollapseInfo(
                    collapseId, ns, memberIds.Count, previews, collapse.DetectedAt));
            }
        }
        finally { _lock.ExitWriteLock(); }

        // Auto-summarize: create clusters + summaries without archiving members
        if (autoSummarize && clusters is not null && embedding is not null)
        {
            foreach (var cluster in detectedClusters)
            {
                var memberIds = cluster.Select(e => e.Id).ToList();

                // Identity comes from the member set, not from when the scan ran. A Guid here made
                // every rescan of the same memories mint a new cluster id and therefore a new
                // summary entry, and nothing ever removed the old one.
                //
                // HasExistingCluster below already suppresses the common case, but it can only see
                // clusters that are still there. Cluster metadata and summary entries persist
                // through separate debounced writes — ScheduleSaveClusters fires only when clusters
                // change, while entry upserts ride the constant write stream — so an exit without a
                // flush can keep the summary and lose the cluster. The next scan then found no match
                // and wrote a duplicate with identical text. A deterministic id makes that rescan
                // overwrite in place instead, whatever the reason the metadata went missing.
                var clusterId = $"auto:{ns}:{MemberSetFingerprint(memberIds)}";

                // Check if cluster already exists for these members (within this tenant).
                // A match does NOT skip unconditionally: the cluster can stand with its
                // summary missing — a crash between the entry write and the pointer publish,
                // a store refused at quota, or a summary deleted since — and membership
                // equality would then suppress the re-store FOREVER (this deterministic-id
                // rescan is the only automatic path that ever stores it). Consult the
                // ownership-validated summary answer and heal by re-storing — the CAS's
                // same-incarnation overwrite makes the re-store idempotent.
                if (HasExistingCluster(clusters, ns, memberIds, tenantId: tenantId))
                {
                    // The membership match can be ANY cluster in the namespace, while the
                    // heal targets the deterministic id — so the heal must verify the
                    // resident deterministic-id cluster still holds EXACTLY the scanned
                    // member set. An auto cluster edited since keeps its fingerprint id
                    // with diverged members, and a summary generated from the scan's set
                    // would describe members the cluster no longer contains.
                    var resident = clusters.GetCluster(clusterId, tenantId);
                    if (resident is not null && resident.SummaryEntry is null
                        && resident.Members.Count == memberIds.Count
                        && resident.Members.All(m => memberIds.Contains(m.Id)))
                    {
                        var healText = AutoSummarizer.GenerateSummary(cluster);
                        // The membership witness re-verifies the set ATOMICALLY with the store
                        // and again at the publish — the projection check above is only a
                        // cheap pre-screen and can go stale before the write lands.
                        var healId = clusters.StoreSummary(clusterId, healText, embedding.Embed(healText), tenantId: tenantId,
                            onlyIfStamp: null, onlyIfMembers: memberIds);
                        if (!healId.StartsWith("Error:"))
                            autoSummaries.Add(new AutoSummaryInfo(clusterId, healId, memberIds.Count));
                    }
                    continue;
                }

                var createResult = clusters.CreateCluster(clusterId, ns, memberIds, "Auto-summarized", tenantId: tenantId);
                if (createResult.StartsWith("Error:"))
                {
                    // ALREADY EXISTS with a set the membership match above did not recognize —
                    // a subset admission or an edited auto cluster. Without a re-propose the
                    // wedge is permanent: every later scan re-detects the same members, finds
                    // no matching cluster, hits this branch, and the heal (which requires an
                    // exact membership match) never fires. Re-proposing the missing members
                    // lets the set converge once ambiguity resolves, exactly as the collapse
                    // path's already-exists branch does; the NEXT scan's heal then stores the
                    // summary against the converged set.
                    if (createResult.Contains("already exists"))
                    {
                        var missingMembers = memberIds
                            .Where(m => !clusters.GetClusterMembershipsForEntry(m, tenantId: tenantId)
                                .Any(x => x.ClusterId == clusterId))
                            .ToList();
                        if (missingMembers.Count > 0)
                            clusters.UpdateCluster(clusterId, addIds: missingMembers, removeIds: null,
                                label: null, tenantId: tenantId);
                    }
                    continue;
                }

                // Generate the summary from the set the cluster actually ADMITTED — the
                // create's topology screen can admit a subset of the scanned members, and a
                // summary generated from the full scan set would describe members the cluster
                // does not hold (while a witness on the full set would refuse forever). The
                // witness then binds the store to that admitted set.
                var createdView = clusters.GetCluster(clusterId, tenantId);
                if (createdView is null || createdView.Members.Count == 0) continue;
                var admittedIds = createdView.Members.Select(m => m.Id).ToList();
                var admittedEntries = cluster.Where(e => admittedIds.Contains(e.Id)).ToList();
                if (admittedEntries.Count == 0) continue;

                var summaryText = AutoSummarizer.GenerateSummary(admittedEntries);
                var summaryVector = embedding.Embed(summaryText);
                var summaryId = clusters.StoreSummary(clusterId, summaryText, summaryVector, tenantId: tenantId,
                    onlyIfStamp: null, onlyIfMembers: admittedIds);
                if (summaryId.StartsWith("Error:")) continue;

                autoSummaries.Add(new AutoSummaryInfo(clusterId, summaryId, admittedIds.Count));
            }
        }

        return new AccretionScanResult(candidates.Count, detectedClusters.Count, newCollapses, autoSummaries, notScanned);
    }

    /// <summary>
    /// Resolve the namespace of a pending collapse by id, without resolving its members.
    /// Used by callers that need to namespace-gate a collapse before acting on it.
    /// </summary>
    public string? GetPendingCollapseNs(string collapseId, string tenantId)
    {
        tenantId = Tenancy.Normalize(tenantId);
        _lock.EnterReadLock();
        try
        {
            return _pendingCollapses.TryGetValue(collapseId, out var collapse)
                    && !collapse.Dismissed && collapse.TenantId == tenantId
                ? collapse.Ns
                : null;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Resolve the namespace of a recorded collapse by id, without resolving its members.
    /// Used by callers that need to namespace-gate a collapse reversal before acting on it.
    /// </summary>
    public string? GetCollapseRecordNs(string collapseId, string tenantId)
    {
        tenantId = Tenancy.Normalize(tenantId);

        // THE STORE ANSWERS FIRST, in both directions. The process-local cache goes
        // stale-PRESENT when another stack or process retires the record, and stale-ABSENT
        // when one persists a record after this scanner's one-shot load — a cache-first
        // answer keeps serving a retired record's namespace forever even when the miss path
        // reads strictly. A readable store is definitive: found → its namespace, absent →
        // null. Deliberately NOT installed into the cache: this read races a concurrent
        // undo's window between its cache remove and its store delete, and an ungated
        // install would resurrect the just-retired record — wedging dismissal behind a
        // phantom "partially executed attempt". Every destructive consumer does its own
        // strict read; the warm buys nothing worth that.
        if (_persistence is not null && _persistence.TryReadCollapseRecord(collapseId, out var durable))
        {
            return durable is not null && durable.TenantId == tenantId ? durable.Ns : null;
        }

        // Store unreadable (or no persistence at all): the lenient cached view is the best
        // remaining answer. Its stale-PRESENT direction gates towards ATTEMPTING the undo,
        // whose own strict read then refuses honestly — the safe side.
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureHistoryLoaded();
            if (_collapseHistory.TryGetValue(collapseId, out var record) && record.TenantId == tenantId)
                return record.Ns;
        }
        finally { _lock.ExitUpgradeableReadLock(); }
        return null;
    }

    /// <summary>Get all pending (non-dismissed) collapses for a namespace.</summary>
    public IReadOnlyList<PendingCollapseInfo> GetPendingCollapses(string ns, string tenantId)
    {
        tenantId = Tenancy.Normalize(tenantId);
        // Snapshot collapse data under _lock, then resolve entries via _index outside
        List<(string collapseId, string collapseNs, List<string> memberIds, int memberCount, DateTimeOffset detectedAt)> snapshot;

        _lock.EnterReadLock();
        try
        {
            snapshot = _pendingCollapses.Values
                .Where(c => c.Ns == ns && !c.Dismissed && c.TenantId == tenantId)
                .Select(c => (c.CollapseId, c.Ns, c.MemberIds.ToList(), c.MemberIds.Count, c.DetectedAt))
                .ToList();
        }
        finally { _lock.ExitReadLock(); }

        // Resolve entries outside _lock (uses _index's own lock, (tenant, ns)-scoped to avoid loading all)
        var result = new List<PendingCollapseInfo>();
        foreach (var (collapseId, collapseNs, memberIds, memberCount, detectedAt) in snapshot)
        {
            var previews = new List<CognitiveEntryInfo>();
            foreach (var memberId in memberIds)
            {
                var entry = _index.Get(memberId, collapseNs, tenantId: tenantId);
                if (entry is not null)
                    previews.Add(new CognitiveEntryInfo(entry.Id, entry.Text, entry.Ns, entry.Category, entry.LifecycleState));
            }

            result.Add(new PendingCollapseInfo(collapseId, collapseNs, memberCount, previews, detectedAt));
        }
        return result;
    }

    /// <summary>
    /// Execute a pending collapse: create cluster, store summary, archive original members.
    /// </summary>
    public string ExecuteCollapse(
        string collapseId, string summaryText, float[] summaryVector,
        ClusterManager clusters, string tenantId)
    {
        tenantId = Tenancy.Normalize(tenantId);
        PendingCollapse collapse;

        _lock.EnterWriteLock();
        try
        {
            // Shape a cross-tenant miss as plain "not found" so a collapse id can't be probed across tenants.
            if (!_pendingCollapses.TryGetValue(collapseId, out collapse!) || collapse.TenantId != tenantId)
                return $"Error: Collapse '{collapseId}' not found.";
            if (collapse.Dismissed)
                return $"Error: Collapse '{collapseId}' has been dismissed.";
            if (!TryEnterInFlight(collapseId))
                return $"Error: Collapse '{collapseId}' is already being executed or undone.";
        }
        finally { _lock.ExitWriteLock(); }

        try
        {
            return ExecuteCollapseSerialized(collapseId, collapse, summaryText, summaryVector, clusters, tenantId);
        }
        finally
        {
            ExitInFlight(collapseId);
        }
    }

    // The body of ExecuteCollapse, entered only while holding the collapse's in-flight slot.
    private string ExecuteCollapseSerialized(
        string collapseId, PendingCollapse collapse, string summaryText, float[] summaryVector,
        ClusterManager clusters, string tenantId)
    {
        // THE WRITE-AHEAD RECORD, durable BEFORE the first side effect of any kind — cluster
        // creation and summary storage included, not just the archives. The pending collapse is
        // process-local, so a crash after any side effect and before a durable record would
        // otherwise leave state (a cluster, a summary, archived members) that nothing on disk
        // knows how to reverse. A RETRY carries the prior attempt's receipts forward, and a
        // persist failure restores the PRIOR record — never removes it: the receipt of archives
        // that already happened must survive every later failure.
        //
        // THE CLUSTER ID IS MINTED PER INCARNATION, not derived: a retry reuses the id its own
        // prior record carries, and a fresh incarnation (no record — the previous one was fully
        // undone or never started) mints a new nonce. Derivable ids let a cluster with the same
        // name arise from OUTSIDE this record's lineage — a manual creation, or a later
        // incarnation — and the undo's content-identity summary delete, keyed by cluster id,
        // could then reach across lineages. With a per-incarnation nonce the id names exactly
        // one lineage ever, and the summary id derives from it (see ClusterManager.StoreSummary),
        // so the write-ahead record still names both before either exists.
        CollapseRecord? prior;
        CollapseRecord intentRecord;
        string clusterId;
        string summaryId;
        string clusterStamp;
        string clusterInstance;
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureHistoryLoaded();
            _lock.EnterWriteLock();
            try
            {
                prior = _collapseHistory.TryGetValue(collapseId, out var p) && p.TenantId == tenantId ? p : null;
                clusterId = prior?.ClusterId
                    ?? $"accretion:{collapseId.Replace("collapse:", "")}:{Guid.NewGuid():N}";
                summaryId = prior?.SummaryEntryId ?? $"summary:{clusterId}";
                // The incarnation stamp is minted HERE, before the cluster exists, so the
                // write-ahead record can carry it — the cluster id alone is public and
                // recreatable, while the stamp names exactly one lineage and every destructive
                // cleanup compares it atomically. Its safety rests on no INGESTION path ever
                // accepting a caller-supplied stamp: the tool layer's create_cluster calls the
                // stampless overload and list_collapse_history projects the stamp away, so a
                // same-tenant reader cannot replay one into a forged incarnation.
                clusterStamp = prior?.ClusterStamp ?? Guid.NewGuid().ToString("N");
                // The PHYSICAL-instance CANDIDATE is fresh per attempt — never inherited from
                // the prior record the way the lineage stamp is — but the INTENT record below
                // deliberately carries the PRIOR attempt's instance (or none), not this
                // candidate: the record's instance must only ever name a physical object
                // whose summary the record actually owns, and at intent time the candidate
                // names nothing. The record is advanced to the candidate by a generation-CAS
                // re-persist immediately after a successful CREATE (and to the resident's
                // instance on the ADOPTION path) — both write-ahead of the summary store. A
                // crash before either correction leaves the record naming the prior state:
                // the safe direction, since the prior summary is exactly what its cleanup
                // still owns, while a candidate persisted up front would leave failure exits
                // holding a record whose instance matches no object — and the lineage's live
                // summary would be spared by every cleanup, orphaned forever.
                clusterInstance = Guid.NewGuid().ToString("N");
                // Recorded on the proposal so a zero-admission attempt's empty shell stays
                // findable by dismissal after this attempt retracts its record.
                collapse.ClusterId = clusterId;
                collapse.ClusterStamp = clusterStamp;
                intentRecord = new CollapseRecord(
                    collapseId, clusterId, summaryId, collapse.Ns,
                    collapse.MemberIds.ToList(),
                    prior is null
                        ? new Dictionary<string, string>()
                        : new Dictionary<string, string>(prior.PreviousStates),
                    tenantId: tenantId,
                    appliedLifecycleRevisions: prior?.AppliedLifecycleRevisions,
                    expectedLifecycleRevisions: prior?.ExpectedLifecycleRevisions,
                    // A FRESH lineage seeds its generation from the unique reservation counter
                    // rather than restarting at 1: generations are equality-compared CAS
                    // tokens, and a restarted sequence let an undoer suspended with an OLD
                    // lineage's generation-1 view terminal-delete a NEW lineage's generation-1
                    // record — cross-lineage ABA. A legacy prior (generation 0) jumps to the
                    // unique range for the same reason; a live prior advances by one.
                    generation: prior is null || prior.Generation == 0
                        ? _index.ReserveLifecycleRevision().Value
                        : prior.Generation + 1,
                    clusterStamp: clusterStamp,
                    clusterInstance: prior?.ClusterInstance);
                _collapseHistory[collapseId] = intentRecord;
            }
            finally { _lock.ExitWriteLock(); }
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        // CONDITIONAL, like every durable record write in this method: the persist takes
        // effect only against the exact generation this attempt planned from. A store that
        // moved underneath (a second process undid or advanced the record after our history
        // load) refuses here, before any side effect, instead of being silently overwritten.
        switch (PersistRecordIf(intentRecord, expectedGeneration: prior?.Generation))
        {
            case CollapseRecordCas.Applied:
                break;
            case CollapseRecordCas.AlreadyAbsent:
            case CollapseRecordCas.GenerationMoved:
                RefreshRecordFromStore(collapseId);
                return $"Error: Collapse '{collapseId}' changed on the store since it was read (a concurrent actor moved it); nothing was executed. Pending collapse preserved for retry.";
            default:
                // StoreFailed is an UNKNOWN outcome — the write can have committed with only
                // its confirmation lost (the commit path's own discipline, below). RESOLVE
                // the unknown with a strict read when the store will answer: found → the
                // write landed, the cache keeps the intent (matching the store); absent → it
                // did not, memory returns to the prior state. Only an UNREADABLE store keeps
                // the intent cached unresolved — stale-PRESENT fails safe (dismissal's
                // strict read and any retry's conditional persist reconcile it honestly),
                // where erasing it would leave the cache stale-ABSENT over a possibly-durable
                // record, and dismissal's cache-absent path — which skips the strict store
                // read on the strength of "every record write of this process lands in the
                // cache first" — would then retire the proposal over a durable phantom.
                if (_persistence is not null && _persistence.TryReadCollapseRecord(collapseId, out var afterFail))
                {
                    if (afterFail is null || afterFail.TenantId != tenantId)
                        RestoreRecordInMemory(collapseId, prior);
                    // else: the write landed; the cached intent already matches the store.
                }
                return $"Error: Collapse '{collapseId}' was not executed — its recovery receipt could not be persisted, and no side effect may happen without one. Pending collapse preserved for retry.";
        }

        // Create cluster (in the collapse's tenant), carrying the incarnation stamp AND the
        // physical instance the durable record already names.
        var createResult = clusters.CreateCluster(clusterId, collapse.Ns, collapse.MemberIds,
            "Auto-accreted cluster", tenantId: tenantId, creationStamp: clusterStamp,
            creationInstance: clusterInstance);
        if (createResult.StartsWith("Error:"))
        {
            if (!createResult.Contains("already exists"))
                return createResult;

            // IDENTITY CHECK before adopting the resident — by INCARNATION STAMP, not by name
            // or namespace. The cluster id is public knowledge once the record is durable, and
            // a cluster is free to be created under any id in any namespace; a name-only
            // adoption would write this collapse's summary into a cluster this record never
            // created (and, cross-namespace, orphan it where no cleanup will ever look). The
            // stamp exists in exactly one lineage: this record's.
            if (!clusters.TryGetClusterIdentity(clusterId, tenantId, out var residentStamp, out var residentInstance)
                || !string.Equals(residentStamp, clusterStamp, StringComparison.Ordinal))
            {
                return $"Error: Collapse '{collapseId}' found its cluster id occupied by a different cluster incarnation. Not adopted; pending collapse preserved for retry.";
            }

            // ADOPTION corrects the record's PHYSICAL instance before any summary exists,
            // when it differs. The intent record carries the PRIOR attempt's instance, so a
            // straightforward adoption of the prior's surviving object matches and skips the
            // write; a divergence (a legacy record meeting a stamped object, or a resident
            // recreated by a crashed attempt's correction) is re-persisted generation-CAS'd
            // — write-ahead like the intent itself — so the durable record names the exact
            // object whose summary this attempt will store and later own. Refusal fails
            // closed with the pending preserved; the store may have been advanced by a
            // concurrent actor this attempt must not write over.
            if (!string.Equals(residentInstance, intentRecord.ClusterInstance, StringComparison.Ordinal))
            {
                // PRESERVE BOTH SIDES OF THE HANDOFF — see the fresh-create branch: a
                // divergence means the record's named state left a summary the advanced
                // record could never clean up. Delete it under the current authority first,
                // and PROVE IT DURABLE with an UNCONDITIONAL flush (never gated on the
                // delete's return — see the fresh-create branch for the retry-bypass that
                // gating opened) before the advance.
                if (prior is not null)
                {
                    _index.DeleteIfSummaryOf(summaryId, collapse.Ns, tenantId, clusterId,
                        onlyIfStamp: clusterStamp, onlyIfInstance: intentRecord.ClusterInstance);
                    if (_persistence is not null && !_persistence.TryFlush())
                        return $"Error: Collapse '{collapseId}' could not make its summary handoff durable; no members were archived. Pending collapse preserved for retry.";
                }

                var adopted = new CollapseRecord(
                    collapseId, clusterId, summaryId, collapse.Ns,
                    intentRecord.MemberIds,
                    intentRecord.PreviousStates,
                    tenantId: tenantId,
                    appliedLifecycleRevisions: intentRecord.AppliedLifecycleRevisions,
                    expectedLifecycleRevisions: intentRecord.ExpectedLifecycleRevisions,
                    generation: intentRecord.Generation + 1,
                    clusterStamp: clusterStamp,
                    clusterInstance: residentInstance);
                if (PersistRecordIf(adopted, expectedGeneration: intentRecord.Generation) != CollapseRecordCas.Applied)
                {
                    RefreshRecordFromStore(collapseId);
                    return $"Error: Collapse '{collapseId}' could not record its adopted cluster instance (the record moved or the store refused); nothing was executed. Pending collapse preserved for retry.";
                }
                _lock.EnterWriteLock();
                try { _collapseHistory[collapseId] = adopted; }
                finally { _lock.ExitWriteLock(); }
                intentRecord = adopted;
            }

            // A cluster left by an earlier attempt adds nothing on the already-exists branch, so
            // a member that was screened THEN would stay outside it FOREVER — a proposal whose
            // first attempt admitted nothing could never admit anything later, whatever became
            // of the ambiguity. Re-propose whatever the stored map does not hold and let
            // UpdateCluster's own screen re-decide the admissions now. The edit is
            // stamp-conditioned like every other write into this cluster: a replacement landing
            // between the check above and this write refuses instead of being rewritten.
            var missing = collapse.MemberIds
                .Where(m => !clusters.GetClusterMembershipsForEntry(m, tenantId: tenantId)
                    .Any(x => x.ClusterId == clusterId))
                .ToList();
            if (missing.Count > 0)
            {
                // The refusal matters: UpdateCluster fails the WHOLE write when attribution
                // moved during it, and swallowing that here would let the partition below read
                // the raced members as "screened" — the collapse could then complete
                // successfully while silently omitting a member that was never ambiguous at
                // all, only unlucky. (A genuinely ambiguous member is different: the write
                // succeeds and the screen drops just that id, which the partition reports.)
                var readmit = clusters.UpdateCluster(clusterId, addIds: missing, removeIds: null, label: null,
                    tenantId: tenantId, onlyIfStamp: clusterStamp);
                if (readmit.StartsWith("Error:"))
                    return $"Error: Collapse '{collapseId}' could not re-propose members into existing cluster '{clusterId}'. Pending collapse preserved for retry. Details: {readmit}";
            }
        }
        else
        {
            // PRESERVE BOTH SIDES OF THE HANDOFF: the record is about to stop naming the
            // prior attempt's state, and whatever summary that state left behind is only
            // deletable under the CURRENT record's authority — once the record advances,
            // every instance-conditioned cleanup would spare it forever. Delete it now,
            // write-ahead of the advance, while the record still owns it (instance-pinned
            // for instance-carrying records, stamp-only for legacy ones — the generation
            // CAS already fenced out every concurrent lineage writer at the intent persist,
            // so a stamp-S summary here is provably the prior attempt's own). A crash after
            // this delete and before the advance leaves the record naming the prior state
            // with its summary already gone — every later cleanup no-ops harmlessly.
            if (prior is not null)
            {
                _index.DeleteIfSummaryOf(summaryId, collapse.Ns, tenantId, clusterId,
                    onlyIfStamp: clusterStamp, onlyIfInstance: intentRecord.ClusterInstance);
                // THE HANDOFF MUST BE DURABLE before the record advances, and the flush is
                // UNCONDITIONAL — never gated on the delete's return: a false there can mean
                // "nothing resident" OR "deleted by an EARLIER failed attempt whose durable
                // delete is still queued/retained, durability unproven". Gating on it let a
                // retry advance the record synchronously over an unflushed delete; a crash
                // then resurrected the prior summary under a record naming the new instance
                // — unaddressable by any cleanup forever. A flush over an empty queue is
                // cheap; fail closed with the record still naming the prior state.
                if (_persistence is not null && !_persistence.TryFlush())
                    return $"Error: Collapse '{collapseId}' could not make its summary handoff durable; no members were archived. Pending collapse preserved for retry.";
            }

            // FRESH CREATE: the durable record still names the PRIOR attempt's instance (or
            // none) — advance it to the instance just created, write-ahead of the summary
            // store, so the record owns exactly the object whose summary it will later clean
            // up. Refusal fails closed; a crash before this correction leaves the record in
            // the safe prior-naming state (see the candidate's comment above).
            var created = new CollapseRecord(
                collapseId, clusterId, summaryId, collapse.Ns,
                intentRecord.MemberIds,
                intentRecord.PreviousStates,
                tenantId: tenantId,
                appliedLifecycleRevisions: intentRecord.AppliedLifecycleRevisions,
                expectedLifecycleRevisions: intentRecord.ExpectedLifecycleRevisions,
                generation: intentRecord.Generation + 1,
                clusterStamp: clusterStamp,
                clusterInstance: clusterInstance);
            if (PersistRecordIf(created, expectedGeneration: intentRecord.Generation) != CollapseRecordCas.Applied)
            {
                RefreshRecordFromStore(collapseId);
                return $"Error: Collapse '{collapseId}' could not record its created cluster instance (the record moved or the store refused); no members were archived. Pending collapse preserved for retry.";
            }
            _lock.EnterWriteLock();
            try { _collapseHistory[collapseId] = created; }
            finally { _lock.ExitWriteLock(); }
            intentRecord = created;
        }

        // Archive only what the cluster actually ADMITTED, and read that from the STORED
        // membership map — never from the projected GetCluster view. CreateCluster's topology
        // screen silently drops an id the tenant holds in two namespaces, and archiving a
        // screened-out member would hide it from default search with nothing standing in for
        // it. But the projection screens by attribution AT READBACK TIME: a twin planted after
        // admission would suppress an admitted member from the readback, dropping it from the
        // history record — where the undo could never find it again while its membership (and
        // its archived state) quietly survived. The stored map is unscreened by contract, so
        // an admission, once made, stays visible to this receipt whatever crosses later. A
        // member that was never admitted and is also GONE keeps the pre-existing
        // partial-failure contract: error out and preserve the pending collapse for retry.
        var admittedMembers = new List<string>(collapse.MemberIds.Count);
        var archiveErrors = new List<string>();
        int skippedMembers = 0;
        foreach (var memberId in collapse.MemberIds)
        {
            bool admitted = clusters.GetClusterMembershipsForEntry(memberId, tenantId: tenantId)
                .Any(m => m.ClusterId == clusterId);
            if (admitted)
            {
                admittedMembers.Add(memberId);
            }
            else if (_index.Get(memberId, collapse.Ns, tenantId: tenantId) is not null)
            {
                // Present in the collapse's namespace but never admitted: the creation screen
                // deliberately excluded it. Leave it unarchived and say so below.
                skippedMembers++;
            }
            else
            {
                archiveErrors.Add($"{memberId}: Error: Entry '{memberId}' not found.");
            }
        }

        // Nothing admitted and nothing missing: every member is currently screened out. A
        // "Collapsed 0" success here would retire the proposal while an empty cluster is all
        // that remains. Refuse instead — and retract the intent record, which covered nothing:
        // the pending collapse stays retryable (a later attempt simply mints a fresh lineage),
        // and the re-propose on the already-exists branch above readmits members once their
        // ambiguity resolves.
        //
        // The empty shell comes down BEFORE the record retracts, in that order deliberately:
        // the record is the only durable thing that names the shell, so retracting it first
        // and then failing the cleanup would orphan the shell with nothing on disk that knows
        // about it. (In-process, the proposal's ClusterId also names it, which is what lets
        // DISMISSAL clean up when this removal fails or the attempt dies here.) A shell that
        // is suddenly NOT empty means someone put members into this incarnation's cluster
        // concurrently — then the record must stand, because it is what a later undo needs.
        if (admittedMembers.Count == 0 && archiveErrors.Count == 0)
        {
            // Stamp-conditioned, like every destructive cluster operation this record drives:
            // only OUR incarnation's shell comes down. A same-id resident someone else created
            // (StampMismatch) is not ours to remove — and not ours to keep a record for.
            if (clusters.RemoveClusterIfEmpty(clusterId, tenantId: tenantId, onlyIfStamp: clusterStamp)
                == EmptyClusterRemoval.NotEmpty)
            {
                return $"Error: Collapse '{collapseId}' admitted no members, but its cluster has since acquired members from elsewhere. Record preserved; retry or undo.";
            }

            // RELEASE the record's claims before retracting it — proven, not argued. The old
            // argument ("all-screened implies no applied claims: applied members live in the
            // stored membership until an undo evicts and restores them") assumed only the undo
            // evicts memberships, and the public UpdateCluster disproves it: a partial
            // collapse's archived members can be evicted by any caller, twins can then screen
            // re-admission, and this branch would retract a record whose claims still hold
            // real archived entries — stranding them the moment the record is gone. The
            // release pass restores what stands archived under our installed witnesses and
            // disarms what is still armed; after it, every claim provably matches no entry —
            // or the record stays, fail-closed, because a claim could not be released.
            if (!ReleaseClaims(intentRecord, tenantId))
                return $"Error: Collapse '{collapseId}' admitted no members, but a prior claim could not be released; record preserved. Undo it before dismissing.";

            // A PARTIAL prior attempt can have stored the summary before failing; retiring the
            // record without deleting it would leave the summary searchable forever with
            // nothing left that names it. Stamped AND instance-conditioned: the stamp alone
            // names a lineage every retry shares, so a concurrent retry's live summary (its
            // record advanced with a fresh instance before its effects) would match a
            // stamp-only delete — the instance this record persisted spares it. A legacy
            // record without an instance keeps the stamp-only compare.
            _index.DeleteIfSummaryOf(summaryId, collapse.Ns, tenantId, clusterId,
                onlyIfStamp: clusterStamp, onlyIfInstance: intentRecord.ClusterInstance);

            // The record must OUTLIVE the shell removal and the restores it names — the same
            // durability order the undo obeys: those effects ride debounced writes, while the
            // retraction below commits synchronously, and a crash between the two would
            // resurrect durable state on restart with no record naming it. A flush that
            // cannot prove them durable keeps the record and retries.
            if (_persistence is not null && !_persistence.TryFlush())
                return $"Error: Collapse '{collapseId}' admitted no members and released its prior claims, but the changes could not be made durable; record preserved for retry.";

            // The retraction then DELETES outright, prior or no prior — its claims were just
            // released, and keeping the prior instead would leave an intent-shaped record
            // that blocks dismissal forever for no receipt at all.
            //
            // GENERATION-COMPARED, store-first, and checked: an unconditional delete here
            // could erase a record a concurrent writer had just advanced with claims this
            // branch never judged, and a refused store write with memory already rolled back
            // would let dismissal — which consults only memory — retire the proposal while a
            // durable record survives.
            switch (_persistence is null
                ? CollapseRecordCas.Applied
                : _persistence.DeleteCollapseRecordSync(collapseId, intentRecord.Generation))
            {
                case CollapseRecordCas.Applied:
                case CollapseRecordCas.AlreadyAbsent:
                    RestoreRecordInMemory(collapseId, null);
                    return $"Error: Collapse '{collapseId}' admitted no members — every member id is currently ambiguous in the tenant. Pending collapse preserved for retry.";
                case CollapseRecordCas.GenerationMoved:
                    RefreshRecordFromStore(collapseId);
                    return $"Error: Collapse '{collapseId}' admitted no members, but its record advanced concurrently; nothing was retracted. Pending collapse preserved for retry.";
                default:
                    return $"Error: Collapse '{collapseId}' admitted no members, and its intent record could not be retracted from the store. Pending collapse preserved for retry.";
            }
        }

        // Store summary — STAMPED, like every write this record drives: a replacement cluster
        // that took the id between the adoption check and this call refuses here instead of
        // receiving this collapse's summary (which the undo would then rightly spare, leaving
        // the corruption in place). No occupation pin is needed beyond that — the undo deletes
        // the summary by content identity plus incarnation, which the intent record already
        // authorizes.
        var storedSummaryId = clusters.StoreSummary(clusterId, summaryText, summaryVector, tenantId: tenantId,
            onlyIfStamp: clusterStamp);
        if (storedSummaryId.StartsWith("Error:"))
            return storedSummaryId;

        // PLAN the archives: pre-read each admitted member, skip the already-archived (a prior
        // attempt's claim is carried in the receipt; another actor's archive is not ours), and
        // capture the FULL planned transition — the state it leaves, the lifecycle witness it
        // leaves it AT, and the reserved revision its archive will install.
        var planned = new Dictionary<string, (string FromState, long Expected, LifecycleReservation Install)>(StringComparer.Ordinal);
        foreach (var memberId in admittedMembers)
        {
            var pre = _index.Get(memberId, collapse.Ns, tenantId: tenantId);
            if (pre is null)
            {
                archiveErrors.Add($"{memberId}: Error: Entry '{memberId}' not found.");
                continue;
            }
            if (pre.LifecycleState == "archived")
                continue;

            planned[memberId] = (pre.LifecycleState, pre.LifecycleRevision, _index.ReserveLifecycleRevision());
        }

        // CLAIMS AHEAD OF EFFECTS: the receipt — each planned member's from-state, the witness
        // it was planned at, and the reserved revision its archive will install — is durable
        // BEFORE any archive runs. Safe to over-declare, because a reserved revision only ever
        // enters an entry through the CAS below: a claim whose transition never fired (crash,
        // CAS loss) matches no entry and is inert. And the CAS fires only from the exact
        // recorded state AND witness, so the receipt cannot go stale between this persist and
        // the effect: an intervening lifecycle change moves the witness, and so does a
        // same-state ABA (ltm → stm → ltm, or a replacement upsert landing on the same state) —
        // a state-only compare absorbed exactly those, archiving occupations the plan never
        // examined.
        CollapseRecord durableRecord = intentRecord;
        if (planned.Count > 0)
        {
            CollapseRecord claimedRecord;
            _lock.EnterWriteLock();
            try
            {
                var existing = _collapseHistory[collapseId];
                var prevStates = new Dictionary<string, string>(existing.PreviousStates);
                var appliedAll = existing.AppliedLifecycleRevisions is null
                    ? new Dictionary<string, long>(StringComparer.Ordinal)
                    : new Dictionary<string, long>(existing.AppliedLifecycleRevisions, StringComparer.Ordinal);
                var expectedAll = existing.ExpectedLifecycleRevisions is null
                    ? new Dictionary<string, long>(StringComparer.Ordinal)
                    : new Dictionary<string, long>(existing.ExpectedLifecycleRevisions, StringComparer.Ordinal);
                foreach (var (m, plan) in planned)
                {
                    prevStates[m] = plan.FromState;
                    appliedAll[m] = plan.Install.Value;
                    expectedAll[m] = plan.Expected;
                }
                claimedRecord = new CollapseRecord(
                    existing.CollapseId, existing.ClusterId, existing.SummaryEntryId, existing.Ns,
                    existing.MemberIds, prevStates, existing.CollapsedAt,
                    existing.TenantId, appliedAll, expectedAll,
                    generation: existing.Generation + 1,
                    clusterStamp: existing.ClusterStamp,
                    clusterInstance: existing.ClusterInstance);
                _collapseHistory[collapseId] = claimedRecord;
            }
            finally { _lock.ExitWriteLock(); }

            switch (PersistRecordIf(claimedRecord, expectedGeneration: intentRecord.Generation))
            {
                case CollapseRecordCas.Applied:
                    durableRecord = claimedRecord;
                    break;
                case CollapseRecordCas.AlreadyAbsent:
                    // A concurrent undoer retired the record after our intent persisted;
                    // nothing has been archived, so there is nothing of ours to reverse.
                    RestoreRecordInMemory(collapseId, null);
                    return $"Error: Collapse '{collapseId}' was concurrently undone before its archives began; nothing was archived. Pending collapse preserved for retry.";
                case CollapseRecordCas.GenerationMoved:
                    RefreshRecordFromStore(collapseId);
                    return $"Error: Collapse '{collapseId}' advanced concurrently before its archives began; nothing was archived. Pending collapse preserved for retry.";
                default:
                    // The un-persisted claims must not stand in memory either: memory and disk
                    // would disagree about what this collapse holds. The intent record IS the
                    // last durable state — nothing between its persist and here wrote another.
                    RestoreRecordInMemory(collapseId, intentRecord);
                    return $"Error: Collapse '{collapseId}' could not persist its archive receipt; nothing was archived. Pending collapse preserved for retry.";
            }

            OnBeforeArchiveCas?.Invoke();

            // CAS-archive each planned member: from exactly the recorded state and witness,
            // installing exactly the claimed revision. A refusal means the member changed
            // between the plan and now — its claim stays inert and the retry re-plans it from
            // fresh state.
            foreach (var (memberId, plan) in planned)
            {
                if (!_index.TryTransitionLifecycle(
                        memberId, collapse.Ns, tenantId,
                        fromState: plan.FromState, toState: "archived",
                        installRevision: plan.Install,
                        expectedRevision: plan.Expected))
                {
                    archiveErrors.Add($"{memberId}: Error: Entry '{memberId}' changed concurrently and was not archived. Retry.");
                }
            }
        }

        // THE DURABLE PHASE TRANSITION, after the archives and before any reply — a
        // generation-CAS WRITE, not a read. A read could only say the record existed at some
        // instant; an undoer's terminal delete could still land between that read and this
        // method returning, leaving the archives standing with no receipt anywhere. The commit
        // write advances the record's generation atomically at the store, so exactly one of
        // the two terminal operations wins: if this commit APPLIES first, any undoer holding
        // the older generation gets GenerationMoved on its delete and must re-read; if the
        // undoer's delete landed first, this commit refuses and the archives are rolled back
        // by the same CAS discipline that installed them. (An undoer can also retire the
        // record while claims are persisted but UNFIRED — its restore pass DISARMS such armed
        // claims by moving the members' witnesses, so the archive loop above refuses and the
        // AlreadyAbsent branch below rolls back nothing that stands.)
        //
        // STATED RESIDUAL: the record CAS cannot witness ENTRY-level changes. A cross-process
        // undoer that restored these freshly-fired archives and tore down the cluster between
        // the archive loop and this commit leaves the commit to APPLY against an unmoved
        // generation — this method then reports success for a collapse the store no longer
        // holds. Every artifact of that interleaving is safe and self-healing (the committed
        // record's claims are inert against the restored witnesses, and a later undo retires
        // it as a near-no-op); only this reply's tense can overstate.
        //
        // A STORE FAILURE is neither: existence is unknown — roll back the archives (fail
        // toward safety; the rollback only re-inertifies this attempt's claims) but touch
        // nothing else, because erasing the in-memory record on an unreadable store once let a
        // retry find prior=null, mint a fresh lineage, and overwrite the durable receipt.
        OnBeforeDurableCommit?.Invoke();

        if (_persistence is not null)
        {
            void RollBackPlannedArchives()
            {
                foreach (var (memberId, plan) in planned)
                {
                    _index.TryTransitionLifecycle(
                        memberId, collapse.Ns, tenantId,
                        fromState: "archived", toState: plan.FromState,
                        installRevision: _index.ReserveLifecycleRevision(),
                        expectedRevision: plan.Install.Value);
                }
            }

            var commitRecord = new CollapseRecord(
                durableRecord.CollapseId, durableRecord.ClusterId, durableRecord.SummaryEntryId,
                durableRecord.Ns, durableRecord.MemberIds, durableRecord.PreviousStates,
                durableRecord.CollapsedAt, durableRecord.TenantId,
                durableRecord.AppliedLifecycleRevisions, durableRecord.ExpectedLifecycleRevisions,
                generation: durableRecord.Generation + 1,
                clusterStamp: durableRecord.ClusterStamp,
                clusterInstance: durableRecord.ClusterInstance);

            switch (_persistence.UpsertCollapseRecordSync(commitRecord, durableRecord.Generation))
            {
                case CollapseRecordCas.Applied:
                    RestoreRecordInMemory(collapseId, commitRecord);
                    break;
                case CollapseRecordCas.AlreadyAbsent:
                    RollBackPlannedArchives();
                    RestoreRecordInMemory(collapseId, null);
                    return $"Error: Collapse '{collapseId}' lost its durable record mid-execution (a concurrent undo removed it); this attempt's archives were rolled back. Pending collapse preserved for retry.";
                case CollapseRecordCas.GenerationMoved:
                    RollBackPlannedArchives();
                    RefreshRecordFromStore(collapseId);
                    return $"Error: Collapse '{collapseId}' saw its durable record advance mid-execution; this attempt's archives were rolled back. Pending collapse preserved for retry.";
                default:
                    RollBackPlannedArchives();
                    return $"Error: Collapse '{collapseId}' could not COMMIT its durable phase (store unreadable or write refused); this attempt's archives were rolled back as a precaution and the receipt stands. Pending collapse preserved for retry.";
            }
        }

        if (archiveErrors.Count > 0)
        {
            return $"Error: Collapse '{collapseId}' partially failed during archive step. Pending collapse preserved for retry. Details: {string.Join(" | ", archiveErrors)}";
        }

        // Only retire the pending proposal after every step succeeded. The durable record needs
        // no further write here: the applied-set update above already persisted its final
        // content, receipts included.
        _lock.EnterWriteLock();
        try
        {
            _pendingCollapses.Remove(collapseId);
        }
        finally { _lock.ExitWriteLock(); }

        return $"Collapsed {admittedMembers.Count} entries into cluster '{clusterId}' with summary '{summaryId}'."
            + (skippedMembers > 0
                ? $" {skippedMembers} member(s) were not admitted to the cluster and were left unarchived."
                : "");
    }

    /// <summary>
    /// Dismiss a pending collapse and mark its members to skip in future scans.
    /// <paramref name="clusters"/> is REQUIRED, not optional, because dismissal is the last
    /// exit for a zero-admission attempt's empty cluster shell — the one execution state that
    /// leaves a side effect with no history record. An optional cleanup dependency is a cleanup
    /// that silently does not happen at exactly the call sites that forget it.
    /// </summary>
    public string DismissCollapse(string collapseId, string tenantId, ClusterManager clusters)
    {
        tenantId = Tenancy.Normalize(tenantId);
        string? shellClusterId;
        string? shellClusterStamp;
        bool cacheHasRecord;
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureHistoryLoaded();
            _lock.EnterWriteLock();
            try
            {
                if (!_pendingCollapses.TryGetValue(collapseId, out var pending) || pending.TenantId != tenantId)
                    return $"Error: Collapse '{collapseId}' not found.";

                // The id AND incarnation stamp the latest attempt minted for its cluster, if
                // any attempt ran — the only name the zero-admission shell has once that
                // attempt retracted its record. Captured under the lock the attempt set them
                // under; the stamp is what keeps the cleanup below from deleting a same-id
                // cluster some other actor created since.
                shellClusterId = pending.ClusterId;
                shellClusterStamp = pending.ClusterStamp;

                // Whether the CACHE claims a partially executed attempt — decided under the
                // lock, but ACTED on only after the in-flight slot is held and the store has
                // been consulted (below): the cache is a process-local view that goes
                // stale-PRESENT when another stack retires the record, and refusing from it
                // alone wedged dismissal behind a receipt that no longer exists anywhere.
                cacheHasRecord = _collapseHistory.TryGetValue(collapseId, out var executed)
                    && executed.TenantId == tenantId;

                // Same in-flight slot as execute/undo: a dismissal racing an execution could
                // report success while the execution goes on to archive the members anyway.
                if (!TryEnterInFlight(collapseId))
                    return $"Error: Collapse '{collapseId}' is already being executed or undone.";
            }
            finally { _lock.ExitWriteLock(); }
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        try
        {
            // A partially executed attempt owns real side effects — archived members, a
            // cluster, a durable provisional record. Dismissal would orphan every one of them
            // while erasing the proposal that knows how to retry or reverse them. DURABLE
            // ABSENCE OVERRIDES THE CACHE: with the in-flight slot held (no execute or undo
            // of this collapse is mid-flight), a strict read that finds no record means the
            // attempt was fully retired elsewhere — the stale cache entry is dropped and the
            // dismissal proceeds. An unreadable store refuses retryably; a durable record
            // refuses toward undo, as before. (The reverse staleness cannot arise: pendings
            // are process-local, and every record write of this process lands in the cache
            // under the same lock before the store CAS.)
            if (cacheHasRecord)
            {
                if (_persistence is null)
                    return $"Error: Collapse '{collapseId}' has a partially executed attempt on record. Undo it before dismissing.";
                if (!_persistence.TryReadCollapseRecord(collapseId, out var durable))
                    return $"Error: Collapse '{collapseId}' has an attempt on record that could not be strictly read from the store; nothing was dismissed. Retry.";
                if (durable is not null && durable.TenantId == tenantId)
                    return $"Error: Collapse '{collapseId}' has a partially executed attempt on record. Undo it before dismissing.";
                _lock.EnterWriteLock();
                try { _collapseHistory.Remove(collapseId); }
                finally { _lock.ExitWriteLock(); }
            }

            // Shell cleanup BEFORE the proposal is retired, while the in-flight slot still
            // blocks a concurrent execute from recreating it: an attempt that admitted no
            // members created an empty cluster and nothing else, and dismissing the proposal
            // is that shell's last exit. Retiring first and cleaning after would let a cleanup
            // failure orphan the shell with nothing left that knows about it. The id comes
            // from the proposal — cluster ids carry a per-incarnation nonce and cannot be
            // derived — and a proposal no attempt ever executed has no shell to clean.
            //
            // A NON-empty accretion cluster refuses the dismissal outright. With no history
            // record (checked above), members inside it can only mean an attempt that admitted
            // them and then failed before its durability point — retiring the proposal now
            // would orphan those admissions with nothing left that can complete or reverse
            // them. Executing the proposal to completion (the already-exists branch re-adopts
            // the cluster) resolves it; so does emptying the cluster by hand.
            // Stamp-conditioned, like every destructive cluster operation a proposal drives:
            // only the incarnation THIS lineage minted comes down. A same-id resident someone
            // else created since (StampMismatch) is not ours — nothing to clean, dismissal
            // proceeds — and the NotEmpty refusal fires only for a shell this lineage owns.
            if (shellClusterId is not null
                && clusters.RemoveClusterIfEmpty(shellClusterId, tenantId: tenantId, onlyIfStamp: shellClusterStamp)
                    == EmptyClusterRemoval.NotEmpty)
            {
                return $"Error: Collapse '{collapseId}' has admitted members in its cluster from an incomplete attempt. Execute it to completion (or empty the cluster) before dismissing.";
            }

            int memberCount;
            _lock.EnterWriteLock();
            try
            {
                if (!_pendingCollapses.TryGetValue(collapseId, out var collapse) || collapse.TenantId != tenantId)
                    return $"Error: Collapse '{collapseId}' not found.";

                collapse.Dismissed = true;
                foreach (var memberId in collapse.MemberIds)
                    _dismissedEntryIds.Add((tenantId, memberId));

                _pendingCollapses.Remove(collapseId);
                memberCount = collapse.MemberIds.Count;
            }
            finally { _lock.ExitWriteLock(); }

            return $"Dismissed collapse '{collapseId}'. {memberCount} entries excluded from future scans.";
        }
        finally
        {
            ExitInFlight(collapseId);
        }
    }

    /// <summary>
    /// Reverse a previously executed collapse: restore members to pre-collapse state,
    /// delete the summary entry, and remove the cluster.
    /// </summary>
    public string UndoCollapse(
        string collapseId, LifecycleEngine lifecycle, ClusterManager clusters, string tenantId)
    {
        tenantId = Tenancy.Normalize(tenantId);
        CollapseRecord? record;
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureHistoryLoaded();
            record = _collapseHistory.TryGetValue(collapseId, out var cached) && cached.TenantId == tenantId
                ? cached
                : null;
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        // A CACHE MISS IS NOT ABSENCE: the in-memory map is a one-shot lenient boot load, so
        // a record another stack persisted since — or one the lenient loader dropped — is
        // invisible to it. A recovery scanner must be able to see and undo such a record, so
        // a miss consults the store STRICTLY before answering "not found".
        if (record is null)
        {
            if (_persistence is null)
                return $"Error: No collapse record found for '{collapseId}'.";
            if (!_persistence.TryReadCollapseRecord(collapseId, out var missed))
                return $"Error: Collapse '{collapseId}' could not be strictly read from the store; nothing was undone. Retry.";
            if (missed is null || missed.TenantId != tenantId)
                return $"Error: No collapse record found for '{collapseId}'.";
            // Deliberately NOT installed into the cache here — this read is OUTSIDE the
            // in-flight slot, so it races a concurrent undo's window between its cache remove
            // and its store delete exactly like GetCollapseRecordNs (see its comment), and an
            // install would resurrect the just-retired record into the cache while the
            // TryEnterInFlight below then fails and leaves the phantom behind. The in-slot
            // strict re-read installs the authoritative copy; this local suffices until then.
            record = missed;
        }

        // Same in-flight slot as ExecuteCollapse: an undo interleaved with an execution
        // (or another undo) of the same collapse would race the restore/archive
        // sequences and the history record.
        if (!TryEnterInFlight(collapseId))
            return $"Error: Collapse '{collapseId}' is already being executed or undone.";

        try
        {
            // STRICT re-read before the FIRST side effect: the in-memory record may be a
            // LENIENT boot load — checksum-mismatched, tampered, or stale — because boot
            // loading deliberately degrades rather than refuses. Everything below acts on
            // ownership claims (which entries to restore, which cluster to tear down), so the
            // claims must come from a read that fails closed; the store's validated copy is
            // authoritative, memory is re-synced to it, and an unreadable store undoes
            // nothing.
            if (_persistence is not null)
            {
                if (!_persistence.TryReadCollapseRecord(collapseId, out var durable))
                    return $"Error: Collapse '{collapseId}' has a record that could not be strictly read from the store; nothing was undone. Retry.";
                RestoreRecordInMemory(collapseId, durable);
                if (durable is null || durable.TenantId != tenantId)
                    return $"Error: No collapse record found for '{collapseId}'.";
                record = durable;
            }

            for (int attempt = 0; ; attempt++)
            {
                var result = UndoCollapseSerialized(collapseId, record, lifecycle, clusters, tenantId,
                    out bool generationMoved);
                if (!generationMoved)
                    return result;

                // The record advanced under this undo: a concurrent executor OUTSIDE this
                // process's in-flight gate persisted new claims after this pass read the
                // record, and the generation-compared delete refused to discard them. Re-read
                // the fresh record and run the whole reversal again against it — the CAS
                // restores and the idempotent cleanup act only on what the fresh claims
                // actually cover. Bounded: a second advance means the collapse is being
                // actively re-executed, and that is the caller's news, not a spin loop's.
                if (attempt >= 1)
                    return $"Error: Collapse '{collapseId}' is being re-executed concurrently; its record was not retired. Collapse record preserved; retry.";
                if (_persistence is null || !_persistence.TryReadCollapseRecord(collapseId, out var fresh))
                    return $"Error: Collapse '{collapseId}' changed while being undone and could not be re-read. Collapse record preserved; retry.";
                if (fresh is null)
                {
                    // Someone else retired the record; this pass's cleanup already ran. The
                    // exact restore count is split between the two undoers, so the reply
                    // states the outcome without inventing a number.
                    RestoreRecordInMemory(collapseId, null);
                    return $"Reversed collapse '{collapseId}': its record was retired (a concurrent undoer completed the retirement) and summary '{record.SummaryEntryId}' was removed.";
                }
                if (fresh.TenantId != tenantId)
                    return $"Error: Collapse '{collapseId}' changed while being undone. Collapse record preserved; retry.";

                record = fresh;
                _lock.EnterWriteLock();
                try { _collapseHistory[collapseId] = fresh; }
                finally { _lock.ExitWriteLock(); }
            }
        }
        finally
        {
            ExitInFlight(collapseId);
        }
    }

    // The body of UndoCollapse, entered only while holding the collapse's in-flight slot.
    // generationMoved reports the one outcome the caller must handle by re-reading: the
    // record's generation advanced between this pass's read and its terminal delete.
    private string UndoCollapseSerialized(
        string collapseId, CollapseRecord record, LifecycleEngine lifecycle, ClusterManager clusters, string tenantId,
        out bool generationMoved)
    {
        generationMoved = false;
        // OWNERSHIP: "archived" alone cannot say WHICH collapse archived a member. For a MODERN
        // record the answer is the installed witness and nothing else: the restore CAS below
        // fires only while the member's archived state carries the very revision this record's
        // archive installed, so an archive genuinely performed by a later collapse (its own
        // installed revision), or by any other actor (a moved witness), refuses by itself.
        // Deferring additionally to later records' CLAIM SETS was wrong precisely because a
        // claim is not an application: a later record that claimed a member ahead of an archive
        // that never fired (crash, CAS refusal) holds an INERT claim, and skipping on it here
        // — then retiring this record — left the member archived with no record anywhere still
        // entitled to restore it.
        //
        // A LEGACY record (no applied set) has no witness to CAS against, so only there does
        // the later-record claim scan still guard the heuristic restore — failing toward
        // restoring less, which is the safe direction for a receipt that cannot prove
        // ownership.
        HashSet<string>? claimedByLater = null;
        if (record.AppliedLifecycleRevisions is null)
        {
            // The peers this guard is built from must be STRICT, like the record itself: the
            // in-memory map is a LENIENT boot load frozen at first touch, so a later record
            // persisted since (by another stack, or tampered past the lenient loader) would be
            // invisible here and the heuristic restore would revert that later collapse's
            // work. A store that cannot be strictly read undoes nothing.
            IReadOnlyList<CollapseRecord> peers;
            if (_persistence is not null)
            {
                if (!_persistence.TryReadCollapseHistory(out var strictPeers))
                    return $"Error: Collapse '{collapseId}' is a legacy record whose peers could not be strictly read from the store; nothing was undone. Retry.";
                peers = strictPeers;
            }
            else
            {
                _lock.EnterReadLock();
                try { peers = _collapseHistory.Values.ToList(); }
                finally { _lock.ExitReadLock(); }
            }

            claimedByLater = new HashSet<string>(StringComparer.Ordinal);
            foreach (var other in peers)
            {
                if (other.CollapseId == record.CollapseId) continue;
                if (other.TenantId != tenantId || other.Ns != record.Ns) continue;
                // Strictly-later wins; an equal-instant tie (coarse clock, two collapses in
                // one tick) is broken by ordinal id so the order is TOTAL — with a bare "<="
                // skip, two same-instant records would each treat the other as not-later and
                // neither would defer, letting either undo restore members the other
                // archived.
                bool otherIsLater = other.CollapsedAt > record.CollapsedAt
                    || (other.CollapsedAt == record.CollapsedAt
                        && string.CompareOrdinal(other.CollapseId, record.CollapseId) > 0);
                if (!otherIsLater) continue;
                foreach (var m in (IEnumerable<string>?)other.AppliedLifecycleRevisions?.Keys ?? other.MemberIds)
                    claimedByLater.Add(m);
            }
        }

        // Restore each member to its pre-collapse lifecycle state — but only a member this
        // collapse ACTUALLY archived, whose archived state standing NOW is still the very
        // transition this collapse performed.
        var restoreErrors = new List<string>();
        // COUNTED, not assumed: the reply reports how many restores actually RAN. MemberIds
        // counts proposals; a member whose CAS refused (already restored, owned by newer
        // work, never archived) was not restored by this call, and saying it was overstates
        // the reversal.
        int restoredCount = 0;
        foreach (var (memberId, previousState) in record.PreviousStates)
        {
            if (record.AppliedLifecycleRevisions is not null)
            {
                if (!record.AppliedLifecycleRevisions.TryGetValue(memberId, out var archiveRevision))
                    continue;

                // CAS, not check-then-restore: the transition fires only while the entry is
                // still archived AND its lifecycle witness is still the very revision this
                // record's archive installed — validated and applied under one partition write
                // lock, so no later lifecycle work can slip between a separate check and the
                // restore. A refusal means the member is no longer this record's to restore
                // (never archived, restored already, or owned by newer work — a later
                // collapse's INSTALLED revision refuses here without any claim-set consult) —
                // a skip, not an error.
                if (_index.TryTransitionLifecycle(
                        memberId, record.Ns, tenantId,
                        fromState: "archived", toState: previousState,
                        installRevision: _index.ReserveLifecycleRevision(),
                        expectedRevision: archiveRevision))
                {
                    restoredCount++;
                    continue;
                }

                // NOT restored — but "not archived" is not "inert". A claim persisted by an
                // executor whose archive CAS has NOT YET FIRED is ARMED: the member still sits
                // at the exact planned witness, and once this undo retires the record, the
                // executor's pending CAS could archive the member with no receipt left
                // anywhere entitled to reverse it (the executor's own AlreadyAbsent rollback
                // is volatile and dies with its process). DISARM it: bump the member's
                // lifecycle witness off the planned value — state untouched — so the pending
                // archive CAS refuses forever. Fires only while the member stands exactly at
                // the planned witness; any other state means the claim already fired (restored
                // above), is inert, or the member moved on — all skips.
                if (record.ExpectedLifecycleRevisions is not null
                    && record.ExpectedLifecycleRevisions.TryGetValue(memberId, out var plannedWitness))
                {
                    _index.TryBumpLifecycleWitness(
                        memberId, record.Ns, tenantId,
                        expectedRevision: plannedWitness,
                        install: _index.ReserveLifecycleRevision());
                }
                continue;
            }

            // Legacy record without an applied receipt: the current-state heuristic is the best
            // available — restore only what stands archived now, and never a member a later
            // record lays claim to.
            if (claimedByLater!.Contains(memberId))
                continue;

            var current = _index.Get(memberId, record.Ns, tenantId: tenantId);
            if (current is null || current.LifecycleState != "archived")
                continue;

            var result = lifecycle.PromoteMemory(memberId, previousState, record.Ns, tenantId: tenantId);
            if (result.StartsWith("Error:"))
                restoreErrors.Add($"{memberId}: {result}");
            else
                restoredCount++;
        }

        if (restoreErrors.Count > 0)
            return $"Error: Uncollapse '{collapseId}' partially failed during restore. Details: {string.Join(" | ", restoreErrors)}";

        // INCARNATION GATE for the cluster-side cleanup: everything below is addressed only by
        // the PUBLIC cluster id, which anyone can recreate. A stamped record fires its
        // membership eviction and cluster removal only against the incarnation it minted —
        // resolved here to pick the path, and re-compared ATOMICALLY inside each destructive
        // operation, so a replacement landing between this read and the mutation refuses there
        // rather than being rewritten. A same-id resident this record never minted is simply
        // not ours: its members, its shell and its summary all stand. Legacy records (no
        // stamp) keep their id-only behavior.
        bool clusterIsOurs = true;
        if (record.ClusterStamp is not null
            && clusters.TryGetClusterStamp(record.ClusterId, tenantId, out var residentStamp)
            && !string.Equals(residentStamp, record.ClusterStamp, StringComparison.Ordinal))
        {
            clusterIsOurs = false;
        }

        bool summaryStaysWithCluster = false;
        if (clusterIsOurs)
        {
            // Remove the cluster (by removing all members then the cluster itself is emptied).
            // The return value matters: UpdateCluster refuses the whole write when attribution
            // moved during it, and swallowing that refusal while deleting the record below
            // would leave the memberships (and a dangling SummaryEntryId) in place with no way
            // to ever retry. A cluster that is already gone — or replaced by an incarnation
            // that is not ours — is the goal state for THIS cleanup, not a failure.
            var updateResult = clusters.UpdateCluster(record.ClusterId, addIds: null, removeIds: record.MemberIds,
                label: null, tenantId: tenantId, onlyIfStamp: record.ClusterStamp);
            if (updateResult.Contains("different incarnation"))
            {
                clusterIsOurs = false;
            }
            else if (updateResult.StartsWith("Error:") && !updateResult.Contains("not found"))
            {
                return $"Error: Uncollapse '{collapseId}' could not remove cluster memberships. Collapse record preserved for retry. Details: {updateResult}";
            }
        }

        if (clusterIsOurs)
        {
            // Success from UpdateCluster does not prove the memberships are gone: its topology
            // screen SILENTLY drops a removeId the tenant holds in two namespaces and still
            // replies "Updated cluster". Verify against the stored membership map — unscreened
            // by contract, so a suppressed projection cannot hide a survivor — before the
            // record is deleted; a membership that remains keeps the record so the cleanup
            // stays retryable once the ambiguity is resolved.
            foreach (var memberId in record.MemberIds)
            {
                if (clusters.GetClusterMembershipsForEntry(memberId, tenantId: tenantId)
                    .Any(m => m.ClusterId == record.ClusterId))
                {
                    return $"Error: Uncollapse '{collapseId}' could not remove the membership of '{memberId}' (its id may currently be ambiguous in the tenant). Collapse record preserved for retry.";
                }
            }

            // Remove the cluster OBJECT itself — UpdateCluster above only empties it, and
            // nothing else in this system removes one, so stopping there republishes a
            // zero-member shell whose SummaryEntryId dangles the moment the summary entry
            // goes. Only OUR EMPTY cluster is removable: members that appeared since the
            // verification, or a replacement incarnation, own the id now — the resident stays
            // standing, and with NotEmpty-and-ours the summary stays with it.
            summaryStaysWithCluster =
                clusters.RemoveClusterIfEmpty(record.ClusterId, tenantId: tenantId, onlyIfStamp: record.ClusterStamp)
                == EmptyClusterRemoval.NotEmpty;
        }

        if (!summaryStaysWithCluster)
        {
            // Delete the summary entry (tenant-scoped) only after the membership cleanup is
            // resolved: deleted any earlier, a failed verification would preserve the record
            // for retry while the summary it promises to remove is already gone. The delete is
            // conditional on CONTENT identity — the resident entry must be a summary node of
            // exactly this cluster AND, for stamped records, of exactly this INCARNATION and
            // (for instance-carrying records) this PHYSICAL cluster object — checked and
            // removed under one partition lock. A replacement summary stored by a recreated
            // same-id cluster carries a different stamp and stands; a concurrent retry of
            // THIS lineage carries the same stamp but a different instance, and stands too.
            _index.DeleteIfSummaryOf(record.SummaryEntryId, record.Ns, tenantId, record.ClusterId,
                onlyIfStamp: record.ClusterStamp, onlyIfInstance: record.ClusterInstance);
        }

        // DURABILITY ORDER: the record must OUTLIVE every volatile effect it reverses. The
        // restore CASes and the summary delete above ride the DEBOUNCED entry-write stream,
        // while the record delete below commits synchronously — deleting first would let a
        // crash inside the debounce window strand the members: entries still archived on disk,
        // record gone, nobody left entitled to restore them. The flush is CHECKED: providers
        // swallow queued-write errors on their timer paths, so only TryFlush — which retains a
        // failed batch and says so — can prove the restores durable, and a failed flush FAILS
        // CLOSED with the record preserved.
        if (_persistence is not null && !_persistence.TryFlush())
        {
            return $"Error: Uncollapse '{collapseId}' restored state that could not all be made durable (a pending write failed and was retained for retry); the collapse record is preserved. Retry.";
        }

        // Remove the collapse record — and prove the removal durable before reporting it. A
        // removal that only reached memory would resurrect the record on restart, and a
        // resurrected record is re-undoable; the applied/ownership guards make that re-undo a
        // near-no-op, but "Reversed" must still mean what it says.
        //
        // The delete is GENERATION-COMPARED, closing the last cross-process window: an
        // executor that persisted new claims after this pass read the record advanced its
        // generation, and an unconditional delete here would discard receipts this undo never
        // processed — the executor's own durable phase check would then find the record gone
        // AFTER it had already verified it present and reported success. The compare and the
        // removal are one atom inside the provider's serialized read-modify-write, so the
        // refusal is authoritative: the caller re-reads and re-runs.
        _lock.EnterWriteLock();
        try
        {
            _collapseHistory.Remove(collapseId);
        }
        finally { _lock.ExitWriteLock(); }

        var recordRemoval = _persistence is null
            ? CollapseRecordCas.Applied
            : _persistence.DeleteCollapseRecordSync(collapseId, record.Generation);

        if (recordRemoval is CollapseRecordCas.Applied or CollapseRecordCas.AlreadyAbsent)
            return $"Reversed collapse '{collapseId}': restored {restoredCount} of {record.MemberIds.Count} member(s), removed summary '{record.SummaryEntryId}'.";

        _lock.EnterWriteLock();
        try { _collapseHistory[collapseId] = record; }
        finally { _lock.ExitWriteLock(); }

        if (recordRemoval == CollapseRecordCas.GenerationMoved)
        {
            generationMoved = true;
            return string.Empty; // The caller re-reads the advanced record and re-runs.
        }

        return $"Error: Uncollapse '{collapseId}' completed its cleanup but could not persist the record removal. Collapse record preserved; retry.";
    }

    /// <summary>
    /// Get all recorded collapse records for a namespace — as DEFENSIVE COPIES, refreshed from
    /// a STRICT store read when a store exists. The in-memory map is a one-shot lenient boot
    /// load, so serving it alone hid records another stack persisted since; the strict set is
    /// the store's validated truth, and a store that cannot be strictly read degrades to the
    /// cached view (a listing is informational — the destructive paths do their own strict
    /// reads and fail closed). The copies matter either way: live records are the scanner's
    /// working state and their collections are mutable references; handing them out let any
    /// Core consumer rewrite a receipt's previous-states or claims in place.
    /// </summary>
    public IReadOnlyList<CollapseRecord> GetCollapseHistory(string ns, string tenantId)
    {
        tenantId = Tenancy.Normalize(tenantId);

        if (_persistence is not null && _persistence.TryReadCollapseHistory(out var strictSet))
        {
            return strictSet
                .Where(r => r.Ns == ns && r.TenantId == tenantId)
                .ToList();
        }

        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureHistoryLoaded();
            return _collapseHistory.Values
                .Where(r => r.Ns == ns && r.TenantId == tenantId)
                .Select(r => new CollapseRecord(
                    r.CollapseId, r.ClusterId, r.SummaryEntryId, r.Ns,
                    new List<string>(r.MemberIds),
                    new Dictionary<string, string>(r.PreviousStates),
                    r.CollapsedAt, r.TenantId,
                    r.AppliedLifecycleRevisions is null ? null : new Dictionary<string, long>(r.AppliedLifecycleRevisions),
                    r.ExpectedLifecycleRevisions is null ? null : new Dictionary<string, long>(r.ExpectedLifecycleRevisions),
                    r.Generation, r.ClusterStamp, r.ClusterInstance))
                .ToList();
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>Number of pending (non-dismissed) collapses.</summary>
    public int PendingCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return _pendingCollapses.Count(kv => !kv.Value.Dismissed); }
            finally { _lock.ExitReadLock(); }
        }
    }

    // Called under upgradeable read lock or write lock.
    // Upgrades to write lock if loading is needed.
    private void EnsureHistoryLoaded()
    {
        if (_historyLoaded || _persistence is null) return;

        _lock.EnterWriteLock();
        try
        {
            // Double-check after acquiring write lock
            if (_historyLoaded) return;
            var records = _persistence.LoadCollapseHistory();
            foreach (var r in records)
                _collapseHistory[r.CollapseId] = r;
            _historyLoaded = true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ── DBSCAN Implementation ──

    public static List<List<CognitiveEntry>> Dbscan(
        List<CognitiveEntry> entries, float epsilon, int minPoints)
    {
        if (entries.Count == 0)
            return new();

        // Precompute norms
        var norms = new float[entries.Count];
        for (int i = 0; i < entries.Count; i++)
            norms[i] = VectorMath.Norm(entries[i].Vector);

        // Labels: -1 = unvisited, 0 = noise, >0 = cluster ID
        var labels = new int[entries.Count];
        Array.Fill(labels, -1);

        int clusterId = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            if (labels[i] != -1) continue;

            var neighbors = RangeQuery(entries, norms, i, epsilon);

            if (neighbors.Count < minPoints)
            {
                labels[i] = 0; // Noise
                continue;
            }

            clusterId++;
            labels[i] = clusterId;

            var seedSet = new Queue<int>(neighbors);
            while (seedSet.Count > 0)
            {
                int q = seedSet.Dequeue();

                if (labels[q] == 0)
                    labels[q] = clusterId;

                if (labels[q] != -1) continue;
                labels[q] = clusterId;

                var qNeighbors = RangeQuery(entries, norms, q, epsilon);
                if (qNeighbors.Count >= minPoints)
                {
                    foreach (var n in qNeighbors)
                        seedSet.Enqueue(n);
                }
            }
        }

        // Group by cluster ID
        var clusters = new Dictionary<int, List<CognitiveEntry>>();
        for (int i = 0; i < entries.Count; i++)
        {
            if (labels[i] <= 0) continue;
            if (!clusters.ContainsKey(labels[i]))
                clusters[labels[i]] = new();
            clusters[labels[i]].Add(entries[i]);
        }

        return clusters.Values.ToList();
    }

    private static List<int> RangeQuery(
        List<CognitiveEntry> entries, float[] norms, int pointIndex, float epsilon)
    {
        var neighbors = new List<int>();
        var pointVector = entries[pointIndex].Vector;
        float pointNorm = norms[pointIndex];

        if (pointNorm == 0f) return neighbors;

        for (int i = 0; i < entries.Count; i++)
        {
            if (i == pointIndex) continue;
            if (norms[i] == 0f) continue;
            if (entries[i].Vector.Length != pointVector.Length) continue;

            float dot = VectorMath.Dot(pointVector, entries[i].Vector);
            float cosine = dot / (pointNorm * norms[i]);
            float distance = 1f - cosine;

            if (distance <= epsilon)
                neighbors.Add(i);
        }

        return neighbors;
    }

    private static float[] ComputeCentroid(List<CognitiveEntry> entries)
    {
        if (entries.Count == 0) return Array.Empty<float>();

        var dim = entries[0].Vector.Length;
        var centroid = new float[dim];
        int validCount = 0;

        foreach (var entry in entries)
        {
            if (entry.Vector.Length != dim) continue;
            for (int i = 0; i < dim; i++)
                centroid[i] += entry.Vector[i];
            validCount++;
        }

        if (validCount == 0)
            return Array.Empty<float>();

        for (int i = 0; i < dim; i++)
            centroid[i] /= validCount;

        return centroid;
    }

    /// <summary>Check if a cluster already exists for this set of members.</summary>
    /// <summary>
    /// Stable identity for a set of member ids, order-independent. Trimmed to 32 hex characters so
    /// the id keeps the same shape as the Guid it replaces, which leaves previously written ids
    /// parseable and log lines the same width.
    /// </summary>
    private static string MemberSetFingerprint(IEnumerable<string> memberIds)
    {
        var canonical = string.Join("\n", memberIds.OrderBy(id => id, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32]
            .ToLowerInvariant();
    }

    private static bool HasExistingCluster(ClusterManager clusters, string ns, List<string> memberIds, string tenantId)
    {
        var existing = clusters.ListClusters(ns, tenantId: tenantId);
        foreach (var cluster in existing)
        {
            var detail = clusters.GetCluster(cluster.ClusterId, tenantId: tenantId);
            if (detail is null) continue;
            var existingIds = detail.Members.Select(m => m.Id).ToHashSet();
            if (existingIds.SetEquals(memberIds))
                return true;
        }
        return false;
    }

    private bool IsAlreadyPending(List<string> memberIds, string tenantId)
    {
        var set = new HashSet<string>(memberIds);
        foreach (var collapse in _pendingCollapses.Values)
        {
            if (collapse.Dismissed || collapse.TenantId != tenantId) continue;
            if (collapse.MemberIds.Count == set.Count &&
                collapse.MemberIds.All(id => set.Contains(id)))
                return true;
        }
        return false;
    }
}
