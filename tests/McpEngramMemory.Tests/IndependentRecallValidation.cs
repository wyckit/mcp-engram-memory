using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
using Microsoft.Data.Sqlite;

namespace McpEngramMemory.Tests;

internal static class IndependentRecallValidation
{
    internal const string FrozenHash = "11D86B86DF1629FC9931DE7DC846E4A7B317A9C31F15FB6363D73323CC4F6553";
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    internal static readonly string[] Modes = ["direct", "automatic", "broadcast"];
    internal sealed record Question(string Id, string Query, string Ns, string Project, string Environment,
        string[] Expected, Dictionary<string, string[]> Facets, bool TopOne);
    internal sealed record Corpus(int Version, NoisyRecallEvaluation.Memory[] Entries,
        NoisyRecallEvaluation.Profile[] Profiles, Question[] Queries);
    internal sealed record Observation(string Id, string Mode, string Phase, string Strategy, string[] ReturnedIds,
        string[] Expected, bool AnswerHit, bool TopOneCorrect, bool CorrectAbstention, double? FacetCoverage,
        string[] WrongScopeIds, bool UnsupportedResolution, double Milliseconds, string[] Failures);

    internal static Corpus Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "recall-independent-v1.json");
        Assert.Equal(FrozenHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        var c = JsonSerializer.Deserialize<Corpus>(File.ReadAllText(path), Json)!;
        Assert.Equal(1, c.Version);
        Assert.Equal(22, c.Entries.Length);
        Assert.Equal(24, c.Queries.Length);
        Assert.Equal(16, c.Queries.Count(q => q.Expected.Length > 0));
        Assert.Equal(c.Entries.Length, c.Entries.Select(e => e.Id).Distinct().Count());
        Assert.Equal(c.Queries.Length, c.Queries.Select(q => q.Id).Distinct().Count());
        foreach (var q in c.Queries)
        {
            Assert.All(q.Expected, id => Assert.Contains(c.Entries, e => e.Id == id &&
                e.Project == q.Project && (q.Environment.Length == 0 || q.Environment == e.Environment)));
            Assert.All(q.Facets.Values, ids => { Assert.NotEmpty(ids); Assert.All(ids, id => Assert.Contains(id, q.Expected)); });
        }
        return c;
    }

    internal static Observation Measure(Corpus corpus, Question q, string mode, string phase, RecallResult result, double ms)
    {
        var memories = corpus.Entries.ToDictionary(e => e.Id);
        var ids = result.Results.Select(r => r.Id).ToArray();
        Assert.InRange(ids.Length, 0, 5);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(result.Results, r =>
        {
            Assert.True(memories.ContainsKey(r.Id), $"Unknown result {r.Id}");
            Assert.Equal(memories[r.Id].Text, r.Text);
            if (mode == "direct") Assert.Equal(q.Ns, memories[r.Id].Ns);
        });
        string[] expected = q.Expected.Where(id => mode != "direct" || memories[id].Ns == q.Ns).ToArray();
        var facets = q.Facets.Values.Select(f => f.Where(id => mode != "direct" || memories[id].Ns == q.Ns).ToArray())
            .Where(f => f.Length > 0).ToArray();
        string[] wrong = ids.Where(id => memories[id].Project != q.Project ||
            (q.Environment.Length > 0 && memories[id].Environment != q.Environment)).ToArray();
        bool hit = expected.Any(ids.Contains), top = ids.Length > 0 && expected.Contains(ids[0]);
        bool abstain = expected.Length == 0 && ids.Length == 0;
        double? coverage = facets.Length == 0 ? null : facets.Count(f => f.Any(ids.Contains)) / (double)facets.Length;
        bool unsupported = result.Conflicts?.Any(c => c.Resolution is not null || c.Status == "resolved") == true;
        var failures = new List<string>();
        if (expected.Length > 0 && !hit) failures.Add("answer missed");
        if (q.TopOne && !top) failures.Add("incorrect top one");
        if (expected.Length == 0 && !abstain) failures.Add("non-answer evidence");
        if (coverage < 1) failures.Add("incomplete facets");
        if (wrong.Length > 0) failures.Add("project/environment mismatch");
        if (unsupported) failures.Add("unsupported conflict resolution");
        return new(q.Id, mode, phase, result.Strategy, ids, expected, hit, top, abstain, coverage, wrong, unsupported, ms, failures.ToArray());
    }

    internal static object Metadata() => new
    {
        FixtureSha256 = FrozenHash, FreezeRevision = "991873a",
        SourceRevision = Environment.GetEnvironmentVariable("ENGRAM_EVAL_REVISION") ?? "working-tree; see run log",
        Framework = AppContext.TargetFrameworkName, OS = RuntimeInformation.OSDescription,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Processors = Environment.ProcessorCount,
        Model = "bundled bge-micro-v2 ONNX",
        ModelSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "LocalEmbeddingsModel", "default", "model.onnx"))))
    };

    internal static string Write(string stem, object report)
    {
        string directory = Environment.GetEnvironmentVariable("ENGRAM_INDEPENDENT_OUTPUT") ??
            Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{stem}-net{Environment.Version.Major}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, Json));
        return path;
    }

    internal static object Distribution(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        Assert.NotEmpty(values);
        Assert.All(values, v => Assert.True(double.IsFinite(v) && v >= 0));
        double P(double p) => values[(int)Math.Ceiling(values.Length * p) - 1];
        return new { Count = values.Length, P50 = P(.5), P95 = P(.95), P99 = P(.99), Max = values[^1] };
    }

    internal sealed class Store : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"independent-recall-{Guid.NewGuid():N}");
        private readonly string _backend;
        private readonly IEmbeddingService _embedding;
        internal IStorageProvider Storage { get; private set; } = null!;
        internal CognitiveIndex Index { get; private set; } = null!;
        internal KnowledgeGraph Graph { get; private set; } = null!;
        internal NamespaceRegistry Registry { get; private set; } = null!;
        internal ExpertDispatcher Dispatcher { get; private set; } = null!;
        internal bool Retain { get; set; }
        internal Store(string backend, IEmbeddingService embedding)
        {
            _backend = backend; _embedding = embedding; Directory.CreateDirectory(_root); Open();
        }
        private void Open()
        {
            Storage = _backend == "json" ? new PersistenceManager(_root) :
                new SqliteStorageProvider(Path.Combine(_root, "memory.db"));
            Index = new CognitiveIndex(Storage);
            Graph = new KnowledgeGraph(Storage, Index);
            Registry = new NamespaceRegistry(Index, _embedding);
            Dispatcher = new ExpertDispatcher(Index, _embedding);
        }
        internal CompositeTools Tools(string agent = "default", string tenant = "", ExpertDispatcher? dispatcher = null) =>
            new(Index, _embedding, Graph, new LifecycleEngine(Index, Storage), dispatcher ?? Dispatcher,
                new MetricsCollector(), new SpectralRetrievalReranker(new MemoryDiffusionKernel(Index, Graph)),
                new NamespaceAccess(Registry, new PrincipalContext(tenant, agent)));
        internal void Add(string id, string ns, string text, float[] vector, string tenant = "", string state = "ltm",
            Dictionary<string, string>? metadata = null) =>
            Index.Upsert(new CognitiveEntry(id, vector, ns, text, metadata: metadata, lifecycleState: state, tenantId: tenant));
        internal void Profile(NoisyRecallEvaluation.Profile p, float[] vector) =>
            Index.Upsert(new CognitiveEntry(p.Id, vector, ExpertDispatcher.SystemNamespace, p.Description, "expert-profile",
                new() { ["targetNamespace"] = "expert_" + p.Id }, lifecycleState: "ltm") { IsSummaryNode = true });
        internal void Flush() => Assert.True(Storage.TryFlush(), "All acknowledged writes must flush.");
        internal double Restart()
        {
            var partitions = Index.GetAllAcrossTenants().Select(e => (e.Ns, e.TenantId)).Distinct().ToArray();
            string before = Snapshot();
            string edges = JsonSerializer.Serialize(Graph.GetAllEdges().OrderBy(e => e.SourceId).ThenBy(e => e.TargetId), Json);
            var timer = Stopwatch.StartNew();
            Flush(); Index.Dispose(); Storage.Dispose(); Open();
            foreach (var (ns, tenant) in partitions) Index.GetAllInNamespace(ns, tenant);
            Assert.Equal(before, Snapshot());
            Assert.Equal(edges, JsonSerializer.Serialize(Graph.GetAllEdges().OrderBy(e => e.SourceId).ThenBy(e => e.TargetId), Json));
            return timer.Elapsed.TotalMilliseconds;
        }
        private string Snapshot() => JsonSerializer.Serialize(Index.GetAllAcrossTenants()
            .OrderBy(e => e.TenantId).ThenBy(e => e.Ns).ThenBy(e => e.Id)
            .Select(e => new { e.Id, e.Ns, e.TenantId, e.Text, e.Vector, e.LifecycleState, e.AccessCount,
                e.ActivationEnergy, e.CreatedAt, e.LastAccessedAt, e.IsSummaryNode,
                Metadata = e.Metadata.OrderBy(k => k.Key).ToArray() }), Json);
        public void Dispose()
        {
            if (Retain) return;
            Index.Dispose(); Storage.Dispose();
            if (_backend == "sqlite")
            {
                // Release only this fixture's pool; other concurrent tests own other databases.
                using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "memory.db")}");
                SqliteConnection.ClearPool(connection);
            }
            Directory.Delete(_root, true);
        }
    }
}
