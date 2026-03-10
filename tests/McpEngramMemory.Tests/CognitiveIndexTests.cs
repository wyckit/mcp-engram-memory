using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class CognitiveIndexTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;

    public CognitiveIndexTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"cognitive_test_{Guid.NewGuid():N}");
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

    // ── Count ──

    [Fact]
    public void Count_EmptyIndex_ReturnsZero()
    {
        Assert.Equal(0, _index.Count);
    }

    [Fact]
    public void Count_AfterUpsert_ReturnsOne()
    {
        _index.Upsert(MakeEntry("a", new[] { 1f, 0f }));
        Assert.Equal(1, _index.Count);
    }

    [Fact]
    public void CountInNamespace_ReturnsCorrectCount()
    {
        _index.Upsert(MakeEntry("a", new[] { 1f, 0f }, ns: "work"));
        _index.Upsert(MakeEntry("b", new[] { 0f, 1f }, ns: "personal"));
        Assert.Equal(1, _index.CountInNamespace("work"));
        Assert.Equal(1, _index.CountInNamespace("personal"));
    }

    // ── Upsert ──

    [Fact]
    public void Upsert_SameId_Replaces()
    {
        _index.Upsert(MakeEntry("a", new[] { 1f, 0f }, text: "first"));
        _index.Upsert(MakeEntry("a", new[] { 0f, 1f }, text: "second"));
        Assert.Equal(1, _index.Count);
        var entry = _index.Get("a");
        Assert.Equal("second", entry?.Text);
    }

    [Fact]
    public void Upsert_NullEntry_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _index.Upsert(null!));
    }

    // ── Delete ──

    [Fact]
    public void Delete_ExistingEntry_ReturnsTrueAndReducesCount()
    {
        _index.Upsert(MakeEntry("a", new[] { 1f, 0f }));
        Assert.True(_index.Delete("a"));
        Assert.Equal(0, _index.Count);
    }

    [Fact]
    public void Delete_NonExistentId_ReturnsFalse()
    {
        Assert.False(_index.Delete("missing"));
    }

    // ── Search ──

    [Fact]
    public void Search_ExactMatch_ReturnsScoreOfOne()
    {
        _index.Upsert(MakeEntry("a", new[] { 1f, 0f, 0f }));
        var results = _index.Search(new float[] { 1f, 0f, 0f }, "test", k: 1);
        Assert.Single(results);
        Assert.Equal("a", results[0].Id);
        Assert.Equal(1f, results[0].Score, precision: 5);
    }

    [Fact]
    public void Search_ReturnsKNearest()
    {
        _index.Upsert(MakeEntry("close", new[] { 1f, 0.1f }));
        _index.Upsert(MakeEntry("medium", new[] { 0.5f, 0.5f }));
        _index.Upsert(MakeEntry("far", new[] { 0f, 1f }));

        var results = _index.Search(new float[] { 1f, 0f }, "test", k: 2);
        Assert.Equal(2, results.Count);
        Assert.Equal("close", results[0].Id);
    }

    [Fact]
    public void Search_MinScore_FiltersLowScores()
    {
        _index.Upsert(MakeEntry("a", new[] { 1f, 0f }));
        _index.Upsert(MakeEntry("b", new[] { 0f, 1f }));

        var results = _index.Search(new float[] { 1f, 0f }, "test", k: 10, minScore: 0.99f);
        Assert.Single(results);
        Assert.Equal("a", results[0].Id);
    }

    [Fact]
    public void Search_EmptyIndex_ReturnsEmpty()
    {
        var results = _index.Search(new float[] { 1f, 0f }, "test");
        Assert.Empty(results);
    }

    [Fact]
    public void Search_ZeroQueryVector_Throws()
    {
        _index.Upsert(MakeEntry("a", new[] { 1f, 0f }));
        Assert.Throws<ArgumentException>(() => _index.Search(new float[] { 0f, 0f }, "test"));
    }

    [Fact]
    public void Search_NegativeK_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _index.Search(new float[] { 1f, 0f }, "test", k: -1));
    }

    [Fact]
    public void Search_ZeroK_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _index.Search(new float[] { 1f, 0f }, "test", k: 0));
    }

    [Fact]
    public void Search_DimensionMismatch_SkipsEntry()
    {
        _index.Upsert(MakeEntry("a", new[] { 1f, 0f, 0f })); // 3-dim
        _index.Upsert(MakeEntry("b", new[] { 1f, 0f }));     // 2-dim

        var results = _index.Search(new float[] { 1f, 0f }, "test");
        Assert.Single(results);
        Assert.Equal("b", results[0].Id);
    }

    [Fact]
    public void Search_ResultsOrderedByDescendingScore()
    {
        _index.Upsert(MakeEntry("low", new[] { 0f, 1f }));
        _index.Upsert(MakeEntry("mid", new[] { 0.7071f, 0.7071f }));
        _index.Upsert(MakeEntry("high", new[] { 1f, 0f }));

        var results = _index.Search(new float[] { 1f, 0f }, "test", k: 3);
        Assert.Equal("high", results[0].Id);
        Assert.Equal("mid", results[1].Id);
        Assert.Equal("low", results[2].Id);
    }

    // ── Namespace Isolation ──

    [Fact]
    public void Search_NamespaceIsolation_OnlyReturnsMatchingNamespace()
    {
        _index.Upsert(MakeEntry("a", new[] { 1f, 0f }, ns: "work"));
        _index.Upsert(MakeEntry("b", new[] { 1f, 0f }, ns: "personal"));

        var workResults = _index.Search(new float[] { 1f, 0f }, "work");
        Assert.Single(workResults);
        Assert.Equal("a", workResults[0].Id);
    }

    [Fact]
    public void GetNamespaces_ReturnsAllNamespaces()
    {
        _index.Upsert(MakeEntry("a", new[] { 1f, 0f }, ns: "work"));
        _index.Upsert(MakeEntry("b", new[] { 0f, 1f }, ns: "personal"));

        var namespaces = _index.GetNamespaces();
        Assert.Contains("work", namespaces);
        Assert.Contains("personal", namespaces);
    }

    // ── Lifecycle Filtering ──

    [Fact]
    public void Search_LifecycleFiltering_ExcludesArchivedByDefault()
    {
        _index.Upsert(MakeEntry("a", new[] { 1f, 0f }, lifecycleState: "stm"));
        _index.Upsert(MakeEntry("b", new[] { 1f, 0f }, lifecycleState: "archived"));

        var results = _index.Search(new float[] { 1f, 0f }, "test");
        Assert.Single(results);
        Assert.Equal("a", results[0].Id);
    }

    [Fact]
    public void SearchAllStates_IncludesArchived()
    {
        _index.Upsert(MakeEntry("a", new[] { 1f, 0f }, lifecycleState: "stm"));
        _index.Upsert(MakeEntry("b", new[] { 1f, 0.01f }, lifecycleState: "archived"));

        var results = _index.SearchAllStates(new float[] { 1f, 0f }, "test", minScore: 0f);
        Assert.Equal(2, results.Count);
    }

    // ── Category Filtering ──

    [Fact]
    public void Search_CategoryFilter_OnlyReturnsMatchingCategory()
    {
        _index.Upsert(MakeEntry("a", new[] { 1f, 0f }, category: "meeting-notes"));
        _index.Upsert(MakeEntry("b", new[] { 1f, 0f }, category: "tasks"));

        var results = _index.Search(new float[] { 1f, 0f }, "test", category: "meeting-notes");
        Assert.Single(results);
        Assert.Equal("a", results[0].Id);
    }

    // ── Summary First ──

    [Fact]
    public void Search_SummaryFirst_PrioritizesSummaryNodes()
    {
        var regular = MakeEntry("regular", new[] { 1f, 0f });
        var summary = MakeEntry("summary", new[] { 0.99f, 0.01f });
        summary.IsSummaryNode = true;
        summary.SourceClusterId = "cluster1";

        _index.Upsert(regular);
        _index.Upsert(summary);

        var results = _index.Search(new float[] { 1f, 0f }, "test", summaryFirst: true);
        Assert.Equal("summary", results[0].Id);
    }

    // ── Access Tracking ──

    [Fact]
    public void RecordAccess_IncrementsCountAndUpdatesTimestamp()
    {
        _index.Upsert(MakeEntry("a", new[] { 1f, 0f }));
        var before = _index.Get("a")!.LastAccessedAt;

        _index.RecordAccess("a");
        var entry = _index.Get("a")!;
        Assert.Equal(2, entry.AccessCount); // Starts at 1, incremented by RecordAccess
        Assert.True(entry.LastAccessedAt >= before);
    }

    // ── Lifecycle State ──

    [Fact]
    public void SetLifecycleState_ChangesState()
    {
        _index.Upsert(MakeEntry("a", new[] { 1f, 0f }));
        Assert.True(_index.SetLifecycleState("a", "ltm"));
        Assert.Equal("ltm", _index.Get("a")!.LifecycleState);
    }

    [Fact]
    public void SetLifecycleState_NonExistentId_ReturnsFalse()
    {
        Assert.False(_index.SetLifecycleState("missing", "ltm"));
    }

    // ── State Counts ──

    [Fact]
    public void GetStateCounts_ReturnsCorrectCounts()
    {
        _index.Upsert(MakeEntry("a", new[] { 1f, 0f }, lifecycleState: "stm"));
        _index.Upsert(MakeEntry("b", new[] { 0f, 1f }, lifecycleState: "ltm"));
        _index.Upsert(MakeEntry("c", new[] { 1f, 1f }, lifecycleState: "archived"));

        var (stm, ltm, archived) = _index.GetStateCounts();
        Assert.Equal(1, stm);
        Assert.Equal(1, ltm);
        Assert.Equal(1, archived);
    }

    [Fact]
    public void GetStateCounts_SpecificNamespace_WithSanitizedFileName_LoadsCorrectNamespace()
    {
        var ns = "team/alpha";
        _persistence.SaveNamespaceSync(ns, new NamespaceData
        {
            Entries =
            [
                MakeEntry("persisted1", new[] { 1f, 0f }, ns: ns, lifecycleState: "ltm")
            ]
        });

        var (stm, ltm, archived) = _index.GetStateCounts(ns);
        Assert.Equal(0, stm);
        Assert.Equal(1, ltm);
        Assert.Equal(0, archived);
    }

    // ── Concurrency ──

    [Fact]
    public async Task ConcurrentUpsertAndSearch_DoesNotThrow()
    {
        var tasks = new List<Task>();
        for (int i = 0; i < 100; i++)
        {
            int id = i;
            tasks.Add(Task.Run(() =>
                _index.Upsert(MakeEntry($"v{id}", new[] { id + 1f, id + 2f }))));
        }
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
                _index.Search(new float[] { 1f, 2f }, "test", k: 5)));
        }
        await Task.WhenAll(tasks);
        Assert.Equal(100, _index.Count);
    }

    // ── CognitiveEntry Validation ──

    [Fact]
    public void CognitiveEntry_EmptyId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CognitiveEntry("", new[] { 1f }, "test"));
    }

    [Fact]
    public void CognitiveEntry_EmptyVector_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CognitiveEntry("a", Array.Empty<float>(), "test"));
    }

    [Fact]
    public void CognitiveEntry_NullVector_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CognitiveEntry("a", null!, "test"));
    }

    [Fact]
    public void CognitiveEntry_EmptyNamespace_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CognitiveEntry("a", new[] { 1f }, ""));
    }

    [Fact]
    public void CognitiveEntry_DefensiveCopy_VectorNotMutatedExternally()
    {
        var original = new float[] { 1f, 2f, 3f };
        var entry = new CognitiveEntry("a", original, "test");
        original[0] = 999f;
        Assert.Equal(1f, entry.Vector[0]);
    }

    [Fact]
    public void CognitiveEntry_DefensiveCopy_MetadataNotMutatedExternally()
    {
        var metadata = new Dictionary<string, string> { ["key"] = "value" };
        var entry = new CognitiveEntry("a", new[] { 1f }, "test", metadata: metadata);
        metadata["key"] = "changed";
        Assert.Equal("value", entry.Metadata["key"]);
    }

    [Fact]
    public void CognitiveEntry_DefaultsToStm()
    {
        var entry = new CognitiveEntry("a", new[] { 1f }, "test");
        Assert.Equal("stm", entry.LifecycleState);
    }

    [Fact]
    public void CognitiveEntry_CanSpecifyLifecycleState()
    {
        var entry = new CognitiveEntry("a", new[] { 1f }, "test", lifecycleState: "ltm");
        Assert.Equal("ltm", entry.LifecycleState);
    }

    // ── Dispose ──

    [Fact]
    public void Dispose_CanBeCalledSafely()
    {
        var persistence = new PersistenceManager(Path.Combine(Path.GetTempPath(), $"dispose_test_{Guid.NewGuid():N}"), debounceMs: 50);
        var index = new CognitiveIndex(persistence);
        index.Upsert(MakeEntry("a", new[] { 1f, 0f }));
        index.Dispose();
        persistence.Dispose();
    }

    // ── Cross-namespace ──

    [Fact]
    public void Get_SameIdDifferentNamespaces_ReturnsSomeEntry()
    {
        // Same ID "a" in two namespaces — Get(id) without namespace returns one of them
        _index.Upsert(MakeEntry("a", new[] { 1f, 0f }, ns: "work", text: "work entry"));
        _index.Upsert(MakeEntry("a", new[] { 0f, 1f }, ns: "personal", text: "personal entry"));

        var entry = _index.Get("a");
        Assert.NotNull(entry);
        // Should return one of the entries (undefined which namespace wins)
        Assert.Equal("a", entry.Id);
    }

    [Fact]
    public void Get_ById_WithNamespace_ReturnsCorrectEntry()
    {
        _index.Upsert(MakeEntry("a", new[] { 1f, 0f }, ns: "work", text: "work entry"));
        _index.Upsert(MakeEntry("a", new[] { 0f, 1f }, ns: "personal", text: "personal entry"));

        var workEntry = _index.Get("a", "work");
        var personalEntry = _index.Get("a", "personal");

        Assert.NotNull(workEntry);
        Assert.Equal("work entry", workEntry.Text);
        Assert.NotNull(personalEntry);
        Assert.Equal("personal entry", personalEntry.Text);
    }

    // ── Memory Limits ──

    [Fact]
    public void Upsert_MaxNamespaceSize_RejectsWhenFull()
    {
        var limits = new MemoryLimitsConfig(MaxNamespaceSize: 2);
        using var limitedIndex = new CognitiveIndex(_persistence, limits);

        limitedIndex.Upsert(MakeEntry("a", new[] { 1f, 0f }));
        limitedIndex.Upsert(MakeEntry("b", new[] { 0f, 1f }));

        // Third entry should fail
        var ex = Assert.Throws<InvalidOperationException>(() =>
            limitedIndex.Upsert(MakeEntry("c", new[] { 1f, 1f })));
        Assert.Contains("namespace", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Upsert_MaxNamespaceSize_AllowsUpdate()
    {
        var limits = new MemoryLimitsConfig(MaxNamespaceSize: 2);
        using var limitedIndex = new CognitiveIndex(_persistence, limits);

        limitedIndex.Upsert(MakeEntry("a", new[] { 1f, 0f }));
        limitedIndex.Upsert(MakeEntry("b", new[] { 0f, 1f }));

        // Updating existing entry should succeed even at capacity
        limitedIndex.Upsert(MakeEntry("a", new[] { 0.5f, 0.5f }));
        Assert.Equal(2, limitedIndex.CountInNamespace("test"));
    }

    [Fact]
    public void Upsert_MaxTotalCount_RejectsWhenFull()
    {
        var limits = new MemoryLimitsConfig(MaxTotalCount: 2);
        using var limitedIndex = new CognitiveIndex(_persistence, limits);

        limitedIndex.Upsert(MakeEntry("a", new[] { 1f, 0f }, "ns1"));
        limitedIndex.Upsert(MakeEntry("b", new[] { 0f, 1f }, "ns2"));

        // Third entry in a new namespace should fail
        var ex = Assert.Throws<InvalidOperationException>(() =>
            limitedIndex.Upsert(MakeEntry("c", new[] { 1f, 1f }, "ns3")));
        Assert.Contains("total", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Upsert_NoLimits_AcceptsUnlimited()
    {
        // Default limits (int.MaxValue) should not restrict
        for (int i = 0; i < 100; i++)
            _index.Upsert(MakeEntry($"e{i}", new[] { (float)i, 0f }));
        Assert.Equal(100, _index.CountInNamespace("test"));
    }

    // ── Helper ──

    private static CognitiveEntry MakeEntry(string id, float[] vector, string ns = "test",
        string? text = null, string? category = null, string lifecycleState = "stm")
    {
        return new CognitiveEntry(id, vector, ns, text, category, lifecycleState: lifecycleState);
    }
}
