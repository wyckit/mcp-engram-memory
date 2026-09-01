using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;

// The MCP host orchestrates the package-internal coherent merge primitive without widening that
// concurrency protocol into Core's public NuGet surface.
[assembly: InternalsVisibleTo("McpEngramMemory")]

namespace McpEngramMemory.Core.Services;

/// <summary>
/// An opaque token for a lifecycle revision that
/// <see cref="CognitiveIndex.ReserveLifecycleRevision"/> reserved but whose transition has not
/// happened yet. INTERNAL end to end — the type, the minting method and the installing CAS —
/// so no code outside this assembly can mint, forge, default-construct or replay one; and the
/// CAS additionally rejects a defaulted token (value 0, which no reservation ever carries), so
/// even in-assembly a <c>default(LifecycleReservation)</c> installs nothing. Uniqueness of
/// installed witnesses is enforced by the minting counter, not by every caller independently
/// promising to pass fresh numbers.
/// </summary>
internal readonly struct LifecycleReservation
{
    internal long Value { get; }
    internal LifecycleReservation(long value) => Value = value;
}

/// <summary>
/// A held partition read lock proving one (tenant, ns) partition's occupancy has not moved
/// since a staged baseline — see <see cref="CognitiveIndex.TryPinOccupancy"/>. Dispose exactly
/// once, after the mutation the pin covers.
/// </summary>
internal sealed class OccupancyPin : IDisposable
{
    /// <summary>A pin over nothing, for operations with no watched partition. Never disposed of a lock.</summary>
    internal static readonly OccupancyPin None = new(null);

    private ReaderWriterLockSlim? _held;
    internal OccupancyPin(ReaderWriterLockSlim? held) => _held = held;

    public void Dispose()
    {
        var held = _held;
        _held = null;
        held?.ExitReadLock();
    }
}

/// <summary>
/// One entry as observed for a merge, plus the fields that can change in place without moving
/// <see cref="CognitiveEntry.Revision"/>. The entry is a detached copy; the witness is compared
/// against the resident object under the partition write lock before either merge side changes.
/// </summary>
internal sealed record MergeEntrySnapshot(
    CognitiveEntry Entry,
    long Revision,
    long LifecycleRevision,
    string LifecycleState,
    DateTimeOffset LastAccessedAt,
    int AccessCount,
    float ActivationEnergy);

/// <summary>
/// Thread-safe namespace-partitioned vector index with lifecycle awareness.
///
/// Locking: per-namespace ReaderWriterLockSlim in <c>_nsLocks</c>. Each single-namespace
/// operation (Upsert, Search, Get(id, ns), Delete, RecordAccess, etc.) holds only the
/// target namespace's lock, so writers to different namespaces run in parallel. Readers
/// of a namespace parallelize with each other; a writer to the same namespace is exclusive
/// against other readers and writers OF THAT NAMESPACE only.
///
/// Cross-namespace reads (Count, GetNamespaces, GetAll, GetStateCounts(null)) are lock-free
/// and rely on ConcurrentDictionary semantics — they see a consistent snapshot per entry
/// but not a linearizable snapshot across the whole store. This is intentional and matches
/// the semantics of diagnostic counts.
///
/// Operations that resolve id → namespace (Get(id), Delete(id), RecordAccess(id),
/// SetLifecycleState, SetActivationEnergyAndState) resolve lock-free via the
/// NamespaceStore._idToNamespace ConcurrentDictionary, then acquire the resolved
/// namespace's lock for the actual work. The tenant-scoped bare-id operations
/// (GetNamespacesContaining, GetForTenant, DeleteForTenant, CountNamespacesContaining) resolve
/// through the tenant-qualified candidate index instead — same lock-free-then-acquire shape, but
/// set-valued, because within one tenant a bare id can name more than one entry and those callers
/// have to be able to tell that.
///
/// Events (<see cref="EntryUpserted"/>, <see cref="EntryDeleted"/>) fire AFTER the
/// per-namespace lock is released, so handlers can call back into the index safely.
/// </summary>
public sealed class CognitiveIndex : IDisposable
{
    private static readonly HashSet<string> AllStates = new() { "stm", "ltm", "archived" };

    private readonly NamespaceStore _store;
    private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> _nsLocks = new();
    // 0 = live, 1 = disposed. Int rather than bool so Interlocked.Exchange can provide
    // an atomic "once and only once" transition across concurrent Dispose callers.
    private int _disposedFlag;
    private readonly BM25Index _bm25 = new();
    private readonly TokenReranker _reranker = new();
    private readonly VectorSearchEngine _vectorSearch = new();
    private readonly HybridSearchEngine _hybridSearch;
    private readonly DuplicateDetector _duplicateDetector = new();
    private readonly SynonymExpander _synonymExpander = new();
    private readonly DocumentEnricher _documentEnricher = new();
    private readonly QueryExpander _queryExpander = new();
    private readonly MemoryLimitsConfig _limits;

    /// <summary>
    /// Fires after an entry is successfully upserted (after the write lock is released).
    /// Parallel readers/agents can subscribe to observe new memories in real time without polling.
    /// Raised once per entry for both Upsert and UpsertBatch.
    /// Handlers run synchronously on the writer's thread — keep them cheap or offload.
    /// </summary>
    public event EventHandler<CognitiveEntry>? EntryUpserted;

    /// <summary>
    /// Fires after an entry is successfully deleted (after the write lock is released).
    /// Carries (namespace, id) so subscribers don't need to hold a stale entry reference.
    /// </summary>
    public event EventHandler<(string Namespace, string Id)>? EntryDeleted;

    /// <summary>
    /// Fires after a tenant + namespace partition is removed. Internal maintenance consumers use
    /// the normalized key to retract per-partition derived state in O(1), including when deletion
    /// leaves no live namespace whose future scan could drive cleanup.
    /// </summary>
    internal event Action<NsKey>? NamespaceRemoved;

    public CognitiveIndex(IStorageProvider persistence, MemoryLimitsConfig? limits = null, MetricsCollector? metrics = null)
    {
        _store = new NamespaceStore(persistence, _bm25);
        _limits = limits ?? new MemoryLimitsConfig();
        _hybridSearch = new HybridSearchEngine(metrics);
    }

    /// <summary>
    /// Get or lazily create the ReaderWriterLockSlim for a namespace.
    /// Throws <see cref="ObjectDisposedException"/> if Dispose has run (or begins to run
    /// during this call), including the TOCTOU window between the pre-check and
    /// GetOrAdd. A just-created-then-orphaned lock is disposed inline so nothing leaks.
    ///
    /// Hot path (ns already in the dict): one TryGetValue, zero allocations.
    /// Cold path (first use of a ns): one RWLS allocation, race-safe publication.
    /// </summary>
    private ReaderWriterLockSlim NsLock(string ns)
    {
        if (Volatile.Read(ref _disposedFlag) != 0)
            throw new ObjectDisposedException(nameof(CognitiveIndex));

        // Fast path — the lock is already published. Avoids a RWLS allocation
        // on every single-ns operation after the first.
        if (_nsLocks.TryGetValue(ns, out var existing))
            return existing;

        // Cold path — create-then-publish so we can reclaim our own lock if Dispose
        // races with us between the pre-check and publication.
        var created = new ReaderWriterLockSlim();
        var published = _nsLocks.GetOrAdd(ns, created);

        if (Volatile.Read(ref _disposedFlag) != 0)
        {
            // Dispose ran between our pre-check and GetOrAdd. If WE are the one who
            // just published (Dispose already iterated before we added), yank it out
            // and dispose it so it doesn't leak.
            if (ReferenceEquals(published, created) &&
                ((ICollection<KeyValuePair<string, ReaderWriterLockSlim>>)_nsLocks)
                    .Remove(new KeyValuePair<string, ReaderWriterLockSlim>(ns, created)))
            {
                created.Dispose();
            }
            throw new ObjectDisposedException(nameof(CognitiveIndex));
        }

        // Another thread won the race and published first — discard our unused instance.
        if (!ReferenceEquals(published, created))
            created.Dispose();

        return published;
    }

    // ── Counts + Metadata ──

    /// <summary>
    /// Total entry count across all loaded namespaces. Lock-free: TotalCount is an
    /// Interlocked atomic on NamespaceStore, so this returns an eventually-consistent
    /// snapshot under concurrent writers — exact for diagnostic / memory-limit checks.
    /// </summary>
    public int Count
    {
        get
        {
            _store.LoadAll();
            return _store.TotalCount;
        }
    }

    /// <summary>Count entries in a specific namespace. Per-namespace read lock.</summary>
    public int CountInNamespace(string ns)
        => CountInNamespace(ns, string.Empty);

    /// <summary>Count entries in a specific tenant + namespace partition. Per-partition read lock.</summary>
    public int CountInNamespace(string ns, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterReadLock();
        try
        {
            _store.EnsureLoaded(ns);
            return _store.GetNamespace(key)?.Count ?? 0;
        }
        finally { nsLock.ExitReadLock(); }
    }

    /// <summary>
    /// Get all known namespace names. Lock-free: reads from a ConcurrentDictionary snapshot
    /// and the persisted-namespace list from storage.
    /// </summary>
    public IReadOnlyList<string> GetNamespaces()
        => _store.GetNamespaceNames();

    /// <summary>Get namespaces containing entries for exactly one tenant.</summary>
    public IReadOnlyList<string> GetNamespaces(string tenantId)
    {
        _store.LoadAll();
        return _store.GetNamespaceNames(Tenancy.Normalize(tenantId));
    }

    /// <summary>
    /// Materialize every persisted partition, listing nothing.
    ///
    /// Internal and deliberately narrow: it exists for the one caller that captures
    /// <see cref="AttributionRevisionFor"/> as a freshness baseline and cannot afford its own lazy
    /// load to move that counter afterwards (<see cref="Graph.TopologyGuard.Sweep"/>). Loading
    /// TRACKS every row it materializes, so a cold store's first load bumps the counter once per
    /// id that is already ambiguous on disk — and a consumer comparing the counter later would read
    /// that as somebody else's concurrent write and refuse a write for no reason. Warming first is
    /// what separates "the store just woke up" from "attribution moved".
    ///
    /// Idempotent and nearly free after the first call: <c>NamespaceStore.LoadAll</c> caches its
    /// completion generation and thereafter returns on two atomic reads.
    /// </summary>
    internal void EnsureAllNamespacesLoaded() => _store.LoadAll();

    /// <summary>
    /// The namespaces of <paramref name="tenantId"/> that hold <paramref name="id"/> — usually one,
    /// occasionally two, never the whole namespace list. Every bare-id path (resolution,
    /// ambiguity counting, tenant-scoped get/delete) probes this instead of walking the tenant.
    ///
    /// The store-wide load is kept, deliberately. The full walk this replaces was complete only
    /// because <see cref="GetNamespaces(string)"/> loads every persisted namespace first, and the
    /// candidate index is exact only over partitions materialized in this process.
    /// Dropping the load would turn a namespace nothing has touched into a silent miss — or worse,
    /// leave an ambiguous id looking unique because only one twin happened to be resident, which
    /// is precisely the arbitrary-twin outcome the ambiguity rule exists to refuse. Keeping it is
    /// nearly free after the first call: <c>NamespaceStore.LoadAll</c> records that its
    /// sweep completed and thereafter returns on two atomic reads, so a resolution enumerates the
    /// storage provider once per process rather than once per lookup. Both halves of the old cost
    /// are gone — the per-namespace probes and the namespace-count scaling of the load.
    ///
    /// Returns namespaces, never entries, so it discloses nothing on its own — the caller still
    /// applies its own access predicate before looking inside any of them.
    /// </summary>
    /// <param name="id">Bare entry id.</param>
    /// <param name="tenantId">Required. "" is the legacy partition, not a wildcard.</param>
    public IReadOnlyList<string> GetNamespacesContaining(string id, string tenantId)
    {
        _store.LoadAll();
        return _store.GetCandidateNamespaces(id, Tenancy.Normalize(tenantId));
    }

    /// <summary>
    /// How many times an id in <paramref name="tenantId"/> has crossed the ambiguity boundary —
    /// gained a second namespace of the tenant, or dropped back to one.
    ///
    /// The freshness signal a cache of ATTRIBUTABLE topology needs and can get nowhere else.
    /// Inserting a same-id twin is an ordinary entry write: it creates no edge and no cluster
    /// membership, so
    /// <see cref="McpEngramMemory.Core.Services.Graph.KnowledgeGraph.RevisionFor(string)"/> does not
    /// move, while every edge naming that id has just stopped being usable. A consumer that watched
    /// only the graph revision would keep serving a basis built from edges the attributable view no
    /// longer returns — stale in the one direction that matters, since the stale copy is the one
    /// that still contains another principal's topology.
    ///
    /// Compared, never interpreted: any difference from a recorded value means attribution moved
    /// somewhere in the tenant and the derivation must be rebuilt. It discloses nothing on its own —
    /// a tenant-wide change count naming no id, namespace or entry.
    /// </summary>
    /// <param name="tenantId">Required. "" is the legacy partition, not a wildcard.</param>
    public long AttributionRevisionFor(string tenantId)
        => _store.AttributionRevisionFor(Tenancy.Normalize(tenantId));

    /// <summary>
    /// Take the SHARED side of <paramref name="tenantId"/>'s attribution fence, and hold it across
    /// a topology mutation's final attribution validation AND the mutation itself.
    ///
    /// THIS IS THE GUARANTEE THAT <see cref="AttributionRevisionFor"/> IS NOT. The counter says
    /// whether attribution moved before it was read; it says nothing about the interval between
    /// that read and the write it was read for, and nothing in this index coordinates with
    /// <see cref="Graph.KnowledgeGraph"/>'s or <see cref="Intelligence.ClusterManager"/>'s locks —
    /// so a twin could land after the comparison and before the mutation, and the mutation would
    /// publish a bare id two entries answer to. Under the shared side no id in the tenant can cross
    /// the ambiguity boundary at all, because the crossing takes the EXCLUSIVE side
    /// (<c>NamespaceStore.TrackCandidate</c> / <c>UntrackCandidate</c>, the two places a crossing is
    /// detected). So a revision compare made under this hold stays true for as long as it is held,
    /// which turns a narrowed race into no race.
    ///
    /// CONTRACT FOR THE HOLDER, and it is what keeps the fence deadlock-free:
    ///  - Take it OUTERMOST — before the graph or cluster write lock, released after it.
    ///  - Call NOTHING on this index while holding it except <see cref="AttributionRevisionFor"/>,
    ///    which is a lock-free dictionary read. Every other entry point can take a per-partition
    ///    lock or trigger a lazy store load, and a load TRACKS what it materializes, which is a
    ///    request for the exclusive side of this same fence. Materialize persisted state before
    ///    taking the fence, not under it.
    ///  - Never nest. The underlying primitive is non-recursive, so re-entry throws rather than
    ///    deadlocking; no mutator calls another mutator, which is what makes that a safety net
    ///    rather than a live hazard.
    ///  - A mutator spanning several tenants takes their fences in ORDINAL TENANT ORDER, so two
    ///    such mutators can never hold half of each other's set.
    ///
    /// RETURNS THE INSTANCE IT ENTERED, and the caller MUST release through that reference rather
    /// than by naming the tenant again. A release that re-resolves the fence by key is a release
    /// against whatever the dictionary holds at that later moment, and the two can differ: teardown
    /// unpublishes fences, and an accessor that mints on miss would then hand the holder a
    /// brand-new lock to call ExitReadLock on. That throws
    /// <see cref="SynchronizationLockException"/> out of the holder's finally block — replacing the
    /// mutator's return value with an exception — while the fence it actually holds keeps its
    /// reader forever and every crossing waiting on that fence's exclusive side sleeps forever,
    /// each one still holding its own partition write lock. Holding the reference makes the release
    /// exact by construction instead of by re-derivation.
    ///
    /// Pairs with <see cref="ExitAttributionFence"/> in a finally. Internal because it is a
    /// concurrency contract between this index and the two topology writers in this assembly, not
    /// something a tool or a host should be reaching for.
    /// </summary>
    /// <param name="tenantId">Required. "" is the legacy partition, not a wildcard.</param>
    /// <returns>The fence whose shared side is now held — release it with
    /// <see cref="ExitAttributionFence"/>.</returns>
    /// <exception cref="ObjectDisposedException">This index has been disposed.</exception>
    internal ReaderWriterLockSlim EnterAttributionFence(string tenantId)
    {
        var fence = _store.AttributionFenceFor(Tenancy.Normalize(tenantId));
        fence.EnterReadLock();
        return fence;
    }

    /// <summary>
    /// Release the shared side taken by <see cref="EnterAttributionFence"/>, through the instance
    /// that call returned.
    ///
    /// Takes the fence rather than the tenant so there is no lookup to get wrong and no way to
    /// release a lock other than the one held — see <see cref="EnterAttributionFence"/> for what a
    /// re-resolving release did. Static for the same reason: releasing needs nothing from this
    /// index, and a signature that asked for something from it would invite the lookup back.
    /// </summary>
    internal static void ExitAttributionFence(ReaderWriterLockSlim fence)
    {
        ArgumentNullException.ThrowIfNull(fence);
        fence.ExitReadLock();
    }

    /// <summary>
    /// TEST SEAM: how many threads are currently blocked waiting for the EXCLUSIVE side of a
    /// tenant's attribution fence. Zero when the tenant has no fence at all.
    ///
    /// It exists so a concurrency test can rendezvous on a CONDITION rather than on a delay. The
    /// property being tested is "an ambiguity-changing entry write cannot land between a topology
    /// mutator's validation and its mutation", and the only deterministic way to observe that from
    /// outside is to suspend the mutator at that exact point and watch the interfering write pile
    /// up against the fence. A sleep would prove nothing; this is a state the test can wait on.
    ///
    /// Resolves without minting: a diagnostic read must not be the thing that publishes a fence.
    /// </summary>
    internal int AttributionFenceWaitingWriters(string tenantId)
        => _store.TryGetAttributionFence(Tenancy.Normalize(tenantId)) is { } fence
            ? fence.WaitingWriteCount
            : 0;

    /// <summary>Test diagnostic for a writer queued behind one partition lock.</summary>
    internal int PartitionWaitingWriters(string ns, string tenantId)
        => NsLock(NamespaceStore.PartitionKey(Tenancy.Normalize(tenantId), ns)).WaitingWriteCount;

    /// <summary>
    /// TEST SEAM: how many tenant fences are still published. Pairs with
    /// <see cref="DisposalContendedFenceCount"/> to state that a fence held across
    /// <see cref="Dispose"/> was LEFT IN PLACE rather than unpublished under its holder.
    /// </summary>
    internal int AttributionFenceCount => _store.AttributionFenceCount;

    /// <summary>
    /// All distinct tenant ids in the store (includes the legacy tenant "" when legacy data exists).
    /// Loads every persisted namespace first so background maintenance can cover every tenant.
    /// </summary>
    public IReadOnlyList<string> GetAllTenants()
    {
        _store.LoadAll();
        return _store.GetAllTenants();
    }

    // ── CRUD ──

    /// <summary>Add or replace a cognitive entry. LTM/archived entries are auto-quantized for fast search.</summary>
    // Seeded RANDOMLY, not from the clock: witnesses are compared for EQUALITY only, never
    // ordered, so a seed needs uniqueness and nothing else — and a tick seed does not deliver
    // it. Ticks made post-restart collisions unlikely but let two LIVE processes over one
    // store mint overlapping ranges (start δ ticks apart, overlap after δ increments), and an
    // overlapping value forges CAS ownership: a claim one process reserved but never installed
    // can come to match a witness the other process DID install. A random 62-bit seed makes a
    // cross-process range collision negligible instead of merely unlikely.
    // Incremented per upsert so same-process occupations cannot collide with each other. See
    // CognitiveEntry.Revision.
    private long _entryRevisionCounter = RandomRevisionSeed();

    // Same seeding rationale; moved only on actual lifecycle TRANSITIONS — see
    // CognitiveEntry.LifecycleRevision.
    private long _lifecycleRevisionCounter = RandomRevisionSeed();

    // Positive and clear of the top bits so billions of increments cannot overflow, and never
    // zero-adjacent: 0 is the "no transition" sentinel, and the first minted value is seed+1.
    private static long RandomRevisionSeed()
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return (long)(BitConverter.ToUInt64(bytes) >> 2) + 1;
    }

    // Called inside the partition write lock, after LifecycleState was assigned. A set to the
    // state already held is not a transition and moves nothing.
    private void StampLifecycleTransition(CognitiveEntry entry, string previousState)
    {
        if (!string.Equals(previousState, entry.LifecycleState, StringComparison.Ordinal))
            entry.LifecycleRevision = Interlocked.Increment(ref _lifecycleRevisionCounter);
    }

    /// <summary>
    /// Reserve a lifecycle revision for a transition that has not happened yet. This is what
    /// makes a claims-ahead receipt safe to persist BEFORE its side effect: the claim names a
    /// revision only <see cref="TryTransitionLifecycle"/> can install, so a claim whose
    /// transition never ran (crash, CAS loss) matches no entry and is inert — over-claiming is
    /// structurally impossible rather than carefully avoided. The reservation comes back as an
    /// opaque token rather than a raw number so an arbitrary value can never be installed as a
    /// witness: the only way to mint one is this counter, which never repeats.
    /// </summary>
    internal LifecycleReservation ReserveLifecycleRevision()
        => new(Interlocked.Increment(ref _lifecycleRevisionCounter));

    /// <summary>
    /// Compare-and-swap lifecycle transition, atomic under the partition write lock: the entry
    /// transitions from EXACTLY <paramref name="fromState"/> to <paramref name="toState"/>,
    /// installing <paramref name="installRevision"/> (a token from
    /// <see cref="ReserveLifecycleRevision"/>) as its lifecycle witness — and only when the
    /// current state is <paramref name="fromState"/> and, when
    /// <paramref name="expectedRevision"/> is given, the current witness equals it. Returns
    /// false, changing nothing, otherwise. This is the primitive reversible maintenance needs
    /// at both ends: an archive that only fires from the state AND witness its durable receipt
    /// recorded (so neither an intervening transition nor a same-state replacement — an ABA
    /// through <c>ltm → stm → ltm</c>, or a fresh upsert that landed on the same state — can be
    /// absorbed by a stale plan), and a restore that only fires while the archived state is
    /// still the very transition the receipt claims (closing the validate-then-restore gap a
    /// separate check would leave). Callers performing a PLANNED transition should always pass
    /// <paramref name="expectedRevision"/>; omitting it is for transitions that genuinely only
    /// care about the state.
    /// </summary>
    internal bool TryTransitionLifecycle(
        string id, string ns, string tenantId,
        string fromState, string toState, LifecycleReservation installRevision,
        long? expectedRevision = null)
    {
        // A defaulted token is not a reservation — no minted value is ever 0 (the seed is
        // strictly positive and only ever incremented), so 0 can only mean
        // default(LifecycleReservation), which must install nothing.
        if (installRevision.Value == 0)
            throw new ArgumentException(
                "A default LifecycleReservation cannot be installed; mint one with ReserveLifecycleRevision().",
                nameof(installRevision));

        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is null || !nsEntries.TryGetValue(id, out var tuple))
                return false;

            if (!string.Equals(tuple.Entry.LifecycleState, fromState, StringComparison.Ordinal))
                return false;
            if (expectedRevision is long expected && tuple.Entry.LifecycleRevision != expected)
                return false;
            if (string.Equals(fromState, toState, StringComparison.Ordinal))
                return false;

            tuple.Entry.LifecycleState = toState;
            tuple.Entry.LifecycleRevision = installRevision.Value;
            UpdateQuantization(nsEntries, id, tuple, fromState, toState);
            _store.ScheduleEntryUpsert(ns, tuple.Entry);
            return true;
        }
        finally { nsLock.ExitWriteLock(); }
    }

    /// <summary>
    /// DISARM a planned-but-unfired lifecycle claim: move the entry's lifecycle witness off
    /// <paramref name="expectedRevision"/> — state untouched — only while it still stands
    /// exactly there, atomic under the partition write lock. An undoer retiring a record whose
    /// claims are ARMED (persisted, member still at the planned state and witness, archive CAS
    /// not yet fired) must neutralize them first: the record's deletion does not stop the
    /// executor's pending CAS, but moving the witness makes that CAS refuse — so a crash on
    /// the executor's side after the record is gone can no longer strand members archived
    /// under a receipt nobody holds. A refusal means the claim was not armed (already fired,
    /// already inert, or the member moved) — a skip for the caller, never an error.
    /// </summary>
    internal bool TryBumpLifecycleWitness(
        string id, string ns, string tenantId, long expectedRevision, LifecycleReservation install)
    {
        if (install.Value == 0)
            throw new ArgumentException(
                "A default LifecycleReservation cannot be installed; mint one with ReserveLifecycleRevision().",
                nameof(install));

        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is null || !nsEntries.TryGetValue(id, out var tuple))
                return false;
            if (tuple.Entry.LifecycleRevision != expectedRevision)
                return false;

            tuple.Entry.LifecycleRevision = install.Value;
            _store.ScheduleEntryUpsert(ns, tuple.Entry);
            return true;
        }
        finally { nsLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Pin one partition's OCCUPANCY at <paramref name="baseline"/>: take the partition's READ
    /// lock, verify the occupancy revision still equals the baseline UNDER that lock, and hand
    /// the hold back as a disposable — or return null, holding nothing, when the partition
    /// already moved. While the pin is held no entry in the partition can be written or removed
    /// (every occupancy bump happens under the partition WRITE lock), which is what turns a
    /// staged occupancy check from check-then-act into check-and-hold: the destructive mutation
    /// performed under the pin acts on exactly the occupations the staging examined.
    ///
    /// LOCK ORDER: the pin must be acquired BEFORE the attribution fence and before the graph
    /// or cluster write lock — the same partition-before-fence order an ambiguity-crossing
    /// upsert uses (partition write lock, then the fence's exclusive side) — and nothing may
    /// call back into this index for the pinned partition while holding it: the partition lock
    /// is non-recursive, so a nested read or write from the same thread throws rather than
    /// deadlocks.
    /// </summary>
    internal OccupancyPin? TryPinOccupancy(string ns, string tenantId, long baseline)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterReadLock();
        if (OccupancyRevisionFor(ns, tenantId) != baseline)
        {
            nsLock.ExitReadLock();
            return null;
        }
        return new OccupancyPin(nsLock);
    }

    /// <summary>
    /// Delete <paramref name="summaryId"/> in the exact partition ONLY IF the resident entry is
    /// a summary node of <paramref name="clusterId"/> — and, when <paramref name="onlyIfStamp"/>
    /// is given, of exactly that cluster INCARNATION
    /// (<see cref="CognitiveEntry.SourceClusterStamp"/>) — identity by CONTENT, atomic under
    /// the partition write lock. This is what lets a write-ahead record authorize the deletion
    /// before the summary is ever stored: the record cannot know a revision that does not exist
    /// yet, but it minted the incarnation stamp itself and knows exactly which cluster's
    /// summary it owns. A replacement summary stored by a RECREATED same-id cluster carries a
    /// different stamp and is left standing, as is an unrelated entry a caller manually placed
    /// under the summary's id.
    /// </summary>
    internal bool DeleteIfSummaryOf(string summaryId, string ns, string tenantId, string clusterId,
        string? onlyIfStamp = null, string? onlyIfInstance = null)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        string pk = NamespaceStore.PartitionKey(key);

        string? deletedFromNs = null;
        var nsLock = NsLock(pk);
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is not null
                && nsEntries.TryGetValue(summaryId, out var tuple)
                && tuple.Entry.IsSummaryNode
                && string.Equals(tuple.Entry.SourceClusterId, clusterId, StringComparison.Ordinal)
                && (onlyIfStamp is null
                    || string.Equals(tuple.Entry.SourceClusterStamp, onlyIfStamp, StringComparison.Ordinal))
                // The PHYSICAL-instance condition, for record-driven cleanup: the stamp names
                // a lineage every retry shares, so a stale branch's stamped delete could take
                // down the live summary a concurrent retry published — the retry's record
                // advanced with a fresh instance first, and comparing it here spares that
                // summary. Null = legacy record, stamp-only compare as before.
                && (onlyIfInstance is null
                    || string.Equals(tuple.Entry.SourceClusterInstance, onlyIfInstance, StringComparison.Ordinal))
                && nsEntries.TryRemove(summaryId, out _))
            {
                // Same cleanup as Delete — see its comments.
                _store.UntrackEntry(summaryId, ns, key.Tenant);
                _store.RemoveBM25(summaryId, pk);
                _store.RemoveFromHnsw(pk, summaryId);
                _store.ScheduleEntryDelete(ns, summaryId, key.Tenant);
                BumpOccupancy(key.Tenant, ns);
                deletedFromNs = ns;
            }
        }
        finally { nsLock.ExitWriteLock(); }

        if (deletedFromNs is not null)
        {
            EntryDeleted?.Invoke(this, (deletedFromNs, summaryId));
            return true;
        }
        return false;
    }

    // Per-partition OCCUPANCY revision: moves whenever an entry is written or removed in that
    // (tenant, ns) partition — including a same-slot replacement, which the ATTRIBUTION revision
    // deliberately ignores (no ambiguity boundary is crossed) and which CreatedAt cannot witness
    // on a coarse clock. Destructive maintenance brackets compare it so a staged judgment whose
    // partition changed underneath it is refused rather than applied to occupations it never
    // examined. Bumped inside the partition write lock; read lock-free.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Tenant, string Ns), long> _occupancyRevisions = new();

    private void BumpOccupancy(string tenant, string ns)
        => _occupancyRevisions.AddOrUpdate((tenant, ns), 1L, static (_, v) => v + 1);

    /// <summary>
    /// The partition's occupancy revision — see the field remarks. Compared, never interpreted:
    /// any difference from a recorded value means some entry in the partition was written or
    /// removed, and a maintenance decision staged before that must be re-staged, not applied.
    /// </summary>
    public long OccupancyRevisionFor(string ns, string tenantId)
        => _occupancyRevisions.TryGetValue((Tenancy.Normalize(tenantId), ns), out var v) ? v : 0;

    public void Upsert(CognitiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Every occupation of a slot gets a fresh revision — see CognitiveEntry.Revision.
        entry.Revision = Interlocked.Increment(ref _entryRevisionCounter);
        entry.LifecycleRevision = Interlocked.Increment(ref _lifecycleRevisionCounter);

        // Auto-enrich keywords for BM25 vocabulary bridging
        if (entry.Keywords is null && !string.IsNullOrWhiteSpace(entry.Text))
            entry.Keywords = _documentEnricher.Enrich(entry.Text);

        float norm = VectorMath.Norm(entry.Vector);
        var quantized = entry.LifecycleState is "ltm" or "archived"
            ? VectorQuantizer.Quantize(entry.Vector)
            : null;

        // Partition by (tenant, ns). For the legacy tenant ("") the partition key is exactly the
        // namespace, so this locks/keys identically to the pre-tenant path.
        var nskey = new NsKey(entry.TenantId, entry.Ns);
        string pk = NamespaceStore.PartitionKey(nskey);
        var nsLock = NsLock(pk);
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(entry.Ns);
            var nsEntries = _store.GetOrCreateNamespace(nskey);

            // Enforce memory limits (skip for updates to existing entries)
            if (!nsEntries.ContainsKey(entry.Id))
            {
                if (nsEntries.Count >= _limits.MaxNamespaceSize)
                    throw new InvalidOperationException(
                        $"Namespace '{entry.Ns}' has reached the maximum size of {_limits.MaxNamespaceSize} entries.");
                if (_store.TotalCount >= _limits.MaxTotalCount)
                    throw new InvalidOperationException(
                        $"Total memory count has reached the maximum of {_limits.MaxTotalCount} entries.");
            }

            nsEntries[entry.Id] = (entry, norm, quantized);
            _store.TrackEntry(entry.Id, entry.Ns, entry.TenantId);
            _store.IndexBM25(entry);
            _store.AddToHnsw(nskey, entry.Id, entry.Vector);
            _store.ScheduleEntryUpsert(entry.Ns, entry);
            BumpOccupancy(nskey.Tenant, entry.Ns);
        }
        finally { nsLock.ExitWriteLock(); }

        // Fire event after lock release so handlers can call back into the index safely.
        EntryUpserted?.Invoke(this, entry);
    }

    /// <summary>
    /// Upsert a SUMMARY entry only while its slot is compatible — empty, or occupied by an
    /// entry of the SAME incarnation (equal <see cref="CognitiveEntry.SourceClusterStamp"/>,
    /// null matching null for the legacy stampless world). The compare and the write are one
    /// atom under the partition write lock: a DELAYED summary writer from a replaced
    /// incarnation can therefore never overwrite its successor's summary (and then reap the
    /// wreckage into a dangling pointer) — it refuses here, having written nothing. A
    /// resident that is NOT a summary of the writer's own cluster — an ordinary entry a
    /// caller manually placed under the summary's id in particular — is never overwritten,
    /// whatever the stamps say (the legacy stampless world's null stamp would otherwise
    /// EQUAL an ordinary entry's null stamp). False = refused, slot untouched.
    ///
    /// With <paramref name="replaceStale"/>, the caller — who has verified it speaks for the
    /// cluster's CURRENT incarnation — may additionally replace EXACTLY the stale predecessor
    /// summary it observed, pinned by <paramref name="staleRevision"/>: the PHYSICAL WRITE
    /// identity of the resident the caller saw, not its incarnation stamp. The stamp is a
    /// LINEAGE nonce a collapse retry re-mints onto a recreated cluster, so a stamp-pinned
    /// takeover could destroy the live summary a retry published after the caller's observation
    /// — the revision moves on every write and cannot alias. The replacement is the SAME atom
    /// as the compare — overwrite-in-place on the existing key, so the new-entry quota checks
    /// stay skipped and no instant exists with the slot empty. (A delete-then-retry shape
    /// destroyed the only summary whenever a crash or a quota throw landed between the two
    /// calls.) Anything else in the slot — a successor's summary, a manually stored entry, any
    /// write other than the one observed — still refuses.
    /// </summary>
    internal bool UpsertSummaryIfIncarnation(CognitiveEntry entry, bool replaceStale = false, long? staleRevision = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        entry.Revision = Interlocked.Increment(ref _entryRevisionCounter);
        entry.LifecycleRevision = Interlocked.Increment(ref _lifecycleRevisionCounter);
        if (entry.Keywords is null && !string.IsNullOrWhiteSpace(entry.Text))
            entry.Keywords = _documentEnricher.Enrich(entry.Text);
        float norm = VectorMath.Norm(entry.Vector);
        var quantized = entry.LifecycleState is "ltm" or "archived"
            ? VectorQuantizer.Quantize(entry.Vector)
            : null;

        var nskey = new NsKey(entry.TenantId, entry.Ns);
        string pk = NamespaceStore.PartitionKey(nskey);
        var nsLock = NsLock(pk);
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(entry.Ns);
            var nsEntries = _store.GetOrCreateNamespace(nskey);

            if (nsEntries.TryGetValue(entry.Id, out var resident))
            {
                // A summary writer may only ever replace A SUMMARY OF ITS OWN CLUSTER. An
                // ordinary entry a user stored under the summary's id is never overwritten —
                // including by the legacy stampless world, whose null stamp would otherwise
                // EQUAL an ordinary entry's null stamp and hand the user's memory to the
                // summary machinery. With the resident's summary-hood and cluster verified,
                // the stamp decides WHICH writers proceed: the same incarnation (refresh),
                // or a takeover replacing exactly the stale stamp the caller observed.
                bool ownSlot = resident.Entry.IsSummaryNode
                    && string.Equals(resident.Entry.SourceClusterId, entry.SourceClusterId, StringComparison.Ordinal);
                // Same incarnation means same PHYSICAL cluster object, not merely same
                // lineage: the stamp is reused by a collapse retry's re-created cluster, so
                // stamp equality alone would let a delayed writer admitted by a DEAD object
                // of the lineage overwrite the live object's summary. The instance is never
                // reused (null matching null covers both legacy worlds).
                bool sameIncarnation =
                    string.Equals(resident.Entry.SourceClusterStamp, entry.SourceClusterStamp, StringComparison.Ordinal)
                    && string.Equals(resident.Entry.SourceClusterInstance, entry.SourceClusterInstance, StringComparison.Ordinal);
                bool takeover = replaceStale
                    && staleRevision is not null
                    && resident.Entry.Revision == staleRevision.Value;
                if (!ownSlot || !(sameIncarnation || takeover)) return false;
            }

            if (!nsEntries.ContainsKey(entry.Id))
            {
                if (nsEntries.Count >= _limits.MaxNamespaceSize)
                    throw new InvalidOperationException(
                        $"Namespace '{entry.Ns}' has reached the maximum size of {_limits.MaxNamespaceSize} entries.");
                if (_store.TotalCount >= _limits.MaxTotalCount)
                    throw new InvalidOperationException(
                        $"Total memory count has reached the maximum of {_limits.MaxTotalCount} entries.");
            }

            nsEntries[entry.Id] = (entry, norm, quantized);
            _store.TrackEntry(entry.Id, entry.Ns, entry.TenantId);
            _store.IndexBM25(entry);
            _store.AddToHnsw(nskey, entry.Id, entry.Vector);
            _store.ScheduleEntryUpsert(entry.Ns, entry);
            BumpOccupancy(nskey.Tenant, entry.Ns);
        }
        finally { nsLock.ExitWriteLock(); }

        EntryUpserted?.Invoke(this, entry);
        return true;
    }

    /// <summary>
    /// Capture both sides of a merge under one partition read lock. The copies are detached from
    /// the resident objects, while the accompanying witnesses name both the slot occupation and
    /// every field ordinary index operations can mutate in place.
    /// </summary>
    internal bool TryCaptureMergeEntries(
        string keepId, string archiveId, string ns, string tenantId,
        out MergeEntrySnapshot? keep, out MergeEntrySnapshot? archive)
    {
        keep = null;
        archive = null;
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterReadLock();
        try
        {
            _store.EnsureLoaded(ns);
            var entries = _store.GetNamespace(key);
            if (entries is null
                || !entries.TryGetValue(keepId, out var keepTuple)
                || !entries.TryGetValue(archiveId, out var archiveTuple))
                return false;

            keep = CaptureMergeSnapshot(keepTuple.Entry);
            archive = CaptureMergeSnapshot(archiveTuple.Entry);
            return true;
        }
        finally { nsLock.ExitReadLock(); }
    }

    private static MergeEntrySnapshot CaptureMergeSnapshot(CognitiveEntry source)
    {
        var copy = new CognitiveEntry(
            source.Id, (float[])source.Vector.Clone(), source.Ns, source.Text,
            source.Category, new Dictionary<string, string>(source.Metadata),
            source.LifecycleState, source.CreatedAt, source.LastAccessedAt,
            source.AccessCount, source.ActivationEnergy, source.IsSummaryNode,
            source.SourceClusterId, source.Keywords, source.TenantId)
        {
            Revision = source.Revision,
            LifecycleRevision = source.LifecycleRevision,
            SourceClusterStamp = source.SourceClusterStamp,
            SourceClusterInstance = source.SourceClusterInstance
        };
        return new MergeEntrySnapshot(
            copy, source.Revision, source.LifecycleRevision, source.LifecycleState,
            source.LastAccessedAt, source.AccessCount, source.ActivationEnergy);
    }

    private static bool MatchesMergeSnapshot(CognitiveEntry resident, MergeEntrySnapshot snapshot)
        => resident.Revision == snapshot.Revision
           && resident.LifecycleRevision == snapshot.LifecycleRevision
           && string.Equals(resident.LifecycleState, snapshot.LifecycleState, StringComparison.Ordinal)
           && resident.LastAccessedAt == snapshot.LastAccessedAt
           && resident.AccessCount == snapshot.AccessCount
           && resident.ActivationEnergy.Equals(snapshot.ActivationEnergy)
           && string.Equals(resident.Text, snapshot.Entry.Text, StringComparison.Ordinal)
           && string.Equals(resident.Category, snapshot.Entry.Category, StringComparison.Ordinal)
           && string.Equals(resident.Keywords, snapshot.Entry.Keywords, StringComparison.Ordinal)
           && resident.CreatedAt == snapshot.Entry.CreatedAt
           && resident.IsSummaryNode == snapshot.Entry.IsSummaryNode
           && string.Equals(resident.SourceClusterId, snapshot.Entry.SourceClusterId, StringComparison.Ordinal)
           && string.Equals(resident.SourceClusterStamp, snapshot.Entry.SourceClusterStamp, StringComparison.Ordinal)
           && string.Equals(resident.SourceClusterInstance, snapshot.Entry.SourceClusterInstance, StringComparison.Ordinal)
           && resident.Vector.AsSpan().SequenceEqual(snapshot.Entry.Vector)
           && MetadataEquals(resident.Metadata, snapshot.Entry.Metadata);

    private static bool MetadataEquals(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> expected)
    {
        if (current.Count != expected.Count)
            return false;
        foreach (var (key, value) in expected)
            if (!current.TryGetValue(key, out var actual)
                || !string.Equals(actual, value, StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>
    /// Test seam immediately before the coherent merge takes its partition write lock.
    /// </summary>
    internal Action? OnBeforeMergeCommit;

    /// <summary>
    /// Atomically validate and update both entry sides and publish their prepared graph/cluster
    /// transfer. Lock order is partition -&gt; attribution fence -&gt; graph -&gt; cluster. The partition
    /// remains write-locked from witness comparison through both topology publications, so neither
    /// produced occupation can be replaced or changed in place in the former post-entry window.
    /// </summary>
    internal bool TryCommitMerge(
        MergeEntrySnapshot keep,
        MergeEntrySnapshot archive,
        CognitiveEntry updatedKeep,
        PreparedMergeTopology topology,
        KnowledgeGraph graph,
        ClusterManager clusters,
        out CommittedMergeTopology? committed)
    {
        committed = null;
        if (!string.Equals(keep.Entry.Ns, archive.Entry.Ns, StringComparison.Ordinal)
            || !string.Equals(keep.Entry.Ns, topology.Namespace, StringComparison.Ordinal)
            || !string.Equals(keep.Entry.TenantId, archive.Entry.TenantId, StringComparison.Ordinal)
            || !string.Equals(keep.Entry.TenantId, topology.TenantId, StringComparison.Ordinal)
            || !string.Equals(archive.Entry.Id, topology.FromId, StringComparison.Ordinal)
            || !string.Equals(keep.Entry.Id, topology.ToId, StringComparison.Ordinal))
            return false;

        var keywords = updatedKeep.Keywords
            ?? (string.IsNullOrWhiteSpace(updatedKeep.Text)
                ? null
                : _documentEnricher.Enrich(updatedKeep.Text));
        float norm = VectorMath.Norm(updatedKeep.Vector);
        var quantized = updatedKeep.LifecycleState is "ltm" or "archived"
            ? VectorQuantizer.Quantize(updatedKeep.Vector)
            : null;

        OnBeforeMergeCommit?.Invoke();

        var key = new NsKey(keep.Entry.TenantId, keep.Entry.Ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        bool installed = false;
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(key.Ns);
            var entries = _store.GetNamespace(key);
            if (entries is null
                || OccupancyRevisionFor(key.Ns, key.Tenant) != topology.PartitionOccupancy
                || !entries.TryGetValue(keep.Entry.Id, out var residentKeep)
                || !entries.TryGetValue(archive.Entry.Id, out var residentArchive)
                || !MatchesMergeSnapshot(residentKeep.Entry, keep)
                || !MatchesMergeSnapshot(residentArchive.Entry, archive)
                || residentKeep.Entry.IsSummaryNode
                || residentArchive.Entry.IsSummaryNode
                || residentKeep.Entry.LifecycleState == "archived"
                || residentArchive.Entry.LifecycleState == "archived")
                return false;

            bool topologyCommitted = graph.TryCommitPreparedMerge(
                topology, clusters,
                () =>
                {
                    updatedKeep.Keywords = keywords;
                    updatedKeep.Revision = Interlocked.Increment(ref _entryRevisionCounter);
                    updatedKeep.LifecycleRevision = Interlocked.Increment(ref _lifecycleRevisionCounter);
                    entries[updatedKeep.Id] = (updatedKeep, norm, quantized);
                    _store.TrackEntry(updatedKeep.Id, updatedKeep.Ns, updatedKeep.TenantId);
                    _store.IndexBM25(updatedKeep);
                    _store.AddToHnsw(key, updatedKeep.Id, updatedKeep.Vector);
                    _store.ScheduleEntryUpsert(key.Ns, updatedKeep);
                    BumpOccupancy(key.Tenant, key.Ns);

                    string priorArchiveState = residentArchive.Entry.LifecycleState;
                    residentArchive.Entry.LifecycleState = "archived";
                    residentArchive.Entry.LifecycleRevision =
                        Interlocked.Increment(ref _lifecycleRevisionCounter);
                    UpdateQuantization(
                        entries, archive.Entry.Id, residentArchive,
                        priorArchiveState, residentArchive.Entry.LifecycleState);
                    _store.ScheduleEntryUpsert(key.Ns, residentArchive.Entry);
                    installed = true;
                    return true;
                },
                out committed);
            if (!topologyCommitted)
                return false;
        }
        finally { nsLock.ExitWriteLock(); }

        if (!installed || committed is null)
            return false;

        clusters.CompletePreparedMergeMembership(committed.Membership);
        EntryUpserted?.Invoke(this, updatedKeep);
        return true;
    }

    /// <summary>
    /// Test seam: invoked immediately before <see cref="UpsertIfRevision"/> takes the
    /// partition write lock, which is the exact window between a caller's read and its
    /// compare-and-swap. A test installs a competing occupation from here to prove the
    /// compare refuses; null in production, like every other seam in this assembly.
    /// </summary>
    internal Action? OnBeforeConditionedUpsert;

    /// <summary>
    /// Upsert ONLY over the occupation whose <see cref="CognitiveEntry.Revision"/> matches
    /// <paramref name="onlyIfRevision"/> — the write half of the conditioned pair whose
    /// delete half is <see cref="Delete(string, string, string, long)"/>.
    ///
    /// A caller that reads an entry, judges it, and builds a replacement out of its fields is
    /// holding a SNAPSHOT. Writing that back unconditionally publishes stale content over
    /// whatever occupies the slot now: the newer occupation is discarded without trace, and
    /// every screen the caller ran — summary-hood, lifecycle state, ownership — was applied to
    /// an entry that is no longer there. Comparing HERE, under the same write lock that
    /// installs, makes the judgement and the write one atom; an entry replaced since (same id,
    /// different revision) refuses instead. Revision is the witness for the same reason the
    /// conditioned delete uses it: a same-tick replacement repeats CreatedAt, while every
    /// upsert moves Revision.
    ///
    /// An ABSENT slot refuses too. The caller judged an entry that existed, and a vanished
    /// occupation has moved just as surely as a replaced one — installing here would
    /// resurrect a deleted id under the guise of an update. No quota check is needed for the
    /// same reason: this never creates a slot.
    ///
    /// Returns false without installing when the id is absent or the resident does not match.
    /// </summary>
    public bool UpsertIfRevision(CognitiveEntry entry, long onlyIfRevision)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // A REFUSAL MUST LEAVE NOTHING BEHIND, and that starts with not writing to `entry`.
        // Get hands out the LIVE resident object, so a caller may legitimately pass the very
        // entry that occupies the slot. Minting the revisions up front mutated that resident
        // IN PLACE and then refused: the witness every other holder compares against had
        // moved, so a collapse receipt's restore CAS — which fires only from the exact
        // LifecycleRevision it installed — would refuse forever against an entry nobody had
        // actually changed. Derived values are computed into locals here; the entry itself is
        // touched only once the compare has committed us to installing it.
        var keywords = entry.Keywords
            ?? (string.IsNullOrWhiteSpace(entry.Text) ? null : _documentEnricher.Enrich(entry.Text));
        float norm = VectorMath.Norm(entry.Vector);
        var quantized = entry.LifecycleState is "ltm" or "archived"
            ? VectorQuantizer.Quantize(entry.Vector)
            : null;

        var nskey = new NsKey(entry.TenantId, entry.Ns);
        string pk = NamespaceStore.PartitionKey(nskey);

        OnBeforeConditionedUpsert?.Invoke();

        var nsLock = NsLock(pk);
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(entry.Ns);
            // NON-CREATING, like the conditioned delete: a refused CAS must not bring a
            // namespace into existence as a side effect of declining to write to it.
            var nsEntries = _store.GetNamespace(nskey);

            if (nsEntries is null
                || !nsEntries.TryGetValue(entry.Id, out var resident)
                || resident.Entry.Revision != onlyIfRevision)
                return false;

            // Committed. Only now does the caller's entry become the new occupation, so a
            // refusal above has left both it and the resident exactly as they were.
            entry.Keywords = keywords;
            entry.Revision = Interlocked.Increment(ref _entryRevisionCounter);
            entry.LifecycleRevision = Interlocked.Increment(ref _lifecycleRevisionCounter);

            // Identical install to the unconditional overload — see its comments.
            nsEntries[entry.Id] = (entry, norm, quantized);
            _store.TrackEntry(entry.Id, entry.Ns, entry.TenantId);
            _store.IndexBM25(entry);
            _store.AddToHnsw(nskey, entry.Id, entry.Vector);
            _store.ScheduleEntryUpsert(entry.Ns, entry);
            BumpOccupancy(nskey.Tenant, entry.Ns);
        }
        finally { nsLock.ExitWriteLock(); }

        EntryUpserted?.Invoke(this, entry);
        return true;
    }

    /// <summary>Batch upsert entries with a single write-lock acquisition.</summary>
    public int UpsertBatch(IReadOnlyList<CognitiveEntry> entries)
    {
        if (entries.Count == 0) return 0;

        // Pre-compute enrichment, norms, and quantization outside the lock
        var prepared = new List<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>(entries.Count);
        foreach (var entry in entries)
        {
            // Every occupation of a slot gets a fresh revision — see CognitiveEntry.Revision.
            entry.Revision = Interlocked.Increment(ref _entryRevisionCounter);
        entry.LifecycleRevision = Interlocked.Increment(ref _lifecycleRevisionCounter);
            if (entry.Keywords is null && !string.IsNullOrWhiteSpace(entry.Text))
                entry.Keywords = _documentEnricher.Enrich(entry.Text);
            float norm = VectorMath.Norm(entry.Vector);
            var quantized = entry.LifecycleState is "ltm" or "archived"
                ? VectorQuantizer.Quantize(entry.Vector)
                : null;
            prepared.Add((entry, norm, quantized));
        }

        // Group by namespace so we can take one write lock per ns (parallel across ns).
        // Batch entries that belong to the same ns share that ns's write lock for the
        // duration of their sub-batch; ns A and ns B never block each other.
        var totalLimitHit = false;
        var accepted = new List<CognitiveEntry>(prepared.Count);
        // Group by (tenant, ns) so each partition takes one write lock. For the legacy tenant this
        // is identical to grouping by namespace.
        foreach (var nsGroup in prepared.GroupBy(p => new NsKey(p.Entry.TenantId, p.Entry.Ns)))
        {
            if (totalLimitHit) break;

            var nskey = nsGroup.Key;
            string pk = NamespaceStore.PartitionKey(nskey);
            var nsLock = NsLock(pk);
            nsLock.EnterWriteLock();
            try
            {
                _store.EnsureLoaded(nskey.Ns);
                var nsEntries = _store.GetOrCreateNamespace(nskey);

                foreach (var (entry, norm, quantized) in nsGroup)
                {
                    if (!nsEntries.ContainsKey(entry.Id))
                    {
                        if (nsEntries.Count >= _limits.MaxNamespaceSize)
                            continue; // skip entries that would exceed namespace limit
                        if (_store.TotalCount >= _limits.MaxTotalCount)
                        {
                            totalLimitHit = true;
                            break; // stop if total limit reached
                        }
                    }

                    nsEntries[entry.Id] = (entry, norm, quantized);
                    _store.TrackEntry(entry.Id, entry.Ns, entry.TenantId);
                    _store.IndexBM25(entry);
                    _store.AddToHnsw(nskey, entry.Id, entry.Vector);
                    _store.ScheduleEntryUpsert(entry.Ns, entry);
                    BumpOccupancy(nskey.Tenant, entry.Ns);
                    accepted.Add(entry);
                }
            }
            finally { nsLock.ExitWriteLock(); }
        }

        // Fire events after all locks released, one per accepted entry.
        var handler = EntryUpserted;
        if (handler is not null)
        {
            foreach (var entry in accepted)
                handler(this, entry);
        }

        return accepted.Count;
    }

    /// <summary>Get an entry by ID, searching all namespaces. Resolves id→ns lock-free then takes that ns's read lock.</summary>
    public CognitiveEntry? Get(string id)
    {
        if (!_store.TryResolveOrLoad(id, out var ns))
            return null;

        var nsLock = NsLock(ns);
        nsLock.EnterReadLock();
        try
        {
            var resolved = _store.GetNamespace(ns);
            if (resolved is not null && resolved.TryGetValue(id, out var tuple))
                return tuple.Entry;
            return null;
        }
        finally { nsLock.ExitReadLock(); }
    }

    /// <summary>
    /// Get an entry by ID within a specific namespace and tenant. Per-partition read lock.
    /// <paramref name="tenantId"/> is required — pass "" to resolve within the legacy partition.
    /// An id that exists only under a different tenant returns null — cross-tenant id-probing
    /// is impossible.
    /// </summary>
    public CognitiveEntry? Get(string id, string ns, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        string pk = NamespaceStore.PartitionKey(key);
        var nsLock = NsLock(pk);
        nsLock.EnterReadLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is not null && nsEntries.TryGetValue(id, out var tuple))
                return tuple.Entry;
            return null;
        }
        finally { nsLock.ExitReadLock(); }
    }

    /// <summary>
    /// Get an entry by bare id within one tenant. Returns null when no match exists or when the id
    /// is ambiguous across multiple namespaces in that tenant. This scan is intentionally named
    /// (rather than overloaded) so it cannot be confused with
    /// <see cref="Get(string, string, string)"/>.
    /// </summary>
    public CognitiveEntry? GetForTenant(string id, string tenantId)
        => ScanForTenant(id, tenantId, namespaceSnapshot: null).Match;

    /// <summary>
    /// How many of the tenant's namespaces contain <paramref name="id"/>, saturating at 2.
    /// This is the single expression of the ambiguity rule: a bare id is not an identity, so every
    /// caller that reaches an entry by id alone only ever needs to distinguish none (0) from
    /// exactly-one (1) from ambiguous (2). Counting past the second hit would cost partition
    /// lookups that no caller can observe.
    ///
    /// The namespaces actually probed come from <see cref="GetNamespacesContaining"/>, so the cost
    /// is one or two partition reads rather than one per namespace the tenant owns.
    ///
    /// Pass <paramref name="namespaceSnapshot"/> to bound which namespaces the count is willing to
    /// consider — a sweep that has already listed them says so rather than re-listing per id. It is
    /// no longer a defence against a full store reload per entry, because
    /// <c>NamespaceStore.LoadAll</c> now caches its completion; what it still does, and what
    /// callers depend on, is restrict the walk to the namespaces it names.
    /// </summary>
    public int CountNamespacesContaining(
        string id, string tenantId, IReadOnlyList<string>? namespaceSnapshot = null)
        => ScanForTenant(id, tenantId, namespaceSnapshot).Found;

    /// <summary>
    /// Shared scan behind <see cref="GetForTenant"/>, <see cref="DeleteForTenant"/> and
    /// <see cref="CountNamespacesContaining"/>, so the three can never disagree about what counts
    /// as ambiguous. Returns no match the moment a second namespace claims the id — an ambiguous
    /// id resolves to nothing rather than to an arbitrary winner.
    /// </summary>
    private (CognitiveEntry? Match, string? Ns, int Found) ScanForTenant(
        string id, string tenantId, IReadOnlyList<string>? namespaceSnapshot)
    {
        string tenant = Tenancy.Normalize(tenantId);

        // Probe the candidate namespaces rather than the tenant's whole namespace list. A namespace
        // that does not hold the id contributed nothing to any of Match/Ns/Found before, so
        // dropping it changes no outcome — only the number of partitions touched.
        //
        // A caller-supplied snapshot means the caller has ALREADY listed the tenant's namespaces,
        // and that listing is what loaded every persisted namespace, so the index is complete
        // without routing through GetNamespacesContaining here. The snapshot is a restriction the
        // caller chose to impose, not a performance escape hatch — LoadAll no longer re-enumerates.
        IReadOnlyList<string> candidates = namespaceSnapshot is null
            ? GetNamespacesContaining(id, tenant)
            : _store.GetCandidateNamespaces(id, tenant);

        CognitiveEntry? match = null;
        string? matchNs = null;
        int found = 0;

        foreach (var ns in candidates)
        {
            // The snapshot still bounds the walk. It is a restriction on which namespaces the
            // caller is willing to consider, and one outside it was never visited before either;
            // honoring it keeps this identical to the old scan for a narrowed snapshot instead of
            // quietly widening what a sweep counts.
            if (namespaceSnapshot is not null && !namespaceSnapshot.Contains(ns))
                continue;

            // Re-read the partition instead of trusting the index. The candidate index is
            // maintained outside the per-partition locks, so a placement a concurrent delete just
            // retired can still be named here; confirming with Get keeps a stale candidate from
            // inventing an entry, and keeps Found a count of what actually exists.
            var candidate = Get(id, ns, tenantId: tenant);
            if (candidate is null)
                continue;
            if (++found == 2)
                return (null, null, 2);
            match = candidate;
            matchNs = ns;
        }

        return (match, matchNs, found);
    }

    /// <summary>
    /// Delete an entry by ID, searching all namespaces within the LEGACY tenant. Resolves id→ns
    /// lock-free then takes that ns's write lock. Only ever reaches legacy-tenant entries — a global
    /// id-delete can never remove another tenant's entry. Use <see cref="Delete(string, string, string)"/>
    /// for tenant-scoped deletes.
    /// </summary>
    public bool Delete(string id)
    {
        if (!_store.TryResolveOrLoad(id, out var ns))
            return false;

        string? deletedFromNs = null;
        // Legacy tenant: partition key == ns.
        var nsLock = NsLock(ns);
        nsLock.EnterWriteLock();
        try
        {
            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is not null && nsEntries.TryRemove(id, out _))
            {
                // Legacy tenant throughout this overload, so the placement being retracted is
                // exactly ("", ns, id) — the one the locator resolved us to a few lines above.
                _store.UntrackEntry(id, ns, string.Empty);
                _store.RemoveBM25(id, ns);
                _store.RemoveFromHnsw(ns, id);
                _store.ScheduleEntryDelete(ns, id, string.Empty);
                BumpOccupancy(string.Empty, ns);
                deletedFromNs = ns;
            }
        }
        finally { nsLock.ExitWriteLock(); }

        if (deletedFromNs is not null)
        {
            EntryDeleted?.Invoke(this, (deletedFromNs, id));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Delete an entry scoped to a specific (tenant, ns) partition. Returns false when the entry is
    /// not present in exactly that partition — including when the id exists only under a different
    /// tenant or a different namespace. Pass "" to target the legacy partition.
    /// </summary>
    public bool Delete(string id, string ns, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        string pk = NamespaceStore.PartitionKey(key);

        string? deletedFromNs = null;
        var nsLock = NsLock(pk);
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is not null && nsEntries.TryRemove(id, out _))
            {
                // Unconditional on tenant now: the candidate index is tenant-qualified and must
                // lose this placement for EVERY tenant, or a deleted entry keeps offering its
                // namespace to the next bare-id resolution. UntrackEntry still confines the legacy
                // locator and TotalCount to the legacy tenant, exactly as the guard here did.
                _store.UntrackEntry(id, ns, key.Tenant);
                _store.RemoveBM25(id, pk);
                _store.RemoveFromHnsw(pk, id);
                _store.ScheduleEntryDelete(ns, id, key.Tenant);
                BumpOccupancy(key.Tenant, ns);
                deletedFromNs = ns;
            }
        }
        finally { nsLock.ExitWriteLock(); }

        if (deletedFromNs is not null)
        {
            EntryDeleted?.Invoke(this, (deletedFromNs, id));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Delete an entry scoped to a specific (tenant, ns) partition, but ONLY the occupation
    /// whose <see cref="CognitiveEntry.Revision"/> matches <paramref name="onlyIfRevision"/>.
    /// The check and the removal run under the same partition write lock, so a caller holding a
    /// staged snapshot can delete exactly the version it judged: an entry replaced since — same
    /// id, different revision — is left standing, where a bare check-then-delete would race the
    /// replacement and take the fresh entry down anyway. Revision, not CreatedAt, is the
    /// witness: a same-tick replacement repeats CreatedAt, while every upsert moves Revision.
    /// Returns false when the id is absent or the resident occupation does not match.
    /// </summary>
    public bool Delete(string id, string ns, string tenantId, long onlyIfRevision)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        string pk = NamespaceStore.PartitionKey(key);

        string? deletedFromNs = null;
        var nsLock = NsLock(pk);
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is not null
                && nsEntries.TryGetValue(id, out var tuple)
                && tuple.Entry.Revision == onlyIfRevision
                && nsEntries.TryRemove(id, out _))
            {
                // Same cleanup as the unconditional overload — see its comments.
                _store.UntrackEntry(id, ns, key.Tenant);
                _store.RemoveBM25(id, pk);
                _store.RemoveFromHnsw(pk, id);
                _store.ScheduleEntryDelete(ns, id, key.Tenant);
                BumpOccupancy(key.Tenant, ns);
                deletedFromNs = ns;
            }
        }
        finally { nsLock.ExitWriteLock(); }

        if (deletedFromNs is not null)
        {
            EntryDeleted?.Invoke(this, (deletedFromNs, id));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Delete an entry by bare id within one tenant. Refuses ambiguous ids that occur in more than
    /// one namespace, preventing a tenant-scoped caller from deleting an arbitrary match.
    /// </summary>
    public bool DeleteForTenant(string id, string tenantId)
    {
        var matchNamespace = ScanForTenant(id, tenantId, namespaceSnapshot: null).Ns;
        return matchNamespace is not null && Delete(id, matchNamespace, tenantId: tenantId);
    }

    // ── Search ──

    /// <summary>Unified search entry point supporting vector-only, hybrid, and deep recall modes.</summary>
    public IReadOnlyList<CognitiveSearchResult> Search(SearchRequest request)
    {
        if (request.Query is null || request.Query.Length == 0)
            throw new ArgumentException("Query vector must not be null or empty.", nameof(request));
        if (request.K <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "K must be positive.");

        // The namespace becomes half of a composite partition key below. A namespace carrying the
        // separator — or any other control character — could compose a key that reads as a
        // different (tenant, ns) pair, so it is rejected here rather than allowed to forge one.
        // This is a read path that never constructs an entry, so nothing else validates it.
        Tenancy.ValidatePartitionComponent(request.Namespace, nameof(request));

        // Scope the entire search to the (tenant, ns) partition. For the legacy tenant ("") the
        // partition key is the namespace, so candidate generation, BM25 lookup and getEntry behave
        // exactly as before. Tenant candidate sets never mix, so RRF/rerank run unchanged on an
        // already-isolated pool.
        var nskey = new NsKey(Tenancy.Normalize(request.TenantId), request.Namespace);
        string pk = NamespaceStore.PartitionKey(nskey);

        // Hold the per-namespace read lock across the ENTIRE search — the candidate snapshot, the
        // vector search, and every BM25/hybrid/PRF/auto-escalation call below. BM25Index.Search reads
        // shared inner NamespaceIndex state (plain Dictionary/HashSet) that concurrent writers mutate
        // under this same lock's write side. Releasing the lock right after the snapshot (as this method
        // did previously) left every BM25 call unlocked, so a background decay/consolidation/accretion
        // pass or a concurrent remember could mutate those collections mid-enumeration — risking a
        // "Collection was modified" throw or torn scores. This is a read lock, so concurrent searches
        // still run fully in parallel; only writers to this namespace wait.
        var nsLock = NsLock(pk);
        nsLock.EnterReadLock();
        try
        {
            _store.EnsureLoaded(request.Namespace);
            var nsEntries = _store.GetNamespace(nskey);
            if (nsEntries is null || nsEntries.Count == 0)
                return Array.Empty<CognitiveSearchResult>();
            IReadOnlyCollection<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> snapshot =
                nsEntries.Values.ToList();
            HnswIndex? hnswIndex = _store.GetHnswIndex(pk);

            // Entry resolver for BM25-only ("keyword rescue") candidates. Resolves per-candidate
            // straight from nsEntries rather than allocating a second whole-namespace map up front:
            // the read lock is held for the entire method, so this partition dictionary is stable,
            // and it is already scoped to this (tenant, ns) partition so it can never surface another
            // tenant's entry. Reading nsEntries here instead of calling Get() also avoids re-entering
            // this non-recursive read lock.
            Func<string, string, CognitiveEntry?> getEntry = (eid, _) =>
                nsEntries.TryGetValue(eid, out var tuple) ? tuple.Entry : null;

            // When diversity is active, fetch more candidates so MMR has a broader pool
            int diversityMultiplier = request.Diversity ? 3 : 1;

            if (request.Hybrid && request.QueryText is not null)
            {
                // Expand query with domain synonyms for BM25 vocabulary bridging
                var expandedQueryText = _synonymExpander.Expand(request.QueryText);

                // Scale vector candidate pool with namespace size for better hybrid recall
                int candidateK = snapshot.Count >= 5000
                    ? Math.Max(request.K * 12, 80)
                    : Math.Max(request.K * 6, 30);
                var vectorResults = _vectorSearch.Search(
                    request.Query, snapshot, candidateK, request.MinScore,
                    request.Category, request.IncludeStates, false, hnswIndex);
                int hybridK = request.K * diversityMultiplier;
                var hybridResults = _hybridSearch.HybridSearch(
                    vectorResults, expandedQueryText, pk, hybridK,
                    request.IncludeStates, request.Category,
                    request.Rerank, request.RrfK, _bm25, _reranker, getEntry, request.Query, snapshot.Count);

                // Auto-PRF: if top hybrid result is low confidence, expand query with
                // terms from initial results and re-search for improved recall
                if (hybridResults.Count > 0 &&
                    hybridResults[0].Score < 0.04f &&
                    hybridResults.Count >= 3)
                {
                    var prfQuery = _queryExpander.Expand(expandedQueryText, hybridResults, maxTerms: 6, minDocFreq: 2);
                    if (prfQuery != expandedQueryText)
                    {
                        var prfResults = _hybridSearch.HybridSearch(
                            vectorResults, prfQuery, pk, hybridK,
                            request.IncludeStates, request.Category,
                            request.Rerank, request.RrfK, _bm25, _reranker, getEntry, request.Query, snapshot.Count);
                        // Use PRF results if they improve top score
                        if (prfResults.Count > 0 && prfResults[0].Score > hybridResults[0].Score)
                            return ApplyDiversity(ApplyCategoryBoost(prfResults, request.QueryText), request, snapshot);
                    }
                }

                return ApplyDiversity(ApplyCategoryBoost(hybridResults, request.QueryText), request, snapshot);
            }

            // Vector-only search with auto-escalation to hybrid when confidence is low
            int vectorK = request.K * diversityMultiplier;
            var vectorOnlyResults = _vectorSearch.Search(
                request.Query, snapshot, vectorK, request.MinScore,
                request.Category, request.IncludeStates, request.SummaryFirst, hnswIndex);

            // Auto-escalate: if top vector result is low confidence and we have query text,
            // retry as hybrid search to let BM25 rescue keyword-dependent queries
            if (request.QueryText is not null &&
                vectorOnlyResults.Count > 0 &&
                vectorOnlyResults[0].Score < 0.50f &&
                !request.SummaryFirst)
            {
                int candidateK = snapshot.Count >= 5000
                    ? Math.Max(request.K * 10, 60)
                    : Math.Max(request.K * 5, 25);
                var broadVectorResults = _vectorSearch.Search(
                    request.Query, snapshot, candidateK, request.MinScore,
                    request.Category, request.IncludeStates, false, hnswIndex);
                var expandedQueryText = _synonymExpander.Expand(request.QueryText);
                var escalatedResults = ApplyCategoryBoost(_hybridSearch.HybridSearch(
                    broadVectorResults, expandedQueryText, pk, request.K * diversityMultiplier,
                    request.IncludeStates, request.Category,
                    false, request.RrfK, _bm25, _reranker, getEntry, request.Query, snapshot.Count), request.QueryText);
                return ApplyDiversity(escalatedResults, request, snapshot);
            }

            return ApplyDiversity(vectorOnlyResults, request, snapshot);
        }
        finally { nsLock.ExitReadLock(); }
    }

    /// <summary>
    /// Namespace-scoped k-nearest-neighbor search with two-stage Int8 screening pipeline.
    /// <paramref name="tenantId"/> is required and sits directly after the namespace so the
    /// tenant-qualified identity reads first; pass "" for the legacy partition.
    /// </summary>
    public IReadOnlyList<CognitiveSearchResult> Search(
        float[] query, string ns, string tenantId, int k = 5, float minScore = 0f,
        string? category = null, HashSet<string>? includeStates = null, bool summaryFirst = false)
        => Search(new SearchRequest
        {
            Query = query, Namespace = ns, K = k, MinScore = minScore,
            Category = category, IncludeStates = includeStates, SummaryFirst = summaryFirst,
            TenantId = tenantId
        });

    /// <summary>
    /// Hybrid search combining vector + BM25 via Reciprocal Rank Fusion. The required
    /// <paramref name="tenantId"/> scopes the search to a tenant partition ("" = legacy
    /// tenant, i.e. identical to the pre-tenant behavior). RRF fusion parameters are unchanged.
    /// </summary>
    public IReadOnlyList<CognitiveSearchResult> HybridSearch(
        float[] query, string queryText, string ns, string tenantId, int k = 5, float minScore = 0f,
        string? category = null, HashSet<string>? includeStates = null,
        bool rerank = false, int rrfK = 60)
        => Search(new SearchRequest
        {
            Query = query, QueryText = queryText, Namespace = ns, K = k, MinScore = minScore,
            Category = category, IncludeStates = includeStates, Hybrid = true, Rerank = rerank,
            RrfK = rrfK, TenantId = tenantId
        });

    /// <summary>Apply token-level reranking to existing search results.</summary>
    public IReadOnlyList<CognitiveSearchResult> Rerank(
        string queryText, IReadOnlyList<CognitiveSearchResult> results)
        => _reranker.Rerank(queryText, results);

    /// <summary>Search ALL states including archived (for deep_recall). Pass tenantId "" for the legacy partition.</summary>
    public IReadOnlyList<CognitiveSearchResult> SearchAllStates(
        float[] query, string ns, string tenantId, int k = 10, float minScore = 0.3f,
        string? queryText = null, bool hybrid = false, bool rerank = false)
        => Search(new SearchRequest
        {
            Query = query, Namespace = ns, K = k, MinScore = minScore,
            IncludeStates = AllStates, TenantId = tenantId,
            QueryText = queryText ?? string.Empty, Hybrid = hybrid, Rerank = rerank
        });

    /// <summary>
    /// Search across multiple namespaces and merge results using Reciprocal Rank Fusion.
    /// Returns results annotated with their source namespace.
    /// <paramref name="queryText"/> is required-but-nullable rather than defaulted: tenantId may
    /// not jump over a string slot (an old positional query text would silently bind as the
    /// tenant), so callers without text pass <c>queryText: null</c> explicitly.
    /// <paramref name="tenantId"/> is required; pass "" for the legacy partition.
    /// </summary>
    public IReadOnlyList<Models.CrossSearchResult> SearchMultiple(
        float[] query, IReadOnlyList<string> namespaces, string? queryText, string tenantId,
        int k = 5, float minScore = 0f, string? category = null,
        HashSet<string>? includeStates = null, bool hybrid = false,
        bool rerank = false, int rrfK = 60, bool summaryFirst = false,
        bool diversity = false, float diversityLambda = 0.5f)
    {
        if (namespaces.Count == 0)
            return Array.Empty<Models.CrossSearchResult>();

        // Search each namespace independently. When diversity is requested we route
        // through the SearchRequest path to pick up cluster-aware MMR reranking;
        // otherwise we keep the fast hybrid/vector path for backward compat.
        var allRanked = new Dictionary<string, (Models.CrossSearchResult Result, float RrfScore)>();

        foreach (var ns in namespaces)
        {
            IReadOnlyList<CognitiveSearchResult> nsResults;
            if (diversity)
            {
                nsResults = Search(new SearchRequest
                {
                    Query = query,
                    Namespace = ns,
                    QueryText = queryText,
                    K = k,
                    MinScore = minScore,
                    Category = category,
                    IncludeStates = includeStates,
                    Hybrid = hybrid && queryText is not null,
                    Rerank = rerank,
                    RrfK = rrfK,
                    SummaryFirst = summaryFirst,
                    Diversity = true,
                    DiversityLambda = diversityLambda,
                    TenantId = tenantId,
                });
            }
            else if (hybrid && queryText is not null)
            {
                nsResults = HybridSearch(query, queryText, ns, tenantId: tenantId, k, minScore, category, includeStates, rerank, rrfK);
            }
            else
            {
                // Route through the request path so the tenant scope is applied; for the legacy
                // tenant this constructs exactly the same request as Search(query, ns, ...).
                nsResults = Search(new SearchRequest
                {
                    Query = query, Namespace = ns, K = k, MinScore = minScore,
                    Category = category, IncludeStates = includeStates,
                    SummaryFirst = summaryFirst, TenantId = tenantId
                });
            }

            // Assign RRF scores based on rank within this namespace
            for (int rank = 0; rank < nsResults.Count; rank++)
            {
                var r = nsResults[rank];
                float rrfScore = 1.0f / (rrfK + rank + 1);
                var key = $"{ns}:{r.Id}";

                var crossResult = new Models.CrossSearchResult(
                    r.Id, r.Text, r.Score, ns, r.LifecycleState,
                    r.Category, r.Metadata, r.AccessCount);

                if (allRanked.TryGetValue(key, out var existing))
                {
                    // Same entry found in multiple namespaces — sum RRF scores
                    allRanked[key] = (crossResult, existing.RrfScore + rrfScore);
                }
                else
                {
                    allRanked[key] = (crossResult, rrfScore);
                }
            }
        }

        // Sort by RRF score descending, take top-K
        return allRanked.Values
            .OrderByDescending(x => x.RrfScore)
            .Take(k)
            .Select(x => x.Result)
            .ToList();
    }

    // ── Duplicate Detection (delegated to DuplicateDetector) ──

    /// <summary>Find near-duplicates for a single entry within its namespace (O(N) scan). Pass tenantId "" for the legacy partition.</summary>
    public IReadOnlyList<(string IdA, string IdB, float Similarity)> FindDuplicatesForEntry(
        string ns, string entryId, string tenantId, float threshold = 0.95f)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterReadLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is null)
                return Array.Empty<(string, string, float)>();

            nsEntries.TryGetValue(entryId, out var target);
            return _duplicateDetector.FindDuplicatesForEntry(entryId, target, nsEntries, threshold);
        }
        finally { nsLock.ExitReadLock(); }
    }

    /// <summary>Find near-duplicate entries within a namespace by pairwise cosine similarity. Pass tenantId "" for the legacy partition.</summary>
    public IReadOnlyList<(string IdA, string IdB, float Similarity)> FindDuplicates(
        string ns, string tenantId, float threshold = 0.95f, string? category = null,
        HashSet<string>? includeStates = null, int maxResults = 100)
    {
        if (threshold < 0f || threshold > 1f)
            throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be between 0 and 1.");

        includeStates ??= new HashSet<string> { "stm", "ltm" };

        List<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates;
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterReadLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is null)
                return Array.Empty<(string, string, float)>();

            candidates = nsEntries.Values
                .Where(t => includeStates.Contains(t.Entry.LifecycleState)
                    && (category is null || t.Entry.Category == category)
                    && t.Norm > 0f)
                .ToList();
        }
        finally { nsLock.ExitReadLock(); }

        // Sort by norm ascending for early-exit optimization
        candidates.Sort((a, b) => a.Norm.CompareTo(b.Norm));
        return _duplicateDetector.FindDuplicates(candidates, threshold, maxResults);
    }

    // ── Access Tracking ──

    /// <summary>Record an access (increments count and updates timestamp). Resolves id→ns lock-free, then per-ns write.</summary>
    public void RecordAccess(string id)
    {
        if (!_store.TryResolveOrLoad(id, out var ns))
            return;

        var nsLock = NsLock(ns);
        nsLock.EnterWriteLock();
        try
        {
            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is not null && nsEntries.TryGetValue(id, out var tuple))
            {
                tuple.Entry.AccessCount++;
                tuple.Entry.LastAccessedAt = DateTimeOffset.UtcNow;
                _store.ScheduleEntryUpsert(ns, tuple.Entry);
            }
        }
        finally { nsLock.ExitWriteLock(); }
    }

    /// <summary>Record an access hit within a known namespace. Per-ns write lock.</summary>
    public void RecordAccess(string id, string ns)
        => RecordAccess(id, ns, string.Empty);

    /// <summary>Record an access hit within an exact tenant + namespace partition.</summary>
    public void RecordAccess(string id, string ns, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is not null && nsEntries.TryGetValue(id, out var tuple))
            {
                tuple.Entry.AccessCount++;
                tuple.Entry.LastAccessedAt = DateTimeOffset.UtcNow;
                _store.ScheduleEntryUpsert(ns, tuple.Entry);
            }
        }
        finally { nsLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Boost activation energy for an entry (spreading activation). Per-ns write on the provided ns;
    /// falls back to id→ns resolve if not found there. Returns true if entry was found and updated.
    /// </summary>
    public bool BoostActivationEnergy(string id, string ns, float delta)
        => BoostActivationEnergy(id, ns, delta, string.Empty, allowNamespaceFallback: true);

    /// <summary>Boost activation energy within an exact tenant + namespace partition.</summary>
    public bool BoostActivationEnergy(string id, string ns, float delta, string tenantId)
        => BoostActivationEnergy(id, ns, delta, tenantId, allowNamespaceFallback: false);

    private bool BoostActivationEnergy(
        string id, string ns, float delta, string tenantId, bool allowNamespaceFallback)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        // Fast path: try the caller's supplied ns first
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is not null && nsEntries.TryGetValue(id, out var tuple))
            {
                tuple.Entry.ActivationEnergy += delta;
                _store.ScheduleEntryUpsert(ns, tuple.Entry);
                return true;
            }
        }
        finally { nsLock.ExitWriteLock(); }

        if (!allowNamespaceFallback)
            return false;

        // Fallback: resolve id→ns lock-free, then take the resolved ns's write lock
        if (!_store.TryResolveOrLoad(id, out var resolvedNs) || resolvedNs == ns)
            return false;

        var resolvedLock = NsLock(resolvedNs);
        resolvedLock.EnterWriteLock();
        try
        {
            var resolvedEntries = _store.GetNamespace(resolvedNs);
            if (resolvedEntries is not null && resolvedEntries.TryGetValue(id, out var resolvedTuple))
            {
                resolvedTuple.Entry.ActivationEnergy += delta;
                _store.ScheduleEntryUpsert(resolvedNs, resolvedTuple.Entry);
                return true;
            }
            return false;
        }
        finally { resolvedLock.ExitWriteLock(); }
    }

    // ── Lifecycle State Management ──

    /// <summary>Update an entry's lifecycle state. Resolves id→ns lock-free, then per-ns write. Quantizes on STM→LTM, dequantizes on →STM.</summary>
    public bool SetLifecycleState(string id, string state)
    {
        if (!_store.TryResolveOrLoad(id, out var ns))
            return false;

        var nsLock = NsLock(ns);
        nsLock.EnterWriteLock();
        try
        {
            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is not null && nsEntries.TryGetValue(id, out var tuple))
            {
                var previousState = tuple.Entry.LifecycleState;
                tuple.Entry.LifecycleState = state;
                StampLifecycleTransition(tuple.Entry, previousState);
                UpdateQuantization(nsEntries, id, tuple, previousState, state);
                _store.ScheduleEntryUpsert(ns, tuple.Entry);
                return true;
            }
            return false;
        }
        finally { nsLock.ExitWriteLock(); }
    }

    /// <summary>Update lifecycle state within an exact tenant + namespace partition.</summary>
    public bool SetLifecycleState(string id, string state, string ns, string tenantId)
        => SetLifecycleState(id, state, ns, tenantId, out _);

    /// <summary>
    /// As <see cref="SetLifecycleState(string, string, string, string)"/>, additionally reporting
    /// the <see cref="CognitiveEntry.LifecycleRevision"/> that THIS CALL'S transition produced —
    /// and 0 when the call performed no transition at all (the state was already held), decided
    /// under the same partition write lock. The zero matters as much as the value: a caller
    /// building a reversal receipt claims ownership of a transition, and a set that changed
    /// nothing — including one racing another actor who archived the entry first — performed no
    /// transition to own. Reporting the current revision there would hand the caller somebody
    /// else's witness.
    /// </summary>
    public bool SetLifecycleState(string id, string state, string ns, string tenantId, out long lifecycleRevision)
    {
        lifecycleRevision = 0;
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is null || !nsEntries.TryGetValue(id, out var tuple))
                return false;

            var previousState = tuple.Entry.LifecycleState;
            tuple.Entry.LifecycleState = state;
            StampLifecycleTransition(tuple.Entry, previousState);
            UpdateQuantization(nsEntries, id, tuple, previousState, state);
            _store.ScheduleEntryUpsert(ns, tuple.Entry);
            if (!string.Equals(previousState, state, StringComparison.Ordinal))
                lifecycleRevision = tuple.Entry.LifecycleRevision;
            return true;
        }
        finally { nsLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Update lifecycle state for multiple entries. Groups by resolved namespace so each ns is
    /// locked once for its sub-batch; entries in different namespaces run under different locks.
    /// </summary>
    public int SetLifecycleStateBatch(IEnumerable<string> ids, string state)
    {
        // Resolve all ids first (lock-free), then group by resolved ns so we take each ns's
        // write lock exactly once.
        var byNs = new Dictionary<string, List<string>>();
        foreach (var id in ids)
        {
            if (!_store.TryResolveOrLoad(id, out var ns))
                continue;
            if (!byNs.TryGetValue(ns, out var list))
                byNs[ns] = list = new List<string>();
            list.Add(id);
        }

        int updated = 0;
        foreach (var (ns, idList) in byNs)
        {
            var nsLock = NsLock(ns);
            nsLock.EnterWriteLock();
            try
            {
                var nsEntries = _store.GetNamespace(ns);
                if (nsEntries is null) continue;

                foreach (var id in idList)
                {
                    if (nsEntries.TryGetValue(id, out var tuple))
                    {
                        var previousState = tuple.Entry.LifecycleState;
                        tuple.Entry.LifecycleState = state;
                        StampLifecycleTransition(tuple.Entry, previousState);
                        UpdateQuantization(nsEntries, id, tuple, previousState, state);
                        _store.ScheduleEntryUpsert(ns, tuple.Entry);
                        updated++;
                    }
                }
            }
            finally { nsLock.ExitWriteLock(); }
        }

        return updated;
    }

    /// <summary>Update lifecycle state for ids in an exact tenant + namespace partition.</summary>
    public int SetLifecycleStateBatch(
        IEnumerable<string> ids, string state, string ns, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is null)
                return 0;

            int updated = 0;
            foreach (var id in ids)
            {
                if (!nsEntries.TryGetValue(id, out var tuple))
                    continue;

                var previousState = tuple.Entry.LifecycleState;
                tuple.Entry.LifecycleState = state;
                StampLifecycleTransition(tuple.Entry, previousState);
                UpdateQuantization(nsEntries, id, tuple, previousState, state);
                _store.ScheduleEntryUpsert(ns, tuple.Entry);
                updated++;
            }
            return updated;
        }
        finally { nsLock.ExitWriteLock(); }
    }

    /// <summary>Update activation energy and lifecycle state atomically. Resolves id→ns lock-free, then per-ns write.</summary>
    public bool SetActivationEnergyAndState(string id, float activationEnergy, string? newState = null)
    {
        if (!_store.TryResolveOrLoad(id, out var ns))
            return false;

        var nsLock = NsLock(ns);
        nsLock.EnterWriteLock();
        try
        {
            var nsEntries = _store.GetNamespace(ns);
            if (nsEntries is not null && nsEntries.TryGetValue(id, out var tuple))
            {
                var previousState = tuple.Entry.LifecycleState;
                tuple.Entry.ActivationEnergy = activationEnergy;
                if (newState is not null)
                {
                    tuple.Entry.LifecycleState = newState;
                    StampLifecycleTransition(tuple.Entry, previousState);
                    UpdateQuantization(nsEntries, id, tuple, previousState, newState);
                }
                _store.ScheduleEntryUpsert(ns, tuple.Entry);
                return true;
            }
            return false;
        }
        finally { nsLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Update activation energy and optional lifecycle state within an exact tenant + namespace
    /// partition.
    /// </summary>
    public bool SetActivationEnergyAndState(
        string id, float activationEnergy, string? newState, string ns, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is null || !nsEntries.TryGetValue(id, out var tuple))
                return false;

            var previousState = tuple.Entry.LifecycleState;
            tuple.Entry.ActivationEnergy = activationEnergy;
            if (newState is not null)
            {
                tuple.Entry.LifecycleState = newState;
                StampLifecycleTransition(tuple.Entry, previousState);
                UpdateQuantization(nsEntries, id, tuple, previousState, newState);
            }
            _store.ScheduleEntryUpsert(ns, tuple.Entry);
            return true;
        }
        finally { nsLock.ExitWriteLock(); }
    }

    // ── Bulk Reads ──

    /// <summary>Get all entries in a namespace. Per-namespace read lock.</summary>
    public IReadOnlyList<CognitiveEntry> GetAllInNamespace(string ns)
        => GetAllInNamespace(ns, string.Empty);

    /// <summary>Get all entries in a tenant + namespace partition. Per-partition read lock.</summary>
    public IReadOnlyList<CognitiveEntry> GetAllInNamespace(string ns, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        nsLock.EnterReadLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is null)
                return Array.Empty<CognitiveEntry>();
            return nsEntries.Values.Select(t => t.Entry).ToList();
        }
        finally { nsLock.ExitReadLock(); }
    }

    /// <summary>Delete all entries in a namespace and remove it from in-memory state. Does NOT cascade to graph edges or clusters — callers must handle that.</summary>
    /// <summary>
    /// Remove a namespace partition ONLY IF it holds no entries — the emptiness check and the
    /// removal run under the same partition write lock, so a concurrent upsert cannot land
    /// between a caller's "it is empty" observation and a wholesale delete that would take the
    /// fresh entry down with it. Returns false, touching nothing, when entries are present.
    /// </summary>
    public bool DeleteAllInNamespaceIfEmpty(string ns, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        bool namespaceRemoved = false;
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is not null && nsEntries.Count > 0)
                return false;
            _store.RemoveNamespace(key);
            namespaceRemoved = true;
        }
        finally
        {
            nsLock.ExitWriteLock();

            // Outside the partition lock, for the same lock-ordering reason DeleteAllInNamespace
            // states on its own event dispatch.
            if (namespaceRemoved)
                NamespaceRemoved?.Invoke(key);
        }
        return true;
    }

    public int DeleteAllInNamespace(string ns)
        => DeleteAllInNamespace(ns, string.Empty);

    /// <summary>
    /// Delete every entry in exactly one tenant + namespace partition. Other tenants with the
    /// same namespace or ids are untouched. Does NOT cascade to graph edges or clusters.
    /// </summary>
    public int DeleteAllInNamespace(string ns, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        var nsLock = NsLock(NamespaceStore.PartitionKey(key));
        int removed;
        bool namespaceRemoved = false;
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is null || nsEntries.Count == 0)
            {
                _store.RemoveNamespace(key);
                namespaceRemoved = true;
                removed = 0;
            }
            else
            {
                var ids = nsEntries.Keys.ToList();
                // Remove first so snapshot-based providers observe the post-delete state when their
                // save is scheduled. This clears only the selected partition's BM25/HNSW indexes.
                _store.RemoveNamespace(key);
                namespaceRemoved = true;
                foreach (var id in ids)
                    _store.ScheduleEntryDelete(ns, id, key.Tenant);
                removed = ids.Count;
                BumpOccupancy(key.Tenant, ns);
            }
        }
        finally
        {
            nsLock.ExitWriteLock();

            // Outside the partition lock so a maintenance subscriber can never invert index
            // locking. Kept in the finally so a provider failure while scheduling individual row
            // deletes cannot leave derived state naming a partition already removed from memory.
            // Internal subscribers are synchronous and must remain O(1).
            if (namespaceRemoved)
                NamespaceRemoved?.Invoke(key);
        }
        return removed;
    }

    /// <summary>
    /// Get all entries across all namespaces. Lock-free: snapshots ConcurrentDictionary entries.
    /// Diagnostic-grade consistency — may observe in-flight writes to any single ns but never
    /// returns a torn entry record.
    /// </summary>
    public IReadOnlyList<CognitiveEntry> GetAll()
        => GetAllForTenant(string.Empty);

    /// <summary>Get all entries belonging to exactly one tenant.</summary>
    public IReadOnlyList<CognitiveEntry> GetAllForTenant(string tenantId)
    {
        _store.LoadAll();
        return _store.GetTenantNamespaces(Tenancy.Normalize(tenantId))
            .SelectMany(d => d.Values.Select(t => t.Entry))
            .ToList();
    }

    /// <summary>
    /// Explicit system-level diagnostic snapshot across every tenant. Principal-bound tools and
    /// maintenance paths must use <see cref="GetAllForTenant"/> instead.
    /// </summary>
    public IReadOnlyList<CognitiveEntry> GetAllAcrossTenants()
    {
        _store.LoadAll();
        return _store.AllNamespaces
            .SelectMany(d => d.Values.Select(t => t.Entry))
            .ToList();
    }

    /// <summary>
    /// Get count of entries by lifecycle state. Single-ns path takes that ns's read lock;
    /// cross-ns path is lock-free (diagnostic-grade consistency via ConcurrentDictionary).
    /// </summary>
    public (int stm, int ltm, int archived) GetStateCounts(string? ns = null)
        => GetStateCounts(ns, string.Empty);

    /// <summary>
    /// Get lifecycle-state counts within one tenant. A null or "*" namespace aggregates only
    /// that tenant's partitions; a concrete namespace reads exactly that tenant + namespace.
    /// </summary>
    public (int stm, int ltm, int archived) GetStateCounts(string? ns, string tenantId)
    {
        string tenant = Tenancy.Normalize(tenantId);
        if (ns is null || ns == "*")
        {
            _store.LoadAll();
            var entries = _store.GetTenantNamespaces(tenant)
                .SelectMany(d => d.Values.Select(t => t.Entry));
            return CountStates(entries);
        }
        else
        {
            var key = new NsKey(tenant, ns);
            var nsLock = NsLock(NamespaceStore.PartitionKey(key));
            nsLock.EnterReadLock();
            try
            {
                _store.EnsureLoaded(ns);
                var entries = _store.GetNamespace(key) is { } nsEntries
                    ? nsEntries.Values.Select(t => t.Entry)
                    : Enumerable.Empty<CognitiveEntry>();
                return CountStates(entries);
            }
            finally { nsLock.ExitReadLock(); }
        }

        static (int stm, int ltm, int archived) CountStates(IEnumerable<CognitiveEntry> entries)
        {
            int stm = 0, ltm = 0, archived = 0;
            foreach (var e in entries)
            {
                switch (e.LifecycleState)
                {
                    case "stm": stm++; break;
                    case "ltm": ltm++; break;
                    case "archived": archived++; break;
                }
            }
            return (stm, ltm, archived);
        }
    }

    /// <summary>Re-embed all entries in a namespace. Per-namespace write lock. Pass tenantId "" for the legacy partition.</summary>
    public (int Updated, int Skipped) RebuildEmbeddings(string ns, IEmbeddingService embedding, string tenantId)
    {
        var key = new NsKey(Tenancy.Normalize(tenantId), ns);
        string pk = NamespaceStore.PartitionKey(key);
        var nsLock = NsLock(pk);
        nsLock.EnterWriteLock();
        try
        {
            _store.EnsureLoaded(ns);
            var nsEntries = _store.GetNamespace(key);
            if (nsEntries is null)
                return (0, 0);

            int updated = 0, skipped = 0;
            var ids = nsEntries.Keys.ToList();

            foreach (var id in ids)
            {
                var (oldEntry, _, _) = nsEntries[id];
                if (string.IsNullOrWhiteSpace(oldEntry.Text))
                {
                    skipped++;
                    continue;
                }

                float[] newVector = embedding.Embed(oldEntry.Text);
                var newEntry = new CognitiveEntry(
                    oldEntry.Id, newVector, oldEntry.Ns, oldEntry.Text,
                    oldEntry.Category, oldEntry.Metadata, oldEntry.LifecycleState,
                    oldEntry.CreatedAt, oldEntry.LastAccessedAt, oldEntry.AccessCount,
                    oldEntry.ActivationEnergy, oldEntry.IsSummaryNode, oldEntry.SourceClusterId,
                    oldEntry.Keywords, oldEntry.TenantId)
                {
                    // Summary OWNERSHIP survives the rebuild: a re-embedded summary that lost
                    // its stamp/instance would fail every ownership screen (served as no
                    // summary) and stop matching its record's conditioned cleanup — the
                    // rebuild changes the vector, never whose summary this is.
                    SourceClusterStamp = oldEntry.SourceClusterStamp,
                    SourceClusterInstance = oldEntry.SourceClusterInstance
                };

                // A rebuild is a re-OCCUPATION of the slot, and the witnesses must say so
                // rather than reset to zero: a fresh Revision (so a delete staged against the
                // pre-rebuild occupation refuses), the PRESERVED LifecycleRevision (no
                // lifecycle transition happened, so reversal receipts pointing at the current
                // state must keep matching), and an occupancy bump below.
                newEntry.Revision = Interlocked.Increment(ref _entryRevisionCounter);
                newEntry.LifecycleRevision = oldEntry.LifecycleRevision;

                var quantized = newEntry.LifecycleState is "ltm" or "archived"
                    ? VectorQuantizer.Quantize(newVector)
                    : null;
                nsEntries[id] = (newEntry, VectorMath.Norm(newVector), quantized);
                _store.IndexBM25(newEntry);
                updated++;
            }

            if (updated > 0)
            {
                BumpOccupancy(key.Tenant, ns);
                _store.ScheduleSave(ns);
                // Invalidate the stale HNSW index so it is rebuilt lazily on the next search.
                // The old topology references pre-re-embedding vectors and would return wrong candidates.
                _store.InvalidateHnswIndex(pk);
            }

            return (updated, skipped);
        }
        finally { nsLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Release per-namespace locks. Per the standard <see cref="IDisposable"/> contract,
    /// the caller is responsible for ensuring no in-flight operations are still
    /// executing on this index when Dispose is called — a thread holding a per-ns
    /// lock during Dispose would cause <see cref="ReaderWriterLockSlim.Dispose"/> to
    /// throw <see cref="SynchronizationLockException"/>. Dispose is safe against
    /// concurrent Dispose callers (once-and-only-once) and against racing NsLock
    /// callers (they throw <see cref="ObjectDisposedException"/> before touching a
    /// torn-down lock).
    /// </summary>
    public void Dispose()
    {
        // Atomic once-and-only-once transition via Interlocked.Exchange. Concurrent
        // Dispose callers never both reach the teardown — only the first to flip 0→1
        // proceeds; the rest see a non-zero prior value and return immediately.
        // The flag flips BEFORE teardown so any racing NsLock(ns) call sees it and
        // throws ObjectDisposedException up front.
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;

        // Per-lock best-effort. ReaderWriterLockSlim.Dispose throws if a lock is still held or has
        // waiters, and the hosted server can genuinely reach here in that state: the maintenance
        // passes take no CancellationToken, so a pass that outlives the host's shutdown timeout
        // keeps its namespace lock while the container tears the object graph down.
        //
        // Letting that escape is what costs something. ServiceProvider disposes singletons in
        // reverse creation order and does not catch, so a throw here abandons every disposable
        // created earlier — including PersistenceManager, whose Dispose is the Flush that writes
        // pending debounced entries. An exception at this point silently drops memories.
        //
        // Weighed against that, the lock object is not worth defending: skipping its Dispose leaks
        // internal handles the process is about to release anyway. Losing the flush loses data.
        int skipped = 0;
        foreach (var kv in _nsLocks)
        {
            try { kv.Value.Dispose(); }
            catch (SynchronizationLockException) { skipped++; }
        }
        _nsLocks.Clear();
        DisposalContendedLockCount = skipped;

        // The per-tenant attribution fences, on the same best-effort terms and for the same reason:
        // a fence still held by a maintenance pass that outlived the shutdown timeout throws, and
        // an escape here abandons the persistence flush. Deliberately NOT folded into
        // DisposalContendedLockCount — that figure names contended NAMESPACE locks, which is what
        // the disposal tests assert on, and quietly widening it would change what they measure.
        //
        // The count is KEPT rather than discarded. A discarded return value is an unobservable
        // outcome, and "a fence was held when the index was torn down" is exactly the state whose
        // handling has to be pinned by a test: the holder must still be able to release, through
        // the reference it captured, against a fence this walk left published.
        DisposalContendedFenceCount = _store.DisposeAttributionFences();
    }

    /// <summary>
    /// How many per-namespace locks were still in use when <see cref="Dispose"/> ran, and so were
    /// left undisposed. Non-zero means shutdown raced an in-flight operation — the disposal itself
    /// still completed. Exposed rather than logged because this type takes no logger; a host that
    /// cares can read it after disposing, and the disposal tests assert on it.
    /// </summary>
    public int DisposalContendedLockCount { get; private set; }

    /// <summary>
    /// How many per-tenant attribution fences were still held when <see cref="Dispose"/> ran, and
    /// so were left both undisposed and PUBLISHED. Separate from
    /// <see cref="DisposalContendedLockCount"/>, which counts namespace locks: the two answer
    /// different questions and the disposal tests assert on them separately.
    ///
    /// Non-zero means a topology mutator was mid-flight at teardown. That is survivable by design —
    /// the mutator releases through the reference it captured, so the fence it holds must still be
    /// the instance it entered, which is why a contended fence is never unpublished.
    /// </summary>
    public int DisposalContendedFenceCount { get; private set; }

    // ── Internal Helpers ──

    /// <summary>Apply a small score boost when query terms overlap with entry categories.</summary>
    private static IReadOnlyList<CognitiveSearchResult> ApplyCategoryBoost(
        IReadOnlyList<CognitiveSearchResult> results, string? queryText)
    {
        if (queryText is null || results.Count == 0)
            return results;

        var queryTokens = BM25Index.Tokenize(queryText).ToHashSet();
        if (queryTokens.Count == 0) return results;

        var boosted = new List<CognitiveSearchResult>(results.Count);
        foreach (var r in results)
        {
            float boost = 1f;
            if (r.Category is not null)
            {
                var catTokens = BM25Index.Tokenize(r.Category);
                foreach (var ct in catTokens)
                {
                    if (queryTokens.Contains(ct))
                    {
                        boost = 1.15f; // 15% category match boost
                        break;
                    }
                }
            }

            boosted.Add(new CognitiveSearchResult(
                r.Id, r.Text, r.Score * boost,
                r.LifecycleState, r.ActivationEnergy,
                r.Category, r.Metadata,
                r.IsSummaryNode, r.SourceClusterId, r.AccessCount));
        }

        boosted.Sort((a, b) => b.Score.CompareTo(a.Score));
        return boosted;
    }

    /// <summary>Apply cluster-aware MMR diversity reranking when requested.</summary>
    private static IReadOnlyList<CognitiveSearchResult> ApplyDiversity(
        IReadOnlyList<CognitiveSearchResult> results,
        SearchRequest request,
        IReadOnlyCollection<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> snapshot)
    {
        if (!request.Diversity || results.Count <= 1)
            return results.Count > request.K ? results.Take(request.K).ToList() : results;

        // Build vector lookup from snapshot for MMR inter-result similarity
        var vectorLookup = new Dictionary<string, float[]>(snapshot.Count);
        foreach (var (entry, _, _) in snapshot)
            vectorLookup[entry.Id] = entry.Vector;

        return DiversityReranker.Rerank(
            results, request.Query, id => vectorLookup.GetValueOrDefault(id),
            request.K, request.DiversityLambda);
    }

    private static void UpdateQuantization(
        ConcurrentDictionary<string, (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> entries,
        string id,
        (CognitiveEntry Entry, float Norm, QuantizedVector? Quantized) tuple,
        string previousState, string newState)
    {
        bool wasQuantizable = previousState is "ltm" or "archived";
        bool isQuantizable = newState is "ltm" or "archived";

        if (!wasQuantizable && isQuantizable && tuple.Quantized is null)
            entries[id] = (tuple.Entry, tuple.Norm, VectorQuantizer.Quantize(tuple.Entry.Vector));
        else if (wasQuantizable && !isQuantizable && tuple.Quantized is not null)
            entries[id] = (tuple.Entry, tuple.Norm, null);
    }

}
