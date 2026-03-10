using System.ComponentModel;
using System.Text.Json.Serialization;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for maintenance operations: rebuild embeddings, compression stats.
/// </summary>
[McpServerToolType]
public sealed class MaintenanceTools
{
    private readonly CognitiveIndex _index;
    private readonly IEmbeddingService _embedding;
    private readonly MetricsCollector _metrics;

    public MaintenanceTools(CognitiveIndex index, IEmbeddingService embedding, MetricsCollector metrics)
    {
        _index = index;
        _embedding = embedding;
        _metrics = metrics;
    }

    [McpServerTool(Name = "rebuild_embeddings")]
    [Description("Re-embed all entries in one or all namespaces using the current embedding model. " +
        "Use after upgrading the embedding model to regenerate vectors from stored text. " +
        "Entries without text are skipped. Preserves all metadata, lifecycle state, and timestamps.")]
    public object RebuildEmbeddings(
        [Description("Namespace to rebuild ('*' for all namespaces, default: '*').")] string ns = "*")
    {
        using var timer = _metrics.StartTimer("rebuild_embeddings");

        var namespaces = ns == "*"
            ? _index.GetNamespaces()
            : new[] { ns };

        var results = new List<RebuildNamespaceResult>();
        int totalUpdated = 0, totalSkipped = 0;

        foreach (var namespaceName in namespaces)
        {
            var (updated, skipped) = _index.RebuildEmbeddings(namespaceName, _embedding);
            results.Add(new RebuildNamespaceResult(namespaceName, updated, skipped));
            totalUpdated += updated;
            totalSkipped += skipped;
        }

        return new RebuildEmbeddingsResult(
            totalUpdated, totalSkipped, results.Count, results, _embedding.Dimensions);
    }

    [McpServerTool(Name = "compression_stats")]
    [Description("Show vector compression statistics for a namespace or all namespaces. " +
        "Reports FP32 vs Base64 disk savings, Int8 quantization coverage, and memory footprint estimates.")]
    public object CompressionStats(
        [Description("Namespace to inspect ('*' for all, default: '*').")] string ns = "*")
    {
        var namespaces = ns == "*"
            ? _index.GetNamespaces()
            : new[] { ns };

        var nsStats = new List<NamespaceCompressionStats>();
        int totalEntries = 0, totalQuantized = 0;
        long totalFp32Bytes = 0, totalInt8Bytes = 0;

        foreach (var namespaceName in namespaces)
        {
            var entries = _index.GetAllInNamespace(namespaceName);
            var (stm, ltm, archived) = _index.GetStateCounts(namespaceName);

            int quantizedCount = ltm + archived; // LTM and archived entries are quantized
            int dims = entries.Count > 0 ? entries[0].Vector.Length : _embedding.Dimensions;

            long fp32Bytes = entries.Count * dims * sizeof(float);      // FP32 memory
            long int8Bytes = quantizedCount * dims * sizeof(sbyte);     // Int8 quantized
            long stmBytes = stm * dims * sizeof(float);                  // STM stays FP32
            long totalMemory = stmBytes + int8Bytes + (quantizedCount * 8); // +8 for min/scale

            nsStats.Add(new NamespaceCompressionStats(
                namespaceName, entries.Count, stm, quantizedCount,
                dims, fp32Bytes, int8Bytes + stmBytes, totalMemory));

            totalEntries += entries.Count;
            totalQuantized += quantizedCount;
            totalFp32Bytes += fp32Bytes;
            totalInt8Bytes += int8Bytes + stmBytes;
        }

        float compressionRatio = totalFp32Bytes > 0
            ? (float)totalInt8Bytes / totalFp32Bytes
            : 1f;

        return new CompressionStatsResult(
            totalEntries, totalQuantized, totalFp32Bytes, totalInt8Bytes,
            1f - compressionRatio, nsStats);
    }
}

public sealed record NamespaceCompressionStats(
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("totalEntries")] int TotalEntries,
    [property: JsonPropertyName("stmEntries")] int StmEntries,
    [property: JsonPropertyName("quantizedEntries")] int QuantizedEntries,
    [property: JsonPropertyName("dimensions")] int Dimensions,
    [property: JsonPropertyName("fp32Bytes")] long Fp32Bytes,
    [property: JsonPropertyName("compressedBytes")] long CompressedBytes,
    [property: JsonPropertyName("estimatedMemoryBytes")] long EstimatedMemoryBytes);

public sealed record CompressionStatsResult(
    [property: JsonPropertyName("totalEntries")] int TotalEntries,
    [property: JsonPropertyName("quantizedEntries")] int QuantizedEntries,
    [property: JsonPropertyName("fp32Bytes")] long Fp32Bytes,
    [property: JsonPropertyName("compressedBytes")] long CompressedBytes,
    [property: JsonPropertyName("savingsRatio")] float SavingsRatio,
    [property: JsonPropertyName("namespaces")] IReadOnlyList<NamespaceCompressionStats> Namespaces);
