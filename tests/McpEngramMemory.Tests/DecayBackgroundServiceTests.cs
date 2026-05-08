using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpEngramMemory.Tests;

public class DecayBackgroundServiceTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly LifecycleEngine _lifecycle;

    public DecayBackgroundServiceTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"decay_bg_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _lifecycle = new LifecycleEngine(_index);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    [Fact]
    public async Task ExecuteAsync_RunsDecayCycle()
    {
        // Store an STM entry with high decay parameters so it transitions quickly
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "stm"));

        var service = new DecayBackgroundService(_lifecycle, NullLogger<DecayBackgroundService>.Instance)
        {
            Interval = TimeSpan.FromMilliseconds(50) // Fast interval for testing
        };

        using var cts = new CancellationTokenSource();

        // Start the service
        await service.StartAsync(cts.Token);

        // Poll for the decay cycle to land — at least one cycle must update
        // ActivationEnergy off its default (0). Robust to scheduler jitter under
        // parallel xUnit execution where a fixed-delay-then-assert flakes.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        CognitiveEntry? entry = null;
        while (DateTime.UtcNow < deadline)
        {
            entry = _index.Get("a");
            if (entry is not null && entry.ActivationEnergy != 0f)
                break;
            await Task.Delay(50);
        }

        // Stop the service
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // Verify that decay ran (entry should have updated activation energy)
        Assert.NotNull(entry);
        Assert.NotEqual(0f, entry!.ActivationEnergy);
    }

    [Fact]
    public async Task ExecuteAsync_StopsOnCancellation()
    {
        var service = new DecayBackgroundService(_lifecycle, NullLogger<DecayBackgroundService>.Instance)
        {
            Interval = TimeSpan.FromMinutes(60) // Long interval
        };

        using var cts = new CancellationTokenSource();

        await service.StartAsync(cts.Token);

        // Cancel immediately
        cts.Cancel();

        // Should stop without hanging
        await service.StopAsync(CancellationToken.None);
    }
}
