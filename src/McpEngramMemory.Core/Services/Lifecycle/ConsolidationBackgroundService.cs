using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Lifecycle;

/// <summary>
/// Daily background pass that runs <see cref="LifecycleEngine.RunConsolidationPass"/>
/// across every namespace. Topology-driven STM-&gt;LTM promotion (memories with
/// cluster support graduate to long-term) and LTM-&gt;archived archival (memories
/// whose cluster has decayed get retired) without LLM involvement. The biological
/// inspiration is slow-wave sleep: nightly diffusion-driven replay that decides
/// which traces to consolidate and which to fade.
///
/// Tuning. Default cadence is 24 hours, with a 10-minute startup delay so the
/// embedding warmup, accretion scan, and diffusion-kernel warmup all settle first.
/// Skipping is the failure mode of choice — per-namespace isolation lives in
/// <see cref="LifecycleEngine.RunConsolidationPass"/>, which skips a failing
/// namespace whole (recorded in <c>FailedNamespaces</c>) and tries again next
/// day; this service surfaces those partial failures via
/// <see cref="LifecyclePartialFailure.DescribeConsolidation"/>.
/// </summary>
public sealed class ConsolidationBackgroundService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly LifecycleEngine _lifecycle;
    private readonly IBackgroundWorkerStatusTracker? _statusTracker;
    private readonly ILogger<ConsolidationBackgroundService>? _logger;

    public ConsolidationBackgroundService(
        LifecycleEngine lifecycle,
        ILogger<ConsolidationBackgroundService>? logger = null,
        IBackgroundWorkerStatusTracker? statusTracker = null)
    {
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
            long entriesProcessed = 0;
            var sw = Stopwatch.StartNew();
            try
            {
                // Consolidate every tenant partition (legacy "" included when present).
                int promotions = 0, archivals = 0;
                foreach (var tenant in _lifecycle.GetAllTenants())
                {
                    var result = _lifecycle.RunConsolidationPass("*", tenant);
                    entriesProcessed += result.ProcessedEntries;
                    promotions += result.StmToLtm;
                    archivals += result.LtmToArchived;
                    var partial = LifecyclePartialFailure.DescribeConsolidation(result);
                    if (partial is not null)
                    {
                        errorMessage = partial;
                        _logger?.LogWarning("Consolidation pass (tenant='{Tenant}') completed with partial failures: {Message}", tenant, partial);
                    }
                }
                sw.Stop();
                _logger?.LogInformation(
                    "Maintenance cycle: worker={Worker} namespace={Namespace} durationMs={DurationMs} entriesProcessed={EntriesProcessed} promotionsCount={PromotionsCount} archivalsCount={ArchivalsCount}",
                    "consolidation", "*", sw.ElapsedMilliseconds, entriesProcessed, promotions, archivals);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                sw.Stop();
                errorMessage = ex.Message;
                _logger?.LogError(ex, "Consolidation pass failed; will retry on next interval.");
            }
            _statusTracker?.RecordCycle("consolidation", DateTime.UtcNow, sw.ElapsedMilliseconds, entriesProcessed, errorMessage);

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
