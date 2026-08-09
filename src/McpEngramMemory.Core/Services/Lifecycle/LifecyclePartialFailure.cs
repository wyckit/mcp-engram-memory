using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Lifecycle;

/// <summary>
/// Formats per-namespace partial-failure telemetry from
/// <see cref="LifecycleEngine.RunDecayCycle"/> and
/// <see cref="LifecycleEngine.RunConsolidationPass"/> results into the aggregate
/// error message the background services pass to
/// <see cref="IBackgroundWorkerStatusTracker.RecordCycle"/>, so
/// <c>engram_status</c> surfaces partial failures without treating the whole
/// cycle as an error. Namespace lists are truncated after the first five names
/// to keep <see cref="EngramWorkerStatus.LastErrorMessage"/> bounded.
/// </summary>
public static class LifecyclePartialFailure
{
    /// <summary>Maximum namespace names spelled out before truncating to "+N more".</summary>
    private const int MaxNamesShown = 5;

    /// <summary>
    /// Describe partial failures of a decay cycle, or <c>null</c> when the cycle
    /// completed cleanly (no spectral fallbacks, no failed namespaces).
    /// </summary>
    public static string? DescribeDecay(DecayCycleResult result)
    {
        var parts = new List<string>(2);
        if (result.SpectralFallbackNamespaces is { Count: > 0 } fallback)
            parts.Add($"spectral filter failed for {fallback.Count}/{result.TotalNamespaces} namespaces: {FormatList(fallback)} — ran non-spectral fallback");
        if (result.FailedNamespaces is { Count: > 0 } failed)
            parts.Add($"decay pass failed for {failed.Count}/{result.TotalNamespaces} namespaces: {FormatList(failed)} — skipped");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>
    /// Describe partial failures of a consolidation pass, or <c>null</c> when no
    /// namespace failed.
    /// </summary>
    public static string? DescribeConsolidation(ConsolidationResult result)
    {
        if (result.FailedNamespaces is not { Count: > 0 } failed)
            return null;
        int total = result.ProcessedNamespaces + result.SkippedNamespaces + failed.Count;
        return $"consolidation failed for {failed.Count}/{total} namespaces: {FormatList(failed)} — skipped";
    }

    private static string FormatList(IReadOnlyList<string> namespaces)
    {
        if (namespaces.Count <= MaxNamesShown)
            return string.Join(", ", namespaces);
        return $"{string.Join(", ", namespaces.Take(MaxNamesShown))}, +{namespaces.Count - MaxNamesShown} more";
    }
}
