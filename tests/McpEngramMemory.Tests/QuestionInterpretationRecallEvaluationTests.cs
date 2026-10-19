using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class QuestionInterpretationRecallEvaluationTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    [Fact]
    public void SevenPreviouslyEmptyParaphrasesRecoverTheirIsolatedSource()
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var corpus = JsonSerializer.Deserialize<Corpus>(File.ReadAllText(Path.Combine(fixtures, "recall-realistic-v1.json")), Json)!;
        var cases = JsonSerializer.Deserialize<Supplement>(File.ReadAllText(Path.Combine(fixtures, "recall-question-interpretation-v1.json")), Json)!.Questions;
        string[] isolated = ["owner-titlecase", "endpoint-url", "endpoint-path", "timeout-wait", "retention-period", "cache-fields", "recovery-quickly"];
        using var embedding = new OnnxEmbeddingService();
        foreach (string id in isolated)
        {
            var question = cases.Single(q => q.Id == id);
            var source = corpus.Entries.Single(m => m.Id == Assert.Single(question.Relevant));
            var vector = embedding.Embed(source.Text);
            foreach (string route in new[] { "direct", "expert", "broadcast" })
            {
                using var store = new RecallTestStore(embedding);
                store.Index.Upsert(new CognitiveEntry(source.Id, vector, source.Ns, source.Text, lifecycleState: source.State));
                if (route == "expert") store.Dispatcher.CreateExpert(source.Ns["expert_".Length..], question.Query);
                var result = Assert.IsType<RecallResult>(store.Tools().Recall(question.Query, route == "direct" ? source.Ns : null));
                Assert.Equal(source.Id, Assert.Single(result.Results).Id);
                Assert.Null(result.QueryEvidence);
            }
        }
    }

    [Fact]
    public void ParaphrasesRetrieveAnswersWithoutAdmittingMissingOrWrongScopeEvidence()
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var corpus = JsonSerializer.Deserialize<Corpus>(File.ReadAllText(Path.Combine(fixtures, "recall-realistic-v1.json")), Json)!;
        var cases = JsonSerializer.Deserialize<Supplement>(File.ReadAllText(Path.Combine(fixtures, "recall-question-interpretation-v1.json")), Json)!.Questions;
        Assert.Equal(32, cases.Count);
        using var embedding = new OnnxEmbeddingService();
        var vectors = corpus.Entries.ToDictionary(m => m.Id, m => embedding.Embed(m.Text));
        var rows = new List<Observation>();
        foreach (var question in cases)
        foreach (bool supplied in question.Answer is null ? new[] { false } : new[] { false, true })
        {
            float[]? answerVector = supplied ? embedding.Embed(question.Answer!) : null;
            string[] expected = supplied ? ["supplement-answer"] : question.Relevant;
            string[] alternatives = supplied ? [] : question.AcceptableAlternatives ?? [];
            string[]? directIds = null;
            foreach (string route in new[] { "direct", "expert", "broadcast" })
            {
                using var store = new RecallTestStore(embedding);
                foreach (var memory in corpus.Entries)
                    store.Index.Upsert(new CognitiveEntry(memory.Id, vectors[memory.Id], memory.Ns, memory.Text,
                        lifecycleState: memory.State) { IsSummaryNode = memory.Summary });
                foreach (var edge in corpus.Edges) store.Graph.AddEdge(new GraphEdge(edge[0], edge[1], "related_to"));
                if (supplied)
                    store.Index.Upsert(new CognitiveEntry("supplement-answer", answerVector!, question.Ns, question.Answer, lifecycleState: "ltm"));
                // Tests interpretation after expert selection, not routing quality or fallback.
                if (route == "expert") store.Dispatcher.CreateExpert(question.Ns["expert_".Length..], question.Query);
                var result = Assert.IsType<RecallResult>(store.Tools().Recall(question.Query, route == "direct" ? question.Ns : null));
                string[] ids = result.Results.Select(r => r.Id).ToArray();
                if (route == "direct") directIds = ids;
                rows.Add(new(question.Id, question.Kind, supplied, route, expected, alternatives, ids,
                    result.QueryEvidence?.Status, expected.Any(ids.Contains),
                    expected.Concat(alternatives).Any(ids.Contains), route != "expert" || ids.SequenceEqual(directIds!)));
            }
        }
        string directory = Environment.GetEnvironmentVariable("ENGRAM_RECALL_EVAL_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"recall-realistic-question-interpretation-net{Environment.Version.Major}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Definition = "Unchanged 52-memory corpus, real ONNX, public defaults, fresh store per case/route. Frozen post-#41 assessment labels with the pre-audited cache incident alternative reported explicitly. Inspected regressions, not general entailment accuracy.",
            Missing = rows.Count(r => r.Expected.Length == 0),
            CorrectAbstentions = rows.Count(r => r.Expected.Length == 0 && r.ReturnedIds.Length == 0 && r.Status == "insufficient_evidence"),
            Answerable = rows.Count(r => r.Expected.Length > 0),
            DesignatedHits = rows.Count(r => r.DesignatedHit), AcceptedAnswerHits = rows.Count(r => r.AcceptedAnswerHit),
            Observations = rows
        }, Json));
        output.WriteLine(path);
        Assert.Equal(120, rows.Count);
        Assert.All(rows, r => Assert.True(r.RouteParity && (r.Expected.Length == 0
            ? r.ReturnedIds.Length == 0 && r.Status == "insufficient_evidence"
            : r.AcceptedAnswerHit && r.Status is null), $"{r.Id}/{r.Route}/supplied={r.Supplied}: inspect {path}"));
    }

    private sealed record Corpus(List<Memory> Entries, List<string[]> Edges);
    private sealed record Memory(string Id, string Ns, string Text, string State, bool Summary);
    private sealed record Supplement(List<Question> Questions);
    private sealed record Question(string Id, string Kind, string Ns, string Query, string[] Relevant, string[]? AcceptableAlternatives, string? Answer);
    private sealed record Observation(string Id, string Kind, bool Supplied, string Route, string[] Expected, string[] AcceptableAlternatives,
        string[] ReturnedIds, string? Status, bool DesignatedHit, bool AcceptedAnswerHit, bool RouteParity);
}
