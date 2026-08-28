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
    /// <param name="maxNewEdges">Per-scan edge cap override; pass <c>maxNewEdges: null</c> for the default cap. Required for the same reason as <paramref name="threshold"/>.</param>
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

        // Pull all pairs above threshold up to the cap. The detector's internal
        // limit is `maxResults`; we ask for cap*2 pairs so post-filtering for
        // already-existing edges has slack and we still hit our true cap.
        var pairs = _duplicateDetector.FindDuplicates(candidates, effectiveThreshold, effectiveCap * 2);

        int skippedExisting = 0;
        bool hitCap = false;

        // Proposed first, written once. Two reasons, and both are load-bearing.
        //
        // COST: KnowledgeGraph screens each endpoint against a listing of the tenant's namespaces,
        // and AddEdge builds that listing per call. Adding in a loop therefore re-lists — and so
        // reloads the store — once per candidate edge, on a sweep that runs over every namespace
        // every six hours. AddEdges builds one listing for the whole batch.
        //
        // HONESTY: AddEdges reports what it actually wrote. An endpoint the tenant holds in two
        // namespaces names a node shared with an entry this sweep was never shown, so Core declines
        // it; counting the attempt would put a number in AutoLinkResult.EdgesCreated that no edge in
        // the graph answers to.
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

            if (HasAnyEdgeBetween(src, dst, tenantId))
            {
                skippedExisting++;
                continue;
            }

            pending.Add(new GraphEdge(src, dst, "similar_to", Math.Clamp(sim, 0f, 1f), null, tenantId));
        }

        int created = pending.Count > 0 ? _graph.AddEdges(pending) : 0;
        int refused = pending.Count - created;

        // The log MAY name refusals where a reply may not: this runs as background maintenance with
        // no caller, so the count cannot become an "a twin exists somewhere in your tenant" oracle —
        // and an operator debugging a namespace that stubbornly refuses to densify needs to be able
        // to tell suppression apart from a namespace with nothing similar in it. AutoLinkResult
        // carries no such field for the same reason inverted: it does reach a caller.
        if (created > 0 || refused > 0)
        {
            _logger?.LogInformation(
                "Auto-link scan ns={Namespace}: {Created} new similar_to edges, {Refused} refused (endpoint not attributable to a single entry), {Skipped} skipped (existing edge), {Examined} pairs examined{CapNote}.",
                ns, created, refused, skippedExisting, pairs.Count, hitCap ? " (hit cap)" : "");
        }

        return new AutoLinkResult(ns, candidates.Count, pairs.Count, created, skippedExisting, hitCap, notScanned);
    }

    private bool HasAnyEdgeBetween(string a, string b, string tenantId)
    {
        // GetEdgesForEntry returns both directions for a single entry. Cheaper
        // to scan one entry's edges than fetch both and union.
        var edges = _graph.GetEdgesForEntry(a, tenantId: tenantId);
        foreach (var edge in edges)
        {
            if ((edge.SourceId == a && edge.TargetId == b) ||
                (edge.SourceId == b && edge.TargetId == a))
                return true;
        }
        return false;
    }
}
