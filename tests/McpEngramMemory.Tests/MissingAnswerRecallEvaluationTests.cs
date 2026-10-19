using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class MissingAnswerRecallEvaluationTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    [Fact]
    public void MissingAnswersAndScopeMismatchesAreNotReturnedAsFactualEvidence()
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var corpus = JsonSerializer.Deserialize<Corpus>(File.ReadAllText(Path.Combine(fixtures, "recall-realistic-v1.json")), Json)!;
        var cases = JsonSerializer.Deserialize<Supplement>(File.ReadAllText(Path.Combine(fixtures, "recall-missing-answer-v1.json")), Json)!.Queries;
        Assert.Equal(24, cases.Count);
        using var embedding = new OnnxEmbeddingService();
        var vectors = corpus.Entries.ToDictionary(m => m.Id, m => embedding.Embed(m.Text));
        var rows = new List<Observation>();
        foreach (var question in cases)
        foreach (bool supplyAnswer in question.Answer is null ? new[] { false } : new[] { false, true })
        {
            float[]? answerVector = supplyAnswer ? embedding.Embed(question.Answer!) : null;
            string? expected = supplyAnswer ? "supplement-answer" : question.Expected;
            string[]? directIds = null;
            string? directStatus = null;
            foreach (string route in new[] { "direct", "expert", "broadcast" })
            {
                using var store = new RecallTestStore(embedding);
                foreach (var memory in corpus.Entries)
                    store.Index.Upsert(new CognitiveEntry(memory.Id, vectors[memory.Id], memory.Ns, memory.Text,
                        lifecycleState: memory.State) { IsSummaryNode = memory.Summary });
                foreach (var edge in corpus.Edges) store.Graph.AddEdge(new GraphEdge(edge[0], edge[1], "related_to"));
                if (supplyAnswer)
                    store.Index.Upsert(new CognitiveEntry("supplement-answer", answerVector!, question.Ns, question.Answer, lifecycleState: "ltm"));
                // As in the existing retrieval evaluation, expert selection gets a perfect hint.
                if (route == "expert") store.Dispatcher.CreateExpert(question.Ns["expert_".Length..], question.Query);
                var result = Assert.IsType<RecallResult>(store.Tools().Recall(question.Query, route == "direct" ? question.Ns : null));
                string[] ids = result.Results.Select(r => r.Id).ToArray();
                var payload = JsonSerializer.SerializeToElement(result);
                string? status = payload.TryGetProperty("queryEvidence", out var evidence) ? evidence.GetProperty("status").GetString() : null;
                if (route == "direct") { directIds = ids; directStatus = status; }
                if (route == "expert") { Assert.Equal(directIds, ids); Assert.Equal(directStatus, status); }
                rows.Add(new(question.Id, question.Kind, supplyAnswer, route, expected, ids, status,
                    expected is not null && ids.Contains(expected)));
            }
        }
        string directory = Environment.GetEnvironmentVariable("ENGRAM_RECALL_EVAL_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"recall-realistic-missing-answer-net{Environment.Version.Major}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { Definition = "Unchanged corpus, real ONNX, public defaults, fresh store per case/route. Negative cases paired with supplied answers; inspected authored regressions, not general entailment accuracy.",
            Missing = rows.Count(r => r.Expected is null), CorrectAbstentions = rows.Count(r => r.Expected is null && r.ReturnedIds.Length == 0 && r.Status == "insufficient_evidence"),
            Answerable = rows.Count(r => r.Expected is not null), Hits = rows.Count(r => r.Hit), Observations = rows }, Json));
        output.WriteLine(path);
        Assert.Equal(120, rows.Count);
        Assert.All(rows, r => Assert.True(r.Expected is null
            ? r.ReturnedIds.Length == 0 && r.Status == "insufficient_evidence"
            : r.Hit && r.Status is null, $"{r.Id}/{r.Route}/supplied={r.Supplied}: inspect {path}"));
    }

    private sealed record Corpus(List<Memory> Entries, List<string[]> Edges);
    private sealed record Memory(string Id, string Ns, string Text, string State, bool Summary);
    private sealed record Supplement(List<Question> Queries);
    private sealed record Question(string Id, string Kind, string Ns, string Query, string? Expected, string? Answer);
    private sealed record Observation(string Id, string Kind, bool Supplied, string Route, string? Expected, string[] ReturnedIds, string? Status, bool Hit);
}
