using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Retrieval;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Graph;

/// <summary>
/// The deferred, unbounded pair source an auto-link scan draws from —
/// <see cref="DuplicateDetector.StreamDuplicates"/> in every production path.
///
/// It has no result bound by design: the scan filters what it is handed, so a source that could
/// only be asked for a fixed number of pairs would hand back the same window on every deterministic
/// rescan and never reach what stands behind it.
/// </summary>
internal delegate IEnumerable<(string IdA, string IdB, float Similarity)> PairStream(
    IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
    float threshold);

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
/// similar_to. A per-scan edge cap (configurable via <see cref="DecayConfig"/>)
/// bounds the cost on dense namespaces; subsequent scans pick up where the cap
/// left off.
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
/// pair stream and keeps pulling past what it discards, until it holds more admissible candidates
/// than the cap can spend or the pairwise scan is genuinely finished. Both stopping conditions are
/// reachable and they are distinguishable in the result: <see cref="AutoLinkResult.HitMaxEdgeCap"/>
/// is true only in the first, so a scan reporting zero edges and no cap really did run out of pairs
/// rather than out of window.
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
    /// </summary>
    public const int DefaultMaxScanEntries = 10_000;

    /// <summary>
    /// Spare ADMISSIBLE candidates buffered beyond twice the edge cap.
    ///
    /// It is a ranking margin and nothing else. The pairs arrive in scan order, not in similarity
    /// order, so the top-ranked ones can only be picked out of a buffer that holds more than the cap
    /// will spend; the surplus is what makes "the best admissible candidates" mean more than "the
    /// first ones found". A purely multiplicative margin vanishes exactly where it is needed —
    /// twice a cap of 1 is ONE spare pair — so the additive term keeps a small cap workable while
    /// the multiplier still dominates at large ones.
    ///
    /// It is NOT a bound on how far the scan will look. Ineligible pairs are discarded without
    /// taking a place in this buffer, so however many of them stand in front of a viable pair, the
    /// viable pair is still reached.
    /// </summary>
    private const int PairPoolSlack = 8;

    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly PairStream _pairs;
    private readonly ILogger<AutoLinkScanner>? _logger;

    public AutoLinkScanner(
        CognitiveIndex index,
        KnowledgeGraph graph,
        DuplicateDetector duplicateDetector,
        ILogger<AutoLinkScanner>? logger = null)
        : this(index, graph, duplicateDetector, pairs: null, logger)
    {
    }

    /// <summary>
    /// Seam for a test that has to state which candidate arrives before which, or count how many
    /// pairwise passes one scan costs. Internal because the pair source is not a knob an embedder
    /// should be turning: <paramref name="pairs"/> null means the detector, which is what every
    /// production path gets.
    /// </summary>
    internal AutoLinkScanner(
        CognitiveIndex index,
        KnowledgeGraph graph,
        DuplicateDetector duplicateDetector,
        PairStream? pairs,
        ILogger<AutoLinkScanner>? logger = null)
    {
        _index = index;
        _graph = graph;
        _pairs = pairs ?? duplicateDetector.StreamDuplicates;
        _logger = logger;
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
    public AutoLinkResult Scan(string ns, float? threshold, int? maxNewEdges,
        string tenantId, int maxScanEntries = DefaultMaxScanEntries)
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

        // How many ADMISSIBLE candidates to buffer before the ranking is settled. It is not a bound
        // on how far the scan looks — ineligible pairs never enter this buffer — so an ineligible
        // run of any length no longer hides what is behind it. Saturating rather than multiplying
        // blind: an int.MaxValue cap doubled is NEGATIVE, and a buffer with a negative capacity
        // would be full before the first pair arrived, which is the starvation this whole fix is
        // about arriving by a different road.
        int pairPool = effectiveCap > (int.MaxValue - PairPoolSlack) / 2
            ? int.MaxValue
            : Math.Max((effectiveCap * 2) + PairPoolSlack, PairPoolSlack);

        int pairsExamined = 0;
        int skippedExisting = 0;
        int refusedUnattributable = 0;

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
        var admissible = new List<(GraphEdge Edge, float Similarity)>();
        var proposed = new HashSet<(string Src, string Dst)>();
        var neighbors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        // ONE pairwise pass. The stream is pulled until the buffer holds more admissible candidates
        // than the cap can spend, or until it runs dry — and abandoning it mid-scan abandons the
        // comparisons behind it, so a scan that fills its buffer early costs what the old bounded
        // request cost. Re-asking a fixed-size detector with a larger number would have been the
        // other way to reach the same pairs, and it would repeat the whole quadratic comparison per
        // attempt, on a namespace whose steady state — every neighbor already linked — is precisely
        // the case that forces the most attempts.
        foreach (var (idA, idB, sim) in _pairs(candidates, effectiveThreshold))
        {
            if (admissible.Count >= pairPool)
                break;

            pairsExamined++;

            // Canonical direction: lex-smaller id is the source. This makes
            // re-scans deterministic — we always try to add the same edge object.
            var (src, dst) = string.CompareOrdinal(idA, idB) < 0 ? (idA, idB) : (idB, idA);

            // At most one edge per unordered pair. The graph REPLACES a same source/target/relation
            // edge rather than appending one, so a pair offered twice would be counted twice and
            // stored once — the precise discrepancy this count exists to rule out.
            if (!proposed.Add((src, dst)))
                continue;

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

            // Ranked on the raw cosine rather than on the edge's clamped weight: the clamp exists to
            // keep a stored weight inside [0,1] against float drift, and two candidates that both
            // drifted above 1 would otherwise rank as a tie they are not.
            admissible.Add((candidate, sim));
        }

        // The cap was binding only if it left an admissible candidate unwritten. Stated over what
        // the buffer HOLDS rather than over how the loop ended, because those stopped being the same
        // question: the loop can now also end by exhausting the stream, and a caller reading
        // HitMaxEdgeCap false needs that to mean "everything admissible was written" and not "the
        // window ran out". Sound in both directions because the buffer is larger than the cap: a
        // full buffer proves a candidate was left over, and a buffer that did not fill proves the
        // stream ran dry. The two sizes coincide only at a cap of int.MaxValue, and a buffer that
        // large exhausts memory long before it fills, so the loop cannot end there by filling.
        bool hitCap = admissible.Count > effectiveCap;

        // Highest similarity first, ties broken on the canonical endpoints. The stream arrives in
        // scan order, so the ranking is imposed here; the tiebreak is what keeps two equally-similar
        // candidates from swapping places between rescans and re-deciding which one the cap buys.
        admissible.Sort(static (x, y) =>
        {
            int bySimilarity = y.Similarity.CompareTo(x.Similarity);
            if (bySimilarity != 0) return bySimilarity;
            int bySource = string.CompareOrdinal(x.Edge.SourceId, y.Edge.SourceId);
            return bySource != 0 ? bySource : string.CompareOrdinal(x.Edge.TargetId, y.Edge.TargetId);
        });

        var pending = new List<GraphEdge>(Math.Clamp(effectiveCap, 0, admissible.Count));
        for (int i = 0; i < admissible.Count && pending.Count < effectiveCap; i++)
            pending.Add(admissible[i].Edge);

        int created = pending.Count > 0 ? _graph.AddEdges(pending) : 0;

        // Normally zero now that candidates are pre-screened, and deliberately still measured. The
        // scanner's sweep and the one inside AddEdges snapshot the tenant's namespaces at different
        // moments, so a twin created in between makes the graph decline an edge this scan judged
        // admissible — which is exactly the case that must fail closed. Kept apart from the
        // pre-screen's refusals because a non-zero value here means a race, not an ambiguous id.
        int declinedAtWrite = pending.Count - created;

        // The log MAY name refusals where a reply may not: this runs as background maintenance with
        // no caller, so the count cannot become an "a twin exists somewhere in your tenant" oracle —
        // and an operator debugging a namespace that stubbornly refuses to densify needs to be able
        // to tell suppression apart from a namespace with nothing similar in it. AutoLinkResult
        // carries no such field for the same reason inverted: it does reach a caller.
        if (created > 0 || refusedUnattributable > 0 || declinedAtWrite > 0)
        {
            _logger?.LogInformation(
                "Auto-link scan ns={Namespace}: {Created} new similar_to edges, {Refused} refused (endpoint not attributable to a single entry), {Declined} declined at write (endpoint became ambiguous mid-scan), {Skipped} skipped (existing edge), {Examined} pairs examined{CapNote}.",
                ns, created, refusedUnattributable, declinedAtWrite, skippedExisting, pairsExamined,
                hitCap ? " (hit cap)" : "");
        }

        return new AutoLinkResult(ns, candidates.Count, pairsExamined, created, skippedExisting, hitCap, notScanned);
    }

    /// <summary>
    /// True when an ATTRIBUTABLE edge already runs between the two ids, in either direction and
    /// under any relation — the pairs auto-link leaves alone.
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
    /// the scan, on the same reasoning as the sweep it is passed alongside: the scan now examines
    /// every above-threshold pair rather than a fixed window of them, and one id takes part in as
    /// many pairs as it has neighbors, so reading its adjacency per pair would take the graph's lock
    /// a quadratic number of times. Safe to cache for the whole scan because the scan writes its
    /// edges once, after the loop — no candidate can be judged against adjacency this pass created.
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
