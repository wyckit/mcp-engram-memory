using System.Collections.Concurrent;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Retrieval;
using Microsoft.Extensions.Logging;

namespace McpEngramMemory.Core.Services.Graph;

/// <summary>
/// The deferred pair source an auto-link scan draws from —
/// <c>DuplicateDetector.StreamDuplicates</c> in every production path.
///
/// It has no OUTPUT bound by design: the scan filters what it is handed, so a source that could
/// only be asked for a fixed number of pairs would hand back the same window on every deterministic
/// rescan and never reach what stands behind it. What it does take is a
/// <see cref="PairScanWindow"/> — a bound on COMPARISONS, which is a different thing entirely,
/// because successive windows tile the pair space instead of repeating one prefix of it.
///
/// It yields only pairs that CLEAR the threshold. A consumer counting the loop body therefore counts
/// hits and not comparisons, which is why the comparison count is derived from the window instead
/// (see <see cref="AutoLinkScanner"/>) and why the two are reported separately.
///
/// ANCHOR-MAJOR, as a cost hint and not as a correctness precondition. Both detector paths walk one
/// anchor's row at a time and yield the anchor as <c>IdA</c>, so a consumer memoizing anything keyed
/// on <c>IdA</c> gets one miss per row rather than one per pair. A source that violates it — a test
/// seam scripting an arbitrary order — must still get the same ANSWERS out of the scan: the only
/// thing this property buys is that a lookup hits, and the scan's use of it re-reads on a miss.
/// </summary>
internal delegate IEnumerable<(string IdA, string IdB, float Similarity)> PairStream(
    IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates,
    float threshold,
    PairScanWindow window,
    CancellationToken cancellationToken);

/// <summary>
/// What one scan's pair loop actually cost, for the tests that have to state properties
/// <see cref="AutoLinkResult"/> cannot carry — because they are properties of the implementation
/// rather than of the scan, and because none of them is visible in the result, in the graph or
/// in a timing until it is large enough to take the process down.
///
/// IT MUST COVER EVERY STRUCTURE THE LOOP RETAINS, not one of them. The previous version reported
/// the ranking buffer only, and the loop's LARGEST retained structure — the neighbour memo — sat
/// beside it unmeasured while a test asserted the buffer was small. A witness with a blind spot is
/// how the retention defect this seam exists for came back.
///
/// <see cref="PairsAboveThreshold"/> is how many pairs the stream yielded, which is how many cleared
/// the similarity threshold. It is NOT the comparison count: the stream filters before it yields, so
/// in a steady-state namespace this is three to five orders of magnitude below the work done. The
/// work is <see cref="AutoLinkResult.PairsExamined"/>, derived from the window.
///
/// <see cref="Retained"/> is how many candidates the ranking buffer still held when the loop ended.
/// The retention that mattered was a set keyed by PAIR, which is quadratic in the namespace; this
/// number must track the cap instead.
///
/// <see cref="NeighborNodesMemoized"/> and <see cref="NeighborIdsRetained"/> are the other half of
/// that claim: how many nodes' adjacency the neighbour memo still held, and how many neighbour ids
/// that was. The memo was keyed by pair endpoint and grew toward one set per candidate, each sized
/// by that node's degree — O(candidates x degree) held for a whole scan, largest in exactly the
/// densified steady state auto-link produces. It holds ONE node now, so the node count is the
/// structural claim and the id count is the memory one.
///
/// <see cref="AdjacencyReads"/> is how many times the scan read a node's adjacency out of the graph.
/// Each one takes <c>ReaderWriterLockSlim.EnterUpgradeableReadLock</c>, which admits a single holder
/// at a time process-wide, so this is the count that decides whether a background sweep serializes
/// against interactive graph reads. It must scale with the anchors walked, never with the pairs.
///
/// <see cref="EdgesMaterialized"/> is how many <see cref="GraphEdge"/> objects the scan constructed.
/// It must equal what the scan OFFERS the graph, never what it examined: an edge and its metadata
/// dictionary cost ~146 bytes, and the loop used to build one per ADMISSIBLE pair — which in a
/// namespace of near-duplicate memories, the state this store actually reaches, is very nearly every
/// pair compared. At the default window that worst case is roughly eighteen million objects in one
/// background pass. A count alone could not have caught that, which is why the seam reports this
/// beside <see cref="PairsAboveThreshold"/>.
///
/// THE WITNESS IS THE SINGLE CONSTRUCTION SITE. This number is incremented where the scan builds
/// its edges, so a <c>new GraphEdge(...)</c> written anywhere else in the scanner would be
/// invisible here. That is why there is exactly one such site, and why it has to stay that way.
///
/// <see cref="EdgeCapApplied"/> is the cap after clamping. The cap sizes the ranking buffer and the
/// pending-edge list, so it is a memory bound arriving from a public parameter; reporting what was
/// actually applied is what lets a test state that the bound cannot be defeated by the caller.
/// </summary>
internal readonly record struct AutoLinkScanProbe(
    int PairsAboveThreshold,
    int Retained,
    int NeighborNodesMemoized,
    int NeighborIdsRetained,
    int AdjacencyReads,
    int EdgesMaterialized,
    int EdgeCapApplied);

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
/// That loop walks the whole window, so what it costs PER PAIR is the number that scales. It ranks
/// endpoints and a similarity — a value tuple — and constructs a <see cref="GraphEdge"/> only for
/// the candidates the cap will actually spend. Ranking the objects instead cost an edge and its
/// metadata dictionary per ADMISSIBLE pair; the default window examines roughly eighteen million
/// pairs in one pass, and in a namespace of near-duplicate memories — the state a memory store
/// reaches on its own, and the reason a 0.85 threshold finds anything at all — very nearly all of
/// them are admissible.
///
/// WHAT THE SCAN REPORTS AS ITS COST is the pairs it EXAMINED, which is the window's pair slots and
/// is derived from the window rather than counted in the loop. The pair stream yields only what
/// clears the threshold, so a counter in the loop body counts hits; it carried the name "pairs
/// examined" for several rounds while reporting a number that in steady state is three to five
/// orders of magnitude smaller, and that does not even move monotonically with the work done. Both
/// are reported now, separately and under their own names: see <see cref="AutoLinkResult"/>.
///
/// One scan per (tenant, namespace) runs at a time. This is a singleton reachable from both the
/// background sweep and the tool, and the resume cursor is read, advanced and written as three
/// steps, so two overlapping scans could roll progress backwards onto a window that had already
/// been paid for — and would meanwhile be running the same quadratic walk twice. The second caller
/// is turned away rather than queued, and says so: see
/// <see cref="AutoLinkResult.ScanAlreadyInProgress"/>.
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
    /// Edges one scan proposes when the caller names no cap.
    /// </summary>
    public const int DefaultMaxNewEdgesPerScan = 1_000;

    /// <summary>
    /// Ceiling on the per-scan edge cap, applied to whatever the caller asked for.
    ///
    /// The cap is a MEMORY bound, not just a policy: it sizes the ranking buffer (2x + slack value
    /// tuples) and the pending-edge list (one <see cref="GraphEdge"/> and its metadata dictionary,
    /// ~146 bytes each), and <see cref="KnowledgeGraph.AddEdges"/> builds an admitted list of the
    /// same size under its write lock. So "the buffer bounds memory at O(cap) rather than O(pairs
    /// walked)" is only a bound while the cap is bounded — and it arrives unvalidated from three
    /// public surfaces: <see cref="Scan"/>, the auto-link tool, and
    /// <see cref="DecayConfig.AutoLinkMaxNewEdgesPerScan"/>, a settable int the background sweep
    /// reads straight through. At <c>int.MaxValue</c> the buffer could never reach its own capacity,
    /// so compaction never ran and the buffer became a scan-wide set sized by pairs walked: the
    /// round-6 defect, reached again through a parameter.
    ///
    /// Ten times the default and equal to <see cref="DefaultMaxScanEntries"/>, deliberately: no
    /// single pass should propose more edges than the widest namespace it will look at has entries,
    /// and what a cap defers is not lost — the next scan picks it up. At this value the ranking
    /// buffer is ~480 KB and the pending list ~1.5 MB, both bounded and both far below anything a
    /// six-hourly background job needs to apologise for. Clamping is LOGGED, never silent, on the
    /// same reasoning as entry truncation: a scan that quietly did less than it was asked looks
    /// identical to one that found nothing.
    /// </summary>
    public const int MaxNewEdgesPerScanHardCap = DefaultMaxScanEntries;

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
    /// bounds is MEMORY, at O(cap) rather than O(pairs walked) — and the cap it is O(of) is the
    /// CLAMPED one, because an unclamped cap is what turned that bound back into O(pairs walked).
    /// See <see cref="MaxNewEdgesPerScanHardCap"/>.
    /// </summary>
    private const int RankingBufferSlack = 8;

    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly PairStream _pairs;
    private readonly Action<AutoLinkScanProbe>? _onScanProbe;
    private readonly ILogger<AutoLinkScanner>? _logger;

    /// <summary>
    /// The (tenant, namespace) keys a scan is currently inside. Presence is the lock.
    ///
    /// This type is a SINGLETON reachable from two places at once — the six-hourly
    /// <see cref="AutoLinkBackgroundService"/> and the <c>auto_link</c> tool — over a resume cursor
    /// that is read, advanced and written as three separate steps. Two scans of one key could
    /// therefore read the same start, and the slower one would finish last and overwrite the faster
    /// one's cursor with an older value: observed progress running 0, 0, 1, 1 while an intervening
    /// scan had already advanced to 2. That is the starvation this whole area keeps rediscovering,
    /// arriving by a third road — a window the rotation has stepped back over is a window the next
    /// scan pays for again.
    ///
    /// Mutual exclusion rather than a versioned cursor, because the cursor is only half the cost. A
    /// version stamp would keep progress monotonic and still let two scans run the same quadratic
    /// window at the same time, which is the more expensive half of the defect.
    ///
    /// TRY-ACQUIRE, NEVER WAIT, and the second caller is told so rather than served a lie. Blocking
    /// would put an interactive tool call behind a background sweep that can run for seconds and
    /// then have it redo a window that was just done. It is also what makes this deadlock-proof by
    /// construction: the key is taken before any graph or index lock and released after all of them,
    /// so it is strictly outermost, and a re-entrant call cannot self-deadlock because a
    /// try-acquire on a key already held simply fails. <see cref="AutoLinkResult.ScanAlreadyInProgress"/>
    /// carries the outcome, and <see cref="AutoLinkResult.PairScanIncomplete"/> is set with it so a
    /// caller reading only the completeness flags can never mistake a deferred scan for "nothing
    /// left to link".
    /// </summary>
    private readonly ConcurrentDictionary<(string Tenant, string Namespace), byte> _scansInFlight = new();

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
    ///
    /// A ConcurrentDictionary makes each entry's write atomic, which was never the problem: the
    /// unsafe step is READ, advance, WRITE, and only one scan per key being in flight at a time
    /// makes those three one step. See <see cref="_scansInFlight"/>.
    ///
    /// WHAT REMOVES AN ENTRY, because for several rounds nothing did and this dictionary was
    /// monotonic over the lifetime of a process designed to run for weeks. Two paths, both inside
    /// the scan, because no teardown anywhere else reaches this type: <c>DeleteAllInNamespace</c>,
    /// <c>NamespaceStore.RemoveNamespace</c> and <c>MemoryDiffusionKernel.Invalidate</c> all retract
    /// their own per-partition state and none of them knows this exists.
    ///
    /// - A namespace that no longer holds two scannable entries has no pair space to be part-way
    ///   through, so its cursor describes nothing and is dropped on the way out of the early return.
    /// - <see cref="ForgetCursorsForDeadNamespaces"/> drops the cursors of namespaces the tenant no
    ///   longer has at all. That is the case the first path cannot reach: a deleted namespace is
    ///   never scanned again, so it would never come back to have its own cursor removed. Debate
    ///   namespaces make it concrete — <c>active-debate-{sessionId}</c>, one per session, deleted on
    ///   a TTL by <c>purge_debates</c>, and none of them starting with '_' so the sweep scans every
    ///   one of them.
    ///
    /// Together those bound the dictionary by the namespaces that currently hold a pair worth
    /// scanning, rather than by every (tenant, namespace) ever scanned.
    /// </summary>
    private readonly ConcurrentDictionary<(string Tenant, string Namespace), int> _resumeAnchors = new();

    /// <summary>
    /// How many resume cursors are held. For the test that has to state that this dictionary
    /// SHRINKS — a property invisible in the result, in the graph and in the timings until it is
    /// large enough to matter, exactly like the numbers on <see cref="AutoLinkScanProbe"/>.
    /// </summary>
    internal int ResumeCursorCount => _resumeAnchors.Count;

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
    /// <paramref name="onScanProbe"/> is handed one <see cref="AutoLinkScanProbe"/> per scan: every
    /// structure the pair loop still held when it ended, how much of the graph it read to get there,
    /// how many edge objects it built, and the cap those bounds derive from. All of them are cost
    /// properties, and a cost property is invisible in the result, in the graph and in the timings
    /// until it is large enough to take the process down with it.
    /// </summary>
    internal AutoLinkScanner(
        CognitiveIndex index,
        KnowledgeGraph graph,
        DuplicateDetector duplicateDetector,
        PairStream? pairs,
        ILogger<AutoLinkScanner>? logger = null,
        Action<AutoLinkScanProbe>? onScanProbe = null)
    {
        _index = index;
        _graph = graph;
        _pairs = pairs ?? duplicateDetector.StreamDuplicates;
        _logger = logger;
        _onScanProbe = onScanProbe;
    }

    /// <summary>
    /// Scan a single namespace and add <c>similar_to</c> edges for high-cosine
    /// pairs that don't already have any edge between them.
    /// </summary>
    /// <param name="ns">Namespace to scan.</param>
    /// <param name="threshold">Similarity threshold override; pass <c>threshold: null</c> to use the namespace's <see cref="DecayConfig.AutoLinkSimilarityThreshold"/>. Required so tenantId never sits behind a nullable slot an old positional call could silently shift into.</param>
    /// <param name="maxNewEdges">Per-scan cap on edges the graph ACCEPTS, not on candidates offered; pass <c>maxNewEdges: null</c> for <see cref="DefaultMaxNewEdgesPerScan"/>. Required for the same reason as <paramref name="threshold"/>. Clamped into [0, <see cref="MaxNewEdgesPerScanHardCap"/>] and logged when it is — the cap sizes this scan's ranking buffer and pending-edge list, so it is a memory bound and cannot be left to the caller.</param>
    /// <param name="tenantId">Tenant partition to scan. Pass "" for the legacy partition.</param>
    /// <param name="maxScanEntries">Upper bound on entries fed to the quadratic pairwise stage in one pass; 0 disables it. Anything skipped is reported in the result.</param>
    /// <param name="maxPairComparisons">Cosine comparisons this pass will make before deferring the rest to the next scan, which resumes where this one stopped; 0 or less disables the bound. That a pass stopped early is reported as <see cref="AutoLinkResult.PairScanIncomplete"/>, and what it spent — in this same unit — as <see cref="AutoLinkResult.PairsExamined"/>.</param>
    /// <param name="cancellationToken">Stops the pairwise walk between anchors. A cancelled scan writes what it already ranked and leaves its resume cursor untouched, so the window it abandoned is the window the next scan starts on.</param>
    public AutoLinkResult Scan(string ns, float? threshold, int? maxNewEdges,
        string tenantId, int maxScanEntries = DefaultMaxScanEntries,
        long maxPairComparisons = DefaultMaxPairComparisons,
        CancellationToken cancellationToken = default)
    {
        // One scan per (tenant, namespace) at a time — see _scansInFlight for why, and for why the
        // loser is turned away instead of queued. Acquired around the WHOLE scan because the thing
        // being protected is not the cursor write, it is read-compute-write as one step.
        var key = (tenantId, ns);
        if (!_scansInFlight.TryAdd(key, 0))
        {
            // Honest, and honest in the caller's own terms: nothing was examined and nothing was
            // written, so the completeness flag says the pair space was not covered. A caller that
            // reads only EdgesCreated sees the same 0 an exhausted namespace produces, which is why
            // the deferral gets a field of its own rather than being inferred from the counts.
            _logger?.LogInformation(
                "Auto-link scan ns={Namespace} deferred: a scan of this namespace is already in flight.", ns);
            return new AutoLinkResult(ns, 0, 0, 0, 0, false,
                EntriesNotScanned: 0, PairScanIncomplete: true, ScanAlreadyInProgress: true);
        }

        try
        {
            return ScanExclusive(ns, threshold, maxNewEdges, tenantId, maxScanEntries,
                maxPairComparisons, cancellationToken);
        }
        finally
        {
            // In a finally, because a scan that threw must not wedge its namespace shut for the
            // lifetime of the process: the background service catches per-namespace failures and
            // carries on, and the next sweep has to be able to try again.
            _scansInFlight.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// The scan itself, entered only by the holder of this (tenant, namespace) key. Everything about
    /// the resume cursor below — reading the start anchor, deciding whether to advance, writing the
    /// new one — is safe to treat as a single step because of that exclusion and for no other
    /// reason.
    /// </summary>
    private AutoLinkResult ScanExclusive(string ns, float? threshold, int? maxNewEdges,
        string tenantId, int maxScanEntries,
        long maxPairComparisons,
        CancellationToken cancellationToken)
    {
        // Retraction first, and for this tenant only: the sweep visits every tenant every cycle, so
        // scoping it to the one being scanned still reconciles the whole dictionary once per cycle
        // while costing a scan nothing it does not already pay. See _resumeAnchors.
        ForgetCursorsForDeadNamespaces(tenantId);

        var entries = _index.GetAllInNamespace(ns, tenantId: tenantId);
        var nonSummary = new List<CognitiveEntry>(entries.Count);
        foreach (var e in entries)
            if (!e.IsSummaryNode && e.Vector.Length > 0) nonSummary.Add(e);

        if (nonSummary.Count < 2)
        {
            // Fewer than two scannable entries: there is no pair space for a cursor to point into,
            // so a cursor here is dead state. One TryRemove on a path that is already returning.
            _resumeAnchors.TryRemove((tenantId, ns), out _);
            return new AutoLinkResult(ns, nonSummary.Count, 0, 0, 0, false);
        }

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
        {
            // Same reasoning as above: no pair space, so no cursor. Reached separately because a
            // namespace can hold entries that are all zero-norm and never become candidates.
            _resumeAnchors.TryRemove((tenantId, ns), out _);
            return new AutoLinkResult(ns, candidates.Count, 0, 0, 0, false, notScanned);
        }

        float effectiveThreshold = threshold ?? 0.85f;

        // CLAMPED RATHER THAN TRUSTED, because this number is a memory bound and it arrives from a
        // public parameter with no validation anywhere on the path. See MaxNewEdgesPerScanHardCap
        // for why an unclamped cap put a scan-wide set sized by pairs walked back into the loop.
        // The clamp is what lets rankingCapacity below be a plain multiplication: at the hard cap it
        // is 20,008, so there is no saturating branch to get wrong and none to leave untested.
        int requestedCap = maxNewEdges ?? DefaultMaxNewEdgesPerScan;
        int effectiveCap = Math.Clamp(requestedCap, 0, MaxNewEdgesPerScanHardCap);
        if (effectiveCap != requestedCap)
        {
            _logger?.LogWarning(
                "Auto-link scan for ns={Namespace} clamped its per-scan edge cap from {Requested} to {Cap}. " +
                "The cap sizes this scan's ranking buffer and its pending-edge list, so it bounds memory; " +
                "edges it defers are picked up by the next scan.",
                ns, requestedCap, effectiveCap);
        }
        int spendable = effectiveCap;

        // How many of the best admissible candidates to keep while ranking.
        int rankingCapacity = (effectiveCap * 2) + RankingBufferSlack;

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

        // WHAT THIS PASS COSTS, in the unit its budget is spent in — derived from the window, not
        // counted in the loop, because the loop cannot see a comparison. The pair stream filters
        // before it yields (see PairStream), so a counter in the loop body counts pairs that CLEARED
        // the threshold. That number carried the name "pairs examined" through several rounds while
        // being, in a steady-state namespace, three to five orders of magnitude below the work done
        // — and it does not even move monotonically with that work, since a namespace of
        // near-duplicates reports a large number for the same walk that reports a tiny one when
        // nothing matches. Both numbers are reported now, each under its own name.
        long pairsExamined = PairSlotsInWindow(candidates.Count, startAnchor, maxAnchors);

        int pairsAboveThreshold = 0;
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
        //
        // ENDPOINTS AND A SIMILARITY, not an edge. Ranking decides which candidates are worth
        // WRITING, and a candidate that loses the ranking is a candidate whose GraphEdge would be
        // built and thrown away — object plus metadata dictionary, ~146 bytes, once per ADMISSIBLE
        // pair rather than once per edge kept. The loop walks its whole window now, and the default
        // window walks ~18 million pairs; in a namespace of near-duplicate memories nearly all of
        // them are admissible, so the worst case really is ~18 million objects and ~2.6 GB of churn
        // in one pass of a six-hourly background job with no one to report it to. The tuple is a
        // value type: the buffer holds two string references and a float per candidate and allocates
        // nothing per pair.
        var ranked = new List<(string Source, string Target, float Similarity)>();

        // ONE node's neighbours at a time — see NeighborMemo for why this may not be a dictionary.
        var neighbors = new NeighborMemo();

        // Counted at the one construction site below. See AutoLinkScanProbe: this is the number the
        // memory property is actually about, and a retention count alone could not express it.
        int edgesMaterialized = 0;

        // ONE pairwise pass. Re-asking a fixed-size detector with a larger number would have been
        // the other way to reach the pairs behind an ineligible run, and it would repeat the whole
        // quadratic comparison per attempt, on a namespace whose steady state — every neighbor
        // already linked — is precisely the case that forces the most attempts.
        foreach (var (idA, idB, sim) in _pairs(candidates, effectiveThreshold,
                     new PairScanWindow(startAnchor, maxAnchors), cancellationToken))
        {
            pairsAboveThreshold++;

            // Canonical direction: lex-smaller id is the source. This makes
            // re-scans deterministic — we always try to add the same edge.
            var (src, dst) = string.CompareOrdinal(idA, idB) < 0 ? (idA, idB) : (idB, idA);

            guard ??= TopologyGuard.ForSweep(_index, tenantId);

            // Screened as the EDGE that would be written — both endpoints, the same predicate
            // AddEdges will apply — and BEFORE the slot is taken; that ordering is the fix. A
            // candidate the graph is going to decline must not spend a cap the next candidate could
            // have used. It is asked of the endpoints rather than of a constructed GraphEdge for
            // the reason above the buffer: the overwhelming majority of pairs walked never become
            // an edge, and building one to ask the question pays for every one of them.
            //
            // Ahead of the existing-edge probe, not after it, and that is load-bearing beyond cost:
            // EdgesSkippedExisting reaches a caller. Probing first would let an unattributable pair
            // land in that count whenever a hidden edge happens to run between the two ids, and the
            // count would then answer a question about a node the caller was never shown.
            if (!guard.IsEdgeUsable(src, dst))
            {
                refusedUnattributable++;
                continue;
            }

            // Asked of the pair as the STREAM presented it, not of the canonical endpoints: the
            // memo holds one node and the stream is anchor-major, so idA is the id every pair of
            // this row shares. Canonicalizing first would key the memo on min(idA, idB), which
            // changes within a single row and is what made the old memo grow toward one entry per
            // candidate. The question is unaffected — one node's adjacency covers both directions.
            if (HasAnyEdgeBetween(idA, idB, tenantId, guard, neighbors))
            {
                skippedExisting++;
                continue;
            }

            // Counted rather than inferred from the buffer's size, because the buffer now discards
            // its losers as it fills. HitMaxEdgeCap asks how many admissible candidates EXISTED, and
            // that question outlived the buffer that used to answer it.
            admissibleSeen++;

            // Ranked on the raw cosine rather than on the weight a GraphEdge would carry: that
            // constructor clamps into [0,1] to keep a stored weight sane against float drift, and
            // two candidates that both drifted above 1 would otherwise rank as a tie they are not.
            ranked.Add((src, dst, sim));
            if (ranked.Count >= rankingCapacity)
                KeepBest(ranked, spendable);
        }

        // Bounded by the cap and not by the pairs walked. Captured here, where the loop ends,
        // and reported below with the rest of the probe.
        int retained = ranked.Count;

        // The cap was binding only if it left an admissible candidate unwritten. Stated over how
        // many admissible candidates the scan SAW, which is exact: it does not depend on how large
        // the ranking buffer is, and it stays true now that the buffer drops candidates it has
        // outranked. A caller reading HitMaxEdgeCap false learns that every admissible candidate in
        // this scan's window was written — and PairScanIncomplete tells it whether that window was
        // the whole namespace.
        //
        // Stated over the CLAMPED cap, which is the one the scan actually spent. Comparing against
        // what the caller asked for would report "the cap did not bind" on a scan the ceiling had
        // just bound, and the cursor rule below reads this flag: the window would then be advanced
        // past edges it still owed.
        bool hitCap = admissibleSeen > effectiveCap;

        // Highest similarity first, ties broken on the canonical endpoints. The stream arrives in
        // scan order, so the ranking is imposed here; the tiebreak is what keeps two equally-similar
        // candidates from swapping places between rescans and re-deciding which one the cap buys.
        KeepBest(ranked, spendable);

        // THE ONE PLACE THIS SCAN CONSTRUCTS A GraphEdge, and the count that witnesses it. Only the
        // candidates the cap will actually spend get an object; everything the loop examined and
        // everything the ranking discarded cost a value tuple and nothing else. A construction site
        // added anywhere else in this file would be invisible to AutoLinkScanProbe.EdgesMaterialized
        // and would silently reopen the per-pair allocation this replaces, so there must not be one.
        //
        // The weight is the raw cosine; GraphEdge's constructor clamps it into [0,1].
        var pending = new List<GraphEdge>(Math.Min(spendable, ranked.Count));
        for (int i = 0; i < ranked.Count && pending.Count < spendable; i++)
        {
            var (src, dst, sim) = ranked[i];
            edgesMaterialized++;
            pending.Add(new GraphEdge(src, dst, "similar_to", sim, null, tenantId));
        }

        _onScanProbe?.Invoke(new AutoLinkScanProbe(
            pairsAboveThreshold, retained,
            neighbors.NodesHeld, neighbors.Neighbors.Count, neighbors.Reads,
            edgesMaterialized, effectiveCap));

        // OnlyIfUnlinked, because "these two are not related yet" is the one precondition this
        // scanner cannot establish for itself. HasAnyEdgeBetween above reads a snapshot of one
        // node's adjacency and holds it for as long as that node keeps arriving; a manual relation
        // created after that read is invisible to it, and the default write boundary replaces only
        // the SAME relation, so the pair would end up carrying both the manual edge and a derived
        // similar_to. The graph re-tests the condition under its own write lock, where it is atomic
        // with the write.
        int created = pending.Count > 0 ? _graph.AddEdges(pending, EdgeAddMode.OnlyIfUnlinked) : 0;

        // Three causes, kept together because all three mean the same thing to a caller — the scan
        // judged a candidate admissible and the graph disagreed at the instant of writing. An
        // endpoint became ambiguous between the scanner's sweep and the one inside AddEdges; or
        // attribution moved anywhere in the tenant between that sweep and the graph's write lock, in
        // which case AddEdges refuses the whole batch and this equals pending.Count; or a relation
        // appeared between the endpoints after this scan read their adjacency. All three must fail
        // closed, and none is an error; a non-zero value here means a race, not a bad candidate.
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
            // Both numbers, and neither standing in for the other. The sentence used to read "{n}
            // pairs examined over anchors ..." off the yield counter, asserting that a count of
            // above-threshold HITS was the work done over that anchor range — the sentence an
            // operator reads when a six-hourly sweep is slow and they are deciding whether the
            // pairwise stage is worth tuning.
            _logger?.LogInformation(
                "Auto-link scan ns={Namespace}: {Created} new similar_to edges, {Refused} refused (endpoint not attributable to a single entry), {Declined} declined at write (endpoint became ambiguous, or the pair was linked, mid-scan), {Skipped} skipped (existing edge), {AboveThreshold} pairs above threshold out of {Examined} examined over anchors {Start}..+{Anchors} of {Total}{CapNote}{BudgetNote}.",
                ns, created, refusedUnattributable, declinedAtWrite, skippedExisting,
                pairsAboveThreshold, pairsExamined,
                startAnchor, maxAnchors, candidates.Count,
                hitCap ? " (hit cap)" : "",
                pairScanIncomplete ? " (pair scan incomplete; next scan resumes where this one stopped)" : "");
        }

        return new AutoLinkResult(ns, candidates.Count, pairsExamined, created, skippedExisting,
            hitCap, notScanned, pairScanIncomplete, PairsAboveThreshold: pairsAboveThreshold);
    }

    /// <summary>
    /// The pair slots the anchors of one window own, computed rather than walked.
    ///
    /// Anchor <c>i</c> owns exactly the pairs <c>(i, j)</c> for <c>j &gt; i</c> — the triangular
    /// walk both detector paths perform, and the reason a window can be resumed at all — so a
    /// window's cost is the sum of <c>count - 1 - i</c> over the anchors it visits. The anchors of a
    /// window are consecutive and wrap at most once, so that is one or two arithmetic series and
    /// there is no reason to iterate.
    ///
    /// EXACT for the direct pairwise path: every slot there is one FP32 cosine. Above the spectral
    /// pivot the same slots are walked and most are settled by a cheaper projection dot, so it is an
    /// upper bound on the full comparisons and an exact count of the slots — which is the unit
    /// <c>maxPairComparisons</c> is spent in on either path. A slot whose two vectors differ in
    /// length is visited and not compared; the index does not produce mixed-dimension namespaces,
    /// and over-reporting the cost of one is the safe direction.
    /// </summary>
    private static long PairSlotsInWindow(int count, int startAnchor, int anchors)
    {
        int rows = Math.Min(anchors, count);
        if (rows <= 0 || count <= 0) return 0;
        int firstRun = Math.Min(rows, count - startAnchor);
        return SlotsInAnchorRun(count, startAnchor, firstRun)
             + SlotsInAnchorRun(count, 0, rows - firstRun);
    }

    /// <summary>
    /// Pair slots owned by <paramref name="anchors"/> consecutive anchors starting at
    /// <paramref name="first"/>, which is the arithmetic series from the last anchor's slot count
    /// up to the first's.
    /// </summary>
    private static long SlotsInAnchorRun(int count, int first, int anchors)
    {
        if (anchors <= 0) return 0;
        long high = count - 1 - first;             // slots owned by the first anchor of the run
        long low = count - first - anchors;        // ...by the last, at index first + anchors - 1
        return (high + low) * (high - low + 1) / 2;
    }

    /// <summary>
    /// Drop the resume cursors of namespaces <paramref name="tenantId"/> no longer has.
    ///
    /// The retraction this type never had. Nothing tells a DI singleton that a namespace was torn
    /// down — the candidate index, the BM25/HNSW indexes and the diffusion kernel each retract their
    /// own per-partition state and none of them reaches here — and a deleted namespace is never
    /// scanned again, so it can never come back to drop its own cursor. Without this the dictionary
    /// is monotonic in the number of (tenant, namespace) pairs EVER scanned, in a process designed
    /// to run for weeks; a host churning debate namespaces accumulates dead keys forever.
    ///
    /// Scoped to the tenant being scanned so a scan pays only for its own partition, which is enough
    /// to reconcile everything: the sweep visits every tenant every cycle, so a dead cursor outlives
    /// its namespace by at most one sweep. The listing is skipped entirely when this tenant holds no
    /// cursors, so a first scan pays nothing.
    ///
    /// Over-removal is harmless by this cursor's own contract — losing one costs a repeat of one
    /// window, never a pair no scan reaches — which is why a listing that raced a namespace's
    /// creation could not do damage even if it lost.
    /// </summary>
    private void ForgetCursorsForDeadNamespaces(string tenantId)
    {
        if (_resumeAnchors.IsEmpty) return;

        HashSet<string>? live = null;
        foreach (var (key, _) in _resumeAnchors)
        {
            if (!string.Equals(key.Tenant, tenantId, StringComparison.Ordinal)) continue;
            // Built on the first cursor this tenant owns and not before: GetNamespaces materializes
            // every persisted partition, and a scan with nothing to reconcile must not pay for it.
            live ??= new HashSet<string>(_index.GetNamespaces(tenantId), StringComparer.Ordinal);
            if (!live.Contains(key.Namespace))
                _resumeAnchors.TryRemove(key, out _);
        }
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
    private static void KeepBest(List<(string Source, string Target, float Similarity)> ranked, int keep)
    {
        ranked.Sort(static (x, y) =>
        {
            int bySimilarity = y.Similarity.CompareTo(x.Similarity);
            if (bySimilarity != 0) return bySimilarity;
            int bySource = string.CompareOrdinal(x.Source, y.Source);
            return bySource != 0 ? bySource : string.CompareOrdinal(x.Target, y.Target);
        });
        if (ranked.Count > keep)
            ranked.RemoveRange(keep, ranked.Count - keep);
    }

    /// <summary>
    /// True when an ATTRIBUTABLE edge already ran between the two ids — in either direction and
    /// under any relation — as of when this scan last read <paramref name="anchor"/>'s adjacency.
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
    /// <paramref name="memo"/> holds <paramref name="anchor"/>'s attributable neighbor ids, on the
    /// same reasoning as the sweep it is passed alongside: the scan examines every above-threshold
    /// pair in its window rather than a fixed number of them, and one id takes part in as many pairs
    /// as its row is wide, so reading its adjacency per pair would take the graph's lock a quadratic
    /// number of times. It holds ONE node — see <see cref="NeighborMemo"/> for why it may not hold
    /// more — so this must be called with the id the pair stream put first.
    /// </summary>
    private bool HasAnyEdgeBetween(string anchor, string other, string tenantId,
        TopologyGuard.Sweep guard, NeighborMemo memo)
    {
        if (!string.Equals(memo.Node, anchor, StringComparison.Ordinal))
        {
            // Disowned BEFORE the set is touched and re-owned only once it is complete, so a throw
            // out of the read below leaves the memo describing nothing rather than answering for
            // the previous node out of a set that now holds part of this one's.
            memo.Node = null;

            // Cleared and refilled rather than replaced: the set keeps its capacity across a row
            // change, so a scan allocates one set however many anchors it walks, and a degree-0 node
            // costs nothing at all. The old memo allocated an empty set per such node AND kept it.
            memo.Neighbors.Clear();
            memo.Reads++;
            // One node's adjacency covers both directions, so there is no need to fetch the other's
            // and union.
            foreach (var edge in _graph.GetStoredEdgesForEntry(anchor, tenantId: tenantId))
            {
                if (!guard.IsEdgeUsable(edge)) continue;
                if (edge.SourceId == anchor) memo.Neighbors.Add(edge.TargetId);
                else if (edge.TargetId == anchor) memo.Neighbors.Add(edge.SourceId);
            }
            memo.Node = anchor;
        }
        return memo.Neighbors.Contains(other);
    }

    /// <summary>
    /// ONE node's attributable neighbours, and the reason it may not be more than one.
    ///
    /// This was a <c>Dictionary&lt;string, HashSet&lt;string&gt;&gt;</c> keyed by the pair's
    /// canonical (lex-smaller) endpoint, and it was the scan's LARGEST retained structure. Keying by
    /// id order rather than by walk position means the key changes within a single anchor row, so
    /// the dictionary grew toward one set per candidate in the namespace, each sized by that node's
    /// degree: O(candidates x degree) — the edges of the window's induced subgraph — held for the
    /// whole scan, and largest in precisely the densified steady state this scanner exists to
    /// produce. At 10,000 candidates of average degree 200 that is 2,000,000 retained slots, ~80 MB
    /// live in a six-hourly background job, growing with every scan that succeeds. It is the round-6
    /// finding's exact shape — a scan-wide retained set, quadratic in the namespace — reintroduced
    /// by the fix for it, and invisible to the probe installed to catch that class of defect.
    ///
    /// One slot, keyed on the id the pair stream puts FIRST. Both detector paths are anchor-major
    /// (see <see cref="PairStream"/>): a row's pairs all carry their anchor as IdA, so one slot is
    /// hit by every pair of the row and retention falls to O(one node's degree). The graph read
    /// count falls with it — one per anchor row instead of one per distinct lex-min id — and each
    /// of those reads takes <c>EnterUpgradeableReadLock</c>, which admits one holder at a time
    /// across the whole process.
    ///
    /// A stream that is NOT anchor-major stays CORRECT and only pays. A miss re-reads the graph, and
    /// <see cref="HasAnyEdgeBetween"/> is a cost filter rather than the authority — the condition is
    /// finally decided by <see cref="EdgeAddMode.OnlyIfUnlinked"/> under the graph's write lock — so
    /// a re-read can never reach a different decision than a hit would have.
    /// </summary>
    private sealed class NeighborMemo
    {
        /// <summary>The node whose neighbours are held, or null before the first read.</summary>
        internal string? Node;

        /// <summary><see cref="Node"/>'s attributable neighbour ids.</summary>
        internal readonly HashSet<string> Neighbors = new(StringComparer.Ordinal);

        /// <summary>Adjacency reads performed — one graph-lock acquisition each.</summary>
        internal int Reads;

        /// <summary>
        /// Nodes memoized: 0 or 1 by construction, and reported so a test can state that. A
        /// dictionary keyed by pair endpoint reports thousands here on the same fixture.
        /// </summary>
        internal int NodesHeld => Node is null ? 0 : 1;
    }
}
