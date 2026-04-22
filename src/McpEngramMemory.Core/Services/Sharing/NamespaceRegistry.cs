using System.Collections.Concurrent;
using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Sharing;

/// <summary>
/// Manages namespace ownership and sharing permissions for multi-agent memory sharing.
/// Stores permission data in the _system_sharing namespace of CognitiveIndex.
/// When agent identity is "default" (not explicitly set), all namespaces are accessible.
/// </summary>
public sealed class NamespaceRegistry
{
    /// <summary>Hidden system namespace for sharing metadata.</summary>
    public const string SystemNamespace = "_system_sharing";

    private const string PermissionCategory = "ns-permission";

    private readonly CognitiveIndex _index;
    private readonly IEmbeddingService _embedding;

    // Per-namespace locks serialize read-modify-write on permission entries
    // (Share/Unshare/EnsureOwnership) so concurrent grants don't overwrite each
    // other. Keyed by target namespace — grants to different namespaces stay
    // parallel; grants to the same namespace serialize through one monitor.
    private readonly ConcurrentDictionary<string, object> _permissionLocks = new();

    private object LockFor(string ns) =>
        _permissionLocks.GetOrAdd(ns, _ => new object());

    public NamespaceRegistry(CognitiveIndex index, IEmbeddingService embedding)
    {
        _index = index;
        _embedding = embedding;
    }

    /// <summary>
    /// Grant an agent access to a namespace. Creates the permission entry if it doesn't exist.
    /// Thread-safe: concurrent Share calls to the same namespace are serialized by a per-namespace
    /// monitor, so grants cannot overwrite one another. Calls to different namespaces stay parallel.
    /// </summary>
    public ShareResult Share(string ns, string ownerAgentId, string targetAgentId, string accessLevel)
    {
        if (accessLevel is not ("read" or "write"))
            return new ShareResult("error", ns, targetAgentId, accessLevel);

        lock (LockFor(ns))
        {
            var permission = GetOrCreatePermissionUnlocked(ns, ownerAgentId);

            // Check ownership (default agent bypasses ownership checks for backward compat)
            if (permission.Owner != ownerAgentId && ownerAgentId != AgentIdentity.DefaultAgentId)
                return new ShareResult("error_not_owner", ns, targetAgentId, accessLevel);

            // Update sharing list
            var grants = permission.SharedWith.ToList();
            var existing = grants.FindIndex(g => g.AgentId == targetAgentId);
            if (existing >= 0)
                grants[existing] = new ShareGrant(targetAgentId, accessLevel);
            else
                grants.Add(new ShareGrant(targetAgentId, accessLevel));

            SavePermission(ns, permission.Owner, grants);
            return new ShareResult("shared", ns, targetAgentId, accessLevel);
        }
    }

    /// <summary>
    /// Revoke an agent's access to a namespace.
    /// Thread-safe under the same per-namespace serialization as <see cref="Share"/>.
    /// </summary>
    public ShareResult Unshare(string ns, string ownerAgentId, string targetAgentId)
    {
        lock (LockFor(ns))
        {
            var permission = GetPermission(ns);
            if (permission is null)
                return new ShareResult("error_not_found", ns, targetAgentId, "none");

            if (permission.Owner != ownerAgentId)
                return new ShareResult("error_not_owner", ns, targetAgentId, "none");

            var grants = permission.SharedWith.Where(g => g.AgentId != targetAgentId).ToList();
            SavePermission(ns, permission.Owner, grants);
            return new ShareResult("unshared", ns, targetAgentId, "none");
        }
    }

    /// <summary>
    /// Check if an agent has at least the specified access level to a namespace.
    /// Default agent always has access (backward compatible).
    /// </summary>
    public bool HasAccess(string agentId, string ns, string requiredLevel = "read")
    {
        // Default agent has unrestricted access (backward compatible)
        if (agentId == AgentIdentity.DefaultAgentId)
            return true;

        // System namespaces are always accessible
        if (ns.StartsWith('_'))
            return true;

        // Owner always has full access
        var permission = GetPermission(ns);
        if (permission is null)
            return true; // Unregistered namespaces are open (backward compat)

        if (permission.Owner == agentId)
            return true;

        // Check shared grants
        var grant = permission.SharedWith.FirstOrDefault(g => g.AgentId == agentId);
        if (grant is null)
            return false;

        return requiredLevel == "read" || grant.AccessLevel == "write";
    }

    /// <summary>
    /// Get all namespaces accessible to an agent (owned + shared).
    /// </summary>
    public WhoAmIResult GetAccessibleNamespaces(string agentId)
    {
        var allPermissions = _index.GetAllInNamespace(SystemNamespace)
            .Where(e => e.Category == PermissionCategory)
            .ToList();

        var owned = new List<string>();
        var shared = new List<NamespacePermission>();

        foreach (var entry in allPermissions)
        {
            var owner = entry.Metadata.GetValueOrDefault("owner") ?? AgentIdentity.DefaultAgentId;
            var ns = entry.Metadata.GetValueOrDefault("ns") ?? entry.Id;

            if (owner == agentId)
            {
                owned.Add(ns);
            }
            else
            {
                var grantsStr = entry.Metadata.GetValueOrDefault("grants") ?? "";
                var grants = ParseGrants(grantsStr);
                if (grants.Any(g => g.AgentId == agentId))
                {
                    shared.Add(new NamespacePermission(ns, owner, grants));
                }
            }
        }

        // If default agent, also include all persisted namespaces
        if (agentId == AgentIdentity.DefaultAgentId)
        {
            var registeredNs = allPermissions.Select(e => e.Metadata.GetValueOrDefault("ns") ?? e.Id).ToHashSet();
            // All non-system namespaces not in registry are implicitly owned by default
            var allNs = _index.GetAll()
                .Select(e => e.Ns)
                .Where(n => !n.StartsWith('_'))
                .Distinct()
                .Where(n => !registeredNs.Contains(n));
            owned.AddRange(allNs);
        }

        return new WhoAmIResult(agentId, owned, shared);
    }

    /// <summary>
    /// Register namespace ownership (called implicitly on first write).
    /// Uses double-checked locking so the registered-path is lock-free; concurrent callers that
    /// race to register the same namespace are serialized per-namespace and only the first write
    /// wins (subsequent callers become no-ops).
    /// </summary>
    public void EnsureOwnership(string ns, string agentId)
    {
        if (ns.StartsWith('_')) return; // System namespaces not tracked

        // Double-checked: fast path avoids acquiring the per-ns lock once registered.
        if (GetPermission(ns) is not null) return;

        lock (LockFor(ns))
        {
            if (GetPermission(ns) is not null) return; // Another thread registered first
            SavePermission(ns, agentId, Array.Empty<ShareGrant>());
        }
    }

    private NamespacePermission? GetPermission(string ns)
    {
        var entryId = $"perm_{ns}";
        var entry = _index.Get(entryId, SystemNamespace);
        if (entry is null) return null;

        var owner = entry.Metadata.GetValueOrDefault("owner") ?? AgentIdentity.DefaultAgentId;
        var grantsStr = entry.Metadata.GetValueOrDefault("grants") ?? "";
        return new NamespacePermission(ns, owner, ParseGrants(grantsStr));
    }

    // Caller must hold LockFor(ns). Used by Share when the per-ns lock is already held.
    private NamespacePermission GetOrCreatePermissionUnlocked(string ns, string ownerAgentId)
    {
        var existing = GetPermission(ns);
        if (existing is not null) return existing;

        SavePermission(ns, ownerAgentId, Array.Empty<ShareGrant>());
        return new NamespacePermission(ns, ownerAgentId, Array.Empty<ShareGrant>());
    }

    private void SavePermission(string ns, string owner, IReadOnlyList<ShareGrant> grants)
    {
        var entryId = $"perm_{ns}";
        var grantsStr = string.Join(";", grants.Select(g => $"{g.AgentId}:{g.AccessLevel}"));
        var vector = _embedding.Embed($"namespace permission {ns}");

        var entry = new CognitiveEntry(
            id: entryId,
            vector: vector,
            ns: SystemNamespace,
            text: $"Namespace '{ns}' owned by '{owner}'",
            category: PermissionCategory,
            metadata: new Dictionary<string, string>
            {
                ["ns"] = ns,
                ["owner"] = owner,
                ["grants"] = grantsStr
            },
            lifecycleState: "ltm")
        {
            IsSummaryNode = true
        };

        _index.Upsert(entry);
    }

    private static IReadOnlyList<ShareGrant> ParseGrants(string grantsStr)
    {
        if (string.IsNullOrEmpty(grantsStr))
            return Array.Empty<ShareGrant>();

        return grantsStr.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(g =>
            {
                var parts = g.Split(':', 2);
                return parts.Length == 2
                    ? new ShareGrant(parts[0], parts[1])
                    : new ShareGrant(parts[0], "read");
            })
            .ToList();
    }
}
