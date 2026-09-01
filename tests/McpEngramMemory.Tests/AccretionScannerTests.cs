using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class AccretionScannerTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly ClusterManager _clusters;
    private readonly LifecycleEngine _lifecycle;
    private readonly AccretionScanner _scanner;

    public AccretionScannerTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"accretion_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
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

    // ── DBSCAN Unit Tests ──

    [Fact]
    public void Dbscan_EmptyInput_ReturnsNoClusters()
    {
        var clusters = AccretionScanner.Dbscan(new List<CognitiveEntry>(), 0.15f, 3);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Dbscan_SinglePoint_NoCluster()
    {
        var entries = new List<CognitiveEntry>
        {
            new("a", new[] { 1f, 0f }, "test", lifecycleState: "ltm")
        };
        var clusters = AccretionScanner.Dbscan(entries, 0.15f, 3);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Dbscan_TightCluster_DetectedAsSingleCluster()
    {
        // 4 nearly identical vectors — each has 3 external neighbors, meets minPoints=3
        var entries = new List<CognitiveEntry>
        {
            new("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"),
            new("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"),
            new("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"),
            new("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"),
        };

        var clusters = AccretionScanner.Dbscan(entries, 0.15f, 3);
        Assert.Single(clusters);
        Assert.Equal(4, clusters[0].Count);
    }

    [Fact]
    public void Dbscan_TwoDistinctClusters_DetectedSeparately()
    {
        // Cluster 1: 4 vectors near (1, 0, 0)
        // Cluster 2: 4 vectors near (0, 1, 0)
        var entries = new List<CognitiveEntry>
        {
            new("a1", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"),
            new("a2", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"),
            new("a3", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"),
            new("a4", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"),
            new("b1", new[] { 0f, 1f, 0f }, "test", lifecycleState: "ltm"),
            new("b2", new[] { 0.01f, 0.99f, 0f }, "test", lifecycleState: "ltm"),
            new("b3", new[] { 0.02f, 0.98f, 0f }, "test", lifecycleState: "ltm"),
            new("b4", new[] { 0.03f, 0.97f, 0f }, "test", lifecycleState: "ltm"),
        };

        var clusters = AccretionScanner.Dbscan(entries, 0.15f, 3);
        Assert.Equal(2, clusters.Count);
    }

    [Fact]
    public void Dbscan_ScatteredPoints_AllNoise()
    {
        // Orthogonal vectors — very far apart
        var entries = new List<CognitiveEntry>
        {
            new("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"),
            new("b", new[] { 0f, 1f, 0f }, "test", lifecycleState: "ltm"),
            new("c", new[] { 0f, 0f, 1f }, "test", lifecycleState: "ltm"),
        };

        var clusters = AccretionScanner.Dbscan(entries, 0.01f, 3);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Dbscan_BelowMinPoints_NoCluster()
    {
        // 2 close vectors but minPoints=3 (need 3 external neighbors)
        var entries = new List<CognitiveEntry>
        {
            new("a", new[] { 1f, 0f }, "test", lifecycleState: "ltm"),
            new("b", new[] { 0.99f, 0.01f }, "test", lifecycleState: "ltm"),
        };

        var clusters = AccretionScanner.Dbscan(entries, 0.15f, 3);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Dbscan_SelfExcluded_MinPointsMeansExternalNeighbors()
    {
        // 3 close vectors with minPoints=3 — each has only 2 external neighbors, not enough
        var entries = new List<CognitiveEntry>
        {
            new("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"),
            new("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"),
            new("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"),
        };

        var clusters = AccretionScanner.Dbscan(entries, 0.15f, 3);
        Assert.Empty(clusters); // 3 points, each with 2 neighbors < minPoints=3

        // But with minPoints=2, they cluster
        var clusters2 = AccretionScanner.Dbscan(entries, 0.15f, 2);
        Assert.Single(clusters2);
        Assert.Equal(3, clusters2[0].Count);
    }

    // ── ScanNamespace Integration Tests ──

    [Fact]
    public void ScanNamespace_OnlyScansLtmEntries()
    {
        // STM entries should be ignored even if they cluster
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "stm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f }, "test", lifecycleState: "stm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f }, "test", lifecycleState: "stm"));

        var result = _scanner.ScanNamespace("test", tenantId: "");
        Assert.Equal(0, result.ScannedCount);
        Assert.Equal(0, result.ClustersDetected);
    }

    [Fact]
    public void ScanNamespace_DetectsLtmCluster()
    {
        // 4 entries so each has 3 external neighbors (meets default minPoints=3)
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var result = _scanner.ScanNamespace("test", tenantId: "");
        Assert.Equal(4, result.ScannedCount);
        Assert.Equal(1, result.ClustersDetected);
        Assert.Single(result.NewCollapses);
        Assert.Equal(4, result.NewCollapses[0].MemberCount);
    }

    [Fact]
    public void ScanNamespace_SkipsSummaryNodes()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f }, "test", lifecycleState: "ltm"));
        var summaryEntry = new CognitiveEntry("s", new[] { 0.995f, 0.005f }, "test", lifecycleState: "ltm")
        {
            IsSummaryNode = true
        };
        _index.Upsert(summaryEntry);

        var result = _scanner.ScanNamespace("test", tenantId: "", minPoints: 1);
        // Summary node should be excluded from scan — only 2 entries scanned
        Assert.Equal(2, result.ScannedCount);
    }

    [Fact]
    public void ScanNamespace_DuplicateScan_DoesNotDuplicateCollapses()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var result1 = _scanner.ScanNamespace("test", tenantId: "");
        Assert.Equal(1, result1.ClustersDetected);
        Assert.Single(result1.NewCollapses);

        // Scan again — same entries should not produce a new collapse
        var result2 = _scanner.ScanNamespace("test", tenantId: "");
        Assert.Equal(1, result2.ClustersDetected);
        Assert.Empty(result2.NewCollapses); // Already pending
    }

    // ── Pending Collapse Lifecycle ──

    [Fact]
    public void GetPendingCollapses_ReturnsOnlyForNamespace()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "ns1", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "ns1", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "ns1", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "ns1", lifecycleState: "ltm"));

        _scanner.ScanNamespace("ns1", tenantId: "");

        Assert.Single(_scanner.GetPendingCollapses("ns1", tenantId: ""));
        Assert.Empty(_scanner.GetPendingCollapses("ns2", tenantId: ""));
    }

    [Fact]
    public void ExecuteCollapse_ArchivesMembersAndCreatesCluster()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var scanResult = _scanner.ScanNamespace("test", tenantId: "");
        var collapseId = scanResult.NewCollapses[0].CollapseId;

        var result = _scanner.ExecuteCollapse(
            collapseId, "Summary of a, b, c, d", new[] { 0.99f, 0.01f, 0f },
            _clusters, tenantId: "");

        Assert.Contains("Collapsed 4 entries", result);

        // Members should be archived
        Assert.Equal("archived", _index.Get("a")!.LifecycleState);
        Assert.Equal("archived", _index.Get("b")!.LifecycleState);
        Assert.Equal("archived", _index.Get("c")!.LifecycleState);
        Assert.Equal("archived", _index.Get("d")!.LifecycleState);

        // Cluster should exist
        Assert.Equal(1, _clusters.ClusterCount);

        // Pending collapse should be removed
        Assert.Equal(0, _scanner.PendingCount);
    }

    [Fact]
    public void ExecuteCollapse_NonExistentId_ReturnsError()
    {
        var result = _scanner.ExecuteCollapse(
            "nonexistent", "summary", new[] { 1f, 0f },
            _clusters, tenantId: "");
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void ExecuteCollapse_WhenArchiveStepFails_PreservesPendingCollapse()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var scanResult = _scanner.ScanNamespace("test", tenantId: "");
        var collapseId = scanResult.NewCollapses[0].CollapseId;

        // Simulate partial failure: one member disappears before collapse execution.
        _index.Delete("d");

        var result = _scanner.ExecuteCollapse(
            collapseId, "Summary of a, b, c, d", new[] { 0.99f, 0.01f, 0f },
            _clusters, tenantId: "");

        Assert.StartsWith("Error:", result);
        Assert.Contains("partially failed during archive step", result);
        Assert.Equal(1, _scanner.PendingCount);
    }

    [Fact]
    public void ExecuteCollapse_AfterArchiveFailure_RetrySucceedsAndClearsPending()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var scanResult = _scanner.ScanNamespace("test", tenantId: "");
        var collapseId = scanResult.NewCollapses[0].CollapseId;

        // First attempt fails because one member disappears.
        _index.Delete("d");
        var firstAttempt = _scanner.ExecuteCollapse(
            collapseId, "Summary of a, b, c, d", new[] { 0.99f, 0.01f, 0f },
            _clusters, tenantId: "");
        Assert.StartsWith("Error:", firstAttempt);
        Assert.Equal(1, _scanner.PendingCount);

        // Restore missing member and retry the same pending collapse.
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));
        var secondAttempt = _scanner.ExecuteCollapse(
            collapseId, "Summary of a, b, c, d", new[] { 0.99f, 0.01f, 0f },
            _clusters, tenantId: "");

        Assert.Contains("Collapsed 4 entries", secondAttempt);
        Assert.Equal(0, _scanner.PendingCount);
        Assert.Equal("archived", _index.Get("a")!.LifecycleState);
        Assert.Equal("archived", _index.Get("b")!.LifecycleState);
        Assert.Equal("archived", _index.Get("c")!.LifecycleState);
        Assert.Equal("archived", _index.Get("d")!.LifecycleState);
    }

    [Fact]
    public void ExecuteCollapse_ScreenedOutMember_IsNotArchived()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var scanResult = _scanner.ScanNamespace("test", tenantId: "");
        var collapseId = scanResult.NewCollapses[0].CollapseId;

        // 'b' gains a same-id twin in another namespace of the tenant, so the cluster's topology
        // screen silently refuses it. Archiving it anyway would hide it from default search with
        // nothing — neither the cluster nor the summary — standing in for it.
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f, 0f }, "other", lifecycleState: "ltm"));

        var result = _scanner.ExecuteCollapse(
            collapseId, "Summary of a, c, d", new[] { 0.99f, 0.01f, 0f },
            _clusters, tenantId: "");

        Assert.StartsWith("Collapsed", result);
        Assert.Contains("not admitted", result);
        Assert.Equal("ltm", _index.Get("b", "test", tenantId: "")!.LifecycleState);
        Assert.Equal("archived", _index.Get("a", "test", tenantId: "")!.LifecycleState);
        Assert.Equal("archived", _index.Get("c", "test", tenantId: "")!.LifecycleState);
        Assert.Equal("archived", _index.Get("d", "test", tenantId: "")!.LifecycleState);
        Assert.Equal(0, _scanner.PendingCount);
    }

    [Fact]
    public void UndoCollapse_AmbiguousMember_PreservesRecordForRetry()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var scanResult = _scanner.ScanNamespace("test", tenantId: "");
        var collapseId = scanResult.NewCollapses[0].CollapseId;
        var executeResult = _scanner.ExecuteCollapse(
            collapseId, "Summary of a, b, c, d", new[] { 0.99f, 0.01f, 0f },
            _clusters, tenantId: "");
        Assert.StartsWith("Collapsed", executeResult);
        var record = Assert.Single(_scanner.GetCollapseHistory("test", tenantId: ""));

        // 'b' gains a same-id twin: UpdateCluster's screen will silently drop its removal while
        // still replying success, so the undo must verify the memberships and refuse to delete
        // the record — otherwise the leftover membership becomes permanently unretryable.
        _index.Upsert(new CognitiveEntry("b", new[] { 0f, 1f, 0f }, "other", lifecycleState: "ltm"));

        var undo = _scanner.UndoCollapse(record.CollapseId, _lifecycle, _clusters, tenantId: "");

        Assert.StartsWith("Error:", undo);
        Assert.Single(_scanner.GetCollapseHistory("test", tenantId: ""));
        Assert.Contains(_clusters.GetClusterMembershipsForEntry("b", tenantId: ""),
            m => m.ClusterId == record.ClusterId);
        // The failed undo must not have consumed anything the retry needs: the summary entry is
        // deleted only after membership cleanup is verified, so a preserved record still names a
        // summary that exists.
        Assert.NotNull(_index.Get(record.SummaryEntryId, "test", tenantId: ""));

        // Resolving the ambiguity makes the same record retryable, and the retry fully reverses.
        _index.Delete("b", "other", tenantId: "");
        var retry = _scanner.UndoCollapse(record.CollapseId, _lifecycle, _clusters, tenantId: "");

        Assert.StartsWith("Reversed", retry);
        Assert.Empty(_scanner.GetCollapseHistory("test", tenantId: ""));
        Assert.DoesNotContain(_clusters.GetClusterMembershipsForEntry("b", tenantId: ""),
            m => m.ClusterId == record.ClusterId);
        Assert.Null(_index.Get(record.SummaryEntryId, "test", tenantId: ""));
        // The cluster OBJECT is gone too — an undo that stopped at "emptied" would republish a
        // zero-member shell whose SummaryEntryId dangles.
        Assert.Null(_clusters.GetCluster(record.ClusterId, tenantId: ""));
    }

    [Fact]
    public void UndoCollapse_AfterRetriedExecute_RestoresOriginalStates()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var scanResult = _scanner.ScanNamespace("test", tenantId: "");
        var collapseId = scanResult.NewCollapses[0].CollapseId;

        // First attempt fails on the missing member — but it has already archived a, b, c.
        _index.Delete("d");
        var first = _scanner.ExecuteCollapse(
            collapseId, "Summary of a, b, c, d", new[] { 0.99f, 0.01f, 0f },
            _clusters, tenantId: "");
        Assert.StartsWith("Error:", first);
        Assert.Equal("archived", _index.Get("a", "test", tenantId: "")!.LifecycleState);

        // The retry must NOT re-read current states as the receipt: a, b, c now read "archived"
        // — the state the FAILED attempt put them in, not their pre-collapse state.
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));
        var second = _scanner.ExecuteCollapse(
            collapseId, "Summary of a, b, c, d", new[] { 0.99f, 0.01f, 0f },
            _clusters, tenantId: "");
        Assert.Contains("Collapsed 4 entries", second);

        var record = Assert.Single(_scanner.GetCollapseHistory("test", tenantId: ""));
        var undo = _scanner.UndoCollapse(record.CollapseId, _lifecycle, _clusters, tenantId: "");
        Assert.StartsWith("Reversed", undo);

        // The receipt captured on the FIRST attempt is what the undo restores from.
        Assert.Equal("ltm", _index.Get("a", "test", tenantId: "")!.LifecycleState);
        Assert.Equal("ltm", _index.Get("b", "test", tenantId: "")!.LifecycleState);
        Assert.Equal("ltm", _index.Get("c", "test", tenantId: "")!.LifecycleState);
        Assert.Equal("ltm", _index.Get("d", "test", tenantId: "")!.LifecycleState);
    }

    [Fact]
    public void ExecuteCollapse_AllMembersScreened_RefusesAndPreservesPending()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var scanResult = _scanner.ScanNamespace("test", tenantId: "");
        var collapseId = scanResult.NewCollapses[0].CollapseId;

        // EVERY member gains a twin: the creation screen admits nothing, and "Collapsed 0"
        // would retire the proposal while an empty cluster and an orphan summary remain.
        foreach (var id in new[] { "a", "b", "c", "d" })
            _index.Upsert(new CognitiveEntry(id, new[] { 0f, 1f, 0f }, "other", lifecycleState: "ltm"));

        var result = _scanner.ExecuteCollapse(
            collapseId, "Summary of nothing", new[] { 0.99f, 0.01f, 0f },
            _clusters, tenantId: "");

        Assert.StartsWith("Error:", result);
        Assert.Contains("admitted no members", result);
        Assert.Equal(1, _scanner.PendingCount);
        Assert.Equal("ltm", _index.Get("a", "test", tenantId: "")!.LifecycleState);
        Assert.Equal("ltm", _index.Get("d", "test", tenantId: "")!.LifecycleState);

        // Once the ambiguity resolves, the SAME proposal must become executable again — the
        // already-exists branch re-proposes the members an earlier attempt could not admit.
        foreach (var id in new[] { "a", "b", "c", "d" })
            _index.Delete(id, "other", tenantId: "");

        var retry = _scanner.ExecuteCollapse(
            collapseId, "Summary of a, b, c, d", new[] { 0.99f, 0.01f, 0f },
            _clusters, tenantId: "");

        Assert.Contains("Collapsed 4 entries", retry);
        Assert.Equal(0, _scanner.PendingCount);
        Assert.Equal("archived", _index.Get("a", "test", tenantId: "")!.LifecycleState);
    }

    [Fact]
    public void ExecuteCollapse_PartialFailure_IsUndoableAfterRestart()
    {
        // Persistence-backed scanner: the durable receipt is the whole point of this test.
        var scanner = new AccretionScanner(_index, _persistence);
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var scanResult = scanner.ScanNamespace("test", tenantId: "");
        var collapseId = scanResult.NewCollapses[0].CollapseId;

        _index.Delete("d");
        var first = scanner.ExecuteCollapse(
            collapseId, "Summary of a, b, c, d", new[] { 0.99f, 0.01f, 0f },
            _clusters, tenantId: "");
        Assert.StartsWith("Error:", first);
        Assert.Equal("archived", _index.Get("a", "test", tenantId: "")!.LifecycleState);

        // "Restart": rebuild the WHOLE stack from disk — index, clusters, lifecycle, scanner —
        // so nothing in-memory survives. The Flush stands in for the entry writes that had
        // already left the debounce window when the process died; the RECEIPT must not need it,
        // because the durability point flushes synchronously before the first archive.
        _persistence.Flush();
        _index.Dispose();
        _persistence.Dispose();

        using var persistence2 = new PersistenceManager(_testDataPath, debounceMs: 50);
        using var index2 = new CognitiveIndex(persistence2);
        var clusters2 = new ClusterManager(index2, persistence2);
        var lifecycle2 = new LifecycleEngine(index2);
        var restarted = new AccretionScanner(index2, persistence2);
        Assert.Equal(0, restarted.PendingCount);

        var record = Assert.Single(restarted.GetCollapseHistory("test", tenantId: ""));
        var undo = restarted.UndoCollapse(record.CollapseId, lifecycle2, clusters2, tenantId: "");

        Assert.StartsWith("Reversed", undo);
        Assert.Equal("ltm", index2.Get("a", "test", tenantId: "")!.LifecycleState);
        Assert.Equal("ltm", index2.Get("b", "test", tenantId: "")!.LifecycleState);
        Assert.Equal("ltm", index2.Get("c", "test", tenantId: "")!.LifecycleState);
        Assert.Null(clusters2.GetCluster(record.ClusterId, tenantId: ""));
        Assert.Empty(restarted.GetCollapseHistory("test", tenantId: ""));
    }

    /// <summary>
    /// Delegates everything to a real provider but fails the synchronous receipt save on
    /// demand — the seam for proving that execution refuses to archive without a receipt.
    /// </summary>
    private sealed class ReceiptFailingProvider : IStorageProvider
    {
        private readonly IStorageProvider _inner;
        public bool FailReceiptSaves { get; set; } = true;
        public ReceiptFailingProvider(IStorageProvider inner) => _inner = inner;

        public bool UpsertCollapseRecordSync(CollapseRecord record)
            => !FailReceiptSaves && _inner.UpsertCollapseRecordSync(record);

        public bool DeleteCollapseRecordSync(string collapseId)
            => !FailReceiptSaves && _inner.DeleteCollapseRecordSync(collapseId);

        public CollapseRecordCas UpsertCollapseRecordSync(CollapseRecord record, long? onlyIfGeneration)
            => FailReceiptSaves ? CollapseRecordCas.StoreFailed : _inner.UpsertCollapseRecordSync(record, onlyIfGeneration);

        public CollapseRecordCas DeleteCollapseRecordSync(string collapseId, long onlyIfGeneration)
            => FailReceiptSaves ? CollapseRecordCas.StoreFailed : _inner.DeleteCollapseRecordSync(collapseId, onlyIfGeneration);

        public bool TryReadCollapseRecord(string collapseId, out CollapseRecord? record)
            => _inner.TryReadCollapseRecord(collapseId, out record);

        public bool TryReadCollapseHistory(out List<CollapseRecord> records)
            => _inner.TryReadCollapseHistory(out records);

        public bool TryFlush() => _inner.TryFlush();

        public NamespaceData LoadNamespace(string ns) => _inner.LoadNamespace(ns);
        public IReadOnlyList<string> GetPersistedNamespaces() => _inner.GetPersistedNamespaces();
        public void ScheduleSave(string ns, Func<NamespaceData> dataProvider) => _inner.ScheduleSave(ns, dataProvider);
        public void SaveNamespaceSync(string ns, NamespaceData data) => _inner.SaveNamespaceSync(ns, data);
        public bool SupportsIncrementalWrites => _inner.SupportsIncrementalWrites;
        public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) => _inner.ScheduleUpsertEntry(ns, entry);
        public void ScheduleDeleteEntry(string ns, string entryId) => _inner.ScheduleDeleteEntry(ns, entryId);
        public List<GraphEdge> LoadGlobalEdges() => _inner.LoadGlobalEdges();
        public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider) => _inner.ScheduleSaveGlobalEdges(dataProvider);
        public List<SemanticCluster> LoadClusters() => _inner.LoadClusters();
        public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider) => _inner.ScheduleSaveClusters(dataProvider);
        public List<CollapseRecord> LoadCollapseHistory() => _inner.LoadCollapseHistory();
        public Dictionary<string, DecayConfig> LoadDecayConfigs() => _inner.LoadDecayConfigs();
        public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider) => _inner.ScheduleSaveDecayConfigs(dataProvider);
        public HnswSnapshot? LoadHnswSnapshot(string ns) => _inner.LoadHnswSnapshot(ns);
        public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot) => _inner.SaveHnswSnapshotSync(ns, snapshot);
        public void DeleteHnswSnapshot(string ns) => _inner.DeleteHnswSnapshot(ns);
        public Task DeleteNamespaceAsync(string ns) => _inner.DeleteNamespaceAsync(ns);
        public void Flush() => _inner.Flush();
        public void Dispose() { }
    }

    [Fact]
    public void ExecuteCollapse_ReceiptSaveFailure_ArchivesNothing()
    {
        var failing = new ReceiptFailingProvider(_persistence);
        var scanner = new AccretionScanner(_index, failing);

        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var collapseId = scanner.ScanNamespace("test", tenantId: "").NewCollapses[0].CollapseId;

        // The receipt cannot be persisted, so NOTHING it was meant to cover may happen: no
        // member archived, no record left in memory to masquerade as durable, proposal intact.
        var result = scanner.ExecuteCollapse(
            collapseId, "Summary of a, b, c, d", new[] { 0.99f, 0.01f, 0f },
            _clusters, tenantId: "");

        Assert.StartsWith("Error:", result);
        Assert.Contains("receipt", result);
        Assert.Equal(1, scanner.PendingCount);
        Assert.Empty(scanner.GetCollapseHistory("test", tenantId: ""));
        foreach (var id in new[] { "a", "b", "c", "d" })
            Assert.Equal("ltm", _index.Get(id, "test", tenantId: "")!.LifecycleState);

        // Once the backend recovers, the same proposal executes to completion.
        failing.FailReceiptSaves = false;
        var retry = scanner.ExecuteCollapse(
            collapseId, "Summary of a, b, c, d", new[] { 0.99f, 0.01f, 0f },
            _clusters, tenantId: "");
        Assert.Contains("Collapsed 4 entries", retry);
        Assert.Equal(0, scanner.PendingCount);
    }

    [Fact]
    public void UndoCollapse_MembersClaimedByLaterCollapse_AreLeftToIt()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var collapseA = _scanner.ScanNamespace("test", tenantId: "").NewCollapses[0].CollapseId;
        var executeA = _scanner.ExecuteCollapse(
            collapseA, "Summary A", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");
        Assert.Contains("Collapsed 4 entries", executeA);

        // The members are resurrected and later collapsed AGAIN by a second proposal. Their
        // current "archived" now belongs to collapse B, not A. (The sleep keeps B's CollapsedAt
        // strictly later than A's on coarse clocks.)
        foreach (var id in new[] { "a", "b", "c", "d" })
            Assert.DoesNotContain("Error", _lifecycle.PromoteMemory(id, "ltm", "test", tenantId: ""));
        Thread.Sleep(50);
        var collapseB = _scanner.ScanNamespace("test", tenantId: "").NewCollapses[0].CollapseId;
        var executeB = _scanner.ExecuteCollapse(
            collapseB, "Summary B", new[] { 0.99f, 0.01f, 0f }, _clusters, tenantId: "");
        Assert.Contains("Collapsed 4 entries", executeB);
        Assert.Equal(2, _scanner.GetCollapseHistory("test", tenantId: "").Count);

        // Undoing the OLD collapse must not touch members a later collapse owns — restoring
        // them here would revert B's archives and corrupt B's receipt chain.
        var undoA = _scanner.UndoCollapse(collapseA, _lifecycle, _clusters, tenantId: "");
        Assert.StartsWith("Reversed", undoA);
        foreach (var id in new[] { "a", "b", "c", "d" })
            Assert.Equal("archived", _index.Get(id, "test", tenantId: "")!.LifecycleState);

        // B stays fully undoable afterwards, from its own receipt.
        var undoB = _scanner.UndoCollapse(collapseB, _lifecycle, _clusters, tenantId: "");
        Assert.StartsWith("Reversed", undoB);
        foreach (var id in new[] { "a", "b", "c", "d" })
            Assert.Equal("ltm", _index.Get(id, "test", tenantId: "")!.LifecycleState);
    }

    [Fact]
    public void ExecuteCollapse_CrashBeforeDebounce_ReceiptAloneRecoversCleanly()
    {
        // A LONG debounce turns this fixture into a crash simulator: nothing reaches disk
        // except what is flushed explicitly (the seeded baseline) or written SYNCHRONOUSLY —
        // which is exactly the write-ahead receipt at the durability point. The directory copy
        // below is therefore the true crash image: the receipt is in it; the cluster, the
        // summary, the archives and the member deletion are not, because they were still
        // queued behind sixty seconds of debounce when the "process died".
        var slowPath = Path.Combine(_testDataPath, "crash_source");
        var crashPath = Path.Combine(_testDataPath, "crash_image");

        using var slowPersistence = new PersistenceManager(slowPath, debounceMs: 60_000);
        using var index = new CognitiveIndex(slowPersistence);
        var clusters = new ClusterManager(index, slowPersistence);
        var lifecycle = new LifecycleEngine(index);
        var scanner = new AccretionScanner(index, slowPersistence);

        index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));
        slowPersistence.Flush(); // the durable baseline the crash survives

        var collapseId = scanner.ScanNamespace("test", tenantId: "").NewCollapses[0].CollapseId;
        index.Delete("d");
        var first = scanner.ExecuteCollapse(
            collapseId, "Summary of a, b, c, d", new[] { 0.99f, 0.01f, 0f },
            clusters, tenantId: "");
        Assert.StartsWith("Error:", first);

        // The crash: image the store as it stands, mid-debounce.
        Directory.CreateDirectory(crashPath);
        foreach (var file in Directory.GetFiles(slowPath))
            File.Copy(file, Path.Combine(crashPath, Path.GetFileName(file)));

        using var recoveredPersistence = new PersistenceManager(crashPath, debounceMs: 50);
        using var recoveredIndex = new CognitiveIndex(recoveredPersistence);
        var recoveredClusters = new ClusterManager(recoveredIndex, recoveredPersistence);
        var recoveredLifecycle = new LifecycleEngine(recoveredIndex);
        var recovered = new AccretionScanner(recoveredIndex, recoveredPersistence);

        var record = Assert.Single(recovered.GetCollapseHistory("test", tenantId: ""));
        var undo = recovered.UndoCollapse(record.CollapseId, recoveredLifecycle, recoveredClusters, tenantId: "");

        Assert.StartsWith("Reversed", undo);
        // Nothing the crash swallowed gets "restored" into a bogus state: the archives never
        // became durable, so the ownership-guarded undo touches none of the members and the
        // store reads exactly as it did before the collapse began.
        foreach (var id in new[] { "a", "b", "c", "d" })
            Assert.Equal("ltm", recoveredIndex.Get(id, "test", tenantId: "")!.LifecycleState);
        Assert.Null(recoveredClusters.GetCluster(record.ClusterId, tenantId: ""));
        Assert.Empty(recovered.GetCollapseHistory("test", tenantId: ""));
    }

    [Fact]
    public void DismissCollapse_AfterPartialExecution_RefusesUntilUndone()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var scanResult = _scanner.ScanNamespace("test", tenantId: "");
        var collapseId = scanResult.NewCollapses[0].CollapseId;

        _index.Delete("d");
        var first = _scanner.ExecuteCollapse(
            collapseId, "Summary of a, b, c, d", new[] { 0.99f, 0.01f, 0f },
            _clusters, tenantId: "");
        Assert.StartsWith("Error:", first);

        // The partial attempt owns archived members, a cluster, and a provisional record —
        // dismissal would orphan them all while erasing the proposal that can retry or reverse.
        var dismiss = _scanner.DismissCollapse(collapseId, tenantId: "", clusters: _clusters);
        Assert.StartsWith("Error:", dismiss);
        Assert.Contains("Undo it before dismissing", dismiss);
        Assert.Equal(1, _scanner.PendingCount);
    }

    [Fact]
    public void DismissCollapse_AllScreenedProposal_LeavesNoClusterShell()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var scanResult = _scanner.ScanNamespace("test", tenantId: "");
        var collapseId = scanResult.NewCollapses[0].CollapseId;

        foreach (var id in new[] { "a", "b", "c", "d" })
            _index.Upsert(new CognitiveEntry(id, new[] { 0f, 1f, 0f }, "other", lifecycleState: "ltm"));

        var result = _scanner.ExecuteCollapse(
            collapseId, "Summary of nothing", new[] { 0.99f, 0.01f, 0f },
            _clusters, tenantId: "");
        Assert.Contains("admitted no members", result);

        // The refused attempt cleans up after itself: the empty shell it created comes down
        // BEFORE its intent record retracts, so no side effect ever outlives the record that
        // names it. (Cluster ids carry a per-incarnation nonce and cannot be derived here —
        // the listing is the honest way to look.)
        Assert.Empty(_clusters.ListClusters("test", tenantId: ""));

        // Dismissal — the proposal's last exit — still succeeds, with nothing left to clean.
        var dismiss = _scanner.DismissCollapse(collapseId, tenantId: "", clusters: _clusters);
        Assert.StartsWith("Dismissed", dismiss);
        Assert.Empty(_clusters.ListClusters("test", tenantId: ""));
    }

    [Fact]
    public void DismissCollapse_MarksEntriesAsExcluded()
    {
        _index.Upsert(new CognitiveEntry("a", new[] { 1f, 0f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("b", new[] { 0.99f, 0.01f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("c", new[] { 0.98f, 0.02f, 0f }, "test", lifecycleState: "ltm"));
        _index.Upsert(new CognitiveEntry("d", new[] { 0.97f, 0.03f, 0f }, "test", lifecycleState: "ltm"));

        var scanResult = _scanner.ScanNamespace("test", tenantId: "");
        var collapseId = scanResult.NewCollapses[0].CollapseId;

        var dismissResult = _scanner.DismissCollapse(collapseId, tenantId: "", clusters: _clusters);
        Assert.Contains("Dismissed", dismissResult);

        // Pending count should be 0
        Assert.Equal(0, _scanner.PendingCount);

        // Subsequent scan should not detect these entries again
        var scanResult2 = _scanner.ScanNamespace("test", tenantId: "");
        Assert.Equal(0, scanResult2.ScannedCount);
        Assert.Empty(scanResult2.NewCollapses);
    }

    [Fact]
    public void DismissCollapse_NonExistent_ReturnsError()
    {
        var result = _scanner.DismissCollapse("nonexistent", tenantId: "", clusters: _clusters);
        Assert.StartsWith("Error:", result);
    }
}
