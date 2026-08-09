using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Evaluation;

namespace McpEngramMemory.Tests;

public class RegressionGateTests
{
    // ------------------------------------------------------------------
    // PairedStatistics: one-sided paired t-test
    // ------------------------------------------------------------------

    [Fact]
    public void OneSidedPairedTTest_HandComputedVector()
    {
        // deltas = candidate - baseline per query. Hand-computed:
        //   mean = -0.125, sd = sqrt(0.0125/3) = 0.0645497, t = -3.8730, df = 3,
        //   one-sided lower-tail p ~= 0.01524.
        var result = PairedStatistics.OneSidedPairedTTest(new[] { -0.10, -0.20, -0.15, -0.05 });

        Assert.Equal(4, result.N);
        Assert.Equal(-0.125, result.MeanDelta, 10);
        Assert.Equal(0.0645497, result.StdDevDelta, 5);
        Assert.Equal(-3.8730, result.TStatistic, 3);
        Assert.Equal(3.0, result.DegreesOfFreedom);
        Assert.Equal(0.01524, result.PValue, 3);
    }

    [Theory]
    [InlineData(0.0, 1.0, 0.5)]
    [InlineData(0.0, 17.0, 0.5)]
    [InlineData(-1.0, 1.0, 0.25)]      // Cauchy closed form: 1/2 + arctan(-1)/pi
    [InlineData(-2.015, 5.0, 0.050)]   // t_{0.05, df=5}
    [InlineData(-1.886, 2.0, 0.100)]   // t_{0.10, df=2}
    [InlineData(2.132, 4.0, 0.950)]    // 1 - t_{0.05, df=4}
    public void StudentTCdf_KnownValues(double t, double df, double expected)
    {
        Assert.Equal(expected, PairedStatistics.StudentTCdf(t, df), 3);
    }

    [Fact]
    public void OneSidedPairedTTest_DegenerateVariance()
    {
        // Zero variance, negative mean: certain regression, p = 0.
        var drop = PairedStatistics.OneSidedPairedTTest(new[] { -0.1, -0.1, -0.1 });
        Assert.Equal(0.0, drop.PValue);
        Assert.True(double.IsNegativeInfinity(drop.TStatistic));

        // Zero variance, zero mean: no evidence of regression, p = 1.
        var flat = PairedStatistics.OneSidedPairedTTest(new[] { 0.0, 0.0, 0.0 });
        Assert.Equal(1.0, flat.PValue);
        Assert.Equal(0.0, flat.TStatistic);

        // n = 1: not testable; callers fall back to the legacy comparison.
        Assert.Throws<ArgumentException>(() => PairedStatistics.OneSidedPairedTTest(new[] { -0.1 }));
    }

    [Fact]
    public void HolmBonferroni_StepDownOrdering()
    {
        // Sorted: 0.005 (<= 0.05/4 = 0.0125, reject), 0.01 (<= 0.05/3 = 0.0167, reject),
        // 0.03 (> 0.05/2 = 0.025, stop), 0.04 blocked even though 0.04 <= 0.05.
        bool[] rejected = PairedStatistics.HolmBonferroni(new[] { 0.01, 0.04, 0.03, 0.005 }, 0.05);
        Assert.Equal(new[] { true, false, false, true }, rejected);
    }

    // ------------------------------------------------------------------
    // Gate scenarios (candidate + baseline artifacts on disk)
    // ------------------------------------------------------------------

    [Fact]
    public void Gate_QuantizationFlip_DoesNotFail()
    {
        // THE acceptance test: 18 queries, one flips 1.0 -> 0.0. Mean drop 0.0556
        // exceeds the legacy tolerance 0.02 (old gate FAILs), but t = -1.000 exactly
        // and p ~= 0.166 is not significant, so the statistical gate PASSes.
        RunGateScenario(
            baselineRecalls: Repeat(1.0, 18),
            candidateRecalls: WithFlip(Repeat(1.0, 18), index: 0, value: 0.0),
            assert: report =>
            {
                var check = Assert.Single(report.Checks);
                Assert.Equal("paired-t", check.Method);
                Assert.Equal("PASS", check.Status);
                Assert.NotNull(check.TTest);
                Assert.Equal(-1.000, check.TTest!.TStatistic, 3);
                Assert.Equal(0.166, check.TTest.PValue, 2);
                Assert.False(check.HolmSignificant);
                Assert.True(report.Passed);
            });
    }

    [Fact]
    public void Gate_UniformDrop_Fails()
    {
        // All 18 recalls drop 1.0 -> 0.9: sd = 0 => p = 0, Holm-significant, and the
        // mean drop 0.10 exceeds the MDE 0.02 => FAIL.
        RunGateScenario(
            baselineRecalls: Repeat(1.0, 18),
            candidateRecalls: Repeat(0.9, 18),
            assert: report =>
            {
                var check = Assert.Single(report.Checks);
                Assert.Equal("paired-t", check.Method);
                Assert.Equal("FAIL", check.Status);
                Assert.True(check.HolmSignificant);
                Assert.False(report.Passed);
            });
    }

    [Fact]
    public void Gate_SignificantButBelowMde_Passes()
    {
        // All 18 recalls drop by 0.005: p ~= 0 (zero variance), but the mean drop is
        // below the MDE 0.02, so the AND-of-both-arms rule keeps it a PASS.
        RunGateScenario(
            baselineRecalls: Repeat(1.0, 18),
            candidateRecalls: Repeat(0.995, 18),
            assert: report =>
            {
                var check = Assert.Single(report.Checks);
                Assert.Equal("paired-t", check.Method);
                Assert.Equal("PASS", check.Status);
                Assert.True(check.HolmSignificant);
                Assert.True(report.Passed);
            });
    }

    [Fact]
    public void Gate_LegacyFallback_BaselineWithoutPerQuery()
    {
        // Baseline with aggregates only (no queryScores, mimicking the flat
        // benchmarks/baseline-v1.json shape): the statistical path cannot engage,
        // so the legacy point comparison decides and a regenerate warning is emitted.
        RunGateScenario(
            baselineRecalls: Repeat(1.0, 18),
            candidateRecalls: Repeat(0.95, 18),
            baselineIncludesQueryScores: false,
            assert: report =>
            {
                var check = Assert.Single(report.Checks);
                Assert.Equal("legacy", check.Method);
                Assert.Equal("FAIL", check.Status); // 0.95 < 1.0 - 0.02
                Assert.Contains(report.Warnings, w => w.Contains("regenerate the pinned baseline"));
            });

        RunGateScenario(
            baselineRecalls: Repeat(1.0, 18),
            candidateRecalls: Repeat(0.99, 18),
            baselineIncludesQueryScores: false,
            assert: report =>
            {
                var check = Assert.Single(report.Checks);
                Assert.Equal("legacy", check.Method);
                Assert.Equal("PASS", check.Status); // 0.99 >= 1.0 - 0.02
                Assert.True(report.Passed);
            });
    }

    [Fact]
    public void Gate_MismatchedQueryIds_FallsBackAndWarns()
    {
        string candDir = TempDir();
        string baseDir = TempDir();
        try
        {
            string[] candidateIds = Enumerable.Range(1, 18).Select(i => $"q{i}").ToArray();
            string[] baselineIds = Enumerable.Range(1, 17).Select(i => $"q{i}").Append("qX").ToArray();

            File.WriteAllText(Path.Combine(candDir, "candidate.json"),
                MakeFlatArtifact("ds-mismatch", "hybrid", Repeat(1.0, 18), candidateIds));
            File.WriteAllText(Path.Combine(baseDir, "baseline.json"),
                MakeFlatArtifact("ds-mismatch", "hybrid", Repeat(1.0, 18), baselineIds));

            var report = RegressionGateRunner.Evaluate(new RegressionGateOptions(candDir, baseDir));

            var check = Assert.Single(report.Checks);
            Assert.Equal("legacy", check.Method);
            Assert.Null(check.TTest);
            Assert.Contains(report.Warnings, w => w.Contains("query id sets differ"));
        }
        finally
        {
            Cleanup(candDir, baseDir);
        }
    }

    [Fact]
    public void Gate_AgentOutcomeShape_BothTaskPropertyNames()
    {
        // Live baselines serialize per-task vectors as taskResults; offline candidates
        // as taskScores. The parser must accept both so the paired path engages.
        string candDir = TempDir();
        string baseDir = TempDir();
        try
        {
            double[] successScores = { 0.8, 0.9, 1.0, 0.7 };
            bool[] passed = { true, true, false, true };
            double[] requiredCoverage = { 1.0, 0.75, 1.0, 0.5 };

            File.WriteAllText(Path.Combine(candDir, "candidate.json"),
                MakeAgentOutcomeArtifact("agent-ds", "taskScores", successScores, passed, requiredCoverage));
            File.WriteAllText(Path.Combine(baseDir, "baseline.json"),
                MakeAgentOutcomeArtifact("agent-ds", "taskResults", successScores, passed, requiredCoverage));

            var report = RegressionGateRunner.Evaluate(new RegressionGateOptions(candDir, baseDir));

            Assert.Equal(3, report.Total);
            Assert.Equal(new[] { "SuccessScore", "PassRate", "RequiredCoverage" },
                report.Checks.Select(c => c.Metric).ToArray());
            Assert.All(report.Checks, c => Assert.Equal("paired-t", c.Method));
            Assert.All(report.Checks, c => Assert.Equal("PASS", c.Status));
            Assert.All(report.Checks, c => Assert.Equal(4, c.TTest!.N));
            Assert.True(report.Passed);
        }
        finally
        {
            Cleanup(candDir, baseDir);
        }
    }

    [Fact]
    public void Gate_HolmFamily_OnlyTrueRegressorFails()
    {
        // Two datasets in one run share a Holm family: dataset A regresses uniformly
        // (p ~= 0), dataset B has a single-query flip (p ~= 0.166). Only A fails.
        string candDir = TempDir();
        string baseDir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(candDir, "a.json"),
                MakeFlatArtifact("ds-a", "hybrid", Repeat(0.9, 18)));
            File.WriteAllText(Path.Combine(candDir, "b.json"),
                MakeFlatArtifact("ds-b", "hybrid", WithFlip(Repeat(1.0, 18), 0, 0.0)));
            File.WriteAllText(Path.Combine(baseDir, "a-baseline.json"),
                MakeFlatArtifact("ds-a", "hybrid", Repeat(1.0, 18)));
            File.WriteAllText(Path.Combine(baseDir, "b-baseline.json"),
                MakeFlatArtifact("ds-b", "hybrid", Repeat(1.0, 18)));

            var report = RegressionGateRunner.Evaluate(new RegressionGateOptions(candDir, baseDir));

            Assert.Equal(2, report.Total);
            var a = Assert.Single(report.Checks, c => c.Label == "ds-a/hybrid");
            var b = Assert.Single(report.Checks, c => c.Label == "ds-b/hybrid");
            Assert.Equal("FAIL", a.Status);
            Assert.True(a.HolmSignificant);
            Assert.Equal("PASS", b.Status);
            Assert.False(b.HolmSignificant);
            Assert.Equal(1, report.Failed);
        }
        finally
        {
            Cleanup(candDir, baseDir);
        }
    }

    [Fact]
    public void Gate_FloorViolation_FailsRegardlessOfStats()
    {
        // Candidate identical to baseline (all deltas zero, p = 1) but below the
        // absolute Recall floor 0.20: floors are sanity nets and always FAIL.
        RunGateScenario(
            baselineRecalls: Repeat(0.15, 18),
            candidateRecalls: Repeat(0.15, 18),
            assert: report =>
            {
                var check = Assert.Single(report.Checks);
                Assert.Equal("FAIL", check.Status);
                Assert.Contains("below floor", check.Notes);
                Assert.Equal("paired-t", check.Method);
                Assert.False(check.HolmSignificant);
                Assert.False(report.Passed);
            });
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static void RunGateScenario(
        double[] baselineRecalls,
        double[] candidateRecalls,
        Action<RegressionGateReport> assert,
        bool baselineIncludesQueryScores = true)
    {
        string candDir = TempDir();
        string baseDir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(candDir, "candidate.json"),
                MakeFlatArtifact("ds-1", "hybrid", candidateRecalls));
            File.WriteAllText(Path.Combine(baseDir, "baseline.json"),
                MakeFlatArtifact("ds-1", "hybrid", baselineRecalls, includeQueryScores: baselineIncludesQueryScores));

            var report = RegressionGateRunner.Evaluate(new RegressionGateOptions(candDir, baseDir));
            assert(report);
        }
        finally
        {
            Cleanup(candDir, baseDir);
        }
    }

    /// <summary>
    /// Builds a flat IR-quality artifact with the exact camelCase field names that
    /// BenchmarkRunResult/QueryScore serialize (BenchmarkModels.cs). Only the
    /// meanRecallAtK aggregate is emitted so each artifact contributes exactly one
    /// Recall@K check.
    /// </summary>
    private static string MakeFlatArtifact(
        string datasetId, string mode, double[] perQueryRecalls,
        string[]? queryIds = null, bool includeQueryScores = true)
    {
        queryIds ??= Enumerable.Range(1, perQueryRecalls.Length).Select(i => $"q{i}").ToArray();
        var payload = new Dictionary<string, object?>
        {
            ["datasetId"] = datasetId,
            ["mode"] = mode,
            ["runAt"] = "2026-08-09T00:00:00+00:00",
            ["meanRecallAtK"] = perQueryRecalls.Average(),
            ["meanLatencyMs"] = 1.0,
            ["totalQueries"] = perQueryRecalls.Length
        };
        if (includeQueryScores)
        {
            payload["queryScores"] = perQueryRecalls.Select((recall, i) => new Dictionary<string, object?>
            {
                ["queryId"] = queryIds[i],
                ["recallAtK"] = recall,
                ["precisionAtK"] = recall,
                ["mrr"] = recall,
                ["ndcgAtK"] = recall,
                ["latencyMs"] = 1.0,
                ["actualResultIds"] = Array.Empty<string>()
            }).ToArray();
        }
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Builds an agent-outcome artifact whose full_engram condition carries per-task
    /// vectors under <paramref name="taskPropertyName"/> — "taskScores" (offline shape)
    /// or "taskResults" (live shape).
    /// </summary>
    private static string MakeAgentOutcomeArtifact(
        string datasetId, string taskPropertyName,
        double[] successScores, bool[] passed, double[] requiredCoverage)
    {
        var tasks = successScores.Select((score, i) => new Dictionary<string, object?>
        {
            ["taskId"] = $"t{i + 1}",
            ["successScore"] = score,
            ["passed"] = passed[i],
            ["requiredCoverage"] = requiredCoverage[i],
            ["latencyMs"] = 1.0
        }).ToArray();

        var payload = new Dictionary<string, object?>
        {
            ["datasetId"] = datasetId,
            ["runAt"] = "2026-08-09T00:00:00+00:00",
            ["baselineCondition"] = "no_memory",
            ["comparisons"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["condition"] = "full_engram",
                    ["result"] = new Dictionary<string, object?>
                    {
                        ["condition"] = "full_engram",
                        [taskPropertyName] = tasks,
                        ["meanSuccessScore"] = successScores.Average(),
                        ["passRate"] = passed.Count(p => p) / (double)passed.Length,
                        ["meanRequiredCoverage"] = requiredCoverage.Average()
                    }
                }
            }
        };
        return JsonSerializer.Serialize(payload);
    }

    private static double[] Repeat(double value, int count)
        => Enumerable.Repeat(value, count).ToArray();

    private static double[] WithFlip(double[] values, int index, double value)
    {
        var copy = (double[])values.Clone();
        copy[index] = value;
        return copy;
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"gate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(params string[] dirs)
    {
        foreach (string dir in dirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
