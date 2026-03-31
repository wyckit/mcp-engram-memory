using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class AdminToolsTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly AdminTools _tools;

    private sealed class StubEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    private sealed class StubStorageProvider : IStorageProvider
    {
        public bool SupportsIncrementalWrites => false;
        public NamespaceData LoadNamespace(string ns) => new();
        public IReadOnlyList<string> GetPersistedNamespaces() => Array.Empty<string>();
        public void ScheduleSave(string ns, Func<NamespaceData> dataProvider) { }
        public void SaveNamespaceSync(string ns, NamespaceData data) { }
        public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) { }
        public void ScheduleDeleteEntry(string ns, string entryId) { }
        public List<GraphEdge> LoadGlobalEdges() => new();
        public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider) { }
        public List<SemanticCluster> LoadClusters() => new();
        public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider) { }
        public List<CollapseRecord> LoadCollapseHistory() => new();
        public void ScheduleSaveCollapseHistory(Func<List<CollapseRecord>> dataProvider) { }
        public Dictionary<string, DecayConfig> LoadDecayConfigs() => new();
        public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider) { }
        public HnswSnapshot? LoadHnswSnapshot(string ns) => null;
        public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot) { }
        public void DeleteHnswSnapshot(string ns) { }
        public Task DeleteNamespaceAsync(string ns) => Task.CompletedTask;
        public void Flush() { }
        public void Dispose() { }
    }

    public AdminToolsTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"admin_tools_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
        _tools = new AdminTools(_index, _graph, _clusters, new StubStorageProvider());
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    // ── GetMemory ──

    [Fact]
    public void GetMemory_ExistingEntry_ReturnsFullContext()
    {
        var metadata = new Dictionary<string, string> { ["source"] = "unit-test" };
        _index.Upsert(new CognitiveEntry("entry1", new[] { 1f, 0f }, "work", "hello world", "note", metadata));

        var result = _tools.GetMemory("entry1");
        Assert.IsType<GetMemoryResult>(result);

        var mem = (GetMemoryResult)result;
        Assert.Equal("entry1", mem.Entry.Id);
        Assert.Equal("hello world", mem.Text);
        Assert.Equal("work", mem.Entry.Namespace);
        Assert.Equal("note", mem.Entry.Category);
        Assert.Equal("stm", mem.LifecycleState);
        Assert.Equal(1, mem.AccessCount);
        Assert.NotNull(mem.Metadata);
        Assert.Equal("unit-test", mem.Metadata!["source"]);
    }

    [Fact]
    public void GetMemory_NonExistent_ReturnsNotFound()
    {
        var result = _tools.GetMemory("missing-id");
        Assert.IsType<string>(result);
        Assert.Equal("Entry 'missing-id' not found.", (string)result);
    }

    [Fact]
    public void GetMemory_WithEdgesAndClusters()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "work", "entry a"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f }, "work", "entry b"));
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to"));
        _clusters.CreateCluster("c1", "work", new[] { "a", "b" });

        var result = _tools.GetMemory("a");
        Assert.IsType<GetMemoryResult>(result);

        var mem = (GetMemoryResult)result;
        Assert.NotEmpty(mem.Edges);
        Assert.Contains(mem.Edges, e => e.SourceId == "a" && e.TargetId == "b");
        Assert.NotEmpty(mem.ClusterIds);
        Assert.Contains("c1", mem.ClusterIds);
    }

    // ── CognitiveStats ──

    [Fact]
    public void CognitiveStats_ReturnsCorrectCounts()
    {
        _index.Upsert(new CognitiveEntry("s1", new[] { 1f, 0f }, "work", lifecycleState: "stm"));
        _index.Upsert(new CognitiveEntry("s2", new[] { 0f, 1f }, "work", lifecycleState: "stm"));
        _index.Upsert(new CognitiveEntry("l1", new[] { 1f, 1f }, "work", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("a1", new[] { 0.5f, 0.5f }, "work", lifecycleState: "archived"));
        _graph.AddEdge(new GraphEdge("s1", "s2", "similar_to"));
        _clusters.CreateCluster("c1", "work", new[] { "s1", "s2" });

        var stats = _tools.CognitiveStats();

        Assert.Equal(4, stats.TotalEntries);
        Assert.Equal(2, stats.StmCount);
        Assert.Equal(1, stats.LtmCount);
        Assert.Equal(1, stats.ArchivedCount);
        Assert.Equal(1, stats.EdgeCount);
        Assert.Equal(1, stats.ClusterCount);
        Assert.Contains("work", stats.Namespaces);
    }

    [Fact]
    public void CognitiveStats_NamespaceFilter()
    {
        _index.Upsert(new CognitiveEntry("w1", new[] { 1f, 0f }, "work", lifecycleState: "stm"));
        _index.Upsert(new CognitiveEntry("p1", new[] { 0f, 1f }, "personal", lifecycleState: "ltm"));

        var stats = _tools.CognitiveStats("work");

        Assert.Equal(1, stats.TotalEntries);
        Assert.Equal(1, stats.StmCount);
        Assert.Equal(0, stats.LtmCount);
    }

    [Fact]
    public void CognitiveStats_EmptyIndex()
    {
        var stats = _tools.CognitiveStats();

        Assert.Equal(0, stats.TotalEntries);
        Assert.Equal(0, stats.StmCount);
        Assert.Equal(0, stats.LtmCount);
        Assert.Equal(0, stats.ArchivedCount);
        Assert.Equal(0, stats.ClusterCount);
        Assert.Equal(0, stats.EdgeCount);
        Assert.Empty(stats.Namespaces);
    }

    // ── PurgeDebates ──

    [Fact]
    public async Task PurgeDebates_DryRun_ReportsButDoesNotDelete()
    {
        // Create a stale debate namespace (entries created >25 hours ago)
        var entry = new CognitiveEntry("d1", new[] { 1f, 0f }, "active-debate-123", "debate entry");
        entry.CreatedAt = DateTimeOffset.UtcNow.AddHours(-25);
        _index.Upsert(entry);
        _graph.AddEdge(new GraphEdge("d1", "d1", "self_ref"));

        var result = await _tools.PurgeDebates(maxAgeHours: 24, dryRun: true);
        var purgeResult = Assert.IsType<PurgeDebatesResult>(result);

        Assert.True(purgeResult.DryRun);
        Assert.Equal(1, purgeResult.NamespacesAffected);
        Assert.Equal(1, purgeResult.TotalEntriesRemoved);
        Assert.True(purgeResult.TotalEdgesRemoved > 0);

        // Entry should still exist (dry run)
        Assert.NotNull(_index.Get("d1"));
    }

    [Fact]
    public async Task PurgeDebates_Execute_DeletesStaleDebateNamespaces()
    {
        // Create a stale debate namespace (entries created >25 hours ago)
        var entry = new CognitiveEntry("d1", new[] { 1f, 0f }, "active-debate-456", "debate entry");
        entry.CreatedAt = DateTimeOffset.UtcNow.AddHours(-25);
        _index.Upsert(entry);

        var entry2 = new CognitiveEntry("d2", new[] { 0f, 1f }, "active-debate-456", "debate entry 2");
        entry2.CreatedAt = DateTimeOffset.UtcNow.AddHours(-26);
        _index.Upsert(entry2);

        _graph.AddEdge(new GraphEdge("d1", "d2", "similar_to"));
        _clusters.CreateCluster("dc1", "active-debate-456", new[] { "d1", "d2" });

        var result = await _tools.PurgeDebates(maxAgeHours: 24, dryRun: false);
        var purgeResult = Assert.IsType<PurgeDebatesResult>(result);

        Assert.False(purgeResult.DryRun);
        Assert.Equal(1, purgeResult.NamespacesAffected);
        Assert.Equal(2, purgeResult.TotalEntriesRemoved);

        // Entries should be deleted
        Assert.Null(_index.Get("d1"));
        Assert.Null(_index.Get("d2"));

        // Edges should be removed
        Assert.Equal(0, _graph.EdgeCount);
    }

    [Fact]
    public async Task PurgeDebates_NoDebateNamespaces_ReturnsEmpty()
    {
        // Only non-debate namespaces exist
        _index.Upsert(new CognitiveEntry("w1", new[] { 1f, 0f }, "work", "regular entry"));

        var result = await _tools.PurgeDebates(maxAgeHours: 24, dryRun: false);
        var purgeResult = Assert.IsType<PurgeDebatesResult>(result);

        Assert.Equal(0, purgeResult.NamespacesAffected);
        Assert.Equal(0, purgeResult.TotalEntriesRemoved);
        Assert.Equal(0, purgeResult.TotalEdgesRemoved);
        Assert.Empty(purgeResult.Namespaces);
    }
}
