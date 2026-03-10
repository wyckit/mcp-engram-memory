using System.Collections.Concurrent;
using System.Diagnostics;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// Lightweight metrics collector for operation timing and throughput tracking.
/// Thread-safe. Cap of MaxSamplesPerType per operation type is approximate under concurrent load.
/// </summary>
public sealed class MetricsCollector
{
    private const int MaxSamplesPerType = 10_000;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<OperationSample>> _samples = new();

    public OperationTimer StartTimer(string operationType)
    {
        return new OperationTimer(this, operationType);
    }

    public void Record(string operationType, double durationMs)
    {
        var queue = _samples.GetOrAdd(operationType, _ => new ConcurrentQueue<OperationSample>());
        queue.Enqueue(new OperationSample(durationMs, DateTimeOffset.UtcNow));

        while (queue.Count > MaxSamplesPerType)
            queue.TryDequeue(out _);
    }

    public MetricsSummary GetSummary(string operationType)
    {
        if (!_samples.TryGetValue(operationType, out var queue) || queue.IsEmpty)
            return new MetricsSummary(operationType, 0, 0, 0, 0, 0, 0, 0);

        var durations = queue.Select(s => s.DurationMs).ToList();
        durations.Sort();

        return new MetricsSummary(
            operationType,
            durations.Count,
            durations.Average(),
            Percentile(durations, 0.50),
            Percentile(durations, 0.95),
            Percentile(durations, 0.99),
            durations[0],
            durations[^1]);
    }

    public IReadOnlyList<MetricsSummary> GetAllSummaries()
    {
        return _samples.Keys.Select(GetSummary).ToList();
    }

    public void Reset(string? operationType = null)
    {
        if (operationType is not null)
            _samples.TryRemove(operationType, out _);
        else
            _samples.Clear();
    }

    internal static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        double index = p * (sorted.Count - 1);
        int lower = (int)Math.Floor(index);
        int upper = Math.Min(lower + 1, sorted.Count - 1);
        double fraction = index - lower;
        return sorted[lower] + fraction * (sorted[upper] - sorted[lower]);
    }

    public readonly struct OperationTimer : IDisposable
    {
        private readonly MetricsCollector _collector;
        private readonly string _operationType;
        private readonly long _startTicks;

        internal OperationTimer(MetricsCollector collector, string operationType)
        {
            _collector = collector;
            _operationType = operationType;
            _startTicks = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(_startTicks);
            _collector.Record(_operationType, elapsed.TotalMilliseconds);
        }
    }
}

public sealed record OperationSample(double DurationMs, DateTimeOffset Timestamp);

public sealed record MetricsSummary(
    string OperationType,
    int Count,
    double MeanMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MinMs,
    double MaxMs);
