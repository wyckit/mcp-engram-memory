using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;

namespace McpEngramMemory.Core.Services;

/// <summary>
/// Implements Collins &amp; Loftus spreading activation for graph-coupled energy transfer.
/// When a memory is accessed, activation energy propagates to graph neighbors and cluster peers,
/// pre-warming related memories for anticipatory retrieval.
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
    /// </summary>
    private void PropagateGraph(string id, float energy, int depth, Dictionary<(string Ns, string Id), float> boosted, string tenantId)
    {
        if (depth >= MaxPropagationDepth || energy < MinPropagationThreshold)
            return;

        var neighborsResult = _graph.GetNeighbors(id, relation: null, direction: "both", tenantId: tenantId);
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
            // keyed (tenant, id) with no namespace dimension, so there is nothing else to pass.
            PropagateGraph(neighborId, boost * RecursiveDecay, depth + 1, boosted, tenantId);
        }
    }

    /// <summary>
    /// Cluster-based pre-warming: accessing any member activates cluster summary and top peers.
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
