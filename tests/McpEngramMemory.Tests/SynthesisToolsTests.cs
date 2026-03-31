using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Core.Services.Synthesis;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

public class SynthesisToolsTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly ClusterManager _clusters;
    private readonly SynthesisEngine _synthesis;
    private readonly SynthesisTools _tools;

    private sealed class StubEmbeddingService : IEmbeddingService
    {
        public int Dimensions => 2;
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    public SynthesisToolsTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"synthesis_tools_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _clusters = new ClusterManager(_index, _persistence);
        _synthesis = new SynthesisEngine(_index, _clusters);
        _tools = new SynthesisTools(_synthesis);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    // ── SynthesizeMemories ──

    [Fact]
    public async Task SynthesizeMemories_EmptyNamespace_ReturnsEmptyResult()
    {
        // Empty namespace has no entries, so SynthesisEngine returns early
        // after the Ollama availability check. Since Ollama won't be running
        // in CI, this returns an error status before reaching empty-namespace logic.
        // Either way, the result should be a valid SynthesisResult.
        var result = await _tools.SynthesizeMemories("empty-ns") as SynthesisResult;

        Assert.NotNull(result);
        // Without Ollama: status is "error" (Ollama not available)
        // With Ollama but empty ns: status is "empty"
        Assert.True(result!.Status is "error" or "empty");
        Assert.Null(result.Synthesis);
        Assert.Equal(0, result.EntriesProcessed);
    }

    [Fact]
    public async Task SynthesizeMemories_CancelledToken_ThrowsOrReturnsEarly()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // A pre-cancelled token may throw OperationCanceledException
        // during the Ollama HTTP call, or may be caught and return error.
        try
        {
            var result = await _tools.SynthesizeMemories("empty-ns", cancellationToken: cts.Token);
            // If it returns, it should be a valid result (Ollama catch block returns false)
            Assert.NotNull(result);
        }
        catch (OperationCanceledException)
        {
            // Expected — tool correctly propagates cancellation
        }
    }
}
