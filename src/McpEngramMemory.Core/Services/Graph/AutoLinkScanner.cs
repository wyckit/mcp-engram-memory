using System.Collections.Concurrent;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Retrieval;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Graph;

/// <summary>
/// The deferred pair source an auto-link scan draws from —
/// <see cref="DuplicateDetector.StreamDuplicates"/> in every production path.
///
/// It has no OUTPUT bound by design: the scan filters what it is handed, so a source that could
/// only be asked for a fixed number of pairs would hand back the same window on every deterministic
/// rescan and never reach what stands behind it. What it does take is a
/// <see cref="PairScanWindow"/> — a bound on COMPARISONS, which is a different thing entirely,
/// because successive windows tile the pair space instead of repeating one prefix of it.
/// </summary>
internal delegate IEnumerable<(string IdA, string IdB, float Similarity)> PairStream(
    IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
    float threshold,
    PairScanWindow window,
    CancellationToken cancellationToken);

/// <summary>
/// Background-friendly graph maintenance that periodically scans a namespace for
/// semantically-similar entry pairs and creates <c>similar_to</c> edges between
/// them. The diffusion kernel, sleep consolidation, and any future spectral
/// retrieval all become more powerful as the graph densifies — this service
/// builds that density automatically from the embeddings the system already has,
/// without requiring explicit <c>link_memories</c> calls.
///
/// Internals piggyback on <see cref="DuplicateDetector"/>: the detector already
/// knows how to find pairs above a similarity threshold, including the spectral
/// pre-filter for namespaces above 256 entries. Auto-link calls into it with a
/// looser threshold (default 0.85 — clear semantic neighbors but not duplicates,
/// which sit near 0.95) and converts each surviving pair into an undirected
/// graph edge in canonical (lex-ordered) direction so re-scans don't oscillate
/// between A-&gt;B and B-&gt;A directions.
///
/// Pairs that already have any existing edge between them — in either direction,
/// any relation — are skipped. Auto-link never overwrites manually-created
/// edges, never duplicates a contradicts/parent_child/etc. with a redundant
/// similar_to. That is a precondition about the graph rather than about the candidate, so it is
/// finally decided by <see cref="EdgeAddMode.OnlyIfUnlinked"/> under the graph's own write lock; the
/// probe this scan does while ranking is a cost filter, not the authority. A per-scan edge cap
/// (configurable via <see cref="DecayConfig"/>) bounds how much is written; subsequent scans pick up
/// what the cap deferred.
///
/// <see cref="AutoLinkResult.EdgesCreated"/> is the count the GRAPH accepted, never the count this
/// scan offered. The two differ: this sweep runs with no principal and no namespace of its own, so
/// it can propose an edge whose endpoint id names two of the tenant's namespaces, and Core declines
/// that endpoint because the node it would land on is shared with an entry nobody showed the sweep.
/// A number that counted attempts would be a number no edge in the graph answers to, and the
/// background service, the tool that surfaces it, and every operator reading it would inherit the
/// error.
///
/// The edge CAP is spent on accepted writes for the same reason, and that is a separate property
/// from the count being honest. A candidate is screened for endpoint attribution before it takes a
/// slot, because a slot spent on a write the graph will decline is a slot the next viable candidate
/// never gets: a namespace whose highest-ranked pair happens to be unattributable would write
/// nothing at all under a small cap, and — the scan being deterministic over unchanged entries —
/// would write nothing again on every rescan, so it could never heal itself.
///
/// The pairs are DRAWN the same way, and for the same reason. The two eligibility filters — already
/// carries an edge, endpoint not attributable — consume candidates, so a fixed-size request for
/// candidates is a request that ineligible pairs can empty before a viable one is reached. Eleven
/// viable pairs under a cap of one used to stop at ten stored edges and stay there: ten were offered
/// on every deterministic rescan and the eleventh on none of them. So this pulls from a DEFERRED
/// pair stream and keeps pulling past what it discards, holding nothing per pair beyond a ranking
/// buffer the size of the cap.
///
/// A scan is bounded by COMPARISONS rather than by candidates offered, and it resumes: each
/// namespace carries a cursor into the pairwise walk's anchor space, and a scan that stopped on the
/// budget starts the next one where it left off, wrapping. That is the only shape of bound that does
/// not reintroduce the starvation above — a budget that always restarted at the first pair would
/// hide everything past it on every rescan forever, which is the same defect with a different cause.
/// The three ways a scan can stop are all reported, and are not collapsed into each other: see
/// <see cref="AutoLinkResult"/>.
/// </summary>
public sealed class AutoLinkScanner
{
    /// <summary>
    /// Upper bound on entries fed to the pairwise duplicate scan in one pass.
    ///
    /// The scan is quadratic in the candidate count, and it runs automatically on every
    /// namespace every six hours, so an unbounded namespace turns a routine sweep into a
    /// steadily growing CPU cost with no backpressure. Measured on 384-dim vectors:
    /// ~0.4s at 2,000 entries, ~1.1s at 4,200, ~2.6s at 8,000 — quadratic from there, so
    /// tens of thousands of entries in one namespace reach minutes per sweep.
    ///
    /// 10,000 sits well above any namespace this is expected to meet while capping a single
    /// pass at a few seconds. Truncation is reported in the result and logged, never silent:
    /// a scan that quietly examined half the namespace looks identical to one that found
    /// nothing, which is the worse failure.
    ///
    /// Unlike <see cref="DefaultMaxPairComparisons"/> this bound does NOT rotate. Entries past it
    /// are not examined by any scan until the namespace shrinks or the caller raises the bound.
    /// </summary>
    public const int DefaultMaxScanEntries = 10_000;

    /// <summary>
    /// Cosine comparisons one scan will make before stopping and leaving the rest to the next one.
    ///
    /// Without it, a namespace in steady state — every above-threshold neighbour already linked, so
    /// nothing found is usable — walks its entire pair space every six hours to discover that. At
    /// <see cref="DefaultMaxScanEntries"/> that is 49,995,000 comparisons per namespace per sweep,
    /// paid forever for a result that is always "nothing to do".
    ///
    /// 20,000,000 caps one pass at roughly what a 4,200-entry namespace costs today (~1.1s), and it
    /// is deliberately far above what a real namespace needs: any namespace up to ~4,470 entries
    /// still gets its whole pair space in a single scan, so the windowing is invisible to everything
    /// except the pathological case it exists for. At the entry cap it takes five scans — about a
    /// day and a quarter of the six-hourly cadence — to come round, which is the right trade for a
    /// job whose output is a slowly-densifying graph.
    ///
    /// It bounds the pairwise stage only. The per-scan candidate set-up (loading the namespace,
    /// norms, and above the pivot the subspace projection) is linear in the entries and is paid
    /// whole every scan.
    /// </summary>
    public const long DefaultMaxPairComparisons = 20_000_000;

    /// <summary>
    /// Spare capacity in the ranking buffer beyond twice the edge cap.
    ///
    /// The buffer is a bounded top-K: candidates arrive in scan order, not in similarity order, so
    /// the best of them can only be picked out by keeping more than the cap will spend and dropping
    /// the losers as it fills. A purely multiplicative margin vanishes exactly where it is needed —
    /// twice a cap of 1 is ONE spare pair — so the additive term keeps a small cap workable while
    /// the multiplier still dominates at large ones, and it is the multiplier that makes compaction
    /// amortize to O(1) per candidate: each one discards half the buffer.
    ///
    /// It is NOT a bound on how far the scan looks, and no longer stops the loop at all. Every
    /// admissible candidate in the scan's window is ranked against every other; what the buffer
    /// bounds is MEMORY, at O(cap) rather than O(pairs walked).
    /// </summary>
    private const int RankingBufferSlack = 8;

    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly PairStream _pairs;
    private readonly Action<int>? _onLoopRetention;
    private readonly ILogger<AutoLinkScanner>? _logger;

    /// <summary>
    /// Where the next scan of each (tenant, namespace) resumes in the pairwise walk's anchor space.
    ///
    /// In memory and not persisted, deliberately. It exists to stop one budgeted scan from being the
    /// same budgeted scan forever; losing it on restart costs a repeat of one window, never a pair
    /// that no scan reaches. Persisting it would buy nothing and would have to be reconciled against
    /// a namespace whose entry count changed underneath it.
    ///
    /// It is an anchor index, not a bookmark: the candidate list is rebuilt per scan and an insert
    /// or delete shifts what a given anchor covers. That is sound for what this is — a rotation that
    /// guarantees every anchor is visited, not a promise about which pair comes next — and the
    /// steady state it exists to bound is precisely the case where nothing shifts.
    /// </summary>
    private readonly ConcurrentDictionary<(string Tenant, string Namespace), int> _resumeAnchors = new();

    public AutoLinkScanner(
        CognitiveIndex index,
        KnowledgeGraph graph,
        DuplicateDetector duplicateDetector,
        ILogger<AutoLinkScanner>? logger = null)
        : this(index, graph, duplicateDetector, pairs: null, logger)
    {
    }

    /// <summary>
    /// Seam for a test that has to state which candidate arrives before which, count how many
    /// pairwise passes one scan costs, or observe what the pair loop still holds when it ends.
    /// Internal because neither is a knob an embedder should be turning: <paramref name="pairs"/>
    /// null means the detector, which is what every production path gets.
    ///
    /// <paramref name="onLoopRetention"/> is handed the number of candidate tuples the scan is still
    /// holding when its pair loop ends. It is the only way to state the memory property from
    /// outside: the retention that mattered was a set keyed by pair, and a set keyed by pair is
    /// invisible in the result, in the graph and in the timings until it is large enough to take the
    /// process down with it.
    /// </summary>
    internal AutoLinkScanner(
        CognitiveIndex index,
        KnowledgeGraph graph,
        DuplicateDetector duplicateDetector,
        PairStream? pairs,
        ILogger<AutoLinkScanner>? logger = null,
        Action<int>? onLoopRetention = null)
    {
        _index = index;
        _graph = graph;
        _pairs = pairs ?? duplicateDetector.StreamDuplicates;
        _logger = logger;
        _onLoopRetention = onLoopRetention;
    }

    /// <summary>
    /// Scan a single namespace and add <c>similar_to</c> edges for high-cosine
    /// pairs that don't already have any edge between them.
    /// </summary>
    /// <param name="ns">Namespace to scan.</param>
    /// <param name="threshold">Similarity threshold override; pass <c>threshold: null</c> to use the namespace's <see cref="DecayConfig.AutoLinkSimilarityThreshold"/>. Required so tenantId never sits behind a nullable slot an old positional call could silently shift into.</param>
    /// <param name="maxNewEdges">Per-scan cap on edges the graph ACCEPTS, not on candidates offered; pass <c>maxNewEdges: null</c> for the default cap. Required for the same reason as <paramref name="threshold"/>.</param>
    /// <param name="tenantId">Tenant partition to scan. Pass "" for the legacy partition.</param>
    /// <param name="maxScanEntries">Upper bound on entries fed to the quadratic pairwise stage in one pass; 0 disables it. Anything skipped is reported in the result.</param>
    /// <param name="maxPairComparisons">Cosine comparisons this pass will make before deferring the rest to the next scan, which resumes where this one stopped; 0 or less disables the bound. Reported as <see cref="AutoLinkResult.PairScanIncomplete"/>.</param>
    /// <param name="cancellationToken">Stops the pairwise walk between anchors. A cancelled scan writes what it already ranked and leaves its resume cursor untouched, so the window it abandoned is the window the next scan starts on.</param>
    public AutoLinkResult Scan(string ns, float? threshold, int? maxNewEdges,
        string tenantId, int maxScanEntries = DefaultMaxScanEntries,
        long maxPairComparisons = DefaultMaxPairComparisons,
        CancellationToken cancellationToken = default)
    {
        var entries = _index.GetAllInNamespace(ns, tenantId: tenantId);
        var nonSummary = new List<CognitiveEntry>(entries.Count);
        foreach (var e in entries)
            if (!e.IsSummaryNode && e.Vector.Length > 0) nonSummary.Add(e);

        if (nonSummary.Count < 2)
            return new AutoLinkResult(ns, nonSummary.Count, 0, 0, 0, false);

        int notScanned = 0;
        if (maxScanEntries > 0 && nonSummary.Count > maxScanEntries)
        {
            notScanned = nonSummary.Count - maxScanEntries;
            _logger?.LogWarning(
                "Auto-link scan for ns={Namespace} bounded to {Max} of {Total} entries; {Skipped} not examined this pass. " +
                "The pairwise stage is quadratic; raise maxScanEntries to scan more at higher cost.",
                ns, maxScanEntries, nonSummary.Count, notScanned);
            nonSummary.RemoveRange(maxScanEntries, notScanned);
        }

        // Build the (entry, norm, quantized) triples DuplicateDetector expects.
        // Quantized vectors are reserved for archived entries elsewhere; we don't
        // need them here, so pass null and let the detector use FP32 directly.
        var candidates = new List<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>(nonSummary.Count);
        foreach (var entry in nonSummary)
        {
            float norm = VectorMath.Norm(entry.Vector);
            if (norm == 0f) continue;
            candidates.Add((entry, norm, null));
        }
        if (candidates.Count < 2)
            return new AutoLinkResult(ns, candidates.Count, 0, 0, 0, false, notScanned);

        float effectiveThreshold = threshold ?? 0.85f;
        int effectiveCap = maxNewEdges ?? 1000;
        int spendable = Math.Max(effectiveCap, 0);

        // How many of the best admissible candidates to keep while ranking. Saturating rather than
        // multiplying blind: an int.MaxValue cap doubled is NEGATIVE, and a buffer with a negative
        // capacity would be full before the first candidate arrived — the starvation this whole fix
        // is about, arriving by a different road.
        int rankingCapacity = effectiveCap > (int.MaxValue - RankingBufferSlack) / 2
            ? int.MaxValue
            : Math.Max((effectiveCap * 2) + RankingBufferSlack, RankingBufferSlack);

        // The window this scan gets, and where the next one picks up. maxAnchors is derived from the
        // comparison budget rather than configured directly because an anchor is worth a different
        // amount of work in every namespace: the widest anchor costs one comparison per candidate,
        // so budget/candidates anchors cost at most the budget. At least one anchor always runs — an
        // anchor is indivisible, so a budget below one namespace-width buys one row rather than
        // nothing, and a scan that examined nothing could never advance its own cursor.
        int startAnchor = _resumeAnchors.TryGetValue((tenantId, ns), out var stored)
            ? stored % candidates.Count
            : 0;
        int maxAnchors = maxPairComparisons <= 0
            ? candidates.Count
            : (int)Math.Clamp(maxPairComparisons / candidates.Count, 1, candidates.Count);

        int pairsExamined = 0;
        int skippedExisting = 0;
        int refusedUnattributable = 0;
        int admissibleSeen = 0;

        // ONE sweep for the whole scan, built on the first pair that needs judging and never again.
        // Every question this loop asks about an id — is the tenant holding it in more than one
        // namespace, does it already carry an attributable edge — resolves through CognitiveIndex,
        // and a guard built per candidate re-lists the tenant's namespaces once per pair on a job
        // that visits every namespace every six hours. The sweep judges each distinct id once and
        // against one snapshot, so two candidates naming the same id can never disagree about it.
        //
        // Deferred to first use rather than built up front, which is the same rule KnowledgeGraph
        // applies to a node with no adjacency: constructing it lists the tenant's namespaces, and a
        // namespace with no candidate pair at all must not pay for a listing it would never consult.
        TopologyGuard.Sweep? guard = null;

        // Proposed first, written once. Three reasons, and all three are load-bearing.
        //
        // COST: KnowledgeGraph screens each endpoint against a listing of the tenant's namespaces,
        // and AddEdge builds that listing per call. Adding in a loop therefore re-lists — and so
        // reloads the store — once per candidate edge. AddEdges builds one listing for the batch.
        //
        // HONESTY: AddEdges reports what it actually wrote. An endpoint the tenant holds in two
        // namespaces names a node shared with an entry this sweep was never shown, so Core declines
        // it; counting the attempt would put a number in AutoLinkResult.EdgesCreated that no edge in
        // the graph answers to.
        //
        // CAP: only admissible candidates reach this list, so the cap bounds accepted writes rather
        // than attempts. A refused candidate that consumed a slot would starve the viable candidate
        // ranked behind it — permanently, since an unchanged namespace produces the same ranking on
        // every rescan.
        //
        // This list is the ONLY thing the loop accumulates, and it is bounded by rankingCapacity. It
        // used to be joined by a set of every pair the stream had yielded, which was quadratic in
        // the namespace and bought nothing: the stream yields each unordered pair exactly once (see
        // DuplicateDetector.StreamDuplicates), and the write boundary below refuses a second edge
        // between one pair of endpoints anyway.
        var ranked = new List<(GraphEdge Edge, float Similarity)>();
        var neighbors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        // ONE pairwise pass. Re-asking a fixed-size detector with a larger number would have been
        // the other way to reach the pairs behind an ineligible run, and it would repeat the whole
        // quadratic comparison per attempt, on a namespace whose steady state — every neighbor
        // already linked — is precisely the case that forces the most attempts.
        foreach (var (idA, idB, sim) in _pairs(candidates, effectiveThreshold,
                     new PairScanWindow(startAnchor, maxAnchors), cancellationToken))
        {
            pairsExamined++;

            // Canonical direction: lex-smaller id is the source. This makes
            // re-scans deterministic — we always try to add the same edge object.
            var (src, dst) = string.CompareOrdinal(idA, idB) < 0 ? (idA, idB) : (idB, idA);

            guard ??= TopologyGuard.ForSweep(_index, tenantId);

            var candidate = new GraphEdge(src, dst, "similar_to", Math.Clamp(sim, 0f, 1f), null, tenantId);

            // Screened as the EDGE that would be written, against the same predicate AddEdges will
            // apply to it, and BEFORE the slot is taken — that ordering is the fix. A candidate the
            // graph is going to decline must not spend a cap the next candidate could have used.
            //
            // Ahead of the existing-edge probe, not after it, and that is load-bearing beyond cost:
            // EdgesSkippedExisting reaches a caller. Probing first would let an unattributable pair
            // land in that count whenever a hidden edge happens to run between the two ids, and the
            // count would then answer a question about a node the caller was never shown.
            if (!guard.IsEdgeUsable(candidate))
            {
                refusedUnattributable++;
                continue;
            }

            if (HasAnyEdgeBetween(src, dst, tenantId, guard, neighbors))
            {
                skippedExisting++;
                continue;
            }

            // Counted rather than inferred from the buffer's size, because the buffer now discards
            // its losers as it fills. HitMaxEdgeCap asks how many admissible candidates EXISTED, and
            // that question outlived the buffer that used to answer it.
            admissibleSeen++;

            // Ranked on the raw cosine rather than on the edge's clamped weight: the clamp exists to
            // keep a stored weight inside [0,1] against float drift, and two candidates that both
            // drifted above 1 would otherwise rank as a tie they are not.
            ranked.Add((candidate, sim));
            if (ranked.Count >= rankingCapacity)
                KeepBest(ranked, spendable);
        }

        // Bounded by the cap and not by the pairs walked. The witness is here rather than in the
        // result because it is a property of the implementation, not of the scan.
        _onLoopRetention?.Invoke(ranked.Count);

        // The cap was binding only if it left an admissible candidate unwritten. Stated over how
        // many admissible candidates the scan SAW, which is exact: it does not depend on how large
        // the ranking buffer is, and it stays true now that the buffer drops candidates it has
        // outranked. A caller reading HitMaxEdgeCap false learns that every admissible candidate in
        // this scan's window was written — and PairScanIncomplete tells it whether that window was
        // the whole namespace.
        bool hitCap = admissibleSeen > effectiveCap;

        // Highest similarity first, ties broken on the canonical endpoints. The stream arrives in
        // scan order, so the ranking is imposed here; the tiebreak is what keeps two equally-similar
        // candidates from swapping places between rescans and re-deciding which one the cap buys.
        KeepBest(ranked, spendable);

        var pending = new List<GraphEdge>(Math.Min(spendable, ranked.Count));
        for (int i = 0; i < ranked.Count && pending.Count < spendable; i++)
            pending.Add(ranked[i].Edge);

        // OnlyIfUnlinked, because "these two are not related yet" is the one precondition this
        // scanner cannot establish for itself. HasAnyEdgeBetween above reads a snapshot and memoizes
        // it for the whole scan; a manual relation created after that read is invisible to it, and
        // the default write boundary replaces only the SAME relation, so the pair would end up
        // carrying both the manual edge and a derived similar_to. The graph re-tests the condition
        // under its own write lock, where it is atomic with the write.
        int created = pending.Count > 0 ? _graph.AddEdges(pending, EdgeAddMode.OnlyIfUnlinked) : 0;

        // Two causes, kept together because both mean the same thing to a caller — the scan judged a
        // candidate admissible and the graph disagreed at the instant of writing. Either an endpoint
        // became ambiguous between the scanner's sweep and the one inside AddEdges, or a relation
        // appeared between the endpoints after this scan read their adjacency. Both must fail
        // closed, and neither is an error; a non-zero value here means a race, not a bad candidate.
        int declinedAtWrite = pending.Count - created;

        bool cancelled = cancellationToken.IsCancellationRequested;
        bool pairScanIncomplete = maxAnchors < candidates.Count || cancelled;

        // WHERE THE NEXT SCAN STARTS, and the one rule that keeps a budget from becoming the
        // starvation it is supposed to be safe from.
        //
        // The default is to advance past the anchors this scan examined, so successive scans tile
        // the anchor space and every pair is reached within ceil(candidates / maxAnchors) scans.
        // The cursor HOLDS in exactly two cases.
        //
        // - The cap bound this scan AND it wrote something. The window still owes edges, so
        //   re-running it drains another capful; each written pair is already-linked next time, so
        //   the admissible count strictly falls and the window cannot repeat forever. Without this a
        //   cap of one would take one edge per full rotation out of a window holding hundreds. The
        //   "wrote something" half is what stops a cap of zero — or a window whose every write was
        //   declined — from holding the rotation still with no prospect of draining anything.
        // - Cancellation. The window was abandoned rather than examined, so advancing past it would
        //   step over pairs no scan looked at.
        if (!cancelled && (!hitCap || created == 0))
            _resumeAnchors[(tenantId, ns)] = (startAnchor + maxAnchors) % candidates.Count;

        // The log MAY name refusals where a reply may not: this runs as background maintenance with
        // no caller, so the count cannot become an "a twin exists somewhere in your tenant" oracle —
        // and an operator debugging a namespace that stubbornly refuses to densify needs to be able
        // to tell suppression apart from a namespace with nothing similar in it. AutoLinkResult
        // carries no such field for the same reason inverted: it does reach a caller.
        if (created > 0 || refusedUnattributable > 0 || declinedAtWrite > 0 || pairScanIncomplete)
        {
            _logger?.LogInformation(
                "Auto-link scan ns={Namespace}: {Created} new similar_to edges, {Refused} refused (endpoint not attributable to a single entry), {Declined} declined at write (endpoint became ambiguous, or the pair was linked, mid-scan), {Skipped} skipped (existing edge), {Examined} pairs examined over anchors {Start}..+{Anchors} of {Total}{CapNote}{BudgetNote}.",
                ns, created, refusedUnattributable, declinedAtWrite, skippedExisting, pairsExamined,
                startAnchor, maxAnchors, candidates.Count,
                hitCap ? " (hit cap)" : "",
                pairScanIncomplete ? " (pair scan incomplete; next scan resumes where this one stopped)" : "");
        }

        return new AutoLinkResult(ns, candidates.Count, pairsExamined, created, skippedExisting,
            hitCap, notScanned, pairScanIncomplete);
    }

    /// <summary>
    /// Sort by rank and drop everything past <paramref name="keep"/>.
    ///
    /// Called both as the buffer fills and once at the end, which is what makes the buffer a top-K
    /// rather than a prefix: a candidate discarded here was outranked by <paramref name="keep"/>
    /// others, so it cannot belong to the best <paramref name="keep"/> of anything this scan goes on
    /// to see. Compaction is O(k log k) and frees half the buffer, so it amortizes to O(1) per
    /// candidate examined.
    /// </summary>
    private static void KeepBest(List<(GraphEdge Edge, float Similarity)> ranked, int keep)
    {
        ranked.Sort(static (x, y) =>
        {
            int bySimilarity = y.Similarity.CompareTo(x.Similarity);
            if (bySimilarity != 0) return bySimilarity;
            int bySource = string.CompareOrdinal(x.Edge.SourceId, y.Edge.SourceId);
            return bySource != 0 ? bySource : string.CompareOrdinal(x.Edge.TargetId, y.Edge.TargetId);
        });
        if (ranked.Count > keep)
            ranked.RemoveRange(keep, ranked.Count - keep);
    }

    /// <summary>
    /// True when an ATTRIBUTABLE edge already ran between the two ids — in either direction and
    /// under any relation — as of when this scan first read that node's adjacency.
    ///
    /// A COST FILTER, NOT THE AUTHORITY. It answers from a snapshot while the graph stays mutable,
    /// so a relation created after the read is invisible to it; the condition is finally enforced by
    /// <see cref="EdgeAddMode.OnlyIfUnlinked"/> inside the graph's write lock, which is the only
    /// place it can be atomic with the write. What this buys is that the overwhelming majority of
    /// already-linked pairs never reach that lock at all, and that they are reported in
    /// <see cref="AutoLinkResult.EdgesSkippedExisting"/> instead of silently vanishing at the write.
    ///
    /// It reads the stored adjacency and applies the caller's sweep instead of calling
    /// <see cref="KnowledgeGraph.GetEdgesForEntry"/>, which builds a fresh sweep per call: this runs
    /// once per candidate pair, and a namespace listing per pair is precisely the cost a sweep
    /// exists to avoid. The predicate applied is the one that method applies — an edge is usable
    /// only when BOTH endpoints are attributable — so the answer is the attributable view's and not
    /// the stored view's, and it stays that way even if a caller ever reaches here without having
    /// pre-screened. Nothing read leaves this method: the only question put to the adjacency is
    /// whether the far endpoint is an id already in hand, so no bare id is resolved or projected.
    ///
    /// <paramref name="neighbors"/> memoizes one node's attributable neighbor ids for the rest of
    /// the scan, on the same reasoning as the sweep it is passed alongside: the scan examines every
    /// above-threshold pair in its window rather than a fixed number of them, and one id takes part
    /// in as many pairs as it has neighbors, so reading its adjacency per pair would take the
    /// graph's lock a quadratic number of times.
    /// </summary>
    private bool HasAnyEdgeBetween(string a, string b, string tenantId, TopologyGuard.Sweep guard,
        Dictionary<string, HashSet<string>> neighbors)
    {
        if (!neighbors.TryGetValue(a, out var adjacent))
        {
            adjacent = new HashSet<string>(StringComparer.Ordinal);
            // One node's adjacency covers both directions, so there is no need to fetch b's and union.
            foreach (var edge in _graph.GetStoredEdgesForEntry(a, tenantId: tenantId))
            {
                if (!guard.IsEdgeUsable(edge)) continue;
                if (edge.SourceId == a) adjacent.Add(edge.TargetId);
                else if (edge.TargetId == a) adjacent.Add(edge.SourceId);
            }
            neighbors[a] = adjacent;
        }
        return adjacent.Contains(b);
    }
}
