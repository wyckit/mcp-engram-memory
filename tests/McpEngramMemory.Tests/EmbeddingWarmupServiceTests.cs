using McpEngramMemory.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpEngramMemory.Tests;

public class EmbeddingWarmupServiceTests
{
    private sealed class ThrowingEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 384;
        public float[] Embed(string text) => throw new InvalidOperationException("Embedding unavailable");
    }

    private sealed class SuccessfulEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 2;
        public int Calls { get; private set; }

        public float[] Embed(string text)
        {
            Calls++;
            return [0.1f, 0.2f];
        }
    }

    [Fact]
    public async Task StartAsync_WhenEmbeddingThrows_DoesNotThrow()
    {
        var service = new EmbeddingWarmupService(
            new ThrowingEmbeddingService(),
            NullLogger<EmbeddingWarmupService>.Instance);

        await service.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_WhenEmbeddingAvailable_PerformsWarmupCall()
    {
        var embedding = new SuccessfulEmbeddingService();
        var service = new EmbeddingWarmupService(
            embedding,
            NullLogger<EmbeddingWarmupService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, embedding.Calls);
    }
}
