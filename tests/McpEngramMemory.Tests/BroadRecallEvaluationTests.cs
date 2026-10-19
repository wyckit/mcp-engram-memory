using System.Diagnostics;
using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class BroadRecallEvaluationTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    [Fact]
    public void BroadQueriesReturnComplementaryEvidenceAcrossRoutes()
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var corpus = JsonSerializer.Deserialize<Corpus>(File.ReadAllText(Path.Combine(fixtures, "recall-realistic-v1.json")), Json)!;
        var supplement = JsonSerializer.Deserialize<Supplement>(File.ReadAllText(Path.Combine(fixtures, "recall-broad-v1.json")), Json)!;
        Assert.Equal(12, supplement.Queries.Count);
        var memories = corpus.Entries.ToDictionary(e => e.Id);
        using var embedding = new OnnxEmbeddingService();
        var vectors = memories.ToDictionary(e => e.Key, e => embedding.Embed(e.Value.Text));
        var rows = new List<Observation>();
        foreach (var question in supplement.Queries)
        {
            Assert.All(question.Grades, g => { Assert.True(memories.ContainsKey(g.Key)); Assert.InRange(g.Value, 1, 2); });
            Assert.All(question.Facets.Values.SelectMany(x => x), id => Assert.True(question.Grades.ContainsKey(id)));
            string[]? direct = null;
            foreach (string route in new[] { "direct", "expert", "broadcast" })
            {
                using var store = new RecallTestStore(embedding);
                foreach (var memory in memories.Values)
                    store.Index.Upsert(new CognitiveEntry(memory.Id, vectors[memory.Id], memory.Ns, memory.Text,
                        lifecycleState: memory.State) { IsSummaryNode = memory.Summary });
                foreach (var edge in corpus.Edges) store.Graph.AddEdge(new GraphEdge(edge[0], edge[1], "related_to"));
                if (route == "expert") store.Dispatcher.CreateExpert(question.Ns["expert_".Length..], question.Query);
                var watch = Stopwatch.StartNew();
                var result = Assert.IsType<RecallResult>(store.Tools().Recall(question.Query, route == "direct" ? question.Ns : null));
                watch.Stop();
                string[] ids = result.Results.Select(r => r.Id).ToArray();
                if (route == "direct") direct = ids;
                if (route == "expert") Assert.Equal(direct, ids);
                Assert.InRange(ids.Length, 1, 5);
                Assert.Null(result.Conflicts);
                double Gain(IEnumerable<int> grades) => grades.Take(5).Select((g, i) => (Math.Pow(2, g) - 1) / Math.Log2(i + 2)).Sum();
                int facets = question.Facets.Count(f => f.Value.Intersect(ids).Any());
                rows.Add(new(question.Id, route, ids, facets, (double)facets / question.Facets.Count,
                    Gain(ids.Select(id => question.Grades.GetValueOrDefault(id))) / Gain(question.Grades.Values.OrderDescending()),
                    ids.Count(id => question.Grades.GetValueOrDefault(id) == 2),
                    ids.Count(id => !question.Grades.ContainsKey(id)), watch.Elapsed.TotalMilliseconds));
            }
        }
        string directory = Environment.GetEnvironmentVariable("ENGRAM_RECALL_EVAL_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"recall-realistic-broad-coverage-net{Environment.Version.Major}.json");
        var groups = rows.GroupBy(r => r.Route).Select(g => new { Route = g.Key, FacetCoverage = g.Average(r => r.FacetCoverage),
            GradedNdcg = g.Average(r => r.GradedNdcg), SourceCount = g.Average(r => r.SourceCount), IrrelevantCount = g.Average(r => r.IrrelevantCount) });
        File.WriteAllText(path, JsonSerializer.Serialize(new { Definition = "Fresh store per query/route; original corpus and public defaults; graded supplement, not blind validation. Latency includes embedding/retrieval/updates, excludes seeding.", Groups = groups, Observations = rows }, Json));
        output.WriteLine(path);
        Assert.All(rows, row => Assert.True(row.Facets >= supplement.Queries.Single(q => q.Id == row.Id).MinFacets,
            $"{row.Id}/{row.Route}: only {row.Facets} facets; inspect {path}"));
        var baseline = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(Path.Combine(fixtures, "recall-broad-v1.baseline.json")), Json)!;
        Assert.Equal(rows.Count, baseline.Observations.Count);
        foreach (var row in rows)
        {
            var before = Assert.Single(baseline.Observations, b => b.Id == row.Id && b.Route == row.Route);
            Assert.True(row.Facets >= before.Facets && row.GradedNdcg + .0001 >= before.GradedNdcg,
                $"Broad quality regression for {row.Id}/{row.Route}; inspect {path} before changing labels or floors.");
        }
    }

    private sealed record Corpus(List<Memory> Entries, List<string[]> Edges);
    private sealed record Memory(string Id, string Ns, string Text, string State, bool Summary);
    private sealed record Supplement(List<Question> Queries);
    private sealed record Baseline(List<Reference> Observations);
    private sealed record Reference(string Id, string Route, int Facets, double GradedNdcg);
    private sealed record Question(string Id, string Ns, string Query, Dictionary<string, int> Grades, Dictionary<string, string[]> Facets, int MinFacets);
    private sealed record Observation(string Id, string Route, string[] ReturnedIds, int Facets, double FacetCoverage,
        double GradedNdcg, int SourceCount, int IrrelevantCount, double Milliseconds);
}
