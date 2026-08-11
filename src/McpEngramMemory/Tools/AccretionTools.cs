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
    private readonly NamespaceAccess _access;

    public AccretionTools(AccretionScanner scanner, ClusterManager clusters, LifecycleEngine lifecycle,
        IEmbeddingService embedding, NamespaceAccess access)
    {
        _scanner = scanner;
        _clusters = clusters;
        _lifecycle = lifecycle;
        _embedding = embedding;
        _access = access;
    }

    [McpServerTool(Name = "get_pending_collapses")]
    [Description("List dense clusters awaiting LLM summarization. Check this to find clusters ready for collapse_cluster.")]
    public IReadOnlyList<PendingCollapseInfo> GetPendingCollapses(
        [Description("Namespace to check for pending collapses.")] string ns)
    {
        if (!_access.CanRead(ns)) return Array.Empty<PendingCollapseInfo>();
        return _scanner.GetPendingCollapses(ns);
    }

    [McpServerTool(Name = "collapse_cluster")]
    [Description("Execute a pending collapse: store summary as a searchable entry, archive original members, register the cluster. Reversible via uncollapse_cluster.")]
    public string CollapseCluster(
        [Description("The pending collapse ID to execute.")] string collapseId,
        [Description("LLM-generated summary text for the cluster.")] string summaryText,
        [Description("Embedding vector of the summary text.")] float[]? summaryVector = null)
    {
        // Resolve first: same reply shape as a genuine miss for both "doesn't exist" and
        // "exists but you can't touch it".
        var ns = _scanner.GetPendingCollapseNs(collapseId);
        if (ns is null || !_access.CanWrite(ns))
            return $"Error: Collapse '{collapseId}' not found.";

        var resolved = summaryVector is not null && summaryVector.Length > 0
            ? summaryVector
            : _embedding.Embed(summaryText);

        var result = _scanner.ExecuteCollapse(collapseId, summaryText, resolved, _clusters, _lifecycle);
        if (!result.StartsWith("Error:"))
            _access.ClaimOnWrite(ns);
        return result;
    }

    [McpServerTool(Name = "dismiss_collapse")]
    [Description("Dismiss a pending collapse and exclude its members from future accretion scans.")]
    public string DismissCollapse(
        [Description("The pending collapse ID to dismiss.")] string collapseId)
    {
        var ns = _scanner.GetPendingCollapseNs(collapseId);
        if (ns is null || !_access.CanWrite(ns))
            return $"Error: Collapse '{collapseId}' not found.";

        return _scanner.DismissCollapse(collapseId);
    }

    public AccretionScanResult TriggerAccretionScan(
        [Description("Namespace to scan.")] string ns,
        [Description("DBSCAN distance threshold (default: 0.15). Lower values require tighter clusters.")] float epsilon = 0.15f,
        [Description("DBSCAN minimum cluster size (default: 3).")] int minPoints = 3,
        [Description("Auto-generate extractive summaries for detected clusters without archiving members (default: false).")] bool autoSummarize = false)
    {
        return _scanner.ScanNamespace(ns, epsilon, minPoints,
            autoSummarize, autoSummarize ? _clusters : null, autoSummarize ? _embedding : null);
    }
}
