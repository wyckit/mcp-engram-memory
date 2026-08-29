using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;

namespace McpEngramMemory.Core.Services;

/// <summary>
/// Implements Collins &amp; Loftus spreading activation for graph-coupled energy transfer.
/// When a memory is accessed, activation energy propagates to graph neighbors and cluster peers,
/// pre-warming related memories for anticipatory retrieval.
///
/// This is a multi-hop WALK that WRITES, and it runs with no principal of its own, so the bare-id
/// attribution rule applies to it in full. Graph adjacency and cluster membership are keyed
/// (tenant, id) with no namespace, so an id the tenant holds in two namespaces names ONE node and
/// ONE membership bucket shared by two entries: reaching either one reaches whatever hangs off a
/// twin nobody showed this walk, and the boost that lands there is a write on a stranger's entry,
/// observable afterwards in that entry's retrieval ordering.
///
/// Almost all of that is enforced at the boundary rather than here, and this class deliberately does
/// NOT restate it. <see cref="KnowledgeGraph.GetNeighbors"/> applies
/// <see cref="Graph.TopologyGuard.Sweep.IsEdgeUsable(Models.GraphEdge)"/> to every edge it hands back, so no hop can
/// cross an unattributable node; <see cref="ClusterManager.GetCluster"/> withholds an
/// unattributable member and an unattributable summary from its own projection. A second copy of
/// either test here would refuse nothing extra and would be one more place to keep in step.
///
/// ONE test is genuinely this class's, and it is the entry point.
/// <see cref="ClusterManager.GetClustersForEntry"/> is unscreened by design — it answers "which
/// clusters hold THIS id" and discloses no member but the one named — and its contract puts the
/// gate on whoever supplies the id. That is this class, so <see cref="PropagateAccess"/> gates its
/// root before asking. See <see cref="Graph.TopologyGuard"/> for why the test is ACL-blind, why an
/// id no entry answers to is safe, and what one bit the suppression costs.
/// </summary>
public sealed class SpreadingActivationService
{
    private const float MinPropagationThreshold = 0.1f;
    private const int MaxPropagationDepth = 3;
    private const float RecursiveDecay = 0.5f;
    private const float ClusterSummaryBoost = 1.0f;
    private const float ClusterPeerBoost = 0.5f;
    private const float ClusterNeighborBoost = 0.25f;
    private const int MaxClusterPeers = 3;

    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;

    public SpreadingActivationService(CognitiveIndex index, KnowledgeGraph graph, ClusterManager clusters)
    {
        _index = index;
        _graph = graph;
        _clusters = clusters;
    }

    /// <summary>
    /// Propagate activation energy from an accessed memory to its graph neighbors and cluster peers.
    /// Called asynchronously after search results are returned to avoid adding latency.
    /// </summary>
    /// <param name="id">The accessed memory's ID.</param>
    /// <param name="ns">The namespace of the accessed memory.</param>
    /// <param name="tenantId">Tenant whose graph/cluster structures to traverse. Pass "" for the legacy partition.</param>
    /// <param name="baseEnergy">Base energy to propagate (default 1.0).</param>
    public SpreadingResult PropagateAccess(string id, string ns, string tenantId, float baseEnergy = 1.0f)
    {
        // An id alone is not an identity — ids are unique only per (tenant, namespace). Topology
        // (graph adjacency, cluster membership) hands back bare ids, so every target is accumulated
        // under the namespace it was actually resolved from. Keying by the pair also stops two
        // distinct entries that happen to share an id in different namespaces of the same tenant
        // from merging their energy into whichever namespace was discovered first.
        var boosted = new Dictionary<(string Ns, string Id), float>();

        // The root is the one id this class must judge itself, because the cluster phase enters
        // membership through GetClustersForEntry, which is unscreened by contract and expects its
        // caller to have gated the id. When the tenant holds this id in two namespaces the
        // membership bucket is shared with an entry nobody showed this walk, so the clusters it
        // reports are not necessarily the accessed entry's — and pre-warming their peers would push
        // energy into a stranger's neighbourhood.
        //
        // The single-id overload, not a sweep: exactly one id is tested here (the hops and the
        // cluster members are judged at the boundary), and this overload probes the candidate index
        // for that id rather than listing the tenant's namespaces.
        //
        // Reaching nothing is exactly what an entry with no topology already reports, so failing
        // closed is not a signal about what else exists.
        if (!TopologyGuard.IsSafe(_index, id, tenantId))
            return new SpreadingResult(id, 0, 0, 0f);

        // Phase 1: Graph-based spreading activation (within the caller's tenant)
        PropagateGraph(id, baseEnergy, depth: 0, boosted, tenantId);

        // Phase 2: Cluster-based pre-warming
        PropagateCluster(id, baseEnergy, boosted, tenantId);

        // Phase 3: Apply all accumulated boosts, each in its own namespace
        var source = (Ns: ns, Id: id);
        int applied = 0;
        foreach (var (target, totalBoost) in boosted)
        {
            // The authoritative self-boost guard: only the exact accessed entry is excluded, not a
            // same-id entry that lives in a different namespace.
            if (target == source) continue;

            if (ApplyBoost(target.Id, target.Ns, totalBoost, tenantId))
                applied++;
        }

        return new SpreadingResult(id, boosted.Count, applied, boosted.Values.Sum());
    }

    /// <summary>
    /// Boost one target in ITS OWN namespace — the single place that knows about the tenancy branch.
    /// Legacy keeps the id→ns fallback overload (a stale or unresolvable target still lands), which
    /// is now only a safety net because the fast path already gets the right namespace. A tenant
    /// boost uses the no-fallback overload: the exact (tenant, targetNs) partition, so it can never
    /// reach another tenant's co-keyed entry.
    /// </summary>
    private bool ApplyBoost(string targetId, string targetNs, float boost, string tenantId)
        => tenantId.Length == 0
            ? _index.BoostActivationEnergy(targetId, targetNs, boost)
            : _index.BoostActivationEnergy(targetId, targetNs, boost, tenantId: tenantId);

    /// <summary>
    /// Recursive graph-based energy propagation with fan-out attenuation and depth cutoff.
    ///
    /// No topology test of its own, and that is the boundary fix working rather than an omission.
    /// <see cref="KnowledgeGraph.GetNeighbors"/> withholds any edge whose far endpoint is
    /// unattributable, so an ambiguous intermediate is not merely absent from the boosts — it is
    /// absent from the adjacency this walk recurses into, which is the half that matters. A node
    /// crossed and then filtered has already led the walk to a twin's descendants.
    /// </summary>
    private void PropagateGraph(string id, float energy, int depth, Dictionary<(string Ns, string Id), float> boosted,
        string tenantId)
    {
        if (depth >= MaxPropagationDepth || energy < MinPropagationThreshold)
            return;

        var neighborsResult = _graph.GetNeighbors(id, relation: null, direction: "both", tenantId: tenantId);

        // Degree is the ADMITTED neighbour count, which is the only one available here and also the
        // right one: attenuating by a fan-out that included withheld edges would make the
        // suppression readable off the energy the survivors received.
        int nodeDegree = neighborsResult.Neighbors.Count;

        foreach (var neighbor in neighborsResult.Neighbors)
        {
            string neighborId = neighbor.Entry.Id;
            // The traversal already resolved this neighbor to a concrete entry, so its namespace is
            // authoritative — it is where the boost must land, not wherever the caller came from.
            string neighborNs = neighbor.Entry.Namespace;
            float boost = PhysicsEngine.ComputeSpreadingEnergy(energy, neighbor.Edge.Relation, nodeDegree);

            if (boost < MinPropagationThreshold)
                continue;

            Accumulate(boosted, neighborNs, neighborId, boost);

            // Recursive spread at reduced energy. The bare id is correct here: graph adjacency is
            // keyed (tenant, id) with no namespace dimension, so there is nothing else to pass — and
            // this id already passed the edge test that admitted it, so it names one entry.
            PropagateGraph(neighborId, boost * RecursiveDecay, depth + 1, boosted, tenantId);
        }
    }

    /// <summary>
    /// Cluster-based pre-warming: accessing any member activates cluster summary and top peers.
    ///
    /// No topology test of its own, and both halves of that are load-bearing. The bare id it enters
    /// by is <paramref name="id"/>, which <see cref="PropagateAccess"/> gated precisely because
    /// <see cref="ClusterManager.GetClustersForEntry"/> does not. Every peer and summary it boosts
    /// then arrives through <see cref="ClusterManager.GetCluster"/>, whose projection withholds
    /// anything it cannot attribute to one entry — so a second copy of that predicate here would be
    /// one more place to keep in step with Core for no additional refusal.
    /// </summary>
    private void PropagateCluster(string id, float baseEnergy, Dictionary<(string Ns, string Id), float> boosted, string tenantId)
    {
        var clusterIds = _clusters.GetClustersForEntry(id, tenantId: tenantId);

        foreach (var clusterId in clusterIds)
        {
            var clusterInfo = _clusters.GetCluster(clusterId, tenantId: tenantId);
            if (clusterInfo is null) continue;

            // Boost cluster summary node (full boost). A CognitiveSearchResult carries no namespace,
            // but the cluster's own namespace is authoritative for it: StoreSummary writes the
            // summary entry with ns = cluster.Ns, which is exactly what GetCluster reports here.
            if (clusterInfo.SummaryEntry is not null)
            {
                var summaryId = clusterInfo.SummaryEntry.Id;
                // Cheap pre-filter only; the exact (ns, id) comparison in PropagateAccess decides.
                if (summaryId != id)
                    Accumulate(boosted, clusterInfo.Namespace, summaryId, baseEnergy * ClusterSummaryBoost);
            }

            // Boost top-N highest-energy cluster peers (50% boost)
            int peerCount = 0;
            foreach (var member in clusterInfo.Members)
            {
                if (member.Id == id) continue;
                if (peerCount >= MaxClusterPeers) break;

                Accumulate(boosted, member.Namespace, member.Id, baseEnergy * ClusterPeerBoost);
                peerCount++;
            }
        }
    }

    /// <summary>
    /// Accumulate a boost against the (namespace, id) pair: a node reachable via multiple paths gets
    /// combined energy, while two same-id entries in different namespaces stay separate targets.
    /// </summary>
    private static void Accumulate(Dictionary<(string Ns, string Id), float> boosted, string ns, string id, float boost)
    {
        var key = (ns, id);
        boosted[key] = boosted.TryGetValue(key, out float existing) ? existing + boost : boost;
    }
}

/// <summary>Result of a spreading activation propagation.</summary>
public sealed record SpreadingResult(
    string SourceId,
    int NodesReached,
    int NodesUpdated,
    float TotalEnergySpread);
