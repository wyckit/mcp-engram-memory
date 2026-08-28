using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Graph;

/// <summary>
/// Background service that pre-warms the diffusion basis cache so the first decay
/// cycle after startup doesn't pay the eigendecomposition cost on the foreground
/// path. Sweeps every qualifying <c>(tenant, namespace)</c> partition on a periodic
/// interval; each call to <see cref="MemoryDiffusionKernel.GetBasis"/> builds and
/// caches if missing or stale, otherwise no-ops.
///
/// Why the sweep is per-tenant: a basis is cached under the <c>(tenant, ns)</c>
/// partition key, so warming a namespace under one tenant does nothing for the same
/// namespace under another. Enumerating namespaces without a tenant and warming them
/// all as the legacy tenant "" therefore warmed exactly one partition per namespace —
/// the wrong one for every identified tenant, which then paid a foreground
/// eigendecomposition on its first request.
///
/// I/O cost: <see cref="CognitiveIndex.GetAllTenants"/> and the per-tenant
/// <see cref="CognitiveIndex.GetNamespaces(string)"/> each force a full
/// <c>NamespaceStore.LoadAll</c>, which the old no-tenant <c>GetNamespaces()</c> did
/// not. That is inherent, not incidental: an unloaded namespace's tenant set is
/// unknowable without reading it, so complete tenant discovery cannot be cheaper than
/// a full load. Loading is idempotent per namespace, so the disk cost is paid once per
/// process and later sweeps read memory. It runs on a background thread after
/// <see cref="StartupDelay"/> and blocks nothing on the startup path.
///
/// Tuning: a 30-minute refresh interval is generous — bases only need rebuild
/// when graph topology changes (revision counter), and rebuild happens lazily
/// at first read regardless. This service exists to amortize the *first* hit
/// per process lifetime, plus catch new namespaces that crossed the
/// qualification threshold since the last sweep.
/// </summary>
public sealed class DiffusionKernelWarmupService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

    /// <summary>Maximum tenant names spelled out in the failure line before truncating to "+N more".</summary>
    private const int MaxTenantsShown = 5;

    private readonly MemoryDiffusionKernel _kernel;
    private readonly CognitiveIndex _index;
    private readonly ILogger<DiffusionKernelWarmupService>? _logger;

    public DiffusionKernelWarmupService(
        MemoryDiffusionKernel kernel,
        CognitiveIndex index,
        ILogger<DiffusionKernelWarmupService>? logger = null)
    {
        _kernel = kernel;
        _index = index;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                WarmAllQualifyingPartitions(stoppingToken);
            }
            catch (Exception ex)
            {
                // Shutdown is not a fault. The per-partition guards inside the sweep
                // deliberately decline to swallow once the token trips, so whatever was
                // in flight at teardown surfaces here; exiting quietly is what keeps
                // cancellation from being reported as a warmup failure.
                if (stoppingToken.IsCancellationRequested) return;
                _logger?.LogError(ex, "Diffusion kernel warmup pass failed; will retry on next interval.");
            }

            try { await Task.Delay(RefreshInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Run one full warmup sweep across every tenant's qualifying namespaces.
    /// <see cref="ExecuteAsync"/> is the only production caller. Internal rather than public
    /// so a test can drive a single deterministic pass — asserting on what the kernel was
    /// asked to compute instead of waiting out <see cref="StartupDelay"/> and racing the
    /// hosted loop — without the seam becoming part of this package's public surface: this
    /// assembly ships on NuGet, and a consumer calling this directly would take a full
    /// synchronous store load on its own thread. Throws
    /// <see cref="OperationCanceledException"/> if <paramref name="stoppingToken"/> trips
    /// mid-sweep.
    /// </summary>
    internal void WarmAllQualifyingPartitions(CancellationToken stoppingToken)
    {
        int warmed = 0;
        int bypassed = 0;
        int failed = 0;
        int attempted = 0;
        int tenantCount = 0;
        var failedTenants = new List<string>();
        var sw = Stopwatch.StartNew();

        // Every tenant, including the legacy tenant "" when legacy data is present.
        foreach (var tenant in _index.GetAllTenants())
        {
            stoppingToken.ThrowIfCancellationRequested();
            tenantCount++;

            IReadOnlyList<string> namespaces;
            try
            {
                namespaces = _index.GetNamespaces(tenant);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The isolation boundary stays on the partition even though a tenant loop
                // now wraps the namespace loop. Per-tenant enumeration hits storage in its
                // own right, so leaving it outside the guards would let one unreadable
                // tenant unwind the sweep and permanently starve every tenant ordered
                // after it — the failure is deterministic, so the next cycle dies in the
                // same place.
                failed++;
                attempted++;
                AddFailedTenant(failedTenants, tenant);
                _logger?.LogWarning(ex,
                    "Diffusion warmup namespace enumeration failed for tenant='{Tenant}'; continuing.", tenant);
                continue;
            }

            foreach (var ns in namespaces)
            {
                stoppingToken.ThrowIfCancellationRequested();

                // Skip system / internal namespaces — anything starting with underscore.
                if (ns.StartsWith('_')) continue;

                attempted++;

                // Per-partition fault isolation: one failing basis computation must
                // not abort warmup for every later partition. The kernel negative-caches
                // the failure per graph revision, so subsequent sweeps rethrow cheaply.
                try
                {
                    var basis = _kernel.GetBasis(ns, tenantId: tenant);
                    if (basis is not null) warmed++;
                    else bypassed++;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    failed++;
                    AddFailedTenant(failedTenants, tenant);
                    _logger?.LogWarning(ex,
                        "Diffusion warmup failed for tenant='{Tenant}' ns={Namespace}; continuing.", tenant, ns);
                }
            }
        }

        sw.Stop();

        // Counts, never a tenant roster: a deployment can hold thousands of tenants, and a
        // healthy sweep would put every one of them into the Information stream every 30
        // minutes. Identity is still recoverable — each per-partition warning above names its
        // tenant and namespace — and the failing tenants are named below, truncated, only
        // when there is something to name.
        _logger?.LogInformation(
            "Diffusion warmup: {Warmed} of {Attempted} partitions hold a basis across {Tenants} tenant(s) ({Bypassed} bypassed as too-small/sparse, {Failed} failed across {FailedTenants} tenant(s)) in {Ms}ms.",
            warmed, attempted, tenantCount, bypassed, failed, failedTenants.Count, sw.ElapsedMilliseconds);

        if (failedTenants.Count > 0)
        {
            _logger?.LogInformation(
                "Diffusion warmup failures affected tenants: {Tenants}.",
                DescribeFailedTenants(failedTenants));
        }
    }

    /// <summary>
    /// Record <paramref name="tenant"/> as having at least one failed partition this sweep.
    /// Distinct-on-insert keeps the count a tenant count rather than a partition count; the
    /// list is short by construction in any healthy deployment.
    /// </summary>
    private static void AddFailedTenant(List<string> failedTenants, string tenant)
    {
        string label = DescribeTenant(tenant);
        if (!failedTenants.Contains(label, StringComparer.Ordinal))
            failedTenants.Add(label);
    }

    /// <summary>
    /// Labels a tenant for telemetry only. The legacy tenant is the empty string, which would
    /// render as nothing at all and read as a formatting bug rather than as the legacy
    /// partition, so it is named explicitly. Display only — nothing branches on tenant
    /// emptiness to decide what gets warmed, because "" is a real partition and not a wildcard.
    /// </summary>
    private static string DescribeTenant(string tenant) =>
        tenant.Length == 0 ? "(legacy)" : tenant;

    /// <summary>
    /// Comma-joined failing tenants, truncated after the first five so a mass failure cannot
    /// emit an unbounded log line. Matches the truncation in
    /// <see cref="McpEngramMemory.Core.Services.Intelligence.AccretionBackgroundService"/>.
    /// </summary>
    private static string DescribeFailedTenants(IReadOnlyList<string> failedTenants) =>
        failedTenants.Count <= MaxTenantsShown
            ? string.Join(", ", failedTenants)
            : $"{string.Join(", ", failedTenants.Take(MaxTenantsShown))}, +{failedTenants.Count - MaxTenantsShown} more";
}
