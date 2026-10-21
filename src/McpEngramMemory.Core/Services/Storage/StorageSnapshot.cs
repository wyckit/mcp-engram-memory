using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Storage;

internal static class StorageSnapshot
{
    // Capture outside the scheduling lock: providers can take index/graph/cluster
    // locks held by callers that must still be able to schedule entry persistence.
    // Keep exceptions inside the timer boundary so the caller can retain the save.
    internal static bool TryCapture<T>(Func<T> provider, ILogger? logger, out T? snapshot)
        where T : class
    {
        try
        {
            snapshot = provider();
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to capture pending storage snapshot");
            snapshot = null;
            return false;
        }
    }
}
