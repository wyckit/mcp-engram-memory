using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Lifecycle;

/// <summary>
/// Background service that runs decay cycles on all namespaces at a regular interval.
/// </summary>
public sealed class DecayBackgroundService : BackgroundService
{
    private readonly LifecycleEngine _lifecycle;
    private readonly ILogger<DecayBackgroundService> _logger;

    /// <summary>Default interval between decay cycles.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);

    /// <summary>Configurable interval (for testing).</summary>
    public TimeSpan Interval { get; set; } = DefaultInterval;

    public DecayBackgroundService(LifecycleEngine lifecycle, ILogger<DecayBackgroundService> logger)
    {
        _lifecycle = lifecycle;
        _logger = logger;
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

            try
            {
                var result = _lifecycle.RunDecayCycle("*", useStoredConfig: true);
                _logger.LogInformation(
                    "Decay cycle completed: {Processed} processed, {StmToLtm} STM->LTM, {LtmToArchived} LTM->Archived",
                    result.ProcessedCount, result.StmToLtm, result.LtmToArchived);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during decay cycle");
            }
        }

        _logger.LogInformation("Decay background service stopped");
    }
}
