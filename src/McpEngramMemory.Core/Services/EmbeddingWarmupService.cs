using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services;

public sealed class EmbeddingWarmupService : IHostedService
{
    private readonly IEmbeddingService _embedding;
    private readonly ILogger<EmbeddingWarmupService> _logger;

    public EmbeddingWarmupService(IEmbeddingService embedding, ILogger<EmbeddingWarmupService> logger)
    {
        _embedding = embedding;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Warming up embedding model ({Dimensions}-dim)...", _embedding.Dimensions);
        try
        {
            _embedding.Embed("warmup");
            _logger.LogInformation("Embedding model ready.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding warmup failed. Server will continue; embedding calls may fail until dependencies are available.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
