using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class MaintenanceToolsTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;

    public MaintenanceToolsTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"maint_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    [Fact]
    public void RebuildEmbeddings_UpdatesVectorsFromText()
    {
        var oldEmbedding = new HashEmbeddingService(dimensions: 4);
        var entry = new CognitiveEntry("e1", oldEmbedding.Embed("hello world"), "test-ns", "hello world");
        _index.Upsert(entry);

        var originalVector = _index.Get("e1")!.Vector.ToArray();

        // Rebuild with a different embedding service (different dimensions = different vectors)
        var newEmbedding = new HashEmbeddingService(dimensions: 8);
        var (updated, skipped) = _index.RebuildEmbeddings("test-ns", newEmbedding);

        Assert.Equal(1, updated);
        Assert.Equal(0, skipped);

        var rebuilt = _index.Get("e1")!;
        Assert.Equal(8, rebuilt.Vector.Length);
        Assert.NotEqual(originalVector.Length, rebuilt.Vector.Length);
    }

    [Fact]
    public void RebuildEmbeddings_PreservesMetadata()
    {
        var embedding = new HashEmbeddingService(dimensions: 4);
        var meta = new Dictionary<string, string> { ["key"] = "value" };
        var entry = new CognitiveEntry("e1", embedding.Embed("test text"), "test-ns", "test text",
            category: "my-cat", metadata: meta, lifecycleState: "ltm");
        entry.AccessCount = 42;
        entry.ActivationEnergy = 0.75f;
        entry.IsSummaryNode = true;
        entry.SourceClusterId = "cluster-1";
        _index.Upsert(entry);

        var originalCreatedAt = _index.Get("e1")!.CreatedAt;

        _index.RebuildEmbeddings("test-ns", new HashEmbeddingService(dimensions: 8));

        var rebuilt = _index.Get("e1")!;
        Assert.Equal("test-ns", rebuilt.Ns);
        Assert.Equal("test text", rebuilt.Text);
        Assert.Equal("my-cat", rebuilt.Category);
        Assert.Equal("ltm", rebuilt.LifecycleState);
        Assert.Equal(42, rebuilt.AccessCount);
        Assert.Equal(0.75f, rebuilt.ActivationEnergy);
        Assert.True(rebuilt.IsSummaryNode);
        Assert.Equal("cluster-1", rebuilt.SourceClusterId);
        Assert.Equal("value", rebuilt.Metadata["key"]);
        Assert.Equal(originalCreatedAt, rebuilt.CreatedAt);
    }

    [Fact]
    public void RebuildEmbeddings_SkipsEntriesWithoutText()
    {
        var embedding = new HashEmbeddingService(dimensions: 4);
        _index.Upsert(new CognitiveEntry("e1", embedding.Embed("has text"), "test-ns", "has text"));
        _index.Upsert(new CognitiveEntry("e2", [1f, 0f, 0f, 0f], "test-ns")); // no text

        var (updated, skipped) = _index.RebuildEmbeddings("test-ns", embedding);

        Assert.Equal(1, updated);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void RebuildEmbeddings_EmptyNamespace_ReturnsZeros()
    {
        var embedding = new HashEmbeddingService(dimensions: 4);
        var (updated, skipped) = _index.RebuildEmbeddings("empty-ns", embedding);

        Assert.Equal(0, updated);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void RebuildEmbeddings_MultipleEntries_AllUpdated()
    {
        var oldEmbed = new HashEmbeddingService(dimensions: 4);
        for (int i = 0; i < 10; i++)
            _index.Upsert(new CognitiveEntry($"e{i}", oldEmbed.Embed($"text {i}"), "bulk-ns", $"text {i}"));

        var newEmbed = new HashEmbeddingService(dimensions: 8);
        var (updated, skipped) = _index.RebuildEmbeddings("bulk-ns", newEmbed);

        Assert.Equal(10, updated);
        Assert.Equal(0, skipped);

        // Verify all entries have new dimensions
        for (int i = 0; i < 10; i++)
            Assert.Equal(8, _index.Get($"e{i}")!.Vector.Length);
    }

    [Fact]
    public void RebuildEmbeddings_SearchWorksAfterRebuild()
    {
        var embedding = new HashEmbeddingService(dimensions: 4);
        _index.Upsert(new CognitiveEntry("e1", embedding.Embed("machine learning"), "test-ns", "machine learning"));
        _index.Upsert(new CognitiveEntry("e2", embedding.Embed("deep learning AI"), "test-ns", "deep learning AI"));

        // Rebuild with same embedding (vectors should be identical, search still works)
        _index.RebuildEmbeddings("test-ns", embedding);

        var results = _index.Search(embedding.Embed("machine learning"), "test-ns", 2, 0f);
        Assert.True(results.Count > 0);
        Assert.Equal("e1", results[0].Id);
    }

    [Fact]
    public void RebuildEmbeddings_OnlyAffectsTargetNamespace()
    {
        var embedding = new HashEmbeddingService(dimensions: 4);
        _index.Upsert(new CognitiveEntry("e1", embedding.Embed("text"), "ns-a", "text"));
        _index.Upsert(new CognitiveEntry("e2", embedding.Embed("text"), "ns-b", "text"));

        var originalVectorB = _index.Get("e2", "ns-b")!.Vector.ToArray();

        var newEmbed = new HashEmbeddingService(dimensions: 8);
        _index.RebuildEmbeddings("ns-a", newEmbed);

        // ns-a should be updated
        Assert.Equal(8, _index.Get("e1")!.Vector.Length);
        // ns-b should be untouched
        Assert.Equal(4, _index.Get("e2", "ns-b")!.Vector.Length);
    }
}
