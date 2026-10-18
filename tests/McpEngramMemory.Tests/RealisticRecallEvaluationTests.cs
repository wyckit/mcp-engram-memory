using System.Diagnostics;
using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

/// <summary>Small, reproducible quality gate using real local embeddings and authored relevance labels.</summary>
public class RealisticRecallEvaluationTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    [Fact]
    public void EvaluateRealisticRecallAcrossRoutes()
    {
        var corpus = JsonSerializer.Deserialize<Corpus>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "recall-realistic-v1.json")), Json)!;
        ValidateCorpus(corpus);
        using var embedding = new OnnxEmbeddingService(); // Mandatory bundled model: no silent skip/fake embeddings.
        var vectors = corpus.Entries.ToDictionary(e => e.Id, e => embedding.Embed(e.Text));
        embedding.Embed("Warm up realistic recall evaluation");
        var rows = new List<Observation>();
        var parityFailures = new List<string>();
        foreach (var question in corpus.Queries)
        {
            RecallResult? direct = null;
            foreach (string route in new[] { "direct", "expert", "broadcast" })
            {
                using var store = new RecallTestStore(embedding);
                foreach (var memory in corpus.Entries)
                    store.Index.Upsert(new CognitiveEntry(memory.Id, vectors[memory.Id], memory.Ns, memory.Text,
                        lifecycleState: memory.State) { IsSummaryNode = memory.Summary });
                foreach (var edge in corpus.Edges)
                    store.Graph.AddEdge(new GraphEdge(edge[0], edge[1], "related_to"));
                // Deliberately provide a perfect routing hint. This evaluates retrieval after
                // expert selection, not the dispatcher's ability to choose a persona.
                if (route == "expert") store.Dispatcher.CreateExpert(question.Ns["expert_".Length..], question.Query);
                if (question.Restart)
                {
                    store.Restart();
                    Assert.All(corpus.Entries, e => Assert.Equal(e.State, store.Index.Get(e.Id, e.Ns, "")!.LifecycleState));
                    Assert.NotEmpty(store.Graph.GetEdgesForEntry(corpus.Edges[0][0], tenantId: ""));
                }

                var tools = store.Tools();
                var timer = Stopwatch.StartNew();
                var result = Assert.IsType<RecallResult>(tools.Recall(question.Query,
                    route == "direct" ? question.Ns : null)); // All public defaults, including graph and spectral auto.
                timer.Stop();
                if (route == "direct") Assert.Contains(result.Strategy, new[] { "direct", "deep_recall" });
                else Assert.Equal(route == "expert" ? "expert_routed" : "broadcast", result.Strategy);
                Assert.InRange(result.Results.Count, 0, 5);
                if (route == "direct") direct = result;
                if (route == "expert" && (!direct!.Results.Select(r => r.Id).SequenceEqual(result.Results.Select(r => r.Id)) ||
                    !direct.Results.Select(r => r.Score).SequenceEqual(result.Results.Select(r => r.Score))))
                    parityFailures.Add(question.Id);
                rows.Add(Measure(question, route, result, corpus, vectors, timer.Elapsed.TotalMilliseconds));
            }
        }

        var groups = rows.GroupBy(r => (r.Split, r.Route)).Select(g => new
        {
            g.Key.Split, g.Key.Route, Questions = g.Count(),
            HitAt5 = g.Average(r => r.HitAt5), RecallAt5 = g.Average(r => r.RecallAt5),
            SourceRecallAt5 = g.Average(r => r.SourceRecallAt5), Mrr = g.Average(r => r.Mrr),
            NdcgAt5 = g.Average(r => r.NdcgAt5), StaleTop1 = g.Average(r => r.StaleTop1),
            SummaryShare = g.Average(r => r.SummaryShare), RedundantPairShare = g.Average(r => r.RedundantPairShare),
            MeanReturnedCharacters = g.Average(r => r.ReturnedCharacters),
            P50Milliseconds = Percentile(g.Select(r => r.Milliseconds), .50),
            P95Milliseconds = Percentile(g.Select(r => r.Milliseconds), .95)
        }).ToList();
        var report = new { corpus.Version, Model = "bundled bge-micro-v2 ONNX", Framework = AppContext.TargetFrameworkName,
            CorpusMemories = corpus.Entries.Count, Questions = corpus.Queries.Count,
            Definition = "Fresh store per query/route; warm embedding model; latency includes query embedding, retrieval and returned-result updates, excludes seeding/restart; characters are not tokens.",
            Groups = groups, ParityFailures = parityFailures, Observations = rows };
        string directory = Environment.GetEnvironmentVariable("ENGRAM_RECALL_EVAL_OUTPUT") ??
            Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"recall-realistic-net{Environment.Version.Major}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, Json));
        output.WriteLine($"Full per-query report: {path}");
        output.WriteLine(JsonSerializer.Serialize(groups, Json));
        output.WriteLine($"Expert/direct parity failures: {string.Join(", ", parityFailures)}");

        // Fixed initial floors, declared before the first run. Keep unresolved summary/
        // correction selection visible in the report instead of tuning those policies here.
        Assert.All(groups, g => Assert.True(g.HitAt5 >= .80 && g.Mrr >= .50,
            $"{g.Split}/{g.Route}: Hit@5={g.HitAt5:F3}, MRR={g.Mrr:F3}; required >=0.80/0.50"));
        Assert.Empty(parityFailures);
        var reference = JsonSerializer.Deserialize<QualityReference>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "recall-realistic-v1.baseline.json")), Json)!;
        Assert.Equal(corpus.Version, reference.Version);
        Assert.Equal(groups.Count, reference.Groups.Count);
        foreach (var current in groups)
        {
            var expected = Assert.Single(reference.Groups, g => g.Split == current.Split && g.Route == current.Route);
            const double tolerance = .0001;
            Assert.True(current.HitAt5 + tolerance >= expected.HitAt5 &&
                current.RecallAt5 + tolerance >= expected.RecallAt5 &&
                current.SourceRecallAt5 + tolerance >= expected.SourceRecallAt5 &&
                current.Mrr + tolerance >= expected.Mrr && current.NdcgAt5 + tolerance >= expected.NdcgAt5 &&
                current.StaleTop1 <= expected.StaleTop1 + tolerance,
                $"Quality regression in {current.Split}/{current.Route}; inspect {path} before changing the reference.");
        }
    }

    private static Observation Measure(Question q, string route, RecallResult result, Corpus corpus,
        Dictionary<string, float[]> vectors, double elapsed)
    {
        var ids = result.Results.Select(r => r.Id).ToArray();
        var relevant = q.Relevant.ToHashSet();
        var sources = relevant.Where(id => !corpus.Entries.Single(e => e.Id == id).Summary).ToHashSet();
        int hits = ids.Count(relevant.Contains), first = Array.FindIndex(ids, relevant.Contains);
        double dcg = ids.Select((id, i) => relevant.Contains(id) ? 1 / Math.Log2(i + 2) : 0).Sum();
        double ideal = Enumerable.Range(0, Math.Min(5, relevant.Count)).Sum(i => 1 / Math.Log2(i + 2));
        int redundant = 0, pairs = 0;
        for (int i = 0; i < ids.Length; i++)
            for (int j = i + 1; j < ids.Length; j++)
            {
                pairs++;
                var a = vectors[ids[i]]; var b = vectors[ids[j]];
                if (VectorMath.Dot(a, b) / (VectorMath.Norm(a) * VectorMath.Norm(b) + 1e-9f) >= .95f) redundant++;
            }
        return new(q.Id, q.Split, q.Scenario, route, q.Restart, ids, hits > 0 ? 1 : 0,
            (double)hits / relevant.Count, sources.Count == 0 ? 1 : (double)ids.Count(sources.Contains) / sources.Count,
            first < 0 ? 0 : 1.0 / (first + 1), dcg / ideal,
            ids.Length > 0 && q.Obsolete.Contains(ids[0]) ? 1 : 0,
            ids.Length == 0 ? 0 : (double)result.Results.Count(r => r.IsSummaryNode) / ids.Length,
            pairs == 0 ? 0 : (double)redundant / pairs,
            result.Results.Sum(r => r.Text?.Length ?? 0), elapsed);
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];
    }

    private static void ValidateCorpus(Corpus corpus)
    {
        Assert.Equal(1, corpus.Version);
        Assert.Equal(36, corpus.Queries.Count);
        Assert.Equal(24, corpus.Queries.Count(q => q.Split == "development"));
        Assert.Equal(12, corpus.Queries.Count(q => q.Split == "heldout"));
        Assert.Equal(corpus.Entries.Count, corpus.Entries.Select(e => e.Id).Distinct().Count());
        Assert.Equal(corpus.Queries.Count, corpus.Queries.Select(q => q.Id).Distinct().Count());
        var entries = corpus.Entries.ToDictionary(e => e.Id);
        Assert.All(corpus.Queries, q =>
        {
            Assert.NotEmpty(q.Relevant);
            Assert.All(q.Relevant, id => { Assert.Equal(q.Ns, entries[id].Ns); Assert.False(entries[id].Obsolete); });
            Assert.All(q.Obsolete, id => Assert.True(entries[id].Obsolete));
            Assert.Empty(q.Relevant.Intersect(q.Obsolete));
        });
        Assert.All(corpus.Edges, e => { Assert.Equal(2, e.Length); Assert.All(e, id => Assert.Contains(id, entries.Keys)); });
    }

    private sealed record Corpus(int Version, List<Memory> Entries, List<string[]> Edges, List<Question> Queries);
    private sealed record QualityReference(int Version, List<QualityGroup> Groups);
    private sealed record QualityGroup(string Split, string Route, double HitAt5, double RecallAt5,
        double SourceRecallAt5, double Mrr, double NdcgAt5, double StaleTop1);
    private sealed record Memory(string Id, string Ns, string Text, string State, bool Summary, bool Obsolete);
    private sealed record Question(string Id, string Scenario, string Ns, string Query, string[] Relevant,
        string[] Obsolete, string Split, bool Restart);
    private sealed record Observation(string Id, string Split, string Scenario, string Route, bool Restart,
        string[] ReturnedIds, double HitAt5, double RecallAt5, double SourceRecallAt5, double Mrr,
        double NdcgAt5, double StaleTop1, double SummaryShare, double RedundantPairShare,
        int ReturnedCharacters, double Milliseconds);
}
