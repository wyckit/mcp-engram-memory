using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Experts;

/// <summary>
/// Volatile in-memory session state for debate panels.
/// Maps integer aliases to actual entry UUIDs per session.
/// Auto-purges sessions after a configurable TTL (default: 1 hour).
///
/// Tenant isolation: sessions are keyed by <c>(tenant, sessionId)</c>, matching the house
/// convention used by <see cref="Graph.KnowledgeGraph"/> and <see cref="Intelligence.ClusterManager"/>.
/// A session id is caller-supplied, so without the tenant component two tenants that happen to pick
/// the same id share one alias table — which would let one tenant read the other's entry ids and
/// destroy its session. The tenant is normalized (never concatenated) so no tenant value can forge
/// another pair's key by embedding a delimiter, and the legacy tenant <c>""</c> keeps its own
/// partition exactly as before.
/// </summary>
public sealed class DebateSessionManager : IDisposable
{
    private readonly Dictionary<(string Tenant, string SessionId), DebateSession> _sessions = new();
    private readonly object _lock = new();
    private readonly Timer _purgeTimer;
    private readonly TimeSpan _ttl;

    public DebateSessionManager(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromHours(1);
        // Purge expired sessions every 5 minutes
        _purgeTimer = new Timer(_ => PurgeExpired(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    // Every session-keyed member routes through here, so null/whitespace/untrimmed tenant values can
    // never split one tenant's sessions across two keys.
    private static (string Tenant, string SessionId) Key(string tenantId, string sessionId)
        => (Tenancy.Normalize(tenantId), sessionId);

    /// <summary>
    /// Register a new node alias in a session. Returns the assigned integer alias.
    /// Creates the session if it doesn't exist.
    /// </summary>
    public int RegisterNode(string tenantId, string sessionId, string entryId)
    {
        var key = Key(tenantId, sessionId);
        lock (_lock)
        {
            if (!_sessions.TryGetValue(key, out var session))
            {
                session = new DebateSession();
                _sessions[key] = session;
            }

            session.Touch();
            return session.AddNode(entryId);
        }
    }

    /// <summary>
    /// Resolve an integer alias to the actual entry UUID for a given session.
    /// </summary>
    public string? ResolveAlias(string tenantId, string sessionId, int alias)
    {
        var key = Key(tenantId, sessionId);
        lock (_lock)
        {
            if (!_sessions.TryGetValue(key, out var session))
                return null;

            session.Touch();
            return session.GetEntryId(alias);
        }
    }

    /// <summary>
    /// Get all entry IDs registered in a session.
    /// </summary>
    public IReadOnlyList<string> GetAllEntryIds(string tenantId, string sessionId)
    {
        var key = Key(tenantId, sessionId);
        lock (_lock)
        {
            if (!_sessions.TryGetValue(key, out var session))
                return Array.Empty<string>();

            session.Touch();
            return session.GetAllEntryIds();
        }
    }

    /// <summary>
    /// Get the debate namespace for a session (deterministic: "active-debate-{sessionId}").
    /// Deliberately tenant-free: storage already partitions a namespace by tenant, so folding the
    /// tenant into the name here would fork the on-disk layout, break the "active-debate-" prefix
    /// match in the purge tool, and change persisted permission record ids.
    /// </summary>
    public static string GetDebateNamespace(string sessionId)
        => $"active-debate-{sessionId}";

    /// <summary>
    /// Remove a session (called after resolve_debate or on TTL expiry).
    /// </summary>
    public bool RemoveSession(string tenantId, string sessionId)
    {
        var key = Key(tenantId, sessionId);
        lock (_lock)
        {
            return _sessions.Remove(key);
        }
    }

    /// <summary>
    /// Check if a session exists.
    /// </summary>
    public bool HasSession(string tenantId, string sessionId)
    {
        var key = Key(tenantId, sessionId);
        lock (_lock)
        {
            return _sessions.ContainsKey(key);
        }
    }

    /// <summary>
    /// Atomically check-and-create a session. Returns true if a new session was created,
    /// false if the session already exists. Eliminates TOCTOU race between HasSession + RegisterNode.
    /// </summary>
    public bool TryCreateSession(string tenantId, string sessionId)
    {
        var key = Key(tenantId, sessionId);
        lock (_lock)
        {
            if (_sessions.ContainsKey(key))
                return false;
            _sessions[key] = new DebateSession();
            return true;
        }
    }

    // The TTL sweep is deliberately tenant-agnostic: expiry is a property of the session's own
    // last-access time, so it walks every (tenant, sessionId) pair.
    private void PurgeExpired()
    {
        lock (_lock)
        {
            var expired = new List<(string Tenant, string SessionId)>();
            var now = DateTimeOffset.UtcNow;

            foreach (var (key, session) in _sessions)
            {
                if (now - session.LastAccessed > _ttl)
                    expired.Add(key);
            }

            foreach (var key in expired)
                _sessions.Remove(key);
        }
    }

    public void Dispose()
    {
        _purgeTimer.Dispose();
    }

    /// <summary>
    /// Internal session state: maps integer aliases to entry UUIDs.
    /// </summary>
    private sealed class DebateSession
    {
        private readonly Dictionary<int, string> _aliasToId = new();
        private readonly Dictionary<string, int> _idToAlias = new();
        private int _nextAlias = 1;

        public DateTimeOffset LastAccessed { get; private set; } = DateTimeOffset.UtcNow;

        public void Touch() => LastAccessed = DateTimeOffset.UtcNow;

        public int AddNode(string entryId)
        {
            // If already registered, return existing alias
            if (_idToAlias.TryGetValue(entryId, out int existing))
                return existing;

            int alias = _nextAlias++;
            _aliasToId[alias] = entryId;
            _idToAlias[entryId] = alias;
            return alias;
        }

        public string? GetEntryId(int alias)
            => _aliasToId.TryGetValue(alias, out var id) ? id : null;

        public IReadOnlyList<string> GetAllEntryIds()
            => _aliasToId.Values.ToList();
    }
}
