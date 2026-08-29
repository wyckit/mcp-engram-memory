using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Graph;
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
/// No member defaults its <c>tenantId</c>: <c>""</c> is the legacy partition — a real, populated
/// dataset, not a sentinel — so a forgotten tenant argument must be a compile error rather than a
/// silent cross-tenant read. Callers that mean the legacy partition say so with an explicit
/// <c>tenantId: ""</c>.
///
/// Membership is keyed by (tenant, BARE id) with no namespace, so an id the tenant holds in two
/// namespaces names ONE membership bucket shared by two entries. Every member set here is
/// therefore screened through <see cref="TopologyGuard"/> before an id enters, moves, is resolved,
/// is counted, or is folded into a centroid. The guard is ACL-blind and so needs no principal,
/// which is what lets it sit here at the writer instead of at each tool that reaches it.
///
/// Screening the WRITES was never enough, and that is the correction this class carries. A member
/// admitted while its id was still unique becomes ambiguous the moment a twin appears, and from
/// then on every projection of that member set is a disclosure: <see cref="GetCluster"/> resolves
/// the bare id into whichever twin the caller can read and presents it as a member of THIS
/// cluster, <see cref="ListClusters"/> tallies it, and the centroid folds its vector into the
/// ranking. So the read side screens too, before resolution rather than after it.
///
/// <see cref="GetClustersForEntry"/> and <see cref="GetClusterMembershipsForEntry"/> are
/// deliberately unscreened, and they are the one place where the argument really is the whole
/// story: they answer "which clusters hold THIS id", disclose no member but the one the caller
/// named, and are the ground truth a caller (and a test) uses to observe that a projection
/// suppressed something. Their argument is gated where a principal supplies it.
///
/// Locking strategy:
/// - Read-only methods use EnterUpgradeableReadLock, upgrading to write only if EnsureLoaded needs to load.
/// - Mutating methods use EnterWriteLock directly.
/// - RecomputeCentroid is done outside the cluster lock to avoid lock-ordering deadlocks
///   with CognitiveIndex (which has its own lock). The topology screen resolves through
///   CognitiveIndex for the same reason and so runs BEFORE the cluster lock is taken, never inside
///   it — which is why each mutator screens its whole id list up front rather than per member.
///
/// A CLUSTER IN <c>_clusters</c> IS FROZEN. Every mutator publishes a REPLACEMENT
/// <see cref="SemanticCluster"/> under the write lock — see <see cref="Replace"/> — and no code
/// here edits an object that is already in the map, its <see cref="SemanticCluster.MemberIds"/>
/// list included. That invariant is not a style preference; it is what makes
/// <see cref="ScheduleSaveClusters"/> correct.
///
/// The persistence layer is the one reader that runs AFTER the lock is released. It captures the
/// provider under the lock and invokes it later from a debounce timer on a thread-pool thread that
/// never takes this class's lock, then hands the result to <c>JsonSerializer.Serialize</c>. Every
/// other reader here copies under the lock (<see cref="GetCluster"/>, <see cref="ListClusters"/>)
/// and is done before it returns, so the shallow <c>Values.ToList()</c> that snapshot used to be
/// enough for was never enough for this one: <c>ToList</c> copies the list of REFERENCES, so a
/// concurrent <c>MemberIds.Add/Remove</c> bumped <c>List&lt;T&gt;._version</c> under a serializer
/// already walking that list. The serializer throws "Collection was modified", the write is caught
/// and logged, and — because the pending provider was cleared before the callback ran — nothing
/// reschedules it. Freezing on publish costs one small object per mutation and nothing per save,
/// and it leaves every critical section exactly the length it already was.
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

    /// <summary>
    /// Create a new cluster with initial members. Members whose id names more than one of the
    /// tenant's namespaces are dropped rather than failing the whole creation: an auto-clustering
    /// sweep must still form its cluster out of the members it CAN attribute, and refusing the
    /// cluster outright would let one ambiguous id delete the feature for a whole namespace.
    /// </summary>
    public string CreateCluster(string clusterId, string ns, IReadOnlyList<string> memberIds, string? label, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(memberIds);

        // Key on the normalized tenant: SemanticCluster normalizes its own TenantId, and EnsureLoaded
        // re-keys the map from that property, so a raw key would survive only until the next reload.
        var tenant = Tenancy.Normalize(tenantId);
        var key = (tenant, clusterId);

        // Screened before the lock, like the centroid below and for the same reason: the guard
        // resolves through CognitiveIndex, which has its own lock. One sweep for the whole member
        // list — a per-member test re-lists (and so reloads) the store once per member.
        var guard = TopologyGuard.ForSweep(_index, tenant);
        var memberIdsCopy = new List<string>(memberIds.Count);
        foreach (var memberId in memberIds)
            if (guard.IsTopologySafe(memberId))
                memberIdsCopy.Add(memberId);

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            if (_clusters.ContainsKey(key))
                return $"Error: Cluster '{clusterId}' already exists.";

            // memberIdsCopy becomes the new cluster's frozen member list AND stays reachable from
            // this method for the centroid pass below. That aliasing is safe for exactly one
            // reason: nothing ever mutates a published member list — see the class remarks — so the
            // read outside the lock and the serializer's read on a pool thread see the same fixed
            // content. Anything that needs a DIFFERENT member set builds a new list.
            _clusters[key] = new SemanticCluster(clusterId, ns, memberIdsCopy, label, tenant);
            ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        // Compute centroid outside cluster lock (calls _index resolution which has its own lock)
        var centroid = ComputeCentroidFromMembers(memberIdsCopy, tenant, guard);

        _lock.EnterWriteLock();
        try
        {
            if (_clusters.TryGetValue(key, out var c))
                _clusters[key] = Replace(c, c.MemberIds, c.Label, centroid, c.SummaryEntryId);
            ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        // The count of what was actually stored, not of what was asked for. A cluster that reports
        // three members and holds two is a lie, and the difference is the same one bit the guard
        // already costs elsewhere.
        return $"Created cluster '{clusterId}' with {memberIdsCopy.Count} members.";
    }

    /// <summary>
    /// Update cluster members and/or label. Both directions are screened: adding an ambiguous id
    /// would enrol a twin the caller cannot see, and removing one would evict that twin's
    /// membership. The reported member count is the real one after screening.
    /// </summary>
    public string UpdateCluster(string clusterId, IReadOnlyList<string>? addIds,
        IReadOnlyList<string>? removeIds, string? label, string tenantId)
    {
        List<string> memberIdsCopy;
        var tenant = Tenancy.Normalize(tenantId);
        var key = (tenant, clusterId);

        // Screened before the lock — the guard resolves through CognitiveIndex, which has its own
        // lock, and this class never holds the cluster lock across index work. One sweep covers
        // both lists AND the centroid below; it is no longer built conditionally, because even a
        // label-only update recomputes the centroid over the existing member set, and members that
        // were unique when they joined may not be any more.
        var guard = TopologyGuard.ForSweep(_index, tenant);
        List<string>? admittedAdds = addIds?.Where(guard.IsTopologySafe).ToList();
        List<string>? admittedRemoves = removeIds?.Where(guard.IsTopologySafe).ToList();

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            if (!_clusters.TryGetValue(key, out var cluster))
                return $"Error: Cluster '{clusterId}' not found.";

            // Edited on a copy and published by replacement, never in place. This is the same one
            // allocation the old `cluster.MemberIds.ToList()` at the end of this block already
            // made — it has moved to the front and become the stored list instead of a throwaway,
            // so the critical section is neither longer nor more allocating than it was.
            memberIdsCopy = new List<string>(cluster.MemberIds);

            if (admittedAdds is not null)
            {
                foreach (var id in admittedAdds)
                    if (!memberIdsCopy.Contains(id))
                        memberIdsCopy.Add(id);
            }

            if (admittedRemoves is not null)
            {
                foreach (var id in admittedRemoves)
                    memberIdsCopy.Remove(id);
            }

            _clusters[key] = Replace(
                cluster, memberIdsCopy, label ?? cluster.Label, cluster.Centroid, cluster.SummaryEntryId);
            ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        // Counted outside the lock, because counting consults the guard and the guard resolves
        // through CognitiveIndex. The figure is the attributable one, matching what GetCluster
        // reports: a reply that said 3 while get_cluster showed 2 would be the "a twin exists"
        // oracle re-opened as an arithmetic difference.
        var memberCount = memberIdsCopy.Count(guard.IsTopologySafe);

        // Compute centroid outside cluster lock
        var centroid = ComputeCentroidFromMembers(memberIdsCopy, tenant, guard);

        _lock.EnterWriteLock();
        try
        {
            if (_clusters.TryGetValue(key, out var c))
                _clusters[key] = Replace(c, c.MemberIds, c.Label, centroid, c.SummaryEntryId);
            ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        return $"Updated cluster '{clusterId}' ({memberCount} members).";
    }

    /// <summary>Store an LLM-generated summary as a searchable entry tied to a cluster.</summary>
    public string StoreSummary(string clusterId, string summaryText, float[] summaryVector, string tenantId)
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
            // Published by replacement like every other edit here. A reference assignment cannot
            // tear, so this one never crashed the serializer the way a member-list edit did — but a
            // snapshot captured a moment earlier would still have acquired a summary id it was
            // never meant to carry, which is a half-applied edit written as though it were whole.
            _clusters[key] = Replace(
                cluster, cluster.MemberIds, cluster.Label, cluster.Centroid, summaryId);
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

    /// <summary>
    /// Get cluster details with members and summary.
    ///
    /// A member whose id is not attributable is withheld — from the member list, from
    /// <c>MemberCount</c>, from the staleness comparison and from the summary slot. This is the
    /// disclosure the reviewer found: membership is stored as a BARE id, so resolving one that the
    /// tenant holds twice returns whichever twin the caller happens to be able to read, and the
    /// reply then states that this caller's own private entry is a member of somebody else's
    /// cluster. Withholding before resolution is what stops the shared bucket from acquiring a
    /// face; withholding after would already have given it one.
    ///
    /// <c>MemberCount</c> follows the same screen rather than reporting the raw list length,
    /// because a count that disagreed with the members beside it would be the "a twin exists
    /// somewhere in this tenant" bit restated as arithmetic.
    /// </summary>
    public GetClusterResult? GetCluster(string clusterId, string tenantId)
    {
        // Snapshot cluster info under lock, resolve entries outside
        string? clusterLabel;
        string clusterNs;
        List<string> memberIds;
        string? summaryEntryId;

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
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        // One sweep for the whole projection, built after the cluster lock is released: the guard
        // resolves through CognitiveIndex, and a per-member test would re-list the tenant's
        // namespaces once per member.
        var guard = TopologyGuard.ForSweep(_index, tenant);

        // Resolve members and summary outside cluster lock, scoped to the tenant. The screen runs
        // BEFORE the resolve, so an unattributable id never becomes an entry at all.
        var resolvedMembers = new List<CognitiveEntry>();
        int memberCount = 0;
        foreach (var memberId in memberIds)
        {
            if (!guard.IsTopologySafe(memberId)) continue;
            memberCount++;

            var entry = ResolveEntry(memberId, tenant);
            if (entry is not null)
                resolvedMembers.Add(entry);
        }

        var members = resolvedMembers
            .Select(e => new CognitiveEntryInfo(e.Id, e.Text, e.Ns, e.Category, e.LifecycleState))
            .ToList();

        CognitiveSearchResult? summaryEntry = null;
        CognitiveEntry? summaryEnt = null;
        // The summary node is reached by the bare id "summary:{clusterId}" like any other member,
        // so it gets the same screen: a tenant that holds that id twice would otherwise have the
        // wrong copy's TEXT served as this cluster's summary.
        if (summaryEntryId is not null && guard.IsTopologySafe(summaryEntryId))
        {
            summaryEnt = ResolveEntry(summaryEntryId, tenant);
            if (summaryEnt is not null)
                summaryEntry = new CognitiveSearchResult(summaryEnt.Id, summaryEnt.Text, 0f, summaryEnt.LifecycleState,
                    summaryEnt.ActivationEnergy, summaryEnt.Category, summaryEnt.Metadata, summaryEnt.IsSummaryNode, summaryEnt.SourceClusterId);
        }

        // Staleness: summary is stale if cluster membership changed since summary was stored.
        // Compared over the members already resolved above — re-resolving would re-admit exactly
        // the ids the screen just excluded, and a CreatedAt comparison against a withheld twin is
        // still that twin answering a question about this cluster.
        bool isStale = false;
        if (summaryEnt is not null)
        {
            var summaryCreatedAt = summaryEnt.CreatedAt;
            isStale = resolvedMembers.Any(member => member.CreatedAt > summaryCreatedAt);
        }

        return new GetClusterResult(clusterId, clusterLabel, clusterNs,
            memberCount, members, summaryEntry, isStale);
    }

    /// <summary>
    /// List all clusters in a namespace within a tenant.
    ///
    /// <c>MemberCount</c> counts attributable members only, for the same reason
    /// <see cref="GetCluster"/> does: this listing is the cheap way to ask a cluster how big it is,
    /// and a count that included a member get_cluster then refuses to show would let a caller read
    /// the suppression off the difference.
    /// </summary>
    public IReadOnlyList<ClusterSummaryInfo> ListClusters(string ns, string tenantId)
    {
        var tenant = Tenancy.Normalize(tenantId);

        // Snapshot under the lock, count outside it: counting consults the guard, the guard
        // resolves through CognitiveIndex, and this class never holds the cluster lock across
        // index work.
        List<(string ClusterId, string? Label, List<string> MemberIds, bool HasSummary)> snapshot;
        _lock.EnterUpgradeableReadLock();
        try
        {
            EnsureLoaded();
            snapshot = _clusters.Values
                .Where(c => c.Ns == ns && c.TenantId == tenant)
                .Select(c => (
                    ClusterId: c.ClusterId,
                    Label: c.Label,
                    MemberIds: c.MemberIds.ToList(),
                    HasSummary: c.SummaryEntryId is not null))
                .ToList();
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        if (snapshot.Count == 0)
            return Array.Empty<ClusterSummaryInfo>();

        // One sweep for every cluster in the listing — the same id recurs across clusters, and the
        // memo answers each repeat for free.
        var guard = TopologyGuard.ForSweep(_index, tenant);
        return snapshot
            .Select(c => new ClusterSummaryInfo(
                c.ClusterId, c.Label, c.MemberIds.Count(guard.IsTopologySafe), c.HasSummary))
            .ToList();
    }

    /// <summary>
    /// Get all cluster IDs within a tenant that contain a given entry.
    /// Projection of <see cref="GetClusterMembershipsForEntry"/> so the membership predicate exists
    /// exactly once and the two views can never disagree about which clusters contain the entry.
    /// </summary>
    public IReadOnlyList<string> GetClustersForEntry(string entryId, string tenantId)
        => GetClusterMembershipsForEntry(entryId, tenantId: tenantId).Select(m => m.ClusterId).ToList();

    /// <summary>
    /// Get all clusters within a tenant that contain a given entry, each paired with its own
    /// namespace. Clusters in one tenant are not all in one namespace, so a caller that has to
    /// authorize what it returns cannot do so from the cluster id alone; emitting the namespace the
    /// lookup already held is what lets it filter without re-resolving every cluster.
    ///
    /// Unscreened, deliberately, and this is the one place where testing the argument really is the
    /// whole test: the only bare id involved is <paramref name="entryId"/>, which the caller
    /// supplied and already knows, and no member but that one is disclosed. It resolves nothing, so
    /// there is no bare id here for a twin to answer. A principal-facing caller gates
    /// <paramref name="entryId"/> before asking; what comes back is the stored membership, which is
    /// also what lets a caller (and a test) observe that a projection suppressed something.
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

    /// <summary>
    /// Remove an entry from all clusters within a tenant (cascade delete).
    ///
    /// It used to be unscreened, on the argument that its sanctioned caller
    /// (<see cref="Graph.TopologyCascade"/>) already tested the id being swept. It is screened here
    /// now for the same reason as its graph twin: the predicate belongs at the writer, so a caller
    /// reaching the primitive directly cannot skip it, and the centroid this recomputes reads the
    /// OTHER members of every affected cluster — nodes no argument names.
    ///
    /// Call the <c>guard</c> overload from a sweep over many entries: one namespace listing and one
    /// memo for the whole purge instead of one per entry.
    /// </summary>
    public void RemoveEntryFromAllClusters(string entryId, string tenantId)
        => RemoveEntryFromAllClusters(entryId, tenantId,
            TopologyGuard.ForSweep(_index, Tenancy.Normalize(tenantId)));

    /// <inheritdoc cref="RemoveEntryFromAllClusters(string, string)"/>
    public void RemoveEntryFromAllClusters(string entryId, string tenantId, TopologyGuard.Sweep guard)
    {
        ArgumentNullException.ThrowIfNull(guard);

        // Phase 1: Remove member from this tenant's clusters, collect the replacements
        var affectedClusters = new List<(string ClusterId, SemanticCluster Updated)>();
        var tenant = Tenancy.Normalize(tenantId);

        // Evicting an ambiguous id evicts the invisible twin's membership along with it, so the
        // screen runs before the lock like every other mutator here.
        if (!guard.IsTopologySafe(entryId))
            return;

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            foreach (var cluster in _clusters.Values)
            {
                if (cluster.TenantId != tenant) continue;
                // Tested before copying, so the copy is paid for only by clusters that actually
                // hold the entry — the old in-place Remove()'s bool did the same job.
                if (!cluster.MemberIds.Contains(entryId)) continue;

                var remaining = new List<string>(cluster.MemberIds);
                remaining.Remove(entryId);
                affectedClusters.Add((cluster.ClusterId,
                    Replace(cluster, remaining, cluster.Label, cluster.Centroid, cluster.SummaryEntryId)));
            }

            // Published after the walk rather than inside it. Overwriting an existing key is legal
            // during a Dictionary enumeration on modern runtimes, but this class is multi-targeted
            // and the guarantee is not worth depending on for a loop that already has the list.
            foreach (var (clusterId, updated) in affectedClusters)
                _clusters[(tenant, clusterId)] = updated;

            if (affectedClusters.Count > 0)
                ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        // Phase 2: Recompute centroids outside cluster lock
        if (affectedClusters.Count == 0) return;

        var centroids = new List<(string clusterId, float[]? centroid)>();
        foreach (var (clusterId, updated) in affectedClusters)
            centroids.Add((clusterId, ComputeCentroidFromMembers(updated.MemberIds, tenant, guard)));

        // Phase 3: Apply centroids under cluster lock
        _lock.EnterWriteLock();
        try
        {
            foreach (var (clusterId, centroid) in centroids)
            {
                if (_clusters.TryGetValue((tenant, clusterId), out var c))
                    _clusters[(tenant, clusterId)] = Replace(c, c.MemberIds, c.Label, centroid, c.SummaryEntryId);
            }
            ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Transfer cluster memberships from one entry to another within a tenant (for merge).
    /// Returns clusters affected.
    ///
    /// Either endpoint being ambiguous refuses the transfer outright rather than moving what it
    /// can: this walks every cluster in the tenant and rewires membership by bare id, so a caller
    /// with a writable twin could otherwise re-home another principal's entry between clusters.
    /// Returning 0 is what a merge of two unclustered entries already reports.
    ///
    /// Unlike <see cref="Graph.KnowledgeGraph.TransferEdges"/>, the two arguments really are the
    /// only nodes rewritten — a cluster's other members keep their membership untouched — so there
    /// is no third id for the argument test to miss. The one place this reaches beyond them is the
    /// centroid recomputation, which reads every member of every affected cluster; that is screened
    /// inside <see cref="ComputeCentroidFromMembers"/> against this same sweep.
    /// </summary>
    public int TransferMembership(string fromId, string toId, string tenantId)
    {
        var affectedClusters = new List<(string ClusterId, SemanticCluster Updated)>();
        var tenant = Tenancy.Normalize(tenantId);

        // Before the lock, as everywhere else here: the guard resolves through CognitiveIndex.
        var guard = TopologyGuard.ForSweep(_index, tenant);
        if (!guard.IsTopologySafe(fromId) || !guard.IsTopologySafe(toId))
            return 0;

        _lock.EnterWriteLock();
        try
        {
            EnsureLoadedUnderWrite();
            foreach (var cluster in _clusters.Values)
            {
                if (cluster.TenantId != tenant) continue;
                // Tested before copying, so only clusters that really hold fromId pay for a list —
                // the old in-place Remove()'s bool decided the same thing.
                if (!cluster.MemberIds.Contains(fromId)) continue;

                var members = new List<string>(cluster.MemberIds);
                members.Remove(fromId);
                // Checked AFTER the removal, as before: a cluster holding both endpoints collapses
                // to one membership rather than keeping a stale duplicate.
                if (!members.Contains(toId))
                    members.Add(toId);

                affectedClusters.Add((cluster.ClusterId,
                    Replace(cluster, members, cluster.Label, cluster.Centroid, cluster.SummaryEntryId)));
            }

            // Published after the walk — see RemoveEntryFromAllClusters for why not inside it.
            foreach (var (clusterId, updated) in affectedClusters)
                _clusters[(tenant, clusterId)] = updated;

            if (affectedClusters.Count > 0)
                ScheduleSaveClusters();
        }
        finally { _lock.ExitWriteLock(); }

        // Recompute centroids outside lock
        if (affectedClusters.Count == 0) return 0;

        var centroids = new List<(string clusterId, float[]? centroid)>();
        foreach (var (clusterId, updated) in affectedClusters)
            centroids.Add((clusterId, ComputeCentroidFromMembers(updated.MemberIds, tenant, guard)));

        _lock.EnterWriteLock();
        try
        {
            foreach (var (clusterId, centroid) in centroids)
            {
                if (_clusters.TryGetValue((tenant, clusterId), out var c))
                    _clusters[(tenant, clusterId)] = Replace(c, c.MemberIds, c.Label, centroid, c.SummaryEntryId);
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

    /// <summary>
    /// One frozen cluster carrying one edit — the only way anything in <c>_clusters</c> changes.
    ///
    /// Identity (<see cref="SemanticCluster.ClusterId"/>, <see cref="SemanticCluster.Ns"/>,
    /// <see cref="SemanticCluster.TenantId"/>) is carried over unchanged, so a replacement is
    /// always the same cluster in the same partition under the same key; only the mutable state is
    /// restated. The tenant is already normalized on the source object and the constructor
    /// normalizes again, which is idempotent.
    ///
    /// <paramref name="memberIds"/> may be the SOURCE cluster's own list when the edit does not
    /// touch membership. Sharing it is free and safe under the freeze invariant — no published list
    /// is ever mutated, so a list two frozen clusters both point at cannot change under either of
    /// them. A caller changing membership passes a list it built itself.
    /// </summary>
    private static SemanticCluster Replace(
        SemanticCluster source, List<string> memberIds, string? label,
        float[]? centroid, string? summaryEntryId)
        => new(source.ClusterId, label, source.Ns, memberIds, centroid, summaryEntryId, source.TenantId);

    /// <summary>
    /// Snapshot and schedule a cluster save (all tenants, one blob). MUST be called within the
    /// write lock.
    ///
    /// The shallow <c>Values.ToList()</c> IS the deep copy here, and only because of the freeze
    /// invariant in the class remarks: it copies references, and every referenced cluster — its
    /// <see cref="SemanticCluster.MemberIds"/> list included — is immutable from the moment it
    /// entered the map. So the provider this hands to persistence can be invoked minutes later,
    /// from a debounce timer on a thread that holds none of this class's locks, and serialize a
    /// state that is internally consistent and cannot move underneath the serializer.
    ///
    /// If a future edit reintroduces in-place mutation of a stored cluster, THIS is the line that
    /// silently becomes wrong — not with a compile error but with an intermittent "Collection was
    /// modified" inside <c>JsonSerializer.Serialize</c> on a pool thread, caught and logged by the
    /// storage provider, after which nothing reschedules the write.
    /// </summary>
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
    ///
    /// Unattributable members contribute nothing. The centroid is what this cluster matches
    /// against at search time, so folding in a vector reached by an ambiguous bare id moves the
    /// cluster's ranking under content that belongs to another principal — a quieter version of
    /// serving that principal's entry outright, and reached through a member the caller never
    /// named. <paramref name="guard"/> is the caller's sweep, so one namespace listing covers the
    /// screen the mutator already ran and this recomputation together.
    /// </summary>
    private float[]? ComputeCentroidFromMembers(
        List<string> memberIds, string tenantId, TopologyGuard.Sweep guard)
    {
        if (memberIds.Count == 0) return null;

        float[]? centroid = null;
        int count = 0;

        foreach (var memberId in memberIds)
        {
            if (!guard.IsTopologySafe(memberId)) continue;

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
