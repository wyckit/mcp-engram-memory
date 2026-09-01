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
    TenantMigrationKind Kind,
    // Null means a legacy manifest that predates occupation witnesses.
    long? Revision = null,
    long? LifecycleRevision = null);

/// <summary>
/// Result of a <see cref="TenantPrefixMigrationTool.Migrate"/> or
/// <see cref="TenantPrefixMigrationTool.Reverse"/> call. Carries the per-entry move log (needed to
/// reverse the operation exactly) plus a row-count parity check so callers can assert nothing was
/// lost or duplicated.
/// </summary>
/// <summary>
/// One namespace-level relocation performed by a forward pass: the source namespace folded
/// into a destination placement. This is the provenance GRAPH rows move by — clusters and
/// collapse receipts name (Ns, TenantId) and have no per-row manifest — and it is not
/// recoverable from the entry records, which only witness sources that still had entries.
/// </summary>
public sealed record MigratedNamespaceRecord(
    string OriginalNs,
    // The tenant the forward pass MATCHED on at the source. "" means it moved only legacy
    // rows and is therefore exactly reversible; null means it moved rows of EVERY tenant at
    // that namespace, which cannot be attributed back per row and so cannot be reversed.
    string? OriginalTenantMatch,
    string NewNs,
    string NewTenantId);

/// <summary>
/// One graph row (a cluster or a collapse receipt) the forward pass rewrote — PER-ROW
/// provenance, read off the row itself at rewrite time. This is what makes reversal EXACT:
/// a namespace- or placement-level record cannot say which tenant an individual row carried
/// before a PrefixSplit re-stamped every tenant at the source, nor distinguish rows the
/// pass placed from rows created at the destination since.
/// </summary>
public sealed record MigratedGraphRowRecord(
    string Kind,
    string Id,
    string FromNs,
    string FromTenant,
    string ToNs,
    string ToTenant,
    // New manifests bind a cluster to its exact physical incarnation and a receipt to its
    // exact generation. HasIdentityWitness distinguishes a witnessed null cluster stamp/id
    // from an older manifest that did not record witnesses at all.
    long? Generation = null,
    string? CreationStamp = null,
    string? InstanceId = null,
    bool HasIdentityWitness = false);

public sealed record TenantMigrationManifest(
    IReadOnlyList<MigratedEntryRecord> Records,
    int TotalEntriesBefore,
    int TotalEntriesAfter,
    IReadOnlyList<string>? Warnings = null,
    // The namespace relocations this pass performed. Null on manifests produced before this
    // was recorded — a reverse then falls back to inferring provenance from entry
    // placements, and says so.
    IReadOnlyList<MigratedNamespaceRecord>? NamespaceMoves = null,
    // The individual graph rows this pass rewrote — the EXACT provenance a reverse plays
    // back. Null on older manifests; the reverse then degrades to the namespace-level
    // records above (refusing what those cannot attribute) and says so.
    IReadOnlyList<MigratedGraphRowRecord>? GraphRowMoves = null)
{
    /// <summary>True when the total entry count across the whole store is unchanged by the operation.</summary>
    public bool RowCountParityOk => TotalEntriesBefore == TotalEntriesAfter;

    /// <summary>Conditions the migration could not resolve on its own (unmovable graph rows,
    /// unreadable receipts, ambiguous reversals). Empty when everything moved cleanly.</summary>
    public IReadOnlyList<string> WarningList => Warnings ?? Array.Empty<string>();
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
/// OFFLINE ONLY: the tool requires exclusive access to the storage provider — no live
/// CognitiveIndex, ClusterManager, or scanner may share it while a migration runs. It writes
/// cluster rows through the provider's shared debounce slot and receipts through the record
/// layer, and a live component's pending saves would displace (or later overwrite) the
/// migrated rows.
///
/// FROZEN: this tool never touches RRF/rerank internals, embedding pins, or the no-tenant public
/// API — it only rewrites the (ns, tenantId) partition key of existing rows via the ordinary
/// <see cref="IStorageProvider"/> surface.
/// </summary>
public sealed class TenantPrefixMigrationTool
{
    private const string PrefixSeparator = "::";

    private readonly IStorageProvider _storage;
    // A warned partial pass keeps its sources and is explicitly retryable. Preserve the
    // durable graph-row effects already proven by that pass so an ordinary retry on this
    // tool returns one manifest capable of reversing the whole attempt chain. Callers that
    // recreate the tool can pass the prior manifest to Migrate's priorAttempt parameter.
    private readonly List<MigratedGraphRowRecord> _pendingGraphRowMoves = new();
    private readonly List<MigratedEntryRecord> _pendingEntryMoves = new();

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
    /// <param name="priorAttempt">The warned manifest from a partial earlier attempt when
    /// retrying through a newly-created tool. A retry on the same tool accumulates this
    /// provenance automatically.</param>
    public TenantMigrationManifest Migrate(string? defaultTenantId = null, bool dryRun = false,
        TenantMigrationManifest? priorAttempt = null)
    {
        var normalizedDefault = string.IsNullOrWhiteSpace(defaultTenantId) ? "" : defaultTenantId.Trim();
        var srcNamespaces = _storage.GetPersistedNamespaces();

        var records = new List<MigratedEntryRecord>();
        var destWrites = new Dictionary<string, List<(CognitiveEntry Entry, MigratedEntryRecord? Record)>>();
        var fullyCapturedNs = new HashSet<string>();
        var srcNamespacesToDelete = new List<string>();
        var nsMoves = new List<(string MatchNs, string? MatchTenant, string DestNs, string NewTenant)>();
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
                // Trimmed HERE, once, so every consumer agrees: the write paths trim tenant
                // ids on construction, and a manifest recording the raw prefix spelling
                // (" acme ") would never match the trimmed rows a reverse looks for.
                newTenantId = tenantFromPrefix.Trim();
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
            {
                fullyCapturedNs.Add(destNs);
            }
            else
            {
                srcNamespacesToDelete.Add(srcNs);
            }

            // PrefixSplit relocates the WHOLE namespace, so every row moves (MatchTenant
            // null) — exactly what happens to its entries. DefaultAssigned re-stamps only
            // the LEGACY rows, so its graph moves match only the empty tenant.
            nsMoves.Add((srcNs, kind == TenantMigrationKind.DefaultAssigned ? "" : null, destNs, newTenantId));

            if (!destWrites.TryGetValue(destNs, out var bucket))
            {
                bucket = new List<(CognitiveEntry, MigratedEntryRecord?)>();
                destWrites[destNs] = bucket;
            }

            foreach (var entry in data.Entries)
            {
                // DefaultAssigned stamps ONLY legacy (empty-tenant) rows. A bare namespace
                // can hold already-tenanted rows — entries a prior partial migration folded
                // in, or column-tenancy writes — and re-stamping them with the default
                // FLATTENS tenants: another tenant's data re-keyed under the default, and a
                // documented "no-op" re-run duplicating it cross-tenant.
                if (kind == TenantMigrationKind.DefaultAssigned && entry.TenantId.Length > 0)
                {
                    bucket.Add((entry, null));
                    continue;
                }
                var originalTenantId = entry.TenantId;
                var record = new MigratedEntryRecord(entry.Id, srcNs, originalTenantId,
                    destNs, newTenantId, kind, entry.Revision, entry.LifecycleRevision);
                bucket.Add((CloneWithNsAndTenant(entry, destNs, newTenantId), record));
                records.Add(record);
            }
        }

        if (dryRun)
        {
            // The read-only-computable warnings a live run would emit belong in the preview
            // too — a dry run that looks clean while the live run would report unmovable
            // receipts or edges defeats the point of previewing.
            var dryWarnings = new List<string>();
            if (nsMoves.Count > 0)
            {
                if (!_storage.TryReadCollapseHistory(out _))
                    dryWarnings.Add("Collapse history could not be strictly read; a live run would NOT migrate receipts. Repair the history file first.");
                int dryLegacyEdges = _storage.LoadGlobalEdges().Count(e => e is not null && string.IsNullOrEmpty(e.TenantId));
                if (dryLegacyEdges > 0)
                    dryWarnings.Add($"{dryLegacyEdges} legacy-tenant graph edge(s) would not be migrated: edges carry no namespace, so their tenant cannot be inferred from a namespace move.");
            }
            return new TenantMigrationManifest(records, totalBefore, totalBefore, dryWarnings);
        }

        var warnings = new List<string>();
        // Destinations where a moved row lost to a differing resident. Their sources are
        // exempt from the delete sweep — see the collision branch below.
        var unsafeSources = new HashSet<string>(StringComparer.Ordinal);
        // Refusals are exact source occupations, not normalized destination keys. Two
        // differently-spelled prefix sources can normalize to the same destination tenant/id;
        // withdrawing by destination key erased the accepted source's provenance too.
        var refused = new HashSet<MigratedEntryRecord>();
        var priorEntryMoves = _pendingEntryMoves
            .Concat(priorAttempt?.Records ?? Array.Empty<MigratedEntryRecord>())
            .ToHashSet();
        foreach (var (destNs, movedEntries) in destWrites)
        {
            var byKey = new Dictionary<(string TenantId, string Id), CognitiveEntry>();
            var acceptedRecord = new Dictionary<(string TenantId, string Id), MigratedEntryRecord?>();
            if (!fullyCapturedNs.Contains(destNs))
            {
                var existing = _storage.LoadNamespace(destNs);
                foreach (var e in existing.Entries)
                    if (e is not null && e.Id is not null)
                    {
                        byKey[(e.TenantId, e.Id)] = e;
                        acceptedRecord[(e.TenantId, e.Id)] = null;
                    }
            }

            // Untouched residents captured from this same physical namespace are still
            // residents, regardless of enumeration order; seed them before proposed moves.
            foreach (var (e, _) in movedEntries.Where(x => x.Record is null))
            {
                byKey[(e.TenantId, e.Id)] = e;
                acceptedRecord[(e.TenantId, e.Id)] = null;
            }

            foreach (var (e, record) in movedEntries.Where(x => x.Record is not null))
            {
                var key = (e.TenantId, e.Id);
                if (byKey.TryGetValue(key, out var resident))
                {
                    acceptedRecord.TryGetValue(key, out var priorRecord);
                    bool exactKnownRetry = priorEntryMoves.Contains(record!);
                    bool sameOccupation = resident.Revision == e.Revision
                        && resident.LifecycleRevision == e.LifecycleRevision;
                    bool sameStagedSource = priorRecord is not null
                        && string.Equals(priorRecord.OriginalNs, record!.OriginalNs,
                            StringComparison.Ordinal);
                    if (!sameOccupation || (!exactKnownRetry && !sameStagedSource))
                    {
                        // The resident wins, but the loser must not simply cease to exist.
                        // Discarding it here and then deleting its source namespace below
                        // destroyed the only remaining copy: the row was neither at the
                        // destination nor at the source, and the warning announced a loss
                        // rather than preventing one. Marking the destination keeps every
                        // source folded into it, so the discarded occupation stays readable
                        // and an operator can reconcile the two by hand.
                        unsafeSources.Add(record!.OriginalNs);
                        // The manifest staged a MigratedEntryRecord for this row before the
                        // fold could refuse it. Left standing it asserts a move that never
                        // happened: the row is still at its source, and a reverse would go
                        // looking for it at a destination it never reached — and, finding the
                        // resident that beat it, treat somebody else's row as the thing to
                        // move back. Withdrawn below.
                        refused.Add(record);
                        if (string.Equals(record.OriginalNs, destNs, StringComparison.Ordinal))
                        {
                            var original = CloneWithNsAndTenant(e, record.OriginalNs,
                                record.OriginalTenantId);
                            byKey[(original.TenantId, original.Id)] = original;
                            acceptedRecord[(original.TenantId, original.Id)] = null;
                        }
                        warnings.Add($"Entry '{e.Id}' (tenant '{e.TenantId}') already exists at '{destNs}' with a different occupation; the resident row was kept and the source copy was NOT migrated. Its source namespace has been left in place so the discarded row is still readable — reconcile the two, then re-run.");
                        continue;
                    }
                }
                byKey[key] = e;
                acceptedRecord[key] = record;
            }

            _storage.SaveNamespaceSync(destNs,
                new NamespaceData { Entries = new List<CognitiveEntry>(byKey.Values) });
        }

        // A manifest may only assert moves that actually landed. Records are staged during the
        // scan, before the fold knows whether the destination will accept each row, so the
        // refusals collected above are removed here rather than shipped as completed moves.
        if (refused.Count > 0)
            records.RemoveAll(refused.Contains);
        records = _pendingEntryMoves
            .Concat(priorAttempt?.Records ?? Array.Empty<MigratedEntryRecord>())
            .Concat(records)
            .Distinct()
            .ToList();

        // Namespace-scoped graph rows make a collision source indivisible. If ANY occupation
        // from a physical source was refused, graphSafeMoves must leave that source's graph
        // rows in place; therefore every entry from the same source must remain authoritative
        // there too. Retract the source's otherwise-successful destination copies and withdraw
        // all of its entry provenance instead of leaving a split/duplicated namespace.
        var retractedUnsafeMoves = records
            .Where(r => unsafeSources.Contains(r.OriginalNs))
            .ToList();
        if (retractedUnsafeMoves.Count > 0)
        {
            foreach (var group in retractedUnsafeMoves.GroupBy(r => r.NewNs))
            {
                var destination = _storage.LoadNamespace(group.Key);
                var retracted = group.ToList();
                var remainder = destination.Entries.Where(e => !retracted.Any(r =>
                    string.Equals(r.Id, e.Id, StringComparison.Ordinal)
                    && string.Equals(r.NewTenantId, e.TenantId, StringComparison.Ordinal)
                    && (r.Revision is null || r.Revision == e.Revision)
                    && (r.LifecycleRevision is null
                        || r.LifecycleRevision == e.LifecycleRevision))).ToList();
                if (remainder.Count == 0)
                    _storage.DeleteNamespaceAsync(group.Key).GetAwaiter().GetResult();
                else
                    _storage.SaveNamespaceSync(group.Key,
                        new NamespaceData { Entries = remainder });
            }
            records.RemoveAll(r => unsafeSources.Contains(r.OriginalNs));
        }

        // GRAPH-LEVEL rows move with their entries, BEFORE the source namespaces are
        // deleted. Clusters and collapse receipts name Ns/TenantId explicitly; left behind,
        // a receipt keeps addressing the partition its members and summary just left — an
        // undo then "restores" nothing while retiring the receipt, stranding the members
        // archived forever. The ordering is the crash-safety half: a re-run rebuilds its
        // move map from the still-present sources and converges, where sources deleted
        // FIRST made a crash here unrecoverable (the map derives from namespaces that no
        // longer exist). Edges carry a tenant but no namespace, so legacy-tenant edges
        // cannot be attributed to a move from edge data alone; they are reported instead of
        // guessed at.
        var graphRowMoves = MergeGraphRowMoves(
            _pendingGraphRowMoves,
            priorAttempt?.GraphRowMoves ?? Array.Empty<MigratedGraphRowRecord>());
        var graphSafeMoves = nsMoves.Where(m => !unsafeSources.Contains(m.MatchNs)).ToList();
        bool graphRowsMoved = MoveGraphRows(graphSafeMoves, warnings, graphRowMoves);

        if (graphRowsMoved)
        {
            // A source that still holds a row the destination refused is the only place that
            // occupation exists. Deleting it would complete the loss the collision branch
            // deliberately stopped short of, so those sources are kept and reported.
            var keptForDiscards = unsafeSources;

            foreach (var srcNs in srcNamespacesToDelete)
            {
                if (keptForDiscards.Contains(srcNs))
                    continue;

                _storage.DeleteNamespaceAsync(srcNs).GetAwaiter().GetResult();
            }

            if (keptForDiscards.Count > 0)
                warnings.Add($"Left {keptForDiscards.Count} source namespace(s) in place because rows they hold were refused by the destination: {string.Join(", ", keptForDiscards.OrderBy(n => n, StringComparer.Ordinal))}.");
        }
        else
        {
            // Some graph rows still name the source namespaces — which are therefore KEPT,
            // or no re-run could ever move them. The destination's copies dedupe by
            // (tenant, id) on the re-run's fold.
            warnings.Add("The source namespaces were left in place so a re-run (after resolving the warnings above) can complete the graph-row move.");
        }

        if (graphRowsMoved)
        {
            _pendingGraphRowMoves.Clear();
            _pendingEntryMoves.Clear();
        }
        else
        {
            _pendingGraphRowMoves.Clear();
            _pendingGraphRowMoves.AddRange(graphRowMoves);
            _pendingEntryMoves.Clear();
            _pendingEntryMoves.AddRange(records);
        }

        var totalAfter = _storage.GetPersistedNamespaces()
            .Sum(ns => _storage.LoadNamespace(ns).Entries.Count);

        return new TenantMigrationManifest(
            records,
            totalBefore,
            totalAfter,
            warnings,
            nsMoves.Select(m => new MigratedNamespaceRecord(m.MatchNs, m.MatchTenant, m.DestNs, m.NewTenant)).ToList(),
            graphRowMoves);
    }

    private static List<MigratedGraphRowRecord> MergeGraphRowMoves(
        IEnumerable<MigratedGraphRowRecord> first,
        IEnumerable<MigratedGraphRowRecord> second)
        => first.Concat(second).Distinct().ToList();

    // Plays back the EXACT per-row provenance a forward pass recorded: each row is matched
    // at the placement the record says the pass left it (id + ToNs + ToTenant — a row moved
    // or replaced since simply does not match and is warned, never guessed at) and restored
    // to the (FromNs, FromTenant) it actually carried.
    private void ReverseGraphRows(IReadOnlyList<MigratedGraphRowRecord> rows, List<string> warnings,
        List<MigratedGraphRowRecord>? rowMoves = null)
    {
        var clusterRows = rows.Where(r => r.Kind == "cluster").ToList();
        if (clusterRows.Count > 0)
        {
            var clusters = _storage.LoadClusters();
            bool changed = false;
            var result = new List<SemanticCluster>(clusters.Count);
            var matched = new HashSet<MigratedGraphRowRecord>();
            var alreadyReversed = clusterRows.Where(m => m.HasIdentityWitness && clusters.Any(c =>
                c is not null
                && string.Equals(m.Id, c.ClusterId, StringComparison.Ordinal)
                && string.Equals(m.FromNs, c.Ns, StringComparison.Ordinal)
                && string.Equals(m.FromTenant, c.TenantId, StringComparison.Ordinal)
                && string.Equals(m.CreationStamp, c.CreationStamp, StringComparison.Ordinal)
                && string.Equals(m.InstanceId, c.InstanceId, StringComparison.Ordinal)))
                .ToHashSet();
            foreach (var m in alreadyReversed)
                rowMoves?.Add(new MigratedGraphRowRecord("cluster", m.Id, m.ToNs, m.ToTenant,
                    m.FromNs, m.FromTenant, CreationStamp: m.CreationStamp,
                    InstanceId: m.InstanceId, HasIdentityWitness: true));
            foreach (var c in clusters)
            {
                var m = c is null ? null : clusterRows.FirstOrDefault(r =>
                    !alreadyReversed.Contains(r)
                    &&
                    string.Equals(r.Id, c.ClusterId, StringComparison.Ordinal)
                    && string.Equals(r.ToNs, c.Ns, StringComparison.Ordinal)
                    && string.Equals(r.ToTenant, c.TenantId, StringComparison.Ordinal)
                    && (!r.HasIdentityWitness
                        || (string.Equals(r.CreationStamp, c.CreationStamp, StringComparison.Ordinal)
                            && string.Equals(r.InstanceId, c.InstanceId, StringComparison.Ordinal))));
                if (m is not null)
                {
                    result.Add(new SemanticCluster(
                        c!.ClusterId, c.Label, m.FromNs, new List<string>(c.MemberIds), c.Centroid,
                        c.SummaryEntryId, m.FromTenant)
                    {
                        CreationStamp = c.CreationStamp,
                        InstanceId = c.InstanceId
                    });
                    changed = true;
                    matched.Add(m);
                }
                else if (c is not null)
                {
                    result.Add(c);
                }
            }
            foreach (var r in clusterRows)
                if (!matched.Contains(r) && !alreadyReversed.Contains(r))
                    warnings.Add($"Cluster '{r.Id}' was not found at its migrated placement ('{r.ToNs}', tenant '{r.ToTenant}') and was not reversed.");
            if (changed)
            {
                _storage.ScheduleSaveClusters(() => result);
                if (!_storage.TryFlush())
                {
                    warnings.Add("Reversed cluster rows could not be made durable; retry the reverse.");
                    // A failed flush leaves the reversed snapshot armed. Supersede it with
                    // the layout we actually observed so a later timer cannot publish an
                    // unmanifested reverse after entries have moved again.
                    var originalClusters = clusters.Where(x => x is not null).ToList();
                    _storage.ScheduleSaveClusters(() => originalClusters);
                    _storage.TryFlush();
                }
                else
                {
                    foreach (var m in matched)
                        rowMoves?.Add(new MigratedGraphRowRecord("cluster", m.Id, m.ToNs, m.ToTenant,
                            m.FromNs, m.FromTenant, CreationStamp: m.CreationStamp,
                            InstanceId: m.InstanceId, HasIdentityWitness: m.HasIdentityWitness));
                }
            }
        }

        var receiptRows = rows.Where(r => r.Kind == "receipt").ToList();
        if (receiptRows.Count > 0)
        {
            if (_storage.TryReadCollapseHistory(out var receipts))
            {
                foreach (var m in receiptRows)
                {
                    var already = m.HasIdentityWitness ? receipts.FirstOrDefault(x =>
                        string.Equals(x.CollapseId, m.Id, StringComparison.Ordinal)
                        && string.Equals(x.Ns, m.FromNs, StringComparison.Ordinal)
                        && string.Equals(x.TenantId, m.FromTenant, StringComparison.Ordinal)
                        && m.Generation == x.Generation) : null;
                    if (already is not null)
                    {
                        rowMoves?.Add(new MigratedGraphRowRecord("receipt", m.Id, m.ToNs,
                            m.ToTenant, m.FromNs, m.FromTenant, Generation: m.Generation,
                            HasIdentityWitness: true));
                        continue;
                    }
                    var r = receipts.FirstOrDefault(x =>
                        string.Equals(x.CollapseId, m.Id, StringComparison.Ordinal)
                        && string.Equals(x.Ns, m.ToNs, StringComparison.Ordinal)
                        && string.Equals(x.TenantId, m.ToTenant, StringComparison.Ordinal)
                        && (!m.HasIdentityWitness || m.Generation == x.Generation));
                    if (r is null)
                    {
                        warnings.Add($"Collapse receipt '{m.Id}' was not found at its migrated placement and was not reversed.");
                        continue;
                    }
                    var back = new CollapseRecord(
                        r.CollapseId, r.ClusterId, r.SummaryEntryId, m.FromNs,
                        r.MemberIds, r.PreviousStates, r.CollapsedAt, m.FromTenant,
                        r.AppliedLifecycleRevisions, r.ExpectedLifecycleRevisions,
                        r.Generation, r.ClusterStamp, r.ClusterInstance);
                    var outcome = m.HasIdentityWitness
                        ? _storage.UpsertCollapseRecordSync(back, r.Generation)
                        : (_storage.UpsertCollapseRecordSync(back)
                            ? CollapseRecordCas.Applied
                            : CollapseRecordCas.StoreFailed);
                    if (outcome != CollapseRecordCas.Applied)
                        warnings.Add($"Collapse receipt '{m.Id}' could not be rewritten to its original namespace; retry the reverse.");
                    else
                        rowMoves?.Add(new MigratedGraphRowRecord("receipt", m.Id, m.ToNs, m.ToTenant,
                            m.FromNs, m.FromTenant, Generation: r.Generation,
                            HasIdentityWitness: m.HasIdentityWitness));
                }
            }
            else
            {
                warnings.Add("Collapse history could not be strictly read; receipts were NOT reversed.");
            }
        }
    }

    // Returns FALSE when ANY graph row still names a pre-move location — an unreadable
    // history, a failed cluster flush, or a refused receipt write — so the caller keeps the
    // source namespaces and a re-run can converge. Warning-only outcomes (unmovable legacy
    // edges) report true.
    private bool MoveGraphRows(List<(string MatchNs, string? MatchTenant, string DestNs, string NewTenant)> nsMoves, List<string> warnings,
        List<MigratedGraphRowRecord>? rowMoves = null)
    {
        if (nsMoves.Count == 0)
            return true;

        bool allMoved = true;

        // First-match lookup over (namespace, optional placement tenant). MatchTenant is the
        // reverse pass's guard: only rows the forward pass PLACED (whose tenant it assigned)
        // move back — matching on Ns alone swept rows that lived at the destination before
        // the migration, or were created there since, into a source they never came from —
        // and keying by namespace alone could not express two tenants' moves out of one
        // folded destination.
        bool TryMatch(string? rowNs, string rowTenant, out (string DestNs, string NewTenant) move)
        {
            foreach (var m in nsMoves)
            {
                if (rowNs is not null && string.Equals(m.MatchNs, rowNs, StringComparison.Ordinal)
                    && (m.MatchTenant is null || string.Equals(rowTenant, m.MatchTenant, StringComparison.Ordinal)))
                {
                    move = (m.DestNs, m.NewTenant);
                    return true;
                }
            }
            move = default;
            return false;
        }

        var clusters = _storage.LoadClusters();
        bool clustersChanged = false;
        var movedClusters = new List<SemanticCluster>(clusters.Count);
        var stagedClusterMoves = new List<MigratedGraphRowRecord>();
        foreach (var c in clusters)
        {
            if (c is not null && c.Ns is not null && TryMatch(c.Ns, c.TenantId, out var mv))
            {
                movedClusters.Add(new SemanticCluster(
                    c.ClusterId, c.Label, mv.DestNs, new List<string>(c.MemberIds), c.Centroid,
                    c.SummaryEntryId, mv.NewTenant)
                {
                    CreationStamp = c.CreationStamp,
                    InstanceId = c.InstanceId
                });
                clustersChanged = true;
                // Per-row provenance, read off the row BEFORE the rewrite — see
                // MigratedGraphRowRecord. STAGED, not committed: the manifest may only
                // assert moves proven durable, so the records land in rowMoves after the
                // flush below succeeds.
                stagedClusterMoves.Add(new MigratedGraphRowRecord("cluster", c.ClusterId, c.Ns,
                    c.TenantId, mv.DestNs, mv.NewTenant, CreationStamp: c.CreationStamp,
                    InstanceId: c.InstanceId, HasIdentityWitness: true));
            }
            else if (c is not null)
            {
                // Null-shaped rows (the loaders' quarantine class) are not carried into a
                // rewritten cluster file.
                movedClusters.Add(c);
            }
        }
        if (clustersChanged)
        {
            _storage.ScheduleSaveClusters(() => movedClusters);
            if (!_storage.TryFlush())
            {
                warnings.Add("Cluster rows could not be made durable at their migrated location; the sources are kept for a re-run.");
                allMoved = false;
                // NEUTRALIZE the retained migrated-layout snapshot: a failed flush leaves it
                // armed on the debounce timer, and a background success AFTER a reverse would
                // durably split the cluster rows from their reversed entries. Re-scheduling
                // the ORIGINAL layout supersedes it (newer wins); whenever that lands it is
                // harmless. The staged provenance is DISCARDED — the manifest must not
                // assert moves that were never proven durable.
                var originalClusters = clusters.Where(x => x is not null).ToList();
                _storage.ScheduleSaveClusters(() => originalClusters);
                _storage.TryFlush();
            }
            else
            {
                rowMoves?.AddRange(stagedClusterMoves);
            }
        }

        if (_storage.TryReadCollapseHistory(out var receipts))
        {
            foreach (var r in receipts)
            {
                if (!TryMatch(r.Ns, r.TenantId, out var mv))
                    continue;
                var moved = new CollapseRecord(
                    r.CollapseId, r.ClusterId, r.SummaryEntryId, mv.DestNs,
                    r.MemberIds, r.PreviousStates, r.CollapsedAt, mv.NewTenant,
                    r.AppliedLifecycleRevisions, r.ExpectedLifecycleRevisions,
                    r.Generation, r.ClusterStamp, r.ClusterInstance);
                if (_storage.UpsertCollapseRecordSync(moved, r.Generation) != CollapseRecordCas.Applied)
                {
                    warnings.Add($"Collapse receipt '{r.CollapseId}' could not be rewritten to its migrated namespace; the sources are kept for a re-run.");
                    allMoved = false;
                }
                else
                {
                    rowMoves?.Add(new MigratedGraphRowRecord("receipt", r.CollapseId, r.Ns!,
                        r.TenantId, mv.DestNs, mv.NewTenant, Generation: r.Generation,
                        HasIdentityWitness: true));
                }
            }
        }
        else
        {
            warnings.Add("Collapse history could not be strictly read; receipts were NOT migrated and still name the pre-migration namespaces. Repair the history file, then re-run.");
            allMoved = false;
        }

        int legacyEdges = _storage.LoadGlobalEdges().Count(e => e is not null && string.IsNullOrEmpty(e.TenantId));
        if (legacyEdges > 0)
            warnings.Add($"{legacyEdges} legacy-tenant graph edge(s) were not migrated: edges carry no namespace, so their tenant cannot be inferred from a namespace move. Re-key them manually if their endpoints moved tenants.");
        return allMoved;
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
        var reverseWarnings = new List<string>();

        // A pass can move GRAPH rows without leaving a single entry record — a namespace that
        // held only clusters and receipts, or one whose every entry the destination refused.
        // Returning early on "no entry records" then did nothing at all and reported success,
        // stranding those rows at the migrated layout with no way back.
        var namespaceMoves = manifest.NamespaceMoves ?? Array.Empty<MigratedNamespaceRecord>();
        if (actionable.Count == 0 && namespaceMoves.Count == 0
            && manifest.GraphRowMoves is not { Count: > 0 })
            return new TenantMigrationManifest(Array.Empty<MigratedEntryRecord>(), totalBefore, totalBefore);

        // PRE-FLIGHT every original write slot before deleting a single destination row.
        // Reverse owns only the occupation recorded in the manifest; a client may have
        // recreated (tenant,id) at OriginalNs since migration, and overwriting that newer
        // source row after already deleting the migrated destination loses both authority
        // and recoverability. Snapshot all sources up front so decisions for multi-row
        // source/destination groups are made against one stable offline image.
        var originalSnapshots = actionable
            .Select(r => r.OriginalNs)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(ns => ns, ns => _storage.LoadNamespace(ns), StringComparer.Ordinal);
        var destinationSnapshots = actionable
            .Select(r => r.NewNs)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(ns => ns, ns => _storage.LoadNamespace(ns), StringComparer.Ordinal);

        bool IsExactOccupation(CognitiveEntry entry, MigratedEntryRecord record)
            => record.Revision is not null
                && record.LifecycleRevision is not null
                && entry.Revision == record.Revision
                && entry.LifecycleRevision == record.LifecycleRevision;

        var recordsToRestore = new HashSet<MigratedEntryRecord>();
        var recordsToRemoveFromDestination = new HashSet<MigratedEntryRecord>();
        var reversedEntries = new List<MigratedEntryRecord>();

        (bool Blocked, bool SourceExact, CognitiveEntry? DestinationExact) PreflightEntry(
            MigratedEntryRecord record)
        {
            var sourceOccupants = originalSnapshots[record.OriginalNs].Entries.Where(e =>
                string.Equals(e.Id, record.Id, StringComparison.Ordinal)
                && string.Equals(e.TenantId, record.OriginalTenantId,
                    StringComparison.Ordinal)).ToList();
            bool sourceExact = sourceOccupants.Count == 1
                && IsExactOccupation(sourceOccupants[0], record);
            if (sourceOccupants.Count > 0 && !sourceExact)
            {
                reverseWarnings.Add($"Entry '{record.Id}' was not reversed because its original slot ('{record.OriginalNs}', tenant '{record.OriginalTenantId}') is occupied by a different, ambiguous, or legacy-unprovable occupation; the migrated destination was left intact.");
                return (true, false, null);
            }

            var destinationOccupants = destinationSnapshots[record.NewNs].Entries.Where(e =>
                string.Equals(e.Id, record.Id, StringComparison.Ordinal)
                && string.Equals(e.TenantId, record.NewTenantId,
                    StringComparison.Ordinal)).ToList();
            CognitiveEntry? destinationExact = destinationOccupants.Count == 1
                && (record.Revision is null || record.Revision == destinationOccupants[0].Revision)
                && (record.LifecycleRevision is null
                    || record.LifecycleRevision == destinationOccupants[0].LifecycleRevision)
                    ? destinationOccupants[0]
                    : null;

            // An exact source witness proves an earlier reverse already completed this entry.
            // An exact duplicate at the destination can be removed; absence or a replacement
            // is left untouched while topology retries. Without that source proof, the exact
            // migrated destination is required.
            if (sourceExact)
                return (false, true, destinationExact);
            if (destinationExact is not null)
                return (false, false, destinationExact);

            reverseWarnings.Add($"Entry '{record.Id}' was not reversed because its migrated destination slot ('{record.NewNs}', tenant '{record.NewTenantId}') is missing, ambiguous, or replaced and no exact original-source witness proves an earlier reverse.");
            return (true, false, null);
        }

        // Entries and graph rows sharing this exact placement provenance are one reversible
        // unit. If any entry refuses, moving the group's cluster/receipt rows would split
        // topology from the still-migrated entry (and can attach it to a source replacement).
        var blockedPlacements = new HashSet<(string OriginalNs, string OriginalTenant,
            string NewNs, string NewTenant)>();
        foreach (var group in actionable.GroupBy(r =>
                     (r.OriginalNs, OriginalTenant: r.OriginalTenantId,
                         r.NewNs, NewTenant: r.NewTenantId)))
        {
            bool blocked = false;
            var states = new List<(MigratedEntryRecord Record, bool SourceExact,
                CognitiveEntry? DestinationExact)>();
            foreach (var record in group)
            {
                var state = PreflightEntry(record);
                blocked |= state.Blocked;
                states.Add((record, state.SourceExact, state.DestinationExact));
            }

            if (blocked)
            {
                blockedPlacements.Add(group.Key);
                reverseWarnings.Add($"Placement ('{group.Key.OriginalNs}', tenant '{group.Key.OriginalTenant}') <-> ('{group.Key.NewNs}', tenant '{group.Key.NewTenant}') was not reversed as a unit; all of its entries, clusters, and collapse receipts remain at the migrated placement.");
            }
            else
            {
                foreach (var state in states)
                {
                    reversedEntries.Add(state.Record);
                    if (!state.SourceExact)
                        recordsToRestore.Add(state.Record);
                    if (state.DestinationExact is not null)
                        recordsToRemoveFromDestination.Add(state.Record);
                }
            }
        }

        // Remove only exact migrated rows. A replacement destination coexisting with an exact
        // already-reversed source is deliberately absent from this map and remains untouched.
        var byNewNs = recordsToRemoveFromDestination
            .GroupBy(r => r.NewNs)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => (r.NewTenantId, r.Id)));
        var origWrites = new Dictionary<string, List<CognitiveEntry>>();
        var remainderByNewNs = new Dictionary<string, List<CognitiveEntry>>();

        foreach (var (newNs, idMap) in byNewNs)
        {
            var current = destinationSnapshots[newNs];
            var remainder = new List<CognitiveEntry>();

            foreach (var entry in current.Entries)
            {
                // The composite key IS the tenant match; no separate tenant compare is needed.
                if (idMap.TryGetValue((entry.TenantId, entry.Id), out var record))
                {
                    if (recordsToRestore.Contains(record))
                    {
                        if (!origWrites.TryGetValue(record.OriginalNs, out var list))
                        {
                            list = new List<CognitiveEntry>();
                            origWrites[record.OriginalNs] = list;
                        }
                        list.Add(CloneWithNsAndTenant(entry, record.OriginalNs,
                            record.OriginalTenantId));
                    }
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
            // DEDUPED by (tenant, id), restored rows winning — mirroring Migrate's fold: a
            // reverse after a warned partial forward (sources kept) finds the ORIGINALS
            // still present here, and a blind append wrote duplicate-id rows (silent twin
            // ambiguity on JSON, a PK violation mid-reverse on the SQL backends).
            var existing = _storage.LoadNamespace(originalNs);
            var byKey = new Dictionary<(string TenantId, string Id), CognitiveEntry>();
            foreach (var e in existing.Entries)
                if (e is not null && e.Id is not null)
                    byKey[(e.TenantId, e.Id)] = e;
            foreach (var e in restoredEntries)
                byKey[(e.TenantId, e.Id)] = e;
            _storage.SaveNamespaceSync(originalNs, new NamespaceData { Entries = new List<CognitiveEntry>(byKey.Values) });
        }

        // GRAPH ROWS REVERSE BY NAMESPACE PROVENANCE, NOT BY ENTRY PLACEMENT. Clusters and
        // receipts name (Ns, TenantId) and have no per-row manifest, so the only record of
        // where they came from is the namespace relocation the forward pass performed.
        // Deriving it from entry origins was wrong twice over: a source that contributed no
        // surviving entry records was invisible, so its destination looked unambiguous and
        // every graph row there moved back to some other source's namespace; and a pass with
        // no entry records at all skipped the reversal entirely.
        var reverseMoves = new List<(string MatchNs, string? MatchTenant, string DestNs, string NewTenant)>();
        var reverseRowMoves = new List<MigratedGraphRowRecord>();

        // PER-ROW provenance reverses EXACTLY — each recorded row moves back to the (Ns,
        // TenantId) it actually carried before the forward pass, including rows a
        // PrefixSplit re-stamped across tenants, which no placement-level record can
        // attribute. Only manifests without it fall back to the namespace-level records
        // below (and refuse what those cannot attribute).
        // NULL means "no provenance recorded" (an old manifest — fall back and say so);
        // an EMPTY list means "the forward pass provably moved no graph rows" — the
        // reverse must move none. Conflating the two re-armed the placement-level sweep
        // for exactly the manifests that recorded a clean no-op, re-tenanting rows the
        // pass never touched.
        if (manifest.GraphRowMoves is { } recordedRows)
        {
            var approvedRows = recordedRows.Where(r => !blockedPlacements.Contains(
                (r.FromNs, r.FromTenant, r.ToNs, r.ToTenant))).ToList();
            if (approvedRows.Count > 0)
                ReverseGraphRows(approvedRows, reverseWarnings, reverseRowMoves);
        }
        else if (manifest.NamespaceMoves is null)
            reverseWarnings.Add("This manifest predates namespace-move provenance; its cluster rows and collapse receipts were NOT reversed and still name the migrated layout.");

        // Grouped by PLACEMENT — (NewNs, NewTenantId) — never by namespace alone: two tenants
        // folded into one destination namespace are two independent, reversible placements,
        // and each row moves back only under the tenant the forward pass assigned it. Runs
        // only for manifests WITHOUT per-row provenance — reverseMoves stays empty otherwise.
        foreach (var group in manifest.GraphRowMoves is not null
            ? Enumerable.Empty<IGrouping<(string NewNs, string NewTenantId), MigratedNamespaceRecord>>()
            : namespaceMoves.GroupBy(m => (m.NewNs, m.NewTenantId)))
        {
            var sources = group
                .Select(m => (m.OriginalNs, m.OriginalTenantMatch))
                .Distinct()
                .ToList();

            if (sources.Count > 1)
            {
                reverseWarnings.Add($"Placement ('{group.Key.NewNs}', tenant '{group.Key.NewTenantId}') was assembled from {sources.Count} sources; its cluster rows and collapse receipts were NOT reversed and still name the migrated layout.");
                continue;
            }

            var (originalNs, originalTenantMatch) = sources[0];
            if (originalTenantMatch is null)
            {
                // The forward pass moved rows of every tenant at that namespace into one
                // tenant. Nothing records which row belonged to which, so restoring them all
                // under a single guessed tenant would invent an attribution.
                reverseWarnings.Add($"Placement ('{group.Key.NewNs}', tenant '{group.Key.NewTenantId}') was migrated from '{originalNs}' across all tenants; its cluster rows and collapse receipts were NOT reversed because the original tenant of each row was not recorded.");
                continue;
            }

            if (blockedPlacements.Contains((originalNs, originalTenantMatch,
                    group.Key.NewNs, group.Key.NewTenantId)))
                continue;

            reverseMoves.Add((group.Key.NewNs, group.Key.NewTenantId, originalNs, originalTenantMatch));
        }

        MoveGraphRows(reverseMoves, reverseWarnings, reverseRowMoves);

        var totalAfter = _storage.GetPersistedNamespaces()
            .Sum(ns => _storage.LoadNamespace(ns).Entries.Count);

        var reverseRecords = reversedEntries
            .Select(r => new MigratedEntryRecord(r.Id, r.NewNs, r.NewTenantId,
                r.OriginalNs, r.OriginalTenantId, r.Kind, r.Revision, r.LifecycleRevision))
            .ToList();

        // The reverse's OWN manifest carries the provenance of what it just moved — a
        // reverse-of-reverse (or an audit) can then play graph rows back exactly instead of
        // silently skipping them behind a predates-provenance warning.
        return new TenantMigrationManifest(reverseRecords, totalBefore, totalAfter, reverseWarnings,
            NamespaceMoves: null, GraphRowMoves: reverseRowMoves);
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
            newTenantId)
        {
            // Summary OWNERSHIP survives the migration: a moved summary stripped of its
            // stamp/instance would fail every ownership screen and stop matching its
            // record's conditioned cleanup — the migration changes tenancy, never whose
            // summary this is.
            SourceClusterStamp = entry.SourceClusterStamp,
            SourceClusterInstance = entry.SourceClusterInstance,
            // The WITNESSES survive too — RebuildEmbeddings' treatment. A Migrate+Reverse
            // round trip that zeroed LifecycleRevision would make every standing collapse
            // receipt's restore CAS refuse (witness 0 never equals the recorded revision),
            // silently consuming the receipt while restoring nothing; a zeroed Revision
            // invalidates every staged occupancy-pinned judgment the same way.
            Revision = entry.Revision,
            LifecycleRevision = entry.LifecycleRevision
        };
    }
}
