using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// Regression coverage for every form of state retained by the diffusion singleton. A namespace
/// may leave behind a basis, a negative-cached failure, or only its compute lock; the bounded
/// rotation must make all three retractable.
/// </summary>
public sealed class MemoryDiffusionRetractionTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;

    public MemoryDiffusionRetractionTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"diffusion_retraction_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    [Fact]
    public void ABypassedNamespaceDoesNotLeaveItsComputeLockBehind()
    {
        var kernel = new MemoryDiffusionKernel(_index, _graph);
        _index.Upsert(new CognitiveEntry("tiny-0", [1f, 0f], "tiny", "tiny"));

        Assert.Null(kernel.GetBasis("tiny", tenantId: ""));
        Assert.Equal(0, kernel.CachedPartitionCount);
        Assert.Equal(0, kernel.FailedPartitionCount);
        Assert.Equal(1, kernel.ComputeLockPartitionCount);
        Assert.Equal(1, kernel.RetractablePartitionCount);

        // One unrelated request advances the one-partition rotation and replaces tiny's
        // lock-only state with probe's. Invalidating probe then leaves no retained state at all.
        Assert.Null(kernel.GetBasis("probe", tenantId: ""));
        kernel.Invalidate("probe", tenantId: "");

        AssertNoRetainedState(kernel);
    }

    [Fact]
    public void AFailureOnlyPartitionIsRetractedAfterItsNamespaceDisappears()
    {
        const string ns = "failed";
        SeedEntries(ns, MemoryDiffusionKernel.MinimumNodesForSpectral);
        var kernel = new ThrowingKernel(_index, _graph, ns);

        Assert.Throws<InvalidOperationException>(() => kernel.GetBasis(ns, tenantId: ""));
        Assert.Equal(0, kernel.CachedPartitionCount);
        Assert.Equal(1, kernel.FailedPartitionCount);
        Assert.Equal(1, kernel.ComputeLockPartitionCount);
        Assert.Equal(1, kernel.RetractablePartitionCount);

        Assert.True(_index.DeleteAllInNamespace(ns, tenantId: "") > 0);
        Assert.Null(kernel.GetBasis("probe", tenantId: ""));
        kernel.Invalidate("probe", tenantId: "");

        AssertNoRetainedState(kernel);
    }

    [Fact]
    public async Task AnInFlightComputationRepublishesItsCleanupMarkerAfterRetraction()
    {
        const string ns = "in-flight";
        using var kernel = new BlockingKernel(_index, _graph, ns);

        var computation = Task.Run(() => kernel.GetBasis(ns, tenantId: ""));
        Assert.True(kernel.Entered.Wait(TimeSpan.FromSeconds(10)),
            "The controlled computation never reached its blocking seam.");

        try
        {
            Assert.Equal(1, kernel.ComputeLockPartitionCount);
            Assert.Equal(1, kernel.RetractablePartitionCount);

            // The target has no store entries, so this unrelated request retracts its lock while
            // ComputeBasis is still running. The producer must re-register after it publishes.
            Assert.Null(kernel.GetBasis("probe", tenantId: ""));
            kernel.Invalidate("probe", tenantId: "");
        }
        finally
        {
            kernel.Release.Set();
        }

        Assert.NotNull(await computation.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, kernel.CachedPartitionCount);
        Assert.Equal(1, kernel.RetractablePartitionCount);

        // The re-published marker makes the late basis visible to the next bounded rotation.
        Assert.Null(kernel.GetBasis("cleanup", tenantId: ""));
        kernel.Invalidate("cleanup", tenantId: "");
        AssertNoRetainedState(kernel);
    }

    private void SeedEntries(string ns, int count)
    {
        for (int i = 0; i < count; i++)
            _index.Upsert(new CognitiveEntry($"{ns}-{i}", [i + 1f, 1f], ns, $"entry {i}"));
    }

    private static void AssertNoRetainedState(MemoryDiffusionKernel kernel)
    {
        Assert.Equal(0, kernel.CachedPartitionCount);
        Assert.Equal(0, kernel.FailedPartitionCount);
        Assert.Equal(0, kernel.ComputeLockPartitionCount);
        Assert.Equal(0, kernel.RetractablePartitionCount);
    }

    private sealed class ThrowingKernel : MemoryDiffusionKernel
    {
        private readonly string _failingNamespace;

        public ThrowingKernel(CognitiveIndex index, KnowledgeGraph graph, string failingNamespace)
            : base(index, graph)
        {
            _failingNamespace = failingNamespace;
        }

        protected override DiffusionBasis? ComputeBasis(
            string ns, int topK, long graphRevision, string tenantId)
        {
            if (ns == _failingNamespace)
                throw new InvalidOperationException("controlled eigensolver failure");

            return base.ComputeBasis(ns, topK, graphRevision, tenantId);
        }
    }

    private sealed class BlockingKernel : MemoryDiffusionKernel, IDisposable
    {
        private readonly string _blockingNamespace;

        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);

        public BlockingKernel(CognitiveIndex index, KnowledgeGraph graph, string blockingNamespace)
            : base(index, graph)
        {
            _blockingNamespace = blockingNamespace;
        }

        protected override DiffusionBasis? ComputeBasis(
            string ns, int topK, long graphRevision, string tenantId)
        {
            if (ns != _blockingNamespace)
                return base.ComputeBasis(ns, topK, graphRevision, tenantId);

            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Controlled diffusion computation was not released.");

            return new DiffusionBasis(
                ns,
                ["synthetic"],
                [0f],
                new float[,] { { 1f } },
                edgeCount: 1,
                graphRevision);
        }

        public void Dispose()
        {
            Release.Set();
            Entered.Dispose();
            Release.Dispose();
        }
    }
}
