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

public class DefaultRecallRankingTests : IDisposable
{
    private const string Namespace = "ranking";
    private const string Query = "installer profile counts discrepancy 17 39 63 regression tests";
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"recall-ranking-{Guid.NewGuid():N}");
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly MemoryDiffusionKernel _kernel;
    private readonly CompositeTools _tools;

    public DefaultRecallRankingTests()
    {
        _persistence = new PersistenceManager(_dataPath);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _kernel = new MemoryDiffusionKernel(_index, _graph);
        var embedding = new QueryEmbedding();
        _tools = new CompositeTools(_index, embedding, _graph, new LifecycleEngine(_index, _persistence),
            new ExpertDispatcher(_index, embedding), new MetricsCollector(), new SpectralRetrievalReranker(_kernel),
            new NamespaceAccess(new NamespaceRegistry(_index, embedding), AgentIdentity.Default));
    }

    [Theory]
    [InlineData(32, true, false)]
    [InlineData(32, false, false)]
    [InlineData(128, true, false)]
    [InlineData(32, true, true)]
    public void DefaultRecallPreservesBestEvidenceWithMixedTopology(int linkedCount, bool expandGraph, bool archived)
    {
        SeedCorpus(linkedCount, archived);
        Assert.NotNull(_kernel.GetBasis(Namespace, tenantId: ""));

        // Establish relevance through the actual hybrid/token-rerank/fallback pipeline.
        var baseline = Assert.IsType<RecallResult>(_tools.Recall(Query, Namespace,
            expandGraph: expandGraph, spectralMode: "none"));
        Assert.Equal("answer", baseline.Results[0].Id);
        if (archived)
        {
            Assert.Equal("deep_recall", baseline.Strategy);
            // Deep recall can resurrect entries; reset so every mode sees the same states.
            foreach (var entry in _index.GetAllInNamespace(Namespace, tenantId: ""))
                entry.LifecycleState = "archived";
        }

        // No spectralMode argument: exercise the public default, not just the reranker.
        var actual = Assert.IsType<RecallResult>(_tools.Recall(Query, Namespace, expandGraph: expandGraph));

        Assert.Equal(baseline.Strategy, actual.Strategy);
        Assert.Equal("answer", actual.Results[0].Id);
        Assert.Equal(5, actual.Results.Count);
        Assert.Equal(5, actual.Results.Select(r => r.Id).Distinct().Count());
        Assert.True(actual.Results[0].Score >= baseline.Results[0].Score);
        Assert.All(actual.Results, r => Assert.NotNull(_index.Get(r.Id, Namespace, tenantId: "")));
    }

    private void SeedCorpus(int linkedCount, bool archived)
    {
        string state = archived ? "archived" : "ltm";
        // Controlled embeddings make the relevance gaps deterministic; retrieval, token
        // reranking, graph expansion, fallback, and spectral ranking remain production code.
        Add("answer", 0.99f, Query + ": fixed and independently verified.", state);
        Add("related", 0.92f, "installer profile counts discrepancy regression tests", state);
        for (int i = 2; i < linkedCount; i++) Add($"linked-{i}", 0.05f, "unrelated graph detail", state);
        for (int i = 0; i < 3; i++) Add($"isolated-{i}", 0.87f - i * 0.01f,
            "installer profile counts discrepancy legacy summary", state);

        var ids = new[] { "answer", "related" }.Concat(Enumerable.Range(2, linkedCount - 2).Select(i => $"linked-{i}")).ToArray();
        for (int i = 0; i < ids.Length; i++)
            for (int j = i + 1; j < ids.Length; j++)
                _graph.AddEdge(new GraphEdge(ids[i], ids[j], "similar_to", 1f));
    }

    private void Add(string id, float cosine, string text, string state) =>
        _index.Upsert(new CognitiveEntry(id, new[] { cosine, MathF.Sqrt(1f - cosine * cosine) },
            Namespace, text, lifecycleState: state));

    private sealed class QueryEmbedding : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => new[] { 1f, 0f };
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, true);
    }
}
