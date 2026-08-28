using System.Text.Json;
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
using McpEngramMemory.Core.Services.Sharing;
using ModelContextProtocol;

namespace McpEngramMemory.Tests;

/// <summary>
/// Regression coverage for the reflect/relatedIds binding bug: supplying relatedIds
/// as anything other than a JSON array of strings failed MCP model binding before the
/// tool body ran, so the caller only ever saw "An error occurred invoking 'reflect'".
///
/// The binding tests deserialize each wire shape into the parameter type declared on
/// <see cref="CompositeTools.Reflect"/> using the MCP SDK's own serializer options —
/// the exact marshalling step that threw. Declaring relatedIds as anything narrower
/// than a tolerant type fails these outright.
/// </summary>
public class ReflectRelatedIdsBindingTests : IDisposable
{
    private readonly string _dataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly HashEmbeddingService _embedding;
    private readonly CompositeTools _tools;

    public ReflectRelatedIdsBindingTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), $"relatedids_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_dataPath);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _embedding = new HashEmbeddingService();
        var lifecycle = new LifecycleEngine(_index, _persistence);
        var dispatcher = new ExpertDispatcher(_index, _embedding);
        var metrics = new MetricsCollector();
        var spectral = new SpectralRetrievalReranker(new MemoryDiffusionKernel(_index, _graph));
        _tools = new CompositeTools(_index, _embedding, _graph, lifecycle, dispatcher, metrics, spectral, new NamespaceRegistry(_index, _embedding), AgentIdentity.Default);
    }

    /// <summary>The JSON the MCP client puts on the wire for relatedIds, one row per shape.</summary>
    public static TheoryData<string> AcceptedWireShapes() => new()
    {
        """["seed-a"]""",              // JSON array, single id
        """["seed-a","seed-b"]""",     // JSON array, several ids
        "\"seed-a\"",                  // bare string, single id
        "\"seed-a,seed-b\"",           // bare string, comma-separated
        "\"seed-a, seed-b\"",          // bare string, comma-separated with spaces
        "null",                        // explicit null
        "[]",                          // empty array
    };

    // ── binding layer ──

    [Theory]
    [MemberData(nameof(AcceptedWireShapes))]
    public void RelatedIds_BindsFromEveryAcceptedWireShape(string json)
    {
        var parameterType = RelatedIdsParameterType();

        // Throws JsonException if the declared parameter type can't represent this shape —
        // which is exactly how the original bug surfaced as a generic invocation error.
        var bound = JsonSerializer.Deserialize(json, parameterType, McpJsonUtilities.DefaultOptions);

        Assert.True(bound is not null || json == "null");
    }

    [Fact]
    public void RelatedIds_DeclaredTypeAcceptsArbitraryJson()
    {
        // The guard that keeps the fix in place: a narrower parameter type (string[], List<string>)
        // would throw here, pushing the failure back before the tool body and back to the
        // generic "An error occurred invoking 'reflect'" message.
        var parameterType = RelatedIdsParameterType();

        var ex = Record.Exception(() =>
            JsonSerializer.Deserialize("""{"not":"a list"}""", parameterType, McpJsonUtilities.DefaultOptions));

        Assert.Null(ex);
    }

    private static Type RelatedIdsParameterType() =>
        typeof(CompositeTools)
            .GetMethod(nameof(CompositeTools.Reflect))!
            .GetParameters()
            .Single(p => p.Name == "relatedIds")
            .ParameterType;

    // ── end-to-end through the tool body ──

    [Theory]
    [InlineData("""["seed-a"]""")]
    [InlineData("\"seed-a\"")]
    [InlineData("""["seed-a","seed-b"]""")]
    [InlineData("\"seed-a,seed-b\"")]
    public void Reflect_LinksRelatedIds_ForEveryAcceptedShape(string json)
    {
        SeedMemory("seed-a");
        SeedMemory("seed-b");

        var result = InvokeReflect(json, topic: "shape-test") as ReflectResult;

        Assert.NotNull(result);
        Assert.Equal("stored", result!.Status);
        Assert.Contains(result.Actions, a => a.Contains("seed-a"));
        Assert.Contains(_graph.GetEdgesForEntry(result.Id, tenantId: ""),
            e => e.TargetId == "seed-a" && e.Relation == "elaborates");
    }

    [Theory]
    [InlineData("""null""")]
    [InlineData("""[]""")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void Reflect_TreatsEmptyRelatedIdsAsAbsent(string json)
    {
        var result = InvokeReflect(json, topic: "empty-test") as ReflectResult;

        Assert.NotNull(result);
        Assert.Equal("stored", result!.Status);
        Assert.DoesNotContain(result.Actions, a => a.StartsWith("linked to "));
    }

    [Theory]
    [InlineData("""{"id":"seed-a"}""")]
    [InlineData("""[["seed-a"]]""")]
    [InlineData("""[{"id":"seed-a"}]""")]
    public void Reflect_RejectsUnusableShapes_NamingParameterAndExpectedShape(string json)
    {
        var result = InvokeReflect(json, topic: "reject-test");

        var message = Assert.IsType<string>(result);
        Assert.StartsWith("Error: ", message);
        Assert.Contains("relatedIds", message);
        Assert.Contains("comma-separated string", message);
    }

    [Fact]
    public void Reflect_UnknownRelatedId_IsSkippedWithoutFailing()
    {
        var result = InvokeReflect("""["does-not-exist"]""", topic: "unknown-test") as ReflectResult;

        Assert.NotNull(result);
        Assert.Equal("stored", result!.Status);
        Assert.DoesNotContain(result.Actions, a => a.Contains("does-not-exist"));
    }

    private object InvokeReflect(string relatedIdsJson, string topic) =>
        _tools.Reflect(
            $"A reflection exercising the {topic} relatedIds path.",
            "bindingns",
            topic,
            relatedIds: JsonDocument.Parse(relatedIdsJson).RootElement);

    private void SeedMemory(string id) =>
        _index.Upsert(new CognitiveEntry(
            id, _embedding.Embed($"seed entry {id}"), "bindingns", $"seed entry {id}",
            lifecycleState: "ltm"));

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, recursive: true);
        GC.SuppressFinalize(this);
    }
}
