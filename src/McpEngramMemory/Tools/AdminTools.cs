using System.ComponentModel;
using System.Text.Json.Serialization;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for inspection: get_memory, cognitive_stats, purge_debates, and engram_status.
/// </summary>
[McpServerToolType]
public sealed class AdminTools
{
    /// <summary>Default cap on the namespace list returned by cognitive_stats.</summary>
    private const int DefaultNamespaceLimit = 100;

    /// <summary>Default cap on the per-namespace detail returned by purge_debates.</summary>
    private const int DefaultDetailLimit = 25;

    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly IStorageProvider _storage;
    private readonly IBackgroundWorkerStatusTracker? _statusTracker;
    private readonly NamespaceRegistry _registry;
    private readonly AgentIdentity _agent;

    public AdminTools(CognitiveIndex index, KnowledgeGraph graph, ClusterManager clusters, IStorageProvider storage,
        NamespaceRegistry registry, AgentIdentity agent,
        IBackgroundWorkerStatusTracker? statusTracker = null)
    {
        _index = index;
        _graph = graph;
        _clusters = clusters;
        _storage = storage;
        _registry = registry;
        _agent = agent;
        _statusTracker = statusTracker;
    }

    private bool CanRead(string ns) => _registry.HasAccess(_agent.AgentId, ns);

    [McpServerTool(Name = "get_memory")]
    [Description("Look up one memory's full metadata — lifecycle state, graph edges, cluster memberships, access count — without triggering an access-count increment. Don't use it to search by topic; use `recall` or `search_memory` for that.")]
    public object GetMemory(
        [Description("Entry ID.")] string id)
    {
        var entry = _index.Get(id);
        if (entry is null)
            return $"Entry '{id}' not found.";

        // get_memory resolves by id across every namespace, so without this it hands back the
        // full text and metadata of any entry whose id a caller can guess or has seen in a
        // graph edge. Same reply as a genuine miss - a distinct denial would confirm the id.
        if (!CanRead(entry.Ns))
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
    [Description("Check how many memories exist across lifecycle states (STM/LTM/archived), plus cluster and edge counts and the namespace list. The namespace list is capped — raise namespaceLimit or pass 0 for all. Don't use it to check background worker health; use `engram_status` for that.")]
    public LifecycleStats CognitiveStats(
        [Description("Namespace ('*' for all, default).")] string ns = "*",
        [Description("Maximum namespaces to list (default: 100). Use 0 for no cap. Counts are always exact regardless of this limit.")] int namespaceLimit = DefaultNamespaceLimit)
    {
        var (stm, ltm, archived) = _index.GetStateCounts(ns);
        // Namespace names alone can be sensitive (project or client names) and are a stepping
        // stone to targeting other tools by name, so list only what this caller may read.
        // Counts stay store-wide: they are aggregates and disclose nothing specific.
        var namespaces = _index.GetNamespaces().Where(CanRead).ToList();
        var edgeCount = _graph.EdgeCount;
        var clusterCount = _clusters.ClusterCount;

        // A store with hundreds of namespaces returns a list that dominates the caller's
        // context window for no benefit — the counts are what the tool is usually asked for.
        // Cap the list, but never the counts, and say plainly that it was capped.
        var totalNamespaces = namespaces.Count;
        var listed = namespaceLimit > 0 && totalNamespaces > namespaceLimit
            ? namespaces.Take(namespaceLimit).Append($"… +{totalNamespaces - namespaceLimit} more (pass namespaceLimit=0 to list all)").ToList()
            : namespaces;

        return new LifecycleStats(
            stm + ltm + archived,
            stm, ltm, archived,
            clusterCount, edgeCount,
            listed);
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
        [Description("If true, only list what would be purged without deleting (default: true).")] bool dryRun = true,
        [Description("Maximum namespaces to detail in the response (default: 25). Use 0 for all. Totals are always exact regardless of this limit.")] int detailLimit = DefaultDetailLimit)
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
                // Dry run: count the DISTINCT edges that would be removed. Summing
                // GetEdgesForEntry over every entry double-counts any edge whose endpoints
                // both live in this namespace — which is most of them, since debate
                // namespaces are internally linked. That made the dry run report roughly
                // twice the edges the real purge removes, on the one operation whose whole
                // job is to let you check before deleting.
                var distinct = new HashSet<(string, string, string)>();
                foreach (var entry in entries)
                    foreach (var edge in _graph.GetEdgesForEntry(entry.Id))
                        distinct.Add((edge.SourceId, edge.TargetId, edge.Relation));
                edgesRemoved = distinct.Count;
            }

            totalEntriesRemoved += entryCount;
            totalEdgesRemoved += edgesRemoved;
            purged.Add(new PurgedNamespaceInfo(debateNs, entryCount, edgesRemoved, newestEntry.CreatedAt));
        }

        // A real purge can span hundreds of namespaces; returning every one in full detail
        // buries the totals that actually drive the go/no-go decision. Cap the detail,
        // never the totals.
        var detail = detailLimit > 0 && purged.Count > detailLimit
            ? purged.Take(detailLimit).ToList()
            : purged;

        return new PurgeDebatesResult(
            purged.Count, totalEntriesRemoved, totalEdgesRemoved, dryRun, detail);
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
