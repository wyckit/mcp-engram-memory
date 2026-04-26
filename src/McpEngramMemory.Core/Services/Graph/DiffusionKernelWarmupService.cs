using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Graph;

/// <summary>
/// Background service that pre-warms the per-namespace diffusion basis cache so
/// the first decay cycle after startup doesn't pay the eigendecomposition cost
/// on the foreground path. Sweeps all qualifying namespaces on a periodic
/// interval; each call to <see cref="MemoryDiffusionKernel.GetBasis"/> builds
/// and caches if missing or stale, otherwise no-ops.
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
                WarmAllQualifyingNamespaces();
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger?.LogError(ex, "Diffusion kernel warmup pass failed; will retry on next interval.");
            }

            try { await Task.Delay(RefreshInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void WarmAllQualifyingNamespaces()
    {
        var namespaces = _index.GetNamespaces();
        int warmed = 0;
        int bypassed = 0;
        var sw = Stopwatch.StartNew();
        foreach (var ns in namespaces)
        {
            // Skip system / internal namespaces — anything starting with underscore.
            if (ns.StartsWith('_')) continue;

            var basis = _kernel.GetBasis(ns);
            if (basis is not null) warmed++;
            else bypassed++;
        }
        sw.Stop();
        _logger?.LogInformation(
            "Diffusion warmup: {Warmed} of {Total} namespaces hold a basis ({Bypassed} bypassed as too-small/sparse) in {Ms}ms.",
            warmed, namespaces.Count, bypassed, sw.ElapsedMilliseconds);
    }
}
