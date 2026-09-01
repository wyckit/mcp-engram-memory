using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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

    private object LockFor(string ns, string tenantId) =>
        _permissionLocks.GetOrAdd(PermissionEntryId(ns, tenantId), _ => new object());

    public NamespaceRegistry(CognitiveIndex index, IEmbeddingService embedding)
    {
        _index = index;
        _embedding = embedding;
    }

    /// <summary>
    /// Grant an agent access to a namespace. An unregistered namespace resolves through
    /// <see cref="RegisterLegacyOwnerUnlocked"/>: the legacy default agent becomes its owner (it
    /// already has unrestricted access, so this only materializes the record grants hang off),
    /// while an identified principal gets <c>error_not_found</c> rather than taking it over.
    /// System namespaces are never shareable — <see cref="HasAccess"/> refuses them ahead of any
    /// permission lookup, so a grant on one would be inert.
    ///
    /// Thread-safe: concurrent Share calls to the same namespace are serialized by a per-namespace
    /// monitor, so grants cannot overwrite one another. Calls to different namespaces stay parallel.
    /// </summary>
    public ShareResult Share(string ns, string ownerAgentId, string targetAgentId, string accessLevel,
        string tenantId)
    {
        if (accessLevel is not ("read" or "write"))
            return new ShareResult("error", ns, targetAgentId, accessLevel);

        if (ns.StartsWith('_'))
            return new ShareResult("error_not_found", ns, targetAgentId, accessLevel);

        lock (LockFor(ns, tenantId: tenantId))
        {
            var permission = GetPermission(ns, tenantId)
                             ?? RegisterLegacyOwnerUnlocked(ns, ownerAgentId, tenantId);
            if (permission is null)
                return new ShareResult("error_not_found", ns, targetAgentId, accessLevel);

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

            SavePermission(ns, permission.Owner, grants, tenantId);
            return new ShareResult("shared", ns, targetAgentId, accessLevel);
        }
    }

    /// <summary>
    /// Revoke an agent's access to a namespace.
    /// Thread-safe under the same per-namespace serialization as <see cref="Share"/>.
    /// </summary>
    public ShareResult Unshare(string ns, string ownerAgentId, string targetAgentId, string tenantId)
    {
        lock (LockFor(ns, tenantId: tenantId))
        {
            var permission = GetPermission(ns, tenantId);
            if (permission is null)
                return new ShareResult("error_not_found", ns, targetAgentId, "none");

            if (permission.Owner != ownerAgentId)
                return new ShareResult("error_not_owner", ns, targetAgentId, "none");

            var grants = permission.SharedWith.Where(g => g.AgentId != targetAgentId).ToList();
            SavePermission(ns, permission.Owner, grants, tenantId);
            return new ShareResult("unshared", ns, targetAgentId, "none");
        }
    }

    /// <summary>
    /// Check if an agent has at least the specified access level to a namespace.
    /// Default agent always has access (backward compatible).
    ///
    /// Both <c>requiredLevel</c> and <c>tenantId</c> are deliberately required: with the old
    /// defaults, <c>HasAccess(agent, ns, "write")</c> could silently bind "write" into the tenant
    /// slot and fall back to a read-level check — an ACL predicate both mis-partitioned and
    /// weakened. Pass "" for the legacy tenant partition.
    /// </summary>
    public bool HasAccess(string agentId, string ns, string requiredLevel, string tenantId)
    {
        // Default agent has unrestricted access (backward compatible)
        if (agentId == AgentIdentity.DefaultAgentId && Tenancy.Normalize(tenantId).Length == 0)
            return true;

        // System namespaces contain control-plane data. Internal services access them
        // through dedicated APIs; generic memory tools must not expose them to an
        // identified principal. The legacy default remains explicitly unisolated above.
        if (ns.StartsWith('_'))
            return false;

        // Owner always has full access
        var permission = GetPermission(ns, tenantId);
        if (permission is null)
        {
            // Identified principals never inherit an unregistered namespace. A write may
            // atomically claim only a genuinely empty namespace; pre-existing legacy content
            // must be assigned by an administrator/migration, preventing first-writer takeover.
            return requiredLevel == "write" && TryClaimEmptyNamespace(ns, agentId, tenantId);
        }

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
    public WhoAmIResult GetAccessibleNamespaces(string agentId, string tenantId)
    {
        tenantId = Tenancy.Normalize(tenantId);
        var allPermissions = _index.GetAllInNamespace(SystemNamespace)
            .Where(e => e.Category == PermissionCategory)
            .Where(e => Tenancy.Normalize(e.Metadata.GetValueOrDefault("tenantId")) == tenantId)
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
        if (agentId == AgentIdentity.DefaultAgentId && tenantId.Length == 0)
        {
            var registeredNs = allPermissions.Select(e => e.Metadata.GetValueOrDefault("ns") ?? e.Id).ToHashSet();
            // All non-system namespaces not in registry are implicitly owned by default
            var allNs = _index.GetAllForTenant(tenantId)
                .Select(e => e.Ns)
                .Where(n => !n.StartsWith('_'))
                .Distinct()
                .Where(n => !registeredNs.Contains(n));
            owned.AddRange(allNs);
        }

        return new WhoAmIResult(agentId, owned, shared);
    }

    /// <summary>
    /// Claim ownership of <paramref name="ns"/> for <paramref name="agentId"/> on write, if it is
    /// not already owned. Identified principals normally claim an empty namespace atomically in
    /// <see cref="HasAccess"/> before the write; this method is an idempotent compatibility guard.
    ///
    /// Deliberately a no-op for the default agent. Servers that never set <c>AGENT_ID</c> run as
    /// the default identity, which <see cref="HasAccess"/> short-circuits to full access anyway;
    /// registering ownership for it would create records that do nothing now but would lock the
    /// operator out of their own data the moment they later set an <c>AGENT_ID</c>. Access control
    /// therefore activates only once agents are actually given distinct identities.
    ///
    /// Pre-existing unregistered content is never claimable through a data-plane write; an
    /// administrator or migration must assign its owner explicitly.
    /// </summary>
    public void ClaimOwnershipOnWrite(string ns, string agentId, string tenantId)
    {
        if (agentId == AgentIdentity.DefaultAgentId && Tenancy.Normalize(tenantId).Length == 0) return;
        EnsureOwnership(ns, agentId, tenantId: tenantId);
    }

    /// <summary>
    /// Register namespace ownership. Prefer <see cref="ClaimOwnershipOnWrite"/> from write paths —
    /// it encodes the default-agent policy that keeps single-identity servers unaffected.
    /// Uses double-checked locking so the registered-path is lock-free; concurrent callers that
    /// race to register the same namespace are serialized per-namespace and only the first write
    /// wins (subsequent callers become no-ops).
    /// </summary>
    public void EnsureOwnership(string ns, string agentId, string tenantId)
    {
        if (ns.StartsWith('_')) return; // System namespaces not tracked
        tenantId = Tenancy.Normalize(tenantId);

        // Double-checked: fast path avoids acquiring the per-ns lock once registered.
        if (GetPermission(ns, tenantId) is not null) return;

        lock (LockFor(ns, tenantId: tenantId))
        {
            if (GetPermission(ns, tenantId) is not null) return; // Another thread registered first
            SavePermission(ns, agentId, Array.Empty<ShareGrant>(), tenantId);
        }
    }

    private NamespacePermission? GetPermission(string ns, string tenantId)
    {
        var entryId = PermissionEntryId(ns, tenantId);
        // Sharing metadata deliberately lives in the legacy partition: the tenant discriminator
        // is folded into the entry id by PermissionEntryId, not into the index partition.
        var entry = _index.Get(entryId, SystemNamespace, tenantId: "");
        if (entry is null) return null;

        var owner = entry.Metadata.GetValueOrDefault("owner") ?? AgentIdentity.DefaultAgentId;
        var grantsStr = entry.Metadata.GetValueOrDefault("grants") ?? "";
        return new NamespacePermission(ns, owner, ParseGrants(grantsStr));
    }

    /// <summary>
    /// Materialize the owner record that <see cref="Share"/> needs to hang grants off, but only for
    /// the legacy-unisolated default agent. That agent never registers ownership on its own:
    /// <see cref="ClaimOwnershipOnWrite"/> is deliberately a no-op for it and
    /// <see cref="HasAccess"/> short-circuits it to full access, so without this every
    /// <c>share_namespace</c> call on a server started without <c>AGENT_ID</c> would fail with
    /// <c>error_not_found</c> and there would be no MCP-reachable way to register an owner first.
    /// Recording it as owner grants it nothing it did not already have.
    ///
    /// Returns null for every identified principal: those must claim a namespace by writing to it
    /// (or be assigned it administratively), so sharing can never be the act that takes over
    /// pre-existing unregistered content. Caller must hold <see cref="LockFor"/>.
    /// </summary>
    private NamespacePermission? RegisterLegacyOwnerUnlocked(string ns, string agentId, string tenantId)
    {
        if (agentId != AgentIdentity.DefaultAgentId || Tenancy.Normalize(tenantId).Length != 0)
            return null;

        SavePermission(ns, agentId, Array.Empty<ShareGrant>(), tenantId);
        return new NamespacePermission(ns, agentId, Array.Empty<ShareGrant>());
    }

    private bool TryClaimEmptyNamespace(string ns, string agentId, string tenantId)
    {
        tenantId = Tenancy.Normalize(tenantId);
        lock (LockFor(ns, tenantId: tenantId))
        {
            var existing = GetPermission(ns, tenantId);
            if (existing is not null)
                return existing.Owner == agentId ||
                       existing.SharedWith.Any(grant => grant.AgentId == agentId && grant.AccessLevel == "write");

            if (_index.GetAllInNamespace(ns, tenantId: tenantId).Count != 0)
                return false;

            SavePermission(ns, agentId, Array.Empty<ShareGrant>(), tenantId);
            return true;
        }
    }

    private void SavePermission(string ns, string owner, IReadOnlyList<ShareGrant> grants, string tenantId)
    {
        tenantId = Tenancy.Normalize(tenantId);
        var entryId = PermissionEntryId(ns, tenantId);
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
                ["tenantId"] = tenantId,
                ["owner"] = owner,
                ["grants"] = grantsStr
            },
            lifecycleState: "ltm")
        {
            IsSummaryNode = true
        };

        _index.Upsert(entry);
    }

    private static string PermissionEntryId(string ns, string tenantId)
    {
        tenantId = Tenancy.Normalize(tenantId);
        if (tenantId.Length == 0)
            return $"perm_{ns}";

        // Keep legacy ids byte-for-byte stable while using a bounded, non-reversible tenant
        // discriminator for new partitions. The full tenant remains in private metadata.
        var tenantHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tenantId)))[..16];
        return $"perm_t_{tenantHash}_{ns}";
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
