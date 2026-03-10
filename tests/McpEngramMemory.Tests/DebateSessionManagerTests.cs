using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Experts;

namespace McpEngramMemory.Tests;

public class DebateSessionManagerTests : IDisposable
{
    private readonly DebateSessionManager _manager;

    public DebateSessionManagerTests()
    {
        _manager = new DebateSessionManager(ttl: TimeSpan.FromHours(1));
    }

    public void Dispose()
    {
        _manager.Dispose();
    }

    [Fact]
    public void RegisterNode_AssignsSequentialAliases()
    {
        int alias1 = _manager.RegisterNode("session-1", "entry-a");
        int alias2 = _manager.RegisterNode("session-1", "entry-b");
        int alias3 = _manager.RegisterNode("session-1", "entry-c");

        Assert.Equal(1, alias1);
        Assert.Equal(2, alias2);
        Assert.Equal(3, alias3);
    }

    [Fact]
    public void RegisterNode_SameEntry_ReturnsSameAlias()
    {
        int alias1 = _manager.RegisterNode("session-1", "entry-a");
        int alias2 = _manager.RegisterNode("session-1", "entry-a");

        Assert.Equal(alias1, alias2);
    }

    [Fact]
    public void RegisterNode_DifferentSessions_IndependentAliases()
    {
        int alias1 = _manager.RegisterNode("session-1", "entry-a");
        int alias2 = _manager.RegisterNode("session-2", "entry-b");

        Assert.Equal(1, alias1);
        Assert.Equal(1, alias2); // Different session, alias restarts at 1
    }

    [Fact]
    public void ResolveAlias_ValidAlias_ReturnsEntryId()
    {
        _manager.RegisterNode("session-1", "entry-abc");

        var resolved = _manager.ResolveAlias("session-1", 1);

        Assert.Equal("entry-abc", resolved);
    }

    [Fact]
    public void ResolveAlias_InvalidAlias_ReturnsNull()
    {
        _manager.RegisterNode("session-1", "entry-a");

        Assert.Null(_manager.ResolveAlias("session-1", 99));
    }

    [Fact]
    public void ResolveAlias_UnknownSession_ReturnsNull()
    {
        Assert.Null(_manager.ResolveAlias("nonexistent", 1));
    }

    [Fact]
    public void GetAllEntryIds_ReturnsAllRegistered()
    {
        _manager.RegisterNode("session-1", "entry-a");
        _manager.RegisterNode("session-1", "entry-b");
        _manager.RegisterNode("session-1", "entry-c");

        var allIds = _manager.GetAllEntryIds("session-1");

        Assert.Equal(3, allIds.Count);
        Assert.Contains("entry-a", allIds);
        Assert.Contains("entry-b", allIds);
        Assert.Contains("entry-c", allIds);
    }

    [Fact]
    public void GetAllEntryIds_UnknownSession_ReturnsEmpty()
    {
        var allIds = _manager.GetAllEntryIds("nonexistent");
        Assert.Empty(allIds);
    }

    [Fact]
    public void GetDebateNamespace_ReturnsDeterministicName()
    {
        string ns = DebateSessionManager.GetDebateNamespace("debate-101");
        Assert.Equal("active-debate-debate-101", ns);
    }

    [Fact]
    public void RemoveSession_ExistingSession_ReturnsTrue()
    {
        _manager.RegisterNode("session-1", "entry-a");

        Assert.True(_manager.RemoveSession("session-1"));
        Assert.False(_manager.HasSession("session-1"));
    }

    [Fact]
    public void RemoveSession_UnknownSession_ReturnsFalse()
    {
        Assert.False(_manager.RemoveSession("nonexistent"));
    }

    [Fact]
    public void HasSession_AfterRegistration_ReturnsTrue()
    {
        _manager.RegisterNode("session-1", "entry-a");
        Assert.True(_manager.HasSession("session-1"));
    }

    [Fact]
    public void HasSession_NoRegistration_ReturnsFalse()
    {
        Assert.False(_manager.HasSession("nonexistent"));
    }

    [Fact]
    public void ResolveAlias_AfterRemoveSession_ReturnsNull()
    {
        _manager.RegisterNode("session-1", "entry-a");
        _manager.RemoveSession("session-1");

        Assert.Null(_manager.ResolveAlias("session-1", 1));
    }
}
