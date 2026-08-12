using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Core.Services.Synthesis;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

/// <summary>
/// Adversarial coverage for structures that still use global bare entry IDs. A tenant-scoped
/// principal must fail closed even when same-id entries exist in both tenant and legacy
/// partitions; the explicit legacy-unisolated principal remains the compatibility control.
/// </summary>
public sealed class TenantStructureIsolationTests : IDisposable
{
    private const string Ns = "shared-structure";
    private const string TenantId = "tenant-a";
    private const string EntryA = "same-a";
    private const string EntryB = "same-b";

    private readonly string _dataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly LifecycleEngine _lifecycle;
    private readonly AccretionScanner _scanner;
    private readonly StubEmbedding _embedding = new();
    private readonly NamespaceRegistry _registry;

    public TenantStructureIsolationTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"tenant_structures_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_dataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
        _lifecycle = new LifecycleEngine(_index);
        _scanner = new AccretionScanner(_index);
        _registry = new NamespaceRegistry(_index, _embedding);
    }

    [Fact]
    public async Task TenantPrincipal_CannotReadOrMutateAnyLegacyBareIdStructure()
    {
        SeedCollidingPartitions();
        var tenantAccess = Access(new PrincipalContext(TenantId, "alice"));
        var legacyAccess = Access(PrincipalContext.LegacyUnisolated);

        var autoLink = new AutoLinkScanner(_index, _graph, new DuplicateDetector());
        var tenantGraph = new GraphTools(_graph, autoLink, _index, tenantAccess);
        var legacyGraph = new GraphTools(_graph, autoLink, _index, legacyAccess);
        var edgeCount = _graph.EdgeCount;

        Assert.Empty(tenantGraph.GetNeighbors(EntryA).Neighbors);
        Assert.Equal(NamespaceAccess.TenantStructureUnavailable,
            tenantGraph.LinkMemories(EntryB, EntryA, "depends_on"));
        Assert.Equal(edgeCount, _graph.EdgeCount);
        var legacyNeighbors = legacyGraph.GetNeighbors(EntryA);
        Assert.Single(legacyNeighbors.Neighbors);
        Assert.Equal("legacy beta", legacyNeighbors.Neighbors[0].Entry.Text);

        var tenantClusters = new ClusterTools(_clusters, _embedding, tenantAccess);
        var legacyClusters = new ClusterTools(_clusters, _embedding, legacyAccess);
        Assert.Equal("Cluster 'global-cluster' not found.", tenantClusters.GetCluster("global-cluster"));
        Assert.Equal(NamespaceAccess.TenantStructureUnavailable,
            tenantClusters.UpdateCluster("global-cluster", label: "tenant rewrite"));
        Assert.Equal("legacy cluster", _clusters.GetCluster("global-cluster")?.Label);
        var legacyCluster = Assert.IsType<GetClusterResult>(legacyClusters.GetCluster("global-cluster"));
        Assert.Equal(2, legacyCluster.MemberCount);
        Assert.All(legacyCluster.Members, member => Assert.StartsWith("legacy", member.Text));

        var tenantLifecycle = new LifecycleTools(_lifecycle, _embedding, _index, tenantAccess);
        var legacyLifecycle = new LifecycleTools(_lifecycle, _embedding, _index, legacyAccess);
        Assert.Equal(NamespaceAccess.TenantStructureUnavailable,
            tenantLifecycle.PromoteMemory(EntryA, "ltm"));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<CognitiveSearchResult>>(
            tenantLifecycle.DeepRecall(Ns, vector: [1f, 0f], minScore: 0f)));
        Assert.Equal("stm", _index.Get(EntryA, Ns)?.LifecycleState);
        Assert.Equal("stm", _index.Get(EntryA, Ns, TenantId)?.LifecycleState);
        Assert.Contains("stm -> ltm", legacyLifecycle.PromoteMemory(EntryA, "ltm"));
        Assert.Equal("ltm", _index.Get(EntryA, Ns)?.LifecycleState);
        Assert.Equal("stm", _index.Get(EntryA, Ns, TenantId)?.LifecycleState);

        var tenantIntelligence = Intelligence(tenantAccess);
        var legacyIntelligence = Intelligence(legacyAccess);
        var tenantDuplicates = Assert.IsType<DuplicateDetectionResult>(
            tenantIntelligence.DetectDuplicates(Ns, threshold: 0.9f));
        Assert.Equal(0, tenantDuplicates.ScannedCount);
        Assert.Empty(tenantDuplicates.Duplicates);
        Assert.Equal(NamespaceAccess.TenantStructureUnavailable,
            tenantIntelligence.MergeMemories(EntryA, EntryB, Ns));
        Assert.Equal("stm", _index.Get(EntryB, Ns)?.LifecycleState);
        Assert.Equal(edgeCount, _graph.EdgeCount);
        var legacyDuplicates = Assert.IsType<DuplicateDetectionResult>(
            legacyIntelligence.DetectDuplicates(Ns, threshold: 0.9f));
        Assert.Equal(2, legacyDuplicates.ScannedCount);
        Assert.NotEmpty(legacyDuplicates.Duplicates);

        var kernel = new MemoryDiffusionKernel(_index, _graph);
        var tenantDiffusion = new MemoryDiffusionTools(kernel, tenantAccess);
        var legacyDiffusion = new MemoryDiffusionTools(kernel, legacyAccess);
        Assert.Null(tenantDiffusion.ComputeDiffusionBasis(Ns));
        Assert.Null(tenantDiffusion.DiffusionStats(Ns));
        Assert.Equal(NamespaceAccess.TenantStructureUnavailable,
            tenantDiffusion.InvalidateDiffusion(Ns));
        Assert.Contains("Invalidated", legacyDiffusion.InvalidateDiffusion(Ns));

        var reranker = new SpectralRetrievalReranker(kernel);
        var tenantSpectral = new SpectralRetrievalTools(_index, _embedding, reranker, tenantAccess);
        var legacySpectral = new SpectralRetrievalTools(_index, _embedding, reranker, legacyAccess);
        Assert.Empty(tenantSpectral.SpectralRecall("alpha", Ns, mode: "none", minScore: 0f));
        var legacySpectralResults = legacySpectral.SpectralRecall(
            "alpha", Ns, mode: "none", minScore: 0f);
        Assert.Equal(2, legacySpectralResults.Count);
        Assert.All(legacySpectralResults, result => Assert.StartsWith("legacy", result.Text));

        var originalLegacyVector = _index.Get(EntryA, Ns)!.Vector.ToArray();
        var originalTenantVector = _index.Get(EntryA, Ns, TenantId)!.Vector.ToArray();
        var tenantMaintenance = new MaintenanceTools(
            _index, new ReembeddingService(), new MetricsCollector(), tenantAccess);
        var legacyMaintenance = new MaintenanceTools(
            _index, new ReembeddingService(), new MetricsCollector(), legacyAccess);
        Assert.Equal(NamespaceAccess.TenantStructureUnavailable,
            tenantMaintenance.RebuildEmbeddings(Ns));
        Assert.Equal(originalLegacyVector, _index.Get(EntryA, Ns)!.Vector);
        Assert.Equal(originalTenantVector, _index.Get(EntryA, Ns, TenantId)!.Vector);
        var tenantCompression = Assert.IsType<CompressionStatsResult>(
            tenantMaintenance.CompressionStats(Ns));
        Assert.Equal(0, tenantCompression.TotalEntries);
        var legacyCompression = Assert.IsType<CompressionStatsResult>(
            legacyMaintenance.CompressionStats(Ns));
        Assert.Equal(2, legacyCompression.TotalEntries);
        var rebuilt = Assert.IsType<RebuildEmbeddingsResult>(legacyMaintenance.RebuildEmbeddings(Ns));
        Assert.Equal(2, rebuilt.TotalUpdated);
        Assert.Equal(3, _index.Get(EntryA, Ns)!.Vector.Length);
        Assert.Equal(2, _index.Get(EntryA, Ns, TenantId)!.Vector.Length);

        var generator = new RecordingTextGenerator();
        var synthesis = new SynthesisEngine(_index, _clusters, generator);
        var tenantSynthesis = new SynthesisTools(synthesis, tenantAccess);
        var legacySynthesis = new SynthesisTools(synthesis, legacyAccess);
        var tenantSynthesisResult = Assert.IsType<SynthesisResult>(
            await tenantSynthesis.SynthesizeMemories(Ns));
        Assert.Equal("empty", tenantSynthesisResult.Status);
        Assert.Equal(0, generator.AvailabilityCalls);
        var legacySynthesisResult = Assert.IsType<SynthesisResult>(
            await legacySynthesis.SynthesizeMemories(Ns));
        Assert.Equal("synthesized", legacySynthesisResult.Status);
        Assert.True(generator.AvailabilityCalls > 0);

        var tenantVisualization = new VisualizationTools(_index, _graph, _clusters, tenantAccess);
        var legacyVisualization = new VisualizationTools(_index, _graph, _clusters, legacyAccess);
        var tenantSnapshot = tenantVisualization.GetGraphSnapshot(Ns, includeArchived: true);
        Assert.Empty(tenantSnapshot.Nodes);
        Assert.Empty(tenantSnapshot.Edges);
        Assert.Empty(tenantSnapshot.Clusters);
        var legacySnapshot = legacyVisualization.GetGraphSnapshot(Ns, includeArchived: true);
        Assert.Equal(2, legacySnapshot.Nodes.Count);
        Assert.Single(legacySnapshot.Edges);
        Assert.Single(legacySnapshot.Clusters);
        Assert.All(legacySnapshot.Nodes, node => Assert.StartsWith("legacy", node.Text));
    }

    [Fact]
    public async Task TenantAdminPurge_DeletesTenantEntriesWithoutTouchingGlobalGraphOrClusters()
    {
        const string debateNs = "active-debate-stale";
        const string debateA = "debate-a";
        const string debateB = "debate-b";
        var stale = DateTimeOffset.UtcNow.AddDays(-3);

        var legacyA = Entry(debateA, debateNs, "legacy debate a");
        var legacyB = Entry(debateB, debateNs, "legacy debate b");
        var tenantA = Entry(debateA, debateNs, "tenant debate a", TenantId);
        legacyA.CreatedAt = stale;
        legacyB.CreatedAt = stale;
        tenantA.CreatedAt = stale;
        _index.Upsert(legacyA);
        _index.Upsert(legacyB);
        _index.Upsert(tenantA);
        _registry.EnsureOwnership(debateNs, "alice", TenantId);
        _graph.AddEdge(new GraphEdge(debateA, debateB, "supports"));
        _clusters.CreateCluster("debate-cluster", debateNs, [debateA, debateB]);

        var tenantAdmin = new AdminTools(
            _index, _graph, _clusters, _persistence, _registry,
            new PrincipalContext(TenantId, "alice"));
        var tenantResult = Assert.IsType<PurgeDebatesResult>(
            await tenantAdmin.PurgeDebates(maxAgeHours: 24, dryRun: false));

        Assert.Equal(1, tenantResult.NamespacesAffected);
        Assert.Equal(1, tenantResult.TotalEntriesRemoved);
        Assert.Equal(0, tenantResult.TotalEdgesRemoved);
        Assert.Null(_index.Get(debateA, debateNs, TenantId));
        Assert.NotNull(_index.Get(debateA, debateNs));
        Assert.Equal(1, _graph.EdgeCount);
        Assert.Contains("debate-cluster", _clusters.GetClustersForEntry(debateA));

        var legacyAdmin = new AdminTools(
            _index, _graph, _clusters, _persistence, _registry,
            PrincipalContext.LegacyUnisolated);
        var legacyResult = Assert.IsType<PurgeDebatesResult>(
            await legacyAdmin.PurgeDebates(maxAgeHours: 24, dryRun: false));

        Assert.Equal(1, legacyResult.NamespacesAffected);
        Assert.Equal(2, legacyResult.TotalEntriesRemoved);
        Assert.Equal(1, legacyResult.TotalEdgesRemoved);
        Assert.Null(_index.Get(debateA, debateNs));
        Assert.Null(_index.Get(debateB, debateNs));
        Assert.Equal(0, _graph.EdgeCount);
        Assert.Empty(_clusters.GetClustersForEntry(debateA));
    }

    private NamespaceAccess Access(IPrincipalContext principal)
        => new(_registry, principal);

    private IntelligenceTools Intelligence(NamespaceAccess access)
        => new(_index, _graph, _embedding, _scanner, _clusters, _lifecycle, access);

    private void SeedCollidingPartitions()
    {
        _index.Upsert(Entry(EntryA, Ns, "legacy alpha"));
        _index.Upsert(Entry(EntryB, Ns, "legacy beta"));
        _index.Upsert(Entry(EntryA, Ns, "tenant alpha", TenantId));
        _index.Upsert(Entry(EntryB, Ns, "tenant beta", TenantId));
        _graph.AddEdge(new GraphEdge(EntryA, EntryB, "similar_to"));
        _clusters.CreateCluster("global-cluster", Ns, [EntryA, EntryB], "legacy cluster");
    }

    private static CognitiveEntry Entry(string id, string ns, string text, string tenantId = "")
        => new(id, [1f, 0f], ns, text, tenantId: tenantId);

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [1f, 0f];
    }

    private sealed class ReembeddingService : IEmbeddingService
    {
        public int Dimensions => 3;
        public float[] Embed(string text) => [0f, 1f, 0f];
    }

    private sealed class RecordingTextGenerator : ITextGenerator
    {
        public int AvailabilityCalls { get; private set; }

        public Task<bool> IsAvailableAsync(string model, CancellationToken ct = default)
        {
            AvailabilityCalls++;
            return Task.FromResult(true);
        }

        public Task<string?> GenerateAsync(
            string model,
            string prompt,
            int maxTokens = 512,
            float temperature = 0.1f,
            CancellationToken ct = default)
            => Task.FromResult<string?>("generated synthesis");

        public void Dispose() { }
    }
}
