using McpEngramMemory.Core.Services.Lifecycle;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Graph;

/// <summary>
/// Periodic background scan that runs <see cref="AutoLinkScanner.Scan"/> across
/// every non-system namespace at a 6-hour cadence. Reads each namespace's
/// <see cref="DecayConfig"/> for its threshold and edge cap, and respects
/// <see cref="DecayConfig.EnableAutoLink"/> for opt-out.
///
/// Schedule rationale: edge structure changes far more slowly than activation
/// does, so 6 hours is plenty often. The first pass starts 15 minutes after
/// service start to let the embedding warmup, accretion scanner, and diffusion
/// kernel warmup all settle.
/// </summary>
public sealed class AutoLinkBackgroundService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly AutoLinkScanner _scanner;
    private readonly CognitiveIndex _index;
    private readonly LifecycleEngine _lifecycle;
    private readonly ILogger<AutoLinkBackgroundService>? _logger;

    public AutoLinkBackgroundService(
        AutoLinkScanner scanner,
        CognitiveIndex index,
        LifecycleEngine lifecycle,
        ILogger<AutoLinkBackgroundService>? logger = null)
    {
        _scanner = scanner;
        _index = index;
        _lifecycle = lifecycle;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { ScanAllNamespaces(); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger?.LogError(ex, "Auto-link background pass failed; will retry on next interval.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void ScanAllNamespaces()
    {
        var namespaces = _index.GetNamespaces();
        int totalCreated = 0;
        int scannedCount = 0;
        int skippedCount = 0;

        foreach (var ns in namespaces)
        {
            // Skip system namespaces (sharing registry, etc.).
            if (ns.StartsWith('_')) { skippedCount++; continue; }

            var config = _lifecycle.GetDecayConfig(ns);
            // No stored config means defaults — auto-link is on by default.
            if (config is not null && !config.EnableAutoLink)
            {
                skippedCount++;
                continue;
            }

            float threshold = config?.AutoLinkSimilarityThreshold ?? 0.85f;
            int cap = config?.AutoLinkMaxNewEdgesPerScan ?? 1000;

            try
            {
                var result = _scanner.Scan(ns, threshold, cap);
                scannedCount++;
                totalCreated += result.EdgesCreated;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Auto-link scan failed for ns={Namespace}; continuing.", ns);
            }
        }

        _logger?.LogInformation(
            "Auto-link sweep: {Total} new similar_to edges across {Scanned} namespaces ({Skipped} skipped).",
            totalCreated, scannedCount, skippedCount);
    }
}
