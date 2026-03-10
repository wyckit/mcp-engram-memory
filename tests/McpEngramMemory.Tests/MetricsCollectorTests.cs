using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;

namespace McpEngramMemory.Tests;

public class MetricsCollectorTests
{
    [Fact]
    public void Record_AndGetSummary_ComputesCorrectStats()
    {
        var collector = new MetricsCollector();
        collector.Record("search", 10.0);
        collector.Record("search", 20.0);
        collector.Record("search", 30.0);

        var summary = collector.GetSummary("search");
        Assert.Equal(3, summary.Count);
        Assert.Equal(20.0, summary.MeanMs, 1);
        Assert.Equal(10.0, summary.MinMs);
        Assert.Equal(30.0, summary.MaxMs);
    }

    [Fact]
    public void GetSummary_NoData_ReturnsZeros()
    {
        var collector = new MetricsCollector();
        var summary = collector.GetSummary("missing");
        Assert.Equal(0, summary.Count);
        Assert.Equal(0, summary.MeanMs);
    }

    [Fact]
    public void Timer_RecordsDuration()
    {
        var collector = new MetricsCollector();
        using (collector.StartTimer("test"))
        {
            Thread.Sleep(10);
        }

        var summary = collector.GetSummary("test");
        Assert.Equal(1, summary.Count);
        Assert.True(summary.MeanMs >= 5);
    }

    [Fact]
    public void Reset_ClearsSpecificType()
    {
        var collector = new MetricsCollector();
        collector.Record("search", 10.0);
        collector.Record("store", 20.0);

        collector.Reset("search");

        Assert.Equal(0, collector.GetSummary("search").Count);
        Assert.Equal(1, collector.GetSummary("store").Count);
    }

    [Fact]
    public void Reset_ClearsAll()
    {
        var collector = new MetricsCollector();
        collector.Record("search", 10.0);
        collector.Record("store", 20.0);

        collector.Reset();

        Assert.Empty(collector.GetAllSummaries());
    }

    [Fact]
    public void GetAllSummaries_ReturnsAllTypes()
    {
        var collector = new MetricsCollector();
        collector.Record("search", 10.0);
        collector.Record("store", 20.0);
        collector.Record("delete", 5.0);

        var summaries = collector.GetAllSummaries();
        Assert.Equal(3, summaries.Count);
    }

    [Fact]
    public void Percentiles_ComputeCorrectly()
    {
        var collector = new MetricsCollector();
        for (int i = 1; i <= 100; i++)
            collector.Record("test", i);

        var summary = collector.GetSummary("test");
        Assert.Equal(100, summary.Count);
        Assert.Equal(50.5, summary.MeanMs, 1);
        Assert.Equal(1.0, summary.MinMs);
        Assert.Equal(100.0, summary.MaxMs);
        Assert.InRange(summary.P50Ms, 49, 52);
        Assert.InRange(summary.P95Ms, 94, 96);
        Assert.InRange(summary.P99Ms, 98, 100);
    }

    [Fact]
    public void SingleSample_AllPercentilesEqual()
    {
        var collector = new MetricsCollector();
        collector.Record("single", 42.0);

        var summary = collector.GetSummary("single");
        Assert.Equal(1, summary.Count);
        Assert.Equal(42.0, summary.MeanMs);
        Assert.Equal(42.0, summary.P50Ms);
        Assert.Equal(42.0, summary.P95Ms);
        Assert.Equal(42.0, summary.P99Ms);
    }
}
