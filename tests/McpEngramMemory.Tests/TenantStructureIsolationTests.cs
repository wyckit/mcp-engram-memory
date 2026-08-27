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
/// Cross-tenant isolation for the graph, cluster, lifecycle, intelligence, diffusion, maintenance,
/// synthesis, and visualization surfaces. The fixture seeds COLLIDING partitions — the same (ns, id)
/// pairs exist in both the legacy tenant and tenant-a, with different content, edges, and clusters —
/// and every test asserts that a tenant principal operates on, and sees, only its own partition,
/// while the legacy principal is entirely unaffected. This is the behavior that replaced the old
/// fail-closed containment.
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
        SeedCollidingPartitions();
    }

    private NamespaceAccess Tenant() => Access(new PrincipalContext(TenantId, "alice"));
    private NamespaceAccess Legacy() => Access(PrincipalContext.LegacyUnisolated);

    [Fact]
    public void Graph_NeighborsAndLinks_AreIsolatedPerTenant()
    {
        var autoLink = new AutoLinkScanner(_index, _graph, new DuplicateDetector());
        var tenantGraph = new GraphTools(_graph, autoLink, _index, Tenant());
        var legacyGraph = new GraphTools(_graph, autoLink, _index, Legacy());

        // Each tenant resolves EntryA's neighbor through its own edge, to its own entry text.
        var tn = Assert.Single(tenantGraph.GetNeighbors(EntryA).Neighbors);
        Assert.Equal("tenant beta", tn.Entry.Text);
        Assert.Equal("depends_on", tn.Edge.Relation);

        var ln = Assert.Single(legacyGraph.GetNeighbors(EntryA).Neighbors);
        Assert.Equal("legacy beta", ln.Entry.Text);
        Assert.Equal("similar_to", ln.Edge.Relation);

        // A tenant link adds an edge to the tenant partition only; the legacy graph is untouched.
        int legacyEdgesBefore = _graph.GetAllEdges("").Count;
        Assert.StartsWith("Linked", tenantGraph.LinkMemories(EntryB, EntryA, "elaborates"));
        Assert.Equal(legacyEdgesBefore, _graph.GetAllEdges("").Count);
        Assert.Contains(_graph.GetAllEdges(TenantId), e => e.Relation == "elaborates");
    }

    [Fact]
    public void Clusters_AreIsolatedPerTenant()
    {
        var tenantClusters = new ClusterTools(_clusters, _embedding, Tenant());
        var legacyClusters = new ClusterTools(_clusters, _embedding, Legacy());

        // Each tenant sees only its own cluster in the shared namespace.
        var tenantList = Assert.Single(tenantClusters.ListClusters(Ns));
        Assert.Equal("tenant-cluster", tenantList.ClusterId);
        var legacyList = Assert.Single(legacyClusters.ListClusters(Ns));
        Assert.Equal("global-cluster", legacyList.ClusterId);

        // The other tenant's cluster id is not found — same shape as a genuine miss.
        Assert.Equal("Cluster 'global-cluster' not found.", tenantClusters.GetCluster("global-cluster"));

        var tenantCluster = Assert.IsType<GetClusterResult>(tenantClusters.GetCluster("tenant-cluster"));
        Assert.All(tenantCluster.Members, m => Assert.StartsWith("tenant", m.Text));
        var legacyCluster = Assert.IsType<GetClusterResult>(legacyClusters.GetCluster("global-cluster"));
        Assert.All(legacyCluster.Members, m => Assert.StartsWith("legacy", m.Text));
    }

    [Fact]
    public void Lifecycle_PromoteMemory_IsolatesPerTenant()
    {
        var tenantLifecycle = new LifecycleTools(_lifecycle, _embedding, _index, Tenant());

        // Promoting the tenant's EntryA moves only the tenant copy; the legacy copy stays STM.
        Assert.Contains("stm -> ltm", tenantLifecycle.PromoteMemory(EntryA, "ltm"));
        Assert.Equal("ltm", _index.Get(EntryA, Ns, TenantId)?.LifecycleState);
        Assert.Equal("stm", _index.Get(EntryA, Ns)?.LifecycleState);
    }

    [Fact]
    public void Intelligence_DetectAndMerge_IsolatePerTenant()
    {
        var tenantIntel = Intelligence(Tenant());

        // Duplicate detection scans only the tenant's two entries.
        var dupes = Assert.IsType<DuplicateDetectionResult>(tenantIntel.DetectDuplicates(Ns, threshold: 0.9f));
        Assert.Equal(2, dupes.ScannedCount);

        // Merge archives the tenant's EntryB and transfers its tenant edge; legacy EntryB is untouched.
        Assert.StartsWith("Merged", tenantIntel.MergeMemories(EntryA, EntryB, Ns));
        Assert.Equal("archived", _index.Get(EntryB, Ns, TenantId)?.LifecycleState);
        Assert.Equal("stm", _index.Get(EntryB, Ns)?.LifecycleState);
        // The legacy A->B edge still exists after a tenant-side merge.
        Assert.Contains(_graph.GetAllEdges(""), e => e.SourceId == EntryA && e.TargetId == EntryB);
    }

    [Fact]
    public void Diffusion_GuardLifted_TenantInvalidateSucceeds()
    {
        var kernel = new MemoryDiffusionKernel(_index, _graph);
        var tenantDiffusion = new MemoryDiffusionTools(kernel, Tenant());
        // The operation no longer fails closed for a tenant; it runs and reports success.
        Assert.Contains("Invalidated", tenantDiffusion.InvalidateDiffusion(Ns));
    }

    [Fact]
    public void Maintenance_RebuildEmbeddings_IsolatesPerTenant()
    {
        var legacyVectorBefore = _index.Get(EntryA, Ns)!.Vector.ToArray();
        var tenantMaintenance = new MaintenanceTools(
            _index, new ReembeddingService(), new MetricsCollector(), Tenant());

        var rebuilt = Assert.IsType<RebuildEmbeddingsResult>(tenantMaintenance.RebuildEmbeddings(Ns));
        Assert.Equal(2, rebuilt.TotalUpdated);

        // Tenant vectors were re-embedded (dim 3); legacy vectors are byte-for-byte unchanged.
        Assert.Equal(3, _index.Get(EntryA, Ns, TenantId)!.Vector.Length);
        Assert.Equal(legacyVectorBefore, _index.Get(EntryA, Ns)!.Vector);
    }

    [Fact]
    public async Task Synthesis_RunsOverTenantPartition()
    {
        var generator = new RecordingTextGenerator();
        var synthesis = new SynthesisEngine(_index, _clusters, generator);
        var tenantSynthesis = new SynthesisTools(synthesis, Tenant());

        var result = Assert.IsType<SynthesisResult>(await tenantSynthesis.SynthesizeMemories(Ns));
        Assert.Equal("synthesized", result.Status);
        Assert.True(generator.AvailabilityCalls > 0);
    }

    [Fact]
    public void Visualization_Snapshot_IsolatesPerTenant()
    {
        var tenantViz = new VisualizationTools(_index, _graph, _clusters, Tenant());
        var legacyViz = new VisualizationTools(_index, _graph, _clusters, Legacy());

        var tenantSnapshot = tenantViz.GetGraphSnapshot(Ns, includeArchived: true);
        Assert.Equal(2, tenantSnapshot.Nodes.Count);
        Assert.All(tenantSnapshot.Nodes, n => Assert.StartsWith("tenant", n.Text));
        Assert.Single(tenantSnapshot.Edges);
        Assert.Equal("depends_on", tenantSnapshot.Edges[0].Relation);
        Assert.Single(tenantSnapshot.Clusters);
        Assert.Equal("tenant-cluster", tenantSnapshot.Clusters[0].ClusterId);

        var legacySnapshot = legacyViz.GetGraphSnapshot(Ns, includeArchived: true);
        Assert.Equal(2, legacySnapshot.Nodes.Count);
        Assert.All(legacySnapshot.Nodes, n => Assert.StartsWith("legacy", n.Text));
        Assert.Single(legacySnapshot.Edges);
        Assert.Equal("similar_to", legacySnapshot.Edges[0].Relation);
        Assert.Single(legacySnapshot.Clusters);
        Assert.Equal("global-cluster", legacySnapshot.Clusters[0].ClusterId);
    }

    [Fact]
    public async Task TenantAdminPurge_DeletesTenantEntriesWithoutTouchingLegacyGraphOrClusters()
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
        Assert.Null(_index.Get(debateA, debateNs, TenantId));
        Assert.NotNull(_index.Get(debateA, debateNs));
        // The legacy graph/cluster are untouched by a tenant purge: the legacy debate edge survives.
        Assert.Contains(_graph.GetAllEdges(""), e => e.SourceId == debateA && e.TargetId == debateB && e.Relation == "supports");
        Assert.Contains("debate-cluster", _clusters.GetClustersForEntry(debateA));
    }

    private NamespaceAccess Access(IPrincipalContext principal)
        => new(_registry, principal);

    private IntelligenceTools Intelligence(NamespaceAccess access)
        => new(_index, _graph, _embedding, _scanner, _clusters, _lifecycle, access);

    private void SeedCollidingPartitions()
    {
        // Same (ns, id) pairs in BOTH partitions, with distinct content.
        _index.Upsert(Entry(EntryA, Ns, "legacy alpha"));
        _index.Upsert(Entry(EntryB, Ns, "legacy beta"));
        _index.Upsert(Entry(EntryA, Ns, "tenant alpha", TenantId));
        _index.Upsert(Entry(EntryB, Ns, "tenant beta", TenantId));

        // Legacy graph + cluster.
        _graph.AddEdge(new GraphEdge(EntryA, EntryB, "similar_to"));
        _clusters.CreateCluster("global-cluster", Ns, [EntryA, EntryB], "legacy cluster");

        // Tenant-a graph + cluster over the same bare ids — must not collide with legacy.
        _graph.AddEdge(new GraphEdge(EntryA, EntryB, "depends_on", 1f, null, TenantId));
        _clusters.CreateCluster("tenant-cluster", Ns, [EntryA, EntryB], "tenant cluster", TenantId);

        // The identified tenant principal must own the namespace to reach it — an unregistered
        // namespace is closed to identified agents. The legacy default agent needs no ownership.
        _registry.EnsureOwnership(Ns, "alice", TenantId);
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
