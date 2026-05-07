using System.ComponentModel;
using System.Text.Json.Serialization;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for inspection: get_memory, cognitive_stats, purge_debates, and engram_status.
/// </summary>
[McpServerToolType]
public sealed class AdminTools
{
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly IStorageProvider _storage;
    private readonly IBackgroundWorkerStatusTracker? _statusTracker;

    public AdminTools(CognitiveIndex index, KnowledgeGraph graph, ClusterManager clusters, IStorageProvider storage,
        IBackgroundWorkerStatusTracker? statusTracker = null)
    {
        _index = index;
        _graph = graph;
        _clusters = clusters;
        _storage = storage;
        _statusTracker = statusTracker;
    }

    [McpServerTool(Name = "get_memory")]
    [Description("Look up one memory's full metadata — lifecycle state, graph edges, cluster memberships, access count — without triggering an access-count increment. Don't use it to search by topic; use `recall` or `search_memory` for that.")]
    public object GetMemory(
        [Description("Entry ID.")] string id)
    {
        var entry = _index.Get(id);
        if (entry is null)
            return $"Entry '{id}' not found.";

        var edges = _graph.GetEdgesForEntry(id);
        var clusterIds = _clusters.GetClustersForEntry(id);

        return new GetMemoryResult(
            new CognitiveEntryInfo(entry.Id, entry.Text, entry.Ns, entry.Category, entry.LifecycleState),
            entry.Text,
            entry.Metadata,
            entry.LifecycleState,
            entry.ActivationEnergy,
            entry.AccessCount,
            entry.CreatedAt,
            entry.LastAccessedAt,
            edges,
            clusterIds);
    }

    [McpServerTool(Name = "cognitive_stats")]
    [Description("Check how many memories exist across lifecycle states (STM/LTM/archived), plus cluster and edge counts and the full namespace list. Don't use it to check background worker health; use `engram_status` for that.")]
    public LifecycleStats CognitiveStats(
        [Description("Namespace ('*' for all, default).")] string ns = "*")
    {
        var (stm, ltm, archived) = _index.GetStateCounts(ns);
        var namespaces = _index.GetNamespaces();
        var edgeCount = _graph.EdgeCount;
        var clusterCount = _clusters.ClusterCount;

        return new LifecycleStats(
            stm + ltm + archived,
            stm, ltm, archived,
            clusterCount, edgeCount,
            namespaces);
    }

    [McpServerTool(Name = "engram_status")]
    [Description("Check the last-run timestamps, cycle counts, and error counts for every background worker (decay, consolidation, diffusion, accretion). Don't use it to see memory counts or namespace lists; use `cognitive_stats` for that.")]
    public EngramStatusOutput EngramStatus()
    {
        return _statusTracker?.GetSnapshot()
            ?? new EngramStatusOutput(
                new EngramWorkerStatus("decay",         null, 0, 0, 0, null),
                new EngramWorkerStatus("consolidation", null, 0, 0, 0, null),
                new EngramWorkerStatus("auto_link",     null, 0, 0, 0, null),
                new EngramWorkerStatus("accretion",     null, 0, 0, 0, null));
    }

    [McpServerTool(Name = "purge_debates")]
    [Description("Clean up stale debate namespaces older than maxAgeHours. Deletes entries, edges, and cluster memberships. Defaults to dry-run mode.")]
    public async Task<object> PurgeDebates(
        [Description("Maximum age in hours before a debate namespace is considered stale (default: 24).")] int maxAgeHours = 24,
        [Description("If true, only list what would be purged without deleting (default: true).")] bool dryRun = true)
    {
        var namespaces = _index.GetNamespaces();
        var debateNamespaces = namespaces
            .Where(n => n.StartsWith("active-debate-"))
            .ToList();

        if (debateNamespaces.Count == 0)
            return new PurgeDebatesResult(0, 0, 0, dryRun, Array.Empty<PurgedNamespaceInfo>());

        var cutoff = DateTimeOffset.UtcNow.AddHours(-maxAgeHours);
        var purged = new List<PurgedNamespaceInfo>();
        int totalEntriesRemoved = 0;
        int totalEdgesRemoved = 0;

        foreach (var debateNs in debateNamespaces)
        {
            var entries = _index.GetAllInNamespace(debateNs);
            if (entries.Count == 0)
            {
                // Empty namespace — always purge
                if (!dryRun)
                {
                    _index.DeleteAllInNamespace(debateNs);
                    await _storage.DeleteNamespaceAsync(debateNs);
                }
                purged.Add(new PurgedNamespaceInfo(debateNs, 0, 0, null));
                continue;
            }

            // Check age using the most recent entry's CreatedAt timestamp
            var newestEntry = entries.MaxBy(e => e.CreatedAt);
            if (newestEntry is null || newestEntry.CreatedAt >= cutoff)
                continue; // Not stale yet

            int entryCount = entries.Count;
            int edgesRemoved = 0;

            if (!dryRun)
            {
                // Cascade: remove graph edges and cluster memberships for each entry
                foreach (var entry in entries)
                {
                    edgesRemoved += _graph.RemoveAllEdgesForEntry(entry.Id);
                    _clusters.RemoveEntryFromAllClusters(entry.Id);
                }

                // Remove entries and namespace from index
                _index.DeleteAllInNamespace(debateNs);
                await _storage.DeleteNamespaceAsync(debateNs);
            }
            else
            {
                // Dry run: count edges that would be removed
                foreach (var entry in entries)
                    edgesRemoved += _graph.GetEdgesForEntry(entry.Id).Count;
            }

            totalEntriesRemoved += entryCount;
            totalEdgesRemoved += edgesRemoved;
            purged.Add(new PurgedNamespaceInfo(debateNs, entryCount, edgesRemoved, newestEntry.CreatedAt));
        }

        return new PurgeDebatesResult(
            purged.Count, totalEntriesRemoved, totalEdgesRemoved, dryRun, purged);
    }
}

public sealed record PurgedNamespaceInfo(
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("entryCount")] int EntryCount,
    [property: JsonPropertyName("edgeCount")] int EdgeCount,
    [property: JsonPropertyName("newestEntryAt")] DateTimeOffset? NewestEntryAt);

public sealed record PurgeDebatesResult(
    [property: JsonPropertyName("namespacesAffected")] int NamespacesAffected,
    [property: JsonPropertyName("totalEntriesRemoved")] int TotalEntriesRemoved,
    [property: JsonPropertyName("totalEdgesRemoved")] int TotalEdgesRemoved,
    [property: JsonPropertyName("dryRun")] bool DryRun,
    [property: JsonPropertyName("namespaces")] IReadOnlyList<PurgedNamespaceInfo> Namespaces);
