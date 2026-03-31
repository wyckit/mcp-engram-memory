using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class PhysicsEngineTests
{
    private readonly PhysicsEngine _engine = new();

    private sealed class StubEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    // ── ComputeMass ──

    [Fact]
    public void ComputeMass_ZeroAccess_ReturnsZero()
    {
        // log(1 + 0) * tierWeight = 0
        Assert.Equal(0f, PhysicsEngine.ComputeMass(0, "stm"));
        Assert.Equal(0f, PhysicsEngine.ComputeMass(0, "ltm"));
        Assert.Equal(0f, PhysicsEngine.ComputeMass(0, "archived"));
    }

    [Fact]
    public void ComputeMass_LtmHasHigherWeightThanStm()
    {
        float stmMass = PhysicsEngine.ComputeMass(10, "stm");
        float ltmMass = PhysicsEngine.ComputeMass(10, "ltm");
        Assert.True(ltmMass > stmMass);
        Assert.Equal(stmMass * 2f, ltmMass, precision: 5);
    }

    [Fact]
    public void ComputeMass_ArchivedHasLowestWeight()
    {
        float archivedMass = PhysicsEngine.ComputeMass(10, "archived");
        float stmMass = PhysicsEngine.ComputeMass(10, "stm");
        Assert.True(archivedMass < stmMass);
    }

    [Fact]
    public void ComputeMass_MoreAccessMeansMoreMass()
    {
        float lowAccess = PhysicsEngine.ComputeMass(2, "ltm");
        float highAccess = PhysicsEngine.ComputeMass(100, "ltm");
        Assert.True(highAccess > lowAccess);
    }

    [Fact]
    public void ComputeMass_UnknownState_UsesDefaultWeight()
    {
        // Unknown state defaults to tierWeight 1.0 (same as STM)
        float unknownMass = PhysicsEngine.ComputeMass(10, "unknown");
        float stmMass = PhysicsEngine.ComputeMass(10, "stm");
        Assert.Equal(stmMass, unknownMass, precision: 5);
    }

    // ── ComputeGravity ──

    [Fact]
    public void ComputeGravity_HighScore_HighGravity()
    {
        // distance = 1 - 0.99 = 0.01, F = mass / 0.01^2 = mass / 0.0001
        float gravity = PhysicsEngine.ComputeGravity(1.0f, 0.99f);
        Assert.True(gravity > 10000f); // 1.0 / 0.0001 = 10000
    }

    [Fact]
    public void ComputeGravity_LowScore_LowGravity()
    {
        // distance = 1 - 0.1 = 0.9, F = mass / 0.81
        float gravity = PhysicsEngine.ComputeGravity(1.0f, 0.1f);
        Assert.True(gravity < 2f);
    }

    [Fact]
    public void ComputeGravity_PerfectScore_ClampsDistance()
    {
        // score = 1.0, distance = max(0, 0.001) = 0.001
        float gravity = PhysicsEngine.ComputeGravity(1.0f, 1.0f);
        Assert.Equal(1.0f / (0.001f * 0.001f), gravity, precision: 1);
    }

    [Fact]
    public void ComputeGravity_ZeroMass_ZeroGravity()
    {
        Assert.Equal(0f, PhysicsEngine.ComputeGravity(0f, 0.9f));
    }

    // ── Slingshot ──

    [Fact]
    public void Slingshot_EmptyResults_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _engine.Slingshot(Array.Empty<CognitiveSearchResult>()));
    }

    [Fact]
    public void Slingshot_SingleResult_AsteroidAndSunAreSame()
    {
        var results = new[]
        {
            MakeResult("a", score: 0.9f, accessCount: 5, state: "ltm")
        };

        var slingshot = _engine.Slingshot(results);
        Assert.Equal("a", slingshot.Asteroid.Id);
        Assert.Equal("a", slingshot.Sun.Id);
        Assert.Single(slingshot.AllResults);
    }

    [Fact]
    public void Slingshot_Asteroid_IsHighestCosineScore()
    {
        // "a" has highest cosine (0.95), "b" is lower but more accessed
        var results = new[]
        {
            MakeResult("a", score: 0.95f, accessCount: 1, state: "stm"),
            MakeResult("b", score: 0.7f, accessCount: 100, state: "ltm"),
        };

        var slingshot = _engine.Slingshot(results);
        Assert.Equal("a", slingshot.Asteroid.Id);
    }

    [Fact]
    public void Slingshot_Sun_IsHighestGravityForce()
    {
        // "a" has highest cosine but low mass; "b" has slightly lower cosine but massive access count + LTM tier weight
        // With close scores, "b"'s much higher mass should dominate gravity
        var results = new[]
        {
            MakeResult("a", score: 0.8f, accessCount: 1, state: "stm"),
            MakeResult("b", score: 0.75f, accessCount: 1000, state: "ltm"),
        };

        var slingshot = _engine.Slingshot(results);
        Assert.Equal("a", slingshot.Asteroid.Id); // highest cosine
        Assert.Equal("b", slingshot.Sun.Id); // highest gravity (massive mass with similar distance)
        Assert.True(slingshot.Sun.GravityForce > slingshot.Asteroid.GravityForce);
    }

    [Fact]
    public void Slingshot_AllResults_SortedByGravityDescending()
    {
        var results = new[]
        {
            MakeResult("a", score: 0.9f, accessCount: 1, state: "stm"),
            MakeResult("b", score: 0.8f, accessCount: 50, state: "ltm"),
            MakeResult("c", score: 0.7f, accessCount: 200, state: "ltm"),
        };

        var slingshot = _engine.Slingshot(results);

        for (int i = 0; i < slingshot.AllResults.Count - 1; i++)
            Assert.True(slingshot.AllResults[i].GravityForce >= slingshot.AllResults[i + 1].GravityForce);
    }

    [Fact]
    public void Slingshot_ZeroAccessEntries_HaveZeroMassAndGravity()
    {
        var results = new[]
        {
            MakeResult("a", score: 0.9f, accessCount: 0, state: "stm"),
        };

        var slingshot = _engine.Slingshot(results);
        Assert.Equal(0f, slingshot.Asteroid.Mass);
        Assert.Equal(0f, slingshot.Asteroid.GravityForce);
    }

    [Fact]
    public void Slingshot_PreservesAllFields()
    {
        var results = new[]
        {
            new CognitiveSearchResult("x", "hello", 0.8f, "ltm", 1.5f, "notes",
                new Dictionary<string, string> { ["k"] = "v" }, true, "cluster1", 10)
        };

        var slingshot = _engine.Slingshot(results);
        var r = slingshot.Asteroid;
        Assert.Equal("x", r.Id);
        Assert.Equal("hello", r.Text);
        Assert.Equal(0.8f, r.CosineScore);
        Assert.Equal("ltm", r.LifecycleState);
        Assert.Equal(1.5f, r.ActivationEnergy);
        Assert.Equal(10, r.AccessCount);
        Assert.Equal("notes", r.Category);
        Assert.True(r.IsSummaryNode);
        Assert.Equal("cluster1", r.SourceClusterId);
        Assert.True(r.Mass > 0f);
        Assert.True(r.GravityForce > 0f);
    }

    // ── Integration: search_memory with usePhysics ──

    [Fact]
    public void SearchMemory_UsePhysics_ReturnsSlingshotResult()
    {
        var testDataPath = Path.Combine(Path.GetTempPath(), $"physics_test_{Guid.NewGuid():N}");
        var persistence = new PersistenceManager(testDataPath, debounceMs: 50);
        var index = new CognitiveIndex(persistence);
        var physics = new PhysicsEngine();
        var graph = new KnowledgeGraph(persistence, index);
        var clusters = new ClusterManager(index, persistence);
        var spreading = new SpreadingActivationService(index, graph, clusters);
        var tools = new CoreMemoryTools(index, physics, new StubEmbeddingService(), new MetricsCollector(), graph, new QueryExpander(), spreading, clusters);

        try
        {
            // Both entries at moderate distance from query, but "a" is closer (asteroid)
            tools.StoreMemory(id: "a", ns: "test", text: "first", vector: new[] { 0.9f, 0.436f });
            tools.StoreMemory(id: "b", ns: "test", text: "second", vector: new[] { 0.85f, 0.527f });

            // Simulate access to make "b" more massive — enough to overcome distance difference
            for (int i = 0; i < 50; i++)
                index.RecordAccess("b");

            var result = tools.SearchMemory(ns: "test", vector: new[] { 1f, 0f }, usePhysics: true);
            Assert.IsType<SlingshotResult>(result);

            var slingshot = (SlingshotResult)result;
            Assert.Equal("a", slingshot.Asteroid.Id);
            Assert.Equal("b", slingshot.Sun.Id);
        }
        finally
        {
            index.Dispose();
            persistence.Dispose();
            if (Directory.Exists(testDataPath))
                Directory.Delete(testDataPath, true);
        }
    }

    [Fact]
    public void SearchMemory_UsePhysicsFalse_ReturnsFlatList()
    {
        var testDataPath = Path.Combine(Path.GetTempPath(), $"physics_test_{Guid.NewGuid():N}");
        var persistence = new PersistenceManager(testDataPath, debounceMs: 50);
        var index = new CognitiveIndex(persistence);
        var physics = new PhysicsEngine();
        var graph = new KnowledgeGraph(persistence, index);
        var clusters = new ClusterManager(index, persistence);
        var spreading = new SpreadingActivationService(index, graph, clusters);
        var tools = new CoreMemoryTools(index, physics, new StubEmbeddingService(), new MetricsCollector(), graph, new QueryExpander(), spreading, clusters);

        try
        {
            tools.StoreMemory(id: "a", ns: "test", text: "first", vector: new[] { 1f, 0f });
            var result = tools.SearchMemory(ns: "test", vector: new[] { 1f, 0f });
            Assert.IsType<CognitiveSearchResult[]>(result);
        }
        finally
        {
            index.Dispose();
            persistence.Dispose();
            if (Directory.Exists(testDataPath))
                Directory.Delete(testDataPath, true);
        }
    }

    [Fact]
    public void SearchMemory_UsePhysics_EmptyResults_ReturnsFlatEmpty()
    {
        var testDataPath = Path.Combine(Path.GetTempPath(), $"physics_test_{Guid.NewGuid():N}");
        var persistence = new PersistenceManager(testDataPath, debounceMs: 50);
        var index = new CognitiveIndex(persistence);
        var physics = new PhysicsEngine();
        var graph = new KnowledgeGraph(persistence, index);
        var clusters = new ClusterManager(index, persistence);
        var spreading = new SpreadingActivationService(index, graph, clusters);
        var tools = new CoreMemoryTools(index, physics, new StubEmbeddingService(), new MetricsCollector(), graph, new QueryExpander(), spreading, clusters);

        try
        {
            // No entries stored — even with usePhysics, returns flat empty list
            var result = tools.SearchMemory(ns: "test", vector: new[] { 1f, 0f }, usePhysics: true);
            Assert.IsAssignableFrom<IReadOnlyList<CognitiveSearchResult>>(result);
        }
        finally
        {
            index.Dispose();
            persistence.Dispose();
            if (Directory.Exists(testDataPath))
                Directory.Delete(testDataPath, true);
        }
    }

    // Helper
    private static CognitiveSearchResult MakeResult(
        string id, float score, int accessCount, string state,
        string? text = null, string? category = null)
    {
        return new CognitiveSearchResult(
            id, text, score, state, 0f, category, null, false, null, accessCount);
    }
}
