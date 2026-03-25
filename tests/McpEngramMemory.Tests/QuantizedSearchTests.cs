using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// Integration tests for the two-stage quantized search pipeline.
/// Validates that Int8 screening + FP32 reranking maintains IR quality.
/// </summary>
public class QuantizedSearchTests
{
    private static CognitiveIndex CreateIndex()
    {
        var persistence = new InMemoryStorageProvider();
        return new CognitiveIndex(persistence);
    }

    [Fact]
    public void Upsert_StmEntry_NotQuantized()
    {
        var index = CreateIndex();
        var entry = new CognitiveEntry("e1", MakeVector(0.5f), "ns", lifecycleState: "stm");
        index.Upsert(entry);

        var results = index.Search(MakeVector(0.5f), "ns", k: 1);
        Assert.Single(results);
        Assert.Equal("e1", results[0].Id);
    }

    [Fact]
    public void Upsert_LtmEntry_SearchReturnsCorrectResults()
    {
        var index = CreateIndex();

        // Insert as LTM (will be quantized)
        index.Upsert(new CognitiveEntry("e1", MakeVector(0.9f), "ns", lifecycleState: "ltm"));
        index.Upsert(new CognitiveEntry("e2", MakeVector(0.1f), "ns", lifecycleState: "ltm"));

        var results = index.Search(MakeVector(0.9f), "ns", k: 2,
            includeStates: new HashSet<string> { "ltm" });

        Assert.Equal(2, results.Count);
        // e1 should be the closest match to query vector 0.9f
        Assert.Equal("e1", results[0].Id);
    }

    [Fact]
    public void MixedStates_SearchReturnsCorrectRanking()
    {
        var index = CreateIndex();

        // Mix of STM and LTM entries
        index.Upsert(new CognitiveEntry("stm1", MakeVector(0.8f), "ns", lifecycleState: "stm"));
        index.Upsert(new CognitiveEntry("ltm1", MakeVector(0.9f), "ns", lifecycleState: "ltm"));
        index.Upsert(new CognitiveEntry("ltm2", MakeVector(0.1f), "ns", lifecycleState: "ltm"));

        var results = index.Search(MakeVector(0.85f), "ns", k: 3);
        Assert.Equal(3, results.Count);

        // Both stm1 (0.8) and ltm1 (0.9) should be top-ranked
        var topIds = results.Take(2).Select(r => r.Id).ToHashSet();
        Assert.Contains("stm1", topIds);
        Assert.Contains("ltm1", topIds);
    }

    [Fact]
    public void LifecycleTransition_QuantizesOnLtm()
    {
        var index = CreateIndex();
        index.Upsert(new CognitiveEntry("e1", MakeVector(0.5f), "ns", lifecycleState: "stm"));

        // Transition to LTM (should trigger quantization)
        index.SetLifecycleState("e1", "ltm");

        // Should still be searchable with correct results
        var results = index.Search(MakeVector(0.5f), "ns", k: 1,
            includeStates: new HashSet<string> { "ltm" });
        Assert.Single(results);
        Assert.Equal("e1", results[0].Id);
    }

    [Fact]
    public void LifecycleTransition_DequantizesOnStm()
    {
        var index = CreateIndex();
        index.Upsert(new CognitiveEntry("e1", MakeVector(0.5f), "ns", lifecycleState: "ltm"));

        // Promote back to STM (should remove quantization)
        index.SetLifecycleState("e1", "stm");

        var results = index.Search(MakeVector(0.5f), "ns", k: 1);
        Assert.Single(results);
        Assert.Equal("e1", results[0].Id);
    }

    [Fact]
    public void LargeNamespace_TwoStageSearch_MaintainsAccuracy()
    {
        var index = CreateIndex();
        var rng = new Random(42);

        // Create 50 entries to trigger two-stage search (threshold = 30)
        var targetVector = MakeRandomVector(384, rng);
        for (int i = 0; i < 50; i++)
        {
            var v = MakeRandomVector(384, rng);
            index.Upsert(new CognitiveEntry($"e{i}", v, "ns", lifecycleState: "ltm"));
        }
        // Add a very similar entry
        var similar = new float[384];
        Array.Copy(targetVector, similar, 384);
        for (int i = 0; i < 384; i++)
            similar[i] += (float)(rng.NextDouble() * 0.01 - 0.005);
        index.Upsert(new CognitiveEntry("target", similar, "ns", lifecycleState: "ltm"));

        var results = index.Search(targetVector, "ns", k: 5,
            includeStates: new HashSet<string> { "ltm" });

        // The very similar entry should be in top results
        Assert.Contains(results, r => r.Id == "target");
        Assert.True(results[0].Score > 0.9f, "Top result should have high similarity");
    }

    [Fact]
    public void FindDuplicates_WorksWithQuantizedEntries()
    {
        var index = CreateIndex();
        var v = MakeVector(0.5f);
        var vDup = (float[])v.Clone();
        for (int i = 0; i < vDup.Length; i++) vDup[i] += 0.001f;

        index.Upsert(new CognitiveEntry("e1", v, "ns", lifecycleState: "ltm"));
        index.Upsert(new CognitiveEntry("e2", vDup, "ns", lifecycleState: "ltm"));
        index.Upsert(new CognitiveEntry("e3", MakeVector(-0.5f), "ns", lifecycleState: "ltm"));

        var dups = index.FindDuplicates("ns", threshold: 0.99f,
            includeStates: new HashSet<string> { "ltm" });

        Assert.Single(dups);
        Assert.Equal("e1", dups[0].IdA);
        Assert.Equal("e2", dups[0].IdB);
    }

    [Fact]
    public void SetActivationEnergyAndState_QuantizesCorrectly()
    {
        var index = CreateIndex();
        index.Upsert(new CognitiveEntry("e1", MakeVector(0.5f), "ns", lifecycleState: "stm"));

        // SetActivationEnergyAndState with state transition to LTM
        index.SetActivationEnergyAndState("e1", -1.0f, "ltm");

        var results = index.Search(MakeVector(0.5f), "ns", k: 1,
            includeStates: new HashSet<string> { "ltm" });
        Assert.Single(results);
        Assert.Equal("e1", results[0].Id);
    }

    // ── Helpers ──

    private static float[] MakeVector(float value, int dim = 384)
    {
        var v = new float[dim];
        for (int i = 0; i < dim; i++)
            v[i] = value + (i * 0.001f);
        return v;
    }

    private static float[] MakeRandomVector(int dim, Random rng)
    {
        var v = new float[dim];
        for (int i = 0; i < dim; i++)
            v[i] = (float)(rng.NextDouble() * 2 - 1);
        return v;
    }
}

/// <summary>
/// Minimal in-memory storage provider for testing.
/// </summary>
file sealed class InMemoryStorageProvider : IStorageProvider
{
    private readonly Dictionary<string, NamespaceData> _data = new();

    public NamespaceData LoadNamespace(string ns)
        => _data.TryGetValue(ns, out var d) ? d : new NamespaceData();

    public void ScheduleSave(string ns, Func<NamespaceData> dataProvider)
        => _data[ns] = dataProvider();

    public void SaveNamespaceSync(string ns, NamespaceData data)
        => _data[ns] = data;

    public IReadOnlyList<string> GetPersistedNamespaces()
        => _data.Keys.ToList();

    public List<GraphEdge> LoadGlobalEdges() => new();
    public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider) { }
    public List<SemanticCluster> LoadClusters() => new();
    public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider) { }
    public List<CollapseRecord> LoadCollapseHistory() => new();
    public void ScheduleSaveCollapseHistory(Func<List<CollapseRecord>> dataProvider) { }
    public Dictionary<string, DecayConfig> LoadDecayConfigs() => new();
    public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider) { }
    public bool SupportsIncrementalWrites => false;
    public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) { }
    public void ScheduleDeleteEntry(string ns, string entryId) { }
    public HnswSnapshot? LoadHnswSnapshot(string ns) => null;
    public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot) { }
    public void DeleteHnswSnapshot(string ns) { }
    public Task DeleteNamespaceAsync(string ns) { _data.Remove(ns); return Task.CompletedTask; }
    public void Flush() { }
    public void Dispose() { }
}
