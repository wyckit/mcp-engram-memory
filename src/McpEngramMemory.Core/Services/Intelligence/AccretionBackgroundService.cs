using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Intelligence;

/// <summary>
/// Background service that periodically scans all namespaces for dense LTM clusters
/// using DBSCAN and creates pending collapses for LLM-driven summarization.
/// </summary>
public sealed class AccretionBackgroundService : BackgroundService
{
    private readonly AccretionScanner _scanner;
    private readonly CognitiveIndex _index;
    private readonly ClusterManager _clusters;
    private readonly IEmbeddingService _embedding;
    private readonly IBackgroundWorkerStatusTracker? _statusTracker;
    private readonly ILogger<AccretionBackgroundService> _logger;

    /// <summary>Default interval between accretion scans.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);

    /// <summary>Configurable interval (for testing).</summary>
    public TimeSpan Interval { get; set; } = DefaultInterval;

    public AccretionBackgroundService(
        AccretionScanner scanner, CognitiveIndex index, ClusterManager clusters,
        IEmbeddingService embedding, ILogger<AccretionBackgroundService> logger,
        IBackgroundWorkerStatusTracker? statusTracker = null)
    {
        _scanner = scanner;
        _index = index;
        _clusters = clusters;
        _embedding = embedding;
        _logger = logger;
        _statusTracker = statusTracker;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Accretion background service started (interval: {Interval})", Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            string? errorMessage = null;
            long totalEntriesProcessed = 0;
            int attemptedPartitions = 0;
            var failedPartitions = new List<string>();
            var swTotal = Stopwatch.StartNew();
            try
            {
                int totalClusters = 0;

                // Scan every tenant's namespaces (the legacy tenant "" is included when present).
                //
                // Fault isolation belongs at the partition, not at the cycle. Once this sweep
                // grew a tenant loop around the namespace loop, a single throwing namespace
                // stopped being a one-namespace outage and became starvation for every tenant
                // ordered after it — the outer catch below unwinds the whole sweep, and the next
                // cycle deterministically dies in the same place. Guarding each scan (and the
                // per-tenant enumeration, which hits storage in its own right) keeps one bad
                // partition local; the failures are counted and surfaced through engram_status
                // rather than silently degrading into a sweep that never reaches most of the data.
                foreach (var tenant in _index.GetAllTenants())
                {
                    IReadOnlyList<string> namespaces;
                    try
                    {
                        namespaces = _index.GetNamespaces(tenant);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        attemptedPartitions++;
                        failedPartitions.Add(DescribePartition(tenant, "*"));
                        _logger.LogWarning(ex,
                            "Accretion namespace enumeration failed for tenant='{Tenant}'; continuing.", tenant);
                        continue;
                    }

                    foreach (var ns in namespaces)
                    {
                        attemptedPartitions++;
                        var sw = Stopwatch.StartNew();
                        try
                        {
                            var result = _scanner.ScanNamespace(ns, tenantId: tenant,
                                autoSummarize: true, clusters: _clusters, embedding: _embedding);
                            sw.Stop();
                            totalClusters += result.ClustersDetected;
                            totalEntriesProcessed += result.ScannedCount;

                            _logger.LogInformation(
                                "Maintenance cycle: worker={Worker} namespace={Namespace} durationMs={DurationMs} entriesProcessed={EntriesProcessed} clustersDetected={ClustersDetected}",
                                "accretion", ns, sw.ElapsedMilliseconds, result.ScannedCount, result.ClustersDetected);
                        }
                        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                        {
                            sw.Stop();
                            failedPartitions.Add(DescribePartition(tenant, ns));
                            _logger.LogWarning(ex,
                                "Accretion scan failed for tenant='{Tenant}' ns={Namespace}; continuing.", tenant, ns);
                        }
                    }
                }

                swTotal.Stop();
                _logger.LogDebug("Accretion scan completed across {Count} namespace(s), {Clusters} total clusters",
                    attemptedPartitions, totalClusters);
            }
            catch (Exception ex)
            {
                // Last-resort net: the per-partition guards above already contain anything a
                // single namespace can throw, so reaching here means the sweep itself failed.
                swTotal.Stop();
                errorMessage = ex.Message;
                _logger.LogError(ex, "Error during accretion scan");
            }

            // Partial failures are not cycle failures, but they must not be invisible either:
            // a partition that starves every cycle looks identical to an idle one from the
            // outside unless the count reaches engram_status.
            var partial = DescribeFailedPartitions(failedPartitions, attemptedPartitions);
            if (partial is not null)
                errorMessage = errorMessage is null ? partial : $"{errorMessage}; {partial}";

            _statusTracker?.RecordCycle("accretion", DateTime.UtcNow, swTotal.ElapsedMilliseconds, totalEntriesProcessed, errorMessage);
        }

        _logger.LogInformation("Accretion background service stopped");
    }

    /// <summary>Maximum partition names spelled out before truncating to "+N more".</summary>
    private const int MaxNamesShown = 5;

    /// <summary>
    /// Labels a partition for telemetry. The legacy tenant is the empty string, which would
    /// render as a bare leading slash and read as a formatting bug rather than as the legacy
    /// partition, so it is named explicitly.
    /// </summary>
    private static string DescribePartition(string tenant, string ns) =>
        tenant.Length == 0 ? $"(legacy)/{ns}" : $"{tenant}/{ns}";

    /// <summary>
    /// Aggregate error text for partitions this cycle skipped, or <c>null</c> when the sweep
    /// reached every one. Names are truncated after the first five so
    /// <c>EngramWorkerStatus.LastErrorMessage</c> stays bounded, matching
    /// <see cref="McpEngramMemory.Core.Services.Lifecycle.LifecyclePartialFailure"/>.
    /// </summary>
    private static string? DescribeFailedPartitions(IReadOnlyList<string> failed, int attempted)
    {
        if (failed.Count == 0)
            return null;

        var names = failed.Count <= MaxNamesShown
            ? string.Join(", ", failed)
            : $"{string.Join(", ", failed.Take(MaxNamesShown))}, +{failed.Count - MaxNamesShown} more";
        return $"accretion scan failed for {failed.Count}/{attempted} partitions: {names} — skipped";
    }
}
