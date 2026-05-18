using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class CoreMemoryToolsTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly CoreMemoryTools _tools;

    private sealed class StubEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    public CoreMemoryToolsTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"tools_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
        var spreading = new SpreadingActivationService(_index, _graph, _clusters);
        _tools = new CoreMemoryTools(_index, new PhysicsEngine(), new StubEmbeddingService(), new MetricsCollector(), _graph, new QueryExpander(), spreading, _clusters);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    // ── StoreMemory ──

    [Fact]
    public void StoreMemory_ValidInput_StoresAndReturnsMessage()
    {
        var result = _tools.StoreMemory(id: "test1", ns: "work", text: "hello", vector: new[] { 1f, 0f });
        Assert.Contains("test1", result);
        Assert.Contains("2-dim", result);
        Assert.Contains("work", result);
        Assert.Equal(1, _index.Count);
    }

    [Fact]
    public void StoreMemory_WithCategoryAndMetadata()
    {
        var metadata = Meta(("source", "\"test\""));
        var result = _tools.StoreMemory(id: "m1", ns: "work", text: "text", vector: new[] { 1f, 2f }, category: "meeting-notes", metadata: metadata);
        Assert.Contains("m1", result);

        var entry = _index.Get("m1");
        Assert.Equal("meeting-notes", entry!.Category);
        Assert.Equal("test", entry.Metadata["source"]);
    }

    [Fact]
    public void StoreMemory_MetadataWithArrayValue_StringifiesToJson()
    {
        // Regression for engram-feedback-metadata-array-binding-failure-2026-05-18:
        // metadata values that are JSON arrays used to fail MCP binding (the parameter
        // was Dictionary<string,string>). Now the value should be JSON-serialized into
        // the stored metadata bag instead of crashing.
        var metadata = Meta(("artifacts", "[\"a\",\"b\"]"));
        var result = _tools.StoreMemory(id: "arr1", ns: "work", vector: new[] { 1f, 0f }, metadata: metadata);
        Assert.DoesNotContain("Error", result);

        var entry = _index.Get("arr1");
        Assert.Equal("[\"a\",\"b\"]", entry!.Metadata["artifacts"]);
    }

    [Fact]
    public void StoreMemory_MetadataWithNestedObjectValue_StringifiesToJson()
    {
        var metadata = Meta(("config", "{\"depth\":3,\"enabled\":true}"));
        var result = _tools.StoreMemory(id: "obj1", ns: "work", vector: new[] { 1f, 0f }, metadata: metadata);
        Assert.DoesNotContain("Error", result);

        var entry = _index.Get("obj1");
        Assert.Equal("{\"depth\":3,\"enabled\":true}", entry!.Metadata["config"]);
    }

    [Fact]
    public void StoreMemory_MetadataWithMixedScalarTypes()
    {
        var metadata = Meta(
            ("name", "\"alpha\""),
            ("count", "7"),
            ("active", "true"),
            ("nothing", "null"));
        var result = _tools.StoreMemory(id: "mix1", ns: "work", vector: new[] { 1f, 0f }, metadata: metadata);
        Assert.DoesNotContain("Error", result);

        var entry = _index.Get("mix1");
        Assert.Equal("alpha", entry!.Metadata["name"]);
        Assert.Equal("7", entry.Metadata["count"]);
        Assert.Equal("true", entry.Metadata["active"]);
        Assert.Equal(string.Empty, entry.Metadata["nothing"]);
    }

    // ── StoreBatch ──

    [Fact]
    public void StoreBatch_MetadataWithArrayValue_StringifiesToJson()
    {
        var entries = new[]
        {
            new BatchEntry
            {
                Id = "b1",
                Text = "first",
                Metadata = Meta(("tags", "[\"x\",\"y\"]")),
            },
            new BatchEntry
            {
                Id = "b2",
                Text = "second",
                Metadata = Meta(("source", "\"upload\"")),
            },
        };

        var result = _tools.StoreBatch(ns: "batch_ns", entries: entries, checkDuplicates: false);
        Assert.NotNull(result);

        Assert.Equal("[\"x\",\"y\"]", _index.Get("b1")!.Metadata["tags"]);
        Assert.Equal("upload", _index.Get("b2")!.Metadata["source"]);
    }

    private static Dictionary<string, JsonElement> Meta(params (string Key, string JsonValue)[] entries)
    {
        var dict = new Dictionary<string, JsonElement>(entries.Length);
        foreach (var (key, jsonValue) in entries)
            dict[key] = JsonDocument.Parse(jsonValue).RootElement.Clone();
        return dict;
    }

    [Fact]
    public void StoreMemory_EmptyId_ReturnsError()
    {
        var result = _tools.StoreMemory(id: "", ns: "work", vector: new[] { 1f, 0f });
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void StoreMemory_NoVectorNoText_ReturnsError()
    {
        var result = _tools.StoreMemory(id: "test1", ns: "work");
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void StoreMemory_SameId_Replaces()
    {
        _tools.StoreMemory(id: "a", ns: "work", text: "first", vector: new[] { 1f, 0f });
        _tools.StoreMemory(id: "a", ns: "work", text: "second", vector: new[] { 0f, 1f });
        Assert.Equal(1, _index.Count);
    }

    [Fact]
    public void StoreMemory_DefaultsToStm()
    {
        _tools.StoreMemory(id: "a", ns: "work", vector: new[] { 1f, 0f });
        Assert.Equal("stm", _index.Get("a")!.LifecycleState);
    }

    [Fact]
    public void StoreMemory_SpecifyLifecycleState()
    {
        _tools.StoreMemory(id: "a", ns: "work", vector: new[] { 1f, 0f }, lifecycleState: "ltm");
        Assert.Equal("ltm", _index.Get("a")!.LifecycleState);
    }

    [Fact]
    public void StoreMemory_TextOnly_AutoEmbeds()
    {
        var result = _tools.StoreMemory(id: "auto1", ns: "work", text: "auto embed this");
        Assert.Contains("auto1", result);
        Assert.Contains("2-dim", result); // StubEmbeddingService returns 2-dim

        var entry = _index.Get("auto1");
        Assert.NotNull(entry);
        Assert.Equal("auto embed this", entry.Text);
    }

    [Fact]
    public void StoreMemory_VectorTakesPriority_OverAutoEmbed()
    {
        var explicitVector = new[] { 0.9f, 0.1f };
        _tools.StoreMemory(id: "prio", ns: "work", text: "some text", vector: explicitVector);

        var entry = _index.Get("prio");
        Assert.NotNull(entry);
        Assert.Equal(0.9f, entry.Vector[0]);
        Assert.Equal(0.1f, entry.Vector[1]);
    }

    // ── SearchMemory ──

    [Fact]
    public void SearchMemory_ReturnsResults()
    {
        _tools.StoreMemory(id: "a", ns: "work", text: "first", vector: new[] { 1f, 0f });
        _tools.StoreMemory(id: "b", ns: "work", text: "second", vector: new[] { 0f, 1f });

        var results = (IReadOnlyList<CognitiveSearchResult>)_tools.SearchMemory(ns: "work", vector: new[] { 1f, 0f }, k: 1);
        Assert.Single(results);
        Assert.Equal("a", results[0].Id);
    }

    [Fact]
    public void SearchMemory_EmptyIndex_ReturnsEmpty()
    {
        var results = (IReadOnlyList<CognitiveSearchResult>)_tools.SearchMemory(ns: "work", vector: new[] { 1f, 0f });
        Assert.Empty(results);
    }

    [Fact]
    public void SearchMemory_NamespaceScoped()
    {
        _tools.StoreMemory(id: "a", ns: "work", vector: new[] { 1f, 0f });
        _tools.StoreMemory(id: "b", ns: "personal", vector: new[] { 1f, 0f });

        var results = (IReadOnlyList<CognitiveSearchResult>)_tools.SearchMemory(ns: "work", vector: new[] { 1f, 0f });
        Assert.Single(results);
        Assert.Equal("a", results[0].Id);
    }

    [Fact]
    public void SearchMemory_IncrementsAccessCount()
    {
        _tools.StoreMemory(id: "a", ns: "work", vector: new[] { 1f, 0f });
        _tools.SearchMemory(ns: "work", vector: new[] { 1f, 0f });

        var entry = _index.Get("a");
        Assert.Equal(2, entry!.AccessCount); // Starts at 1, incremented by search
    }

    [Fact]
    public void SearchMemory_IncludeStatesFilter()
    {
        _tools.StoreMemory(id: "a", ns: "work", vector: new[] { 1f, 0f }, lifecycleState: "stm");
        _tools.StoreMemory(id: "b", ns: "work", vector: new[] { 1f, 0f }, lifecycleState: "archived");

        var results = (IReadOnlyList<CognitiveSearchResult>)_tools.SearchMemory(ns: "work", vector: new[] { 1f, 0f }, includeStates: "archived");
        Assert.Single(results);
        Assert.Equal("b", results[0].Id);
    }

    [Fact]
    public void SearchMemory_TextOnly_AutoEmbeds()
    {
        // Store with explicit vector
        _tools.StoreMemory(id: "a", ns: "work", vector: new[] { 0.5f, 0.5f });

        // Search with text only — stub returns [0.5, 0.5] which is identical to entry
        var results = (IReadOnlyList<CognitiveSearchResult>)_tools.SearchMemory(ns: "work", text: "search text");
        Assert.Single(results);
        Assert.Equal("a", results[0].Id);
    }

    [Fact]
    public void SearchMemory_NoVectorNoText_ReturnsError()
    {
        var result = _tools.SearchMemory(ns: "work");
        Assert.IsType<string>(result);
        Assert.StartsWith("Error:", (string)result);
    }

    // ── DeleteMemory ──

    [Fact]
    public void DeleteMemory_Existing_ReturnsDeleted()
    {
        _tools.StoreMemory(id: "a", ns: "work", vector: new[] { 1f, 0f });
        var result = _tools.DeleteMemory("a");
        Assert.Contains("Deleted", result);
        Assert.Equal(0, _index.Count);
    }

    [Fact]
    public void DeleteMemory_NonExistent_ReturnsNotFound()
    {
        var result = _tools.DeleteMemory("missing");
        Assert.Contains("not found", result);
    }

    [Fact]
    public void DeleteMemory_CascadeRemovesEdges()
    {
        _tools.StoreMemory(id: "a", ns: "work", vector: new[] { 1f, 0f });
        _tools.StoreMemory(id: "b", ns: "work", vector: new[] { 0f, 1f });
        _graph.AddEdge(new GraphEdge("a", "b", "similar_to"));

        _tools.DeleteMemory("a");
        Assert.Equal(0, _graph.EdgeCount);
    }

    [Fact]
    public void DeleteMemory_CascadeRemovesClusterMemberships()
    {
        _tools.StoreMemory(id: "a", ns: "work", vector: new[] { 1f, 0f });
        _tools.StoreMemory(id: "b", ns: "work", vector: new[] { 0f, 1f });
        _clusters.CreateCluster("c1", "work", new[] { "a", "b" });

        _tools.DeleteMemory("a");
        var cluster = _clusters.GetCluster("c1");
        Assert.Equal(1, cluster!.MemberCount);
    }
}
