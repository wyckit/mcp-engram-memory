using System.Collections.Concurrent;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services;

/// <summary>
/// Identifies a storage partition by tenant + namespace. The legacy single-tenant partition is
/// <c>Tenant == ""</c>. Value equality (record struct) makes it a safe dictionary key.
/// </summary>
internal readonly record struct NsKey(string Tenant, string Ns);

/// <summary>
/// Raised by a storage provider that could not establish which namespaces are persisted.
///
/// It exists because success and failure used to be the same value: every provider caught its
/// listing error and returned an empty list, which is exactly what a store with nothing in it
/// returns. A caller cannot distinguish those by inspecting the result, so the only way to make the
/// distinction impossible to miss is to make failure a control-flow event.
///
/// The consequence is a security one before it is an availability one. A namespace that fails to
/// list is a namespace whose entries are invisible, and an invisible persisted twin makes a
/// duplicated id look unique — so the ACL-blind, tenant-wide duplicate test that topology must fail
/// closed on passes instead. "Could not enumerate" therefore has to reach the caller as a refusal,
/// never as "there is nothing there".
///
/// This type is public because it escapes public provider methods; a caller that cannot name it
/// cannot catch it. The message deliberately carries no backend detail — no SQL, no path, no
/// namespace name — and the provider keeps the cause as <see cref="Exception.InnerException"/> and
/// logs it. It is also raised uniformly for every id and every principal, so it is not an oracle:
/// it says the store could not be listed, never anything about whether some entry exists.
/// </summary>
public sealed class NamespaceEnumerationException : Exception
{
    /// <summary>Wrap the backend failure that prevented the namespace listing.</summary>
    public NamespaceEnumerationException(Exception innerException)
        : base("Failed to enumerate persisted namespaces.", innerException)
    {
    }
}

/// <summary>
/// Manages tenant + namespace-partitioned storage of cognitive entries with lazy loading from disk.
/// Partitions are keyed by <see cref="NsKey"/> = (tenant, ns); the legacy tenant is <c>""</c>, so
/// for every pre-tenant (no-tenant) caller the partition key is exactly the namespace and behavior
/// is byte-for-byte identical to the single-tenant design.
///
/// Infrastructure is thread-safe via ConcurrentDictionary. Per-partition entry dictionaries are
/// also ConcurrentDictionary, allowing CognitiveIndex to use ReadLock for read paths and WriteLock
/// only for mutations.
///
/// Tenant isolation: the reverse id locator (<see cref="_idToNamespace"/>) and the total-count
/// atomic track the LEGACY tenant only. This is deliberate — the global, tenant-less id operations
/// (Get(id)/Delete(id) and the id→ns resolvers) must resolve strictly within the legacy tenant so
/// a caller can never probe or reach another tenant's entry by id alone. Tenant partitions are
/// reached only through the explicit tenant-scoped APIs.
///
/// The candidate index (<see cref="_idCandidates"/>) is a second, independent structure and covers
/// every tenant precisely because it is tenant-QUALIFIED: a lookup names the tenant it wants, so
/// tracking another tenant's placements cannot widen what a tenant-less caller reaches. The two are
/// not interchangeable — the locator answers "the one namespace" (tenant-blind, last writer wins),
/// the candidate index answers "every namespace of THIS tenant", which is the only form in which
/// unique-versus-ambiguous is decidable.
/// </summary>
internal sealed class NamespaceStore
{
    /// <summary>Minimum partition size to activate HNSW indexing.</summary>
    private const int HnswThreshold = 200;

    // The separator used to compose a tenant-scoped partition key lives on Tenancy, together with
    // Tenancy.ValidatePartitionComponent — the guard that makes "never appears in a namespace or
    // tenant id" an enforced fact instead of an assumption. It is enforced where untrusted strings
    // become key components (Tenancy.Normalize, the CognitiveEntry write ctor, and the read/delete/
    // config paths that never construct an entry), so composition below can be injective.

    private readonly ConcurrentDictionary<NsKey, ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>> _namespaces = new();
    // Loaded tracking + load locks are keyed by NAMESPACE (the persistence unit): one LoadNamespace
    // call materializes every tenant's rows for that ns and buckets them into the right partition.
    private readonly ConcurrentDictionary<string, bool> _loadedNamespaces = new();
    // Completion tracking for LoadAll. _namespaceSetGeneration is bumped whenever a namespace is
    // un-loaded — the only in-process event that can make "every persisted namespace has been
    // materialized" stop being true — and _loadedGeneration records the generation the last sweep
    // that ran to completion started under. Monotonic, so a completion recorded against an older
    // generation can never be mistaken for a current one; -1 because generation 0 is the initial,
    // never-swept state.
    private long _namespaceSetGeneration;
    private long _loadedGeneration = -1;
    // Single-flight gate for the full sweep. Cold callers queue here so N of them pay ONE
    // enumeration rather than N: the leader sweeps and publishes, and each waiter's re-check under
    // the gate then finds the completion already covering it and returns without touching the
    // provider. Only the cold path reaches this — a warm LoadAll returns on two atomic reads taken
    // above the lock, allocating nothing and blocking nobody.
    //
    // Lock ordering, and why this cannot deadlock: the gate is strictly OUTERMOST of the two locks
    // NamespaceStore takes. LoadAll acquires it and then EnsureLoaded's per-namespace load lock
    // beneath it, and EnsureLoaded never calls LoadAll, so the reverse edge does not exist.
    // CognitiveIndex's per-partition ReaderWriterLockSlim sits above both and never inverts either:
    // every LoadAll caller resolves before taking a partition lock, and nothing reachable from this
    // gate acquires one.
    private readonly object _sweepLock = new();
    // LEGACY-ONLY reverse index: id → ns. Only legacy-tenant ("") entries are tracked here so the
    // global id-based operations resolve strictly within the legacy tenant.
    private readonly ConcurrentDictionary<string, string> _idToNamespace = new();
    // Candidate index: (tenant, id) → every namespace of that tenant currently holding the id.
    // Set-valued because (tenant, ns, id) is the real identity — one id legitimately occurs in
    // several namespaces of one tenant, and a bare-id resolver that saw only one of them would
    // call an ambiguous id unique and then act on an arbitrary twin. Keyed by value tuple so
    // equality is structural and ordinal, and valued by a ConcurrentDictionary used as a set
    // because the BCL has no concurrent set, so a lookup never blocks a placement or vice versa.
    private readonly ConcurrentDictionary<(string Tenant, string Id), ConcurrentDictionary<string, byte>> _idCandidates = new();
    // Publishing a bucket and retiring an emptied one are the one pair that must not interleave: a
    // retirement decides on emptiness and then unpublishes that instance, and a placement written
    // into the same instance in between is left live but unreachable. That is not merely a lost
    // entry — an id occupying two namespaces then reads back as occupying one, so the tenant-wide
    // duplicate test that topology fails closed on passes instead. The two are therefore serialized
    // per (tenant, id) by these stripes, while lookups stay lock-free and unaffected. Striping
    // rather than a lock object per key because a per-key lock would itself need the
    // publish/retire protocol it exists to provide. 64 is deliberately modest: the critical section
    // is two dictionary operations, so a hash collision between unrelated ids costs almost nothing,
    // and the array is allocated once per store.
    private const int CandidateStripeCount = 64;
    private readonly object[] _candidateStripes =
        Enumerable.Range(0, CandidateStripeCount).Select(_ => new object()).ToArray();
    // Monotonic per-tenant counter of AMBIGUITY-BOUNDARY crossings in the candidate index: an id
    // gaining a second namespace of the tenant, or dropping back to one.
    //
    // It exists because attribution can change with no topology write at all. A same-id twin is an
    // ordinary entry insert — it touches no edge and no cluster — yet every edge naming that id
    // becomes unusable the moment it lands. A consumer that caches something derived from the
    // ATTRIBUTABLE view therefore cannot detect the change by watching a graph revision, because
    // the graph did not change; only the meaning of its bare ids did. This counter is the missing
    // half of that freshness test.
    //
    // Only the CROSSING bumps, never every placement. Attribution is the predicate "this tenant
    // holds the id in at most one namespace", so 0 -> 1 and 2 -> 3 change nothing any consumer can
    // observe, while bumping on every track/untrack would invalidate every derived cache on every
    // entry write — a cache invalidated by all writes is not a cache.
    //
    // The crossing is decidable exactly because both mutators already run under this key's stripe:
    // publication and retirement are serialized per (tenant, id), so the bucket size read on either
    // side of a mutation cannot interleave with another mutation of the same bucket.
    //
    // Tenant-wide rather than per-namespace, deliberately, and the cost is real: a crossing
    // anywhere in the tenant invalidates that tenant's derived caches, not only the namespaces the
    // crossing id occupies. Taken because attribution IS a tenant-scoped predicate and the consumer
    // already keys freshness by tenant, so this composes with the graph revision beside it as one
    // more long; and because a crossing requires an id to gain or lose a twin, which is rare enough
    // that the extra rebuilds cost less than a per-namespace variant that has to decide, under the
    // stripe, which namespaces a bucket named at the instant its size changed.
    private readonly ConcurrentDictionary<string, long> _attributionRevisions = new();
    // THE ATTRIBUTION FENCE: one reader/writer primitive per tenant, and the only thing in this
    // process that makes an attribution decision safe to ACT on rather than merely fresh when it
    // was taken.
    //
    // The counter above is a freshness signal and nothing more. A topology writer samples it, then
    // mutates; between the sample and the mutation a twin can still land, because nothing
    // serializes a candidate-index write against a graph or cluster write. Sampling narrows that
    // interval, it does not close it — a narrower race is still a race, and the writer that loses
    // it publishes a bare id two entries answer to.
    //
    // So the two sides are made mutually exclusive:
    //   SHARED  — every topology mutation (KnowledgeGraph's five mutators, ClusterManager's four),
    //             held across its final attribution validation AND its mutation. Shared, because
    //             topology writers do not conflict with each other over attribution; they each
    //             have their own lock for their own structure.
    //   EXCLUSIVE — an index write that CHANGES AMBIGUITY, which is exactly the (tenant, id)
    //             1 <-> 2 crossing detected in TrackCandidate / UntrackCandidate below. Held
    //             across the bucket mutation and its BumpAttribution, so a writer holding the
    //             shared side cannot observe the counter before the crossing and the placement
    //             after it.
    //
    // An ordinary entry write crosses no boundary and takes NEITHER side — see the two-phase shape
    // of TrackCandidate. That is not an optimization but the difference between a fence and a
    // global write serializer: making every upsert take the exclusive side would queue every entry
    // write in a tenant behind every graph write in it.
    //
    // LOCK ORDER, AND IT IS ASYMMETRIC. An earlier draft of this remark said the fence is simply
    // "the outermost lock in the process"; it is outermost on one side only, and the difference is
    // what decides how a long hold is felt elsewhere, so it is stated rather than smoothed over.
    //
    //   SHARED side — OUTERMOST. Taken before the holder's own structural lock (graph lock,
    //     cluster lock) and released after it. Nothing reachable while it is held acquires a
    //     partition lock, a load lock or the sweep lock, and nothing reachable while it is held
    //     loads, calls a storage provider, or allocates per element.
    //
    //   EXCLUSIVE side — INNERMOST. It is taken from Track/UntrackCandidate, which already run
    //     under a partition lock in WRITE mode (CognitiveIndex.Upsert/UpsertBatch/Delete/
    //     RecordAccess/SetLifecycleState/SetActivationEnergyAndState/DeleteAllInNamespace/
    //     RebuildEmbeddings), under a partition lock in READ mode (Search/FindDuplicates/
    //     GetStateCounts, each of which holds it across EnsureLoaded -> LoadEntries), under
    //     _loadLocks[ns], or — via LoadAll — under the process-global sweep lock. The real order
    //     there is {partition or load or sweep lock} -> fence -> stripe.
    //
    // NO CYCLE, and that is a deduction rather than a hope: no fence holder ever asks for a
    // partition lock, a load lock or the sweep lock, so the two orders share no edge and cannot
    // close one. Deadlock-freedom does not depend on the asymmetry going away.
    //
    // WHAT THE ASYMMETRY COSTS, because it is real and only bounded, never absent: a crossing waits
    // for the fence while HOLDING a partition lock, and ReaderWriterLockSlim prefers writers — so
    // every later reader of that partition queues behind the blocked crossing. A long shared hold
    // therefore reads as unavailability of a namespace the shared holder never named. The
    // mitigation has to live on the shared side, where the hold is, and it does: KnowledgeGraph
    // .AddEdges — the one fenced section whose size is chosen by a caller — releases and retakes
    // the fence every AddEdgesFenceChunk edges instead of spanning the whole batch, and every other
    // fenced section is bounded by structures this process already holds in memory.
    //
    // Per tenant, deliberately: attribution IS a tenant-scoped predicate, so a crossing in tenant A
    // must not stall tenant B's topology writes. That separation holds for the fences themselves;
    // it is NOT a claim about _loadLocks, which are per namespace and not per tenant, so one
    // namespace holding rows for two tenants materializes both under one monitor. Keyed by the
    // NORMALIZED tenant, which every accessor guarantees, or two spellings would fence against two
    // different locks.
    //
    // Non-recursive by construction (ReaderWriterLockSlim's default policy): re-entering either
    // side on one thread throws rather than silently nesting, which is the behaviour wanted — no
    // mutator here calls another mutator, and that is a property worth having enforced rather than
    // documented.
    private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> _attributionFences = new();
    // Raised by DisposeAttributionFences before it walks, read by AttributionFenceFor. An int
    // rather than a bool so Volatile.Read/Write matches the shape CognitiveIndex already uses for
    // its own _disposedFlag.
    private int _fencesDisposedFlag;
    // BM25/HNSW sub-indexes are keyed by the composed partition-key STRING (see PartitionKey).
    private readonly ConcurrentDictionary<string, HnswIndex> _hnswIndices = new();
    private readonly ConcurrentDictionary<string, object> _loadLocks = new();
    private readonly IStorageProvider _persistence;
    private readonly BM25Index _bm25;

    // Atomic total-entry count for the LEGACY tenant across all namespaces — maintained by
    // TrackEntry / UntrackEntry / RemoveNamespace / LoadEntries so memory-limits checks can run
    // without a cross-namespace lock. Approximate under concurrent mutations but reliably
    // incremented once per distinct legacy id insertion. Tenant partitions are excluded by design
    // (they are unreachable via the global id/count paths).
    private long _totalCountApprox;

    public NamespaceStore(IStorageProvider persistence, BM25Index bm25)
    {
        _persistence = persistence;
        _bm25 = bm25;
    }

    /// <summary>Compose the partition-key string for BM25/HNSW sub-indexes. Legacy tenant → the namespace itself.</summary>
    /// <exception cref="ArgumentException">
    /// Either component carries a control character, which would make the composition non-injective
    /// and let one caller forge another tenant's key.
    /// </exception>
    public static string PartitionKey(string tenant, string ns)
    {
        // Composition is injective only while neither component carries the separator, so validating
        // here — the one place a key is ever composed — is what actually holds the invariant up. Every
        // other entry point (decay configs, diffusion bases, index invalidation) reaches a partition
        // through this method, so one check covers them all rather than each site remembering.
        //
        // This throws rather than asserting on purpose: a failing Debug.Assert calls
        // Environment.FailFast, which would turn a containment bug into a killed process (and a dead
        // test host) instead of a catchable error, and would vanish entirely in Release — exactly
        // where forging matters. The cost is two vectorized scans of a short string, well below the
        // allocation on the next line.
        Tenancy.ValidatePartitionComponent(tenant, nameof(tenant));
        Tenancy.ValidatePartitionComponent(ns, nameof(ns));
        return ComposeKeyUnchecked(tenant, ns);
    }

    /// <summary>
    /// The key format itself, with no assertion. Reserved for the recovery paths that must be able
    /// to key an ALREADY-poisoned component — the whole point of those paths is to survive data the
    /// assert exists to keep out, so they cannot route through <see cref="PartitionKey(string,string)"/>.
    /// </summary>
    private static string ComposeKeyUnchecked(string tenant, string ns)
        => tenant.Length == 0 ? ns : string.Concat(tenant, Tenancy.PartitionSeparator.ToString(), ns);

    /// <summary>Compose the partition-key string for a <see cref="NsKey"/>.</summary>
    public static string PartitionKey(NsKey key) => PartitionKey(key.Tenant, key.Ns);

    /// <summary>
    /// Build the partition-keyed decay-config map every storage provider hands to the lifecycle
    /// engine. Shared here because the collision it has to survive is a property of the key format,
    /// not of any one backend: a store written before <see cref="Tenancy.ValidatePartitionComponent"/>
    /// existed can hold two rows that compose to the same key, and the obvious
    /// <c>ToDictionary</c> throws on that — turning a historical bad write into a host that cannot
    /// boot, with manual database repair as the only way out. Keeping one row deterministically
    /// (tenant-scoped ahead of legacy, then stored order) degrades a poisoned pair to a logged
    /// warning while leaving every well-formed store byte-for-byte unchanged.
    /// </summary>
    internal static Dictionary<string, DecayConfig> DecayConfigsByPartition(
        List<DecayConfig>? configs, ILogger? logger)
    {
        var map = new Dictionary<string, DecayConfig>();
        if (configs is null)
            return map;

        // OrderBy is a stable sort, so within each group the stored order decides. That makes the
        // survivor arbitrary but reproducible across boots, which is what keeps decay behaviour
        // from flipping between restarts.
        foreach (var config in configs.OrderBy(c => c.TenantId.Length == 0 ? 1 : 0))
        {
            // ComposeKeyUnchecked, not PartitionKey: the rows being keyed here are exactly the ones
            // that may already carry a separator, so the assert would abort the boot this method
            // exists to rescue.
            if (map.TryAdd(ComposeKeyUnchecked(config.TenantId, config.Ns), config))
                continue;

            logger?.LogWarning(
                "Duplicate decay-config partition key for tenant '{TenantId}' namespace '{Namespace}' — " +
                "keeping the first row in tenant-scoped-first order and discarding the rest. This store " +
                "was written before partition-component validation and should be repaired.",
                EscapeControlChars(config.TenantId), EscapeControlChars(config.Ns));
        }
        return map;
    }

    /// <summary>
    /// Render a partition component safely for a log line. A poisoned store is exactly the case
    /// where these strings may hold newlines, so echoing them raw would let a bad write forge log
    /// records. Never used on a success path.
    /// </summary>
    private static string EscapeControlChars(string value)
        => value.Any(char.IsControl)
            ? string.Concat(value.Select(c => char.IsControl(c) ? $"\\u{(int)c:X4}" : c.ToString()))
            : value;

    /// <summary>Get the entry dictionary for the legacy-tenant partition of a namespace (null if absent).</summary>
    public ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>? GetNamespace(string ns)
        => GetNamespace(new NsKey(string.Empty, ns));

    /// <summary>Get the entry dictionary for a tenant partition (null if it doesn't exist).</summary>
    public ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>? GetNamespace(NsKey key)
        => _namespaces.TryGetValue(key, out var entries) ? entries : null;

    /// <summary>Get or create the entry dictionary for the legacy-tenant partition of a namespace.</summary>
    public ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> GetOrCreateNamespace(string ns)
        => GetOrCreateNamespace(new NsKey(string.Empty, ns));

    /// <summary>Get or create the entry dictionary for a tenant partition.</summary>
    public ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> GetOrCreateNamespace(NsKey key)
        => _namespaces.GetOrAdd(key, _ => new ConcurrentDictionary<string, (CognitiveEntry, float, QuantizedVector?)>());

    /// <summary>
    /// Remove the LEGACY-tenant partition of a namespace entirely from in-memory state (entries,
    /// locator, BM25, HNSW, loaded tracking). Only removes locator entries that still point at this
    /// ns — an orphaned id that was later upserted into a different ns keeps its updated locator +
    /// count entry. Tenant partitions of the same ns are untouched (they are managed independently).
    /// The namespace is validated by the <see cref="NsKey"/> overload this delegates to, which runs
    /// before any state is touched.
    /// </summary>
    public void RemoveNamespace(string ns)
    {
        var key = new NsKey(string.Empty, ns);
        RemoveNamespace(key);
        _loadedNamespaces.TryRemove(ns, out _);

        // Un-load first, invalidate second, and never the other way round. LoadAll reads the
        // generation before its sweep, so bumping after the un-load guarantees that any completion
        // published by a sweep which ran with this namespace already gone carries a generation the
        // next caller will reject. Bumping first would let such a sweep read the NEW generation,
        // find the namespace still marked loaded, and then publish a completion that our removal
        // falsifies a moment later — a namespace persisted but permanently unmaterialized, which is
        // the stale MISS the candidate index must never serve.
        //
        // The NsKey overload deliberately does not bump: it empties a partition but leaves the
        // namespace loaded, so "every persisted namespace has been materialized" still holds.
        Interlocked.Increment(ref _namespaceSetGeneration);
    }

    /// <summary>
    /// Remove exactly one tenant + namespace partition from in-memory state, its candidate-index
    /// placements and its search indexes. Other tenant partitions with the same namespace are left
    /// loaded and untouched. Persistence rows are deliberately not deleted here; the caller must
    /// schedule deletes for the removed entry ids so incremental providers can target the full
    /// (tenant, ns, id) key.
    /// </summary>
    public void RemoveNamespace(NsKey key)
    {
        // A delete reaches storage without ever constructing a CognitiveEntry, so the entry write
        // ctor's guard does not cover it. Validate before anything is removed: a namespace carrying
        // the separator would compose to another tenant's partition key and clear that tenant's
        // BM25/HNSW indexes and persisted snapshot.
        Tenancy.ValidatePartitionComponent(key.Tenant, nameof(key));
        Tenancy.ValidatePartitionComponent(key.Ns, nameof(key));

        if (_namespaces.TryRemove(key, out var entries))
        {
            int removed = 0;
            foreach (var id in entries.Keys)
            {
                // The candidate index is retracted for EVERY tenant, not just the legacy one: it
                // is keyed by the full (tenant, ns, id) placement, so a tenant partition skipped
                // here would keep naming a namespace whose entries no longer exist — and a
                // candidate that outlives its partition is exactly the stale resolution this
                // index exists to prevent.
                UntrackCandidate(id, key.Ns, key.Tenant);

                if (key.Tenant.Length != 0)
                    continue;

                // Use the KeyValuePair overload of TryRemove so we only delete a locator entry
                // when it currently points at THIS namespace. Guards against a rare but real
                // scenario: id X was upserted to ns=A (orphan), then re-upserted to ns=B (locator
                // now points at B). If we unconditionally TryRemove(X), we'd blow away B's
                // locator AND decrement the total count while B's entries dict still has X —
                // driving TotalCount negative.
                if (_idToNamespace.TryRemove(new KeyValuePair<string, string>(id, key.Ns)))
                    removed++;
            }
            if (removed > 0)
                Interlocked.Add(ref _totalCountApprox, -removed);
        }
        string pk = PartitionKey(key);
        _bm25.ClearNamespace(pk);
        _hnswIndices.TryRemove(pk, out _);
        _persistence.DeleteHnswSnapshot(pk);
    }

    /// <summary>All partition entry dictionaries (for cross-partition diagnostic operations).</summary>
    public IEnumerable<ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>> AllNamespaces
        => _namespaces.Values;

    /// <summary>Total legacy-tenant entries across all namespaces. O(1) atomic read — safe without a lock.</summary>
    public int TotalCount => (int)Interlocked.Read(ref _totalCountApprox);

    /// <summary>
    /// Get all known namespace names (loaded + persisted), tenant-independent. A provider that
    /// cannot list throws through rather than silently degrading to the resident namespaces only —
    /// a truncated list here reads exactly like a smaller store, which is the confusion this
    /// contract exists to prevent.
    /// </summary>
    /// <exception cref="NamespaceEnumerationException">The persisted namespaces could not be listed.</exception>
    public IReadOnlyList<string> GetNamespaceNames()
    {
        var persisted = _persistence.GetPersistedNamespaces();
        var inMemory = _namespaces.Keys.Select(k => k.Ns);
        return persisted.Union(inMemory).Distinct().ToList();
    }

    /// <summary>
    /// Get namespace names that currently contain entries for one tenant. Callers must load all
    /// persisted namespaces first when they require complete discovery. Empty legacy partitions
    /// created as a side effect of loading another tenant are excluded.
    /// </summary>
    public IReadOnlyList<string> GetNamespaceNames(string tenantId)
        => _namespaces
            .Where(kv => kv.Key.Tenant == tenantId && !kv.Value.IsEmpty)
            .Select(kv => kv.Key.Ns)
            .Distinct()
            .ToList();

    /// <summary>
    /// Distinct tenant ids across all loaded partitions (includes the legacy tenant "" when legacy
    /// data is present). Callers that need complete discovery must <see cref="LoadAll"/> first.
    /// </summary>
    public IReadOnlyList<string> GetAllTenants()
        => _namespaces.Keys.Select(k => k.Tenant).Distinct().ToList();

    /// <summary>Snapshot the entry dictionaries belonging to exactly one tenant.</summary>
    public IEnumerable<ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>>
        GetTenantNamespaces(string tenantId)
        => _namespaces
            .Where(kv => kv.Key.Tenant == tenantId)
            .Select(kv => kv.Value);

    /// <summary>
    /// Ensure a namespace is loaded from disk (all tenants). Thread-safe via per-namespace load lock
    /// with double-check pattern. Multiple namespaces can be loaded concurrently. Loaded rows are
    /// bucketed into their (tenant, ns) partitions by <see cref="CognitiveEntry.TenantId"/>.
    /// </summary>
    public void EnsureLoaded(string ns)
    {
        if (_loadedNamespaces.ContainsKey(ns))
            return;

        // Per-namespace lock prevents concurrent double-loading of the same namespace
        lock (_loadLocks.GetOrAdd(ns, _ => new object()))
        {
            if (_loadedNamespaces.ContainsKey(ns))
                return; // Another thread loaded while we waited

            var data = _persistence.LoadNamespace(ns);
            // Ensure the legacy partition exists even when empty, matching prior behavior where
            // GetNamespace(ns) returned a (possibly empty) dictionary after EnsureLoaded.
            _namespaces.GetOrAdd(new NsKey(string.Empty, ns), _ => new ConcurrentDictionary<string, (CognitiveEntry, float, QuantizedVector?)>());

            LoadEntries(ns, data.Entries);

            // Build BM25 + restore HNSW per (tenant, ns) partition so a search never mixes tenants
            // at the candidate stage. For the legacy tenant this is identical to the single-tenant
            // path (partition key == ns, one group).
            foreach (var group in data.Entries.GroupBy(e => e.TenantId))
            {
                string pk = PartitionKey(group.Key, ns);
                var groupList = group as IList<CognitiveEntry> ?? group.ToList();

                if (!_bm25.HasNamespace(pk))
                    _bm25.RebuildNamespace(pk, groupList);

                // Try to restore HNSW from persisted snapshot (avoids O(N log N) rebuild)
                if (groupList.Count >= HnswThreshold && !_hnswIndices.ContainsKey(pk))
                    TryRestoreHnsw(pk, new NsKey(group.Key, ns));
            }

            _loadedNamespaces.TryAdd(ns, true);
        }
    }

    /// <summary>
    /// Materialize every persisted namespace, then remember that it was done.
    ///
    /// The enumeration is the expensive half and it is what the cache removes: a directory listing
    /// for the JSON provider, a <c>SELECT DISTINCT ns</c> for the database ones, paid on every call,
    /// while the <see cref="EnsureLoaded"/> that follows is a dictionary hit for everything already
    /// resident. Every bare-id resolution reaches this method (see
    /// <see cref="GetCandidateNamespaces"/>), so leaving the enumeration uncached puts one storage
    /// round trip on a lookup that otherwise touches only memory — the end-to-end scaling the
    /// candidate index was supposed to remove.
    ///
    /// Caching is exactly as complete as re-sweeping, for this process. A namespace persisted BY
    /// this process was materialized before it could be written — the write paths call
    /// <see cref="EnsureLoaded"/> under the partition write lock before creating the partition — so
    /// it is already resident and already in the candidate index without any sweep. The one
    /// in-process event that falsifies the claim is un-loading a namespace, and
    /// <see cref="RemoveNamespace(string)"/> reports that by bumping the generation. A namespace
    /// another PROCESS creates is not discovered, but that boundary already existed: EnsureLoaded
    /// never re-reads a namespace it has loaded, so another process's rows in any known namespace
    /// were already invisible, and one server process per data directory is the documented
    /// supported topology.
    ///
    /// Concurrency: the generation is read BEFORE the sweep and republished after it, so an un-load
    /// landing mid-sweep leaves an already-stale generation recorded and the next caller sweeps
    /// again. Cold callers are single-flighted through <see cref="_sweepLock"/>: the first one
    /// enumerates while the rest queue, and each of those then re-checks and returns on the
    /// completion the leader published, so a cold burst of N callers costs one enumeration rather
    /// than N. The warm path never takes the lock.
    ///
    /// Failure: an enumeration that throws propagates, and completion is NOT published. That is the
    /// deliberate choice between the two ways to be wrong here. Degrading — swallowing the error and
    /// carrying on over whatever was listed — would hand the caller a store that could not be read
    /// dressed as a store with nothing in it, and a bare id whose twin lives in an unlisted
    /// namespace then reads back as unique, which is the fail-OPEN answer to the tenant-wide
    /// duplicate test. Refusing costs availability on a path that was already broken and keeps
    /// "I could not establish the namespace set" from being spelled the same way as "there is
    /// nothing there". Waiters are never handed a poisoned success: the only thing they observe is
    /// the published generation, and a failed sweep publishes nothing, so the next caller in the
    /// queue retries the enumeration itself.
    /// </summary>
    /// <exception cref="NamespaceEnumerationException">
    /// The storage provider could not list the persisted namespaces. The cache is left unpublished,
    /// so a later call retries rather than serving an empty sweep.
    /// </exception>
    public void LoadAll()
    {
        // Warm path: two atomic reads, no allocation, no lock, no queueing behind anyone. This is a
        // fast-path test only — the generation that gets published is the one re-read under the gate.
        if (Interlocked.Read(ref _namespaceSetGeneration) == Interlocked.Read(ref _loadedGeneration))
            return;

        lock (_sweepLock)
        {
            // Re-read under the gate rather than trusting the pre-check above it: while we queued,
            // the leader may have completed the very sweep we were about to duplicate. Reading the
            // generation here (not before the lock) is also what keeps the published value honest —
            // it is the generation this sweep actually runs under.
            long generation = Interlocked.Read(ref _namespaceSetGeneration);
            if (Interlocked.Read(ref _loadedGeneration) == generation)
                return;

            // Publication is the line AFTER the loop, so a throw from either the enumeration or a
            // namespace load leaves _loadedGeneration untouched and the next caller sweeps again.
            // Marking an incomplete sweep complete is the one outcome that must never happen: it
            // would make a persisted twin permanently invisible to the ambiguity test.
            foreach (var ns in _persistence.GetPersistedNamespaces())
                EnsureLoaded(ns);

            // Exchange rather than a plain store: it fences the sweep ahead of the publication, so
            // no other thread can observe the completion before the namespaces it vouches for are
            // loaded.
            Interlocked.Exchange(ref _loadedGeneration, generation);
        }
    }

    /// <summary>
    /// Snapshot current namespace data (ALL tenant partitions of the namespace) and schedule a
    /// debounced write to disk. The full snapshot spans every tenant because the persistence unit
    /// is the namespace; each entry carries its own <see cref="CognitiveEntry.TenantId"/>.
    /// </summary>
    public void ScheduleSave(string ns)
    {
        var data = new NamespaceData();
        var list = new List<CognitiveEntry>();
        foreach (var kv in _namespaces)
        {
            if (kv.Key.Ns != ns) continue;
            list.AddRange(kv.Value.Values.Select(t => t.Entry));
        }
        data.Entries = list;

        _persistence.ScheduleSave(ns, () => data);

        // Persist HNSW snapshots alongside namespace data (one per tenant partition)
        ScheduleHnswSave(ns);
    }

    /// <summary>Schedule an incremental upsert (SQLite/SQL Server) or full snapshot (JSON) for a single entry.</summary>
    public void ScheduleEntryUpsert(string ns, CognitiveEntry entry)
    {
        if (_persistence.SupportsIncrementalWrites)
            _persistence.ScheduleUpsertEntry(ns, entry);
        else
            ScheduleSave(ns);
    }

    /// <summary>
    /// Schedule a tenant-scoped incremental delete (SQLite/SQL Server) or full snapshot (JSON) for a
    /// single entry. The tenant is threaded to the provider so an incremental delete targets the
    /// full (tenant, ns, id) key and never removes a co-keyed row in another tenant.
    /// </summary>
    public void ScheduleEntryDelete(string ns, string entryId, string tenantId)
    {
        if (_persistence.SupportsIncrementalWrites)
            _persistence.ScheduleDeleteEntry(ns, entryId, tenantId);
        else
            ScheduleSave(ns);
    }

    /// <summary>Index an entry in BM25 under its (tenant, ns) partition for keyword search.</summary>
    public void IndexBM25(CognitiveEntry entry)
        => _bm25.Index(entry, PartitionKey(entry.TenantId, entry.Ns));

    /// <summary>Remove an entry from the BM25 keyword index for the given partition key.</summary>
    public void RemoveBM25(string id, string partitionKey) => _bm25.Remove(id, partitionKey);

    // ── Id Locators: the LEGACY-only reverse index (entryId → one ns) and the tenant-qualified
    //    candidate index ((tenant, entryId) → every ns holding it). Both are maintained by the
    //    same Track/Untrack pair so they cannot drift apart. ──

    /// <summary>Resolve a legacy-tenant namespace via locator, falling back to LoadAll if not found.</summary>
    public bool TryResolveOrLoad(string entryId, out string ns)
    {
        if (_idToNamespace.TryGetValue(entryId, out ns!))
            return true;
        LoadAll();
        return _idToNamespace.TryGetValue(entryId, out ns!);
    }

    /// <summary>
    /// Record that (<paramref name="tenantId"/>, <paramref name="ns"/>) holds
    /// <paramref name="entryId"/>, in the candidate index for every tenant and additionally in the
    /// LEGACY locator + total count. Tenant entries (non-empty tenantId) are intentionally NOT put
    /// in the locator, keeping the global id/count paths legacy-scoped.
    /// Increments TotalCount atomically only when the id was not already tracked (so upsert-of-existing
    /// doesn't drift the count).
    /// </summary>
    public void TrackEntry(string entryId, string ns, string tenantId)
    {
        // Upsert never removes the id from a namespace it previously occupied, so the candidate
        // set grows rather than moves — which is what makes it agree with a full scan, where both
        // twins would have been found. The locator below moves instead, and that difference is
        // exactly why it cannot answer the ambiguity question.
        TrackCandidate(entryId, ns, tenantId);

        if (tenantId.Length != 0)
            return; // tenant partitions are excluded from the global locator/count by design

        if (_idToNamespace.TryAdd(entryId, ns))
            Interlocked.Increment(ref _totalCountApprox);
        else
            _idToNamespace[entryId] = ns;
    }

    /// <summary>
    /// Retract one (<paramref name="tenantId"/>, <paramref name="ns"/>, <paramref name="entryId"/>)
    /// placement from the candidate index, and for the legacy tenant also from the locator +
    /// TotalCount. The namespace and tenant are required rather than looked up: the candidate index
    /// is set-valued, so retracting the right placement needs the full triple, and a delete in one
    /// namespace must not evict a surviving twin in another.
    /// </summary>
    public void UntrackEntry(string entryId, string ns, string tenantId)
    {
        UntrackCandidate(entryId, ns, tenantId);

        if (tenantId.Length != 0)
            return;

        // Deliberately unconditional on ns, unlike RemoveNamespace above: this is the pre-existing
        // legacy-locator contract and the delete paths that call it already resolved through the
        // locator or are deleting the only legacy placement they know of.
        if (_idToNamespace.TryRemove(entryId, out _))
            Interlocked.Decrement(ref _totalCountApprox);
    }

    /// <summary>
    /// Every namespace of <paramref name="tenantId"/> that currently holds
    /// <paramref name="entryId"/>. Constant-time in the number of namespaces the tenant owns —
    /// this is the structure that replaces walking them all.
    ///
    /// Exact only over partitions that have been materialized in this process, so a caller that
    /// needs completeness must have loaded the persisted namespaces first (see
    /// <see cref="LoadAll"/>). Returning a stale-but-superset answer would be tolerable — every
    /// caller re-reads the partition to confirm — but a stale MISS would not, which is why every
    /// path that can move an id between partitions maintains this index.
    ///
    /// The lookup itself takes no lock and still cannot miss a placement that is live for the whole
    /// call: a bucket is unpublished only while empty and only under its stripe, so the instance
    /// read here cannot have been retired while it still held the placement, and no bucket
    /// published afterwards can hold one that already existed when this read began.
    /// </summary>
    public IReadOnlyList<string> GetCandidateNamespaces(string entryId, string tenantId)
    {
        if (!_idCandidates.TryGetValue((tenantId, entryId), out var bucket))
            return Array.Empty<string>();

        // Materialize rather than hand back the live key collection: the caller iterates it while
        // probing partitions, and a concurrent write to this bucket would otherwise mutate the
        // sequence mid-walk.
        return bucket.Keys.ToList();
    }

    /// <summary>
    /// How many times an id in <paramref name="tenantId"/> has crossed the ambiguity boundary —
    /// gained a second namespace, or dropped back to one. Zero before the first crossing.
    ///
    /// The value is meaningless in itself and is only ever COMPARED: a consumer records it beside
    /// whatever it derived from attributable topology, and a later difference means at least one id
    /// changed between attributable and unattributable, so the derivation must be rebuilt rather
    /// than served.
    ///
    /// Deliberately does not <see cref="LoadAll"/>. Every crossing this process can observe is
    /// produced by <see cref="TrackCandidate"/> / <see cref="UntrackCandidate"/>, and a lazy load
    /// goes through them too (<see cref="LoadEntries"/> tracks every row it materializes), so the
    /// counter is already current for everything resident. Loading belongs on the path that reads
    /// the topology — which is where the completeness actually matters — not on the cheap read
    /// that only asks whether anything moved.
    /// </summary>
    public long AttributionRevisionFor(string tenantId)
        => _attributionRevisions.TryGetValue(tenantId, out var revision) ? revision : 0;

    /// <summary>
    /// Record one ambiguity-boundary crossing for a tenant. Called only from inside a candidate
    /// stripe and only AFTER the bucket mutation that caused the crossing is published: a consumer
    /// that observes the new counter must already be able to observe the placement it describes, or
    /// it would rebuild over the pre-crossing state and stamp it with the post-crossing revision —
    /// a stale derivation that never expires.
    /// </summary>
    private void BumpAttribution(string tenantId)
        => _attributionRevisions.AddOrUpdate(tenantId, 1L, static (_, current) => current + 1);

    /// <summary>
    /// The attribution fence for one tenant — see the field remarks for what each side means and
    /// which side is outermost.
    ///
    /// <paramref name="tenantId"/> must already be normalized. Every caller reaches this through
    /// <see cref="CognitiveIndex"/>, which normalizes; keying on a raw spelling would hand out a
    /// second lock for the same tenant, and two writers fencing against two different locks are
    /// not fenced at all.
    ///
    /// DISPOSAL-GUARDED, exactly the way <c>CognitiveIndex.NsLock</c> is, and for a sharper reason
    /// than leak avoidance. An unguarded <c>GetOrAdd</c> MINTS A FRESH FENCE after teardown has
    /// run: a crossing arriving late would then take the exclusive side of a lock no topology
    /// mutator holds the shared side of — exclusion against nobody — and republish it into a
    /// dictionary teardown had already walked, where nothing would ever dispose it. Throwing is the
    /// answer every other entry point on a disposed index gives, and it is reachable only after
    /// teardown, because every path into here first passes a partition lock that throws sooner.
    ///
    /// A lost <c>GetOrAdd</c> race discards an unused <see cref="ReaderWriterLockSlim"/>. That
    /// instance was never entered, so it holds no waiter events and nothing to release — the type
    /// allocates those lazily on contention — and it is unreachable the moment the winner is
    /// published.
    /// </summary>
    /// <exception cref="ObjectDisposedException">
    /// Teardown has run, or begins to run during this call — including the window between the
    /// pre-check and publication, where a just-orphaned instance is reclaimed inline.
    /// </exception>
    internal ReaderWriterLockSlim AttributionFenceFor(string tenantId)
    {
        if (Volatile.Read(ref _fencesDisposedFlag) != 0)
            throw new ObjectDisposedException(nameof(NamespaceStore));

        // Hot path — the fence is already published. No allocation on any crossing after the first.
        if (_attributionFences.TryGetValue(tenantId, out var existing))
            return existing;

        var created = new ReaderWriterLockSlim();
        var published = _attributionFences.GetOrAdd(tenantId, created);

        if (Volatile.Read(ref _fencesDisposedFlag) != 0)
        {
            // Teardown ran between the pre-check and the GetOrAdd. If WE published, yank it back
            // out and dispose it, so a fence created after teardown cannot outlive it.
            if (ReferenceEquals(published, created) &&
                ((ICollection<KeyValuePair<string, ReaderWriterLockSlim>>)_attributionFences)
                    .Remove(new KeyValuePair<string, ReaderWriterLockSlim>(tenantId, created)))
            {
                created.Dispose();
            }
            throw new ObjectDisposedException(nameof(NamespaceStore));
        }

        if (!ReferenceEquals(published, created))
            created.Dispose();

        return published;
    }

    /// <summary>
    /// The published fence for a tenant, or null when there is none. For DIAGNOSTIC reads only: a
    /// probe must never be the thing that mints a fence, or reading a counter would publish a lock
    /// into a dictionary teardown may already have walked past.
    /// </summary>
    internal ReaderWriterLockSlim? TryGetAttributionFence(string tenantId)
        => _attributionFences.TryGetValue(tenantId, out var fence) ? fence : null;

    /// <summary>
    /// Best-effort teardown of every tenant's fence, mirroring what <see cref="CognitiveIndex"/>
    /// already does for its per-namespace locks: a fence still held by a maintenance pass that
    /// outlived the host's shutdown timeout throws on Dispose, and letting that escape would
    /// abandon the persistence flush. Returns how many were skipped for that reason.
    ///
    /// A SKIPPED FENCE STAYS PUBLISHED, and that is the whole difference between best-effort and
    /// destructive. It was skipped precisely because a thread still holds it; unpublishing it would
    /// leave that holder's release resolving a key that is no longer there — which, with a
    /// <c>GetOrAdd</c> accessor, silently resurrected a brand-new lock and called ExitReadLock on
    /// it, throwing out of the holder's finally block while the real fence kept its reader and any
    /// waiting crossing slept forever. Only fences this actually disposed are unpublished, removed
    /// by (key, value) so a fence minted between the walk and the removal is not evicted in place
    /// of the one that was disposed.
    ///
    /// The flag is raised FIRST, so nothing can publish a new fence behind the walk.
    /// </summary>
    internal int DisposeAttributionFences()
    {
        Volatile.Write(ref _fencesDisposedFlag, 1);

        int skipped = 0;
        foreach (var kv in _attributionFences)
        {
            try
            {
                kv.Value.Dispose();
                ((ICollection<KeyValuePair<string, ReaderWriterLockSlim>>)_attributionFences).Remove(kv);
            }
            catch (SynchronizationLockException) { skipped++; }
        }
        return skipped;
    }

    /// <summary>
    /// How many tenant fences are still published. The seam a disposal test needs to state that a
    /// CONTENDED fence was LEFT IN PLACE rather than cleared out from under the thread holding it.
    /// </summary>
    internal int AttributionFenceCount => _attributionFences.Count;

    /// <summary>
    /// The stripe that serializes publication and retirement for one candidate key. Hashing the
    /// whole key rather than the id alone keeps two tenants that legitimately share an id from
    /// colliding on the same stripe more often than chance.
    /// </summary>
    private object CandidateStripe((string Tenant, string Id) key)
        => _candidateStripes[key.GetHashCode() & (CandidateStripeCount - 1)];

    /// <summary>
    /// Add one placement to the candidate index, under this key's stripe so it cannot interleave
    /// with a retirement of the same key. Placements for other keys, and every lookup, run
    /// unblocked; a concurrent <see cref="UntrackCandidate"/> for the same id in a DIFFERENT
    /// namespace genuinely races, because those two run under two different per-partition write
    /// locks and nothing above this serializes them.
    /// </summary>
    private void TrackCandidate(string entryId, string ns, string tenantId)
    {
        var key = (tenantId, entryId);

        // TWO PHASES, AND THE SPLIT IS THE POINT. Almost every entry write crosses no ambiguity
        // boundary — a first placement (0 -> 1) and a re-placement into a namespace the id already
        // occupies change nothing a topology consumer can observe — and those must NOT take the
        // exclusive side of the fence, or every upsert in a tenant would serialize against every
        // graph and cluster write in it. Phase one performs exactly those placements and reports
        // false, having changed nothing, when the placement would cross.
        if (TryTrackWithoutCrossing(key, ns))
            return;

        // THE CROSSING. This placement makes a (tenant, id) ambiguous, which unmakes every edge and
        // every cluster membership that names it. A topology mutator holding the SHARED side of the
        // fence has already validated its sweep against the current attribution revision and is
        // partway through writing bare ids it judged attributable, so the crossing waits for it —
        // that mutual exclusion, not the revision compare, is what closes the window.
        //
        // The fence is taken OUTSIDE the stripe and released after it. That direction is
        // one-directional everywhere: fence, then stripe, never the reverse — see the field
        // remarks for the whole-process argument, including which side of the fence is outermost.
        var fence = AttributionFenceFor(tenantId);
        fence.EnterWriteLock();
        try
        {
            lock (CandidateStripe(key))
            {
                // Fetching the bucket and writing into it is one atomic step against a retirement,
                // so the instance written here is still the published one when the stripe is
                // released. No post-write re-check can substitute for that: a retirement evaluates
                // emptiness BEFORE the write and unpublishes AFTER it, so the re-check passes and
                // the placement is lost anyway. That ordering is what the previous lock-free
                // version could not close.
                var bucket = _idCandidates.GetOrAdd(key, static _ => new ConcurrentDictionary<string, byte>());

                // Re-decided here rather than trusted from phase one, because phase one released
                // the stripe: the crossing it predicted may have evaporated (the twin deleted, or
                // this same placement made by another thread). A crossing needs the bucket to have
                // held exactly one namespace before this placement, so an empty one cannot produce
                // it — and under the stripe an empty published bucket is precisely the one GetOrAdd
                // just created, because retirement never leaves a live empty instance behind.
                bool couldCross = !bucket.IsEmpty;
                if (bucket.TryAdd(ns, 0) && couldCross && bucket.Count == 2)
                    BumpAttribution(tenantId);
            }
        }
        finally { fence.ExitWriteLock(); }
    }

    /// <summary>
    /// Phase one of <see cref="TrackCandidate"/>: publish the placement when it provably crosses no
    /// ambiguity boundary, and otherwise leave the index untouched and say so.
    ///
    /// Everything it does is done under the stripe, so the decision and the write it authorizes
    /// cannot be separated by another placement or retirement of the same key. Returning false is a
    /// promise that NOTHING was written — the caller re-does the whole step under the fence, and a
    /// half-applied placement here would be published without the exclusion it needs.
    ///
    /// <c>ContainsKey</c> is asked before <c>Count</c> deliberately: it is a lock-free probe and the
    /// re-placement of an existing entry — the hot upsert path — answers it true, while
    /// <c>Count</c> takes every lock of the inner dictionary. A first placement reaches neither,
    /// because the key misses.
    /// </summary>
    private bool TryTrackWithoutCrossing((string Tenant, string Id) key, string ns)
    {
        lock (CandidateStripe(key))
        {
            if (_idCandidates.TryGetValue(key, out var bucket))
            {
                // The crossing is 1 -> 2, and only that: the id occupies exactly one namespace and
                // this placement names a different one. 2 -> 3 leaves an already-ambiguous id
                // ambiguous and changes nothing any consumer can observe, so it stays on this path.
                if (!bucket.ContainsKey(ns) && bucket.Count == 1)
                    return false;

                bucket.TryAdd(ns, 0);
                return true;
            }

            // No bucket: this is the id's first placement in the tenant, 0 -> 1, which crosses
            // nothing — an id one entry answers to is attributable. GetOrAdd rather than an
            // indexer write so the shape matches the fenced path exactly.
            _idCandidates
                .GetOrAdd(key, static _ => new ConcurrentDictionary<string, byte>())
                .TryAdd(ns, 0);
            return true;
        }
    }

    /// <summary>
    /// Remove one placement from the candidate index, retiring the bucket once it is empty so a
    /// store that churns ids does not retain a dictionary per id ever seen. Retirement is the whole
    /// reason the stripe exists: emptiness has to still hold at the moment the instance is
    /// unpublished, and only mutual exclusion with <see cref="TrackCandidate"/> makes it hold.
    /// </summary>
    private void UntrackCandidate(string entryId, string ns, string tenantId)
    {
        var key = (tenantId, entryId);

        // Two phases, exactly as in TrackCandidate and for the same reason: the ordinary delete
        // (the id's only placement, 1 -> 0) crosses nothing and must not take the exclusive side.
        if (TryUntrackWithoutCrossing(key, ns))
            return;

        // THE CROSSING, 2 -> 1: an ambiguous id becomes attributable again. It matters in the same
        // way as the other direction — a topology mutator that judged this id UNSAFE and skipped it
        // must not have its skip re-decided underneath it — so it takes the exclusive side too.
        var fence = AttributionFenceFor(tenantId);
        fence.EnterWriteLock();
        try
        {
            lock (CandidateStripe(key))
            {
                if (!_idCandidates.TryGetValue(key, out var bucket))
                    return;

                bool removed = bucket.TryRemove(ns, out _);
                if (!bucket.IsEmpty)
                {
                    // Re-decided under the stripe rather than trusted from phase one, which
                    // released it. Only a placement that was actually there can shrink the bucket,
                    // and a shrink lands on the boundary only when exactly one namespace is left —
                    // the move back from ambiguous to attributable. Emptying the bucket entirely
                    // (1 -> 0) crosses nothing: an id no entry answers to was never ambiguous.
                    if (removed && bucket.Count == 1)
                        BumpAttribution(tenantId);
                    return;
                }

                // Plain TryRemove, not the value-matching overload used in RemoveNamespace. Under
                // the stripe no other thread can have published a different bucket for this key, so
                // there is nothing left for a value match to protect — and it never protected the
                // case that mattered, since the observed instance and the instance a racing
                // placement just wrote into are the same object.
                _idCandidates.TryRemove(key, out _);
            }
        }
        finally { fence.ExitWriteLock(); }
    }

    /// <summary>
    /// Phase one of <see cref="UntrackCandidate"/>, mirroring <see cref="TryTrackWithoutCrossing"/>:
    /// retire the placement when it provably crosses no ambiguity boundary, and otherwise leave the
    /// index untouched and say so.
    ///
    /// The crossing is 2 -> 1 and only that. Emptying the bucket entirely (1 -> 0) crosses nothing —
    /// an id no entry answers to was never ambiguous — and 3 -> 2 leaves an already-ambiguous id
    /// ambiguous. A key that is not there at all removes nothing, so it crosses nothing either.
    ///
    /// Returning true is a promise the retirement is COMPLETE, including the bucket's own removal
    /// once it is empty; returning false is a promise nothing was touched.
    /// </summary>
    private bool TryUntrackWithoutCrossing((string Tenant, string Id) key, string ns)
    {
        lock (CandidateStripe(key))
        {
            if (!_idCandidates.TryGetValue(key, out var bucket))
                return true;

            // ContainsKey before Count for the same reason as the placement side: a delete naming a
            // namespace the id does not occupy is a lock-free miss, while Count takes every lock of
            // the inner dictionary.
            if (bucket.ContainsKey(ns) && bucket.Count == 2)
                return false;

            if (bucket.TryRemove(ns, out _) && bucket.IsEmpty)
                _idCandidates.TryRemove(key, out _);
            return true;
        }
    }

    // ── HNSW Index Management (keyed by composed partition key) ──

    /// <summary>Get the HNSW index for a partition, or null if not built.</summary>
    public HnswIndex? GetHnswIndex(string partitionKey)
        => _hnswIndices.TryGetValue(partitionKey, out var idx) ? idx : null;

    /// <summary>Add an entry to the per-partition HNSW index, building the index if the partition is large enough.</summary>
    public void AddToHnsw(NsKey key, string id, float[] vector)
    {
        string pk = PartitionKey(key);
        var nsEntries = GetNamespace(key);
        int count = nsEntries?.Count ?? 0;

        if (!_hnswIndices.TryGetValue(pk, out var idx))
        {
            if (count < HnswThreshold)
                return; // Not large enough yet

            // Build HNSW from all existing entries in the partition
            idx = new HnswIndex();
            if (nsEntries is not null)
            {
                foreach (var (entry, _, _) in nsEntries.Values)
                    idx.Add(entry.Id, entry.Vector);
            }
            _hnswIndices[pk] = idx;
            return; // The new entry is already in nsEntries if called after dict update
        }

        idx.Add(id, vector);

        if (idx.NeedsRebuild)
            _hnswIndices[pk] = idx.Rebuild();
    }

    /// <summary>Remove an entry from the per-partition HNSW index.</summary>
    public void RemoveFromHnsw(string partitionKey, string id)
    {
        if (_hnswIndices.TryGetValue(partitionKey, out var idx))
        {
            idx.Remove(id);
            if (idx.NeedsRebuild)
                _hnswIndices[partitionKey] = idx.Rebuild();
        }
    }

    /// <summary>
    /// Invalidate the in-memory HNSW index for a partition and delete its persisted snapshot.
    /// Call this after bulk re-embedding so the stale index is rebuilt lazily on the next search.
    /// </summary>
    public void InvalidateHnswIndex(string partitionKey)
    {
        ValidatePartitionKey(partitionKey, nameof(partitionKey));
        _hnswIndices.TryRemove(partitionKey, out _);
        _persistence.DeleteHnswSnapshot(partitionKey);
    }

    /// <summary>
    /// Validate an ALREADY-COMPOSED partition key at an entry point that receives one rather than
    /// its two components. A well-formed key is either a bare namespace (legacy tenant) or exactly
    /// <c>tenant + separator + ns</c>, so at most one separator is legal and every other control
    /// character is a component that skipped its boundary guard. This cannot distinguish a legacy
    /// namespace that smuggled in one separator from a genuine tenant key — that ambiguity is
    /// inherent to the flattened representation and is closed where components are validated on the
    /// way in, not here.
    /// </summary>
    private static void ValidatePartitionKey(string partitionKey, string paramName)
    {
        int sep = partitionKey.IndexOf(Tenancy.PartitionSeparator);
        if (sep < 0)
        {
            Tenancy.ValidatePartitionComponent(partitionKey, paramName);
            return;
        }
        Tenancy.ValidatePartitionComponent(partitionKey[..sep], paramName);
        Tenancy.ValidatePartitionComponent(partitionKey[(sep + 1)..], paramName);
    }

    /// <summary>Try to restore a partition's HNSW index from a persisted snapshot. Falls back to lazy rebuild if snapshot is stale.</summary>
    private void TryRestoreHnsw(string partitionKey, NsKey key)
    {
        var snapshot = _persistence.LoadHnswSnapshot(partitionKey);
        if (snapshot == null) return;

        var nsEntries = GetNamespace(key);
        if (nsEntries == null) return;

        var restored = Retrieval.HnswIndex.RestoreFromSnapshot(snapshot, id =>
        {
            if (nsEntries.TryGetValue(id, out var tuple))
                return tuple.Entry.Vector;
            return null;
        });

        if (restored != null)
            _hnswIndices[partitionKey] = restored;
        // else: snapshot was stale, HNSW will be lazily rebuilt on first AddToHnsw call
    }

    /// <summary>Persist current HNSW index snapshots for every tenant partition of a namespace.</summary>
    private void ScheduleHnswSave(string ns)
    {
        foreach (var key in _namespaces.Keys)
        {
            if (key.Ns != ns) continue;
            string pk = PartitionKey(key);
            if (_hnswIndices.TryGetValue(pk, out var idx))
            {
                var snapshot = idx.CreateSnapshot();
                _persistence.SaveHnswSnapshotSync(pk, snapshot);
            }
        }
    }

    private void LoadEntries(string ns, List<CognitiveEntry> entries)
    {
        int added = 0;
        foreach (var entry in entries)
        {
            var key = new NsKey(entry.TenantId, entry.Ns);
            var nsDict = _namespaces.GetOrAdd(key, _ => new ConcurrentDictionary<string, (CognitiveEntry, float, QuantizedVector?)>());
            float norm = VectorMath.Norm(entry.Vector);
            var quantized = entry.LifecycleState is "ltm" or "archived"
                ? VectorQuantizer.Quantize(entry.Vector)
                : null;
            nsDict[entry.Id] = (entry, norm, quantized);

            // Track the placement the row actually landed in (entry.Ns, which keyed nsDict above),
            // not the namespace being loaded: a row whose Ns disagrees with its file lives in the
            // partition its own Ns names, and that is the partition a candidate lookup must send
            // the caller to. This is the path that makes the index complete after LoadAll — without
            // it, an id in a namespace untouched this process would resolve as a miss.
            TrackCandidate(entry.Id, entry.Ns, entry.TenantId);

            // Only legacy-tenant entries populate the global locator + count.
            if (entry.TenantId.Length == 0)
            {
                if (_idToNamespace.TryAdd(entry.Id, ns))
                    added++;
                else
                    _idToNamespace[entry.Id] = ns;
            }
        }
        if (added > 0)
            Interlocked.Add(ref _totalCountApprox, added);
    }
}
