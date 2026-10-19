using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class ExpertRoutingRecallEvaluationTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    [Fact]
    public void FixedPersonasAndEmptyOverlappingExpertsRetainAvailableRecallEvidence()
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        T Read<T>(string file) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(fixtures, file)), Json)!;
        var corpus = Read<Corpus>("recall-realistic-v1.json");
        var supplement = Read<Supplement>("recall-question-interpretation-v1.json");
        var profiles = Read<ProfileSet>("recall-expert-routing-v1.json").Profiles;
        Assert.Equal(6, profiles.Count);
        Assert.Equal(3, profiles.Count(p => p.Overlap));
        var cases = corpus.Queries.Select(q => new Scenario("original-" + q.Id, "original", q.Ns, q.Query,
            q.Relevant, [], q.Restart, null)).ToList();
        foreach (var q in supplement.Questions)
        {
            cases.Add(new(q.Id, q.Kind, q.Ns, q.Query, q.Relevant, q.AcceptableAlternatives ?? [], false, null));
            if (q.Answer is not null)
                cases.Add(new(q.Id + "-supplied", "supplied", q.Ns, q.Query, ["supplement-answer"], [], false, q.Answer));
        }
        Assert.Equal(76, cases.Count);
        using var embedding = new OnnxEmbeddingService();
        var vectors = corpus.Entries.ToDictionary(m => m.Id, m => embedding.Embed(m.Text));
        var personas = profiles.ToDictionary(p => p.Id, p => embedding.Embed(p.Description));
        var rows = new List<Observation>();
        foreach (var question in cases)
        {
            var queryVector = embedding.Embed(question.Query);
            var answerVector = question.Answer is null ? null : embedding.Embed(question.Answer);
            foreach (string mode in new[] { "direct", "broadcast", "fixed-profiles", "overlapping-empty-profiles" })
            {
                using var store = new RecallTestStore(embedding);
                foreach (var memory in corpus.Entries)
                    store.Index.Upsert(new CognitiveEntry(memory.Id, vectors[memory.Id], memory.Ns, memory.Text,
                        lifecycleState: memory.State) { IsSummaryNode = memory.Summary });
                foreach (var edge in corpus.Edges) store.Graph.AddEdge(new GraphEdge(edge[0], edge[1], "related_to"));
                if (question.Answer is not null)
                    store.Index.Upsert(new CognitiveEntry("supplement-answer", answerVector!, question.Ns, question.Answer, lifecycleState: "ltm"));
                if (mode is "fixed-profiles" or "overlapping-empty-profiles")
                    foreach (var p in profiles.Where(p => !p.Overlap || mode == "overlapping-empty-profiles"))
                        store.Index.Upsert(new CognitiveEntry(p.Id, personas[p.Id], ExpertDispatcher.SystemNamespace, p.Description,
                            "expert-profile", new Dictionary<string, string> { ["targetNamespace"] = "expert_" + p.Id }, lifecycleState: "ltm")
                            { IsSummaryNode = true });
                if (question.Restart) store.Restart();
                var (status, candidates) = store.Dispatcher.Route(queryVector, topK: 3, threshold: .7f);
                var result = Assert.IsType<RecallResult>(store.Tools().Recall(question.Query, mode == "direct" ? question.Ns : null));
                string[] ids = result.Results.Select(r => r.Id).ToArray();
                rows.Add(new(question.Id, question.Kind, question.Restart, mode,
                    status == "routed" ? candidates.FirstOrDefault()?.ExpertId : null,
                    candidates.Select(c => c.ExpertId).ToArray(), result.Strategy, result.Namespace,
                    question.Relevant, question.Alternatives, ids, result.QueryEvidence?.Status,
                    question.Relevant.Any(ids.Contains), question.Relevant.Concat(question.Alternatives).Any(ids.Contains)));
            }
        }
        var groups = rows.GroupBy(r => (r.Kind, r.Mode)).Select(g => new
        {
            g.Key.Kind, g.Key.Mode, Cases = g.Count(), Answerable = g.Count(r => r.Expected.Length > 0),
            DesignatedHits = g.Count(r => r.DesignatedHit), AcceptedAnswerHits = g.Count(r => r.AcceptedAnswerHit),
            Missing = g.Count(r => r.Expected.Length == 0),
            CorrectAbstentions = g.Count(r => r.Expected.Length == 0 && r.ReturnedIds.Length == 0 && r.Status == "insufficient_evidence")
        }).ToArray();
        string directory = Environment.GetEnvironmentVariable("ENGRAM_RECALL_EVAL_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"recall-realistic-expert-routing-net{Environment.Version.Major}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Definition = "Unchanged 52-memory corpus, 36 original questions (including restarts), frozen 32 interpretation questions and eight supplied answers. Real ONNX/public defaults. Fixed personas from the post-#41 probe, never query-equal hints. Three deliberately empty overlapping specialists are fault injection, not production accuracy. The pre-audited metadata incident remains an explicit alternative.",
            Groups = groups, Observations = rows
        }, Json));
        output.WriteLine(path);
        Assert.Equal(304, rows.Count);
        Assert.Contains(rows, r => r.Mode == "overlapping-empty-profiles" && profiles.Any(p => p.Overlap && p.Id == r.InitialTopExpert));
        Assert.All(rows, r => Assert.True(r.ReturnedIds.Length <= 5 && (r.Expected.Length == 0
            ? r.ReturnedIds.Length == 0 && r.Status == "insufficient_evidence"
            : r.AcceptedAnswerHit && r.Status is null), $"{r.Id}/{r.Mode}: inspect {path}"));
    }

    private sealed record Corpus(List<Memory> Entries, List<string[]> Edges, List<OriginalQuestion> Queries);
    private sealed record Memory(string Id, string Ns, string Text, string State, bool Summary);
    private sealed record OriginalQuestion(string Id, string Ns, string Query, string[] Relevant, bool Restart);
    private sealed record Supplement(List<Question> Questions);
    private sealed record Question(string Id, string Kind, string Ns, string Query, string[] Relevant, string[]? AcceptableAlternatives, string? Answer);
    private sealed record ProfileSet(List<Profile> Profiles);
    private sealed record Profile(string Id, string Description, bool Overlap);
    private sealed record Scenario(string Id, string Kind, string Ns, string Query, string[] Relevant, string[] Alternatives, bool Restart, string? Answer);
    private sealed record Observation(string Id, string Kind, bool Restart, string Mode, string? InitialTopExpert, string[] RoutingCandidates,
        string Strategy, string? SelectedNamespace, string[] Expected, string[] AcceptableAlternatives, string[] ReturnedIds,
        string? Status, bool DesignatedHit, bool AcceptedAnswerHit);
}
