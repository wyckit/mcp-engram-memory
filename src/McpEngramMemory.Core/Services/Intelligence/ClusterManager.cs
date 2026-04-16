using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Core.Services.Intelligence;

/// <summary>
/// Manages semantic clusters: CRUD operations and centroid computation.
///
/// Locking strategy:
/// - Read-only methods use EnterUpgradeableReadLock, upgrading to write only if EnsureLoaded needs to load.
/// - Mutating methods use EnterWriteLock directly.
/// - RecomputeCentroid is done outside the cluster lock to avoid lock-ordering deadlocks
///   with CognitiveIndex (which has its own lock).
/// </summary>
public sealed class ClusterManager
{
    private readonly Dictionary<string, SemanticCluster> _clusters = new();
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly CognitiveIndex _index;
    private readonly IStorageProvider _persistence;
    private bool _loaded;

    public ClusterManager(CognitiveIndex index, IStorageProvider persistence)
    {
        _index = index;
        _persistence = persistence;
    }

    /// <summary>Total number of clusters.</summary>
    public int ClusterCount
    {
        get
        {
            _lock.EnterUpgradeableReadLock();
            try
            {
                EnsureLoaded();
                return _clusters.Count;
            }
            finally { _lock.ExitUpgradeableReadLock(); }
        }
    }

    /// <summary>Create a new cluster with initial members.</summary>
    public string CreateCluster(string clusterId, string ns, IReadOnlyList<string> memberIds, string? label = null)
    {
        List<string> memberIdsCopy;

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            if (_clusters.ContainsKey(clusterId))
                return $"Error: Cluster '{clusterId}' already exists.";

            memberIdsCopy = memberIds.ToList();
            var cluster = new SemanticCluster(clusterId, ns, memberIdsCopy, label);
            _clusters[clusterId] = cluster;
            ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        // Compute centroid outside cluster lock (calls _index.Get which has its own lock)
        var centroid = ComputeCentroidFromMembers(memberIdsCopy);

        _lock.EnterWriteLock();
        try
        {
            if (_clusters.TryGetValue(clusterId, out var c))
                c.Centroid = centroid;
            ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        return $"Created cluster '{clusterId}' with {memberIds.Count} members.";
    }

    /// <summary>Update cluster members and/or label.</summary>
    public string UpdateCluster(string clusterId, IReadOnlyList<string>? addIds = null,
        IReadOnlyList<string>? removeIds = null, string? label = null)
    {
        List<string> memberIdsCopy;
        int memberCount;

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            if (!_clusters.TryGetValue(clusterId, out var cluster))
                return $"Error: Cluster '{clusterId}' not found.";

            if (addIds is not null)
            {
                foreach (var id in addIds)
                    if (!cluster.MemberIds.Contains(id))
                        cluster.MemberIds.Add(id);
            }

            if (removeIds is not null)
            {
                foreach (var id in removeIds)
                    cluster.MemberIds.Remove(id);
            }

            if (label is not null)
                cluster.Label = label;

            memberIdsCopy = cluster.MemberIds.ToList();
            memberCount = cluster.MemberIds.Count;
            ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        // Compute centroid outside cluster lock
        var centroid = ComputeCentroidFromMembers(memberIdsCopy);

        _lock.EnterWriteLock();
        try
        {
            if (_clusters.TryGetValue(clusterId, out var c))
                c.Centroid = centroid;
            ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        return $"Updated cluster '{clusterId}' ({memberCount} members).";
    }

    /// <summary>Store an LLM-generated summary as a searchable entry tied to a cluster.</summary>
    public string StoreSummary(string clusterId, string summaryText, float[] summaryVector)
    {
        // Get cluster info under cluster lock, then do CognitiveIndex upsert outside
        // to avoid lock-ordering deadlock.
        string ns;
        string summaryId;
        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            if (!_clusters.TryGetValue(clusterId, out var cluster))
                return $"Error: Cluster '{clusterId}' not found.";

            summaryId = $"summary:{clusterId}";
            ns = cluster.Ns;
            cluster.SummaryEntryId = summaryId;
            ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        // Upsert the summary entry outside cluster lock
        var entry = new CognitiveEntry(
            summaryId, summaryVector, ns,
            text: summaryText, category: "cluster-summary",
            lifecycleState: "ltm")
        {
            IsSummaryNode = true,
            SourceClusterId = clusterId
        };
        _index.Upsert(entry);

        return summaryId;
    }

    /// <summary>Get cluster details with members and summary.</summary>
    public GetClusterResult? GetCluster(string clusterId)
    {
        // Snapshot cluster info under lock, resolve entries outside
        string? clusterLabel;
        string clusterNs;
        List<string> memberIds;
        string? summaryEntryId;
        int memberCount;

        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            if (!_clusters.TryGetValue(clusterId, out var cluster))
                return null;

            clusterLabel = cluster.Label;
            clusterNs = cluster.Ns;
            memberIds = cluster.MemberIds.ToList();
            summaryEntryId = cluster.SummaryEntryId;
            memberCount = cluster.MemberIds.Count;
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        // Resolve members and summary outside cluster lock
        var members = new List<CognitiveEntryInfo>();
        foreach (var memberId in memberIds)
        {
            var entry = _index.Get(memberId);
            if (entry is not null)
                members.Add(new CognitiveEntryInfo(entry.Id, entry.Text, entry.Ns, entry.Category, entry.LifecycleState));
        }

        CognitiveSearchResult? summaryEntry = null;
        CognitiveEntry? summaryEnt = null;
        if (summaryEntryId is not null)
        {
            summaryEnt = _index.Get(summaryEntryId);
            if (summaryEnt is not null)
                summaryEntry = new CognitiveSearchResult(summaryEnt.Id, summaryEnt.Text, 0f, summaryEnt.LifecycleState,
                    summaryEnt.ActivationEnergy, summaryEnt.Category, summaryEnt.Metadata, summaryEnt.IsSummaryNode, summaryEnt.SourceClusterId);
        }

        // Staleness: summary is stale if cluster membership changed since summary was stored.
        bool isStale = false;
        if (summaryEnt is not null)
        {
            foreach (var memberId in memberIds)
            {
                var member = _index.Get(memberId);
                if (member is not null && member.CreatedAt > summaryEnt.CreatedAt)
                {
                    isStale = true;
                    break;
                }
            }
        }

        return new GetClusterResult(clusterId, clusterLabel, clusterNs,
            memberCount, members, summaryEntry, isStale);
    }

    /// <summary>List all clusters in a namespace.</summary>
    public IReadOnlyList<ClusterSummaryInfo> ListClusters(string ns)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            return _clusters.Values
                .Where(c => c.Ns == ns)
                .Select(c => new ClusterSummaryInfo(
                    c.ClusterId, c.Label, c.MemberIds.Count, c.SummaryEntryId is not null))
                .ToList();
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>Get all cluster IDs that contain a given entry.</summary>
    public IReadOnlyList<string> GetClustersForEntry(string entryId)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            return _clusters.Values
                .Where(c => c.MemberIds.Contains(entryId))
                .Select(c => c.ClusterId)
                .ToList();
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>Remove an entry from all clusters (cascade delete).</summary>
    public void RemoveEntryFromAllClusters(string entryId)
    {
        // Phase 1: Remove member from clusters, collect affected cluster member lists
        var affectedClusters = new List<(string clusterId, List<string> memberIds)>();

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            foreach (var cluster in _clusters.Values)
            {
                if (cluster.MemberIds.Remove(entryId))
                    affectedClusters.Add((cluster.ClusterId, cluster.MemberIds.ToList()));
            }
            if (affectedClusters.Count > 0)
                ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        // Phase 2: Recompute centroids outside cluster lock (calls _index.Get)
        if (affectedClusters.Count == 0) return;

        var centroids = new List<(string clusterId, float[]? centroid)>();
        foreach (var (clusterId, memberIds) in affectedClusters)
            centroids.Add((clusterId, ComputeCentroidFromMembers(memberIds)));

        // Phase 3: Apply centroids under cluster lock
        _lock.EnterWriteLock();
        try
        {
            foreach (var (clusterId, centroid) in centroids)
            {
                if (_clusters.TryGetValue(clusterId, out var c))
                    c.Centroid = centroid;
            }
            ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Transfer cluster memberships from one entry to another (for merge operations). Returns count of clusters affected.</summary>
    public int TransferMembership(string fromId, string toId)
    {
        var affectedClusters = new List<(string clusterId, List<string> memberIds)>();

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            foreach (var cluster in _clusters.Values)
            {
                if (!cluster.MemberIds.Remove(fromId)) continue;

                if (!cluster.MemberIds.Contains(toId))
                    cluster.MemberIds.Add(toId);

                affectedClusters.Add((cluster.ClusterId, cluster.MemberIds.ToList()));
            }
            if (affectedClusters.Count > 0)
                ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        // Recompute centroids outside lock
        if (affectedClusters.Count == 0) return 0;

        var centroids = new List<(string clusterId, float[]? centroid)>();
        foreach (var (clusterId, memberIds) in affectedClusters)
            centroids.Add((clusterId, ComputeCentroidFromMembers(memberIds)));

        _lock.EnterWriteLock();
        try
        {
            foreach (var (clusterId, centroid) in centroids)
            {
                if (_clusters.TryGetValue(clusterId, out var c))
                    c.Centroid = centroid;
            }
            ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        return affectedClusters.Count;
    }

    // Called under upgradeable read lock — upgrades to write only if loading needed.
    private void EnsureLoaded()
    {
        if (_loaded) return;

        _lock.EnterWriteLock();
        try
        {
            if (_loaded) return; // Double-check
            var clusters = _persistence.LoadClusters();
            foreach (var c in clusters)
                _clusters[c.ClusterId] = c;
            _loaded = true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    // Called when already holding write lock.
    private void EnsureLoadedUnderWrite()
    {
        if (_loaded) return;
        var clusters = _persistence.LoadClusters();
        foreach (var c in clusters)
            _clusters[c.ClusterId] = c;
        _loaded = true;
    }

    // Snapshot and schedule cluster save. MUST be called within write lock.
    private void ScheduleSaveClusters()
    {
        var snapshot = _clusters.Values.ToList();
        _persistence.ScheduleSaveClusters(() => snapshot);
    }

    /// <summary>
    /// Compute centroid from member IDs by resolving entries via CognitiveIndex.
    /// Called OUTSIDE the cluster lock to avoid lock-ordering deadlocks.
    /// </summary>
    private float[]? ComputeCentroidFromMembers(List<string> memberIds)
    {
        if (memberIds.Count == 0) return null;

        float[]? centroid = null;
        int count = 0;

        foreach (var memberId in memberIds)
        {
            var entry = _index.Get(memberId);
            if (entry is null) continue;

            if (centroid is null)
            {
                centroid = new float[entry.Vector.Length];
            }
            else if (centroid.Length != entry.Vector.Length)
            {
                continue;
            }

            for (int i = 0; i < centroid.Length; i++)
                centroid[i] += entry.Vector[i];
            count++;
        }

        if (centroid is not null && count > 0)
        {
            for (int i = 0; i < centroid.Length; i++)
                centroid[i] /= count;
        }

        return centroid;
    }
}
