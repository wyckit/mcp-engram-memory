namespace McpVectorMemory;

/// <summary>
/// A snapshot of index diagnostics returned by <see cref="VectorIndex.GetStatistics"/>.
/// </summary>
public sealed record IndexStatistics(
    int EntryCount,
    int PendingDeletions,
    IReadOnlyList<int> Dimensions,
    IReadOnlyDictionary<int, int> EntriesPerDimension,
    bool IsPersistent);
