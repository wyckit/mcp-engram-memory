using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class ConsolidationTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly MemoryDiffusionKernel _diffusion;
    private readonly LifecycleEngine _lifecycle;

    public ConsolidationTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"consolidation_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _diffusion = new MemoryDiffusionKernel(_index, _graph);
        _lifecycle = new LifecycleEngine(_index, _persistence, _diffusion);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    /// <summary>
    /// Sleep consolidation should promote STM entries that have cluster support to
    /// LTM, even if their individual access count is modest. Setup: a tight cluster
    /// of 16 STM entries where collectively the cluster has high activation; plus a
    /// pool of isolated STM entries. After consolidation, cluster STM entries
    /// transition to LTM; isolated STM entries stay STM.
    /// </summary>
    [Fact]
    public void ClusterSupportPromotesStmToLtm()
    {
        const string ns = "promote_test";
        const int clusterSize = 16;
        const int isolatedCount = 16;
        SeedTestGraph(ns, clusterSize, isolatedCount, "stm");

        // Give every cluster member a positive activation so the cluster mean is
        // well above the consolidation promotion threshold of 0.
        for (int i = 0; i < clusterSize; i++)
            _index.Get($"c_{i}")!.ActivationEnergy = 5.0f;

        // Isolated entries stay at activation 0 (the default after upsert + tick),
        // sitting right at the threshold; we want them to stay stm and not get
        // promoted, so set them slightly negative.
        for (int i = 0; i < isolatedCount; i++)
            _index.Get($"iso_{i}")!.ActivationEnergy = -1.0f;

        var result = _lifecycle.RunConsolidationPass(ns);

        Assert.Equal(1, result.ProcessedNamespaces);

        // All 16 cluster members should have been promoted.
        for (int i = 0; i < clusterSize; i++)
            Assert.Equal("ltm", _index.Get($"c_{i}")!.LifecycleState);

        // Isolated members should still be stm: their cluster support is just
        // themselves (each in its own connected component) and their activation
        // is below the promotion threshold.
        for (int i = 0; i < isolatedCount; i++)
            Assert.Equal("stm", _index.Get($"iso_{i}")!.LifecycleState);
    }

    /// <summary>
    /// LTM entries whose cluster has cooled below the consolidation archive
    /// threshold should be archived. Setup: an LTM cluster with collectively
    /// negative activation (cluster has died) — every member archives.
    /// </summary>
    [Fact]
    public void DeadClusterArchivesLtmEntries()
    {
        const string ns = "archive_test";
        const int clusterSize = 32;
        SeedTestGraph(ns, clusterSize, isolatedCount: 0, initialState: "ltm");

        // Drive every cluster member's activation well below the archive floor of -5.
        for (int i = 0; i < clusterSize; i++)
            _index.Get($"c_{i}")!.ActivationEnergy = -10.0f;

        var result = _lifecycle.RunConsolidationPass(ns);

        Assert.Equal(1, result.ProcessedNamespaces);
        Assert.Equal(clusterSize, result.LtmToArchived);
        for (int i = 0; i < clusterSize; i++)
            Assert.Equal("archived", _index.Get($"c_{i}")!.LifecycleState);
    }

    /// <summary>
    /// Topology should rescue: an STM entry whose own activation is low but whose
    /// cluster is hot should still get promoted, because the diffused activation
    /// (cluster mean) exceeds the threshold even though the entry's own value
    /// doesn't.
    /// </summary>
    [Fact]
    public void HotClusterPromotesEvenIndividuallyColdEntries()
    {
        const string ns = "rescue_test";
        const int clusterSize = 32;
        SeedTestGraph(ns, clusterSize, isolatedCount: 0, initialState: "stm");

        // All but one cluster member is very hot; one is cold.
        for (int i = 0; i < clusterSize; i++)
            _index.Get($"c_{i}")!.ActivationEnergy = 10.0f;
        _index.Get("c_0")!.ActivationEnergy = -2.0f;

        var result = _lifecycle.RunConsolidationPass(ns);

        // The cold entry should still have been promoted because the cluster
        // mean drags its smoothed activation above the threshold.
        Assert.Equal("ltm", _index.Get("c_0")!.LifecycleState);
        Assert.Equal(clusterSize, result.StmToLtm);
    }

    /// <summary>
    /// Namespaces that don't qualify for the diffusion kernel (too small or too
    /// sparse) should be skipped entirely. The pass shouldn't fall through to
    /// raw-activation thresholding — that would duplicate the regular decay
    /// cycle's role and produce surprising transitions on tiny namespaces.
    /// </summary>
    [Fact]
    public void SkipsNamespacesBelowKernelThreshold()
    {
        const string ns = "tiny";
        for (int i = 0; i < 10; i++)
            _index.Upsert(new CognitiveEntry($"t_{i}", new[] { (float)i, 0f }, ns, $"entry {i}"));
        _graph.AddEdge(new GraphEdge("t_0", "t_1", "similar_to", 1.0f));

        // Give the entries enough activation that a naive threshold-only check
        // would promote them; this confirms we're properly bypassing.
        foreach (var e in _index.GetAllInNamespace(ns))
            e.ActivationEnergy = 100f;

        var result = _lifecycle.RunConsolidationPass(ns);

        Assert.Equal(0, result.ProcessedNamespaces);
        Assert.Equal(1, result.SkippedNamespaces);
        Assert.Equal(0, result.StmToLtm);
        // Every entry must still be stm.
        foreach (var e in _index.GetAllInNamespace(ns))
            Assert.Equal("stm", e.LifecycleState);
    }

    /// <summary>
    /// When the diffusion kernel is missing (not injected), consolidation must
    /// no-op cleanly rather than throwing — there is no graph to diffuse through.
    /// </summary>
    [Fact]
    public void NoKernelInjectedNoOps()
    {
        var lifecycleNoKernel = new LifecycleEngine(_index, _persistence, diffusion: null);
        SeedTestGraph("no_kernel", clusterSize: 16, isolatedCount: 0, initialState: "stm");
        foreach (var e in _index.GetAllInNamespace("no_kernel"))
            e.ActivationEnergy = 5.0f;

        var result = lifecycleNoKernel.RunConsolidationPass("no_kernel");

        Assert.Equal(0, result.ProcessedNamespaces);
        Assert.Equal(0, result.StmToLtm);
        Assert.Equal(0, result.LtmToArchived);
        foreach (var e in _index.GetAllInNamespace("no_kernel"))
            Assert.Equal("stm", e.LifecycleState);
    }

    /// <summary>
    /// EnableConsolidation=false on a namespace's DecayConfig should opt that
    /// namespace out, even when the kernel and entries qualify.
    /// </summary>
    [Fact]
    public void DisabledViaConfigSkipsNamespace()
    {
        const string ns = "opted_out";
        SeedTestGraph(ns, clusterSize: 16, isolatedCount: 0, initialState: "stm");
        foreach (var e in _index.GetAllInNamespace(ns))
            e.ActivationEnergy = 5.0f;

        _lifecycle.SetDecayConfig(ns, decayRate: 0.1f);
        // Mutate the config directly since SetDecayConfig doesn't surface
        // EnableConsolidation yet — that's a follow-up if we want to expose it.
        _lifecycle.GetDecayConfig(ns)!.EnableConsolidation = false;

        var result = _lifecycle.RunConsolidationPass(ns);
        Assert.Equal(0, result.ProcessedNamespaces);
        Assert.Equal(1, result.SkippedNamespaces);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private void SeedTestGraph(string ns, int clusterSize, int isolatedCount, string initialState)
    {
        var rng = new Random(42);
        for (int i = 0; i < clusterSize; i++)
            _index.Upsert(new CognitiveEntry($"c_{i}", new[] { (float)i, 0f }, ns, $"cluster {i}", lifecycleState: initialState));
        for (int i = 0; i < isolatedCount; i++)
            _index.Upsert(new CognitiveEntry($"iso_{i}", new[] { 100f + i, 0f }, ns, $"isolated {i}", lifecycleState: initialState));

        for (int i = 0; i < clusterSize; i++)
            for (int j = i + 1; j < clusterSize; j++)
                if (rng.NextDouble() < 0.6)
                    _graph.AddEdge(new GraphEdge($"c_{i}", $"c_{j}", "similar_to", 1.0f));
    }
}
