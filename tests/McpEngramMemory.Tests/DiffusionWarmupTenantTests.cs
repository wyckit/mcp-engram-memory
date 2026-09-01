using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpEngramMemory.Tests;

/// <summary>
/// Tenant coverage and fault isolation for <see cref="DiffusionKernelWarmupService"/>.
///
/// A diffusion basis is cached under its <c>(tenant, ns)</c> partition key, so warming
/// namespace <c>alpha</c> as the legacy tenant does nothing whatsoever for <c>alpha</c>
/// under tenant <c>t1</c>. The service used to enumerate namespaces with the no-tenant
/// overload and warm every one of them as tenant "", which warmed exactly the wrong
/// partition for every identified tenant. These tests pin the per-tenant sweep, and pin
/// that the fault-isolation boundary stayed on the partition when a tenant loop was
/// wrapped around the namespace loop.
///
/// Every assertion is on recorded state — the exact <c>(ns, tenant)</c> pairs the kernel
/// was asked to compute — never on elapsed time. The sweep is driven directly rather
/// than through the hosted loop so there is no startup delay to wait out and no race.
/// </summary>
public class DiffusionWarmupTenantTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;

    public DiffusionWarmupTenantTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"diffusion_warmup_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    // ── tenant coverage (the fix) ───────────────────────────────────────────────

    /// <summary>
    /// Every tenant's namespaces get warmed under their own tenant. Before the fix this
    /// warmed ("alpha", "") and ("beta", "") — namespaces discovered without a tenant and
    /// attributed to the legacy partition — so t1 and t2 held no basis and paid a
    /// foreground eigendecomposition on first use.
    /// </summary>
    [Fact]
    public void WarmAllQualifyingPartitions_WarmsEveryTenantsNamespaces()
    {
        Seed(tenant: "", ns: "legacy-ns");
        Seed(tenant: "t1", ns: "alpha");
        Seed(tenant: "t2", ns: "beta");

        var kernel = new RecordingDiffusionKernel(_index, _graph);
        Sweep(kernel);

        Assert.Contains(("legacy-ns", ""), kernel.Computed);
        Assert.Contains(("alpha", "t1"), kernel.Computed);
        Assert.Contains(("beta", "t2"), kernel.Computed);

        // The old behavior, pinned as a negative: a tenant's namespace must never be warmed
        // under the legacy tenant, which would cache a basis nobody reads while leaving the
        // real partition cold.
        Assert.DoesNotContain(("alpha", ""), kernel.Computed);
        Assert.DoesNotContain(("beta", ""), kernel.Computed);
    }

    // ── fault isolation ─────────────────────────────────────────────────────────

    /// <summary>
    /// A throwing partition must not starve the other namespaces of its own tenant.
    /// Two of the three partitions throw, so an abort at the first failure records fewer
    /// than three computations whatever order the namespaces are enumerated in.
    /// </summary>
    [Fact]
    public void WarmAllQualifyingPartitions_FailingNamespaceDoesNotStarveSiblingsInSameTenant()
    {
        Seed(tenant: "t1", ns: "alpha");
        Seed(tenant: "t1", ns: "beta");
        Seed(tenant: "t1", ns: "gamma");

        var kernel = new RecordingDiffusionKernel(_index, _graph,
            failing: [("alpha", "t1"), ("beta", "t1")]);
        Sweep(kernel);

        Assert.Contains(("gamma", "t1"), kernel.Computed);
        Assert.Equal(3, kernel.Computed.Count);
    }

    /// <summary>
    /// A throwing partition must not starve later tenants either — the boundary is the
    /// partition, not the sweep. Tenant enumeration order is not deterministic, so this
    /// fails a non-isolating implementation regardless of which tenant is visited first:
    /// two of the three tenants throw, so any abort records fewer than three.
    /// </summary>
    [Fact]
    public void WarmAllQualifyingPartitions_FailingTenantDoesNotStarveOtherTenants()
    {
        Seed(tenant: "t1", ns: "alpha");
        Seed(tenant: "t2", ns: "beta");
        Seed(tenant: "t3", ns: "gamma");

        var kernel = new RecordingDiffusionKernel(_index, _graph,
            failing: [("alpha", "t1"), ("beta", "t2")]);
        Sweep(kernel);

        Assert.Contains(("gamma", "t3"), kernel.Computed);
        Assert.Equal(3, kernel.Computed.Count);
    }

    // ── system namespace skip ───────────────────────────────────────────────────

    /// <summary>
    /// The underscore-prefixed system/internal namespace skip survives the per-tenant
    /// sweep, in every tenant rather than only in the legacy one.
    /// </summary>
    [Fact]
    public void WarmAllQualifyingPartitions_SkipsUnderscoreNamespacesInEveryTenant()
    {
        Seed(tenant: "", ns: "_system_sharing");
        Seed(tenant: "t1", ns: "_system_sharing");
        Seed(tenant: "t1", ns: "alpha");

        var kernel = new RecordingDiffusionKernel(_index, _graph);
        Sweep(kernel);

        Assert.Equal(new[] { ("alpha", "t1") }, kernel.Computed);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drive exactly one sweep. <see cref="CancellationToken.None"/> is passed explicitly:
    /// the parameter has no default, so a shutdown-shaped token can never be supplied by
    /// omission.
    /// </summary>
    private void Sweep(MemoryDiffusionKernel kernel)
    {
        var service = new DiffusionKernelWarmupService(
            kernel, _index, NullLogger<DiffusionKernelWarmupService>.Instance);
        service.WarmAllQualifyingPartitions(CancellationToken.None);
    }

    /// <summary>
    /// One entry is enough to make the (tenant, ns) partition non-empty, which is what
    /// makes it discoverable by <c>GetAllTenants</c> / <c>GetNamespaces(tenant)</c>. The
    /// recording kernel never runs the real eigensolver, so no qualifying-size cluster is
    /// needed here.
    /// </summary>
    private void Seed(string tenant, string ns)
        => _index.Upsert(new CognitiveEntry(
            $"{ns}_seed", [1f, 0f], ns, text: $"{ns} seed", tenantId: tenant));

    /// <summary>
    /// Test double recording the exact (ns, tenantId) pairs the sweep asked it to compute,
    /// and throwing for a designated set of partitions the way the eigensolver's
    /// orthonormality guard does in production. Returning <c>null</c> otherwise is the
    /// kernel's legitimate too-small/sparse bypass, so the sweep counts the partition and
    /// moves on without a real eigendecomposition.
    ///
    /// The sweep is single-threaded, so the recording list needs no synchronization.
    /// </summary>
    private sealed class RecordingDiffusionKernel : MemoryDiffusionKernel
    {
        private readonly HashSet<(string Ns, string TenantId)> _failing;
        private readonly List<(string Ns, string TenantId)> _computed = [];

        public IReadOnlyList<(string Ns, string TenantId)> Computed => _computed;

        public RecordingDiffusionKernel(
            CognitiveIndex index,
            KnowledgeGraph graph,
            (string Ns, string TenantId)[]? failing = null)
            : base(index, graph)
        {
            _failing = new HashSet<(string Ns, string TenantId)>(failing ?? []);
        }

        // No default on tenantId: the base signature dropped its fail-open "" default, and a
        // default re-added on an override would re-open that surface for calls made through
        // this static type.
        protected override DiffusionBasis? ComputeBasis(string ns, int topK, long graphRevision, string tenantId)
        {
            _computed.Add((ns, tenantId));
            if (_failing.Contains((ns, tenantId)))
                throw new InvalidOperationException(
                    "Q after final power iteration: column 0 has norm^2 0.5, expected 1.");
            return null;
        }
    }
}
