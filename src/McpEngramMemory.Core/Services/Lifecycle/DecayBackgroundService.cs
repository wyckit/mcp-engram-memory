using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Lifecycle;

/// <summary>
/// Background service that runs decay cycles on all namespaces at a regular interval.
/// </summary>
public sealed class DecayBackgroundService : BackgroundService
{
    private readonly LifecycleEngine _lifecycle;
    private readonly IBackgroundWorkerStatusTracker? _statusTracker;
    private readonly ILogger<DecayBackgroundService> _logger;

    /// <summary>Default interval between decay cycles.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);

    /// <summary>Configurable interval (for testing).</summary>
    public TimeSpan Interval { get; set; } = DefaultInterval;

    public DecayBackgroundService(
        LifecycleEngine lifecycle,
        ILogger<DecayBackgroundService> logger,
        IBackgroundWorkerStatusTracker? statusTracker = null)
    {
        _lifecycle = lifecycle;
        _logger = logger;
        _statusTracker = statusTracker;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Decay background service started (interval: {Interval})", Interval);

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
            long entriesProcessed = 0;
            var sw = Stopwatch.StartNew();
            try
            {
                var result = _lifecycle.RunDecayCycle("*", useStoredConfig: true);
                sw.Stop();
                entriesProcessed = result.ProcessedCount;
                int stateChanges = result.StmToLtm + result.LtmToArchived;
                _logger.LogInformation(
                    "Maintenance cycle: worker={Worker} namespace={Namespace} durationMs={DurationMs} entriesProcessed={EntriesProcessed} statesChanged={StatesChanged}",
                    "decay", "*", sw.ElapsedMilliseconds, entriesProcessed, stateChanges);
            }
            catch (Exception ex)
            {
                sw.Stop();
                errorMessage = ex.Message;
                _logger.LogError(ex, "Error during decay cycle");
            }
            _statusTracker?.RecordCycle("decay", DateTime.UtcNow, sw.ElapsedMilliseconds, entriesProcessed, errorMessage);
        }

        _logger.LogInformation("Decay background service stopped");
    }
}
