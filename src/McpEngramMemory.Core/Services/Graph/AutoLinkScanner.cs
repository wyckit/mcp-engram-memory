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
/// </summary>
public sealed class AutoLinkScanner
{
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
    /// <param name="threshold">Optional override for the similarity threshold; defaults to the namespace's <see cref="DecayConfig.AutoLinkSimilarityThreshold"/>.</param>
    /// <param name="maxNewEdges">Optional override for the per-scan edge cap.</param>
    public AutoLinkResult Scan(string ns, float? threshold = null, int? maxNewEdges = null)
    {
        var entries = _index.GetAllInNamespace(ns);
        var nonSummary = new List<CognitiveEntry>(entries.Count);
        foreach (var e in entries)
            if (!e.IsSummaryNode && e.Vector.Length > 0) nonSummary.Add(e);

        if (nonSummary.Count < 2)
            return new AutoLinkResult(ns, nonSummary.Count, 0, 0, 0, false);

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
            return new AutoLinkResult(ns, candidates.Count, 0, 0, 0, false);

        float effectiveThreshold = threshold ?? 0.85f;
        int effectiveCap = maxNewEdges ?? 1000;

        // Pull all pairs above threshold up to the cap. The detector's internal
        // limit is `maxResults`; we ask for cap*2 pairs so post-filtering for
        // already-existing edges has slack and we still hit our true cap.
        var pairs = _duplicateDetector.FindDuplicates(candidates, effectiveThreshold, effectiveCap * 2);

        int created = 0;
        int skippedExisting = 0;
        bool hitCap = false;

        foreach (var (idA, idB, sim) in pairs)
        {
            if (created >= effectiveCap)
            {
                hitCap = true;
                break;
            }

            // Canonical direction: lex-smaller id is the source. This makes
            // re-scans deterministic — we always try to add the same edge object.
            var (src, dst) = string.CompareOrdinal(idA, idB) < 0 ? (idA, idB) : (idB, idA);

            if (HasAnyEdgeBetween(src, dst))
            {
                skippedExisting++;
                continue;
            }

            _graph.AddEdge(new GraphEdge(src, dst, "similar_to", Math.Clamp(sim, 0f, 1f)));
            created++;
        }

        if (created > 0)
        {
            _logger?.LogInformation(
                "Auto-link scan ns={Namespace}: {Created} new similar_to edges, {Skipped} skipped (existing edge), {Examined} pairs examined{CapNote}.",
                ns, created, skippedExisting, pairs.Count, hitCap ? " (hit cap)" : "");
        }

        return new AutoLinkResult(ns, candidates.Count, pairs.Count, created, skippedExisting, hitCap);
    }

    private bool HasAnyEdgeBetween(string a, string b)
    {
        // GetEdgesForEntry returns both directions for a single entry. Cheaper
        // to scan one entry's edges than fetch both and union.
        var edges = _graph.GetEdgesForEntry(a);
        foreach (var edge in edges)
        {
            if ((edge.SourceId == a && edge.TargetId == b) ||
                (edge.SourceId == b && edge.TargetId == a))
                return true;
        }
        return false;
    }
}
