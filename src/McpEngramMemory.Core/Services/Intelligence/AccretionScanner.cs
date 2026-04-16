using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Core.Services.Intelligence;

/// <summary>
/// Manages DBSCAN density scanning of LTM-tier entries and pending collapse state.
/// Detects dense vector clusters and proposes them for LLM-driven summarization and collapse.
/// </summary>
public sealed class AccretionScanner
{
    private readonly CognitiveIndex _index;
    private readonly IStorageProvider? _persistence;
    private readonly Dictionary<string, PendingCollapse> _pendingCollapses = new();
    private readonly Dictionary<string, CollapseRecord> _collapseHistory = new();
    private readonly HashSet<string> _dismissedEntryIds = new();
    private readonly ReaderWriterLockSlim _lock = new();
    private bool _historyLoaded;

    public AccretionScanner(CognitiveIndex index, IStorageProvider? persistence = null)
    {
        _index = index;
        _persistence = persistence;
    }

    /// <summary>
    /// Scan a namespace for dense clusters among LTM-tier entries using DBSCAN.
    /// When autoSummarize is true, automatically creates clusters and summary entries
    /// without archiving members (GraphRAG-style hierarchical summaries).
    /// </summary>
    public AccretionScanResult ScanNamespace(
        string ns, float epsilon = 0.15f, int minPoints = 3,
        bool autoSummarize = false, ClusterManager? clusters = null,
        IEmbeddingService? embedding = null)
    {
        // Get all LTM entries in the namespace (outside _lock — uses _index's own lock)
        var allEntries = _index.GetAllInNamespace(ns);
        var ltmEntries = allEntries
            .Where(e => e.LifecycleState == "ltm" && !e.IsSummaryNode)
            .ToList();

        // Filter out dismissed entries
        List<CognitiveEntry> candidates;
        _lock.EnterReadLock();
        try
        {
            candidates = ltmEntries.Where(e => !_dismissedEntryIds.Contains(e.Id)).ToList();
        }
        finally { _lock.ExitReadLock(); }

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

                // Skip if this exact set of members already has a pending collapse
                if (IsAlreadyPending(memberIds))
                    continue;

                var centroid = ComputeCentroid(cluster);
                var collapseId = $"collapse:{ns}:{Guid.NewGuid():N}";
                var collapse = new PendingCollapse(collapseId, ns, memberIds, centroid);
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
                var clusterId = $"auto:{ns}:{Guid.NewGuid():N}";

                // Check if cluster already exists for these members
                if (HasExistingCluster(clusters, ns, memberIds))
                    continue;

                var summaryText = AutoSummarizer.GenerateSummary(cluster);
                var summaryVector = embedding.Embed(summaryText);

                var createResult = clusters.CreateCluster(clusterId, ns, memberIds, "Auto-summarized");
                if (createResult.StartsWith("Error:")) continue;

                var summaryId = clusters.StoreSummary(clusterId, summaryText, summaryVector);
                if (summaryId.StartsWith("Error:")) continue;

                autoSummaries.Add(new AutoSummaryInfo(clusterId, summaryId, memberIds.Count));
            }
        }

        return new AccretionScanResult(candidates.Count, detectedClusters.Count, newCollapses, autoSummaries);
    }

    /// <summary>Get all pending (non-dismissed) collapses for a namespace.</summary>
    public IReadOnlyList<PendingCollapseInfo> GetPendingCollapses(string ns)
    {
        // Snapshot collapse data under _lock, then resolve entries via _index outside
        List<(string collapseId, string collapseNs, List<string> memberIds, int memberCount, DateTimeOffset detectedAt)> snapshot;

        _lock.EnterReadLock();
        try
        {
            snapshot = _pendingCollapses.Values
                .Where(c => c.Ns == ns && !c.Dismissed)
                .Select(c => (c.CollapseId, c.Ns, c.MemberIds.ToList(), c.MemberIds.Count, c.DetectedAt))
                .ToList();
        }
        finally { _lock.ExitReadLock(); }

        // Resolve entries outside _lock (uses _index's own lock, namespace-scoped to avoid loading all)
        var result = new List<PendingCollapseInfo>();
        foreach (var (collapseId, collapseNs, memberIds, memberCount, detectedAt) in snapshot)
        {
            var previews = new List<CognitiveEntryInfo>();
            foreach (var memberId in memberIds)
            {
                var entry = _index.Get(memberId, collapseNs);
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
        ClusterManager clusters, LifecycleEngine lifecycle)
    {
        PendingCollapse collapse;

        _lock.EnterReadLock();
        try
        {
            if (!_pendingCollapses.TryGetValue(collapseId, out collapse!))
                return $"Error: Collapse '{collapseId}' not found.";
            if (collapse.Dismissed)
                return $"Error: Collapse '{collapseId}' has been dismissed.";
        }
        finally { _lock.ExitReadLock(); }

        // Create cluster
        var clusterId = $"accretion:{collapseId.Replace("collapse:", "")}";
        var createResult = clusters.CreateCluster(clusterId, collapse.Ns, collapse.MemberIds, "Auto-accreted cluster");
        if (createResult.StartsWith("Error:"))
        {
            if (!createResult.Contains("already exists"))
                return createResult;
        }

        // Store summary
        var summaryId = clusters.StoreSummary(clusterId, summaryText, summaryVector);
        if (summaryId.StartsWith("Error:"))
            return summaryId;

        // Capture previous lifecycle states before archiving (for reversible collapse)
        var previousStates = new Dictionary<string, string>();
        foreach (var memberId in collapse.MemberIds)
        {
            var entry = _index.Get(memberId, collapse.Ns);
            if (entry is not null)
                previousStates[memberId] = entry.LifecycleState;
        }

        // Archive all original members
        var archiveErrors = new List<string>();
        foreach (var memberId in collapse.MemberIds)
        {
            var promoteResult = lifecycle.PromoteMemory(memberId, "archived");
            if (promoteResult.StartsWith("Error:"))
                archiveErrors.Add($"{memberId}: {promoteResult}");
        }

        if (archiveErrors.Count > 0)
        {
            return $"Error: Collapse '{collapseId}' partially failed during archive step. Pending collapse preserved for retry. Details: {string.Join(" | ", archiveErrors)}";
        }

        // Only remove the pending collapse after all steps succeed; record the collapse for reversal
        _lock.EnterWriteLock();
        try
        {
            _pendingCollapses.Remove(collapseId);
            _collapseHistory[collapseId] = new CollapseRecord(
                collapseId, clusterId, summaryId, collapse.Ns,
                collapse.MemberIds.ToList(), previousStates);
            ScheduleSaveHistory();
        }
        finally { _lock.ExitWriteLock(); }

        return $"Collapsed {collapse.MemberIds.Count} entries into cluster '{clusterId}' with summary '{summaryId}'.";
    }

    /// <summary>Dismiss a pending collapse and mark its members to skip in future scans.</summary>
    public string DismissCollapse(string collapseId)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_pendingCollapses.TryGetValue(collapseId, out var collapse))
                return $"Error: Collapse '{collapseId}' not found.";

            collapse.Dismissed = true;
            foreach (var memberId in collapse.MemberIds)
                _dismissedEntryIds.Add(memberId);

            _pendingCollapses.Remove(collapseId);
            return $"Dismissed collapse '{collapseId}'. {collapse.MemberIds.Count} entries excluded from future scans.";
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Reverse a previously executed collapse: restore members to pre-collapse state,
    /// delete the summary entry, and remove the cluster.
    /// </summary>
    public string UndoCollapse(
        string collapseId, LifecycleEngine lifecycle, ClusterManager clusters)
    {
        CollapseRecord record;
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureHistoryLoaded();
            if (!_collapseHistory.TryGetValue(collapseId, out record!))
                return $"Error: No collapse record found for '{collapseId}'.";
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        // Restore each member to its pre-collapse lifecycle state
        var restoreErrors = new List<string>();
        foreach (var (memberId, previousState) in record.PreviousStates)
        {
            var result = lifecycle.PromoteMemory(memberId, previousState);
            if (result.StartsWith("Error:"))
                restoreErrors.Add($"{memberId}: {result}");
        }

        if (restoreErrors.Count > 0)
            return $"Error: Uncollapse '{collapseId}' partially failed during restore. Details: {string.Join(" | ", restoreErrors)}";

        // Delete the summary entry
        _index.Delete(record.SummaryEntryId);

        // Remove the cluster (by removing all members then the cluster itself is emptied)
        clusters.UpdateCluster(record.ClusterId, removeIds: record.MemberIds);

        // Remove the collapse record and persist
        _lock.EnterWriteLock();
        try
        {
            _collapseHistory.Remove(collapseId);
            ScheduleSaveHistory();
        }
        finally { _lock.ExitWriteLock(); }

        return $"Reversed collapse '{collapseId}': restored {record.MemberIds.Count} entries, removed summary '{record.SummaryEntryId}'.";
    }

    /// <summary>Get all recorded collapse records for a namespace.</summary>
    public IReadOnlyList<CollapseRecord> GetCollapseHistory(string ns)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureHistoryLoaded();
            return _collapseHistory.Values
                .Where(r => r.Ns == ns)
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

    // Called under write lock.
    private void ScheduleSaveHistory()
    {
        if (_persistence is null) return;
        var snapshot = _collapseHistory.Values.ToList();
        _persistence.ScheduleSaveCollapseHistory(() => snapshot);
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
    private static bool HasExistingCluster(ClusterManager clusters, string ns, List<string> memberIds)
    {
        var existing = clusters.ListClusters(ns);
        foreach (var cluster in existing)
        {
            var detail = clusters.GetCluster(cluster.ClusterId);
            if (detail is null) continue;
            var existingIds = detail.Members.Select(m => m.Id).ToHashSet();
            if (existingIds.SetEquals(memberIds))
                return true;
        }
        return false;
    }

    private bool IsAlreadyPending(List<string> memberIds)
    {
        var set = new HashSet<string>(memberIds);
        foreach (var collapse in _pendingCollapses.Values)
        {
            if (collapse.Dismissed) continue;
            if (collapse.MemberIds.Count == set.Count &&
                collapse.MemberIds.All(id => set.Contains(id)))
                return true;
        }
        return false;
    }
}
