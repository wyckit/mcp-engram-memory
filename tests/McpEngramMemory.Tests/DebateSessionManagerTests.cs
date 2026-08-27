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
        int alias1 = _manager.RegisterNode("", "session-1", "entry-a");
        int alias2 = _manager.RegisterNode("", "session-1", "entry-b");
        int alias3 = _manager.RegisterNode("", "session-1", "entry-c");

        Assert.Equal(1, alias1);
        Assert.Equal(2, alias2);
        Assert.Equal(3, alias3);
    }

    [Fact]
    public void RegisterNode_SameEntry_ReturnsSameAlias()
    {
        int alias1 = _manager.RegisterNode("", "session-1", "entry-a");
        int alias2 = _manager.RegisterNode("", "session-1", "entry-a");

        Assert.Equal(alias1, alias2);
    }

    [Fact]
    public void RegisterNode_DifferentSessions_IndependentAliases()
    {
        int alias1 = _manager.RegisterNode("", "session-1", "entry-a");
        int alias2 = _manager.RegisterNode("", "session-2", "entry-b");

        Assert.Equal(1, alias1);
        Assert.Equal(1, alias2); // Different session, alias restarts at 1
    }

    [Fact]
    public void ResolveAlias_ValidAlias_ReturnsEntryId()
    {
        _manager.RegisterNode("", "session-1", "entry-abc");

        var resolved = _manager.ResolveAlias("", "session-1", 1);

        Assert.Equal("entry-abc", resolved);
    }

    [Fact]
    public void ResolveAlias_InvalidAlias_ReturnsNull()
    {
        _manager.RegisterNode("", "session-1", "entry-a");

        Assert.Null(_manager.ResolveAlias("", "session-1", 99));
    }

    [Fact]
    public void ResolveAlias_UnknownSession_ReturnsNull()
    {
        Assert.Null(_manager.ResolveAlias("", "nonexistent", 1));
    }

    [Fact]
    public void GetAllEntryIds_ReturnsAllRegistered()
    {
        _manager.RegisterNode("", "session-1", "entry-a");
        _manager.RegisterNode("", "session-1", "entry-b");
        _manager.RegisterNode("", "session-1", "entry-c");

        var allIds = _manager.GetAllEntryIds("", "session-1");

        Assert.Equal(3, allIds.Count);
        Assert.Contains("entry-a", allIds);
        Assert.Contains("entry-b", allIds);
        Assert.Contains("entry-c", allIds);
    }

    [Fact]
    public void GetAllEntryIds_UnknownSession_ReturnsEmpty()
    {
        var allIds = _manager.GetAllEntryIds("", "nonexistent");
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
        _manager.RegisterNode("", "session-1", "entry-a");

        Assert.True(_manager.RemoveSession("", "session-1"));
        Assert.False(_manager.HasSession("", "session-1"));
    }

    [Fact]
    public void RemoveSession_UnknownSession_ReturnsFalse()
    {
        Assert.False(_manager.RemoveSession("", "nonexistent"));
    }

    [Fact]
    public void HasSession_AfterRegistration_ReturnsTrue()
    {
        _manager.RegisterNode("", "session-1", "entry-a");
        Assert.True(_manager.HasSession("", "session-1"));
    }

    [Fact]
    public void HasSession_NoRegistration_ReturnsFalse()
    {
        Assert.False(_manager.HasSession("", "nonexistent"));
    }

    [Fact]
    public void ResolveAlias_AfterRemoveSession_ReturnsNull()
    {
        _manager.RegisterNode("", "session-1", "entry-a");
        _manager.RemoveSession("", "session-1");

        Assert.Null(_manager.ResolveAlias("", "session-1", 1));
    }

    [Fact]
    public void TryCreateSession_NewSession_ReturnsTrue()
    {
        Assert.True(_manager.TryCreateSession("", "session-new"));
        Assert.True(_manager.HasSession("", "session-new"));
    }

    [Fact]
    public void TryCreateSession_ExistingSession_ReturnsFalse()
    {
        _manager.TryCreateSession("", "session-dup");
        Assert.False(_manager.TryCreateSession("", "session-dup"));
    }

    [Fact]
    public void TryCreateSession_ThenRegisterNode_Works()
    {
        _manager.TryCreateSession("", "session-atomic");
        int alias = _manager.RegisterNode("", "session-atomic", "entry-x");
        Assert.Equal(1, alias);
        Assert.Equal("entry-x", _manager.ResolveAlias("", "session-atomic", 1));
    }

    // ---------------------------------------------------------------------
    // Tenant isolation (RC2: a session's identity is (tenant, sessionId), never
    // the bare caller-supplied session id).
    //
    // NARROW GUARD, NOT THE REPRODUCTION. These two are written against the
    // tenant-aware signatures and therefore cannot compile against the
    // pre-fix manager at all. The genuine before/after reproduction is
    // DebateToolsTests.ResolveDebate_TenantCannotHijackAnotherTenantsSession,
    // which drives the MCP tools only and so compiles unchanged either side of
    // the fix.
    // ---------------------------------------------------------------------

    [Fact]
    public void Sessions_WithSameIdInDifferentTenants_AreIndependent()
    {
        // Both tenants pick the same caller-supplied session id. Pre-fix that id
        // was the whole key, so these two shared one alias table.
        const string sharedSessionId = "session-collide";

        int aliasA = _manager.RegisterNode("tenant-a", sharedSessionId, "entry-tenant-a");
        int aliasB = _manager.RegisterNode("tenant-b", sharedSessionId, "entry-tenant-b");

        // Each tenant's alias counter restarts at 1 — b did not continue a's sequence.
        Assert.Equal(1, aliasA);
        Assert.Equal(1, aliasB);

        // Alias 1 resolves to each tenant's own entry, never the other's.
        Assert.Equal("entry-tenant-a", _manager.ResolveAlias("tenant-a", sharedSessionId, 1));
        Assert.Equal("entry-tenant-b", _manager.ResolveAlias("tenant-b", sharedSessionId, 1));

        // Neither tenant can enumerate the other's entry ids.
        var idsA = _manager.GetAllEntryIds("tenant-a", sharedSessionId);
        var idsB = _manager.GetAllEntryIds("tenant-b", sharedSessionId);
        Assert.Equal(new[] { "entry-tenant-a" }, idsA);
        Assert.Equal(new[] { "entry-tenant-b" }, idsB);

        // The legacy tenant is a third, independent partition — it must not see
        // either identified tenant's session.
        Assert.False(_manager.HasSession("", sharedSessionId));

        // Destroying one tenant's session leaves the other's intact.
        Assert.True(_manager.RemoveSession("tenant-a", sharedSessionId));
        Assert.False(_manager.HasSession("tenant-a", sharedSessionId));
        Assert.True(_manager.HasSession("tenant-b", sharedSessionId));
        Assert.Equal("entry-tenant-b", _manager.ResolveAlias("tenant-b", sharedSessionId, 1));

        // OVER-CORRECTION CONTROL: isolation did not degrade into "nobody can
        // create this id twice" — tenant-a can create the same id again now that
        // its own session is gone, and that is still a fresh session.
        Assert.True(_manager.TryCreateSession("tenant-a", sharedSessionId));
        // ...while tenant-b's live session still refuses a duplicate create.
        Assert.False(_manager.TryCreateSession("tenant-b", sharedSessionId));
    }

    [Fact]
    public void LegacyTenant_NullEmptyAndWhitespace_ResolveToTheSameSession()
    {
        // RC3: "" is the legacy partition, not a sentinel for "no tenancy". The
        // manager routes every key through Tenancy.Normalize, so null, "" and a
        // padded/whitespace value must all land on one legacy session rather than
        // splitting a single-agent deployment across three keys.
        const string sessionId = "session-legacy";

        int alias = _manager.RegisterNode(null!, sessionId, "entry-legacy");
        Assert.Equal(1, alias);

        Assert.True(_manager.HasSession(null!, sessionId));
        Assert.True(_manager.HasSession("", sessionId));
        Assert.True(_manager.HasSession("   ", sessionId));

        Assert.Equal("entry-legacy", _manager.ResolveAlias(null!, sessionId, 1));
        Assert.Equal("entry-legacy", _manager.ResolveAlias("", sessionId, 1));
        Assert.Equal("entry-legacy", _manager.ResolveAlias("   ", sessionId, 1));

        Assert.Equal(new[] { "entry-legacy" }, _manager.GetAllEntryIds("", sessionId));
        Assert.Equal(new[] { "entry-legacy" }, _manager.GetAllEntryIds("   ", sessionId));

        // A second registration through a differently-spelled legacy tenant hits the
        // same alias table, so the same entry keeps its existing alias instead of
        // being handed a fresh one in a forked session.
        Assert.Equal(1, _manager.RegisterNode("   ", sessionId, "entry-legacy"));
        Assert.Equal(2, _manager.RegisterNode("", sessionId, "entry-legacy-2"));
        Assert.Equal("entry-legacy-2", _manager.ResolveAlias(null!, sessionId, 2));

        // Removing through one legacy spelling removes the one legacy session.
        Assert.True(_manager.RemoveSession("   ", sessionId));
        Assert.False(_manager.HasSession(null!, sessionId));
        Assert.False(_manager.HasSession("", sessionId));

        // The legacy tenant is still its own partition, distinct from any
        // identified tenant that happens to reuse the id.
        Assert.True(_manager.TryCreateSession("tenant-x", sessionId));
        Assert.False(_manager.HasSession("", sessionId));
    }
}
