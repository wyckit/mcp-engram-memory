using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Retrieval;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Graph;

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
    /// Spare candidate pairs requested beyond twice the edge cap.
    ///
    /// Two post-filters eat into the pool the detector returns — pairs that already have an edge,
    /// and pairs whose endpoint cannot be attributed to a single entry — so the pool has to be
    /// larger than the cap or the scan cannot fill the cap. A purely multiplicative slack vanishes
    /// exactly where it is needed: twice a cap of 1 is ONE spare pair, so a single suppressed or
    /// already-linked candidate empties the pool, and every deterministic rescan empties it the
    /// same way. The additive term keeps a small cap workable; the multiplier still dominates at
    /// large ones, where the detector's cost is what the pool is really bounding.
    /// </summary>
    private const int PairPoolSlack = 8;

    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly DuplicateDetector _duplicateDetector;
    private readonly ILogger<AutoLinkScanner>? _logger;

    public AutoLinkScanner(
        CognitiveIndex index,
        KnowledgeGraph graph,
        DuplicateDetector duplicateDetector,
        ILogger<AutoLinkScanner>? logger = null)
    {
        _index = index;
        _graph = graph;
        _duplicateDetector = duplicateDetector;
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

        // Pull pairs above threshold. The detector's `maxResults` bounds what is OFFERED, and the
        // two post-filters below consume from that pool, so it is sized above the cap — see
        // PairPoolSlack for why the slack cannot be purely multiplicative. Saturating rather than
        // multiplying blind: an int.MaxValue cap doubled is a NEGATIVE maxResults, and a detector
        // asked for a negative number of pairs returns none at all, which is the starvation this
        // whole fix is about arriving by a different road.
        int pairPool = effectiveCap > (int.MaxValue - PairPoolSlack) / 2
            ? int.MaxValue
            : (effectiveCap * 2) + PairPoolSlack;
        var pairs = _duplicateDetector.FindDuplicates(candidates, effectiveThreshold, pairPool);

        // Nothing above the threshold, so return before the topology guard is built: constructing it
        // lists the tenant's namespaces, and a namespace with no candidate pair must not pay for a
        // listing it would never consult. Same rule KnowledgeGraph applies to a node with no
        // adjacency. The result is identical to what the loop below would have produced.
        if (pairs.Count == 0)
            return new AutoLinkResult(ns, candidates.Count, 0, 0, 0, false, notScanned);

        int skippedExisting = 0;
        int refusedUnattributable = 0;
        bool hitCap = false;

        // ONE sweep for the whole scan. Every question this loop asks about an id — is the tenant
        // holding it in more than one namespace, does it already carry an attributable edge —
        // resolves through CognitiveIndex, and a guard built per candidate re-lists the tenant's
        // namespaces once per pair on a job that visits every namespace every six hours. The sweep
        // judges each distinct id once and against one snapshot, so two candidates naming the same
        // id can never disagree about it.
        var guard = TopologyGuard.ForSweep(_index, tenantId);

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
        var pending = new List<GraphEdge>();
        var proposed = new HashSet<(string Src, string Dst)>();

        foreach (var (idA, idB, sim) in pairs)
        {
            if (pending.Count >= effectiveCap)
            {
                hitCap = true;
                break;
            }

            // Canonical direction: lex-smaller id is the source. This makes
            // re-scans deterministic — we always try to add the same edge object.
            var (src, dst) = string.CompareOrdinal(idA, idB) < 0 ? (idA, idB) : (idB, idA);

            // At most one edge per unordered pair. The graph REPLACES a same source/target/relation
            // edge rather than appending one, so a pair offered twice would be counted twice and
            // stored once — the precise discrepancy this count exists to rule out.
            if (!proposed.Add((src, dst)))
                continue;

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

            if (HasAnyEdgeBetween(src, dst, tenantId, guard))
            {
                skippedExisting++;
                continue;
            }

            pending.Add(candidate);
        }

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
                ns, created, refusedUnattributable, declinedAtWrite, skippedExisting, pairs.Count,
                hitCap ? " (hit cap)" : "");
        }

        return new AutoLinkResult(ns, candidates.Count, pairs.Count, created, skippedExisting, hitCap, notScanned);
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
    /// </summary>
    private bool HasAnyEdgeBetween(string a, string b, string tenantId, TopologyGuard.Sweep guard)
    {
        // One node's adjacency covers both directions, so there is no need to fetch b's and union.
        var edges = _graph.GetStoredEdgesForEntry(a, tenantId: tenantId);
        foreach (var edge in edges)
        {
            if (!guard.IsEdgeUsable(edge)) continue;
            if ((edge.SourceId == a && edge.TargetId == b) ||
                (edge.SourceId == b && edge.TargetId == a))
                return true;
        }
        return false;
    }
}
