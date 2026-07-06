using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Storage.Migration;

/// <summary>
/// Classifies how a single entry was (or will be) handled by <see cref="TenantPrefixMigrationTool"/>.
/// </summary>
public enum TenantMigrationKind
{
    /// <summary>Bare legacy namespace, no default tenant supplied — entry is left exactly as-is.</summary>
    Unchanged,

    /// <summary>Namespace matched the prefix-era "{tenant}::{path}" convention and was split.</summary>
    PrefixSplit,

    /// <summary>Bare legacy namespace, but a default tenant id was supplied — entry gets stamped with it.</summary>
    DefaultAssigned
}

/// <summary>
/// Records exactly what happened to one entry during a migration pass, sufficient to reverse it.
/// </summary>
public sealed record MigratedEntryRecord(
    string Id,
    string OriginalNs,
    string OriginalTenantId,
    string NewNs,
    string NewTenantId,
    TenantMigrationKind Kind);

/// <summary>
/// Result of a <see cref="TenantPrefixMigrationTool.Migrate"/> or
/// <see cref="TenantPrefixMigrationTool.Reverse"/> call. Carries the per-entry move log (needed to
/// reverse the operation exactly) plus a row-count parity check so callers can assert nothing was
/// lost or duplicated.
/// </summary>
public sealed record TenantMigrationManifest(
    IReadOnlyList<MigratedEntryRecord> Records,
    int TotalEntriesBefore,
    int TotalEntriesAfter)
{
    /// <summary>True when the total entry count across the whole store is unchanged by the operation.</summary>
    public bool RowCountParityOk => TotalEntriesBefore == TotalEntriesAfter;
}

/// <summary>
/// One-shot migration tool that retires the prefix-era tenant encoding — namespaces of the form
/// <c>"{tenant}::{path}"</c> (Conductor's T2-01 interim format, used before <c>tenant_id</c> became
/// a first-class column/field) — in favor of the Phase 1/2 <see cref="CognitiveEntry.TenantId"/>
/// column. See <c>docs/tenant-isolation-design.md</c>.
///
/// Two classes of rows are handled in a single pass:
///
/// 1. <b>Prefix-era rows</b> — physical namespace name matches <c>"{tenant}::{path}"</c>. Each such
///    entry is rewritten to <c>Ns = path</c>, <c>TenantId = tenant</c>, and moved into the
///    <c>path</c> namespace bucket; the now-empty prefixed namespace is deleted.
/// 2. <b>Legacy bare-ns rows</b> — physical namespace name has no <c>"::"</c> separator. These get
///    <c>TenantId = defaultTenantId</c> (or <c>""</c>, the legacy tenant, when no default is
///    supplied — in which case the row is left completely untouched).
///
/// The operation is reversible: <see cref="Migrate"/> returns a <see cref="TenantMigrationManifest"/>
/// that <see cref="Reverse"/> can play back to restore the pre-migration layout exactly, and both
/// directions verify row-count parity across the whole store (nothing lost, nothing duplicated).
///
/// FROZEN: this tool never touches RRF/rerank internals, embedding pins, or the no-tenant public
/// API — it only rewrites the (ns, tenantId) partition key of existing rows via the ordinary
/// <see cref="IStorageProvider"/> surface.
/// </summary>
public sealed class TenantPrefixMigrationTool
{
    private const string PrefixSeparator = "::";

    private readonly IStorageProvider _storage;

    public TenantPrefixMigrationTool(IStorageProvider storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    /// <summary>
    /// Attempts to split a prefix-era namespace "{tenant}::{path}" into (tenant, path).
    /// Returns false for a bare namespace (no separator) or a malformed one (empty tenant or
    /// empty path around the separator) — both are treated as legacy bare namespaces.
    /// </summary>
    public static bool TryParsePrefixedNamespace(string ns, out string tenantId, out string path)
    {
        var idx = ns.IndexOf(PrefixSeparator, StringComparison.Ordinal);
        if (idx > 0 && idx + PrefixSeparator.Length < ns.Length)
        {
            tenantId = ns[..idx];
            path = ns[(idx + PrefixSeparator.Length)..];
            return true;
        }

        tenantId = "";
        path = ns;
        return false;
    }

    /// <summary>
    /// Runs the migration across every persisted namespace. Idempotent in spirit — a namespace with
    /// no "::" and no default tenant is a no-op — but not safe to re-run blindly against a store that
    /// has already been migrated once, because a second pass would try to reinterpret plain "path"
    /// namespaces (fine, they simply have no "::" and get skipped/defaulted again, which is a no-op
    /// if the same default is supplied).
    /// </summary>
    /// <param name="defaultTenantId">
    /// Tenant id assigned to legacy bare-ns rows. Null/empty (the default) leaves bare rows completely
    /// untouched — they already implicitly live in the "" legacy tenant.
    /// </param>
    /// <param name="dryRun">When true, computes the manifest without writing any changes to storage.</param>
    public TenantMigrationManifest Migrate(string? defaultTenantId = null, bool dryRun = false)
    {
        var normalizedDefault = string.IsNullOrWhiteSpace(defaultTenantId) ? "" : defaultTenantId.Trim();
        var srcNamespaces = _storage.GetPersistedNamespaces();

        var records = new List<MigratedEntryRecord>();
        var destWrites = new Dictionary<string, List<CognitiveEntry>>();
        var fullyCapturedNs = new HashSet<string>();
        var srcNamespacesToDelete = new List<string>();
        int totalBefore = 0;

        foreach (var srcNs in srcNamespaces)
        {
            var data = _storage.LoadNamespace(srcNs);
            totalBefore += data.Entries.Count;

            var isPrefixed = TryParsePrefixedNamespace(srcNs, out var tenantFromPrefix, out var pathFromPrefix);

            string destNs;
            string newTenantId;
            TenantMigrationKind kind;

            if (isPrefixed)
            {
                destNs = pathFromPrefix;
                newTenantId = tenantFromPrefix;
                kind = TenantMigrationKind.PrefixSplit;
            }
            else if (normalizedDefault.Length > 0)
            {
                destNs = srcNs;
                newTenantId = normalizedDefault;
                kind = TenantMigrationKind.DefaultAssigned;
            }
            else
            {
                // Bare legacy namespace, no default supplied — nothing to do.
                continue;
            }

            if (destNs == srcNs)
                fullyCapturedNs.Add(destNs);
            else
                srcNamespacesToDelete.Add(srcNs);

            if (!destWrites.TryGetValue(destNs, out var bucket))
            {
                bucket = new List<CognitiveEntry>();
                destWrites[destNs] = bucket;
            }

            foreach (var entry in data.Entries)
            {
                var originalTenantId = entry.TenantId;
                bucket.Add(CloneWithNsAndTenant(entry, destNs, newTenantId));
                records.Add(new MigratedEntryRecord(entry.Id, srcNs, originalTenantId, destNs, newTenantId, kind));
            }
        }

        if (dryRun)
            return new TenantMigrationManifest(records, totalBefore, totalBefore);

        foreach (var (destNs, movedEntries) in destWrites)
        {
            List<CognitiveEntry> finalEntries;
            if (fullyCapturedNs.Contains(destNs))
            {
                // We already captured every entry that physically lived at destNs above.
                finalEntries = movedEntries;
            }
            else
            {
                // destNs has pre-existing content (e.g. a plain namespace at the same path a
                // prefixed source is being folded into) that this pass never touched — preserve it.
                var existing = _storage.LoadNamespace(destNs);
                finalEntries = new List<CognitiveEntry>(existing.Entries.Count + movedEntries.Count);
                finalEntries.AddRange(existing.Entries);
                finalEntries.AddRange(movedEntries);
            }

            _storage.SaveNamespaceSync(destNs, new NamespaceData { Entries = finalEntries });
        }

        foreach (var srcNs in srcNamespacesToDelete)
            _storage.DeleteNamespaceAsync(srcNs).GetAwaiter().GetResult();

        var totalAfter = _storage.GetPersistedNamespaces()
            .Sum(ns => _storage.LoadNamespace(ns).Entries.Count);

        return new TenantMigrationManifest(records, totalBefore, totalAfter);
    }

    /// <summary>
    /// Reverses a prior <see cref="Migrate"/> call using its manifest, restoring the exact
    /// pre-migration namespace/tenant layout for every entry the manifest touched. Entries the
    /// manifest marked <see cref="TenantMigrationKind.Unchanged"/> are ignored (nothing was done to
    /// them). Rows unrelated to the migration that happen to share a destination namespace are left
    /// untouched.
    /// </summary>
    public TenantMigrationManifest Reverse(TenantMigrationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var actionable = manifest.Records.Where(r => r.Kind != TenantMigrationKind.Unchanged).ToList();

        var totalBefore = _storage.GetPersistedNamespaces()
            .Sum(ns => _storage.LoadNamespace(ns).Entries.Count);

        if (actionable.Count == 0)
            return new TenantMigrationManifest(Array.Empty<MigratedEntryRecord>(), totalBefore, totalBefore);

        var byNewNs = actionable
            .GroupBy(r => r.NewNs)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.Id));

        var origWrites = new Dictionary<string, List<CognitiveEntry>>();
        var remainderByNewNs = new Dictionary<string, List<CognitiveEntry>>();

        foreach (var (newNs, idMap) in byNewNs)
        {
            var current = _storage.LoadNamespace(newNs);
            var remainder = new List<CognitiveEntry>();

            foreach (var entry in current.Entries)
            {
                if (idMap.TryGetValue(entry.Id, out var record) && entry.TenantId == record.NewTenantId)
                {
                    if (!origWrites.TryGetValue(record.OriginalNs, out var list))
                    {
                        list = new List<CognitiveEntry>();
                        origWrites[record.OriginalNs] = list;
                    }
                    list.Add(CloneWithNsAndTenant(entry, record.OriginalNs, record.OriginalTenantId));
                }
                else
                {
                    remainder.Add(entry);
                }
            }

            remainderByNewNs[newNs] = remainder;
        }

        foreach (var (newNs, remainder) in remainderByNewNs)
        {
            if (remainder.Count == 0)
                _storage.DeleteNamespaceAsync(newNs).GetAwaiter().GetResult();
            else
                _storage.SaveNamespaceSync(newNs, new NamespaceData { Entries = remainder });
        }

        foreach (var (originalNs, restoredEntries) in origWrites)
        {
            var existing = _storage.LoadNamespace(originalNs);
            var merged = new List<CognitiveEntry>(existing.Entries.Count + restoredEntries.Count);
            merged.AddRange(existing.Entries);
            merged.AddRange(restoredEntries);
            _storage.SaveNamespaceSync(originalNs, new NamespaceData { Entries = merged });
        }

        var totalAfter = _storage.GetPersistedNamespaces()
            .Sum(ns => _storage.LoadNamespace(ns).Entries.Count);

        var reverseRecords = actionable
            .Select(r => new MigratedEntryRecord(r.Id, r.NewNs, r.NewTenantId, r.OriginalNs, r.OriginalTenantId, r.Kind))
            .ToList();

        return new TenantMigrationManifest(reverseRecords, totalBefore, totalAfter);
    }

    private static CognitiveEntry CloneWithNsAndTenant(CognitiveEntry entry, string newNs, string newTenantId)
    {
        if (entry.Ns == newNs && entry.TenantId == newTenantId)
            return entry;

        return new CognitiveEntry(
            entry.Id,
            entry.Vector,
            newNs,
            entry.Text,
            entry.Category,
            entry.Metadata,
            entry.LifecycleState,
            entry.CreatedAt,
            entry.LastAccessedAt,
            entry.AccessCount,
            entry.ActivationEnergy,
            entry.IsSummaryNode,
            entry.SourceClusterId,
            entry.Keywords,
            newTenantId);
    }
}
