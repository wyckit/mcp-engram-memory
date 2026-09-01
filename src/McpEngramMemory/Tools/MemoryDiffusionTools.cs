using System.ComponentModel;
using McpEngramMemory.Core.Services.Graph;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools that expose the per-namespace memory-diffusion kernel maintained by
/// <see cref="MemoryDiffusionKernel"/>. Read-only diagnostics plus an explicit
/// invalidation hook; the basis is otherwise computed lazily by consumers (decay
/// diffusion, future spectral retrieval).
/// </summary>
[McpServerToolType]
public sealed class MemoryDiffusionTools
{
    private readonly MemoryDiffusionKernel _kernel;
    private readonly NamespaceAccess _access;

    public MemoryDiffusionTools(MemoryDiffusionKernel kernel, NamespaceAccess access)
    {
        _kernel = kernel;
        _access = access;
    }

    [McpServerTool(Name = "compute_diffusion_basis", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Force computation of the top-K diffusion basis (graph-Laplacian eigenbasis) for a namespace. Returns diagnostics; the basis itself is held in-memory by the server. Returns null if the namespace is below the spectral threshold (32 nodes / 8 positive-relation edges).")]
    public DiffusionStats? ComputeDiffusionBasis(
        [Description("Namespace to compute the basis for.")] string ns,
        [Description("Number of eigenpairs to retain (default 96). Higher = finer multi-scale resolution at higher compute cost.")] int topK = MemoryDiffusionKernel.DefaultTopK,
        [Description("Drop any cached basis and recompute from scratch.")] bool force = false)
    {
        // Nullable return already models "nothing here" (below spectral threshold), so a
        // denied write reuses that same shape rather than a distinct error.
        if (!_access.CanWrite(ns)) return null;

        if (force) _kernel.Invalidate(ns, tenantId: _access.TenantId);
        _ = _kernel.GetBasis(ns, tenantId: _access.TenantId, topK: topK);
        return _kernel.GetStats(ns, tenantId: _access.TenantId);
    }

    [McpServerTool(Name = "diffusion_stats", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Diagnostics for the cached diffusion basis of a namespace, without forcing recomputation if absent.")]
    public DiffusionStats? DiffusionStats(
        [Description("Namespace to inspect.")] string ns) =>
        _access.CanRead(ns) ? _kernel.GetStats(ns, tenantId: _access.TenantId) : null;

    [McpServerTool(Name = "invalidate_diffusion", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Drop the cached diffusion basis for a namespace. Use after manual graph surgery or if you suspect drift.")]
    public string InvalidateDiffusion(
        [Description("Namespace to invalidate.")] string ns)
    {
        if (!_access.CanWrite(ns)) return NamespaceAccess.WriteDenied(ns);

        _kernel.Invalidate(ns, tenantId: _access.TenantId);
        return $"Invalidated diffusion basis for namespace '{ns}'.";
    }
}
