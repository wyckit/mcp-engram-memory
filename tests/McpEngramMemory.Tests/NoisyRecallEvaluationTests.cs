using System.Diagnostics;
using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Tools;
using static McpEngramMemory.Tests.NoisyRecallEvaluation;

namespace McpEngramMemory.Tests;

public class NoisyRecallEvaluationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FrozenNoisyCasesReportQualityAndEnforceSafety()
    {
        var corpus = Load();
        var memories = Generate(corpus, 128, 8);
        using var embedding = new MeasuredEmbedding();
        var timer = Stopwatch.StartNew();
        var vectors = memories.Select(m => m.Text).Concat(corpus.Profiles.Select(p => p.Description)).Distinct()
            .ToDictionary(text => text, embedding.Embed);
        timer.Stop();
        AssertResolvedRecallCases(await Run(corpus, memories, embedding, vectors, timer.Elapsed.TotalMilliseconds, output));
    }

    // Preserve the frozen questions/labels and original blocker contract. These
    // formerly measured misses are now additional regression requirements.
    internal static void AssertResolvedRecallCases(string reportPath)
    {
        HashSet<string> fixedCases = ["owner-paraphrase", "endpoint-incident", "recovery-duration",
            "rollback-command", "underspecified-environment", "missing-secret"];
        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        var observations = report.RootElement.GetProperty("Observations").EnumerateArray()
            .Where(o => fixedCases.Contains(o.GetProperty("Id").GetString()!)).ToArray();
        Assert.Equal(fixedCases.Count * 3 * 2, observations.Length);
        Assert.All(observations, o => Assert.True(
            o.GetProperty(o.GetProperty("Expected").GetArrayLength() == 0 ? "CorrectAbstention" : "AnswerHit").GetBoolean(),
            $"{o.GetProperty("Id")}/{o.GetProperty("Mode")}/{o.GetProperty("Phase")} lost its expected evidence outcome."));
        var historical = report.RootElement.GetProperty("Observations").EnumerateArray()
            .Where(o => o.GetProperty("Id").GetString() == "timeout-history").ToArray();
        Assert.Equal(3 * 2, historical.Length);
        Assert.All(historical, o => Assert.Equal(o.GetProperty("Expected")[0].GetString(),
            o.GetProperty("ReturnedIds")[0].GetString()));
    }

    [Fact]
    public void QualityOracleDistinguishesHitsAbstentionScopeAndIncompleteFacets()
    {
        var corpus = Load();
        var memories = corpus.Entries.ToDictionary(m => m.Id);
        RecallResult Result(params string[] ids) => new("direct", "expert_juniper_ops", ids.Select(id =>
            new CognitiveSearchResult(id, memories[id].Text, .9f, memories[id].State, 1, null, null, false, null)).ToArray());
        Observation Row(string id, params string[] returned) => Measure(corpus.Queries.Single(q => q.Id == id),
            "direct", "warm", Result(returned), memories, [], 10, 1, 1);
        Assert.True(Row("owner-canonical", "juniper-owner").AnswerHit);
        var wrong = Row("owner-canonical", "kestrel-owner");
        Assert.False(wrong.AnswerHit);
        Assert.Equal(new[] { "kestrel-owner" }, wrong.WrongScopeIds);
        Assert.Equal(2, Blockers([wrong]).Length);
        Assert.True(Row("owner-canonical").FalseAbstention);
        Assert.True(Row("missing-budget").CorrectAbstention);
        Assert.False(Row("missing-budget", "juniper-budget-note").CorrectAbstention);
        Assert.Single(Blockers([Row("missing-budget", "juniper-budget-note")]));
        var partial = Row("split-expert-overview", "juniper-owner");
        Assert.True(partial.AnswerHit);
        Assert.Equal(1.0 / 3, partial.FacetCoverage);
        Assert.False(Row("underspecified-environment", "juniper-stage-owner").WrongScopeIds.Any());
        Assert.False(Row("timeout-history", "juniper-timeout-old").StaleTopOne);
        Assert.True(Row("timeout-current", "juniper-timeout-old").StaleTopOne);
    }

    [Fact]
    public void ScaleLayoutsAreDeterministicUniqueAndChangeOnlyNamespacePlacement()
    {
        var corpus = Load();
        foreach (int size in new[] { 1000, 5000 })
        {
            var narrow = Generate(corpus, size, 8);
            var wide = Generate(corpus, size, 32);
            Assert.Equal(narrow, Generate(corpus, size, 8));
            Assert.Equal(narrow.Select(m => m with { Ns = "" }), wide.Select(m => m with { Ns = "" }));
            Assert.Equal(8, narrow.Select(m => m.Ns).Distinct().Count());
            Assert.Equal(32, wide.Select(m => m.Ns).Distinct().Count());
        }
    }
}

// Separate CI lane: the matrix is intentionally not multiplied by all three TFMs.
// An explicit local Category=RecallScale run never silently skips or substitutes a model.
[Trait("Category", "RecallScale")]
public class NoisyRecallScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EvaluateThousandAndFiveThousandMemoriesAcrossNamespaceLayouts()
    {
        var corpus = Load();
        using var embedding = new MeasuredEmbedding();
        var largest = Generate(corpus, 5000, 32);
        var timer = Stopwatch.StartNew();
        var vectors = largest.Select(m => m.Text).Concat(corpus.Profiles.Select(p => p.Description)).Distinct()
            .ToDictionary(text => text, embedding.Embed);
        timer.Stop();
        foreach (int size in new[] { 1000, 5000 })
            foreach (int namespaces in new[] { 8, 32 })
                NoisyRecallEvaluationTests.AssertResolvedRecallCases(
                    await Run(corpus, Generate(corpus, size, namespaces), embedding, vectors, timer.Elapsed.TotalMilliseconds, output));
    }
}
