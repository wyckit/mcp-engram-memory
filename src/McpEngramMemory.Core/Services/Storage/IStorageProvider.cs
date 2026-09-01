using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Core.Services.Storage;

/// <summary>
/// Abstraction for data persistence. Implementations handle loading, saving,
/// and debounced writes for namespace data, graph edges, and clusters.
/// </summary>
public interface IStorageProvider : IDisposable
{
    /// <summary>
    /// A stable identity for the backing STORE — the same string for any two provider
    /// instances whose construction addresses the same underlying data through the
    /// canonicalizations the implementations apply: paths are resolved absolute, stripped of
    /// the <c>\\?\</c> extended-length prefix, trimmed of trailing separators and case-folded
    /// on Windows; SQL Server data sources have common local-host aliases and the <c>tcp:</c>
    /// prefix canonicalized. BEST-EFFORT beyond that: an exotic alias the canonicalization
    /// cannot see (a symlink, a DNS alias) splits only the in-process operation gate keyed by
    /// this value — the durable record layer stays serialized regardless, through backend
    /// transactions, the interprocess lock file and the generation CAS. In-process
    /// coordination (the accretion scanner's per-collapse in-flight gate) is keyed by it. The
    /// default is a PER-INSTANCE identity, which honestly says "I promise nothing beyond this
    /// object" — correct for in-memory stubs, and reducing shared-store coordination to
    /// shared-instance coordination rather than silently claiming a scope the provider cannot
    /// vouch for.
    /// </summary>
    string StoreIdentity
        => $"instance:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)}";

    NamespaceData LoadNamespace(string ns);
    IReadOnlyList<string> GetPersistedNamespaces();
    void ScheduleSave(string ns, Func<NamespaceData> dataProvider);
    void SaveNamespaceSync(string ns, NamespaceData data);

    /// <summary>Whether this provider supports per-entry incremental writes (vs full namespace snapshots).</summary>
    bool SupportsIncrementalWrites { get; }

    /// <summary>Schedule a debounced upsert of a single entry. Only called when SupportsIncrementalWrites is true.</summary>
    void ScheduleUpsertEntry(string ns, CognitiveEntry entry);

    /// <summary>Schedule a debounced delete of a single entry. Only called when SupportsIncrementalWrites is true.</summary>
    void ScheduleDeleteEntry(string ns, string entryId);

    /// <summary>
    /// Schedule a debounced tenant-scoped delete of a single entry, targeting the
    /// <c>(tenant_id, ns, id)</c> row so a delete in one tenant can never remove another tenant's
    /// row that shares <c>(ns, id)</c>. The default implementation ignores the tenant and delegates
    /// to <see cref="ScheduleDeleteEntry(string, string)"/> — correct for single-tenant backends
    /// (all rows live in the legacy <c>""</c> tenant). Tenant-partitioned providers (SQL Server)
    /// override this to include the tenant predicate. Additive: existing implementers inherit the
    /// default and need no changes.
    /// </summary>
    void ScheduleDeleteEntry(string ns, string entryId, string tenantId)
        => ScheduleDeleteEntry(ns, entryId);

    List<GraphEdge> LoadGlobalEdges();
    void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider);

    List<SemanticCluster> LoadClusters();
    void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider);

    List<CollapseRecord> LoadCollapseHistory();

    /// <summary>
    /// Upsert ONE collapse record synchronously, as a read-modify-write the provider serializes
    /// AT THE STORE: the store's CURRENT record set is read, this record joins or replaces its
    /// same-id predecessor, and the whole set is committed through the backend's ordinary
    /// durability path (atomic file replace under a store-keyed gate PLUS an exclusively-held
    /// interprocess lock file for the JSON backend; one backend transaction wrapping the read
    /// and the write for the database backends — in every backend, other OS processes and
    /// alias-spelled paths or connection strings are serialized — not an fsync) before this
    /// returns. Record-level on purpose: full-snapshot writes from two components holding
    /// different in-memory views silently erase each other's records, and did.
    ///
    /// True only when the commit demonstrably happened. False means the store is UNCHANGED —
    /// including when the current set could not be read, because committing over an unreadable
    /// set would erase records this caller never saw. A caller using this as a write-ahead
    /// receipt must, on false, refuse the side effects the record was meant to cover. No
    /// default implementation: the debounced save paths swallow backend errors by design, and a
    /// bridge built on them could neither serialize per record nor report — a contract that
    /// cannot be honored must not be inherited silently.
    /// </summary>
    bool UpsertCollapseRecordSync(CollapseRecord record);

    /// <summary>
    /// Delete ONE collapse record synchronously — the same contract and read-modify-write
    /// discipline as <see cref="UpsertCollapseRecordSync(CollapseRecord)"/>. Deleting an
    /// absent record commits nothing and reports true: the store already agrees.
    /// </summary>
    bool DeleteCollapseRecordSync(string collapseId);

    /// <summary>
    /// Conditional twin of <see cref="UpsertCollapseRecordSync(CollapseRecord)"/>: write the
    /// record ONLY while the resident still stands at <paramref name="onlyIfGeneration"/> —
    /// or, when <paramref name="onlyIfGeneration"/> is NULL, only while NO record with the id
    /// exists. Absence is a DISTINCT token, not a sentinel number, because zero is a real
    /// generation: records persisted before the field existed deserialize at 0, and a caller
    /// CASing against such a legacy resident must be able to say "generation 0" without it
    /// meaning "must be absent" — the conflation let an expected-absent write overwrite a
    /// resident legacy record. The compare and the write are one atom inside the provider's
    /// serialized read-modify-write. This is the executor's durable phase transition:
    /// committing the final record through a generation CAS is what FENCES a concurrent
    /// undoer — the undoer's own terminal delete compares the generation it read, so exactly
    /// one of the two terminal operations wins and the loser re-reads. REQUIRED with no
    /// default: a bridge over the unconditional primitives cannot make the compare atomic,
    /// and a contract that cannot be honored must not be inherited silently.
    /// </summary>
    CollapseRecordCas UpsertCollapseRecordSync(CollapseRecord record, long? onlyIfGeneration);

    /// <summary>
    /// Conditional twin of <see cref="DeleteCollapseRecordSync(string)"/>: remove the record
    /// ONLY while it still stands at <paramref name="onlyIfGeneration"/> (see
    /// <see cref="CollapseRecord.Generation"/>). This is the undo's terminal compare-and-delete
    /// — a record whose generation moved carries claims the caller read PAST, and removing it
    /// would discard receipts nobody processed. The compare and the removal are one atom
    /// inside the provider's serialized read-modify-write. REQUIRED with no default, for the
    /// same reason as the conditional upsert.
    /// </summary>
    CollapseRecordCas DeleteCollapseRecordSync(string collapseId, long onlyIfGeneration);

    /// <summary>
    /// STRICT single-record read: true means the store answered definitively —
    /// <paramref name="record"/> is the resident record, or null because none exists. False
    /// means the current set COULD NOT BE READ OR TRUSTED (backend failure, checksum
    /// mismatch) and existence is UNKNOWN; the caller must not treat that as absence. REQUIRED
    /// with no default: <see cref="LoadCollapseHistory"/> deliberately degrades to an empty
    /// list on failure, so a bridge over it would report "absent" for "unreadable" — the exact
    /// conflation this member exists to kill.
    /// </summary>
    bool TryReadCollapseRecord(string collapseId, out CollapseRecord? record);

    /// <summary>
    /// STRICT read of the FULL record set — the same fail-closed contract as
    /// <see cref="TryReadCollapseRecord"/>, for the one consumer that must judge a record
    /// against its PEERS (a legacy undo building its later-record ownership guard): true with
    /// the validated set, false when the set could not be read or trusted. REQUIRED, for the
    /// same reason as the single-record read.
    /// </summary>
    bool TryReadCollapseHistory(out List<CollapseRecord> records);

    /// <summary>
    /// As <see cref="Flush"/>, but REPORTING: true only when every pending write demonstrably
    /// committed at a final queue checkpoint. A write queued while the flush performs backend
    /// I/O must either be committed by that flush or remain visible at the checkpoint and force
    /// false; work published after the checkpoint is ordered after the flush. On failure the
    /// provider must RETAIN the failed writes (re-queued for a later flush) and return false, so
    /// a caller sequencing durability — the undo, which must not retire a receipt while the
    /// restores it covers are still volatile — can fail closed instead of trusting a void return
    /// over swallowed errors. REQUIRED: only the provider knows which of its queues flushed.
    /// </summary>
    bool TryFlush();

    Dictionary<string, DecayConfig> LoadDecayConfigs();
    void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider);

    /// <summary>Load persisted HNSW graph snapshot for a namespace. Returns null if none exists.</summary>
    HnswSnapshot? LoadHnswSnapshot(string ns);

    /// <summary>Save an HNSW graph snapshot for a namespace.</summary>
    void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot);

    /// <summary>Delete persisted HNSW snapshot for a namespace.</summary>
    void DeleteHnswSnapshot(string ns);

    /// <summary>Delete all entries in a namespace from the backing store.</summary>
    Task DeleteNamespaceAsync(string ns);

    /// <summary>
    /// Delete one tenant's partition of a namespace. Implementations should override this
    /// before advertising tenant support. The default preserves legacy empty-tenant behavior
    /// and fails closed for non-legacy tenants.
    /// </summary>
    Task DeleteNamespaceAsync(string ns, string tenantId) =>
        string.IsNullOrWhiteSpace(tenantId)
            ? DeleteNamespaceAsync(ns)
            : Task.FromException(new NotSupportedException(
                "This storage provider does not support tenant-scoped namespace deletion."));

    void Flush();
}

/// <summary>
/// The path canonicalization behind <see cref="IStorageProvider.StoreIdentity"/> for
/// file-backed stores: resolved absolute, <c>\\?\</c>/<c>\\?\UNC\</c> prefixes stripped,
/// trailing separators trimmed, case-folded on Windows. Two spellings this maps to one string
/// address one store; a spelling it cannot see through (symlinks, subst drives) splits only
/// the in-process gate keyed by the identity — never the durable record layer.
/// </summary>
internal static class StoreIdentityUtil
{
    internal static string CanonicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            full = @"\\" + full.Substring(8);
        else if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
            full = full.Substring(4);
        full = Path.TrimEndingDirectorySeparator(full);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }
}
