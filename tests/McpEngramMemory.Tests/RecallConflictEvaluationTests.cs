using System.Diagnostics;
using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class RecallConflictEvaluationTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    [Fact]
    public void EvaluateUnmarkedConflictsAlongsideTheOriginalRecallCorpus()
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var corpus = JsonSerializer.Deserialize<Corpus>(File.ReadAllText(Path.Combine(fixtures, "recall-realistic-v1.json")), Json)!;
        var supplement = JsonSerializer.Deserialize<Supplement>(File.ReadAllText(Path.Combine(fixtures, "recall-conflicts-v1.json")), Json)!;
        Assert.Equal(1, supplement.Version);
        Assert.Equal(16, supplement.Cases.Count);
        Assert.Equal(8, supplement.Cases.Count(c => c.Conflict));
        Assert.Equal(16, supplement.Cases.Select(c => c.Id).Distinct().Count());
        using var embedding = new OnnxEmbeddingService();
        var vectors = corpus.Entries.ToDictionary(e => e.Id, e => embedding.Embed(e.Text));
        var rows = new List<Observation>();
        var parityFailures = new List<string>();
        foreach (var test in supplement.Cases)
        {
            var a = embedding.Embed(test.A);
            var b = embedding.Embed(test.B);
            RecallResult? direct = null;
            foreach (string route in new[] { "direct", "expert", "broadcast" })
            {
                using var store = new RecallTestStore(embedding);
                foreach (var entry in corpus.Entries)
                    store.Index.Upsert(new CognitiveEntry(entry.Id, vectors[entry.Id], entry.Ns, entry.Text,
                        lifecycleState: entry.State) { IsSummaryNode = entry.Summary });
                foreach (var edge in corpus.Edges) store.Graph.AddEdge(new GraphEdge(edge[0], edge[1], "related_to"));
                // Two opaque IDs, no labels, dates, metadata, or graph links. Both assertions
                // are unmarked; the evaluator does not designate a factual winner.
                store.Index.Upsert(new CognitiveEntry("evidence-z", a, "expert_atlas", test.A, lifecycleState: "ltm"));
                store.Index.Upsert(new CognitiveEntry("evidence-a", b, "expert_atlas", test.B, lifecycleState: "ltm"));
                if (route == "expert") store.Dispatcher.CreateExpert("atlas", test.Query);
                if (test.Restart) store.Restart();
                var timer = Stopwatch.StartNew();
                var result = Assert.IsType<RecallResult>(store.Tools().Recall(test.Query,
                    route == "direct" ? "expert_atlas" : null, k: test.K));
                timer.Stop();
                if (route == "direct") direct = result;
                if (route == "expert" &&
                    (!direct!.Results.Select(r => (r.Id, r.Score)).SequenceEqual(result.Results.Select(r => (r.Id, r.Score))) ||
                     JsonSerializer.Serialize(direct.Conflicts, Json) != JsonSerializer.Serialize(result.Conflicts, Json)))
                    parityFailures.Add(test.Id);
                bool surfaced = result.Conflicts?.Any(c => c.Status == "unresolved" &&
                    c.Evidence.Select(e => e.Id).Order().SequenceEqual(new[] { "evidence-a", "evidence-z" }) &&
                    c.Evidence.All(e => e.Namespace == "expert_atlas")) == true;
                rows.Add(new(test.Id, route, test.Conflict, surfaced, result.Conflicts?.Count ?? 0,
                    result.Results.Select(r => r.Id).ToArray(), result.Results.Select(r => r.Score).ToArray(),
                    result.Conflicts, test.K, test.Restart, timer.Elapsed.TotalMilliseconds));
                Assert.InRange(result.Results.Count, 0, test.K);
                Assert.Equal(test.A, store.Index.Get("evidence-z", "expert_atlas", "")!.Text);
                Assert.Equal(test.B, store.Index.Get("evidence-a", "expert_atlas", "")!.Text);
            }
        }
        var report = new
        {
            supplement.Version, Model = "bundled bge-micro-v2 ONNX", Framework = AppContext.TargetFrameworkName,
            BaseMemories = corpus.Entries.Count, AddedMemoriesPerCase = 2, Cases = supplement.Cases.Count,
            Definition = "Fresh store per case/route; original 52-memory corpus plus two unmarked claims. Perfect expert routing hint. Inspected regression cases, not blind validation. No factual winner label.",
            TruePositives = rows.Count(r => r.ExpectedConflict && r.Surfaced),
            FalseNegatives = rows.Count(r => r.ExpectedConflict && !r.Surfaced),
            FalsePositives = rows.Count(r => !r.ExpectedConflict && r.ConflictCount > 0),
            ParityFailures = parityFailures, Observations = rows
        };
        string directory = Environment.GetEnvironmentVariable("ENGRAM_RECALL_EVAL_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"recall-realistic-conflicts-net{Environment.Version.Major}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, Json));
        output.WriteLine($"Conflict evaluation: {path}; TP={report.TruePositives}, FN={report.FalseNegatives}, FP={report.FalsePositives}");
        Assert.Empty(parityFailures);
        Assert.All(rows.Where(r => r.ExpectedConflict), r => Assert.True(r.Surfaced, $"Missing conflict: {r.Id}/{r.Route}; inspect {path}."));
        Assert.All(rows.Where(r => !r.ExpectedConflict), r => Assert.Equal(0, r.ConflictCount));
    }

    private sealed record Corpus(List<Memory> Entries, List<string[]> Edges);
    private sealed record Memory(string Id, string Ns, string Text, string State, bool Summary);
    private sealed record Supplement(int Version, List<Case> Cases);
    private sealed record Case(string Id, string Query, string A, string B, bool Conflict, int K, bool Restart);
    private sealed record Observation(string Id, string Route, bool ExpectedConflict, bool Surfaced, int ConflictCount,
        string[] ReturnedIds, float[] Scores, IReadOnlyList<RecallConflict>? Conflicts, int K, bool Restart, double Milliseconds);
}
