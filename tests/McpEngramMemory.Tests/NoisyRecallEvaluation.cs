using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

internal static class NoisyRecallEvaluation
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    internal sealed record Memory(string Id, string Ns, string Text, string Project, string Environment, string State, bool Obsolete);
    internal sealed record Profile(string Id, string Description);
    internal sealed record Question(string Id, string Query, string Ns, string Project, string Environment, string Kind,
        string Split, string[] Expected, Dictionary<string, string[]> Facets, bool Critical, bool History);
    internal sealed record Corpus(int Version, string Description, Memory[] Entries, Profile[] Profiles, Question[] Queries);
    internal sealed record Finding(string Query, string Mode, string Phase, string Reason);
    internal sealed record Observation(string Id, string Split, string Kind, bool Critical, string Mode, string Phase,
        string Strategy, string? SelectedNamespace, string[] RoutingCandidates, string[] Expected, string[] ReturnedIds,
        string[] WrongScopeIds, bool AnswerHit, bool FalseAbstention, bool CorrectAbstention, double? FacetCoverage,
        bool StaleTopOne, string? EvidenceStatus, int ConflictCount, bool UnsupportedResolution,
        double Milliseconds, double EmbeddingMilliseconds, int EmbeddingCalls);

    internal static Corpus Load()
    {
        var corpus = JsonSerializer.Deserialize<Corpus>(File.ReadAllText(FixturePath), Json)!;
        Assert.Equal(1, corpus.Version);
        Assert.Equal(30, corpus.Entries.Length);
        Assert.Equal(26, corpus.Queries.Length);
        Assert.Equal(12, corpus.Queries.Count(q => q.Split == "development"));
        Assert.Equal(14, corpus.Queries.Count(q => q.Split == "evaluation"));
        Assert.Equal(corpus.Entries.Length, corpus.Entries.Select(m => m.Id).Distinct().Count());
        Assert.Equal(corpus.Queries.Length, corpus.Queries.Select(q => q.Id).Distinct().Count());
        var memories = corpus.Entries.ToDictionary(m => m.Id);
        foreach (var q in corpus.Queries)
        {
            Assert.Equal(q.Kind == "negative", q.Expected.Length == 0);
            Assert.All(q.Expected, id =>
            {
                Assert.True(memories.TryGetValue(id, out var memory), $"Unknown label: {q.Id}/{id}");
                Assert.Equal(q.Project, memory.Project);
                if (q.Environment.Length > 0) Assert.Equal(q.Environment, memory.Environment);
                Assert.True(q.History || !memory.Obsolete);
            });
            Assert.All(q.Facets.Values, ids => { Assert.NotEmpty(ids); Assert.All(ids, id => Assert.Contains(id, q.Expected)); });
        }
        return corpus;
    }

    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "recall-noisy-v1.json");
    internal static string OutputDirectory => Environment.GetEnvironmentVariable("ENGRAM_RECALL_EVAL_OUTPUT") ??
        Path.Combine(AppContext.BaseDirectory, "TestResults");
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    internal static Memory[] Generate(Corpus corpus, int count, int namespaceCount)
    {
        Assert.True(count >= corpus.Entries.Length && namespaceCount >= 8);
        string[] core = corpus.Entries.Select(m => m.Ns).Distinct().ToArray();
        string[] projects = ["Juniper", "Kestrel", "Sorrel", "Larch"];
        string[] environments = ["production", "staging", "development", "testing"];
        var result = corpus.Entries.ToList();
        for (int i = 0; result.Count < count; i++)
        {
            string project = projects[i % projects.Length];
            string environment = environments[i / projects.Length % environments.Length];
            int variant = i / 16 % 8;
            // Every record has distinct text. Half compete in the answer namespaces;
            // half increase broadcast fan-out. Layout changes never change text or labels.
            string ns = i % 2 == 0 ? core[i / 2 % core.Length] : $"noise_{i / 2 % (namespaceCount - core.Length):D2}";
            string text = variant switch
            {
                0 => $"{project} {environment} deployment review ticket {i:D5} links to a release checklist; the ticket does not identify a deployment owner.",
                1 => $"{project} {environment} readiness endpoint is documented in monitoring note {i:D5}.",
                2 => $"[superseded] {project} {environment} gateway timeout was {100 + i % 80} seconds in retired test record {i:D5}.",
                3 => $"{project} {environment} recovery planning note {i:D5} asks the team to review backup diagrams and label the screenshots.",
                4 => $"{project} {environment} migration discussion {i:D5} compares queue dashboards and database charts without adopting a setting.",
                5 => $"{project} {environment} payment retry review {i:D5} records a documentation task: refresh the examples in the incident handbook.",
                6 => $"Garden journal {i:D5}: water the seedlings, repair the fence, and catalogue the blue and yellow flowers.",
                _ => $"{project} {environment} operational index {i:D5} lists deployment, readiness, backups and payments as topics for a future team workshop."
            };
            result.Add(new($"noise-{i:D5}", ns, text, variant == 6 ? "" : project,
                variant == 6 ? "" : environment, variant == 2 ? "archived" : "ltm", variant == 2));
        }
        Assert.Equal(count, result.Count);
        Assert.Equal(count, result.Select(m => m.Id).Distinct().Count());
        Assert.Equal(count, result.Select(m => m.Text).Distinct().Count());
        Assert.Equal(namespaceCount, result.Select(m => m.Ns).Distinct().Count());
        return result.ToArray();
    }

    internal static Observation Measure(Question q, string mode, string phase, RecallResult result,
        IReadOnlyDictionary<string, Memory> memories, string[] routingCandidates, double elapsed, double embeddingMs, int embeddingCalls)
    {
        string[] ids = result.Results.Select(r => r.Id).ToArray();
        // IDs in the quality corpus are unique. Verify text as well, so an accidentally
        // substituted same-ID source cannot be counted as the labeled answer.
        Assert.All(result.Results, r => { Assert.True(memories.ContainsKey(r.Id)); Assert.Equal(memories[r.Id].Text, r.Text); });
        string[] wrong = ids.Where(id =>
        {
            var m = memories[id];
            return (m.Project.Length > 0 && m.Project != q.Project) ||
                (q.Environment.Length > 0 && m.Environment.Length > 0 && m.Environment != q.Environment);
        }).ToArray();
        bool answerable = q.Expected.Length > 0;
        return new(q.Id, q.Split, q.Kind, q.Critical, mode, phase, result.Strategy, result.Namespace, routingCandidates,
            q.Expected, ids, wrong, answerable && q.Expected.Any(ids.Contains), answerable && ids.Length == 0,
            !answerable && ids.Length == 0, q.Facets.Count == 0 ? null :
                q.Facets.Values.Count(f => f.Any(ids.Contains)) / (double)q.Facets.Count,
            !q.History && ids.Length > 0 && memories[ids[0]].Obsolete, result.QueryEvidence?.Status,
            result.Conflicts?.Count ?? 0, result.Conflicts?.Any(c => c.Resolution is not null || c.Status == "resolved") == true,
            elapsed, embeddingMs, embeddingCalls);
    }

    internal static Finding[] Blockers(IEnumerable<Observation> rows) => rows.SelectMany(r =>
    {
        var reasons = new List<string>();
        if (r.WrongScopeIds.Length > 0) reasons.Add("project/environment mismatch");
        if (r.UnsupportedResolution) reasons.Add("unsupported conflict resolution");
        if (r.Critical && r.Expected.Length > 0 && !r.AnswerHit) reasons.Add("critical answer missed");
        if (r.Critical && r.Expected.Length == 0 && !r.CorrectAbstention) reasons.Add("critical missing-answer query returned evidence");
        return reasons.Select(reason => new Finding(r.Id, r.Mode, r.Phase, reason));
    }).ToArray();

    internal static async Task<string> Run(Corpus corpus, Memory[] memories, MeasuredEmbedding embedding,
        IReadOnlyDictionary<string, float[]> vectors, double preparationMs, ITestOutputHelper output)
    {
        var startedAt = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(OutputDirectory);
        int namespaceCount = memories.Select(m => m.Ns).Distinct().Count();
        string stem = $"recall-noisy-{memories.Length}-{namespaceCount}-net{Environment.Version.Major}";
        string manifest = JsonSerializer.Serialize(memories, Json);
        File.WriteAllText(Path.Combine(OutputDirectory, stem + "-manifest.json"), manifest);
        var rows = new List<Observation>();
        var costs = new List<object>();
        var lookup = memories.ToDictionary(m => m.Id);
        foreach (string mode in new[] { "direct", "automatic", "broadcast" })
        {
            using var store = new EvaluationStore(embedding);
            var setup = Stopwatch.StartNew();
            Seed(store, memories, vectors);
            if (mode == "automatic")
                foreach (var profile in corpus.Profiles)
                    store.Index.Upsert(new CognitiveEntry(profile.Id, vectors[profile.Description], ExpertDispatcher.SystemNamespace,
                        profile.Description, "expert-profile", new() { ["targetNamespace"] = "expert_" + profile.Id }, lifecycleState: "ltm")
                    { IsSummaryNode = true });
            store.Flush();
            setup.Stop();
            Assert.Equal(memories.Length + (mode == "automatic" ? corpus.Profiles.Length : 0), store.Index.Count);
            double restartMs = 0;
            foreach (string phase in new[] { "warm", "restarted" })
            {
                if (phase == "restarted") restartMs = store.RestartVerified();
                var tools = store.Tools();
                foreach (var q in corpus.Queries)
                {
                    // Diagnostics are outside the timed request and do not record dispatch.
                    var (_, candidates) = store.Dispatcher.Route(embedding.Embed(q.Query), topK: 3, threshold: .7f);
                    long beforeCalls = embedding.Calls;
                    double beforeMs = embedding.Milliseconds;
                    var timer = Stopwatch.StartNew();
                    var result = Assert.IsType<RecallResult>(await tools.RecallAsync(q.Query, mode == "direct" ? q.Ns : null));
                    timer.Stop();
                    Assert.InRange(result.Results.Count, 0, 5);
                    Assert.Equal(1, embedding.Calls - beforeCalls);
                    if (mode == "direct") Assert.Contains(result.Strategy, new[] { "direct", "deep_recall" });
                    if (mode == "broadcast") Assert.Equal("broadcast", result.Strategy);
                    rows.Add(Measure(q, mode, phase, result, lookup, candidates.Select(c => c.ExpertId).ToArray(),
                        timer.Elapsed.TotalMilliseconds, embedding.Milliseconds - beforeMs, (int)(embedding.Calls - beforeCalls)));
                }
            }
            store.Flush();
            costs.Add(new { Mode = mode, SeedAndFlushMilliseconds = setup.Elapsed.TotalMilliseconds,
                RestartVerifyMilliseconds = restartMs, PersistedBytes = store.PersistedBytes, TotalRows = store.Index.Count });
        }
        var protections = await CheckProtections(memories, embedding, vectors);
        var blockers = Blockers(rows);
        var groups = rows.GroupBy(r => (r.Split, r.Mode, r.Phase)).Select(g => new
        {
            g.Key.Split, g.Key.Mode, g.Key.Phase, Cases = g.Count(), Answerable = g.Count(r => r.Expected.Length > 0),
            AnswerHits = g.Count(r => r.AnswerHit), FalseAbstentions = g.Count(r => r.FalseAbstention),
            Missing = g.Count(r => r.Expected.Length == 0), CorrectAbstentions = g.Count(r => r.CorrectAbstention),
            WrongScopeQueries = g.Count(r => r.WrongScopeIds.Length > 0), StaleTopOne = g.Count(r => r.StaleTopOne),
            FacetCoverage = g.Where(r => r.FacetCoverage.HasValue).Select(r => r.FacetCoverage).Average(),
            P50Milliseconds = Percentile(g.Select(r => r.Milliseconds), .50),
            P95Milliseconds = Percentile(g.Select(r => r.Milliseconds), .95)
        }).ToArray();
        string path = Path.Combine(OutputDirectory, stem + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, corpus.Version, FixtureSha256 = FileHash(FixturePath), ManifestSha256 = Hash(manifest),
            StartedAtUtc = startedAt, CompletedAtUtc = DateTimeOffset.UtcNow,
            SourceRevision = System.Environment.GetEnvironmentVariable("ENGRAM_EVAL_REVISION") ?? "working-tree; see test run log",
            GeneratorVersion = 1, Model = "bundled bge-micro-v2 ONNX",
            ModelSha256 = FileHash(Path.Combine(AppContext.BaseDirectory, "LocalEmbeddingsModel", "default", "model.onnx")),
            Framework = AppContext.TargetFrameworkName, OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Processors = System.Environment.ProcessorCount,
            DataMemories = memories.Length, Namespaces = namespaceCount, DistinctTexts = memories.Select(m => m.Text).Distinct().Count(),
            Questions = corpus.Queries.Length, ExpectedObservations = corpus.Queries.Length * 6,
            Definition = "Frozen synthetic questions; fresh store per mode, fixed ordered session before/after verified restart. Query time includes real embedding and access updates, excludes setup and route diagnostics. Pre-embedding is shared across layouts. No production tuning or new quality baseline. CI execution/safety pass does not imply release readiness.",
            EmbeddingPreparationMilliseconds = preparationMs, Costs = costs, Protections = protections,
            ReleaseReady = blockers.Length == 0, ReleaseBlockers = blockers, Groups = groups, Observations = rows
        }, Json));
        output.WriteLine($"{path}: {rows.Count} observations, {blockers.Length} release-blocker observations");
        Assert.Equal(corpus.Queries.Length * 6, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => (r.Id, r.Mode, r.Phase)).Distinct().Count());
        return path;
    }

    private static double Percentile(IEnumerable<double> values, double fraction)
    {
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];
    }

    private static void Seed(EvaluationStore store, Memory[] memories, IReadOnlyDictionary<string, float[]> vectors, string tenant = "")
    {
        foreach (var m in memories)
            store.Index.Upsert(new CognitiveEntry(m.Id, vectors[m.Text], m.Ns, m.Text, lifecycleState: m.State, tenantId: tenant));
        store.Graph.AddEdge(new GraphEdge("juniper-backup", "juniper-restore", "related_to", tenantId: tenant));
        store.Graph.AddEdge(new GraphEdge("juniper-timeout", "juniper-timeout-old", "supersedes", tenantId: tenant));
    }

    private static async Task<object> CheckProtections(Memory[] memories, MeasuredEmbedding embedding, IReadOnlyDictionary<string, float[]> vectors)
    {
        const string tenant = "evaluation-a", foreignTenant = "evaluation-b", publicNs = "guard-public", privateNs = "guard-private";
        const string query = "What is Juniper production readiness endpoint?";
        using var store = new EvaluationStore(embedding);
        Seed(store, memories, vectors, tenant);
        foreach (string ns in memories.Select(m => m.Ns).Distinct().Append(publicNs).Append("guard-empty"))
        {
            store.Registry.EnsureOwnership(ns, "owner", tenant);
            Assert.Equal("shared", store.Registry.Share(ns, "owner", "reader", "read", tenant).Status);
        }
        store.Registry.EnsureOwnership(privateNs, "owner", tenant);
        string[] canaries = ["SCOPE_PRIVATE_CANARY", "SCOPE_FOREIGN_CANARY", "SCOPE_LEGACY_CANARY", "SCOPE_GRAPH_CANARY"];
        Add("guard-answer", publicNs, "Juniper production readiness endpoint is /guard/ready.", tenant);
        Add("guard-answer", privateNs, $"Juniper production readiness endpoint is /{canaries[0]}.", tenant);
        Add("guard-answer", publicNs, $"Juniper production readiness endpoint is /{canaries[1]}.", foreignTenant);
        Add("guard-answer", publicNs, $"Juniper production readiness endpoint is /{canaries[2]}.", "");
        Add("graph-public", publicNs, "Juniper production readiness endpoint is /guard/ready.", tenant);
        Add("graph-private", privateNs, $"Juniper production readiness endpoint is /{canaries[3]}.", tenant);
        // Same-ID twins intentionally make guard-answer an ambiguous graph anchor.
        // Use a unique public anchor so the graph check exercises an actual edge.
        store.Graph.AddEdge(new GraphEdge("graph-public", "graph-private", "elaborates", tenantId: tenant));
        Assert.Contains(store.Graph.GetAllEdges(tenant), e => e.SourceId == "graph-public" && e.TargetId == "graph-private");
        var checks = new List<string>();
        foreach (string phase in new[] { "warm", "restarted" })
        {
            if (phase == "restarted") store.RestartVerified();
            foreach (string? ns in new[] { publicNs, null })
            {
                var result = Assert.IsType<RecallResult>(await store.Tools("reader", tenant).RecallAsync(query, ns));
                Assert.NotEmpty(result.Results); // A non-vacuous read grant, despite same-ID decoys.
                if (ns is null) Assert.Equal("broadcast", result.Strategy);
                else
                {
                    Assert.Contains(result.Results, r => r.Id == "guard-answer" && r.Text == "Juniper production readiness endpoint is /guard/ready.");
                    Assert.Contains(result.Results, r => r.Id == "graph-public");
                }
                AssertSafe(result);
                Assert.Equal("archived", store.Index.Get("guard-answer", publicNs, tenant)!.LifecycleState);
                checks.Add($"{phase}/{ns ?? "broadcast"}/read-only-archive-and-complete-response");
            }
            foreach (string ns in new[] { privateNs, "guard-empty" })
            {
                var result = Assert.IsType<RecallResult>(await store.Tools("reader", tenant).RecallAsync(query, ns));
                Assert.Empty(result.Results);
                AssertSafe(result);
                checks.Add($"{phase}/{ns}/no-namespace-fallback");
            }
            var threshold = Assert.IsType<RecallResult>(await store.Tools("reader", tenant).RecallAsync(query, publicNs, minScore: 1f));
            Assert.Empty(threshold.Results);
            AssertSafe(threshold);
            checks.Add($"{phase}/minimum-score");
            foreach (var key in new[] { (privateNs, tenant, "guard-answer"), (publicNs, foreignTenant, "guard-answer"),
                         (publicNs, "", "guard-answer"), (privateNs, tenant, "graph-private") })
            {
                var inaccessible = store.Index.Get(key.Item3, key.Item1, key.Item2)!;
                Assert.Equal(1, inaccessible.AccessCount);
                Assert.Equal("archived", inaccessible.LifecycleState);
            }
        }

        // A bounded mixed workload, not a 48-hour soak. Separate dedicated lanes
        // avoid thread-pool starvation; teardown never disposes under live workers.
        var workers = new ConcurrentTestWorkers();
        string workNs = memories.GroupBy(m => m.Ns).OrderByDescending(g => g.Count()).First().Key;
        var lifecycle = new LifecycleEngine(store.Index, store.Persistence);
        var witness = memories[0];
        int writes = 0, reads = 0, passes = 0;
        var load = Stopwatch.StartNew();
        try
        {
            workers.Run(TimeSpan.FromSeconds(60),
                token =>
                {
                    for (int i = 0; i < 32; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        store.Index.Upsert(new CognitiveEntry($"load-witness-{i}", vectors[witness.Text], workNs,
                            witness.Text, lifecycleState: "ltm", tenantId: tenant));
                        writes++;
                    }
                },
                token =>
                {
                    var tools = store.Tools("reader", tenant);
                    for (int i = 0; i < 16; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var result = Assert.IsType<RecallResult>(tools.Recall(query, publicNs));
                        Assert.NotEmpty(result.Results);
                        AssertSafe(result);
                        reads++;
                    }
                },
                token =>
                {
                    for (int i = 0; i < 8; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        Assert.True(lifecycle.RunDecayCycle(workNs, tenant, decayRate: 0).ProcessedCount > 0);
                        passes++;
                    }
                });
        }
        finally { store.Retain = !workers.IsQuiescent; }
        load.Stop();
        Assert.Equal(32, writes);
        Assert.Equal(16, reads);
        Assert.Equal(8, passes);
        store.RestartVerified();
        for (int i = 0; i < writes; i++) Assert.Equal(witness.Text, store.Index.Get($"load-witness-{i}", workNs, tenant)!.Text);
        checks.Add("mixed-workload/all-writes-durable-after-restart");
        return new { Passed = true, Checks = checks, BaseDataMemories = memories.Length, BoundaryRecords = 6,
            ConcurrentWrites = writes, ConcurrentReads = reads, MaintenancePasses = passes, WorkloadMilliseconds = load.Elapsed.TotalMilliseconds };

        void Add(string id, string ns, string text, string scope) => store.Index.Upsert(new CognitiveEntry(id,
            embedding.Embed(text), ns, text, lifecycleState: "archived", tenantId: scope));
        void AssertSafe(RecallResult result)
        {
            string response = JsonSerializer.Serialize(result, Json);
            Assert.All(canaries, marker => Assert.DoesNotContain(marker, response));
            Assert.Null(result.Conflicts?.FirstOrDefault(c => c.Resolution is not null));
        }
    }

    internal sealed class MeasuredEmbedding : IEmbeddingService, IDisposable
    {
        private readonly OnnxEmbeddingService _inner = new();
        private long _calls, _ticks;
        public long Calls => Interlocked.Read(ref _calls);
        public double Milliseconds => Interlocked.Read(ref _ticks) * 1000.0 / Stopwatch.Frequency;
        public int Dimensions => _inner.Dimensions;
        public float[] Embed(string text)
        {
            long start = Stopwatch.GetTimestamp();
            var result = _inner.Embed(text);
            Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - start);
            Interlocked.Increment(ref _calls);
            return result;
        }
        public void Dispose() => _inner.Dispose();
    }

    private sealed class EvaluationStore : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"engram-noisy-eval-{Guid.NewGuid():N}");
        private readonly IEmbeddingService _embedding;
        internal PersistenceManager Persistence { get; private set; } = null!;
        internal CognitiveIndex Index { get; private set; } = null!;
        internal KnowledgeGraph Graph { get; private set; } = null!;
        internal ExpertDispatcher Dispatcher { get; private set; } = null!;
        internal NamespaceRegistry Registry { get; private set; } = null!;
        internal bool Retain { get; set; }
        internal EvaluationStore(IEmbeddingService embedding) { _embedding = embedding; Open(); }
        private void Open()
        {
            Persistence = new PersistenceManager(_path);
            Index = new CognitiveIndex(Persistence);
            Graph = new KnowledgeGraph(Persistence, Index);
            Dispatcher = new ExpertDispatcher(Index, _embedding);
            Registry = new NamespaceRegistry(Index, _embedding);
        }
        internal CompositeTools Tools(string agent = "default", string tenant = "") => new(Index, _embedding, Graph,
            new LifecycleEngine(Index, Persistence), Dispatcher, new MetricsCollector(),
            new SpectralRetrievalReranker(new MemoryDiffusionKernel(Index, Graph)),
            new NamespaceAccess(Registry, new PrincipalContext(tenant, agent)));
        internal void Flush() => Assert.True(Persistence.TryFlush(), "Evaluation persistence flush failed.");
        internal long PersistedBytes => Directory.EnumerateFiles(_path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        internal double RestartVerified()
        {
            var partitions = Index.GetAllAcrossTenants().Select(e => (e.Ns, e.TenantId)).Distinct().ToArray();
            string entries = Snapshot();
            string edges = Edges();
            var timer = Stopwatch.StartNew();
            Flush();
            Index.Dispose();
            Persistence.Dispose();
            Open();
            // Ordinary namespace discovery deliberately excludes system files.
            // Load the previously present partitions explicitly, including profiles
            // and permission rows, before comparing the durable contents.
            foreach (var (ns, tenant) in partitions) Index.GetAllInNamespace(ns, tenant);
            string reloaded = Snapshot();
            if (entries != reloaded)
            {
                Directory.CreateDirectory(OutputDirectory);
                string suffix = $"net{System.Environment.Version.Major}-{Path.GetFileName(_path)}.json";
                File.WriteAllText(Path.Combine(OutputDirectory, "restart-before-" + suffix), entries);
                File.WriteAllText(Path.Combine(OutputDirectory, "restart-after-" + suffix), reloaded);
            }
            Assert.Equal(Hash(entries), Hash(reloaded));
            Assert.Equal(edges, Edges());
            timer.Stop();
            return timer.Elapsed.TotalMilliseconds;
        }
        // Compare the stable fields used by this evaluation, including vectors,
        // metadata, lifecycle and access state; exclude process-local revisions.
        private string Snapshot() => JsonSerializer.Serialize(Index.GetAllAcrossTenants()
            .OrderBy(e => e.TenantId, StringComparer.Ordinal).ThenBy(e => e.Ns, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => new { e.Id, e.Ns, e.TenantId, e.Text, e.Vector, e.LifecycleState, e.AccessCount, e.ActivationEnergy,
                e.CreatedAt, e.LastAccessedAt, e.Category, e.IsSummaryNode, e.Keywords,
                Metadata = e.Metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray() }), Json);
        private string Edges() => Hash(JsonSerializer.Serialize(Graph.GetAllEdges()
            .OrderBy(e => e.TenantId, StringComparer.Ordinal).ThenBy(e => e.SourceId, StringComparer.Ordinal)
            .ThenBy(e => e.TargetId, StringComparer.Ordinal).ThenBy(e => e.Relation, StringComparer.Ordinal), Json));
        public void Dispose()
        {
            if (Retain) return;
            Index.Dispose();
            Persistence.Dispose();
            Directory.Delete(_path, recursive: true);
        }
    }
}
