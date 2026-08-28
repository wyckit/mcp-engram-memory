using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Core.Services.Intelligence;

/// <summary>
/// Manages semantic clusters: CRUD operations and centroid computation.
///
/// Tenant isolation: clusters are keyed by <c>(tenant, clusterId)</c> and each
/// <see cref="SemanticCluster"/> carries its own <see cref="SemanticCluster.TenantId"/>, so the
/// same cluster id can exist independently under two tenants and no operation ever crosses tenants.
/// Legacy clusters use tenant <c>""</c>, so single-tenant behavior is unchanged. Member entries are
/// resolved tenant-scoped (fast legacy locator for <c>""</c>, tenant scan otherwise).
///
/// Every public entry point normalizes its incoming tenant before it keys or filters the map. A
/// tenant reaches this class raw from the principal, whereas <see cref="SemanticCluster.TenantId"/>
/// is normalized at construction and <c>EnsureLoaded</c> re-keys the map from that property — so an
/// un-normalized key agrees with the map only until the next reload, and an un-normalized filter
/// never agrees with it at all. Normalizing at the boundary keeps one canonical form of the key.
///
/// Locking strategy:
/// - Read-only methods use EnterUpgradeableReadLock, upgrading to write only if EnsureLoaded needs to load.
/// - Mutating methods use EnterWriteLock directly.
/// - RecomputeCentroid is done outside the cluster lock to avoid lock-ordering deadlocks
///   with CognitiveIndex (which has its own lock).
/// </summary>
public sealed class ClusterManager
{
    private readonly Dictionary<(string Tenant, string Id), SemanticCluster> _clusters = new();
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly CognitiveIndex _index;
    private readonly IStorageProvider _persistence;
    private bool _loaded;

    public ClusterManager(CognitiveIndex index, IStorageProvider persistence)
    {
        _index = index;
        _persistence = persistence;
    }

    /// <summary>Total number of clusters, across all tenants.</summary>
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
    public string CreateCluster(string clusterId, string ns, IReadOnlyList<string> memberIds, string? label = null, string tenantId = "")
    {
        List<string> memberIdsCopy;
        // Key on the normalized tenant: SemanticCluster normalizes its own TenantId, and EnsureLoaded
        // re-keys the map from that property, so a raw key would survive only until the next reload.
        var tenant = Tenancy.Normalize(tenantId);
        var key = (tenant, clusterId);

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            if (_clusters.ContainsKey(key))
                return $"Error: Cluster '{clusterId}' already exists.";

            memberIdsCopy = memberIds.ToList();
            var cluster = new SemanticCluster(clusterId, ns, memberIdsCopy, label, tenant);
            _clusters[key] = cluster;
            ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        // Compute centroid outside cluster lock (calls _index resolution which has its own lock)
        var centroid = ComputeCentroidFromMembers(memberIdsCopy, tenant);

        _lock.EnterWriteLock();
        try
        {
            if (_clusters.TryGetValue(key, out var c))
                c.Centroid = centroid;
            ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        return $"Created cluster '{clusterId}' with {memberIds.Count} members.";
    }

    /// <summary>Update cluster members and/or label.</summary>
    public string UpdateCluster(string clusterId, IReadOnlyList<string>? addIds = null,
        IReadOnlyList<string>? removeIds = null, string? label = null, string tenantId = "")
    {
        List<string> memberIdsCopy;
        int memberCount;
        var tenant = Tenancy.Normalize(tenantId);
        var key = (tenant, clusterId);

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            if (!_clusters.TryGetValue(key, out var cluster))
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
        var centroid = ComputeCentroidFromMembers(memberIdsCopy, tenant);

        _lock.EnterWriteLock();
        try
        {
            if (_clusters.TryGetValue(key, out var c))
                c.Centroid = centroid;
            ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        return $"Updated cluster '{clusterId}' ({memberCount} members).";
    }

    /// <summary>Store an LLM-generated summary as a searchable entry tied to a cluster.</summary>
    public string StoreSummary(string clusterId, string summaryText, float[] summaryVector, string tenantId = "")
    {
        // Get cluster info under cluster lock, then do CognitiveIndex upsert outside
        // to avoid lock-ordering deadlock.
        string ns;
        string summaryId;
        var tenant = Tenancy.Normalize(tenantId);
        var key = (tenant, clusterId);
        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            if (!_clusters.TryGetValue(key, out var cluster))
                return $"Error: Cluster '{clusterId}' not found.";

            summaryId = $"summary:{clusterId}";
            ns = cluster.Ns;
            cluster.SummaryEntryId = summaryId;
            ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        // Upsert the summary entry outside cluster lock, into the cluster's own tenant partition.
        var entry = new CognitiveEntry(
            summaryId, summaryVector, ns,
            text: summaryText, category: "cluster-summary",
            lifecycleState: "ltm", tenantId: tenant)
        {
            IsSummaryNode = true,
            SourceClusterId = clusterId
        };
        _index.Upsert(entry);

        return summaryId;
    }

    /// <summary>Get cluster details with members and summary.</summary>
    public GetClusterResult? GetCluster(string clusterId, string tenantId = "")
    {
        // Snapshot cluster info under lock, resolve entries outside
        string? clusterLabel;
        string clusterNs;
        List<string> memberIds;
        string? summaryEntryId;
        int memberCount;

        // The tenant arrives raw from the principal, while the map is keyed by SemanticCluster's
        // normalized TenantId. Comparing the two forms is the split-brain that makes a tenant's own
        // cluster look absent to it, so normalize before the lookup rather than after.
        var tenant = Tenancy.Normalize(tenantId);

        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            if (!_clusters.TryGetValue((tenant, clusterId), out var cluster))
                return null;

            clusterLabel = cluster.Label;
            clusterNs = cluster.Ns;
            memberIds = cluster.MemberIds.ToList();
            summaryEntryId = cluster.SummaryEntryId;
            memberCount = cluster.MemberIds.Count;
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        // Resolve members and summary outside cluster lock, scoped to the tenant
        var members = new List<CognitiveEntryInfo>();
        foreach (var memberId in memberIds)
        {
            var entry = ResolveEntry(memberId, tenant);
            if (entry is not null)
                members.Add(new CognitiveEntryInfo(entry.Id, entry.Text, entry.Ns, entry.Category, entry.LifecycleState));
        }

        CognitiveSearchResult? summaryEntry = null;
        CognitiveEntry? summaryEnt = null;
        if (summaryEntryId is not null)
        {
            summaryEnt = ResolveEntry(summaryEntryId, tenant);
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
                var member = ResolveEntry(memberId, tenant);
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

    /// <summary>List all clusters in a namespace within a tenant.</summary>
    public IReadOnlyList<ClusterSummaryInfo> ListClusters(string ns, string tenantId = "")
    {
        var tenant = Tenancy.Normalize(tenantId);

        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            return _clusters.Values
                .Where(c => c.Ns == ns && c.TenantId == tenant)
                .Select(c => new ClusterSummaryInfo(
                    c.ClusterId, c.Label, c.MemberIds.Count, c.SummaryEntryId is not null))
                .ToList();
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>
    /// Get all cluster IDs within a tenant that contain a given entry.
    /// Projection of <see cref="GetClusterMembershipsForEntry"/> so the membership predicate exists
    /// exactly once and the two views can never disagree about which clusters contain the entry.
    /// </summary>
    public IReadOnlyList<string> GetClustersForEntry(string entryId, string tenantId = "")
        => GetClusterMembershipsForEntry(entryId, tenantId).Select(m => m.ClusterId).ToList();

    /// <summary>
    /// Get all clusters within a tenant that contain a given entry, each paired with its own
    /// namespace. Clusters in one tenant are not all in one namespace, so a caller that has to
    /// authorize what it returns cannot do so from the cluster id alone; emitting the namespace the
    /// lookup already held is what lets it filter without re-resolving every cluster.
    /// <paramref name="tenantId"/> deliberately carries no default: a tenant-scoped lookup that
    /// silently falls back to the legacy <c>""</c> partition would read across tenants, so a
    /// forgotten argument must be a compile error rather than a cross-tenant read.
    /// </summary>
    public IReadOnlyList<ClusterMembershipInfo> GetClusterMembershipsForEntry(string entryId, string tenantId)
    {
        var tenant = Tenancy.Normalize(tenantId);

        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            return _clusters.Values
                .Where(c => c.TenantId == tenant && c.MemberIds.Contains(entryId))
                .Select(c => new ClusterMembershipInfo(c.ClusterId, c.Ns))
                .ToList();
        }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>Remove an entry from all clusters within a tenant (cascade delete).</summary>
    public void RemoveEntryFromAllClusters(string entryId, string tenantId = "")
    {
        // Phase 1: Remove member from this tenant's clusters, collect affected member lists
        var affectedClusters = new List<(string clusterId, List<string> memberIds)>();
        var tenant = Tenancy.Normalize(tenantId);

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            foreach (var cluster in _clusters.Values)
            {
                if (cluster.TenantId != tenant) continue;
                if (cluster.MemberIds.Remove(entryId))
                    affectedClusters.Add((cluster.ClusterId, cluster.MemberIds.ToList()));
            }
            if (affectedClusters.Count > 0)
                ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        // Phase 2: Recompute centroids outside cluster lock
        if (affectedClusters.Count == 0) return;

        var centroids = new List<(string clusterId, float[]? centroid)>();
        foreach (var (clusterId, memberIds) in affectedClusters)
            centroids.Add((clusterId, ComputeCentroidFromMembers(memberIds, tenant)));

        // Phase 3: Apply centroids under cluster lock
        _lock.EnterWriteLock();
        try
        {
            foreach (var (clusterId, centroid) in centroids)
            {
                if (_clusters.TryGetValue((tenant, clusterId), out var c))
                    c.Centroid = centroid;
            }
            ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Transfer cluster memberships from one entry to another within a tenant (for merge). Returns clusters affected.</summary>
    public int TransferMembership(string fromId, string toId, string tenantId = "")
    {
        var affectedClusters = new List<(string clusterId, List<string> memberIds)>();
        var tenant = Tenancy.Normalize(tenantId);

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            foreach (var cluster in _clusters.Values)
            {
                if (cluster.TenantId != tenant) continue;
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
            centroids.Add((clusterId, ComputeCentroidFromMembers(memberIds, tenant)));

        _lock.EnterWriteLock();
        try
        {
            foreach (var (clusterId, centroid) in centroids)
            {
                if (_clusters.TryGetValue((tenant, clusterId), out var c))
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
                _clusters[(c.TenantId, c.ClusterId)] = c;
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
            _clusters[(c.TenantId, c.ClusterId)] = c;
        _loaded = true;
    }

    // Snapshot and schedule cluster save (all tenants, one blob). MUST be called within write lock.
    private void ScheduleSaveClusters()
    {
        var snapshot = _clusters.Values.ToList();
        _persistence.ScheduleSaveClusters(() => snapshot);
    }

    /// <summary>
    /// Resolve an entry id within a tenant: fast legacy locator for tenant "", tenant-scoped scan
    /// otherwise. Callers pass an already-normalized tenant, so the empty-string test really does
    /// select the legacy partition rather than misreading a whitespace-only tenant as a real one.
    /// </summary>
    private CognitiveEntry? ResolveEntry(string id, string tenantId)
        => tenantId.Length == 0 ? _index.Get(id) : _index.GetForTenant(id, tenantId);

    /// <summary>
    /// Compute centroid from member IDs by resolving entries via CognitiveIndex (tenant-scoped).
    /// Called OUTSIDE the cluster lock to avoid lock-ordering deadlocks.
    /// </summary>
    private float[]? ComputeCentroidFromMembers(List<string> memberIds, string tenantId)
    {
        if (memberIds.Count == 0) return null;

        float[]? centroid = null;
        int count = 0;

        foreach (var memberId in memberIds)
        {
            var entry = ResolveEntry(memberId, tenantId);
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
