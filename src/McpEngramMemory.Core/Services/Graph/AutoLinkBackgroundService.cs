using System.Diagnostics;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Lifecycle;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Graph;

/// <summary>
/// Periodic background scan that runs <see cref="AutoLinkScanner.Scan"/> across
/// every non-system namespace at a 6-hour cadence. Reads each namespace's
/// <see cref="Models.DecayConfig"/> for its threshold and edge cap, and respects
/// <see cref="Models.DecayConfig.EnableAutoLink"/> for opt-out.
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
    private readonly IBackgroundWorkerStatusTracker? _statusTracker;
    private readonly ILogger<AutoLinkBackgroundService>? _logger;

    public AutoLinkBackgroundService(
        AutoLinkScanner scanner,
        CognitiveIndex index,
        LifecycleEngine lifecycle,
        ILogger<AutoLinkBackgroundService>? logger = null,
        IBackgroundWorkerStatusTracker? statusTracker = null)
    {
        _scanner = scanner;
        _index = index;
        _lifecycle = lifecycle;
        _logger = logger;
        _statusTracker = statusTracker;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            string? errorMessage = null;
            long totalEntriesProcessed = 0;
            var swTotal = Stopwatch.StartNew();
            try { totalEntriesProcessed = ScanAllNamespaces(stoppingToken, out errorMessage); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                swTotal.Stop();
                errorMessage = ex.Message;
                _logger?.LogError(ex, "Auto-link background pass failed; will retry on next interval.");
            }
            swTotal.Stop();
            _statusTracker?.RecordCycle("auto_link", DateTime.UtcNow, swTotal.ElapsedMilliseconds, totalEntriesProcessed, errorMessage);

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    // The token reaches the scanner rather than only gating the loop between namespaces: one
    // namespace at the entry cap is a multi-second pairwise walk, and a shutdown that has to wait
    // for it holds the whole host up. A cancelled scan writes what it had already ranked and leaves
    // its resume cursor where it was, so nothing is skipped when the process comes back.
    private long ScanAllNamespaces(CancellationToken cancellationToken, out string? partialFailure)
    {
        int totalCreated = 0;
        int scannedCount = 0;
        int skippedCount = 0;
        int tenantsSkippedOnEnumeration = 0;
        int namespacesFailedToScan = 0;
        long totalEntriesProcessed = 0;

        // Scan every tenant's namespaces (the legacy tenant "" is included when present).
        foreach (var tenant in _index.GetAllTenants())
        {
            // The inner loop's break only escapes the inner loop; without this, a shutdown
            // mid-sweep still walks every remaining tenant's enumeration before returning.
            if (cancellationToken.IsCancellationRequested) break;

            // Namespace enumeration fails closed, so one tenant's failing listing must be
            // contained here — unguarded, a single NamespaceEnumerationException unwinds the
            // whole sweep and starves auto-link for every tenant, every cycle. Accretion and
            // diffusion warmup wrap this same call per tenant for the same reason.
            IReadOnlyList<string> tenantNamespaces;
            try { tenantNamespaces = _index.GetNamespaces(tenant); }
            catch (Exception ex) when (ex is NamespaceEnumerationException or ArgumentException)
            {
                // ArgumentException: GetAllTenants() can surface a stored pre-validation
                // tenant that the validating normalize inside GetNamespaces rejects — see
                // LifecycleEngine.RunDecayCycle's guard; contained per tenant, same as there.
                tenantsSkippedOnEnumeration++;
                _logger?.LogWarning(ex, "Auto-link sweep could not enumerate namespaces for a tenant; skipping it this pass.");
                continue;
            }

            foreach (var ns in tenantNamespaces)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Skip system namespaces (sharing registry, etc.).
            if (ns.StartsWith('_')) { skippedCount++; continue; }

            // Contained per NAMESPACE, like the scan itself below: a stored pre-validation
            // namespace can carry a control character the config lookup's validating
            // PartitionKey rejects, and an escaped ArgumentException here sits outside the
            // per-namespace try — it would unwind the whole sweep and starve auto-link for
            // every tenant and namespace ordered after the poisoned one, every cycle.
            DecayConfig? config;
            try { config = _lifecycle.GetDecayConfig(ns, tenantId: tenant); }
            catch (ArgumentException ex)
            {
                namespacesFailedToScan++;
                _logger?.LogWarning(ex, "Auto-link sweep skipped a namespace whose stored name fails partition validation.");
                continue;
            }
            // No stored config means defaults — auto-link is on by default.
            if (config is not null && !config.EnableAutoLink)
            {
                skippedCount++;
                continue;
            }

            float threshold = config?.AutoLinkSimilarityThreshold ?? 0.85f;
            int cap = config?.AutoLinkMaxNewEdgesPerScan ?? 1000;

            var sw = Stopwatch.StartNew();
            try
            {
                var result = _scanner.Scan(ns, threshold, cap, tenantId: tenant, cancellationToken: cancellationToken);
                sw.Stop();
                scannedCount++;
                totalCreated += result.EdgesCreated;
                totalEntriesProcessed += result.ScannedEntries;
                _logger?.LogInformation(
                    "Maintenance cycle: worker={Worker} namespace={Namespace} durationMs={DurationMs} entriesProcessed={EntriesProcessed} edgesCreated={EdgesCreated}",
                    "auto_link", ns, sw.ElapsedMilliseconds, result.ScannedEntries, result.EdgesCreated);
            }
            catch (Exception ex)
            {
                sw.Stop();
                namespacesFailedToScan++;
                _logger?.LogWarning(ex, "Auto-link scan failed for ns={Namespace}; continuing.", ns);
            }
        }
        }

        _logger?.LogInformation(
            "Auto-link sweep: {Total} new similar_to edges across {Scanned} namespaces ({Skipped} skipped).",
            totalCreated, scannedCount, skippedCount);

        // A skipped tenant or a failed namespace scan is a PARTIAL pass, and the cycle record
        // must say so: with a null error, engram_status shows a healthy completed sweep that
        // silently covered nothing for the affected partitions, every cycle, for as long as the
        // failure persists.
        var failures = new List<string>(3);
        if (tenantsSkippedOnEnumeration > 0)
            failures.Add($"namespace enumeration failed for {tenantsSkippedOnEnumeration} tenant(s)");
        if (namespacesFailedToScan > 0)
            failures.Add($"the scan failed for {namespacesFailedToScan} namespace(s)");
        // A cancelled sweep exited its loops early; recording it with a null error would show a
        // healthy completed cycle that silently skipped every remaining partition.
        if (cancellationToken.IsCancellationRequested)
            failures.Add("the sweep was cancelled before completion");
        partialFailure = failures.Count > 0
            ? $"Partial pass: {string.Join(" and ", failures)}; the affected partitions were skipped."
            : null;

        return totalEntriesProcessed;
    }
}
