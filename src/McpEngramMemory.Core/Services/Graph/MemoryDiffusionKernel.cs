using System.Collections.Concurrent;
using McpEngramMemory.Core.Models;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Graph;

/// <summary>
/// Per-namespace cache and operator for diffusing per-entry signals through the
/// memory graph. Internally holds the top-K eigenbasis of the normalized
/// Laplacian; externally exposes <see cref="ApplySpectralFilter"/> as the
/// primary verb. Consumed today by spectral diffusion of decay debt
/// (LifecycleEngine) and reserved for future spectral retrieval and
/// sleep-consolidation operators.
///
/// What "diffusion" means here. Each memory entry has a position on the graph
/// (its node) and a scalar associated with it (decay debt, activation pressure,
/// retrieval relevance — whatever the caller needs to spread). Applying the
/// heat-kernel filter <c>exp(-tL)</c> to this scalar field moves it from each
/// node to its neighbors weighted by edge connectivity, exactly the way
/// activation spreads in classical cognitive-science models of associative memory.
///
/// Construction. For namespace <c>ns</c>:
/// 1. Snapshot entry ids in stable order from <see cref="CognitiveIndex.GetAllInNamespace(string)"/>.
/// 2. Snapshot the ATTRIBUTABLE edge list from <see cref="KnowledgeGraph.GetAllEdges(string)"/>,
///    filter to edges whose endpoints are both in <c>ns</c> and whose relation is in
///    <see cref="PositiveRelations"/> (parent_child, cross_reference, similar_to,
///    elaborates, depends_on). The <c>contradicts</c> relation is excluded so the
///    weight matrix W stays non-negative and the Laplacian L = I - D^(-1/2) W D^(-1/2)
///    stays positive semi-definite (heat kernel exp(-tL) remains a contraction).
/// 3. Symmetrize: W[i,j] = max(w(i->j), w(j->i)).
/// 4. Structurally deflate isolated nodes: entries with no positive-relation
///    edges are excluded from the eigenproblem entirely (they would contribute
///    exactly-zero rows/columns to M, making it exactly rank-deficient — the
///    regime where randomized subspace iteration breaks down). Each isolated
///    node is its own connected component with Laplacian eigenvalue 0, so every
///    spectral filter treats it as identity (exp(-t·0) = 1); the filter's
///    out-of-basis pass-through in <see cref="ApplySpectralFilter"/> implements
///    exactly that. If the remaining linked core is smaller than
///    <see cref="MinimumNodesForSpectral"/>, the kernel bypasses entirely.
/// 5. Find the top-K largest eigenpairs (lambda_M, u) of M = D^(-1/2) W D^(-1/2)
///    over the linked subgraph via <see cref="RandomizedEigensolver.SolveTopK"/>;
///    convert to the smallest eigenpairs of L via lambda_L = 1 - lambda_M and
///    sort ascending.
///
/// Cache invalidation is revision-based on TWO revisions, and one of them is not
/// about edges at all. <see cref="KnowledgeGraph.RevisionFor"/> says which edges
/// exist; <see cref="CognitiveIndex.AttributionRevisionFor"/> says which of them are
/// usable. A same-id twin inserted into another namespace of the tenant writes no
/// edge, so the graph revision does not move — while every edge naming that id drops
/// out of the attributable view. Watching only the graph revision therefore kept
/// serving a basis whose edges the view no longer returns, which is the worst
/// direction to be stale in: the retained copy is the one still holding somebody
/// else's topology. Each cached basis records both revisions and
/// <see cref="GetBasis"/> recomputes when either diverges. Recomputation runs
/// synchronously under a per-namespace lock — concurrent calls for the same namespace
/// serialize, but different namespaces compute independently.
/// </summary>
public class MemoryDiffusionKernel
{
    /// <summary>Edge relations that contribute positive weight to the Laplacian.</summary>
    public static readonly IReadOnlySet<string> PositiveRelations = new HashSet<string>
    {
        "parent_child", "cross_reference", "similar_to", "elaborates", "depends_on",
    };

    /// <summary>Default top-K. 96 covers the dominant low-frequency modes for typical namespaces.</summary>
    public const int DefaultTopK = 96;

    /// <summary>Below this node count, spectral methods give no benefit and the kernel is bypassed.</summary>
    public const int MinimumNodesForSpectral = 32;

    /// <summary>Minimum positive-relation edges required to construct a meaningful basis.</summary>
    public const int MinimumEdgesForSpectral = 8;

    /// <summary>Random sketch oversample (Halko-Martinsson-Tropp typical value).</summary>
    private const int Oversample = 10;

    /// <summary>Power iterations to align the sketch with the dominant subspace.</summary>
    private const int PowerIterations = 5;

    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly ILogger<MemoryDiffusionKernel>? _logger;

    /// <summary>
    /// A cached basis together with the attribution revision it was computed under.
    ///
    /// Carried here rather than on <see cref="DiffusionBasis"/> because the basis's own
    /// <see cref="DiffusionBasis.GraphRevision"/> answers only half the freshness question —
    /// which edges existed — and the half it omits is the one an entry write can change
    /// without touching the graph at all.
    /// </summary>
    private readonly record struct CachedBasis(DiffusionBasis Basis, long AttributionRevision);

    private readonly ConcurrentDictionary<string, CachedBasis> _cache = new();
    private readonly ConcurrentDictionary<string, object> _nsLocks = new();

    /// <summary>
    /// The rotation <see cref="ReconcileOneCachedPartition"/> walks, one partition per
    /// <see cref="GetBasis"/> call, and the gate that keeps two concurrent calls from stepping it at
    /// once. Null between passes.
    /// </summary>
    private readonly object _reconcileGate = new();
    private IEnumerator<KeyValuePair<string, CachedBasis>>? _reconcileWalk;

    /// <summary>
    /// Negative cache: namespaces whose basis computation threw, keyed by the graph AND attribution
    /// revisions at failure time. The eigensolver RNG is seeded from
    /// <c>graphRevision ^ ns.GetHashCode()</c>, so a failure is deterministic per
    /// (namespace, revision) — re-running the expensive eigensolve before the inputs change would
    /// repay the full cost for a guaranteed-identical failure. Instead <see cref="GetBasis"/>
    /// rethrows a cheap exception until one of the revisions moves, which re-arms exactly one
    /// retry. Attribution is in the key for the same reason it is in the positive cache: a twin
    /// insert changes which edges the computation is handed while leaving the graph revision — and
    /// so the RNG seed and the determinism argument — exactly where they were.
    /// </summary>
    private readonly ConcurrentDictionary<string, (long Revision, long AttributionRevision, string Message)> _failedRevisions = new();

    public MemoryDiffusionKernel(
        CognitiveIndex index,
        KnowledgeGraph graph,
        ILogger<MemoryDiffusionKernel>? logger = null)
    {
        _index = index;
        _graph = graph;
        _logger = logger;
    }

    /// <summary>
    /// Return the top-K diffusion basis for <paramref name="ns"/>, recomputing if
    /// the cache is missing, stale (either the graph revision or the attribution
    /// revision diverged), or has fewer eigenpairs than requested. Returns
    /// <c>null</c> if the namespace is too small or sparsely linked to qualify (see
    /// <see cref="MinimumNodesForSpectral"/> and <see cref="MinimumEdgesForSpectral"/>)
    /// — callers should fall back to non-spectral behavior in that case.
    ///
    /// A namespace whose ids the tenant also holds elsewhere reaches that same
    /// <c>null</c>, because none of its edges are attributable and the basis is built
    /// from the attributable view alone. Deliberate, and deliberately spelled the same
    /// way as too-small: a distinct answer would tell the caller that a twin exists in
    /// a namespace it was never shown.
    ///
    /// Failure handling: if computation throws, the failure is negative-cached
    /// against both revisions and cheaply rethrown until one of them moves, keeping
    /// the failure visible to callers every cycle without repaying the
    /// eigensolve for a deterministic re-failure. Rethrowing (rather than
    /// returning <c>null</c>) is deliberate — <c>null</c> would be
    /// indistinguishable from a legitimate too-small-namespace bypass.
    /// </summary>
    public DiffusionBasis? GetBasis(string ns, string tenantId, int topK = DefaultTopK)
        => GetCachedBasis(ns, tenantId, topK)?.Basis;

    /// <summary>
    /// <see cref="GetBasis"/> plus the attribution revision the returned basis was computed under,
    /// which only <see cref="GetStats"/> needs — a caller cannot recover it from
    /// <see cref="DiffusionBasis"/>, which records the graph revision alone.
    /// </summary>
    private CachedBasis? GetCachedBasis(string ns, string tenantId, int topK)
    {
        // NORMALIZED ONCE, HERE, and every use below is of this value. The key was composed from
        // the RAW argument while the two revision reads on the next lines normalized internally, so
        // a padded tenant cached a basis under one key and compared it against another tenant's
        // revisions — and Invalidate, which composed the key the same raw way, missed the copy the
        // warmup service and the search path were actually reading. The warmup service reaches this
        // through CognitiveIndex.GetAllTenants, which returns store tenants and is therefore always
        // the canonical spelling; a principal-supplied tenant need not be.
        tenantId = Tenancy.Normalize(tenantId);

        // Cache/lock/failure keys are the (tenant, ns) partition key so a tenant's basis never
        // collides with another's. For the legacy tenant "" the partition key is exactly ns, so
        // legacy cache keys are unchanged.
        string pk = NamespaceStore.PartitionKey(tenantId, ns);

        // Retraction first, and exactly one partition of it — see ReconcileOneCachedPartition.
        ReconcileOneCachedPartition();

        // Both revisions are read BEFORE the data they describe is snapshotted, and this ordering
        // is load-bearing rather than incidental. A mutation landing between the read and the
        // snapshot leaves a recorded revision that is already behind, so the next call recomputes —
        // wasteful and safe. Reading them after the snapshot inverts that: the basis would record a
        // revision that already covers a change it does not contain, and nothing would ever
        // recompute it.
        long currentRev = _graph.RevisionFor(tenantId);
        long currentAttribution = _index.AttributionRevisionFor(tenantId);

        if (_cache.TryGetValue(pk, out var cached)
            && IsFresh(cached, currentRev, currentAttribution)
            && (cached.Basis.TopK >= topK || cached.Basis.TopK >= cached.Basis.NodeCount))
        {
            // Either the cache has enough modes for the request, or it already
            // has the maximum possible (TopK was clamped to NodeCount). Either
            // way, no recomputation needed.
            return cached;
        }

        if (IsCachedFailure(pk, currentRev, currentAttribution, out var failed))
            throw new InvalidOperationException(FailureMessage(ns, currentRev, failed.Message));

        var nsLock = _nsLocks.GetOrAdd(pk, _ => new object());
        lock (nsLock)
        {
            currentRev = _graph.RevisionFor(tenantId);
            currentAttribution = _index.AttributionRevisionFor(tenantId);
            if (_cache.TryGetValue(pk, out cached)
                && IsFresh(cached, currentRev, currentAttribution)
                && cached.Basis.TopK >= topK)
            {
                return cached;
            }

            if (IsCachedFailure(pk, currentRev, currentAttribution, out failed))
                throw new InvalidOperationException(FailureMessage(ns, currentRev, failed.Message));

            DiffusionBasis? built;
            try
            {
                built = ComputeBasis(ns, topK, currentRev, tenantId: tenantId);
            }
            catch (Exception ex)
            {
                _failedRevisions[pk] = (currentRev, currentAttribution, ex.Message);
                _logger?.LogWarning(ex,
                    "Diffusion basis computation failed for ns={Namespace} at graph revision {Revision} / attribution revision {AttributionRevision}; caching failure until one of them changes.",
                    ns, currentRev, currentAttribution);
                throw;
            }

            _failedRevisions.TryRemove(pk, out _);
            if (built is not null)
            {
                var entry = new CachedBasis(built, currentAttribution);
                _cache[pk] = entry;
                return entry;
            }

            _cache.TryRemove(pk, out _);
            return null;
        }
    }

    /// <summary>
    /// A cached basis is usable only while BOTH of its inputs still hold: the same edges exist, and
    /// the same ones are attributable. Either revision moving means the attributable edge view can
    /// differ from the one the basis was built on, and a basis is not inspectable for which of its
    /// edges came from where — so divergence is a rebuild, never a partial repair.
    /// </summary>
    private static bool IsFresh(CachedBasis cached, long graphRevision, long attributionRevision)
        => cached.Basis.GraphRevision == graphRevision
           && cached.AttributionRevision == attributionRevision;

    private bool IsCachedFailure(
        string pk, long graphRevision, long attributionRevision,
        out (long Revision, long AttributionRevision, string Message) failed)
        => _failedRevisions.TryGetValue(pk, out failed)
           && failed.Revision == graphRevision
           && failed.AttributionRevision == attributionRevision;

    // "its inputs" rather than naming the attribution revision: this string reaches a principal, and
    // the two revisions are now what re-arms a retry, so the wording has to cover both without
    // introducing a term whose only meaning is "somebody else holds this id too".
    private static string FailureMessage(string ns, long rev, string inner) =>
        $"Diffusion basis computation for namespace '{ns}' previously failed at graph revision {rev} and its inputs have not changed since: {inner}";

    /// <summary>
    /// Apply a per-mode spectral filter to a per-entry signal — the kernel's
    /// primary verb. Entries not present in the cached basis pass through
    /// unchanged (e.g., isolated entries deflated out of the eigenproblem — for
    /// which identity is the exact filter value, since a singleton component has
    /// lambda_L = 0 — or entries added after basis computation, on a
    /// stale-but-not-yet-rebuilt basis).
    ///
    /// Mechanism: project signal into the basis (sigma_hat[k] = sum_i U[i,k] · signal[i]),
    /// apply <paramref name="modeFilter"/> to each mode, project back. For diffusion of
    /// decay debt with subdiffusive exponent alpha and step dt, pass:
    /// <c>lambda =&gt; MathF.Exp(-MathF.Pow(lambda, alpha) * dt)</c>.
    /// </summary>
    public IReadOnlyDictionary<string, float> ApplySpectralFilter(
        string ns,
        IReadOnlyDictionary<string, float> signal,
        Func<float, float> modeFilter,
        string tenantId)
    {
        var basis = GetBasis(ns, tenantId: tenantId, topK: DefaultTopK);
        if (basis is null) return signal;

        int n = basis.NodeCount;
        int k = basis.TopK;
        var U = basis.Eigenvectors;

        // Project: sigHat[j] = sum_i U[i,j] * signal[entryIds[i]]
        var sigHat = new float[k];
        for (int i = 0; i < n; i++)
        {
            if (!signal.TryGetValue(basis.EntryIds[i], out var v) || v == 0f) continue;
            for (int j = 0; j < k; j++)
                sigHat[j] += U[i, j] * v;
        }

        // Filter in spectral space.
        for (int j = 0; j < k; j++)
            sigHat[j] *= modeFilter(basis.Eigenvalues[j]);

        // Project back: out[i] = sum_j U[i,j] * sigHat[j].
        var result = new Dictionary<string, float>(signal.Count);
        foreach (var kv in signal) result[kv.Key] = kv.Value; // pass-through for ids outside the basis
        for (int i = 0; i < n; i++)
        {
            float s = 0f;
            for (int j = 0; j < k; j++) s += U[i, j] * sigHat[j];
            result[basis.EntryIds[i]] = s;
        }
        return result;
    }

    /// <summary>
    /// Diagnostics view of the cached basis (or a freshly-computed one) for <paramref name="ns"/>.
    ///
    /// <c>Stale</c> reports the same predicate <see cref="GetBasis"/> acts on, and reporting only
    /// the graph half of it would have been a lie of exactly the kind this round is about: a basis
    /// left behind by a twin insert has an equal graph revision and is nonetheless unusable, so it
    /// would have shown as fresh.
    ///
    /// The attribution revision itself stays OUT of the returned record, deliberately. This reply
    /// reaches a principal, and a tenant-wide counter that ticks when someone mints a same-id twin
    /// is an oracle for exactly the fact the guard exists to withhold. One boolean derived from it
    /// is not: it moves for an ordinary edge write too.
    /// </summary>
    public DiffusionStats? GetStats(string ns, string tenantId)
    {
        // Normalized here as well as inside GetCachedBasis: the freshness compare below reads the
        // two revisions directly, and comparing a cached entry against another spelling's counters
        // reports staleness that is an artefact of the key rather than of the data.
        tenantId = Tenancy.Normalize(tenantId);

        if (GetCachedBasis(ns, tenantId, DefaultTopK) is not { } cached) return null;
        var basis = cached.Basis;
        bool stale = !IsFresh(cached, _graph.RevisionFor(tenantId), _index.AttributionRevisionFor(tenantId));
        return new DiffusionStats(
            ns,
            basis.NodeCount,
            basis.EdgeCount,
            basis.TopK,
            basis.Eigenvalues[0],
            basis.Eigenvalues[^1],
            basis.GraphRevision,
            basis.ComputedAt,
            stale);
    }

    /// <summary>
    /// Drop the cached basis (and any negative-cached failure) for a namespace. Next
    /// <see cref="GetBasis"/> will recompute.
    ///
    /// Normalizes the tenant, like every other entry point here: a forced invalidate composed from a
    /// padded spelling silently addressed a different cache slot from the one the warmup service and
    /// the search path read, so it appeared to work and cleared nothing.
    /// </summary>
    public void Invalidate(string ns, string tenantId)
    {
        Retract(NamespaceStore.PartitionKey(Tenancy.Normalize(tenantId), ns));
    }

    /// <summary>
    /// How many partitions the cache currently holds a basis or a cached failure for.
    ///
    /// The seam for the one property that is otherwise invisible: this dictionary must SHRINK when
    /// the namespaces behind it go away. A retained entry is a <see cref="DiffusionBasis"/> of
    /// NodeCount x TopK floats plus its eigenvalues — orders of magnitude larger than the int cursor
    /// that motivated the equivalent retraction in <c>AutoLinkScanner</c> — and nothing about it is
    /// visible in a result, in the graph or in a timing until the process is out of memory.
    /// </summary>
    internal int CachedPartitionCount => _cache.Count;

    /// <summary>
    /// Forget everything keyed to one partition: the basis, the negative-cached failure, and the
    /// per-partition compute lock.
    ///
    /// Dropping the lock is safe while another thread holds it. The lock exists only to keep two
    /// callers from eigensolving the same partition at once; a thread that arrives after the removal
    /// takes a fresh object and may compute concurrently with the holder, and both then write the
    /// same cache slot with a basis computed from the same revisions. Wasted work, never a wrong
    /// answer. Keeping the object instead would leave the one structure here that is never retracted.
    /// </summary>
    private void Retract(string partitionKey)
    {
        _cache.TryRemove(partitionKey, out _);
        _failedRevisions.TryRemove(partitionKey, out _);
        _nsLocks.TryRemove(partitionKey, out _);
    }

    /// <summary>
    /// Reconcile exactly ONE cached partition against the store, and never more than one.
    ///
    /// THE RETRACTION. Nothing tells a DI singleton that a namespace was torn down.
    /// <c>CognitiveIndex</c> raises no namespace-removal event — <c>EntryDeleted</c> fires per entry
    /// and not at all from <c>DeleteAllInNamespace</c>, which is the path <c>purge_debates</c> takes
    /// — and <see cref="Invalidate"/> is called from the diffusion tools and nowhere else, never
    /// from any teardown path. So a namespace that qualified once, was warmed by
    /// <c>DiffusionKernelWarmupService</c>, and was then deleted leaves its whole eigenbasis
    /// resident: the doesn't-qualify branch in <see cref="GetCachedBasis"/> can never reach it,
    /// because nothing ever asks for that partition's basis again. A host churning one debate
    /// namespace per conversation accumulates one basis per debate, monotonically, in a process
    /// designed to run for weeks.
    ///
    /// THE COST BOUND, which is why this is a rotation rather than a sweep. The warmup service calls
    /// <see cref="GetBasis"/> once per namespace per cycle, so anything done here that is linear in
    /// the number of cached partitions is quadratic per cycle. One partition per call, round-robin,
    /// gives "reconcile the whole cache once per warmup cycle" without this type having to know what
    /// a cycle is: there is at most one cache entry per (tenant, namespace), and a cycle steps the
    /// rotation once per namespace. Per call it is one enumerator step and one partition count — no
    /// listing, no allocation. This is the same shape, and the same argument, as
    /// <c>AutoLinkScanner.ReconcileOneResumeCursor</c>.
    ///
    /// The probe is <c>CountInNamespace</c>, the same predicate <see cref="ComputeBasis"/> applies:
    /// a partition below <see cref="MinimumNodesForSpectral"/> cannot produce a basis, so a cached
    /// one for it is dead state whether the namespace was deleted or merely shrank. Over-removal is
    /// harmless — the next request recomputes — which is what makes it safe to probe a key another
    /// thread is currently computing for.
    /// </summary>
    private void ReconcileOneCachedPartition()
    {
        if (!TryTakeNextCachedPartition(out var pk, out var ns, out var tenant)) return;

        if (_index.CountInNamespace(ns, tenant) < MinimumNodesForSpectral)
            Retract(pk);
    }

    /// <summary>
    /// The next partition of the rotation, resuming where the last call left it and starting a fresh
    /// pass when it is exhausted.
    ///
    /// The enumerator is held across calls, which is the whole point: restarting it per call would
    /// reconcile the first partition forever and never reach the rest.
    /// <c>ConcurrentDictionary</c>'s enumerator is explicitly safe to hold while the dictionary is
    /// mutated — it may miss a key added after it was taken and may surface one removed since — and
    /// both are harmless: a missed key is reconciled next pass, and a stale key resolves to a
    /// partition probe whose retraction is a no-op.
    ///
    /// The lock covers the enumerator step and nothing else. The partition probe takes a read lock
    /// inside <c>CognitiveIndex</c> and is deliberately outside this gate, so the gate is never held
    /// across another component's lock and cannot join a cycle.
    /// </summary>
    private bool TryTakeNextCachedPartition(out string partitionKey, out string ns, out string tenant)
    {
        lock (_reconcileGate)
        {
            var walk = _reconcileWalk;
            if (walk is null || !walk.MoveNext())
            {
                walk?.Dispose();
                walk = ((IEnumerable<KeyValuePair<string, CachedBasis>>)_cache).GetEnumerator();

                if (!walk.MoveNext())
                {
                    // Nothing cached: drop the enumerator rather than keeping an exhausted one, so
                    // the next call starts a pass that can see entries written since.
                    walk.Dispose();
                    _reconcileWalk = null;
                    partitionKey = ns = tenant = string.Empty;
                    return false;
                }

                _reconcileWalk = walk;
            }

            partitionKey = walk.Current.Key;
        }

        // Split back into its two components OUTSIDE the gate. The basis records the namespace it
        // was built for, which is the half a probe needs, and the tenant is whatever precedes the
        // separator — exactly how PartitionKey composed it, and unambiguous because
        // ValidatePartitionComponent refuses a separator in either half.
        int sep = partitionKey.IndexOf(Tenancy.PartitionSeparator);
        tenant = sep < 0 ? string.Empty : partitionKey.Substring(0, sep);
        ns = sep < 0 ? partitionKey : partitionKey.Substring(sep + 1);
        return true;
    }

    // ── internals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the eigenbasis for <paramref name="ns"/>, or <c>null</c> when the
    /// namespace doesn't qualify. Virtual purely as a test seam so fault-isolation
    /// tests can inject deterministic failures — not intended as an extension point.
    /// </summary>
    protected virtual DiffusionBasis? ComputeBasis(string ns, int topK, long graphRevision, string tenantId)
    {
        var entries = _index.GetAllInNamespace(ns, tenantId);
        if (entries.Count < MinimumNodesForSpectral)
        {
            _logger?.LogDebug(
                "Diffusion kernel bypass for ns={Namespace}: {Count} nodes < {Min} minimum.",
                ns, entries.Count, MinimumNodesForSpectral);
            return null;
        }

        var entryIds = entries.Select(e => e.Id).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var indexOf = new Dictionary<string, int>(entryIds.Length);
        for (int i = 0; i < entryIds.Length; i++) indexOf[entryIds[i]] = i;

        // Build symmetric sparse adjacency restricted to this namespace and positive relations.
        // First pass: collect candidate edge weights keyed by ordered (i,j) with i<j.
        //
        // The ATTRIBUTABLE view, and it must stay that way. A GraphEdge carries no namespace, so a
        // stored edge between ids X and Y is only a claim about two BARE ids. If the tenant holds X
        // in two namespaces, the edge is unattributable: an edge created between another
        // principal's private twins is byte-identical to one created between the entries here, and
        // nothing on it distinguishes them. The `indexOf` filter below does not close that — it
        // proves only that entries bearing those ids exist in this namespace, which is a candidate
        // interpretation of the ids, never proof of the edge's origin. Building the basis from
        // unattributable edges therefore imports another principal's topology into this namespace's
        // retrieval ranking, and it does so invisibly: the endpoints never surface, they just
        // silently decide which of THIS namespace's entries get boosted together.
        //
        // ACCEPTED CONSEQUENCE, stated rather than hidden: a deployment that reuses ids across
        // namespaces of one tenant loses its diffusion basis for the affected namespaces until
        // endpoints become namespace-qualified (issue #19). That is the fail-CLOSED outcome and it
        // is the correct one — the behaviour it replaces bought those namespaces a basis by
        // disclosing topology across a boundary they were never shown.
        var allEdges = _graph.GetAllEdges(tenantId);
        var weights = new Dictionary<(int Lo, int Hi), float>();
        int edgeCount = 0;
        foreach (var edge in allEdges)
        {
            if (!PositiveRelations.Contains(edge.Relation)) continue;
            if (!indexOf.TryGetValue(edge.SourceId, out var src)) continue;
            if (!indexOf.TryGetValue(edge.TargetId, out var dst)) continue;
            if (src == dst) continue;

            var key = src < dst ? (src, dst) : (dst, src);
            if (!weights.TryGetValue(key, out var existing) || edge.Weight > existing)
                weights[key] = edge.Weight;
        }
        edgeCount = weights.Count;

        if (edgeCount < MinimumEdgesForSpectral)
        {
            _logger?.LogDebug(
                "Diffusion kernel bypass for ns={Namespace}: only {EdgeCount} positive-relation edges (< {Min}).",
                ns, edgeCount, MinimumEdgesForSpectral);
            return null;
        }

        // ── Structural deflation of isolated nodes ────────────────────────────
        // Entries with no positive-relation edges contribute exactly-zero
        // rows/columns to M = D^(-1/2) W D^(-1/2), making the operator exactly
        // rank-deficient — the regime where the randomized eigensolver's panel
        // collapses and orthonormality cannot be maintained in float32.
        // Mathematically, each isolated node is its own connected component with
        // Laplacian eigenvalue 0, so every spectral filter is identity on it
        // (exp(-t·0) = 1). We therefore exclude isolated nodes from the
        // eigenproblem and let ApplySpectralFilter's out-of-basis pass-through
        // handle them — exact and numerically safe.
        var nonIsolated = new bool[entryIds.Length];
        foreach (var ((lo, hi), _) in weights)
        {
            nonIsolated[lo] = true;
            nonIsolated[hi] = true;
        }
        int compactCount = 0;
        for (int i = 0; i < nonIsolated.Length; i++)
            if (nonIsolated[i]) compactCount++;
        int isolatedCount = entryIds.Length - compactCount;

        if (compactCount < MinimumNodesForSpectral)
        {
            _logger?.LogDebug(
                "Diffusion kernel bypass for ns={Namespace}: {Count} linked nodes < {Min} minimum ({Total} total, {Isolated} isolated).",
                ns, compactCount, MinimumNodesForSpectral, entryIds.Length, isolatedCount);
            return null;
        }

        // Compact index map over non-isolated nodes only, preserving relative
        // (ordinal) order so basis construction stays deterministic.
        var compactOf = new int[entryIds.Length];
        var basisEntryIds = new string[compactCount];
        int next = 0;
        for (int i = 0; i < entryIds.Length; i++)
        {
            if (nonIsolated[i])
            {
                compactOf[i] = next;
                basisEntryIds[next] = entryIds[i];
                next++;
            }
            else
            {
                compactOf[i] = -1;
            }
        }

        // Remap edge weights into compact index space. Order preservation keeps
        // lo < hi invariant intact after remapping.
        var compactWeights = new Dictionary<(int Lo, int Hi), float>(weights.Count);
        foreach (var ((lo, hi), w) in weights)
            compactWeights[(compactOf[lo], compactOf[hi])] = w;

        // CSR-style adjacency for fast matVec, at compact (linked-only) dimension.
        int n = compactCount;
        var rowStart = new int[n + 1];
        var colIdx = new int[edgeCount * 2];
        var vals = new float[edgeCount * 2];

        var degree = new float[n];
        foreach (var ((lo, hi), w) in compactWeights)
        {
            degree[lo] += w;
            degree[hi] += w;
        }

        // Bucket edges by row to fill CSR. First count, then fill.
        var rowCount = new int[n];
        foreach (var ((lo, hi), _) in compactWeights)
        {
            rowCount[lo]++;
            rowCount[hi]++;
        }
        int cursor = 0;
        for (int i = 0; i < n; i++) { rowStart[i] = cursor; cursor += rowCount[i]; }
        rowStart[n] = cursor;

        var fillCursor = new int[n];
        Array.Copy(rowStart, fillCursor, n);
        foreach (var ((lo, hi), w) in compactWeights)
        {
            colIdx[fillCursor[lo]] = hi; vals[fillCursor[lo]] = w; fillCursor[lo]++;
            colIdx[fillCursor[hi]] = lo; vals[fillCursor[hi]] = w; fillCursor[hi]++;
        }

        // Inverse sqrt degree, used in M = D^(-1/2) W D^(-1/2). Isolated nodes
        // were deflated above, so every remaining node has at least one
        // positive-weight edge and degree > 0 by construction — the 0 sentinel
        // (and the exactly-zero rows it produced) can no longer occur.
        var invSqrtDeg = new float[n];
        for (int i = 0; i < n; i++)
        {
            System.Diagnostics.Debug.Assert(degree[i] > 0f,
                "Deflation invariant violated: linked node with zero degree.");
            invSqrtDeg[i] = 1f / MathF.Sqrt(degree[i]);
        }

        // (M x)[i] = invSqrtDeg[i] * sum over neighbors j: w_ij * invSqrtDeg[j] * x[j]
        void MatVec(ReadOnlySpan<float> x, Span<float> y)
        {
            for (int i = 0; i < n; i++)
            {
                float acc = 0f;
                int rs = rowStart[i];
                int re = rowStart[i + 1];
                float si = invSqrtDeg[i];
                for (int p = rs; p < re; p++)
                    acc += vals[p] * invSqrtDeg[colIdx[p]] * x[colIdx[p]];
                y[i] = si * acc;
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rng = new Random(unchecked((int)(graphRevision ^ ns.GetHashCode())));
        var (mEig, mVec) = RandomizedEigensolver.SolveTopK(n, topK, Oversample, PowerIterations, MatVec, rng);
        sw.Stop();

        // Convert eigenpairs of M to eigenpairs of L = I - M and sort ascending.
        // Eigenvalues of the normalized Laplacian must lie in [0, 2]; clamp small
        // negative numerical noise (typically the smallest eigenvalue, which is
        // exactly 0 in exact arithmetic for connected components) so callers can
        // safely apply MathF.Pow(lambda, alpha) for fractional-Laplacian filters
        // without producing NaN.
        var lEigsUnsorted = new float[mEig.Length];
        for (int j = 0; j < mEig.Length; j++)
        {
            float lambdaL = 1f - mEig[j];
            if (lambdaL < 0f) lambdaL = 0f;
            lEigsUnsorted[j] = lambdaL;
        }

        var order = new int[mEig.Length];
        for (int j = 0; j < mEig.Length; j++) order[j] = j;
        Array.Sort(order, (a, b) => lEigsUnsorted[a].CompareTo(lEigsUnsorted[b]));

        var lEigs = new float[mEig.Length];
        var lVecs = new float[n, mEig.Length];
        for (int j = 0; j < mEig.Length; j++)
        {
            int src = order[j];
            lEigs[j] = lEigsUnsorted[src];
            for (int i = 0; i < n; i++) lVecs[i, j] = mVec[i, src];
        }

        _logger?.LogInformation(
            "Diffusion kernel: built basis for ns={Namespace} (n={Nodes} linked of {Total} total, {Isolated} isolated deflated, edges={Edges}, k={TopK}) in {Ms}ms.",
            ns, n, entryIds.Length, isolatedCount, edgeCount, lEigs.Length, sw.ElapsedMilliseconds);

        return new DiffusionBasis(ns, basisEntryIds, lEigs, lVecs, edgeCount, graphRevision);
    }
}
