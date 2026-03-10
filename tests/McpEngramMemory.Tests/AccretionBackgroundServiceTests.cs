using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpEngramMemory.Tests;

public class AccretionBackgroundServiceTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly AccretionScanner _scanner;

    public AccretionBackgroundServiceTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"accretion_bg_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _scanner = new AccretionScanner(_index);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    [Fact]
    public async Task ExecuteAsync_ScansAndDetectsClusters()
    {
        // 4 entries so each has 3 external neighbors (meets default minPoints=3)
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var service = new AccretionBackgroundService(_scanner, _index,
            NullLogger<AccretionBackgroundService>.Instance)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        // Wait for at least one scan cycle
        await Task.Delay(200);

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // The scanner should have detected the cluster
        var pending = _scanner.GetPendingCollapses("test");
        Assert.Single(pending);
        Assert.Equal(4, pending[0].MemberCount);
    }

    [Fact]
    public async Task ExecuteAsync_StopsOnCancellation()
    {
        var service = new AccretionBackgroundService(_scanner, _index,
            NullLogger<AccretionBackgroundService>.Instance)
        {
            Interval = TimeSpan.FromMinutes(60)
        };

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);
        // Should complete without hanging
    }
}
