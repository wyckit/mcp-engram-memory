using System.Diagnostics;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Tools;
using static McpEngramMemory.Tests.IndependentRecallValidation;

namespace McpEngramMemory.Tests;

[CollectionDefinition("Independent recall validation", DisableParallelization = true)]
public sealed class IndependentRecallValidationCollection;

[Collection("Independent recall validation")]
[Trait("Category", "IndependentRecall")]
public sealed class IndependentRecallEvaluationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("json")]
    [InlineData("sqlite")]
    public async Task FrozenCasesProduceCompleteEvidenceWithoutTuning(string backend)
    {
        var corpus = Load();
        using var embedding = new NoisyRecallEvaluation.MeasuredEmbedding();
        var vectors = corpus.Entries.Select(e => e.Text).Concat(corpus.Profiles.Select(p => p.Description))
            .Distinct().ToDictionary(t => t, embedding.Embed);
        var rows = new List<Observation>();
        var started = DateTimeOffset.UtcNow;
        foreach (string mode in Modes)
        {
            using var store = new Store(backend, embedding);
            foreach (var m in corpus.Entries) store.Add(m.Id, m.Ns, m.Text, vectors[m.Text], state: m.State);
            if (mode == "automatic") foreach (var p in corpus.Profiles) store.Profile(p, vectors[p.Description]);
            foreach (string phase in new[] { "warm", "restarted" })
            {
                if (phase == "restarted") store.Restart();
                var tools = store.Tools();
                foreach (var q in corpus.Queries)
                {
                    var timer = Stopwatch.StartNew();
                    var result = Assert.IsType<RecallResult>(await tools.RecallAsync(q.Query, mode == "direct" ? q.Ns : null));
                    timer.Stop();
                    if (mode == "broadcast") Assert.Equal("broadcast", result.Strategy);
                    rows.Add(Measure(corpus, q, mode, phase, result, timer.Elapsed.TotalMilliseconds));
                }
            }
        }
        Assert.Equal(144, rows.Count);
        Assert.Equal(144, rows.Select(r => (r.Id, r.Mode, r.Phase)).Distinct().Count());
        string path = Write($"recall-independent-{backend}", new
        {
            SchemaVersion = 1, Backend = backend, Metadata = Metadata(), StartedAtUtc = started, CompletedAtUtc = DateTimeOffset.UtcNow,
            Questions = 24, Observations = rows, ReleaseReady = rows.All(r => r.Failures.Length == 0),
            FailedObservations = rows.Count(r => r.Failures.Length > 0)
        });
        output.WriteLine($"{path}: {rows.Count(r => r.Failures.Length > 0)}/144 observations fail frozen release criteria.");
        // Keep the frozen corpus and oracle unchanged. These previously failing
        // negative cases are now required to abstain on every route and restart.
        string[] fixedNonAnswers = ["absent-date", "absent-approval-owner", "proposal-not-decision",
            "question-not-number", "topic-not-answer"];
        var fixedRows = rows.Where(r => fixedNonAnswers.Contains(r.Id)).ToArray();
        Assert.Equal(30, fixedRows.Length);
        Assert.All(fixedRows, r => Assert.True(r.CorrectAbstention && r.Failures.Length == 0,
            $"{r.Id}/{r.Mode}/{r.Phase}: {string.Join(", ", r.Failures)}"));
        var restoreRows = rows.Where(r => r.Id == "restore-order").ToArray();
        Assert.Equal(6, restoreRows.Length);
        Assert.All(restoreRows, r => Assert.True(r.AnswerHit && r.TopOneCorrect && r.Failures.Length == 0,
            $"{r.Id}/{r.Mode}/{r.Phase}: {string.Join(", ", r.Failures)}"));
    }
}

public sealed class IndependentRecallOracleTests
{
    [Fact]
    public void OracleDistinguishesWrongTopPartialFacetsNegativesAndWrongScope()
    {
        var c = Load();
        RecallResult Result(params string[] ids) => new("direct", "expert_citrine_ops",
            ids.Select(id => new CognitiveSearchResult(id, c.Entries.Single(e => e.Id == id).Text,
                .9f, "ltm", 1, null, null, false, null)).ToList(), "oracle");
        var q = c.Queries.Single(q => q.Id == "deployment-contact");
        Assert.Empty(Measure(c, q, "direct", "warm", Result("c-owner"), 0).Failures);
        Assert.Contains("incorrect top one", Measure(c, q, "direct", "warm", Result("c-page", "c-owner"), 0).Failures);
        Assert.Contains("answer missed", Measure(c, q, "direct", "warm", Result(), 0).Failures);
        Assert.Contains("project/environment mismatch", Measure(c, q, "automatic", "warm", Result("m-owner"), 0).Failures);
        var compound = c.Queries.Single(q => q.Id == "compound-recovery");
        Assert.Equal(1, Measure(c, compound, "direct", "warm", Result("c-owner"), 0).FacetCoverage);
        Assert.Equal(.5, Measure(c, compound, "automatic", "warm", Result("c-owner"), 0).FacetCoverage);
        var negative = c.Queries.Single(q => q.Id == "absent-secret");
        Assert.True(Measure(c, negative, "direct", "warm", Result(), 0).CorrectAbstention);
        Assert.Contains("non-answer evidence", Measure(c, negative, "direct", "warm", Result("c-secret-note"), 0).Failures);
    }
}
