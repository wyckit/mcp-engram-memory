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

    // ── purge_debates cascade under a real tenant ──
    //
    // Every test below is seeded under tenant "t1", NOT the legacy tenant. In the legacy tenant
    // CognitiveIndex.Delete's UntrackEntry path and the global _idToNamespace alias make two
    // same-id entries alias one another, so a legacy-seeded version of these tests would appear
    // to pass or fail for reasons that have nothing to do with the cascade guard under test.
    //
    // Tenancy also makes the principal genuinely identified: NamespaceRegistry.HasAccess only
    // short-circuits the default agent to unrestricted access when the tenant is the legacy ""
    // partition, so under "t1" purge_debates really does have to pass the write gate, and
    // ownership of each debate namespace is registered explicitly below.

    private const string PurgeTenant = "t1";
    private const string PurgeAgent = "purge-agent";
    private const string LiveNs = "live-ns";

    /// <summary>Seed one entry into <see cref="PurgeTenant"/>, optionally back-dated to look stale.</summary>
    private void SeedTenantEntry(string id, string ns, string text, DateTimeOffset? createdAt = null)
        => _index.Upsert(createdAt.HasValue
            ? MakeEntryWithTimestamp(id, new[] { 1f, 0f }, ns, text, createdAt.Value, PurgeTenant)
            : new CognitiveEntry(id, new[] { 1f, 0f }, ns, text, tenantId: PurgeTenant));

    /// <summary>
    /// purge_debates driven as a tenant-scoped principal that owns <paramref name="ownedNamespaces"/>.
    /// Ownership has to be registered because an identified principal never inherits an unregistered
    /// namespace and cannot claim a non-empty one; without it the tool would simply filter the debate
    /// namespace out and every assertion below would be vacuous.
    /// </summary>
    private AdminTools PurgeAdmin(NamespaceRegistry registry, params string[] ownedNamespaces)
    {
        foreach (var ns in ownedNamespaces)
            registry.EnsureOwnership(ns, PurgeAgent, PurgeTenant);
        return new AdminTools(_index, _graph, _clusters, _persistence, registry,
            new PrincipalContext(PurgeTenant, PurgeAgent));
    }

    [Fact]
    public async Task PurgeDebates_AmbiguousId_PreservesSurvivingNamespaceEdges()
    {
        const string debateNs = "active-debate-ambiguous-edges";
        var registry = new NamespaceRegistry(_index, new CleanupStubEmbedding());
        var stale = DateTimeOffset.UtcNow.AddHours(-48);

        // "shared" is held by BOTH the stale debate namespace and a live one inside tenant t1.
        // Graph adjacency is keyed by (tenant, bare id), so the single edge below is reachable
        // from either entry and nothing at the cascade level can attribute it to one of them.
        SeedTenantEntry("shared", debateNs, "debate copy", stale);
        SeedTenantEntry("shared", LiveNs, "live copy");
        SeedTenantEntry("live-anchor", LiveNs, "live anchor");
        _graph.AddEdge(new GraphEdge("shared", "live-anchor", "similar_to", tenantId: PurgeTenant));
        Assert.Single(_graph.GetEdgesForEntry("shared", PurgeTenant));

        var result = Assert.IsType<PurgeDebatesResult>(
            await PurgeAdmin(registry, debateNs).PurgeDebates(maxAgeHours: 24, dryRun: false));

        // The debate entry itself still goes: DeleteAllInNamespace is namespace-scoped, so it can
        // only ever reach the debate copy.
        Assert.Equal(0, _index.CountInNamespace(debateNs, PurgeTenant));
        // The id was ambiguous, so its topology was declined rather than guessed at — and the
        // refusal is reported, not swallowed.
        Assert.Equal(1, result.TotalIdsSkippedAmbiguous);
        Assert.Equal(0, result.TotalEdgesRemoved);

        // The live namespace's entry keeps the edge it never should have lost.
        var surviving = _graph.GetEdgesForEntry("shared", PurgeTenant);
        Assert.Single(surviving);
        Assert.Equal("live-anchor", surviving[0].TargetId);
        Assert.Equal("similar_to", surviving[0].Relation);

        // With the debate copy gone the id resolves unambiguously again, to the live entry.
        var resolved = _index.GetForTenant("shared", PurgeTenant);
        Assert.NotNull(resolved);
        Assert.Equal(LiveNs, resolved!.Ns);
        Assert.Equal("live copy", resolved.Text);
    }

    [Fact]
    public async Task PurgeDebates_AmbiguousId_PreservesClusterMembership()
    {
        const string debateNs = "active-debate-ambiguous-clusters";
        var registry = new NamespaceRegistry(_index, new CleanupStubEmbedding());
        var stale = DateTimeOffset.UtcNow.AddHours(-48);

        // Same ambiguity as above, but the topology at risk is cluster membership, which is keyed
        // by (tenant, bare id) for exactly the same reason.
        SeedTenantEntry("shared", debateNs, "debate copy", stale);
        SeedTenantEntry("shared", LiveNs, "live copy");
        SeedTenantEntry("live-anchor", LiveNs, "live anchor");
        _clusters.CreateCluster("live-cluster", LiveNs, new[] { "shared", "live-anchor" },
            "live cluster", PurgeTenant);
        Assert.Equal(2, _clusters.GetCluster("live-cluster", PurgeTenant)!.MemberCount);

        var result = Assert.IsType<PurgeDebatesResult>(
            await PurgeAdmin(registry, debateNs).PurgeDebates(maxAgeHours: 24, dryRun: false));

        Assert.Equal(0, _index.CountInNamespace(debateNs, PurgeTenant));
        Assert.Equal(1, result.TotalIdsSkippedAmbiguous);

        // Membership is intact, and still paired with the cluster's OWN namespace.
        var membership = Assert.Single(_clusters.GetClusterMembershipsForEntry("shared", PurgeTenant));
        Assert.Equal("live-cluster", membership.ClusterId);
        Assert.Equal(LiveNs, membership.Ns);
        Assert.Equal(2, _clusters.GetCluster("live-cluster", PurgeTenant)!.MemberCount);
    }

    [Fact]
    public async Task PurgeDebates_DryRun_MatchesRealPurgeEdgeCount()
    {
        const string debateNs = "active-debate-dryrun-parity";
        var registry = new NamespaceRegistry(_index, new CleanupStubEmbedding());
        var stale = DateTimeOffset.UtcNow.AddHours(-48);

        SeedTenantEntry("cd1", debateNs, "debate one", stale);
        SeedTenantEntry("cd2", debateNs, "debate two", stale);
        SeedTenantEntry("cascade-anchor", LiveNs, "cascade anchor");

        // cd1 -> cd2 is INTERNAL to the swept set, so it appears in both entries' edge lists.
        // Summing those lists reports 4 where the purge removes 3; the dry run and the purge must
        // agree, which is why both now run the same TopologyCascade.CascadeAll call.
        _graph.AddEdge(new GraphEdge("cd1", "cd2", "similar_to", tenantId: PurgeTenant));
        _graph.AddEdge(new GraphEdge("cd1", "cascade-anchor", "depends_on", tenantId: PurgeTenant));
        _graph.AddEdge(new GraphEdge("cd2", "cascade-anchor", "depends_on", tenantId: PurgeTenant));

        var dry = Assert.IsType<PurgeDebatesResult>(
            await PurgeAdmin(registry, debateNs).PurgeDebates(maxAgeHours: 24, dryRun: true));
        Assert.True(dry.DryRun);
        // A preview must not have moved anything, or the comparison below is not a comparison.
        Assert.Equal(2, _index.CountInNamespace(debateNs, PurgeTenant));
        Assert.Equal(3, _graph.EdgeCount);

        var real = Assert.IsType<PurgeDebatesResult>(
            await PurgeAdmin(registry, debateNs).PurgeDebates(maxAgeHours: 24, dryRun: false));
        Assert.False(real.DryRun);

        Assert.Equal(dry.TotalEdgesRemoved, real.TotalEdgesRemoved);
        Assert.Equal(dry.NamespacesAffected, real.NamespacesAffected);
        Assert.Equal(dry.TotalEntriesRemoved, real.TotalEntriesRemoved);
        Assert.Equal(dry.TotalIdsSkippedAmbiguous, real.TotalIdsSkippedAmbiguous);
        // Pin the shared figure: an equality between two zeroes would prove nothing, and 4 is the
        // double-counted answer the dry run used to give.
        Assert.Equal(3, real.TotalEdgesRemoved);
        Assert.Equal(0, _graph.EdgeCount);
    }

    [Fact]
    public async Task PurgeDebates_UnambiguousId_StillCascades()
    {
        const string debateNs = "active-debate-unambiguous";
        var registry = new NamespaceRegistry(_index, new CleanupStubEmbedding());
        var stale = DateTimeOffset.UtcNow.AddHours(-48);

        // Over-correction control: nothing else in tenant t1 answers to "solo-d1", so the guard
        // has nothing to be ambiguous about and the cascade must still run in full.
        SeedTenantEntry("solo-d1", debateNs, "the only holder of this id", stale);
        SeedTenantEntry("solo-anchor", LiveNs, "solo anchor");
        _graph.AddEdge(new GraphEdge("solo-d1", "solo-anchor", "similar_to", tenantId: PurgeTenant));
        _clusters.CreateCluster("solo-cluster", debateNs, new[] { "solo-d1", "solo-anchor" },
            "solo cluster", PurgeTenant);

        var result = Assert.IsType<PurgeDebatesResult>(
            await PurgeAdmin(registry, debateNs).PurgeDebates(maxAgeHours: 24, dryRun: false));

        Assert.Equal(0, result.TotalIdsSkippedAmbiguous);
        Assert.Equal(1, result.TotalEntriesRemoved);
        Assert.Equal(1, result.TotalEdgesRemoved);
        Assert.Equal(0, _index.CountInNamespace(debateNs, PurgeTenant));
        Assert.Empty(_graph.GetEdgesForEntry("solo-d1", PurgeTenant));
        Assert.Empty(_clusters.GetClusterMembershipsForEntry("solo-d1", PurgeTenant));

        // The guard skips ambiguous ids; it does not disable the cascade, and it does not take
        // the co-member down with it.
        var surviving = _clusters.GetCluster("solo-cluster", PurgeTenant);
        Assert.NotNull(surviving);
        Assert.Equal(1, surviving!.MemberCount);
        Assert.Equal("solo-anchor", Assert.Single(surviving.Members).Id);
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
    public async Task PersistenceManager_TenantScopedNamespaceDelete_PreservesOtherTenant()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"pm_tenant_delete_{Guid.NewGuid():N}");
        using var pm = new PersistenceManager(tempPath, debounceMs: 50);
        pm.SaveNamespaceSync("shared", new NamespaceData
        {
            Entries =
            [
                new CognitiveEntry("same", [1f], "shared", "tenant A", tenantId: "a"),
                new CognitiveEntry("same", [2f], "shared", "tenant B", tenantId: "b")
            ]
        });

        await pm.DeleteNamespaceAsync("shared", "a");

        var remaining = pm.LoadNamespace("shared").Entries;
        Assert.Single(remaining);
        Assert.Equal("b", remaining[0].TenantId);
        Assert.Equal("tenant B", remaining[0].Text);
    }

    [Fact]
    public async Task SqliteStorageProvider_TenantScopedNamespaceDelete_PreservesOtherTenant()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sqlite_tenant_delete_{Guid.NewGuid():N}", "memory.db");
        using var provider = new SqliteStorageProvider(dbPath, debounceMs: 10);
        provider.SaveNamespaceSync("shared", new NamespaceData
        {
            Entries =
            [
                new CognitiveEntry("same", [1f], "shared", "tenant A", tenantId: "a"),
                new CognitiveEntry("same", [2f], "shared", "tenant B", tenantId: "b")
            ]
        });

        await provider.DeleteNamespaceAsync("shared", "a");

        var remaining = provider.LoadNamespace("shared").Entries;
        Assert.Single(remaining);
        Assert.Equal("b", remaining[0].TenantId);
        Assert.Equal("tenant B", remaining[0].Text);
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

    /// <summary>
    /// Create a CognitiveEntry with a specific CreatedAt timestamp (for testing staleness).
    /// <paramref name="tenantId"/> defaults to null, i.e. the legacy "" partition, which is what
    /// every pre-tenancy caller of this helper already got.
    /// </summary>
    private static CognitiveEntry MakeEntryWithTimestamp(string id, float[] vector, string ns,
        string text, DateTimeOffset createdAt, string? tenantId = null)
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
            sourceClusterId: null,
            keywords: null,
            tenantId: tenantId);
    }
}
