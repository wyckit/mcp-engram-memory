using System.Diagnostics;
using McpEngramMemory.Tools;
using static McpEngramMemory.Tests.IndependentRecallValidation;

namespace McpEngramMemory.Tests;

[Collection("Independent recall validation")]
[Trait("Category", "IndependentRecall")]
public sealed class HistoricalRecallNoiseEvaluationTests
{
    [Theory]
    [InlineData("json", 1000)]
    [InlineData("sqlite", 5000)]
    public async Task FrozenQuestionsRejectTheSoaksUnrelatedLibraryCorpus(string backend, int size)
    {
        var corpus = Load();
        using var embedding = new NoisyRecallEvaluation.MeasuredEmbedding();
        var background = Enumerable.Range(0, size - corpus.Entries.Length).Select(i => new
        {
            Id = $"background-{i}", Ns = $"background-{i % 5}",
            Text = $"Library catalogue volume {i}: coastal birds, tidal plants and navigation charts."
        }).ToArray();
        var vectors = corpus.Entries.Select(e => e.Text).Concat(corpus.Profiles.Select(p => p.Description))
            .Concat(background.Select(e => e.Text)).Distinct().ToDictionary(t => t, embedding.Embed);
        var rows = new List<(Question Question, string Mode, string Phase, RecallResult Result, double Milliseconds)>();
        var accessedNoise = new List<string>();
        foreach (string mode in Modes)
        {
            using var store = new Store(backend, embedding);
            foreach (var e in corpus.Entries) store.Add(e.Id, e.Ns, e.Text, vectors[e.Text], state: e.State);
            foreach (var e in background) store.Add(e.Id, e.Ns, e.Text, vectors[e.Text]);
            if (mode == "automatic") foreach (var p in corpus.Profiles) store.Profile(p, vectors[p.Description]);
            foreach (string phase in new[] { "warm", "restarted" })
            {
                if (phase == "restarted") store.Restart();
                foreach (var q in corpus.Queries)
                {
                    var timer = Stopwatch.StartNew();
                    var result = Assert.IsType<RecallResult>(await store.Tools().RecallAsync(q.Query, mode == "direct" ? q.Ns : null));
                    rows.Add((q, mode, phase, result, timer.Elapsed.TotalMilliseconds));
                }
            }
            accessedNoise.AddRange(background.Where(e => store.Index.Get(e.Id, e.Ns, "")!.AccessCount != 1)
                .Select(e => mode + "/" + e.Id));
            Assert.Equal("archived", store.Index.Get("c-endpoint-old", "expert_citrine_ops", "")!.LifecycleState);
        }
        Write($"recall-history-noise-{backend}-{size}", new
        {
            Metadata = Metadata(), Backend = backend, Memories = size,
            Observations = rows.Select(r => new { r.Question.Id, r.Mode, r.Phase, r.Result, r.Milliseconds }),
            AccessedNoise = accessedNoise
        });
        Assert.Equal(144, rows.Count);
        // Reuse the frozen oracle: unknown library IDs, scope mismatches, missing
        // facets, false answers and incorrect historical top-one all remain failures.
        Assert.All(rows, r => Assert.Empty(Measure(corpus, r.Question, r.Mode, r.Phase, r.Result, r.Milliseconds).Failures));
        Assert.Empty(accessedNoise);
    }
}
