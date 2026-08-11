using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;
using McpEngramMemory.Core.Services.Sharing;
using Microsoft.Data.Sqlite;

namespace McpEngramMemory.Tests;

public class NamespaceCleanupTests : IDisposable
{
    private sealed class CleanupStubEmbedding : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;

    public NamespaceCleanupTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"cleanup_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    // ── DeleteAllInNamespace ──

    [Fact]
    public void DeleteAllInNamespace_RemovesAllEntries()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "debate-ns", "entry a"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f }, "debate-ns", "entry b"));
        _index.Upsert(new CognitiveEntry("c", new[] { 1f, 1f }, "debate-ns", "entry c"));
        Assert.Equal(3, _index.CountInNamespace("debate-ns"));

        int removed = _index.DeleteAllInNamespace("debate-ns");

        Assert.Equal(3, removed);
        Assert.Equal(0, _index.CountInNamespace("debate-ns"));
    }

    [Fact]
    public void DeleteAllInNamespace_DoesNotAffectOtherNamespaces()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "debate-ns", "entry a"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f }, "other-ns", "entry b"));

        _index.DeleteAllInNamespace("debate-ns");

        Assert.Equal(0, _index.CountInNamespace("debate-ns"));
        Assert.Equal(1, _index.CountInNamespace("other-ns"));
    }

    [Fact]
    public void DeleteAllInNamespace_EmptyNamespace_ReturnsZero()
    {
        int removed = _index.DeleteAllInNamespace("nonexistent");
        Assert.Equal(0, removed);
    }

    [Fact]
    public void DeleteAllInNamespace_CascadesToGraphEdges()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "debate-ns", "entry a"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f }, "debate-ns", "entry b"));
        _index.Upsert(new CognitiveEntry("c", new[] { 1f, 1f }, "other-ns", "entry c"));

        _graph.AddEdge(new GraphEdge("a", "b", "similar_to"));
        _graph.AddEdge(new GraphEdge("a", "c", "depends_on"));
        Assert.True(_graph.EdgeCount >= 2);

        // Remove edges for entries in debate-ns before deleting the namespace
        var entries = _index.GetAllInNamespace("debate-ns");
        foreach (var entry in entries)
            _graph.RemoveAllEdgesForEntry(entry.Id);

        _index.DeleteAllInNamespace("debate-ns");

        // All edges involving entries from debate-ns should be gone
        Assert.Equal(0, _graph.EdgeCount);
    }

    [Fact]
    public void DeleteAllInNamespace_CascadesToClusters()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "debate-ns", "entry a"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f }, "debate-ns", "entry b"));
        _index.Upsert(new CognitiveEntry("c", new[] { 1f, 1f }, "other-ns", "entry c"));

        _clusters.CreateCluster("c1", "debate-ns", new[] { "a", "b", "c" }, "test cluster");
        var cluster = _clusters.GetCluster("c1");
        Assert.Equal(3, cluster!.MemberCount);

        // Remove cluster memberships for entries in debate-ns before deleting
        var entries = _index.GetAllInNamespace("debate-ns");
        foreach (var entry in entries)
            _clusters.RemoveEntryFromAllClusters(entry.Id);

        _index.DeleteAllInNamespace("debate-ns");

        // Cluster should only contain entry c now
        cluster = _clusters.GetCluster("c1");
        Assert.Equal(1, cluster!.MemberCount);
    }

    // ── purge_debates tool ──

    [Fact]
    public async Task PurgeDebates_DryRun_ListsButDoesNotDelete()
    {
        // Create a stale debate namespace with entries having old timestamps
        var oldTime = DateTimeOffset.UtcNow.AddHours(-48);
        var entry = MakeEntryWithTimestamp("d1", new[] { 1f, 0f }, "active-debate-old-session",
            "debate entry", oldTime);
        _index.Upsert(entry);

        var tool = new AdminTools(_index, _graph, _clusters, _persistence, new NamespaceRegistry(_index, new CleanupStubEmbedding()), AgentIdentity.Default);
        var result = await tool.PurgeDebates(maxAgeHours: 24, dryRun: true);

        var purgeResult = Assert.IsType<PurgeDebatesResult>(result);
        Assert.True(purgeResult.DryRun);
        Assert.True(purgeResult.NamespacesAffected > 0);
        // Entries should still exist (dry run)
        Assert.Equal(1, _index.CountInNamespace("active-debate-old-session"));
    }

    [Fact]
    public async Task PurgeDebates_DeletesStaleNamespaces()
    {
        var oldTime = DateTimeOffset.UtcNow.AddHours(-48);
        var entry = MakeEntryWithTimestamp("d1", new[] { 1f, 0f }, "active-debate-stale",
            "debate entry", oldTime);
        _index.Upsert(entry);

        var tool = new AdminTools(_index, _graph, _clusters, _persistence, new NamespaceRegistry(_index, new CleanupStubEmbedding()), AgentIdentity.Default);
        var result = await tool.PurgeDebates(maxAgeHours: 24, dryRun: false);

        var purgeResult = Assert.IsType<PurgeDebatesResult>(result);
        Assert.False(purgeResult.DryRun);
        Assert.True(purgeResult.NamespacesAffected > 0);
        Assert.Equal(0, _index.CountInNamespace("active-debate-stale"));
    }

    [Fact]
    public async Task PurgeDebates_SkipsRecentNamespaces()
    {
        // Create a recent debate namespace
        var entry = new CognitiveEntry("d1", new[] { 1f, 0f }, "active-debate-recent",
            "debate entry"); // CreatedAt defaults to UtcNow
        _index.Upsert(entry);

        var tool = new AdminTools(_index, _graph, _clusters, _persistence, new NamespaceRegistry(_index, new CleanupStubEmbedding()), AgentIdentity.Default);
        var result = await tool.PurgeDebates(maxAgeHours: 24, dryRun: false);

        var purgeResult = Assert.IsType<PurgeDebatesResult>(result);
        Assert.Equal(0, purgeResult.NamespacesAffected);
        // Entry should still exist
        Assert.Equal(1, _index.CountInNamespace("active-debate-recent"));
    }

    [Fact]
    public async Task PurgeDebates_SkipsNonDebateNamespaces()
    {
        var oldTime = DateTimeOffset.UtcNow.AddHours(-48);
        _index.Upsert(MakeEntryWithTimestamp("e1", new[] { 1f, 0f }, "work", "work entry", oldTime));
        _index.Upsert(MakeEntryWithTimestamp("e2", new[] { 0f, 1f }, "active-debate-old",
            "debate entry", oldTime));

        var tool = new AdminTools(_index, _graph, _clusters, _persistence, new NamespaceRegistry(_index, new CleanupStubEmbedding()), AgentIdentity.Default);
        var result = await tool.PurgeDebates(maxAgeHours: 24, dryRun: false);

        var purgeResult = Assert.IsType<PurgeDebatesResult>(result);
        // Only the debate namespace should be affected
        Assert.Equal(1, purgeResult.NamespacesAffected);
        Assert.Equal(1, _index.CountInNamespace("work"));
        Assert.Equal(0, _index.CountInNamespace("active-debate-old"));
    }

    // ── DeleteNamespaceAsync for both storage providers ──

    [Fact]
    public async Task PersistenceManager_DeleteNamespaceAsync_RemovesFiles()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"pm_delete_test_{Guid.NewGuid():N}");
        using var pm = new PersistenceManager(tempPath, debounceMs: 50);

        var entry = new CognitiveEntry("a", new[] { 1f, 0f }, "testns", "hello");
        pm.SaveNamespaceSync("testns", new NamespaceData { Entries = [entry] });

        // Verify files exist
        var namespaces = pm.GetPersistedNamespaces();
        Assert.Contains("testns", namespaces);

        await pm.DeleteNamespaceAsync("testns");

        // Verify files are gone
        namespaces = pm.GetPersistedNamespaces();
        Assert.DoesNotContain("testns", namespaces);

        pm.Dispose();
        if (Directory.Exists(tempPath))
            Directory.Delete(tempPath, true);
    }

    [Fact]
    public async Task SqliteStorageProvider_DeleteNamespaceAsync_RemovesEntries()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sqlite_delete_test_{Guid.NewGuid():N}", "memory.db");
        using var provider = new SqliteStorageProvider(dbPath, debounceMs: 10);

        var entry = new CognitiveEntry("a", new[] { 1f, 0f }, "testns", "hello");
        provider.SaveNamespaceSync("testns", new NamespaceData { Entries = [entry] });

        // Verify entry exists
        var loaded = provider.LoadNamespace("testns");
        Assert.Single(loaded.Entries);

        await provider.DeleteNamespaceAsync("testns");

        // Verify entry is gone
        loaded = provider.LoadNamespace("testns");
        Assert.Empty(loaded.Entries);

        provider.Dispose();
        SqliteConnection.ClearAllPools();
        var dir = Path.GetDirectoryName(dbPath);
        if (dir is not null && Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    [Fact]
    public async Task PersistenceManager_DeleteNamespaceAsync_NonExistent_DoesNotThrow()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"pm_delete_noexist_{Guid.NewGuid():N}");
        using var pm = new PersistenceManager(tempPath, debounceMs: 50);

        // Should not throw
        await pm.DeleteNamespaceAsync("nonexistent");

        pm.Dispose();
        if (Directory.Exists(tempPath))
            Directory.Delete(tempPath, true);
    }

    [Fact]
    public async Task SqliteStorageProvider_DeleteNamespaceAsync_NonExistent_DoesNotThrow()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sqlite_delete_noexist_{Guid.NewGuid():N}", "memory.db");
        using var provider = new SqliteStorageProvider(dbPath, debounceMs: 10);

        // Should not throw
        await provider.DeleteNamespaceAsync("nonexistent");

        provider.Dispose();
        SqliteConnection.ClearAllPools();
        var dir = Path.GetDirectoryName(dbPath);
        if (dir is not null && Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    // ── Helper ──

    /// <summary>Create a CognitiveEntry with a specific CreatedAt timestamp (for testing staleness).</summary>
    private static CognitiveEntry MakeEntryWithTimestamp(string id, float[] vector, string ns,
        string text, DateTimeOffset createdAt)
    {
        return new CognitiveEntry(
            id, vector, ns, text,
            category: null,
            metadata: new Dictionary<string, string>(),
            lifecycleState: "stm",
            createdAt: createdAt,
            lastAccessedAt: createdAt,
            accessCount: 1,
            activationEnergy: 0f,
            isSummaryNode: false,
            sourceClusterId: null);
    }
}
