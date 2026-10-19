using System.Diagnostics;
using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Tools;
using static McpEngramMemory.Tests.RecallResolutionTests;

namespace McpEngramMemory.Tests;

public class RecallResolutionEvaluationTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    [Fact]
    public async Task EvaluateGovernedResolutionAlongsideTheOriginalRecallCorpus()
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var corpus = JsonSerializer.Deserialize<Corpus>(File.ReadAllText(Path.Combine(fixtures, "recall-realistic-v1.json")), Json)!;
        var supplement = JsonSerializer.Deserialize<Supplement>(File.ReadAllText(Path.Combine(fixtures, "recall-resolution-v1.json")), Json)!;
        Assert.Equal(1, supplement.Version);
        Assert.Equal(26, supplement.Cases.Count);
        Assert.Equal(12, supplement.Cases.Count(c => c.Resolved));
        using var embedding = new OnnxEmbeddingService();
        var vectors = corpus.Entries.ToDictionary(e => e.Id, e => embedding.Embed(e.Text));
        var rows = new List<Observation>();
        foreach (var test in supplement.Cases)
        {
            foreach (string route in new[] { "direct", "broadcast" })
            {
                using var store = new RecallTestStore(embedding);
                foreach (var ns in corpus.Entries.Select(e => e.Ns).Append(Ns).Distinct())
                    store.Registry.EnsureOwnership(ns, Agent, Tenant);
                foreach (var entry in corpus.Entries)
                    store.Index.Upsert(new CognitiveEntry(entry.Id, vectors[entry.Id], entry.Ns, entry.Text,
                        lifecycleState: entry.State, tenantId: Tenant) { IsSummaryNode = entry.Summary });
                foreach (var edge in corpus.Edges) store.Graph.AddEdge(new GraphEdge(edge[0], edge[1], "related_to", tenantId: Tenant));
                Add("a", test.A); Add("b", test.B);
                if (test.Mode != "none")
                {
                    string sourceId = test.Mode == "self" ? test.Support : "source";
                    if (test.Mode != "self") Add(sourceId, test.Source);
                    await Link(test.Support, sourceId, test.Mode == "disputed" ? ["a"] : null);
                    switch (test.Mode)
                    {
                        case "changed": Add("source", test.A); break;
                        case "missing": store.Index.Delete("source", Ns, Tenant); break;
                        case "archived": store.Index.SetLifecycleState("source", "archived", Ns, Tenant); break;
                        case "both": Add("source-a", test.A); await Link("a", "source-a"); break;
                        case "private": Assert.Equal("shared", store.Registry.Share(Ns, Agent, "bob", "read", Tenant).Status); break;
                        case "stale": await Promote(store, "knowledge-" + test.Support, test.B, ["source"], ["a"]); break;
                    }
                }
                if (test.Restart) store.Restart();
                string agent = test.Mode == "private" ? "bob" : Agent;
                string? requestedNs = route == "direct" ? Ns : null;
                var baseline = Assert.IsType<RecallResult>(store.Tools(agent, Tenant, new EmptyKnowledgeStore())
                    .Recall(test.Query, requestedNs, k: test.K));
                var timer = Stopwatch.StartNew();
                var result = Assert.IsType<RecallResult>(store.Tools(agent, Tenant).Recall(test.Query, requestedNs, k: test.K));
                timer.Stop();
                var pair = result.Conflicts?.SingleOrDefault(c => c.Evidence.Select(e => e.Id).Order().SequenceEqual(new[] { "a", "b" }));
                rows.Add(new(test.Id, route, test.Resolved, pair?.Status == "resolved", pair?.Resolution?.PreferredId,
                    pair?.Resolution?.Sources.Select(s => s.Id).ToArray() ?? [], pair is not null,
                    result.Results.Select(r => r.Id).ToArray(), result.Results.Select(r => r.Score).ToArray(),
                    baseline.Results.Select(r => (r.Id, r.Score)).SequenceEqual(result.Results.Select(r => (r.Id, r.Score))),
                    result.Conflicts, test.K, test.Restart, timer.Elapsed.TotalMilliseconds));
                Assert.InRange(result.Results.Count, 0, test.K);
                Assert.Equal(test.A, store.Index.Get("a", Ns, Tenant)!.Text);
                Assert.Equal(test.B, store.Index.Get("b", Ns, Tenant)!.Text);

                void Add(string id, string text) => store.Index.Upsert(new CognitiveEntry(id, embedding.Embed(text), Ns, text,
                    lifecycleState: "ltm", tenantId: Tenant));
                async Task Link(string candidate, string sourceId, string[]? contradictions = null)
                {
                    var snapshot = await Promote(store, "knowledge-" + candidate, candidate == "a" ? test.A : test.B, [sourceId], contradictions);
                    var entry = store.Index.Get(candidate, Ns, Tenant)!;
                    entry.Metadata["recallKnowledge"] = JsonSerializer.Serialize(new { id = "knowledge-" + candidate, contentHash = snapshot.ActiveVersion!.ContentHash });
                    store.Index.Upsert(entry);
                }
            }
        }
        var report = new
        {
            supplement.Version, Model = "bundled bge-micro-v2 ONNX", Framework = AppContext.TargetFrameworkName,
            BaseMemories = corpus.Entries.Count, Cases = supplement.Cases.Count,
            Definition = "Fresh tenant-scoped store per case/route; unchanged 52-memory corpus plus conflicting claims and cited evidence; actual governed promotion. Direct and authorized broadcast routes. Expert routing is intentionally disabled for authenticated tenants. Inspected regression cases, not blind factual validation.",
            Resolved = rows.Count(r => r.ExpectedResolved && r.Resolved),
            RetrievalMisses = rows.Count(r => r.ExpectedResolved && !r.ConflictSurfaced),
            MissedVisibleResolutions = rows.Count(r => r.ExpectedResolved && r.ConflictSurfaced && !r.Resolved),
            FalseResolutions = rows.Count(r => !r.ExpectedResolved && r.Resolved), Observations = rows
        };
        string directory = Environment.GetEnvironmentVariable("ENGRAM_RECALL_EVAL_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"recall-realistic-resolution-net{Environment.Version.Major}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, Json));
        output.WriteLine($"Resolution evaluation: {path}; resolved={report.Resolved}, retrieval misses={report.RetrievalMisses}, missed visible={report.MissedVisibleResolutions}, false={report.FalseResolutions}");
        // Retain the initial k=1/5 misses instead of relabeling them as successful
        // resolutions. Wider companion cases exercise the same facts with more context.
        string[] knownRetrievalMisses = ["multiple-claims/broadcast", "restart/broadcast", "restart/direct"];
        Assert.Equal(knownRetrievalMisses, rows.Where(r => !r.ConflictSurfaced).Select(r => r.Id + "/" + r.Route).Order());
        Assert.All(rows.Where(r => r.ConflictSurfaced), r => Assert.Equal(r.ExpectedResolved, r.Resolved));
        Assert.All(rows.Where(r => !r.ConflictSurfaced), r => Assert.False(r.Resolved));
        Assert.All(rows, r => Assert.True(r.RankingUnchanged, $"Ranking changed: {r.Id}/{r.Route}"));
        Assert.All(rows.Where(r => r.Resolved), r =>
        {
            Assert.Equal(supplement.Cases.Single(c => c.Id == r.Id).Support, r.PreferredId);
            Assert.Contains("source", r.SourceIds);
        });
    }

    private sealed record Corpus(List<Memory> Entries, List<string[]> Edges);
    private sealed record Memory(string Id, string Ns, string Text, string State, bool Summary);
    private sealed record Supplement(int Version, List<Case> Cases);
    private sealed record Case(string Id, string Query, string A, string B, string Source, string Support,
        bool Resolved, int K, bool Restart, string? Mode);
    private sealed record Observation(string Id, string Route, bool ExpectedResolved, bool Resolved, string? PreferredId,
        string[] SourceIds, bool ConflictSurfaced, string[] ReturnedIds, float[] Scores, bool RankingUnchanged,
        IReadOnlyList<RecallConflict>? Conflicts, int K, bool Restart, double Milliseconds);
}
