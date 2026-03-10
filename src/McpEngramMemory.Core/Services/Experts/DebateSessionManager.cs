namespace McpEngramMemory.Core.Services.Experts;

/// <summary>
/// Volatile in-memory session state for debate panels.
/// Maps integer aliases to actual entry UUIDs per session.
/// Auto-purges sessions after a configurable TTL (default: 1 hour).
/// </summary>
public sealed class DebateSessionManager : IDisposable
{
    private readonly Dictionary<string, DebateSession> _sessions = new();
    private readonly object _lock = new();
    private readonly Timer _purgeTimer;
    private readonly TimeSpan _ttl;

    public DebateSessionManager(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromHours(1);
        // Purge expired sessions every 5 minutes
        _purgeTimer = new Timer(_ => PurgeExpired(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Register a new node alias in a session. Returns the assigned integer alias.
    /// Creates the session if it doesn't exist.
    /// </summary>
    public int RegisterNode(string sessionId, string entryId)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                session = new DebateSession();
                _sessions[sessionId] = session;
            }

            session.Touch();
            return session.AddNode(entryId);
        }
    }

    /// <summary>
    /// Resolve an integer alias to the actual entry UUID for a given session.
    /// </summary>
    public string? ResolveAlias(string sessionId, int alias)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                return null;

            session.Touch();
            return session.GetEntryId(alias);
        }
    }

    /// <summary>
    /// Get all entry IDs registered in a session.
    /// </summary>
    public IReadOnlyList<string> GetAllEntryIds(string sessionId)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                return Array.Empty<string>();

            session.Touch();
            return session.GetAllEntryIds();
        }
    }

    /// <summary>
    /// Get the debate namespace for a session (deterministic: "active-debate-{sessionId}").
    /// </summary>
    public static string GetDebateNamespace(string sessionId)
        => $"active-debate-{sessionId}";

    /// <summary>
    /// Remove a session (called after resolve_debate or on TTL expiry).
    /// </summary>
    public bool RemoveSession(string sessionId)
    {
        lock (_lock)
        {
            return _sessions.Remove(sessionId);
        }
    }

    /// <summary>
    /// Check if a session exists.
    /// </summary>
    public bool HasSession(string sessionId)
    {
        lock (_lock)
        {
            return _sessions.ContainsKey(sessionId);
        }
    }

    private void PurgeExpired()
    {
        lock (_lock)
        {
            var expired = new List<string>();
            var now = DateTimeOffset.UtcNow;

            foreach (var (id, session) in _sessions)
            {
                if (now - session.LastAccessed > _ttl)
                    expired.Add(id);
            }

            foreach (var id in expired)
                _sessions.Remove(id);
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
