using System.ComponentModel;
using McpEngramMemory.Core.Services.Graph;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools that expose the per-namespace graph-Laplacian eigenbasis maintained by
/// <see cref="GraphLaplacianSpine"/>. Read-only diagnostics plus an explicit invalidation
/// hook; the basis is otherwise computed lazily by consumers (decay, spectral retrieval).
/// </summary>
[McpServerToolType]
public sealed class LaplacianTools
{
    private readonly GraphLaplacianSpine _spine;

    public LaplacianTools(GraphLaplacianSpine spine)
    {
        _spine = spine;
    }

    [McpServerTool(Name = "compute_laplacian_basis")]
    [Description("Force computation of the top-K eigenbasis of the memory-graph normalized Laplacian for a namespace. Returns diagnostics; the basis itself is held in-memory by the server. Returns null if the namespace is below the spectral-method threshold (32 nodes / 8 positive-relation edges).")]
    public LaplacianStats? ComputeLaplacianBasis(
        [Description("Namespace to compute the basis for.")] string ns,
        [Description("Number of eigenpairs to retain (default 96). Higher = finer multi-scale resolution at higher compute cost.")] int topK = GraphLaplacianSpine.DefaultTopK,
        [Description("Drop any cached basis and recompute from scratch.")] bool force = false)
    {
        if (force) _spine.Invalidate(ns);
        _ = _spine.GetBasis(ns, topK);
        return _spine.GetStats(ns);
    }

    [McpServerTool(Name = "laplacian_stats")]
    [Description("Diagnostics for the cached graph-Laplacian eigenbasis of a namespace, without forcing recomputation if absent.")]
    public LaplacianStats? LaplacianStats(
        [Description("Namespace to inspect.")] string ns) => _spine.GetStats(ns);

    [McpServerTool(Name = "invalidate_laplacian")]
    [Description("Drop the cached graph-Laplacian eigenbasis for a namespace. Use after manual graph surgery or if you suspect drift.")]
    public string InvalidateLaplacian(
        [Description("Namespace to invalidate.")] string ns)
    {
        _spine.Invalidate(ns);
        return $"Invalidated Laplacian basis for namespace '{ns}'.";
    }
}
