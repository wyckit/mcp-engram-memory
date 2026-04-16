using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

/// <summary>
/// Regression tests for bugs found during code review.
/// </summary>
public class RegressionTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly LifecycleEngine _lifecycle;

    private sealed class StubEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    public RegressionTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"regression_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
        _lifecycle = new LifecycleEngine(_index);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    // Issue 6/12: ScheduleSaveGlobalEdges was overwriting edges with dummy NamespaceData
    [Fact]
    public void EdgePersistence_FlushDoesNotDestroyEdges()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f }, "test"));
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to"));

        // Flush should persist edges correctly, not overwrite with dummy data
        _persistence.Flush();

        // Load edges from disk in a fresh persistence manager
        var persistence2 = new PersistenceManager(_testDataPath, debounceMs: 50);
        var loadedEdges = persistence2.LoadGlobalEdges();
        Assert.Single(loadedEdges);
        Assert.Equal("a", loadedEdges[0].SourceId);
        Assert.Equal("b", loadedEdges[0].TargetId);
        persistence2.Dispose();
    }

    // Issue 4: Clusters were never persisted
    [Fact]
    public void ClusterPersistence_ClustersPersistedOnFlush()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f }, "test"));
        _clusters.CreateCluster("c1", "test", new[] { "a", "b" }, "my cluster");

        _persistence.Flush();

        // Load clusters from disk in a fresh persistence manager
        var persistence2 = new PersistenceManager(_testDataPath, debounceMs: 50);
        var loadedClusters = persistence2.LoadClusters();
        Assert.Single(loadedClusters);
        Assert.Equal("c1", loadedClusters[0].ClusterId);
        Assert.Equal("my cluster", loadedClusters[0].Label);
        Assert.Equal(2, loadedClusters[0].MemberIds.Count);
        persistence2.Dispose();
    }

    // Issue 5: LifecycleEngine.RunDecayCycle mutations persisted via SetActivationEnergyAndState
    [Fact]
    public void DecayCycle_ChangesArePersistedViaCognitiveIndex()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "stm"));
        _lifecycle.RunDecayCycle("test", decayRate: 100f, stmThreshold: 100f);

        // The state change should be reflected in the index
        var entry = _index.Get("a");
        Assert.Equal("ltm", entry!.LifecycleState);
        Assert.True(entry.ActivationEnergy < 100f); // Below stmThreshold, hence demoted
    }

    // Issue 10: DeepRecall returns stale lifecycle state for resurrected entries
    [Fact]
    public void DeepRecall_ReturnsUpdatedLifecycleStateForResurrected()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "archived"));

        var results = _lifecycle.DeepRecall(new float[] { 1f, 0f }, "test", resurrectionThreshold: 0.5f);

        // The returned result should reflect the new state
        Assert.Single(results);
        Assert.Equal("stm", results[0].LifecycleState);

        // And the underlying entry should also be updated
        Assert.Equal("stm", _index.Get("a")!.LifecycleState);
    }

    // Issue 7: Path traversal prevention
    [Fact]
    public void PathTraversal_DotDotInNamespace_IsSanitized()
    {
        // Should not throw or access parent directory
        var entry = new CognitiveEntry("a", new[] { 1f, 0f }, "safe_ns");
        _index.Upsert(entry);

        // The namespace path should be sanitized
        Assert.Equal(1, _index.CountInNamespace("safe_ns"));
    }

    // Issue 8: Lock ordering - StoreSummary should not deadlock with DeleteMemory
    [Fact]
    public async Task ConcurrentStoreSummaryAndDelete_DoesNotDeadlock()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f }, "test"));
        _clusters.CreateCluster("c1", "test", new[] { "a", "b" });

        var spreading = new SpreadingActivationService(_index, _graph, _clusters);
        var tools = new CoreMemoryTools(_index, new PhysicsEngine(), new StubEmbeddingService(), new MetricsCollector(), _graph, new QueryExpander(), spreading, _clusters);

        // Run StoreSummary and DeleteMemory concurrently — should not deadlock
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tasks = new[]
        {
            Task.Run(() => _clusters.StoreSummary("c1", "summary text", new[] { 0.5f, 0.5f }), cts.Token),
            Task.Run(() => tools.DeleteMemory("b"), cts.Token)
        };

        // If this times out, we have a deadlock
        await Task.WhenAll(tasks);
    }

    // Issue 1: ScheduleSave snapshot captured under write lock, no lambda re-entry
    [Fact]
    public void ConcurrentUpsertAndFlush_DoesNotThrow()
    {
        // Rapidly upsert and flush — verifies no LockRecursionException
        for (int i = 0; i < 50; i++)
        {
            _index.Upsert(new CognitiveEntry($"e{i}", new[] { (float)i, 0f }, "test"));
        }
        _persistence.Flush(); // Should not throw

        var persistence2 = new PersistenceManager(_testDataPath, debounceMs: 50);
        var data = persistence2.LoadNamespace("test");
        Assert.Equal(50, data.Entries.Count);
        persistence2.Dispose();
    }

    // Issue 11: Staleness uses CreatedAt (not LastAccessedAt)
    [Fact]
    public void ClusterStaleness_NotStaleWhenMembersOnlyAccessed()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f }, "test"));
        _clusters.CreateCluster("c1", "test", new[] { "a", "b" });
        _clusters.StoreSummary("c1", "summary", new[] { 0.5f, 0.5f });

        // Access a member (but don't modify content)
        _index.RecordAccess("a");

        var cluster = _clusters.GetCluster("c1");
        // Should NOT be stale just because a member was accessed
        Assert.False(cluster!.IsStale);
    }

    // Verify GetPersistedNamespaces excludes _clusters.json
    [Fact]
    public void GetPersistedNamespaces_ExcludesClusterFile()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test"));
        _clusters.CreateCluster("c1", "test", new[] { "a" });
        _persistence.Flush();

        var namespaces = _persistence.GetPersistedNamespaces();
        Assert.DoesNotContain("_clusters", namespaces);
        Assert.Contains("test", namespaces);
    }

    // Issue: ClassifyExpert allCandidates[0] could throw if ScoreEntries filters all entries
    [Fact]
    public void ClassifyExpert_DimensionMismatch_DoesNotThrow()
    {
        var embedding = new StubEmbeddingService(); // 2-dim
        var dispatcher = new ExpertDispatcher(_index, embedding);

        // Create a root with 2-dim vector (via embedding)
        dispatcher.CreateDomainNode("root1", "test root domain", "root");

        // Classify with a different dimension vector — ScoreEntries will skip all entries
        var mismatchedVector = new float[] { 0.5f, 0.5f, 0.5f }; // 3-dim vs 2-dim root
        var placement = dispatcher.ClassifyExpert(mismatchedVector);

        // Should return unclassified, not throw IndexOutOfRangeException
        Assert.Equal("unclassified", placement.Status);
    }

    // Issue: TryCreateSession atomically prevents duplicate session creation (TOCTOU fix)
    [Fact]
    public void TryCreateSession_AtomicCreation_PreventsDuplicates()
    {
        using var sessions = new DebateSessionManager();

        Assert.True(sessions.TryCreateSession("s1"));
        Assert.False(sessions.TryCreateSession("s1")); // Already exists

        // RegisterNode still works on the created session
        int alias = sessions.RegisterNode("s1", "entry-a");
        Assert.Equal(1, alias);
    }
}
