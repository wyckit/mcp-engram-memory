using System.ComponentModel;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>
/// MCP tools for benchmarking and operational metrics.
/// </summary>
[McpServerToolType]
public sealed class BenchmarkTools
{
    private readonly BenchmarkRunner _runner;
    private readonly MetricsCollector _metrics;

    public BenchmarkTools(BenchmarkRunner runner, MetricsCollector metrics)
    {
        _runner = runner;
        _metrics = metrics;
    }

    [McpServerTool(Name = "run_benchmark")]
    [Description("Run an IR quality benchmark in an isolated namespace. Computes Recall@K, Precision@K, MRR, nDCG@K, and latency percentiles. Namespace is cleaned up after.")]
    public object RunBenchmark(
        [Description("Dataset ID: 'default-v1' (25 seeds, 20 queries), 'paraphrase-v1' (rephrased), 'multihop-v1' (cross-topic), 'scale-v1' (80 seeds stress test), 'realworld-v1' (cognitive patterns), 'compound-v1' (domain jargon), 'lifecycle-v1', 'adversarial-v1', 'accretion-v1', 'cluster-summary-v1'.")] string datasetId = "default-v1",
        [Description("Search mode: 'vector' (default), 'hybrid' (BM25+vector RRF fusion), 'vector_rerank' (vector + token reranker), 'hybrid_rerank' (hybrid + token reranker).")] string mode = "vector",
        [Description("When true, prepend category/namespace context to text before embedding (contextual retrieval). Default: false.")] bool contextualPrefix = false)
    {
        var dataset = BenchmarkRunner.CreateDataset(datasetId);
        if (dataset is null)
            return $"Error: Unknown dataset '{datasetId}'. Available: {string.Join(", ", BenchmarkRunner.GetAvailableDatasets())}";

        var searchMode = mode.ToLowerInvariant() switch
        {
            "hybrid" => BenchmarkRunner.SearchMode.Hybrid,
            "vector_rerank" or "vectorrerank" => BenchmarkRunner.SearchMode.VectorRerank,
            "hybrid_rerank" or "hybridrerank" => BenchmarkRunner.SearchMode.HybridRerank,
            _ => BenchmarkRunner.SearchMode.Vector
        };

        return _runner.Run(dataset, searchMode, contextualPrefix);
    }

    [McpServerTool(Name = "get_metrics")]
    [Description("Get operational metrics: P50/P95/P99 latency, throughput, and call counts per operation type.")]
    public IReadOnlyList<MetricsSummary> GetMetrics(
        [Description("Operation type to filter (e.g. 'search', 'store'). Leave empty for all.")] string? operationType = null)
    {
        if (operationType is not null)
        {
            var summary = _metrics.GetSummary(operationType);
            return summary.Count > 0 ? new[] { summary } : Array.Empty<MetricsSummary>();
        }
        return _metrics.GetAllSummaries();
    }

    [McpServerTool(Name = "reset_metrics")]
    [Description("Reset operational metrics counters. Optionally filter by operation type.")]
    public string ResetMetrics(
        [Description("Operation type to reset. Leave empty to reset all.")] string? operationType = null)
    {
        _metrics.Reset(operationType);
        return operationType is not null
            ? $"Reset metrics for '{operationType}'."
            : "All metrics reset.";
    }
}
