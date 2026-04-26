using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class SpectralDecayTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly GraphLaplacianSpine _spine;
    private readonly LifecycleEngine _lifecycle;

    public SpectralDecayTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"spectral_decay_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _spine = new GraphLaplacianSpine(_index, _graph);
        _lifecycle = new LifecycleEngine(_index, _persistence, _spine);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    /// <summary>
    /// Spectral decay should diffuse decay debt through the graph: the entry that
    /// would lose the most activation pointwise (because it has high
    /// hours-since-access) instead shares its forgetting pressure with its
    /// graph neighbors. Entries with no neighbors (isolated nodes) are unaffected
    /// since they form their own connected component (the heat kernel preserves
    /// the constant mode within each component).
    ///
    /// Setup: 16-node densely-connected cluster + 16 isolated nodes. Backdate
    /// ONE cluster node so it carries the only nonzero debt. Then:
    ///
    /// - With spectral OFF: only the backdated node loses activation; others stay near zero.
    /// - With spectral ON: the backdated node retains MORE activation (its debt diffused
    ///   away), and its cluster mates lose SOME activation (they absorbed some debt).
    ///   Isolated nodes are unchanged either way.
    /// </summary>
    [Fact]
    public void SpectralDecayDiffusesDebtThroughCluster()
    {
        const string ns = "spectral_test";
        const int clusterSize = 16;
        const int isolatedCount = 16;
        SeedTestGraph(ns, clusterSize, isolatedCount);

        // Backdate just one cluster node so only it has decay debt at cycle time.
        var backdated = _index.Get("c_0")!;
        backdated.LastAccessedAt = DateTimeOffset.UtcNow.AddHours(-100);

        // Snapshot the entry state so we can reset between runs.
        var snapshots = SnapshotEntries(ns);

        // ── Run 1: spectral OFF ─────────────────────────────────────────────────
        _lifecycle.SetDecayConfig(ns, decayRate: 0.1f, useSpectralDecay: false);
        _lifecycle.RunDecayCycle(ns, useStoredConfig: true);
        var pointwiseAE = ReadActivationEnergies(ns);

        // ── Reset entry state ───────────────────────────────────────────────────
        RestoreEntries(snapshots);

        // ── Run 2: spectral ON, alpha=1 (standard heat kernel) ──────────────────
        _lifecycle.SetDecayConfig(ns, useSpectralDecay: true, subdiffusiveExponent: 1.0f);
        _lifecycle.RunDecayCycle(ns, useStoredConfig: true);
        var spectralAE = ReadActivationEnergies(ns);

        // The backdated node should retain more activation under spectral diffusion
        // (its debt got shared with cluster mates).
        Assert.True(spectralAE["c_0"] > pointwiseAE["c_0"] + 0.5f,
            $"Backdated node 'c_0' should retain more AE under spectral; pointwise={pointwiseAE["c_0"]:F2}, spectral={spectralAE["c_0"]:F2}.");

        // At least one cluster mate should have *lost* activation under spectral
        // (they absorbed some of c_0's debt).
        bool anyClusterAffected = false;
        for (int i = 1; i < clusterSize; i++)
        {
            string id = $"c_{i}";
            if (spectralAE[id] < pointwiseAE[id] - 0.1f) { anyClusterAffected = true; break; }
        }
        Assert.True(anyClusterAffected,
            "At least one cluster mate of the backdated node should have absorbed some debt under spectral diffusion.");

        // Isolated nodes (their own connected components) must be unchanged.
        // Tolerance allows for tiny floating-point differences in summing.
        for (int i = 0; i < isolatedCount; i++)
        {
            string id = $"iso_{i}";
            Assert.True(MathF.Abs(spectralAE[id] - pointwiseAE[id]) < 0.01f,
                $"Isolated node '{id}' should be identical under spectral and pointwise; pointwise={pointwiseAE[id]:F2}, spectral={spectralAE[id]:F2}.");
        }
    }

    /// <summary>
    /// When the spine is missing, useSpectralDecay=true must transparently fall
    /// back to pointwise decay rather than throwing. Tests the null-spine path.
    /// </summary>
    [Fact]
    public void SpectralDecayWithoutSpineFallsBackToPointwise()
    {
        const string ns = "no_spine";
        SeedTestGraph(ns, 16, 16);
        _index.Get("c_0")!.LastAccessedAt = DateTimeOffset.UtcNow.AddHours(-100);

        // Construct a fresh LifecycleEngine WITHOUT spine.
        var lifecycleNoSpine = new LifecycleEngine(_index, _persistence, spine: null);
        lifecycleNoSpine.SetDecayConfig(ns, decayRate: 0.1f, useSpectralDecay: true);

        // Should not throw; pointwise debt is applied.
        var result = lifecycleNoSpine.RunDecayCycle(ns, useStoredConfig: true);
        Assert.Equal(32, result.ProcessedCount);
        Assert.True(_index.Get("c_0")!.ActivationEnergy < -10f,
            "Without spine, pointwise debt should land directly on the backdated node.");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private void SeedTestGraph(string ns, int clusterSize, int isolatedCount)
    {
        var rng = new Random(42);
        for (int i = 0; i < clusterSize; i++)
            _index.Upsert(new CognitiveEntry($"c_{i}", new[] { (float)i, 0f }, ns, $"cluster {i}"));
        for (int i = 0; i < isolatedCount; i++)
            _index.Upsert(new CognitiveEntry($"iso_{i}", new[] { 100f + i, 0f }, ns, $"isolated {i}"));

        for (int i = 0; i < clusterSize; i++)
            for (int j = i + 1; j < clusterSize; j++)
                if (rng.NextDouble() < 0.6)
                    _graph.AddEdge(new GraphEdge($"c_{i}", $"c_{j}", "similar_to", 1.0f));
    }

    private List<(string Id, DateTimeOffset LastAccessedAt, int AccessCount, float ActivationEnergy, string LifecycleState)>
        SnapshotEntries(string ns)
    {
        var entries = _index.GetAllInNamespace(ns);
        return entries.Select(e => (e.Id, e.LastAccessedAt, e.AccessCount, e.ActivationEnergy, e.LifecycleState)).ToList();
    }

    private void RestoreEntries(List<(string Id, DateTimeOffset LastAccessedAt, int AccessCount, float ActivationEnergy, string LifecycleState)> snapshots)
    {
        foreach (var (id, lastAccessedAt, accessCount, activationEnergy, state) in snapshots)
        {
            var entry = _index.Get(id)!;
            entry.LastAccessedAt = lastAccessedAt;
            entry.AccessCount = accessCount;
            entry.ActivationEnergy = activationEnergy;
            entry.LifecycleState = state;
        }
    }

    private Dictionary<string, float> ReadActivationEnergies(string ns)
    {
        return _index.GetAllInNamespace(ns).ToDictionary(e => e.Id, e => e.ActivationEnergy);
    }
}
