using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class IntelligenceTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ClusterManager _clusters;
    private readonly LifecycleEngine _lifecycle;
    private readonly AccretionScanner _scanner;

    public IntelligenceTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"intel_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _clusters = new ClusterManager(_index, _persistence);
        _lifecycle = new LifecycleEngine(_index);
        _scanner = new AccretionScanner(_index);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    // ── Duplicate Detection: CognitiveIndex.FindDuplicates ──

    [Fact]
    public void FindDuplicates_EmptyNamespace_ReturnsEmpty()
    {
        var dups = _index.FindDuplicates("empty", 0.95f);
        Assert.Empty(dups);
    }

    [Fact]
    public void FindDuplicates_IdenticalVectors_Detected()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", "hello"));
        _index.Upsert(new CognitiveEntry("b", new[] { 1f, 0f, 0f }, "test", "hello again"));

        var dups = _index.FindDuplicates("test", 0.95f);
        Assert.Single(dups);
        var ids = new HashSet<string> { dups[0].IdA, dups[0].IdB };
        Assert.Contains("a", ids);
        Assert.Contains("b", ids);
        Assert.Equal(1f, dups[0].Similarity, 3);
    }

    [Fact]
    public void FindDuplicates_DissimilarVectors_NotDetected()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", "hello"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f, 0f }, "test", "world"));

        var dups = _index.FindDuplicates("test", 0.95f);
        Assert.Empty(dups);
    }

    [Fact]
    public void FindDuplicates_NearlyIdentical_DetectedAboveThreshold()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0f, 1f, 0f }, "test")); // different

        var dups = _index.FindDuplicates("test", 0.99f);
        Assert.Single(dups);
        // Order of IdA/IdB depends on internal sorting; check either direction
        Assert.Contains(dups, d =>
            (d.IdA == "a" && d.IdB == "b") || (d.IdA == "b" && d.IdB == "a"));
    }

    [Fact]
    public void FindDuplicates_CategoryFilter_OnlyChecksMatchingCategory()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", category: "cat1"));
        _index.Upsert(new CognitiveEntry("b", new[] { 1f, 0f, 0f }, "test", category: "cat2")); // same vector, different category

        var dups = _index.FindDuplicates("test", 0.95f, category: "cat1");
        Assert.Empty(dups); // Only 1 entry matches cat1, so no pairs

        var dupsAll = _index.FindDuplicates("test", 0.95f);
        Assert.Single(dupsAll); // Without category filter, they're duplicates
    }

    [Fact]
    public void FindDuplicates_LifecycleFilter_ExcludesArchived()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "stm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 1f, 0f, 0f }, "test", lifecycleState: "archived"));

        var dups = _index.FindDuplicates("test", 0.95f); // default stm,ltm
        Assert.Empty(dups); // 'b' is archived, excluded

        var dupsAll = _index.FindDuplicates("test", 0.95f, includeStates: new HashSet<string> { "stm", "ltm", "archived" });
        Assert.Single(dupsAll);
    }

    [Fact]
    public void FindDuplicates_SortedByDescendingSimilarity()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test"));
        _index.Upsert(new CognitiveEntry("c", new[] { 1f, 0f, 0f }, "test")); // identical to a

        var dups = _index.FindDuplicates("test", 0.9f);
        Assert.Equal(3, dups.Count); // a-b, a-c, b-c
        // First pair should be the highest similarity (1.0 for identical vectors)
        Assert.True(dups[0].Similarity >= dups[1].Similarity);
        Assert.True(dups[1].Similarity >= dups[2].Similarity);
    }

    [Fact]
    public void FindDuplicates_InvalidThreshold_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _index.FindDuplicates("test", 1.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => _index.FindDuplicates("test", -0.1f));
    }

    [Fact]
    public void FindDuplicates_CrossNamespace_Isolated()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "ns1"));
        _index.Upsert(new CognitiveEntry("b", new[] { 1f, 0f, 0f }, "ns2")); // same vector, different namespace

        Assert.Empty(_index.FindDuplicates("ns1", 0.95f));
        Assert.Empty(_index.FindDuplicates("ns2", 0.95f));
    }

    // ── Contradiction Surfacing: KnowledgeGraph.GetContradictions ──

    [Fact]
    public void GetContradictions_NoEdges_ReturnsEmpty()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", "statement A"));
        var contradictions = _graph.GetContradictions("test");
        Assert.Empty(contradictions);
    }

    [Fact]
    public void GetContradictions_ContradictEdge_Returned()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", "cats are better"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f }, "test", "dogs are better"));

        _graph.AddEdge(new GraphEdge("a", "b", "contradicts", 0.9f));

        var contradictions = _graph.GetContradictions("test");
        Assert.Single(contradictions);
        Assert.Equal("a", contradictions[0].Edge.SourceId);
        Assert.Equal("b", contradictions[0].Edge.TargetId);
    }

    [Fact]
    public void GetContradictions_NonContradictEdge_Excluded()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f }, "test"));

        _graph.AddEdge(new GraphEdge("a", "b", "similar_to", 0.9f));

        var contradictions = _graph.GetContradictions("test");
        Assert.Empty(contradictions);
    }

    [Fact]
    public void GetContradictions_FilteredByNamespace()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "ns1"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f }, "ns2"));

        _graph.AddEdge(new GraphEdge("a", "b", "contradicts"));

        var ns1 = _graph.GetContradictions("ns1");
        var ns2 = _graph.GetContradictions("ns2");
        Assert.Single(ns1); // source is in ns1
        Assert.Single(ns2); // target is in ns2

        var ns3 = _graph.GetContradictions("ns3");
        Assert.Empty(ns3);
    }

    // ── Reversible Collapse ──

    [Fact]
    public void UndoCollapse_NoRecord_ReturnsError()
    {
        var result = _scanner.UndoCollapse("nonexistent", _lifecycle, _clusters);
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void ExecuteCollapse_RecordsHistory()
    {
        // Set up 3 LTM entries that form a tight cluster
        _index.Upsert(new CognitiveEntry("m1", new[] { 1f, 0f, 0f }, "test", "memory one", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("m2", new[] { 0.99f, 0.01f, 0f }, "test", "memory two", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("m3", new[] { 0.98f, 0.02f, 0f }, "test", "memory three", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("m4", new[] { 0.97f, 0.03f, 0f }, "test", "memory four", lifecycleState: "ltm"));

        // Trigger scan to create pending collapse
        var scan = _scanner.ScanNamespace("test", epsilon: 0.15f, minPoints: 3);
        Assert.True(scan.NewCollapses.Count > 0);

        var collapseId = scan.NewCollapses[0].CollapseId;

        // Execute the collapse
        var summaryVector = new[] { 0.99f, 0.01f, 0f };
        var result = _scanner.ExecuteCollapse(collapseId, "Combined summary", summaryVector, _clusters, _lifecycle);
        Assert.DoesNotContain("Error:", result);

        // Verify history was recorded
        var history = _scanner.GetCollapseHistory("test");
        Assert.Single(history);
        Assert.Equal(collapseId, history[0].CollapseId);
        Assert.Contains("m1", history[0].MemberIds);
        Assert.Contains("m2", history[0].MemberIds);
        Assert.Contains("m3", history[0].MemberIds);
        Assert.Contains("m4", history[0].MemberIds);
        // All members were LTM before collapse
        Assert.All(history[0].PreviousStates.Values, state => Assert.Equal("ltm", state));
    }

    [Fact]
    public void UndoCollapse_RestoresMembers()
    {
        // Set up and execute a collapse
        _index.Upsert(new CognitiveEntry("m1", new[] { 1f, 0f, 0f }, "test", "memory one", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("m2", new[] { 0.99f, 0.01f, 0f }, "test", "memory two", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("m3", new[] { 0.98f, 0.02f, 0f }, "test", "memory three", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("m4", new[] { 0.97f, 0.03f, 0f }, "test", "memory four", lifecycleState: "ltm"));

        var scan = _scanner.ScanNamespace("test", epsilon: 0.15f, minPoints: 3);
        var collapseId = scan.NewCollapses[0].CollapseId;
        _scanner.ExecuteCollapse(collapseId, "Combined summary", new[] { 0.99f, 0.01f, 0f }, _clusters, _lifecycle);

        // Verify members are archived after collapse
        Assert.Equal("archived", _index.Get("m1")!.LifecycleState);
        Assert.Equal("archived", _index.Get("m2")!.LifecycleState);
        Assert.Equal("archived", _index.Get("m3")!.LifecycleState);
        Assert.Equal("archived", _index.Get("m4")!.LifecycleState);

        // Now undo the collapse
        var undoResult = _scanner.UndoCollapse(collapseId, _lifecycle, _clusters);
        Assert.DoesNotContain("Error:", undoResult);

        // Verify members are restored to LTM
        Assert.Equal("ltm", _index.Get("m1")!.LifecycleState);
        Assert.Equal("ltm", _index.Get("m2")!.LifecycleState);
        Assert.Equal("ltm", _index.Get("m3")!.LifecycleState);
        Assert.Equal("ltm", _index.Get("m4")!.LifecycleState);
    }

    [Fact]
    public void UndoCollapse_DeletesSummaryEntry()
    {
        _index.Upsert(new CognitiveEntry("m1", new[] { 1f, 0f, 0f }, "test", "mem1", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("m2", new[] { 0.99f, 0.01f, 0f }, "test", "mem2", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("m3", new[] { 0.98f, 0.02f, 0f }, "test", "mem3", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("m4", new[] { 0.97f, 0.03f, 0f }, "test", "mem4", lifecycleState: "ltm"));

        var scan = _scanner.ScanNamespace("test", epsilon: 0.15f, minPoints: 3);
        var collapseId = scan.NewCollapses[0].CollapseId;
        _scanner.ExecuteCollapse(collapseId, "Summary text", new[] { 0.99f, 0.01f, 0f }, _clusters, _lifecycle);

        // Get summary entry ID from history
        var record = _scanner.GetCollapseHistory("test")[0];
        Assert.NotNull(_index.Get(record.SummaryEntryId));

        // Undo
        _scanner.UndoCollapse(collapseId, _lifecycle, _clusters);

        // Summary entry should be gone
        Assert.Null(_index.Get(record.SummaryEntryId));
    }

    [Fact]
    public void UndoCollapse_RemovesHistoryRecord()
    {
        _index.Upsert(new CognitiveEntry("m1", new[] { 1f, 0f, 0f }, "test", "mem1", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("m2", new[] { 0.99f, 0.01f, 0f }, "test", "mem2", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("m3", new[] { 0.98f, 0.02f, 0f }, "test", "mem3", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("m4", new[] { 0.97f, 0.03f, 0f }, "test", "mem4", lifecycleState: "ltm"));

        var scan = _scanner.ScanNamespace("test", epsilon: 0.15f, minPoints: 3);
        var collapseId = scan.NewCollapses[0].CollapseId;
        _scanner.ExecuteCollapse(collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, _lifecycle);

        Assert.Single(_scanner.GetCollapseHistory("test"));

        _scanner.UndoCollapse(collapseId, _lifecycle, _clusters);

        Assert.Empty(_scanner.GetCollapseHistory("test"));
    }

    [Fact]
    public void UndoCollapse_SameIdTwice_SecondReturnsError()
    {
        _index.Upsert(new CognitiveEntry("m1", new[] { 1f, 0f, 0f }, "test", "mem1", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("m2", new[] { 0.99f, 0.01f, 0f }, "test", "mem2", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("m3", new[] { 0.98f, 0.02f, 0f }, "test", "mem3", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("m4", new[] { 0.97f, 0.03f, 0f }, "test", "mem4", lifecycleState: "ltm"));

        var scan = _scanner.ScanNamespace("test", epsilon: 0.15f, minPoints: 3);
        var collapseId = scan.NewCollapses[0].CollapseId;
        _scanner.ExecuteCollapse(collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, _lifecycle);

        var first = _scanner.UndoCollapse(collapseId, _lifecycle, _clusters);
        Assert.DoesNotContain("Error:", first);

        var second = _scanner.UndoCollapse(collapseId, _lifecycle, _clusters);
        Assert.StartsWith("Error:", second);
    }

    [Fact]
    public void GetCollapseHistory_EmptyNamespace_ReturnsEmpty()
    {
        var history = _scanner.GetCollapseHistory("nonexistent");
        Assert.Empty(history);
    }

    [Fact]
    public void GetCollapseHistory_FiltersByNamespace()
    {
        // Set up entries in two namespaces
        _index.Upsert(new CognitiveEntry("a1", new[] { 1f, 0f, 0f }, "ns1", "a1", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("a2", new[] { 0.99f, 0.01f, 0f }, "ns1", "a2", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("a3", new[] { 0.98f, 0.02f, 0f }, "ns1", "a3", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("a4", new[] { 0.97f, 0.03f, 0f }, "ns1", "a4", lifecycleState: "ltm"));

        var scan = _scanner.ScanNamespace("ns1", epsilon: 0.15f, minPoints: 3);
        Assert.True(scan.NewCollapses.Count > 0);
        _scanner.ExecuteCollapse(scan.NewCollapses[0].CollapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, _lifecycle);

        Assert.Single(_scanner.GetCollapseHistory("ns1"));
        Assert.Empty(_scanner.GetCollapseHistory("ns2"));
    }

    // ── Collapse records previous states correctly for mixed states ──

    [Fact]
    public void ExecuteCollapse_MixedStates_RecordsPreviousStatesCorrectly()
    {
        // STM entry that somehow ended up in a dense cluster (edge case)
        _index.Upsert(new CognitiveEntry("m1", new[] { 1f, 0f, 0f }, "test", "mem1", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("m2", new[] { 0.99f, 0.01f, 0f }, "test", "mem2", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("m3", new[] { 0.98f, 0.02f, 0f }, "test", "mem3", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("m4", new[] { 0.97f, 0.03f, 0f }, "test", "mem4", lifecycleState: "ltm"));

        // Manually promote one to STM after scan detects it
        var scan = _scanner.ScanNamespace("test", epsilon: 0.15f, minPoints: 3);
        _lifecycle.PromoteMemory("m1", "stm"); // Change m1 back to STM

        var collapseId = scan.NewCollapses[0].CollapseId;
        _scanner.ExecuteCollapse(collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, _lifecycle);

        var record = _scanner.GetCollapseHistory("test")[0];
        Assert.Equal("stm", record.PreviousStates["m1"]);
        Assert.Equal("ltm", record.PreviousStates["m2"]);
        Assert.Equal("ltm", record.PreviousStates["m3"]);
        Assert.Equal("ltm", record.PreviousStates["m4"]);

        // Undo should restore mixed states
        _scanner.UndoCollapse(collapseId, _lifecycle, _clusters);
        Assert.Equal("stm", _index.Get("m1")!.LifecycleState);
        Assert.Equal("ltm", _index.Get("m2")!.LifecycleState);
        Assert.Equal("ltm", _index.Get("m3")!.LifecycleState);
        Assert.Equal("ltm", _index.Get("m4")!.LifecycleState);
    }

    // ── Feature 1: Decay Tuning ──

    [Fact]
    public void SetDecayConfig_StoresAndRetrieves()
    {
        var config = _lifecycle.SetDecayConfig("test", decayRate: 0.5f, stmThreshold: 5.0f);
        Assert.Equal(0.5f, config.DecayRate);
        Assert.Equal(5.0f, config.StmThreshold);
        Assert.Equal(1.0f, config.ReinforcementWeight); // default
        Assert.Equal(-5.0f, config.ArchiveThreshold); // default

        var retrieved = _lifecycle.GetDecayConfig("test");
        Assert.NotNull(retrieved);
        Assert.Equal(0.5f, retrieved!.DecayRate);
    }

    [Fact]
    public void SetDecayConfig_PartialUpdate_PreservesOtherFields()
    {
        _lifecycle.SetDecayConfig("test", decayRate: 0.5f, reinforcementWeight: 2.0f);
        _lifecycle.SetDecayConfig("test", stmThreshold: 10.0f); // Only update threshold

        var config = _lifecycle.GetDecayConfig("test");
        Assert.NotNull(config);
        Assert.Equal(0.5f, config!.DecayRate); // preserved
        Assert.Equal(2.0f, config.ReinforcementWeight); // preserved
        Assert.Equal(10.0f, config.StmThreshold); // updated
    }

    [Fact]
    public void GetDecayConfig_Unconfigured_ReturnsNull()
    {
        Assert.Null(_lifecycle.GetDecayConfig("nonexistent"));
    }

    [Fact]
    public void GetAllDecayConfigs_ReturnsAll()
    {
        _lifecycle.SetDecayConfig("ns1", decayRate: 0.1f);
        _lifecycle.SetDecayConfig("ns2", decayRate: 0.2f);

        var all = _lifecycle.GetAllDecayConfigs();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void RunDecayCycle_UseStoredConfig_AppliesStoredParameters()
    {
        // Configure a very aggressive decay that will archive immediately
        _lifecycle.SetDecayConfig("test", decayRate: 1000f, archiveThreshold: 10000f, stmThreshold: 10000f);

        _index.Upsert(new CognitiveEntry("x", new[] { 1f, 0f }, "test", lifecycleState: "stm"));

        // Run with useStoredConfig — should use the aggressive decay
        var result = _lifecycle.RunDecayCycle("test", useStoredConfig: true);
        Assert.Equal(1, result.StmToLtm); // STM should demote (activation energy way below 10000 threshold)
    }

    [Fact]
    public void RunDecayCycle_NoStoredConfig_UsesMethodParams()
    {
        // No config stored for "test" — should use method params
        _index.Upsert(new CognitiveEntry("x", new[] { 1f, 0f }, "test", lifecycleState: "stm"));

        // Very permissive threshold — should NOT demote
        var result = _lifecycle.RunDecayCycle("test", stmThreshold: -9999f, useStoredConfig: true);
        Assert.Equal(0, result.StmToLtm);
    }

    // ── Feature 2: Collapse History Persistence ──

    [Fact]
    public void CollapseHistory_PersistsToDisk()
    {
        // Use a scanner with persistence
        var scannerWithPersistence = new AccretionScanner(_index, _persistence);

        _index.Upsert(new CognitiveEntry("p1", new[] { 1f, 0f, 0f }, "test", "p1", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("p2", new[] { 0.99f, 0.01f, 0f }, "test", "p2", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("p3", new[] { 0.98f, 0.02f, 0f }, "test", "p3", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("p4", new[] { 0.97f, 0.03f, 0f }, "test", "p4", lifecycleState: "ltm"));

        var scan = scannerWithPersistence.ScanNamespace("test", epsilon: 0.15f, minPoints: 3);
        var collapseId = scan.NewCollapses[0].CollapseId;
        scannerWithPersistence.ExecuteCollapse(collapseId, "Summary", new[] { 0.99f, 0.01f, 0f }, _clusters, _lifecycle);

        // Flush to disk
        _persistence.Flush();

        // Load in a new scanner instance
        var scanner2 = new AccretionScanner(_index, _persistence);
        var history = scanner2.GetCollapseHistory("test");
        Assert.Single(history);
        Assert.Equal(collapseId, history[0].CollapseId);
    }

    // ── Feature 3: Duplicate-on-Ingest (tested via FindDuplicates maxResults cap) ──

    [Fact]
    public void FindDuplicates_MaxResultsCapped()
    {
        // Create 5 identical vectors — produces 10 pairs (5 choose 2)
        for (int i = 0; i < 5; i++)
            _index.Upsert(new CognitiveEntry($"dup{i}", new[] { 1f, 0f, 0f }, "test"));

        var dups = _index.FindDuplicates("test", 0.9f, maxResults: 3);
        Assert.Equal(3, dups.Count);
    }

    // ── Feature 4: HashEmbeddingService ──

    [Fact]
    public void HashEmbedding_ProducesDeterministicOutput()
    {
        var svc = new HashEmbeddingService(dimensions: 128);
        var v1 = svc.Embed("hello world");
        var v2 = svc.Embed("hello world");

        Assert.Equal(128, v1.Length);
        Assert.Equal(v1, v2); // deterministic
    }

    [Fact]
    public void HashEmbedding_ProducesUnitVector()
    {
        var svc = new HashEmbeddingService(dimensions: 384);
        var v = svc.Embed("test input");

        float normSq = v.Sum(x => x * x);
        Assert.InRange(normSq, 0.99f, 1.01f); // approximately unit norm
    }

    [Fact]
    public void HashEmbedding_DifferentTextProducesDifferentVectors()
    {
        var svc = new HashEmbeddingService();
        var v1 = svc.Embed("cats are great");
        var v2 = svc.Embed("dogs are great");

        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public void HashEmbedding_CorrectDimensions()
    {
        Assert.Equal(384, new HashEmbeddingService().Dimensions);
        Assert.Equal(128, new HashEmbeddingService(128).Dimensions);
        Assert.Throws<ArgumentOutOfRangeException>(() => new HashEmbeddingService(0));
    }

    [Fact]
    public void HashEmbedding_EmptyString_ReturnsZeroVector()
    {
        var svc = new HashEmbeddingService(dimensions: 10);
        var v = svc.Embed("");
        Assert.All(v, x => Assert.Equal(0f, x));
    }

    // ── Feature 5: FindDuplicates sort order is descending similarity ──

    [Fact]
    public void FindDuplicates_StillSortedDescendingAfterOptimization()
    {
        _index.Upsert(new CognitiveEntry("x1", new[] { 1f, 0f, 0f }, "test"));
        _index.Upsert(new CognitiveEntry("x2", new[] { 0.99f, 0.01f, 0f }, "test"));
        _index.Upsert(new CognitiveEntry("x3", new[] { 1f, 0f, 0f }, "test")); // identical to x1

        var dups = _index.FindDuplicates("test", 0.9f);
        Assert.True(dups.Count >= 2);
        for (int i = 1; i < dups.Count; i++)
            Assert.True(dups[i - 1].Similarity >= dups[i].Similarity);
    }

    // ── Decay Config Persistence ──

    [Fact]
    public void DecayConfig_PersistsToDisk()
    {
        var engine1 = new LifecycleEngine(_index, _persistence);
        engine1.SetDecayConfig("test", decayRate: 0.42f, stmThreshold: 7.0f);
        _persistence.Flush();

        // New engine should load from disk
        var engine2 = new LifecycleEngine(_index, _persistence);
        var config = engine2.GetDecayConfig("test");
        Assert.NotNull(config);
        Assert.Equal(0.42f, config!.DecayRate);
        Assert.Equal(7.0f, config.StmThreshold);
    }

    // ── Merge Memories (component-level) ──

    [Fact]
    public void MergeMemories_TransfersEdgesAndClusters()
    {
        // Setup: two entries with edges and cluster membership
        _index.Upsert(new CognitiveEntry("keep", new[] { 1f, 0f, 0f }, "test", "keeper entry"));
        _index.Upsert(new CognitiveEntry("dup", new[] { 0.99f, 0.01f, 0f }, "test", "duplicate entry"));
        _index.Upsert(new CognitiveEntry("other", new[] { 0f, 1f, 0f }, "test", "other entry"));

        // Create edges from dup
        _graph.AddEdge(new GraphEdge("dup", "other", "similar_to"));
        _graph.AddEdge(new GraphEdge("other", "dup", "depends_on"));

        // Create cluster with dup as member
        _clusters.CreateCluster("c1", "test", new[] { "dup", "other" });

        // Transfer edges
        int edgesTransferred = _graph.TransferEdges("dup", "keep");
        Assert.Equal(2, edgesTransferred);

        // Transfer cluster membership
        int clustersTransferred = _clusters.TransferMembership("dup", "keep");
        Assert.Equal(1, clustersTransferred);

        // Archive dup
        _index.SetLifecycleState("dup", "archived");

        // Verify: keep now has the edges
        var keepEdges = _graph.GetEdgesForEntry("keep");
        Assert.True(keepEdges.Count >= 2);

        // Verify: keep is now in the cluster
        var clusters = _clusters.GetClustersForEntry("keep");
        Assert.Contains("c1", clusters);

        // Verify: dup is archived
        var dupEntry = _index.Get("dup", "test");
        Assert.Equal("archived", dupEntry!.LifecycleState);
    }

    [Fact]
    public void MergeMemories_CombinesAccessCounts()
    {
        var keepEntry = new CognitiveEntry("keep", new[] { 1f, 0f }, "test", "keeper");
        keepEntry.AccessCount = 5;
        var dupEntry = new CognitiveEntry("dup", new[] { 0.99f, 0.01f }, "test", "duplicate");
        dupEntry.AccessCount = 3;

        _index.Upsert(keepEntry);
        _index.Upsert(dupEntry);

        // Simulate merge: get entries, combine access counts, upsert updated
        var keep = _index.Get("keep", "test")!;
        var dup = _index.Get("dup", "test")!;
        var merged = new CognitiveEntry(
            keep.Id, keep.Vector, keep.Ns, keep.Text,
            keep.Category, keep.Metadata, keep.LifecycleState,
            keep.CreatedAt, keep.LastAccessedAt,
            keep.AccessCount + dup.AccessCount,
            keep.ActivationEnergy, keep.IsSummaryNode, keep.SourceClusterId);
        _index.Upsert(merged);

        var result = _index.Get("keep", "test");
        Assert.Equal(8, result!.AccessCount);
    }
}
