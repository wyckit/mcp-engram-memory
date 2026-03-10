namespace McpEngramMemory.Core.Models;

/// <summary>
/// Configurable memory limits to prevent unbounded growth.
/// </summary>
public sealed record MemoryLimitsConfig(
    int MaxNamespaceSize = int.MaxValue,
    int MaxTotalCount = int.MaxValue);
