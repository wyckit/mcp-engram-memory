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
    private readonly NamespaceAccess _access;

    public MaintenanceTools(CognitiveIndex index, IEmbeddingService embedding, MetricsCollector metrics, NamespaceAccess access)
    {
        _index = index;
        _embedding = embedding;
        _metrics = metrics;
        _access = access;
    }

    [McpServerTool(Name = "rebuild_embeddings", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Re-embed all entries using the current model. Use after upgrading the embedding model. Skips entries without text, preserves all metadata.")]
    public object RebuildEmbeddings(
        [Description("Namespace to rebuild ('*' for all namespaces, default: '*').")] string ns = "*")
    {
        using var timer = _metrics.StartTimer("rebuild_embeddings");

        // "*" spans every namespace in the caller's tenant - only rewrite vectors in the ones this
        // caller may write to. A single explicit ns that fails the check simply rebuilds
        // nothing, same shape as "namespace has no entries".
        var namespaces = (ns == "*" ? _index.GetNamespaces(tenantId: _access.TenantId) : new[] { ns })
            .Where(_access.CanWrite)
            .ToList();

        var results = new List<RebuildNamespaceResult>();
        int totalUpdated = 0, totalSkipped = 0;

        foreach (var namespaceName in namespaces)
        {
            var (updated, skipped) = _index.RebuildEmbeddings(namespaceName, _embedding, tenantId: _access.TenantId);
            results.Add(new RebuildNamespaceResult(namespaceName, updated, skipped));
            totalUpdated += updated;
            totalSkipped += skipped;
            if (updated > 0) _access.ClaimOnWrite(namespaceName);
        }

        return new RebuildEmbeddingsResult(
            totalUpdated, totalSkipped, results.Count, results, _embedding.Dimensions);
    }

    [McpServerTool(Name = "compression_stats", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Show vector compression stats: FP32 vs Int8 savings, quantization coverage, and memory footprint per namespace.")]
    public object CompressionStats(
        [Description("Namespace to inspect ('*' for all, default: '*').")] string ns = "*")
    {
        var namespaces = (ns == "*" ? _index.GetNamespaces(tenantId: _access.TenantId) : new[] { ns })
            .Where(_access.CanRead)
            .ToList();

        var nsStats = new List<NamespaceCompressionStats>();
        int totalEntries = 0, totalQuantized = 0;
        long totalFp32Bytes = 0, totalInt8Bytes = 0;

        foreach (var namespaceName in namespaces)
        {
            var entries = _index.GetAllInNamespace(namespaceName, tenantId: _access.TenantId);
            var (stm, ltm, archived) = _index.GetStateCounts(namespaceName, tenantId: _access.TenantId);

            int quantizedCount = ltm + archived; // LTM and archived entries are quantized
            int dims = entries.Count > 0 ? entries[0].Vector.Length : _embedding.Dimensions;

            long fp32Bytes = entries.Count * dims * sizeof(float);      // FP32 memory if uncompressed
            long int8Bytes = quantizedCount * dims * sizeof(sbyte);     // Int8 quantized (LTM + archived)
            long stmBytes = stm * dims * sizeof(float);                  // STM stays FP32 (not quantized)
            long totalMemory = stmBytes + int8Bytes + (quantizedCount * 8); // +8 for min/scale per quantized entry

            nsStats.Add(new NamespaceCompressionStats(
                namespaceName, entries.Count, stm, quantizedCount,
                dims, fp32Bytes, int8Bytes, totalMemory));

            totalEntries += entries.Count;
            totalQuantized += quantizedCount;
            totalFp32Bytes += fp32Bytes;
            totalInt8Bytes += int8Bytes;
        }

        // Savings ratio: how much smaller the quantized (LTM + archived) entries are vs. full FP32.
        // STM entries are not quantized and are excluded from this ratio — they remain FP32.
        // Compare int8Bytes (actual quantized storage) against what those same entries would cost at FP32.
        long quantizedAtFp32 = totalQuantized * _embedding.Dimensions * sizeof(float);
        float compressionRatio = quantizedAtFp32 > 0
            ? (float)totalInt8Bytes / quantizedAtFp32
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
