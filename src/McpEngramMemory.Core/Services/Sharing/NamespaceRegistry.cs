using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    private sealed record PermissionRecord(
        string Ns,
        string Owner,
        IReadOnlyList<ShareGrant> SharedWith,
        IReadOnlyList<string> DerivedSources);

    private static readonly JsonSerializerOptions GrantsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CognitiveIndex _index;
    private readonly IEmbeddingService _embedding;

    // Per-namespace locks serialize read-modify-write on permission entries
    // (Share/Unshare/EnsureOwnership) so concurrent grants don't overwrite each
    // other. Keyed by target namespace — grants to different namespaces stay
    // parallel; grants to the same namespace serialize through one monitor.
    private readonly ConcurrentDictionary<string, object> _permissionLocks = new();

    private object LockFor(string ns, string tenantId) =>
        _permissionLocks.GetOrAdd(PermissionEntryId(ns, tenantId), _ => new object());

    private static bool IsLegacyDefault(string agentId, string tenantId) =>
        agentId == AgentIdentity.DefaultAgentId && Tenancy.Normalize(tenantId).Length == 0;

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
        if (string.IsNullOrWhiteSpace(targetAgentId) || accessLevel is not ("read" or "write"))
            return new ShareResult("error", ns, targetAgentId, accessLevel);

        if (ns.StartsWith('_'))
            return new ShareResult("error_not_found", ns, targetAgentId, accessLevel);

        lock (LockFor(ns, tenantId: tenantId))
        {
            var permission = GetPermissionRecord(ns, tenantId)
                             ?? RegisterLegacyOwnerUnlocked(ns, ownerAgentId, tenantId);
            if (permission is null)
                return new ShareResult("error_not_found", ns, targetAgentId, accessLevel);

            // Check ownership (only legacy-unisolated default agent bypasses ownership checks for backward compat)
            if (permission.Owner != ownerAgentId && !IsLegacyDefault(ownerAgentId, tenantId))
                return new ShareResult("error_not_owner", ns, targetAgentId, accessLevel);

            // Prevent permission laundering: a namespace carrying content derived from foreign-owned
            // source namespaces cannot be shared with a principal who lacks read access to every
            // restricted source namespace.
            if (permission.DerivedSources.Count > 0 && !IsLegacyDefault(ownerAgentId, tenantId))
            {
                foreach (var sourceNs in permission.DerivedSources)
                {
                    if (!HasAccess(targetAgentId, sourceNs, requiredLevel: "read", tenantId: tenantId))
                        return new ShareResult("error_derived_restricted", ns, targetAgentId, accessLevel);
                }
            }

            // Update sharing list
            var grants = permission.SharedWith.ToList();
            var existing = grants.FindIndex(g => g.AgentId == targetAgentId);
            if (existing >= 0)
                grants[existing] = new ShareGrant(targetAgentId, accessLevel);
            else
                grants.Add(new ShareGrant(targetAgentId, accessLevel));

            SavePermission(ns, permission.Owner, grants, tenantId, permission.DerivedSources);
            return new ShareResult("shared", ns, targetAgentId, accessLevel);
        }
    }

    /// <summary>
    /// Revoke an agent's access to a namespace.
    /// Thread-safe under the same per-namespace serialization as <see cref="Share"/>.
    /// </summary>
    public ShareResult Unshare(string ns, string ownerAgentId, string targetAgentId, string tenantId)
    {
        if (ns.StartsWith('_'))
            return new ShareResult("error_not_found", ns, targetAgentId, "none");

        lock (LockFor(ns, tenantId: tenantId))
        {
            var permission = GetPermissionRecord(ns, tenantId);
            if (permission is null)
                return new ShareResult("error_not_found", ns, targetAgentId, "none");

            // Keep legacy-unisolated default policy consistent with Share: only the owner or
            // legacy-unisolated default administrator may revoke grants.
            if (permission.Owner != ownerAgentId && !IsLegacyDefault(ownerAgentId, tenantId))
                return new ShareResult("error_not_owner", ns, targetAgentId, "none");

            var grants = permission.SharedWith.Where(g => g.AgentId != targetAgentId).ToList();
            SavePermission(ns, permission.Owner, grants, tenantId, permission.DerivedSources);
            return new ShareResult("unshared", ns, targetAgentId, "none");
        }
    }

    /// <summary>
    /// Side-effect-free write-access check: returns true if <paramref name="agentId"/> is allowed
    /// to write to <paramref name="ns"/> (including claiming it if currently unregistered and empty),
    /// WITHOUT mutating <c>_system_sharing</c>. Use during input validation and dry-run paths,
    /// then call <see cref="TryAcquireWriteAccess"/> immediately before performing the validated write.
    /// </summary>
    public bool CanWriteWithoutClaiming(string agentId, string ns, string tenantId)
    {
        if (IsLegacyDefault(agentId, tenantId))
            return true;

        if (string.IsNullOrEmpty(ns) || ns.StartsWith('_'))
            return false;

        var permission = GetPermissionRecord(ns, tenantId);
        if (permission is null)
            return _index.GetAllInNamespace(ns, tenantId: Tenancy.Normalize(tenantId)).Count == 0;

        if (permission.Owner == agentId)
            return true;

        return permission.SharedWith.Any(g => g.AgentId == agentId && g.AccessLevel == "write");
    }

    /// <summary>
    /// Atomically verify write access and claim ownership if <paramref name="ns"/> is unregistered
    /// and empty. Call immediately before committing a validated write.
    /// </summary>
    public bool TryAcquireWriteAccess(string agentId, string ns, string tenantId)
        => HasAccess(agentId, ns, requiredLevel: "write", tenantId: tenantId);

    /// <summary>
    /// Returns true if <paramref name="agentId"/> is the recorded owner of <paramref name="ns"/>
    /// (or is the legacy-unisolated default principal). Returns false for shared-only grantees.
    /// </summary>
    public bool IsOwner(string agentId, string ns, string tenantId)
    {
        if (IsLegacyDefault(agentId, tenantId))
            return true;

        if (string.IsNullOrEmpty(ns) || ns.StartsWith('_'))
            return false;

        var permission = GetPermissionRecord(ns, tenantId);
        return permission is not null && permission.Owner == agentId;
    }

    /// <summary>
    /// Remove the <c>_system_sharing</c> permission entry for <paramref name="ns"/> if and only if
    /// the namespace currently holds zero entries in <paramref name="tenantId"/>. Prevents orphaned
    /// sharing records from accumulating when temporary namespaces (e.g. debates) are purged.
    /// </summary>
    public bool TryRemoveEmptyNamespacePermission(string ns, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(ns) || ns.StartsWith('_'))
            return false;

        tenantId = Tenancy.Normalize(tenantId);
        lock (LockFor(ns, tenantId: tenantId))
        {
            if (GetPermissionRecord(ns, tenantId) is null)
                return false;

            if (_index.GetAllInNamespace(ns, tenantId: tenantId).Count != 0)
                return false;

            return _index.Delete(PermissionEntryId(ns, tenantId), SystemNamespace, tenantId: "");
        }
    }

    /// <summary>
    /// Return the restricted foreign source namespaces whose content was copied or summarized into
    /// <paramref name="ns"/>. Empty when <paramref name="ns"/> has no derived-source restrictions.
    /// </summary>
    public IReadOnlyList<string> GetDerivedSources(string ns, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(ns) || ns.StartsWith('_'))
            return Array.Empty<string>();

        return GetPermissionRecord(ns, tenantId)?.DerivedSources ?? Array.Empty<string>();
    }

    /// <summary>
    /// Record restricted foreign source namespaces on <paramref name="ns"/> so it cannot later be
    /// shared with any agent lacking read access to every listed source namespace.
    /// </summary>
    public void AddDerivedSources(string ns, IReadOnlyCollection<string> sourceNamespaces, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(ns) || ns.StartsWith('_') || sourceNamespaces is null || sourceNamespaces.Count == 0)
            return;

        tenantId = Tenancy.Normalize(tenantId);
        lock (LockFor(ns, tenantId: tenantId))
        {
            var permission = GetPermissionRecord(ns, tenantId);
            if (permission is null)
                return;

            var merged = new HashSet<string>(permission.DerivedSources, StringComparer.Ordinal);
            bool changed = false;
            foreach (var src in sourceNamespaces)
            {
                if (!string.IsNullOrWhiteSpace(src) && !src.StartsWith('_') && !string.Equals(src, ns, StringComparison.Ordinal))
                {
                    if (merged.Add(src))
                        changed = true;
                }
            }

            if (!changed)
                return;

            var sorted = merged.OrderBy(s => s, StringComparer.Ordinal).ToList();
            SavePermission(ns, permission.Owner, permission.SharedWith, tenantId, sorted);
        }
    }

    /// <summary>
    /// Atomically verify write access, verify that every existing non-actor reader of <paramref name="ns"/>
    /// has read access to every namespace in <paramref name="sourceNamespaces"/>, claim ownership if
    /// <paramref name="ns"/> is unregistered and empty, and record <paramref name="sourceNamespaces"/>
    /// in <c>derivedSources</c> under one per-namespace lock — eliminating any TOCTOU window between
    /// checking recipients and writing derived content.
    /// </summary>
    public bool TryAcquireWriteAccessForDerived(
        string agentId,
        string ns,
        IReadOnlyCollection<string>? sourceNamespaces,
        string tenantId,
        out bool rejectedByDerivedPolicy)
    {
        rejectedByDerivedPolicy = false;

        if (IsLegacyDefault(agentId, tenantId))
            return true;

        if (string.IsNullOrEmpty(ns) || ns.StartsWith('_'))
            return false;

        var normalizedSources = sourceNamespaces is { Count: > 0 }
            ? sourceNamespaces
                .Where(s => !string.IsNullOrWhiteSpace(s) && !s.StartsWith('_') && !string.Equals(s, ns, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : null;

        tenantId = Tenancy.Normalize(tenantId);
        lock (LockFor(ns, tenantId: tenantId))
        {
            var existing = GetPermissionRecord(ns, tenantId);
            if (existing is null)
            {
                if (_index.GetAllInNamespace(ns, tenantId: tenantId).Count != 0)
                    return false;

                var sorted = normalizedSources?.OrderBy(s => s, StringComparer.Ordinal).ToList();
                SavePermission(ns, agentId, Array.Empty<ShareGrant>(), tenantId, sorted);
                return true;
            }

            bool canWrite = existing.Owner == agentId ||
                            existing.SharedWith.Any(grant => grant.AgentId == agentId && grant.AccessLevel == "write");
            if (!canWrite)
                return false;

            if (normalizedSources is { Count: > 0 })
            {
                var recipients = new HashSet<string>(StringComparer.Ordinal);
                if (!string.Equals(existing.Owner, agentId, StringComparison.Ordinal))
                    recipients.Add(existing.Owner);
                foreach (var grant in existing.SharedWith)
                {
                    if (!string.Equals(grant.AgentId, agentId, StringComparison.Ordinal))
                        recipients.Add(grant.AgentId);
                }

                foreach (var recipient in recipients)
                {
                    foreach (var sourceNs in normalizedSources)
                    {
                        if (!HasAccess(recipient, sourceNs, requiredLevel: "read", tenantId: tenantId))
                        {
                            rejectedByDerivedPolicy = true;
                            return false;
                        }
                    }
                }

                var merged = new HashSet<string>(existing.DerivedSources, StringComparer.Ordinal);
                bool changed = false;
                foreach (var src in normalizedSources)
                {
                    if (merged.Add(src))
                        changed = true;
                }

                if (changed)
                {
                    var sorted = merged.OrderBy(s => s, StringComparer.Ordinal).ToList();
                    SavePermission(ns, existing.Owner, existing.SharedWith, tenantId, sorted);
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Verify that every existing reader of <paramref name="ns"/> other than <paramref name="actorAgentId"/>
    /// (both shared grantees and a non-actor owner) already has read access to every namespace in
    /// <paramref name="sourceNamespaces"/>. Returns false if writing derived content into
    /// <paramref name="ns"/> would launder restricted source data to an unauthorized principal.
    /// </summary>
    public bool CanAllReadersAccessSources(
        string ns,
        string actorAgentId,
        IReadOnlyCollection<string> sourceNamespaces,
        string tenantId)
    {
        if (sourceNamespaces is null || sourceNamespaces.Count == 0)
            return true;
        if (IsLegacyDefault(actorAgentId, tenantId))
            return true;
        if (string.IsNullOrWhiteSpace(ns) || ns.StartsWith('_'))
            return false;

        var permission = GetPermissionRecord(ns, tenantId);
        if (permission is null)
            return true; // Unregistered empty namespace has no other readers yet.

        var recipients = new HashSet<string>(StringComparer.Ordinal);
        if (!string.Equals(permission.Owner, actorAgentId, StringComparison.Ordinal))
            recipients.Add(permission.Owner);
        foreach (var grant in permission.SharedWith)
        {
            if (!string.Equals(grant.AgentId, actorAgentId, StringComparison.Ordinal))
                recipients.Add(grant.AgentId);
        }

        foreach (var recipient in recipients)
        {
            foreach (var sourceNs in sourceNamespaces)
            {
                if (!HasAccess(recipient, sourceNs, requiredLevel: "read", tenantId: tenantId))
                    return false;
            }
        }

        return true;
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
        bool isLegacyDefault = IsLegacyDefault(agentId, tenantId);

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
                var callerGrants = grants.Where(g => g.AgentId == agentId).ToList();
                if (callerGrants.Count > 0)
                {
                    // Prevent co-grantee disclosure: non-owners (except legacy default) receive
                    // only their own grant, not the identities or access levels of other grantees.
                    IReadOnlyList<ShareGrant> visibleGrants = isLegacyDefault ? grants : callerGrants;
                    shared.Add(new NamespacePermission(ns, owner, visibleGrants));
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

    private PermissionRecord? GetPermissionRecord(string ns, string tenantId)
    {
        tenantId = Tenancy.Normalize(tenantId);
        var entryId = PermissionEntryId(ns, tenantId);
        // Sharing metadata deliberately lives in the legacy partition: the tenant discriminator
        // is folded into the entry id by PermissionEntryId, not into the index partition.
        var entry = _index.Get(entryId, SystemNamespace, tenantId: "");
        if (entry is null) return null;

        // Defense-in-depth: confirm stored metadata agrees with the requested (tenantId, ns)
        // pair so a historical colliding id or hash collision can never cross partitions.
        var storedTenant = Tenancy.Normalize(entry.Metadata.GetValueOrDefault("tenantId"));
        var storedNs = entry.Metadata.GetValueOrDefault("ns");
        if (storedTenant != tenantId || (storedNs is not null && storedNs != ns))
            return null;

        var owner = entry.Metadata.GetValueOrDefault("owner") ?? AgentIdentity.DefaultAgentId;
        var grantsStr = entry.Metadata.GetValueOrDefault("grants") ?? "";
        var derivedStr = entry.Metadata.GetValueOrDefault("derivedSources") ?? "";
        return new PermissionRecord(ns, owner, ParseGrants(grantsStr), ParseDerivedSources(derivedStr));
    }

    private NamespacePermission? GetPermission(string ns, string tenantId)
    {
        var record = GetPermissionRecord(ns, tenantId);
        return record is null ? null : new NamespacePermission(record.Ns, record.Owner, record.SharedWith);
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
    private PermissionRecord? RegisterLegacyOwnerUnlocked(string ns, string agentId, string tenantId)
    {
        if (agentId != AgentIdentity.DefaultAgentId || Tenancy.Normalize(tenantId).Length != 0)
            return null;

        SavePermission(ns, agentId, Array.Empty<ShareGrant>(), tenantId, Array.Empty<string>());
        return new PermissionRecord(ns, agentId, Array.Empty<ShareGrant>(), Array.Empty<string>());
    }

    private bool TryClaimEmptyNamespace(string ns, string agentId, string tenantId)
    {
        tenantId = Tenancy.Normalize(tenantId);
        lock (LockFor(ns, tenantId: tenantId))
        {
            var existing = GetPermissionRecord(ns, tenantId);
            if (existing is not null)
                return existing.Owner == agentId ||
                       existing.SharedWith.Any(grant => grant.AgentId == agentId && grant.AccessLevel == "write");

            if (_index.GetAllInNamespace(ns, tenantId: tenantId).Count != 0)
                return false;

            SavePermission(ns, agentId, Array.Empty<ShareGrant>(), tenantId, Array.Empty<string>());
            return true;
        }
    }

    private void SavePermission(
        string ns,
        string owner,
        IReadOnlyList<ShareGrant> grants,
        string tenantId,
        IReadOnlyList<string>? derivedSources = null)
    {
        tenantId = Tenancy.Normalize(tenantId);
        var entryId = PermissionEntryId(ns, tenantId);
        // Serialize grants as JSON so delimiter characters (':' or ';') in agent IDs cannot
        // corrupt or forge ACL entries. ParseGrants retains backward-compatible legacy parsing.
        var grantsStr = JsonSerializer.Serialize(grants, GrantsJsonOptions);
        var vector = _embedding.Embed($"namespace permission {ns}");

        var metadata = new Dictionary<string, string>
        {
            ["ns"] = ns,
            ["tenantId"] = tenantId,
            ["owner"] = owner,
            ["grants"] = grantsStr
        };
        if (derivedSources is { Count: > 0 })
            metadata["derivedSources"] = JsonSerializer.Serialize(derivedSources, GrantsJsonOptions);

        var entry = new CognitiveEntry(
            id: entryId,
            vector: vector,
            ns: SystemNamespace,
            text: $"Namespace '{ns}' owned by '{owner}'",
            category: PermissionCategory,
            metadata: metadata,
            lifecycleState: "ltm")
        {
            IsSummaryNode = true
        };

        _index.Upsert(entry);
    }

    private static IReadOnlyList<string> ParseDerivedSources(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(raw, GrantsJsonOptions);
            if (parsed is not null)
                return parsed.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList();
        }
        catch (JsonException)
        {
            // Ignore malformed legacy/hand-edited derivedSources metadata.
        }

        return Array.Empty<string>();
    }

    internal static string PermissionEntryId(string ns, string tenantId)
    {
        tenantId = Tenancy.Normalize(tenantId);
        if (tenantId.Length == 0)
        {
            // Keep normal legacy ids byte-for-byte stable (`perm_{ns}`), while isolating legacy
            // namespaces shaped like `t_<16hex>_...` so they can never collide with tenant ids
            // (`perm_t_<16hex>_...`) in `_system_sharing`.
            return HasReservedTenantDiscriminatorPrefix(ns)
                ? $"legacy_perm_{ns}"
                : $"perm_{ns}";
        }

        // Keep legacy ids byte-for-byte stable while using a bounded, non-reversible tenant
        // discriminator for new partitions. The full tenant remains in private metadata.
        var tenantHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tenantId)))[..16];
        return $"perm_t_{tenantHash}_{ns}";
    }

    private static bool HasReservedTenantDiscriminatorPrefix(string ns)
    {
        if (ns.Length < 19 || !ns.StartsWith("t_", StringComparison.Ordinal) || ns[18] != '_')
            return false;

        for (int i = 2; i < 18; i++)
        {
            if (!Uri.IsHexDigit(ns[i]))
                return false;
        }

        return true;
    }

    internal static IReadOnlyList<ShareGrant> ParseGrants(string grantsStr)
    {
        if (string.IsNullOrWhiteSpace(grantsStr))
            return Array.Empty<ShareGrant>();

        var trimmed = grantsStr.TrimStart();
        if (trimmed.StartsWith('['))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<ShareGrant>>(trimmed, GrantsJsonOptions);
                if (parsed is not null)
                {
                    return parsed
                        .Where(g => g is not null && !string.IsNullOrEmpty(g.AgentId) && g.AccessLevel is ("read" or "write"))
                        .ToList();
                }
            }
            catch (JsonException)
            {
                // Fall back to legacy semicolon/colon format for pre-2.1.x persisted metadata.
            }
        }

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
