using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Governance;
using McpEngramMemory.Core.Services.Governance.Persistence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

internal sealed class RecallTestStore : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"recall-eval-{Guid.NewGuid():N}");
    private readonly IEmbeddingService _embedding;
    private PersistenceManager _persistence = null!;
    public CognitiveIndex Index { get; private set; } = null!;
    public KnowledgeGraph Graph { get; private set; } = null!;
    public ExpertDispatcher Dispatcher { get; private set; } = null!;
    public NamespaceRegistry Registry { get; private set; } = null!;
    public IGovernedKnowledgeStore Knowledge { get; private set; } = null!;

    public RecallTestStore(IEmbeddingService? embedding = null)
    {
        _embedding = embedding ?? new FixedEmbedding();
        Open();
    }

    private void Open()
    {
        _persistence = new PersistenceManager(_path);
        Index = new CognitiveIndex(_persistence);
        Graph = new KnowledgeGraph(_persistence, Index);
        Dispatcher = new ExpertDispatcher(Index, _embedding);
        Registry = new NamespaceRegistry(Index, _embedding);
        Knowledge = new FileGovernedKnowledgeStore(Path.Combine(_path, "governance"));
    }

    public CompositeTools Tools(string agent = "default", string tenant = "", IGovernedKnowledgeStore? knowledge = null) => new(
        Index, _embedding, Graph, new LifecycleEngine(Index, _persistence), Dispatcher,
        new MetricsCollector(), new SpectralRetrievalReranker(new MemoryDiffusionKernel(Index, Graph)),
        new NamespaceAccess(Registry, new PrincipalContext(tenant, agent)), knowledge ?? Knowledge);

    public CognitiveEntry Add(string id, string ns, string text, float cosine = 1f,
        string state = "ltm", bool summary = false, string tenant = "")
    {
        var entry = new CognitiveEntry(id, new[] { cosine, MathF.Sqrt(1f - cosine * cosine) },
            ns, text, lifecycleState: state, tenantId: tenant) { IsSummaryNode = summary };
        Index.Upsert(entry);
        return entry;
    }

    public void Restart()
    {
        Assert.True(_persistence.TryFlush(), "Evaluation restart requires a successful persistence flush.");
        Index.Dispose();
        _persistence.Dispose();
        Open();
    }

    public void Dispose()
    {
        Index.Dispose();
        _persistence.Dispose();
        Directory.Delete(_path, true);
    }

    private sealed class FixedEmbedding : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [1f, 0f];
    }
}
