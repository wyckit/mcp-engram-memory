using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class NonAnswerRecallEvaluationTests(ITestOutputHelper output)
{
    [Fact]
    public void PopulatedExpertsRequireEvidenceBeyondReferenceOnlyNotes()
    {
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
        T Read<T>(string file) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)), json)!;
        var corpus = Read<Corpus>("recall-realistic-v1.json");
        var cases = Read<Cases>("recall-non-answer-v1.json").Questions;
        var profiles = Read<ProfileSet>("recall-expert-routing-v1.json").Profiles;
        using var embedding = new OnnxEmbeddingService();
        var vectors = corpus.Entries.ToDictionary(m => m.Id, m => embedding.Embed(m.Text));
        var personas = profiles.ToDictionary(p => p.Id, p => embedding.Embed(p.Description));
        var notes = cases.ToDictionary(q => q.Id, q => embedding.Embed(q.NonAnswer));
        var supplied = cases.ToDictionary(q => q.Id, q => q.NonAnswer + " " + corpus.Entries.Single(m => m.Id == q.Answer).Text);
        var suppliedVectors = supplied.ToDictionary(p => p.Key, p => embedding.Embed(p.Value));
        var rows = new List<Observation>();
        foreach (var q in cases)
        foreach (string scenario in new[] { "available", "missing", "supplied-in-note" })
        foreach (string mode in new[] { "direct", "broadcast", "populated-experts" })
        foreach (bool restart in new[] { false, true })
        {
            using var store = new RecallTestStore(embedding);
            if (scenario == "available")
            {
                foreach (var m in corpus.Entries)
                    store.Index.Upsert(new CognitiveEntry(m.Id, vectors[m.Id], m.Ns, m.Text, lifecycleState: m.State) { IsSummaryNode = m.Summary });
                foreach (var edge in corpus.Edges) store.Graph.AddEdge(new GraphEdge(edge[0], edge[1], "related_to"));
            }
            foreach (var other in cases)
            {
                bool addAnswer = scenario == "supplied-in-note" && other.Id == q.Id;
                store.Index.Upsert(new CognitiveEntry("note-" + other.Id, addAnswer ? suppliedVectors[other.Id] : notes[other.Id],
                    other.ExpertNs, addAnswer ? supplied[other.Id] : other.NonAnswer, lifecycleState: "ltm"));
            }
            if (mode == "populated-experts")
                foreach (var p in profiles)
                    store.Index.Upsert(new CognitiveEntry(p.Id, personas[p.Id], ExpertDispatcher.SystemNamespace, p.Description,
                        "expert-profile", new Dictionary<string, string> { ["targetNamespace"] = "expert_" + p.Id }, lifecycleState: "ltm") { IsSummaryNode = true });
            if (restart) store.Restart();
            var result = Assert.IsType<RecallResult>(store.Tools().Recall(q.Query, mode == "direct" ? scenario == "available" ? q.Ns : q.ExpertNs : null));
            string? expected = scenario == "available" ? q.Answer : scenario == "supplied-in-note" ? "note-" + q.Id : null;
            rows.Add(new(q.Id, scenario, mode, restart, expected, result.Strategy, result.Namespace,
                result.Results.Select(r => r.Id).ToArray(), result.QueryEvidence?.Status));
        }
        string directory = Environment.GetEnvironmentVariable("ENGRAM_RECALL_EVAL_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"recall-realistic-non-answer-net{Environment.Version.Major}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Definition = "Frozen six post43 cases, original 52-memory corpus and six fixed personas. Real ONNX and public defaults. Available answers, missing answers and answers supplied within reference notes across three routes before/after restart. Authored fault injection, not production accuracy.",
            Groups = rows.GroupBy(r => (r.Scenario, r.Mode)).Select(g => new { g.Key.Scenario, g.Key.Mode, Cases = g.Count(),
                Hits = g.Count(r => r.Expected is not null && r.ReturnedIds.Contains(r.Expected)),
                CorrectAbstentions = g.Count(r => r.Expected is null && r.ReturnedIds.Length == 0 && r.Status == "insufficient_evidence") }),
            Observations = rows
        }, json));
        output.WriteLine(path);
        Assert.Equal(108, rows.Count);
        Assert.All(rows, r => Assert.True(r.Expected is null
            ? r.ReturnedIds.Length == 0 && r.Status == "insufficient_evidence"
            : r.ReturnedIds.Contains(r.Expected) && r.Status is null && r.ReturnedIds.All(id => !id.StartsWith("note-", StringComparison.Ordinal) || id == r.Expected),
            $"{r.Id}/{r.Scenario}/{r.Mode}/restart={r.Restart}: inspect {path}"));
    }

    private sealed record Corpus(List<Memory> Entries, List<string[]> Edges);
    private sealed record Memory(string Id, string Ns, string Text, string State, bool Summary);
    private sealed record Cases(List<Question> Questions);
    private sealed record Question(string Id, string Ns, string ExpertNs, string Query, string Answer, string NonAnswer);
    private sealed record ProfileSet(List<Profile> Profiles);
    private sealed record Profile(string Id, string Description);
    private sealed record Observation(string Id, string Scenario, string Mode, bool Restart, string? Expected,
        string Strategy, string? Namespace, string[] ReturnedIds, string? Status);
}
