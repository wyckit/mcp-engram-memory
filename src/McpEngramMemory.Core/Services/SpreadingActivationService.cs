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
    /// <param name="baseEnergy">Base energy to propagate (default 1.0).</param>
    public SpreadingResult PropagateAccess(string id, string ns, float baseEnergy = 1.0f)
    {
        var boosted = new Dictionary<string, float>();

        // Phase 1: Graph-based spreading activation
        PropagateGraph(id, baseEnergy, depth: 0, boosted);

        // Phase 2: Cluster-based pre-warming
        PropagateCluster(id, baseEnergy, boosted);

        // Phase 3: Apply all accumulated boosts
        int applied = 0;
        foreach (var (targetId, totalBoost) in boosted)
        {
            if (targetId == id) continue; // Don't self-boost
            if (_index.BoostActivationEnergy(targetId, ns, totalBoost))
                applied++;
        }

        return new SpreadingResult(id, boosted.Count, applied, boosted.Values.Sum());
    }

    /// <summary>
    /// Recursive graph-based energy propagation with fan-out attenuation and depth cutoff.
    /// </summary>
    private void PropagateGraph(string id, float energy, int depth, Dictionary<string, float> boosted)
    {
        if (depth >= MaxPropagationDepth || energy < MinPropagationThreshold)
            return;

        var neighborsResult = _graph.GetNeighbors(id);
        int nodeDegree = neighborsResult.Neighbors.Count;

        foreach (var neighbor in neighborsResult.Neighbors)
        {
            string neighborId = neighbor.Entry.Id;
            float boost = PhysicsEngine.ComputeSpreadingEnergy(energy, neighbor.Edge.Relation, nodeDegree);

            if (boost < MinPropagationThreshold)
                continue;

            // Accumulate boosts (a node reachable via multiple paths gets combined energy)
            if (boosted.TryGetValue(neighborId, out float existing))
                boosted[neighborId] = existing + boost;
            else
                boosted[neighborId] = boost;

            // Recursive spread at reduced energy
            PropagateGraph(neighborId, boost * RecursiveDecay, depth + 1, boosted);
        }
    }

    /// <summary>
    /// Cluster-based pre-warming: accessing any member activates cluster summary and top peers.
    /// </summary>
    private void PropagateCluster(string id, float baseEnergy, Dictionary<string, float> boosted)
    {
        var clusterIds = _clusters.GetClustersForEntry(id);

        foreach (var clusterId in clusterIds)
        {
            var clusterInfo = _clusters.GetCluster(clusterId);
            if (clusterInfo is null) continue;

            // Boost cluster summary node (full boost)
            if (clusterInfo.SummaryEntry is not null)
            {
                var summaryId = clusterInfo.SummaryEntry.Id;
                if (summaryId != id)
                    Accumulate(boosted, summaryId, baseEnergy * ClusterSummaryBoost);
            }

            // Boost top-N highest-energy cluster peers (50% boost)
            int peerCount = 0;
            foreach (var member in clusterInfo.Members)
            {
                if (member.Id == id) continue;
                if (peerCount >= MaxClusterPeers) break;

                Accumulate(boosted, member.Id, baseEnergy * ClusterPeerBoost);
                peerCount++;
            }
        }
    }

    private static void Accumulate(Dictionary<string, float> boosted, string id, float boost)
    {
        if (boosted.TryGetValue(id, out float existing))
            boosted[id] = existing + boost;
        else
            boosted[id] = boost;
    }
}

/// <summary>Result of a spreading activation propagation.</summary>
public sealed record SpreadingResult(
    string SourceId,
    int NodesReached,
    int NodesUpdated,
    float TotalEnergySpread);
