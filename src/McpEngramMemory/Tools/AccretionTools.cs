using System.ComponentModel;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for automated accretion: DBSCAN-based cluster detection and two-phase cooperative collapse.
/// </summary>
[McpServerToolType]
public sealed class AccretionTools
{
    private readonly AccretionScanner _scanner;
    private readonly ClusterManager _clusters;
    private readonly LifecycleEngine _lifecycle;
    private readonly IEmbeddingService _embedding;

    public AccretionTools(AccretionScanner scanner, ClusterManager clusters, LifecycleEngine lifecycle, IEmbeddingService embedding)
    {
        _scanner = scanner;
        _clusters = clusters;
        _lifecycle = lifecycle;
        _embedding = embedding;
    }

    [McpServerTool(Name = "get_pending_collapses")]
    [Description("List dense clusters detected by background accretion scanner awaiting LLM summarization.")]
    public IReadOnlyList<PendingCollapseInfo> GetPendingCollapses(
        [Description("Namespace to check for pending collapses.")] string ns)
    {
        return _scanner.GetPendingCollapses(ns);
    }

    [McpServerTool(Name = "collapse_cluster")]
    [Description("Execute a pending collapse: store LLM-generated summary, archive original members, create cluster.")]
    public string CollapseCluster(
        [Description("The pending collapse ID to execute.")] string collapseId,
        [Description("LLM-generated summary text for the cluster.")] string summaryText,
        [Description("Embedding vector of the summary text.")] float[]? summaryVector = null)
    {
        var resolved = summaryVector is not null && summaryVector.Length > 0
            ? summaryVector
            : _embedding.Embed(summaryText);

        return _scanner.ExecuteCollapse(collapseId, summaryText, resolved, _clusters, _lifecycle);
    }

    [McpServerTool(Name = "dismiss_collapse")]
    [Description("Dismiss a pending collapse and exclude its members from future accretion scans.")]
    public string DismissCollapse(
        [Description("The pending collapse ID to dismiss.")] string collapseId)
    {
        return _scanner.DismissCollapse(collapseId);
    }

    [McpServerTool(Name = "trigger_accretion_scan")]
    [Description("Manually trigger an accretion scan for a namespace. Scans LTM-tier entries for dense clusters using DBSCAN.")]
    public AccretionScanResult TriggerAccretionScan(
        [Description("Namespace to scan.")] string ns,
        [Description("DBSCAN distance threshold (default: 0.15). Lower values require tighter clusters.")] float epsilon = 0.15f,
        [Description("DBSCAN minimum cluster size (default: 3).")] int minPoints = 3)
    {
        return _scanner.ScanNamespace(ns, epsilon, minPoints);
    }
}
