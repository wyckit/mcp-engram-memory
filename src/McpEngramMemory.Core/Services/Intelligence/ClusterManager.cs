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
/// Locking strategy, outermost first — the ORDER IS THE DEADLOCK ARGUMENT, and it is the same order
/// <see cref="Graph.KnowledgeGraph"/> obeys, against the same fence:
///
///   attribution fence (shared)  ->  this class's _lock  ->  [mutate]  ->  release _lock
///                                                                      ->  release fence
///                                                                      ->  schedule the save
///
/// - Read-only methods use EnterUpgradeableReadLock, upgrading to write only if EnsureLoaded needs to load.
/// - Mutating methods use EnterWriteLock directly, inside the fence.
/// - RecomputeCentroid is done outside the cluster lock AND outside the fence, to avoid
///   lock-ordering deadlocks with CognitiveIndex (which has its own locks, and whose lazy load asks
///   for the fence's exclusive side). The topology screen resolves through CognitiveIndex for the
///   same reason and so runs BEFORE the fence and the cluster lock are taken, never inside them —
///   which is why each mutator screens its whole id list up front rather than per member.
/// - SCREENING BEFORE THE LOCK IS AN ADMISSION, NOT A GUARANTEE, and that is what the fence fixes.
///   A member admitted while its id was still unique can become ambiguous before the member list is
///   published, because planting a same-id twin is an ordinary entry write that takes none of this
///   class's locks. Every mutator here therefore holds the fence's SHARED side across
///   <see cref="AttributionMovedSince"/> and the publish, while the ambiguity-changing index write
///   takes the EXCLUSIVE side — so the compare cannot go stale between being made and being acted
///   on. All four membership mutators do it: <see cref="CreateCluster"/>,
///   <see cref="UpdateCluster"/>, <see cref="RemoveEntryFromAllClusters(string, string,
///   TopologyGuard.Sweep)"/> and <see cref="TransferMembership"/>, plus the centroid-apply pass each
///   of them runs afterwards, whose value is derived from those same admitted ids.
///   <see cref="StoreSummary"/> is deliberately NOT fenced and is the one member-adjacent write that
///   needs no fence: it admits no id through a sweep (its only id is the derived
///   <c>summary:{clusterId}</c>, screened at every read), and it UPSERTS an entry afterwards — a
///   write that may itself take the fence's exclusive side, which a fence holder could not do.
/// - THE FENCE IS RELEASED THROUGH THE INSTANCE THAT WAS ENTERED.
///   <see cref="CognitiveIndex.EnterAttributionFence"/> returns it and
///   <see cref="CognitiveIndex.ExitAttributionFence"/> takes it, so the release cannot resolve a
///   different lock from the one the acquire took — which it could when both re-derived the fence
///   from the tenant id and teardown had unpublished it in between.
/// - THE FENCED SECTION IS BOUNDED BY THE EDIT, NOT BY THE STORE. The fence's exclusive side is
///   taken by a crossing that is already holding a partition write lock, so a long shared hold
///   queues later readers of that partition behind it. The two cascade mutators therefore build
///   their replacements OUTSIDE the fence, under the read lock, and publish under it — see
///   <see cref="CollectEvictionsUnderLock"/> and <c>_clusterVersion</c>, which is what makes the
///   precomputed list safe to publish. Nothing inside the fence walks every cluster of the tenant
///   in the common case, and nothing inside it allocates per cluster that is not being changed.
/// - NOTHING REACHABLE INSIDE THE FENCE MAY TOUCH <see cref="CognitiveIndex"/> except
///   <see cref="CognitiveIndex.AttributionRevisionFor"/>, which takes no lock. That is why
///   <see cref="EnsureLoadedOutsideFence"/> exists: <c>LoadClusters</c> runs arbitrary
///   storage-provider code, and it runs before the fence is taken, never under it. EVERY mutator
///   calls it, <see cref="StoreSummary"/> included even though that one takes no fence.
/// - WHAT THAT DOES NOT COVER, stated rather than assumed, exactly as
///   <c>KnowledgeGraph.EnsureLoadedOutsideFence</c> states it for its own path: a READER can still
///   be the thread that triggers the one-shot load. <see cref="EnsureLoaded"/> runs
///   <c>LoadClusters</c> under the cluster WRITE lock, so a provider that wrote entries back into
///   <see cref="CognitiveIndex"/> from inside the load would block on the fence's exclusive side
///   while holding the structural lock every fence holder waits for — the reverse of the acquisition
///   order this class's rule depends on, and a genuine deadlock rather than a stall. Warming before
///   the fence removes it for the mutators; it cannot remove it for readers, and no fence discipline
///   can. A storage provider must not call back into <see cref="CognitiveIndex"/> while it is being
///   asked to load. Shipped providers do not; the rule is stated so a new one is checked against it.
///
/// A CLUSTER IN <c>_clusters</c> IS FROZEN. Every mutator publishes a REPLACEMENT
/// <see cref="SemanticCluster"/> under the write lock — see <see cref="Replace"/> — and no code
/// here edits an object that is already in the map, its <see cref="SemanticCluster.MemberIds"/>
/// list included. That invariant is not a style preference; it is what makes
/// <see cref="ScheduleSaveClusters"/> correct.
///
/// The persistence layer is the one reader that runs AFTER the lock is released. It is handed the
/// METHOD GROUP <see cref="SnapshotClustersForSave"/> — not a pre-captured list — and invokes it
/// later from a debounce timer on a thread-pool thread that takes this class's READ lock for the
/// duration of the copy, then hands the result to <c>JsonSerializer.Serialize</c>. Deferring the
/// snapshot is what makes debounced saving correct: registration is last-write-wins over a
/// full-replace blob, so an eagerly captured snapshot could be registered AFTER a newer one and
/// overwrite it, losing a whole cluster with no error anywhere and no symptom until a restart.
/// Every other reader here copies under the lock (<see cref="GetCluster"/>,
/// <see cref="ListClusters"/>) and is done before it returns, so the shallow <c>Values.ToList()</c>
/// that snapshot used to be enough for was never enough for this one: <c>ToList</c> copies the list
/// of REFERENCES, so a concurrent <c>MemberIds.Add/Remove</c> bumped <c>List&lt;T&gt;._version</c>
/// under a serializer already walking that list. The serializer throws "Collection was modified", the write is caught
/// and logged, and — because the pending provider was cleared before the callback ran — nothing
/// reschedules it. Freezing on publish costs one small object per mutation and nothing per save,
/// and it leaves every critical section exactly the length it already was.
/// </summary>
public sealed class ClusterManager
{
    private readonly Dictionary<(string Tenant, string Id), SemanticCluster> _clusters = new();

    /// <summary>
    /// Bumped by <see cref="Publish"/> on every change to <see cref="_clusters"/>, under the write
    /// lock. Read under either lock.
    ///
    /// It exists so the two cascade mutators can build their replacements OUTSIDE the attribution
    /// fence and still publish exact results: an unchanged value between the walk and the publish
    /// means no cluster was created, replaced or removed in between, so the precomputed list still
    /// describes the map. A changed value sends them back through the walk under the write lock,
    /// which is what they did unconditionally before. Compared, never interpreted — the absolute
    /// value means nothing.
    ///
    /// A long rather than an int: wrap-around would make two different maps compare equal, and at
    /// one publish per nanosecond a long still takes close to three centuries to wrap.
    /// </summary>
    private long _clusterVersion;

    private readonly ReaderWriterLockSlim _lock = new();
    private readonly CognitiveIndex _index;
    private readonly IStorageProvider _persistence;
    private bool _loaded;

    public ClusterManager(CognitiveIndex index, IStorageProvider persistence)
    {
        _index = index;
        _persistence = persistence;
    }

    /// <summary>
    /// THE ADMISSION-TO-MUTATION CHECK, the cluster half of the one in
    /// <see cref="Graph.KnowledgeGraph"/> and made exact by the same fence.
    ///
    /// True when some id in <paramref name="tenantId"/> crossed the ambiguity boundary after
    /// <paramref name="guard"/> fixed its view. Every screen here happens before the cluster lock,
    /// because the guard resolves through <see cref="CognitiveIndex"/>; that leaves a writable gap,
    /// and a twin landing in it turns an admitted member into a bare id two entries answer to,
    /// which the publish would then persist. Called under the fence's shared side this compare
    /// covers everything before it and the fence covers everything after, so the member list
    /// published is a list that was attributable at the instant it was published.
    ///
    /// One lock-free read of a per-tenant counter — no index lock, no allocation — which is what
    /// lets it sit inside the cluster write lock at all.
    /// </summary>
    private bool AttributionMovedSince(TopologyGuard.Sweep guard, string tenantId)
        => _index.AttributionRevisionFor(tenantId) != guard.AttributionRevision;

    /// <summary>
    /// The one reply for "this write was abandoned because attribution moved underneath it".
    ///
    /// It is deliberately NOT the member-dropping behaviour the rest of this class uses. Dropping is
    /// right for an id that is ambiguous — a property of the data, which would otherwise let one
    /// permanently ambiguous id disable clustering for a whole namespace. A MOVED REVISION is not a
    /// property of the data at all; it is a race between two counter samples, transient by
    /// construction, and the caller (an accretion sweep, a tool retry) simply asks again. Refusing
    /// therefore cannot deny the feature the way refusing on ambiguity would, and publishing instead
    /// would persist exactly the shared bare-id membership this class exists to keep out.
    ///
    /// It does disclose one bit — "a crossing happened somewhere in this tenant just now" — and
    /// that is stated rather than hidden. It is strictly weaker than the tenant-wide bit
    /// <see cref="TopologyGuard"/> already concedes (which names a persistent condition rather than
    /// an instant), and the same bit is already readable off a member count that changed between two
    /// calls. The alternative is not a smaller leak; it is the disclosure of another principal's
    /// entry through a shared membership bucket.
    /// </summary>
    private static string AttributionMoved(string clusterId)
        => $"Error: Cluster '{clusterId}' was not written — attribution changed during the write. Retry.";

    /// <summary>
    /// Materialize the persisted cluster set BEFORE the attribution fence is taken.
    ///
    /// <see cref="EnsureLoadedUnderWrite"/> calls <c>IStorageProvider.LoadClusters</c>, which is
    /// arbitrary caller-supplied code and may reach back into <see cref="CognitiveIndex"/>. An entry
    /// write that crosses the ambiguity boundary asks for the fence's EXCLUSIVE side, so running the
    /// load under the shared side would be one thread requesting both halves. Hoisting it out keeps
    /// the fence's "no index work inside" rule true by construction; the
    /// <see cref="EnsureLoadedUnderWrite"/> that remains inside each mutator is then a guaranteed
    /// no-op, kept as the fail-safe against a future reordering.
    /// </summary>
    private void EnsureLoadedOutsideFence()
    {
        _lock.EnterUpgradeableReadLock();
        try { EnsureLoaded(); }
        finally { _lock.ExitUpgradeableReadLock(); }
    }

    /// <summary>
    /// TEST SEAM: invoked by every fenced mutator while the attribution fence is held SHARED and the
    /// cluster write lock is held, AFTER <see cref="AttributionMovedSince"/> has passed and BEFORE
    /// the member list is published.
    ///
    /// That interval is the one the fence exists to make empty, and it is reachable from outside no
    /// other way — a test that plants its interfering write before the compare exercises the
    /// compare, not the fence. Null in production: one null check, no allocation.
    /// </summary>
    internal Action? OnValidatedUnderFence;

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
    ///
    /// Attribution MOVING between that screen and the publish is a different case and does refuse —
    /// see <see cref="AttributionMoved"/> for why the two must not share a policy. The reply starts
    /// with "Error:", which is what every caller here (the accretion sweep, the tool layer) already
    /// tests for, so a raced creation is skipped and retried rather than half-applied.
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

        // Outside the fence, because the load runs storage-provider code — see EnsureLoadedOutsideFence.
        EnsureLoadedOutsideFence();

        // The fence spans the compare and the publish, which is the whole point: the screen above
        // ran outside every lock, and the reviewer's repro planted a same-id twin from inside the
        // caller's own IReadOnlyList — after admission, before publication. Nothing in this class
        // could have observed that; the exclusive side of the fence is what now makes it wait.
        var fence = _index.EnterAttributionFence(tenant);
        try
        {
            _lock.EnterWriteLock();
            try
            {
                EnsureLoadedUnderWrite();
                if (_clusters.ContainsKey(key))
                    return $"Error: Cluster '{clusterId}' already exists.";

                if (AttributionMovedSince(guard, tenant))
                    return AttributionMoved(clusterId);

                OnValidatedUnderFence?.Invoke();

                // memberIdsCopy becomes the new cluster's frozen member list AND stays reachable
                // from this method for the centroid pass below. That aliasing is safe for exactly
                // one reason: nothing ever mutates a published member list — see the class remarks —
                // so the read outside the lock and the serializer's read on a pool thread see the
                // same fixed content. Anything that needs a DIFFERENT member set builds a new list.
                Publish(key, new SemanticCluster(clusterId, ns, memberIdsCopy, label, tenant));
            }
            finally { _lock.ExitWriteLock(); }
        }
        finally { CognitiveIndex.ExitAttributionFence(fence); }

        ScheduleSaveClusters();

        // Compute centroid outside cluster lock AND outside the fence: it resolves through _index,
        // which has its own locks and whose lazy load asks for the fence's exclusive side.
        var centroid = ComputeCentroidFromMembers(memberIdsCopy, tenant, guard);
        ApplyCentroid(key, centroid, guard, tenant);

        // The count of what was actually stored, not of what was asked for. A cluster that reports
        // three members and holds two is a lie, and the difference is the same one bit the guard
        // already costs elsewhere.
        return $"Created cluster '{clusterId}' with {memberIdsCopy.Count} members.";
    }

    /// <summary>
    /// Publish a recomputed centroid under the fence, or decline to.
    ///
    /// The centroid is DERIVED from ids <paramref name="guard"/> admitted and resolved outside every
    /// lock, so publishing it is a write of that admission's consequences and gets the same
    /// treatment as the membership publish: fence held across the compare and the store. A moved
    /// revision skips the publish rather than refusing the whole operation, because a centroid is
    /// derived state — leaving the previous one in place is a value that was itself computed under a
    /// valid attribution, and the next mutation recomputes it. Publishing one folded from a member
    /// that has just become ambiguous is the disclosure this class already refuses at every read.
    ///
    /// Fenced separately from the membership publish rather than in one hold, because the
    /// computation between them MUST NOT run inside the fence — see the class remarks.
    /// </summary>
    private void ApplyCentroid(
        (string Tenant, string Id) key, float[]? centroid, TopologyGuard.Sweep guard, string tenant)
    {
        bool published = false;
        var fence = _index.EnterAttributionFence(tenant);
        try
        {
            _lock.EnterWriteLock();
            try
            {
                if (AttributionMovedSince(guard, tenant))
                    return;

                if (_clusters.TryGetValue(key, out var c))
                {
                    Publish(key, Replace(c, c.MemberIds, c.Label, centroid, c.SummaryEntryId));
                    published = true;
                }
            }
            finally { _lock.ExitWriteLock(); }
        }
        finally { CognitiveIndex.ExitAttributionFence(fence); }

        if (published)
            ScheduleSaveClusters();
    }

    /// <summary>
    /// Update cluster members and/or label. Both directions are screened: adding an ambiguous id
    /// would enrol a twin the caller cannot see, and removing one would evict that twin's
    /// membership. The reported member count is the real one after screening.
    ///
    /// Refuses with <see cref="AttributionMoved"/> when attribution moved between that screen and
    /// the publish — an "Error:" reply the callers already skip on, rather than a member list
    /// published against an admission that has expired.
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

        // Outside the fence, because the load runs storage-provider code — see EnsureLoadedOutsideFence.
        EnsureLoadedOutsideFence();

        // Fence held across the compare and the publish, for both directions of the edit: an add
        // admitted while its id was unique would enrol a twin that appeared in between, and a remove
        // admitted the same way would evict that twin's membership.
        var fence = _index.EnterAttributionFence(tenant);
        try
        {
            _lock.EnterWriteLock();
            try
            {
                EnsureLoadedUnderWrite();
                if (!_clusters.TryGetValue(key, out var cluster))
                    return $"Error: Cluster '{clusterId}' not found.";

                if (AttributionMovedSince(guard, tenant))
                    return AttributionMoved(clusterId);

                OnValidatedUnderFence?.Invoke();

                // Edited on a copy and published by replacement, never in place. This is the same
                // one allocation the old `cluster.MemberIds.ToList()` at the end of this block
                // already made — it has moved to the front and become the stored list instead of a
                // throwaway, so the critical section is neither longer nor more allocating than it
                // was.
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

                Publish(key, Replace(
                    cluster, memberIdsCopy, label ?? cluster.Label, cluster.Centroid, cluster.SummaryEntryId));
            }
            finally { _lock.ExitWriteLock(); }
        }
        finally { CognitiveIndex.ExitAttributionFence(fence); }

        ScheduleSaveClusters();

        // Counted outside the lock, because counting consults the guard and the guard resolves
        // through CognitiveIndex. The figure is the attributable one, matching what GetCluster
        // reports: a reply that said 3 while get_cluster showed 2 would be the "a twin exists"
        // oracle re-opened as an arithmetic difference.
        var memberCount = memberIdsCopy.Count(guard.IsTopologySafe);

        // Compute centroid outside cluster lock and outside the fence, apply it under both.
        var centroid = ComputeCentroidFromMembers(memberIdsCopy, tenant, guard);
        ApplyCentroid(key, centroid, guard, tenant);

        return $"Updated cluster '{clusterId}' ({memberCount} members).";
    }

    /// <summary>
    /// Store an LLM-generated summary as a searchable entry tied to a cluster.
    ///
    /// THE ONE MUTATOR HERE THAT TAKES NO ATTRIBUTION FENCE, and deliberately. It admits no id
    /// through a <see cref="TopologyGuard.Sweep"/> — there is no member list, and its only bare id
    /// is the derived <c>summary:{clusterId}</c>, which every read screens before resolving — so
    /// there is no admission for a fence to protect. It also UPSERTS an entry below, and an upsert
    /// that crosses the ambiguity boundary asks for the fence's EXCLUSIVE side; a fence holder
    /// requesting that would be requesting both halves on one thread. Leaving it unfenced is what
    /// keeps the fence's contract honest rather than an exception carved into it.
    ///
    /// It calls <see cref="EnsureLoadedOutsideFence"/> anyway, and taking no fence is precisely why
    /// it has to. Unfenced does not mean uninvolved: this method takes the cluster WRITE lock, and
    /// the one-shot load it can trigger runs <c>IStorageProvider.LoadClusters</c> under that lock. A
    /// provider that wrote an entry back into <see cref="CognitiveIndex"/> from inside the load
    /// would then block on the fence's exclusive side while holding the structural lock that every
    /// fence holder is waiting for — the reverse acquisition order, reachable from the one member
    /// this class describes as needing no fence. Warming outside removes it. See the class remarks
    /// for the residual this does NOT remove.
    /// </summary>
    public string StoreSummary(string clusterId, string summaryText, float[] summaryVector, string tenantId)
    {
        // Get cluster info under cluster lock, then do CognitiveIndex upsert outside
        // to avoid lock-ordering deadlock.
        string ns;
        string summaryId;
        var tenant = Tenancy.Normalize(tenantId);
        var key = (tenant, clusterId);

        // Before the write lock, never under it — see the remarks above.
        EnsureLoadedOutsideFence();

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
            Publish(key, Replace(
                cluster, cluster.MemberIds, cluster.Label, cluster.Centroid, summaryId));
        }
        finally { _lock.ExitWriteLock(); }

        ScheduleSaveClusters();

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
    /// Pass the <c>guard</c> overload a sweep shared with the OTHER cascade primitive for the same
    /// id — <see cref="Graph.KnowledgeGraph.RemoveAllEdgesForEntry(string, string,
    /// TopologyGuard.Sweep)"/> — so one namespace listing covers both halves of that id's purge.
    ///
    /// SHARING A SWEEP ACROSS MANY IDS IS A DIFFERENT PROPOSITION AND IS NOT WHAT THIS IS FOR. A
    /// sweep carries ONE <see cref="TopologyGuard.Sweep.AttributionRevision"/>, and every mutator
    /// holding it fails closed the moment that value goes stale — so one unrelated crossing anywhere
    /// in the tenant turns every remaining call on a batch-wide sweep into a silent no-op while the
    /// deletion those calls were cascading still happens. The unit a sweep may span is the unit that
    /// can tolerate failing closed together.
    /// </summary>
    public void RemoveEntryFromAllClusters(string entryId, string tenantId)
        => RemoveEntryFromAllClusters(entryId, tenantId,
            TopologyGuard.ForSweep(_index, Tenancy.Normalize(tenantId)));

    /// <inheritdoc cref="RemoveEntryFromAllClusters(string, string)"/>
    /// <exception cref="ArgumentException">
    /// <paramref name="guard"/> was built for a different tenant.
    /// </exception>
    public void RemoveEntryFromAllClusters(string entryId, string tenantId, TopologyGuard.Sweep guard)
    {
        ArgumentNullException.ThrowIfNull(guard);

        var tenant = Tenancy.Normalize(tenantId);

        // A SWEEP FROM ANOTHER TENANT FAILS OPEN, which is why this is an assertion. The sweep
        // judges ids against ITS tenant's namespace listing, so asked about this tenant's id it
        // counts zero namespaces — and zero is treated as attributable, deliberately. Every id would
        // be admitted without ever having been judged, on the path whose purpose is to judge it.
        if (!string.Equals(guard.TenantId, tenant, StringComparison.Ordinal))
            throw new ArgumentException(
                "The topology sweep was built for a different tenant than this eviction targets.", nameof(guard));

        // Evicting an ambiguous id evicts the invisible twin's membership along with it, so the
        // screen runs before the lock like every other mutator here.
        if (!guard.IsTopologySafe(entryId))
            return;

        // Outside the fence, because the load runs storage-provider code — see EnsureLoadedOutsideFence.
        EnsureLoadedOutsideFence();

        // PHASE 1, OUTSIDE THE FENCE: walk the tenant's clusters and build the replacements.
        //
        // The walk is O(clusters in the tenant) and allocates a list per affected cluster, and it
        // used to run under the fence's shared side. That matters more than its own cost: the
        // fence's exclusive side is taken by a crossing that is already holding a partition write
        // lock, so every microsecond of shared hold queues later readers of that partition behind
        // it. Nothing in the walk needs the fence — the fence protects the attribution DECISION and
        // the PUBLISH, and the walk is neither.
        long collectedAt;
        List<(string ClusterId, SemanticCluster Updated)> affectedClusters;
        _lock.EnterReadLock();
        try
        {
            collectedAt = _clusterVersion;
            affectedClusters = CollectEvictionsUnderLock(entryId, tenant);
        }
        finally { _lock.ExitReadLock(); }

        bool published = false;

        var fence = _index.EnterAttributionFence(tenant);
        try
        {
            _lock.EnterWriteLock();
            try
            {
                EnsureLoadedUnderWrite();

                // Fails closed the way this method already fails when the id is ambiguous: nothing
                // removed, no cluster touched. An eviction decided against stale attribution takes
                // the invisible twin's membership away with it.
                if (AttributionMovedSince(guard, tenant))
                    return;

                OnValidatedUnderFence?.Invoke();

                // THE PRECOMPUTED REPLACEMENTS ARE USABLE ONLY IF THE MAP HAS NOT MOVED, and
                // _clusterVersion answers exactly that: it is bumped by every publish, under this
                // same write lock, so an unchanged value means no cluster was created, replaced or
                // removed between the walk and here. When it HAS moved the walk is simply redone
                // under this lock, which is what this method did unconditionally before — so the
                // slow path is the old behaviour and the fast path is the same result reached
                // without holding the fence across the walk.
                if (collectedAt != _clusterVersion)
                    affectedClusters = CollectEvictionsUnderLock(entryId, tenant);

                // Published after the walk rather than inside it. Overwriting an existing key is
                // legal during a Dictionary enumeration on modern runtimes, but this class is
                // multi-targeted and the guarantee is not worth depending on for a loop that already
                // has the list.
                foreach (var (clusterId, updated) in affectedClusters)
                    Publish((tenant, clusterId), updated);

                published = affectedClusters.Count > 0;
            }
            finally { _lock.ExitWriteLock(); }
        }
        finally { CognitiveIndex.ExitAttributionFence(fence); }

        if (published)
            ScheduleSaveClusters();

        // Phase 2: Recompute centroids outside cluster lock AND outside the fence
        if (affectedClusters.Count == 0) return;

        var centroids = new List<(string clusterId, float[]? centroid)>();
        foreach (var (clusterId, updated) in affectedClusters)
            centroids.Add((clusterId, ComputeCentroidFromMembers(updated.MemberIds, tenant, guard)));

        // Phase 3: Apply centroids under the fence and the cluster lock — see ApplyCentroids.
        ApplyCentroids(centroids, guard, tenant);
    }

    /// <summary>
    /// The many-cluster form of <see cref="ApplyCentroid"/>, for the two cascade-shaped mutators —
    /// same rule, same reason: the values were derived from ids admitted outside every lock, so they
    /// are published under the fence with the same compare, and a moved revision skips the publish
    /// rather than storing a centroid folded from a member that has just become ambiguous.
    ///
    /// One fence acquisition and one lock acquisition for the whole batch, so the cascade does not
    /// pay a round trip per affected cluster.
    /// </summary>
    private void ApplyCentroids(
        List<(string ClusterId, float[]? Centroid)> centroids, TopologyGuard.Sweep guard, string tenant)
    {
        bool published = false;
        var fence = _index.EnterAttributionFence(tenant);
        try
        {
            _lock.EnterWriteLock();
            try
            {
                if (AttributionMovedSince(guard, tenant))
                    return;

                foreach (var (clusterId, centroid) in centroids)
                {
                    if (_clusters.TryGetValue((tenant, clusterId), out var c))
                    {
                        Publish((tenant, clusterId), Replace(c, c.MemberIds, c.Label, centroid, c.SummaryEntryId));
                        published = true;
                    }
                }
            }
            finally { _lock.ExitWriteLock(); }
        }
        finally { CognitiveIndex.ExitAttributionFence(fence); }

        if (published)
            ScheduleSaveClusters();
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
    ///
    /// A SELF-TRANSFER (<paramref name="fromId"/> == <paramref name="toId"/>) RE-HOMES NOTHING and
    /// is refused up front, for the same reason as its graph twin: the screen above tests the two
    /// arguments SEPARATELY, so both pass when they are the same id. Running it removes the member
    /// and immediately re-adds it, which leaves the membership SET unchanged while republishing
    /// every cluster that held it, moving the member to the end of each list, bumping the map
    /// version, scheduling a persist, recomputing centroids — and returning a count that says those
    /// clusters were re-homed. That count is what <c>merge_memories</c> reports.
    /// </summary>
    public int TransferMembership(string fromId, string toId, string tenantId)
    {
        List<(string ClusterId, SemanticCluster Updated)> affectedClusters;
        var tenant = Tenancy.Normalize(tenantId);

        // Ordinal, matching the id comparisons in the rest of this class and in the graph: two ids
        // that differ only by culture-sensitive equality are two members here.
        if (string.Equals(fromId, toId, StringComparison.Ordinal))
            return 0;

        // Before the lock, as everywhere else here: the guard resolves through CognitiveIndex.
        var guard = TopologyGuard.ForSweep(_index, tenant);
        if (!guard.IsTopologySafe(fromId) || !guard.IsTopologySafe(toId))
            return 0;

        // Outside the fence, because the load runs storage-provider code — see EnsureLoadedOutsideFence.
        EnsureLoadedOutsideFence();

        // PHASE 1, OUTSIDE THE FENCE — see RemoveEntryFromAllClusters for the whole argument: the
        // walk is O(clusters in the tenant) and allocates per affected cluster, the fence protects
        // the attribution decision and the publish rather than the walk, and a shared hold across it
        // queues later readers of an unrelated partition behind a blocked crossing.
        long collectedAt;
        _lock.EnterReadLock();
        try
        {
            collectedAt = _clusterVersion;
            affectedClusters = CollectMembershipTransfersUnderLock(fromId, toId, tenant);
        }
        finally { _lock.ExitReadLock(); }

        bool published = false;

        var fence = _index.EnterAttributionFence(tenant);
        try
        {
            _lock.EnterWriteLock();
            try
            {
                EnsureLoadedUnderWrite();

                // Fails closed the way this method already fails when an endpoint is ambiguous: 0,
                // nothing re-homed. Rewiring membership by bare id against stale attribution is how
                // a caller with a writable twin moves another principal's entry between clusters.
                if (AttributionMovedSince(guard, tenant))
                    return 0;

                OnValidatedUnderFence?.Invoke();

                // Same version test as the eviction path: an unmoved _clusterVersion means the
                // precomputed replacements still describe the current map, and a moved one redoes
                // the walk here — which is exactly what this method did unconditionally before.
                if (collectedAt != _clusterVersion)
                    affectedClusters = CollectMembershipTransfersUnderLock(fromId, toId, tenant);

                // Published after the walk — see RemoveEntryFromAllClusters for why not inside it.
                foreach (var (clusterId, updated) in affectedClusters)
                    Publish((tenant, clusterId), updated);

                published = affectedClusters.Count > 0;
            }
            finally { _lock.ExitWriteLock(); }
        }
        finally { CognitiveIndex.ExitAttributionFence(fence); }

        if (published)
            ScheduleSaveClusters();

        // Recompute centroids outside the lock AND outside the fence
        if (affectedClusters.Count == 0) return 0;

        var centroids = new List<(string clusterId, float[]? centroid)>();
        foreach (var (clusterId, updated) in affectedClusters)
            centroids.Add((clusterId, ComputeCentroidFromMembers(updated.MemberIds, tenant, guard)));

        ApplyCentroids(centroids, guard, tenant);

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
            // Through Publish like every other write to the map: _clusterVersion has to move
            // whenever _clusters does, or a walk collected before a lazy load would look current.
            foreach (var c in clusters)
                Publish((c.TenantId, c.ClusterId), c);
            _loaded = true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    // Called when already holding write lock.
    private void EnsureLoadedUnderWrite()
    {
        if (_loaded) return;
        var clusters = _persistence.LoadClusters();
        // See EnsureLoaded: the version moves with the map, always.
        foreach (var c in clusters)
            Publish((c.TenantId, c.ClusterId), c);
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
    /// The ONE way anything enters or replaces a value in <see cref="_clusters"/>. Caller holds the
    /// write lock.
    ///
    /// Routing every publish through here is what makes <see cref="_clusterVersion"/> trustworthy:
    /// a direct indexer write somewhere else would move the map without moving the version, and the
    /// cascade mutators would then publish replacements built against a map that had changed.
    /// </summary>
    private void Publish((string Tenant, string Id) key, SemanticCluster cluster)
    {
        _clusters[key] = cluster;
        _clusterVersion++;
    }

    /// <summary>
    /// The replacements that evicting <paramref name="entryId"/> from <paramref name="tenant"/>'s
    /// clusters would produce. Caller holds the cluster lock, in either mode; this reads and
    /// allocates but publishes nothing.
    ///
    /// Extracted so the walk can run under the READ lock outside the attribution fence and, on the
    /// rare occasion the map moved in between, be redone verbatim under the write lock. Two copies
    /// of a walk are two chances for them to drift.
    /// </summary>
    private List<(string ClusterId, SemanticCluster Updated)> CollectEvictionsUnderLock(
        string entryId, string tenant)
    {
        var affected = new List<(string ClusterId, SemanticCluster Updated)>();
        foreach (var cluster in _clusters.Values)
        {
            if (cluster.TenantId != tenant) continue;
            // Tested before copying, so the copy is paid for only by clusters that actually hold
            // the entry — the old in-place Remove()'s bool did the same job.
            if (!cluster.MemberIds.Contains(entryId)) continue;

            var remaining = new List<string>(cluster.MemberIds);
            remaining.Remove(entryId);
            affected.Add((cluster.ClusterId,
                Replace(cluster, remaining, cluster.Label, cluster.Centroid, cluster.SummaryEntryId)));
        }
        return affected;
    }

    /// <summary>
    /// The replacements that re-homing <paramref name="fromId"/> onto <paramref name="toId"/> would
    /// produce. Caller holds the cluster lock, in either mode; reads and allocates, publishes
    /// nothing. See <see cref="CollectEvictionsUnderLock"/> for why it is extracted.
    /// </summary>
    private List<(string ClusterId, SemanticCluster Updated)> CollectMembershipTransfersUnderLock(
        string fromId, string toId, string tenant)
    {
        var affected = new List<(string ClusterId, SemanticCluster Updated)>();
        foreach (var cluster in _clusters.Values)
        {
            if (cluster.TenantId != tenant) continue;
            // Tested before copying, so only clusters that really hold fromId pay for a list — the
            // old in-place Remove()'s bool decided the same thing.
            if (!cluster.MemberIds.Contains(fromId)) continue;

            var members = new List<string>(cluster.MemberIds);
            members.Remove(fromId);
            // Checked AFTER the removal, as before: a cluster holding both endpoints collapses to
            // one membership rather than keeping a stale duplicate.
            if (!members.Contains(toId))
                members.Add(toId);

            affected.Add((cluster.ClusterId,
                Replace(cluster, members, cluster.Label, cluster.Centroid, cluster.SummaryEntryId)));
        }
        return affected;
    }

    /// <summary>
    /// Register the deferred cluster snapshot with persistence. MUST be called AFTER the write lock
    /// and the attribution fence are released.
    ///
    /// A METHOD GROUP, NOT A PRE-CAPTURED LIST, and the difference is a silent data-loss bug.
    /// Debounced saving is last-registration-wins over a FULL-REPLACE blob: every provider disposes
    /// the pending timer, overwrites the pending provider, and later rewrites the whole cluster
    /// document from whichever one survived. With an eagerly captured snapshot, capture and
    /// registration are two separate moments, so two overlapping mutators can capture in one order
    /// and register in the other — and the OLDER map is then the one that reaches storage. The
    /// newer mutation stays live in memory, so nothing looks wrong until the process restarts and
    /// the cluster (or membership, or summary link) is simply gone. Handing over
    /// <see cref="SnapshotClustersForSave"/> makes the registration order irrelevant: whichever
    /// registration wins, the snapshot is taken when the debounce fires and is therefore the latest
    /// state. It is the shape <c>KnowledgeGraph.ScheduleSaveEdges</c> already uses, for this reason.
    ///
    /// The call site stays outside both locks. <c>IStorageProvider</c> does not forbid invoking the
    /// provider synchronously, and a synchronous one runs the whole serialize-and-write inline:
    /// under the cluster write lock that would put a storage round trip inside a critical section
    /// every reader contends for AND re-enter this class's non-recursive lock from the snapshot;
    /// under the attribution fence, a provider that touched <see cref="CognitiveIndex"/> would ask
    /// for the exclusive side of a fence this thread holds the shared side of.
    /// </summary>
    private void ScheduleSaveClusters()
    {
        OnBeforeScheduleSave?.Invoke();
        _persistence.ScheduleSaveClusters(SnapshotClustersForSave);
    }

    /// <summary>
    /// TEST SEAM: invoked immediately before a save is registered with persistence, outside the
    /// cluster lock and outside the attribution fence.
    ///
    /// It exists because "the newest snapshot is the one that reaches storage" is only observable by
    /// suspending one mutator in the gap between its publish and its registration, running a second
    /// one to completion through that gap, and then flushing. Null in production: one null check.
    /// </summary>
    internal Action? OnBeforeScheduleSave;

    /// <summary>
    /// The cluster snapshot handed to persistence, taken on the debounce thread under the read lock.
    ///
    /// The shallow <c>Values.ToList()</c> IS the deep copy here, and only because of the freeze
    /// invariant in the class remarks: it copies references, and every referenced cluster — its
    /// <see cref="SemanticCluster.MemberIds"/> list included — is immutable from the moment it
    /// entered the map. So it can be invoked minutes later, from a debounce timer on a thread that
    /// holds none of this class's locks, and serialize a state that is internally consistent and
    /// cannot move underneath the serializer.
    ///
    /// If a future edit reintroduces in-place mutation of a stored cluster, THIS is the line that
    /// silently becomes wrong — not with a compile error but with an intermittent "Collection was
    /// modified" inside <c>JsonSerializer.Serialize</c> on a pool thread, caught and logged by the
    /// storage provider, after which nothing reschedules the write.
    ///
    /// It deliberately does NOT call EnsureLoaded, and cannot need to: the only thing that schedules
    /// a save is a mutator that already loaded under its own write lock, and <c>_loaded</c> never
    /// goes back to false — so there is no interleaving in which this writes an empty in-memory map
    /// over a populated store.
    /// </summary>
    private List<SemanticCluster> SnapshotClustersForSave()
    {
        _lock.EnterReadLock();
        try { return _clusters.Values.ToList(); }
        finally { _lock.ExitReadLock(); }
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
