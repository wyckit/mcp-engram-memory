using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// The compare-and-swap that binds a caller's write to the occupation it validated. The
/// interesting case is the one a bare check-then-act loses: a competing write landing between
/// the caller's read and its install. OnBeforeConditionedUpsert makes that window addressable
/// rather than a matter of timing, so these are deterministic, not probabilistic.
/// </summary>
public class ConditionedUpsertTests : IDisposable
{
    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;

    public ConditionedUpsertTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"cond_upsert_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
    }

    private static CognitiveEntry Entry(string id, string text, string ns = "ns") =>
        new(id, new float[] { 1f, 0f, 0f }, ns, text, "note");

    [Fact]
    public void MatchingOccupation_Installs()
    {
        _index.Upsert(Entry("a", "original"));
        var read = _index.Get("a", "ns", tenantId: "")!;

        var ok = _index.UpsertIfRevision(Entry("a", "merged"), read.Revision);

        Assert.True(ok);
        Assert.Equal("merged", _index.Get("a", "ns", tenantId: "")!.Text);
    }

    [Fact]
    public void OccupationReplacedInTheWindow_Refuses_AndKeepsTheNewerRow()
    {
        _index.Upsert(Entry("a", "original"));
        var read = _index.Get("a", "ns", tenantId: "")!;

        // Land a competing write in the exact window between the caller's read and its
        // install. Without the compare, the stale snapshot below would publish over this.
        _index.OnBeforeConditionedUpsert = () =>
        {
            _index.OnBeforeConditionedUpsert = null; // once, or the nested upsert recurses
            _index.Upsert(Entry("a", "written by somebody else"));
        };

        var ok = _index.UpsertIfRevision(Entry("a", "merged from a stale read"), read.Revision);

        Assert.False(ok);
        Assert.Equal("written by somebody else", _index.Get("a", "ns", tenantId: "")!.Text);
    }

    [Fact]
    public void OccupationDeletedInTheWindow_Refuses_AndDoesNotResurrect()
    {
        _index.Upsert(Entry("a", "original"));
        var read = _index.Get("a", "ns", tenantId: "")!;

        _index.OnBeforeConditionedUpsert = () =>
        {
            _index.OnBeforeConditionedUpsert = null;
            _index.Delete("a", "ns", tenantId: "");
        };

        var ok = _index.UpsertIfRevision(Entry("a", "merged"), read.Revision);

        Assert.False(ok);
        Assert.Null(_index.Get("a", "ns", tenantId: ""));
    }

    [Fact]
    public void AbsentSlot_Refuses()
    {
        Assert.False(_index.UpsertIfRevision(Entry("never-stored", "x"), onlyIfRevision: 1));
        Assert.Null(_index.Get("never-stored", "ns", tenantId: ""));
    }

    [Fact]
    public void ARefusal_LeavesOccupancyAndContentUntouched()
    {
        _index.Upsert(Entry("a", "original"));
        var read = _index.Get("a", "ns", tenantId: "")!;
        var occupancyBefore = _index.OccupancyRevisionFor("ns", "");

        Assert.False(_index.UpsertIfRevision(Entry("a", "merged"), read.Revision + 999));

        Assert.Equal(occupancyBefore, _index.OccupancyRevisionFor("ns", ""));
        Assert.Equal("original", _index.Get("a", "ns", tenantId: "")!.Text);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }
}
