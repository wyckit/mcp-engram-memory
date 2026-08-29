using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// WHAT THE PER-SCAN CAP COUNTS.
///
/// Auto-link proposes candidate pairs in similarity order and the graph refuses any whose endpoint
/// id names two of the tenant's namespaces — that node is shared with an entry the background sweep
/// was never shown, so it cannot be attributed to either. A previous round made the REPORTED count
/// honest (it reports what the graph wrote, not what the sweep offered). These tests pin the other
/// half: the cap must be spent on writes the graph ACCEPTS.
///
/// Spending a slot on a candidate that will be refused is not a rounding error, it is starvation. A
/// namespace whose highest-ranked pair happens to be unattributable writes nothing under a small
/// cap; the scan is deterministic over unchanged entries, so the next scan ranks the same pair first
/// and writes nothing again. Auto-linking never densifies that namespace and never heals itself.
///
/// Ambiguity here is planted the only way it can occur: the SAME bare id in two namespaces of ONE
/// tenant. Graph adjacency is keyed (tenant, id) with no namespace, so those two entries share one
/// node — the defect this suppression mitigates, tracked as issue #19.
///
/// Every "nothing was written" assertion reads the RAW stored accessors on purpose. The attributable
/// accessors would hide an edge on a shared node whether or not it was ever written, so only the raw
/// view can tell "refused" apart from "written and then filtered out of the reply".
///
/// AND WHERE THE CANDIDATES COME FROM. Screening before the cap is spent is only half of it: the
/// candidates being screened were drawn as a fixed window, so ineligible pairs still starved the
/// viable ones behind them — one window further back. Eleven viable pairs under a cap of one stopped
/// at ten stored edges and stayed there, reporting EdgesCreated 0 with HitMaxEdgeCap false, which is
/// indistinguishable from a namespace with nothing left to link. The scan now draws progressively,
/// so tests 5 to 8 pin both halves of the resulting contract: rescans reach every viable pair
/// however many ineligible ones stand in front of it, and one scan still costs ONE pairwise pass.
///
/// AND WHAT DRAWING PROGRESSIVELY COST. Four things, pinned by tests 9 to 13.
///
/// MEMORY (9): a scan that walks every pair must not remember every pair. The consumer's scan-wide
/// pair set was quadratic in the namespace and bought nothing — the detector already yields each
/// unordered pair once — and it lived in a six-hourly background job, where a multi-gigabyte
/// allocation has no one to report it to.
///
/// WORK (10): a namespace in steady state walked its entire pair space every six hours to find
/// nothing. Bounding that is easy; bounding it WITHOUT recreating the starvation above is the whole
/// difficulty, because a budget that restarts at the first pair every time hides everything past it
/// on every scan, forever. The budget resumes, so successive scans tile the pair space.
///
/// THE RACE (11): the neighbour memo that keeps this scan off the graph lock is a snapshot of a
/// mutable graph, so it can only ever be a cost filter. The condition it tests is enforced where it
/// can be atomic — inside the graph's own write lock.
///
/// RANKING (12): the buffer used to stop the loop, so "the highest-ranked admissible candidates"
/// meant the highest-ranked of the first few — ten pairs at 0.90 ahead of one at 0.99 wrote a 0.90
/// edge. It is a bounded top-K over everything the scan examined now, which costs O(cap) memory and
/// is what the comments already claimed.
///
/// AND THE LEGACY MIRROR (13): none of it may be visible in the pre-tenancy partition.
///
/// AND WHAT THE SCAN SAYS ABOUT ITSELF. Four more, and the first of them is not a bug in the scan
/// but a bug in its report — which is worse, because a wrong number is acted on.
///
/// THE COST (17): the pair stream yields only what clears the threshold, so the counter in the
/// consumer's loop counted neighbours FOUND while being named, serialized and logged as the pairs
/// examined. In steady state those differ by three to five orders of magnitude, and the smaller one
/// does not even move monotonically with the work. It was the only cost number this subsystem
/// published, and the budget it was read against is expressed in comparisons.
///
/// THE OTHER RETAINED STRUCTURE (18): the ranking buffer is not the largest thing the loop holds.
/// The neighbour memo was keyed by pair endpoint and grew toward one set per candidate, sized by
/// degree — the round-6 shape again, invisible to the probe installed to catch it, and empty in
/// exactly the two tests that measure retention.
///
/// THE CAP AS A MEMORY BOUND (19): "O(cap), not O(pairs)" is a bound only while the cap is bounded,
/// and it arrived unvalidated from three public surfaces.
///
/// THE CURSOR THAT NEVER SHRANK (20): one write site, no removal site, on a singleton in a process
/// that runs for weeks.
/// </summary>
public class AutoLinkCapAccountingTests : IDisposable
{
    // A real tenant rather than the legacy "" partition: the guard's namespace listing, the
    // candidate index and the scan are all tenant-scoped, and "" takes a separate fast path.
    private const string Tenant = "acme";
    private const string ScanNs = "target";

    // Holds nothing but the twin. It never meets the scan — its only job is to be a second
    // namespace of the same tenant answering to one of the scanned namespace's ids.
    private const string ShadowNs = "shadow";

    // A third namespace, for ids that must be unattributable without appearing in the scan at all.
    private const string SecondShadowNs = "shadow-two";

    // The pre-tenancy partition every legacy deployment still runs in.
    private const string LegacyTenant = "";

    // An id that sorts BETWEEN its partners, "aaa-*" and "zzz-*". The neighbour memo used to be
    // keyed on each pair's canonical (lex-smaller) endpoint, so a row whose anchor outranks half of
    // its partners produced a key per partner rather than a key per row. An anchor sorting first or
    // last would let a keyed-by-endpoint memo pass this by accident.
    private const string MidSortingAnchor = "mmm-anchor";

    // Two dimensions per planted pair, plus one spare for the twin. Sized for the widest fixture
    // here (twelve pairs), so every pair lies in its own plane and no cross-pair can drift above
    // the threshold; a shorter vector would make the ranking a test reasons about depend on which
    // slots happened to overlap.
    private const int Dim = 26;

    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly AutoLinkScanner _scanner;

    public AutoLinkCapAccountingTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"autolink_cap_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _scanner = new AutoLinkScanner(_index, _graph, new DuplicateDetector());
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    // ── 1. THE REPRODUCTION: a refused candidate must not spend the cap ──

    [Fact]
    public void CapOfOne_WithTheTopRankedPairUnattributable_WritesTheSafePairInstead()
    {
        PlantPair(slot: 0, "ambiguous-a", "ambiguous-b", skew: 0.01f);  // ~0.99995 — ranked first
        PlantPair(slot: 1, "safe-a", "safe-b", skew: 0.14f);            // ~0.99029 — ranked second
        PlantTwin("ambiguous-a");

        var result = _scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant);

        // THE ASSERTION THE OLD CODE FAILED: it let the ambiguous pair fill the single slot, hit the
        // cap, and then had the write refused — zero edges from a namespace holding a viable pair.
        Assert.Equal(1, result.EdgesCreated);
        Assert.Equal(result.EdgesCreated, _graph.EdgeCount);
        AssertLinked("safe-a", "safe-b");

        // Nothing landed on the shared node, rather than landing and being hidden from the reply.
        Assert.Empty(_graph.GetStoredEdgesForEntry("ambiguous-a", tenantId: Tenant));
        Assert.Empty(_graph.GetStoredEdgesForEntry("ambiguous-b", tenantId: Tenant));

        // The refusal reaches the background log and nothing else. A per-reason breakdown in the
        // result would read "a twin of one of your ids exists somewhere in this tenant".
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("refus", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ambig", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── 2. RESCAN: the starvation is what makes this more than a one-off miss ──

    [Fact]
    public void Rescan_WithAPermanentlyUnattributableTopPair_KeepsMakingProgress()
    {
        PlantPair(slot: 0, "ambiguous-a", "ambiguous-b", skew: 0.01f);  // ~0.99995 — never admissible
        PlantPair(slot: 1, "safe1-a", "safe1-b", skew: 0.14f);          // ~0.99029
        PlantPair(slot: 2, "safe2-a", "safe2-b", skew: 0.33f);          // ~0.94968
        PlantTwin("ambiguous-a");

        var first = _scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant);
        var second = _scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant);

        // The twin is permanent, so the old behaviour was not "one scan missed a pair": every scan
        // re-ranked the same unattributable pair first, spent the only slot on it and wrote zero.
        Assert.Equal(1, first.EdgesCreated);
        Assert.Equal(1, second.EdgesCreated);
        Assert.Equal(2, _graph.EdgeCount);

        // Best admissible pair first, second-best on the rescan — progress in ranked order.
        AssertLinked("safe1-a", "safe1-b");
        AssertLinked("safe2-a", "safe2-b");

        // The second scan sees the first scan's edge and reports it as skipped, which is the count
        // an already-linked pair has always produced. A suppressed pair must never land here: the
        // test runs before the existing-edge probe precisely so this count cannot answer "is there
        // a hidden edge between these two".
        Assert.Equal(1, second.EdgesSkippedExisting);

        Assert.Empty(_graph.GetStoredEdgesForEntry("ambiguous-a", tenantId: Tenant));
    }

    // ── 3. RANKING: the cap goes to the best ADMISSIBLE candidates, not the first that fit ──

    [Fact]
    public void UnderACap_TheEdgesWrittenAreTheHighestRankedAdmissibleCandidates()
    {
        PlantPair(slot: 0, "ambiguous-a", "ambiguous-b", skew: 0.01f);  // ~0.99995 — best, inadmissible
        PlantPair(slot: 1, "safe1-a", "safe1-b", skew: 0.14f);          // ~0.99029 — best admissible
        PlantPair(slot: 2, "safe2-a", "safe2-b", skew: 0.33f);          // ~0.94968 — second best
        PlantPair(slot: 3, "safe3-a", "safe3-b", skew: 0.48f);          // ~0.90158 — must wait
        PlantTwin("ambiguous-a");

        var result = _scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 2, tenantId: Tenant);

        Assert.Equal(2, result.EdgesCreated);
        Assert.Equal(result.EdgesCreated, _graph.EdgeCount);
        Assert.True(result.HitMaxEdgeCap);

        // Skipping the inadmissible candidate must not degrade into taking whatever fits: the two
        // written edges are the two best admissible pairs, in similarity order.
        AssertLinked("safe1-a", "safe1-b");
        AssertLinked("safe2-a", "safe2-b");
        AssertNotLinked("safe3-a", "safe3-b");
        Assert.Empty(_graph.GetStoredEdgesForEntry("ambiguous-a", tenantId: Tenant));
    }

    // ── 4. OVER-CORRECTION CONTROL: the identical fixture minus the twin ──

    [Fact]
    public void WithNothingUnattributable_TheCapBehavesExactlyAsBefore()
    {
        PlantPair(slot: 0, "safe1-a", "safe1-b", skew: 0.01f);
        PlantPair(slot: 1, "safe2-a", "safe2-b", skew: 0.14f);
        PlantPair(slot: 2, "safe3-a", "safe3-b", skew: 0.33f);

        var capped = _scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 2, tenantId: Tenant);

        // Nothing here is ambiguous, so pre-screening must be invisible: three pairs above the
        // threshold, the top two written, the cap flagged, and the count equal to the graph.
        Assert.Equal(3, capped.PairsAboveThreshold);

        // And what the pass COST, which is a different number and the one the budget is spent in:
        // six entries make fifteen pair slots and the scan compared all fifteen to find those
        // three. The single counter this replaces reported 3 under the name "pairs examined".
        Assert.Equal(15L, capped.PairsExamined);

        Assert.Equal(2, capped.EdgesCreated);
        Assert.Equal(capped.EdgesCreated, _graph.EdgeCount);
        Assert.True(capped.HitMaxEdgeCap);
        Assert.Equal(0, capped.EdgesSkippedExisting);
        AssertLinked("safe1-a", "safe1-b");
        AssertLinked("safe2-a", "safe2-b");
        AssertNotLinked("safe3-a", "safe3-b");

        // And the cap deferred the third pair rather than dropping it: a wider rescan links it and
        // reports the two it already holds as skipped.
        var rest = _scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 10, tenantId: Tenant);

        Assert.Equal(1, rest.EdgesCreated);
        Assert.Equal(2, rest.EdgesSkippedExisting);
        Assert.False(rest.HitMaxEdgeCap);
        Assert.Equal(3, _graph.EdgeCount);
        AssertLinked("safe3-a", "safe3-b");
    }

    // ── 5. THE STARVATION REPRO: a fixed candidate window must not outlive the pairs behind it ──

    /// <summary>
    /// Eleven independent, permanently-viable pairs under a cap of one. Nothing here is ineligible:
    /// every pair is admissible on the scan that first reaches it, so eleven scans must write eleven
    /// edges.
    ///
    /// The window the detector was asked for used to be a fixed <c>2*cap + RankingBufferSlack</c>, and
    /// pairs already linked by earlier scans were filtered only after that window had been filled. So
    /// ten of the eleven were offered forever and the eleventh never once, and the plateau was silent:
    /// the scan that wrote nothing reported EdgesCreated 0 with HitMaxEdgeCap false, which is the
    /// same report a namespace with nothing left to link produces.
    /// </summary>
    [Fact]
    public void ElevenIndependentPairs_UnderACapOfOne_ReachEleventhEdgeInsteadOfPlateauing()
    {
        const int pairs = 11;
        for (int slot = 0; slot < pairs; slot++)
            PlantPair(slot, $"p{slot}-a", $"p{slot}-b", skew: 0.01f + (slot * 0.03f));

        for (int scan = 1; scan <= pairs; scan++)
        {
            var result = _scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant);
            Assert.Equal(1, result.EdgesCreated);
            Assert.Equal(scan, _graph.EdgeCount);
        }

        for (int slot = 0; slot < pairs; slot++)
            AssertLinked($"p{slot}-a", $"p{slot}-b");

        // EXHAUSTION IS NOT STARVATION. Once every pair is linked the scan must still terminate
        // reporting nothing done — and now that report is only ever produced by a namespace that
        // really has nothing left, because a scan can no longer stop with viable pairs unexamined.
        var exhausted = _scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant);
        Assert.Equal(0, exhausted.EdgesCreated);
        Assert.False(exhausted.HitMaxEdgeCap);
        Assert.Equal(pairs, exhausted.EdgesSkippedExisting);
        Assert.Equal(pairs, _graph.EdgeCount);
    }

    // ── 6. A RUN OF INELIGIBLE CANDIDATES LONGER THAN THE BUFFER MUST NOT HIDE WHAT FOLLOWS ──

    /// <summary>
    /// Eleven already-linked pairs handed over before the one viable pair, under a cap of one. The
    /// buffer the scan settles its ranking in holds <c>2*cap + RankingBufferSlack</c> = ten, so a viable
    /// pair sitting twelfth is outside anything a fixed request of that size could have returned.
    ///
    /// The order is scripted rather than planted, because "in front of" is the whole claim: pair
    /// order out of the detector follows the candidate index, which is a hash-bucket walk, so a
    /// fixture that merely planted twelve pairs would be asserting against an ordering it does not
    /// control.
    /// </summary>
    [Fact]
    public void ElevenAlreadyLinkedCandidatesAhead_DoNotHideTheViablePairBehindThem()
    {
        var script = new List<(string IdA, string IdB, float Similarity)>();
        for (int slot = 0; slot < 11; slot++)
        {
            PlantPair(slot, $"linked{slot}-a", $"linked{slot}-b", skew: 0.02f);
            // Any relation counts as "already linked" — auto-link never adds a redundant similar_to
            // over a manually-asserted relationship.
            _graph.AddEdge(new GraphEdge($"linked{slot}-a", $"linked{slot}-b", "contradicts", 1f, null, Tenant));
            script.Add(($"linked{slot}-a", $"linked{slot}-b", 0.99f));
        }
        PlantPair(slot: 11, "viable-a", "viable-b", skew: 0.30f);
        script.Add(("viable-a", "viable-b", 0.95f));

        var scanner = ScannerOver(script);
        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant);

        Assert.Equal(1, result.EdgesCreated);
        Assert.Equal(11, result.EdgesSkippedExisting);
        Assert.Equal(12, result.PairsAboveThreshold);
        AssertLinked("viable-a", "viable-b");

        // The cap did not bind: one admissible candidate existed and it was written, so a caller
        // reading HitMaxEdgeCap false learns "everything admissible is now in the graph" — the exact
        // reading the old fixed window made unsafe.
        Assert.False(result.HitMaxEdgeCap);
    }

    /// <summary>
    /// The same shape by the other route. Unattributable candidates and already-linked ones consume
    /// a window identically, but they arrive through different filters and are counted differently:
    /// only the already-linked ones reach the caller in EdgesSkippedExisting.
    /// </summary>
    [Fact]
    public void ElevenUnattributableCandidatesAhead_DoNotHideTheViablePairBehindThem()
    {
        var script = new List<(string IdA, string IdB, float Similarity)>();
        for (int slot = 0; slot < 11; slot++)
        {
            PlantPair(slot, $"ambig{slot}-a", $"ambig{slot}-b", skew: 0.02f);
            PlantTwin($"ambig{slot}-a");
            script.Add(($"ambig{slot}-a", $"ambig{slot}-b", 0.99f));
        }
        PlantPair(slot: 11, "viable-a", "viable-b", skew: 0.30f);
        script.Add(("viable-a", "viable-b", 0.95f));

        var scanner = ScannerOver(script);
        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant);

        Assert.Equal(1, result.EdgesCreated);
        Assert.Equal(1, _graph.EdgeCount);
        AssertLinked("viable-a", "viable-b");
        Assert.False(result.HitMaxEdgeCap);

        // Refusals stay out of the skipped count and out of the reply entirely — the count would
        // otherwise answer "does a twin of this id exist somewhere in my tenant", one probe at a time.
        Assert.Equal(0, result.EdgesSkippedExisting);
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("refus", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ambig", json, StringComparison.OrdinalIgnoreCase);

        for (int slot = 0; slot < 11; slot++)
            Assert.Empty(_graph.GetStoredEdgesForEntry($"ambig{slot}-a", tenantId: Tenant));
    }

    // ── 7. RANKING SURVIVES THE PROGRESSIVE DRAW ──

    /// <summary>
    /// Candidates arrive in scan order, not in similarity order, so the cap can only buy the best
    /// ones if the scan ranks what it buffered. Scripted worst-first for exactly that reason: a scan
    /// that took the prefix of what it was handed would write the two WORST pairs and still look
    /// like it honoured the cap.
    /// </summary>
    [Fact]
    public void WhenCandidatesArriveWorstFirst_TheCapStillBuysTheBestOnes()
    {
        PlantPair(slot: 0, "best-a", "best-b", skew: 0.01f);
        PlantPair(slot: 1, "second-a", "second-b", skew: 0.14f);
        PlantPair(slot: 2, "third-a", "third-b", skew: 0.33f);
        PlantPair(slot: 3, "worst-a", "worst-b", skew: 0.48f);

        var scanner = ScannerOver(new[]
        {
            ("worst-a", "worst-b", 0.90158f),
            ("third-a", "third-b", 0.94968f),
            ("second-a", "second-b", 0.99029f),
            ("best-a", "best-b", 0.99995f),
        });

        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 2, tenantId: Tenant);

        Assert.Equal(2, result.EdgesCreated);
        Assert.True(result.HitMaxEdgeCap);
        AssertLinked("best-a", "best-b");
        AssertLinked("second-a", "second-b");
        AssertNotLinked("third-a", "third-b");
        AssertNotLinked("worst-a", "worst-b");
    }

    // ── 8. COST: DRAWING PROGRESSIVELY MUST NOT MEAN SCANNING REPEATEDLY ──

    /// <summary>
    /// The pairwise stage is quadratic in the namespace, so the number of times a scan starts one is
    /// the cost that matters. Re-asking a fixed-size detector for a bigger window would have been the
    /// other way to reach the pairs behind an ineligible run, and it would pay that quadratic cost
    /// again per attempt — worst of all on a fully-linked namespace, where every candidate is
    /// ineligible and the background service revisits it every six hours regardless.
    ///
    /// One pass per scan, whatever the scan finds. The assertion is an equality and not an upper
    /// bound so that a future edit reintroducing an escalation loop fails here rather than passing
    /// with a bound someone widened.
    /// </summary>
    [Fact]
    public void OneScanCostsOnePairwisePass_HoweverManyCandidatesAreIneligible()
    {
        for (int slot = 0; slot < 11; slot++)
        {
            PlantPair(slot, $"linked{slot}-a", $"linked{slot}-b", skew: 0.02f);
            _graph.AddEdge(new GraphEdge($"linked{slot}-a", $"linked{slot}-b", "contradicts", 1f, null, Tenant));
        }
        PlantPair(slot: 11, "viable-a", "viable-b", skew: 0.30f);

        int passes = 0;
        var scanner = CountingScanner(() => passes++);

        var first = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant);
        Assert.Equal(1, first.EdgesCreated);
        Assert.Equal(1, passes);

        // And the exhausted rescan — nothing admissible anywhere in the namespace, which is the case
        // an escalating draw would keep widening its request over — is also one pass.
        var exhausted = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant);
        Assert.Equal(0, exhausted.EdgesCreated);
        Assert.False(exhausted.HitMaxEdgeCap);
        Assert.Equal(2, passes);
    }

    /// <summary>
    /// OVER-CORRECTION CONTROL. Nothing ineligible anywhere, so the progressive draw has nothing to
    /// draw past: the counts must be the ones the fixed window produced, and the scan must not have
    /// bought them with a second pairwise pass.
    /// </summary>
    [Fact]
    public void WithNothingIneligible_TheCountsAndTheCostAreUnchanged()
    {
        PlantPair(slot: 0, "safe1-a", "safe1-b", skew: 0.01f);
        PlantPair(slot: 1, "safe2-a", "safe2-b", skew: 0.14f);
        PlantPair(slot: 2, "safe3-a", "safe3-b", skew: 0.33f);

        int passes = 0;
        var scanner = CountingScanner(() => passes++);

        var capped = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 2, tenantId: Tenant);

        Assert.Equal(3, capped.PairsAboveThreshold);
        Assert.Equal(2, capped.EdgesCreated);
        Assert.Equal(0, capped.EdgesSkippedExisting);
        Assert.True(capped.HitMaxEdgeCap);
        AssertLinked("safe1-a", "safe1-b");
        AssertLinked("safe2-a", "safe2-b");
        AssertNotLinked("safe3-a", "safe3-b");
        Assert.Equal(1, passes);

        var rest = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 10, tenantId: Tenant);

        Assert.Equal(1, rest.EdgesCreated);
        Assert.Equal(2, rest.EdgesSkippedExisting);
        Assert.False(rest.HitMaxEdgeCap);
        Assert.Equal(3, _graph.EdgeCount);
        Assert.Equal(2, passes);
    }

    // ── 9. MEMORY: WALKING EVERY PAIR MUST NOT MEAN REMEMBERING EVERY PAIR ──

    /// <summary>
    /// The invariant that licenses the scan to keep no pair identity at all: the detector yields
    /// each unordered pair exactly once, on BOTH paths.
    ///
    /// The consumer used to hold a set of every pair the stream had offered, to be sure it proposed
    /// at most one edge per pair. That set is sized by pairs walked, which is quadratic in the
    /// namespace — 49,995,000 tuples at the entry cap — and it was pure overhead, because the walks
    /// are triangular and entry ids inside one partition are dictionary keys. Uniqueness belongs
    /// here, where the working set is one namespace, not in a consumer whose working set is one
    /// namespace SQUARED.
    /// </summary>
    [Fact]
    public void StreamDuplicates_YieldsEachUnorderedPairExactlyOnce_OnBothPaths()
    {
        // Below the pivot the detector walks pairs directly; above it, it projects to a subspace
        // first and confirms survivors. Both are covered because either could double-count.
        AssertEveryUnorderedPairArrivesOnce(SyntheticCandidates(DuplicateDetector.LowRankPivot - 6));
        AssertEveryUnorderedPairArrivesOnce(SyntheticCandidates(DuplicateDetector.LowRankPivot + 6));
    }

    /// <summary>
    /// Tens of thousands of permanently-refused pairs walked in one scan, and the loop must be
    /// holding a constant number of candidates when it ends.
    ///
    /// This is the availability half of the finding. The scan runs in a BackgroundService over every
    /// namespace every six hours, so per-pair retention is not a slow leak — it is a quadratic
    /// allocation inside a process that has no one to report it to. A structural assertion rather
    /// than a timing or GC one: what is asserted is that retention is a function of the CAP and not
    /// of the pairs examined, which is either true of the loop or it is not.
    /// </summary>
    [Fact]
    public void AFloodOfRefusedPairs_IsWalkedWithoutRetainingThem()
    {
        const int floodIds = 300;
        for (int i = 0; i < floodIds; i++)
            PlantAmbiguousOutsideTheScan($"flood-{i:D4}");

        // The one viable pair, scripted LAST so the flood is also shown not to hide it.
        PlantPair(slot: 0, "viable-a", "viable-b", skew: 0.30f);

        var probe = default(AutoLinkScanProbe);
        var scanner = ScannerOver(FloodThenOneViablePair(floodIds), p => probe = p);
        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 2, tenantId: Tenant);

        int floodPairs = floodIds * (floodIds - 1) / 2;
        Assert.Equal(floodPairs + 1, result.PairsAboveThreshold);

        // The two counts are structurally independent under this seam, which is the point: the
        // scripted flood is not the namespace, so the scan's own window is the two planted entries
        // and one pair slot. A seam where "yielded" and "compared" are the same list by
        // construction can never tell them apart, and that is how a counter of above-threshold hits
        // kept the name "pairs examined" through four review rounds.
        Assert.Equal(1L, result.PairsExamined);

        int retained = probe.Retained;

        // O(cap), not O(pairs). The ranking buffer is 2*cap + slack = 12 at this cap; the flood
        // contributes nothing to it because a refused candidate never enters it. A scan-wide set
        // keyed by pair would report 44,851 here.
        Assert.InRange(retained, 0, 12);

        // AND WHAT THIS FIXTURE CANNOT SEE, stated so the next reader does not mistake it for
        // coverage. All 44,850 flood pairs are refused by the guard one line BEFORE the neighbour
        // memo is consulted, so the loop's OTHER retained structure records only the single viable
        // pair: one read, one node. A memo keyed by pair endpoint would report the same one here,
        // which is exactly how it survived a round of tests that were nominally about retention.
        // The already-linked branch — the one that fills the memo — is driven by
        // AFloodOfAlreadyLinkedPairs_RetainsOneNodesNeighbours_NotOnePerCandidate.
        Assert.Equal(1, probe.AdjacencyReads);
        Assert.Equal(1, probe.NeighborNodesMemoized);
        Assert.True(floodPairs > 40_000, "the flood must dwarf any per-cap bound for this to mean anything");

        Assert.Equal(1, result.EdgesCreated);
        AssertLinked("viable-a", "viable-b");
        Assert.False(result.HitMaxEdgeCap);
    }

    /// <summary>
    /// OVER-CORRECTION CONTROL for the memory fix. Bounding what the loop keeps must not bound what
    /// it RANKS: with more admissible candidates than the buffer holds, the cap still buys the best
    /// ones in the namespace and not the best ones that happened to fit.
    /// </summary>
    [Fact]
    public void TheRankingBufferBoundsMemoryAndNotTheRanking()
    {
        for (int slot = 0; slot < 12; slot++)
            PlantPair(slot, $"p{slot:D2}-a", $"p{slot:D2}-b", skew: 0.02f);

        // Twelve admissible candidates through a buffer of 2*1 + 8 = 10, worst first, so a scan that
        // ranked only what it could hold would write one of the pairs it saw early.
        var script = new List<(string IdA, string IdB, float Similarity)>();
        for (int slot = 0; slot < 12; slot++)
            script.Add(($"p{slot:D2}-a", $"p{slot:D2}-b", 0.86f + (slot * 0.01f)));

        var probe = default(AutoLinkScanProbe);
        var scanner = ScannerOver(script, p => probe = p);
        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant);

        Assert.Equal(1, result.EdgesCreated);
        Assert.True(result.HitMaxEdgeCap);
        AssertLinked("p11-a", "p11-b");
        Assert.InRange(probe.Retained, 0, 10);
    }

    // ── 10. THE WORK BUDGET, AND THE STARVATION IT MUST NOT REINTRODUCE ──

    /// <summary>
    /// A budget that always restarted at the first pair would be the original defect with a new
    /// cause: the pairs past it would be unreachable on every scan, forever, exactly as a fixed
    /// candidate window made them. So the budget resumes.
    ///
    /// One anchor per scan here, with the only viable pair five anchors past where the first scan
    /// can reach. Five scans must report having done nothing AND having stopped early, and the sixth
    /// must write the edge — which is only possible if each scan begins where the last one stopped.
    /// </summary>
    [Fact]
    public void AViablePairBeyondOneScansBudget_IsReachedByASuccessiveScan()
    {
        // Eight candidates, so the anchor space is 0..7 and a budget of eight comparisons buys
        // exactly one anchor per scan.
        for (int slot = 0; slot < 4; slot++)
            PlantPair(slot, $"q{slot}-a", $"q{slot}-b", skew: 0.02f);

        var rows = new Dictionary<int, (string IdA, string IdB, float Similarity)[]>
        {
            [5] = new[] { ("q3-a", "q3-b", 0.95f) },
        };

        var scanner = WindowedScannerOver(rows);

        for (int scan = 0; scan < 5; scan++)
        {
            var early = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant,
                maxPairComparisons: 8);

            Assert.Equal(0, early.PairsAboveThreshold);

            // THE ASSERTION THAT USED TO SAY THIS SCAN DID NOTHING. Eight candidates and one anchor
            // per scan: anchor a owns 7 - a pair slots, so these five scans compare 7, 6, 5, 4 and
            // 3 pairs. The old counter reported 0 for every one of them, and the log sentence built
            // on it told an operator that a scan which had done work had examined nothing.
            Assert.Equal(7L - scan, early.PairsExamined);

            Assert.Equal(0, early.EdgesCreated);
            Assert.False(early.HitMaxEdgeCap);

            // The third outcome, stated rather than folded into one of the other two: nothing was
            // written and the cap was not binding, but this is NOT "nothing left to link".
            Assert.True(early.PairScanIncomplete);
        }

        var reached = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant,
            maxPairComparisons: 8);

        Assert.Equal(1, reached.PairsAboveThreshold);
        Assert.Equal(2L, reached.PairsExamined);
        Assert.Equal(1, reached.EdgesCreated);
        AssertLinked("q3-a", "q3-b");
        Assert.True(reached.PairScanIncomplete);
    }

    /// <summary>
    /// OVER-CORRECTION CONTROL for the budget. A namespace small enough for its whole pair space to
    /// fit inside the default budget — which is every namespace up to a few thousand entries — must
    /// see the windowing not at all: one scan, everything examined, and the completeness flag says
    /// so, which is what lets "no edges and no cap" keep meaning "nothing left to link".
    /// </summary>
    [Fact]
    public void UnderTheDefaultBudget_ASmallNamespaceIsScannedWholeAndSaysSo()
    {
        for (int slot = 0; slot < 4; slot++)
            PlantPair(slot, $"q{slot}-a", $"q{slot}-b", skew: 0.02f);

        var rows = new Dictionary<int, (string IdA, string IdB, float Similarity)[]>
        {
            [5] = new[] { ("q3-a", "q3-b", 0.95f) },
        };

        var scanner = WindowedScannerOver(rows);
        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant);

        Assert.Equal(1, result.EdgesCreated);
        Assert.False(result.PairScanIncomplete);
        AssertLinked("q3-a", "q3-b");

        var exhausted = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant);
        Assert.Equal(0, exhausted.EdgesCreated);
        Assert.Equal(1, exhausted.EdgesSkippedExisting);
        Assert.False(exhausted.HitMaxEdgeCap);
        Assert.False(exhausted.PairScanIncomplete);
    }

    // ── 11. THE RACE: A RELATION ADDED WHILE THE SCAN IS MID-ENUMERATION ──

    /// <summary>
    /// The scan memoizes a node's neighbours so that examining every pair does not take the graph
    /// lock a quadratic number of times. That memo is a snapshot, and the graph stays mutable, so a
    /// relation created after it is read is invisible to the scan; the default write boundary
    /// replaces only the SAME source/target/relation, so a derived <c>similar_to</c> would land on
    /// top of it and the pair would carry both.
    ///
    /// Deterministic, and forced rather than hoped for. The pair source is a lazy iterator, so the
    /// scan is genuinely suspended INSIDE its own enumeration — with the node's adjacency already
    /// memoized — at the instant the writer runs. A timed pair of worker loops that observed no
    /// exception would prove nothing about whether the two ever overlapped; this interleaving is the
    /// one the race needs and it happens on every run.
    /// </summary>
    [Fact]
    public void ARelationCreatedMidEnumeration_IsNotJoinedByADerivedSimilarTo()
    {
        PlantPair(slot: 0, "node-a", "node-b", skew: 0.02f);
        PlantPair(slot: 1, "node-x", "node-z", skew: 0.02f);

        var scanner = ScannerOver(LinkTheSecondPairMidEnumeration());
        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 10, tenantId: Tenant);

        // Exactly one relation between the two, and it is the one a person asserted. The reviewer's
        // barrier left both contradicts and similar_to here.
        Assert.Equal(new[] { "contradicts" }, RelationsBetween("node-a", "node-b"));

        // The count stays the count the graph accepted: the declined write is not reported as one.
        Assert.Equal(1, result.EdgesCreated);
        AssertLinked("node-a", "node-x");

        // And the refusal is invisible to the caller, like every other write-time decline.
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("declin", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// OVER-CORRECTION CONTROL for the race. The identical fixture with nothing racing it: the write
    /// boundary that refuses an already-related pair must still write an unrelated one, or the fix
    /// would have bought its safety by never linking anything.
    /// </summary>
    [Fact]
    public void WithNothingRacingIt_TheUnlinkedWriteBoundaryStillWritesBothEdges()
    {
        PlantPair(slot: 0, "node-a", "node-b", skew: 0.02f);
        PlantPair(slot: 1, "node-x", "node-z", skew: 0.02f);

        var scanner = ScannerOver(new[]
        {
            ("node-a", "node-x", 0.99f),
            ("node-a", "node-b", 0.95f),
        });
        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 10, tenantId: Tenant);

        Assert.Equal(2, result.EdgesCreated);
        Assert.Equal(new[] { "similar_to" }, RelationsBetween("node-a", "node-b"));
        AssertLinked("node-a", "node-x");
    }

    // ── 12. RANKING REACHES PAST THE BUFFER, NOT JUST INTO IT ──

    /// <summary>
    /// Ten admissible candidates at 0.90 and then one at 0.99, under a cap of one.
    ///
    /// The loop used to stop as soon as the buffer held <c>2*cap + slack</c> admissible candidates
    /// and sort only those, so the tenth 0.90 pair closed the scan and the 0.99 pair was never
    /// examined — while the cap's documented contract said "the highest-ranked admissible
    /// candidates". The buffer is now a bounded top-K over everything the scan's budget lets it
    /// examine: it still costs O(cap) memory, and what it holds at the end really is the best of
    /// what was seen.
    /// </summary>
    [Fact]
    public void TenGoodCandidatesAheadOfABetterOne_DoNotClaimTheCap()
    {
        for (int slot = 0; slot < 11; slot++)
            PlantPair(slot, $"r{slot:D2}-a", $"r{slot:D2}-b", skew: 0.02f);

        var script = new List<(string IdA, string IdB, float Similarity)>();
        for (int slot = 0; slot < 10; slot++)
            script.Add(($"r{slot:D2}-a", $"r{slot:D2}-b", 0.90f));
        script.Add(("r10-a", "r10-b", 0.99f));

        var scanner = ScannerOver(script);
        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant);

        Assert.Equal(11, result.PairsAboveThreshold);
        Assert.Equal(1, result.EdgesCreated);
        AssertLinked("r10-a", "r10-b");
        for (int slot = 0; slot < 10; slot++)
            AssertNotLinked($"r{slot:D2}-a", $"r{slot:D2}-b");

        // Eleven admissible candidates and a cap of one: the cap was binding, and the flag says so
        // from a count of what existed rather than from how full a buffer got.
        Assert.True(result.HitMaxEdgeCap);
    }

    /// <summary>
    /// OVER-CORRECTION CONTROL for the ranking. With a cap wide enough for all of them, a top-K must
    /// discard nobody: eleven admissible candidates, eleven edges, and no cap reported.
    /// </summary>
    [Fact]
    public void WhenTheCapFitsEveryCandidate_TheTopKDiscardsNone()
    {
        for (int slot = 0; slot < 11; slot++)
            PlantPair(slot, $"r{slot:D2}-a", $"r{slot:D2}-b", skew: 0.02f);

        var script = new List<(string IdA, string IdB, float Similarity)>();
        for (int slot = 0; slot < 10; slot++)
            script.Add(($"r{slot:D2}-a", $"r{slot:D2}-b", 0.90f));
        script.Add(("r10-a", "r10-b", 0.99f));

        var scanner = ScannerOver(script);
        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 11, tenantId: Tenant);

        Assert.Equal(11, result.EdgesCreated);
        Assert.False(result.HitMaxEdgeCap);
        Assert.False(result.PairScanIncomplete);
        for (int slot = 0; slot <= 10; slot++)
            AssertLinked($"r{slot:D2}-a", $"r{slot:D2}-b");
    }

    // ── 13. THE LEGACY MIRROR: the same scan in the partition with no tenant ──

    /// <summary>
    /// Everything above is planted in a real tenant, because the guard's namespace listing, the
    /// candidate index and the scan are all tenant-scoped and the legacy <c>""</c> partition takes a
    /// separate fast path through the index. This is that path: a default agent, unique ids, nothing
    /// ambiguous anywhere. None of the three fixes may show up here at all — same counts, same
    /// ranking, same deferral of what the cap did not buy.
    /// </summary>
    [Fact]
    public void InTheLegacyPartition_WithUniqueIds_NothingAboveChangesTheOutcome()
    {
        PlantPairIn(LegacyTenant, slot: 0, "safe1-a", "safe1-b", skew: 0.01f);
        PlantPairIn(LegacyTenant, slot: 1, "safe2-a", "safe2-b", skew: 0.14f);
        PlantPairIn(LegacyTenant, slot: 2, "safe3-a", "safe3-b", skew: 0.33f);

        var capped = _scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 2, tenantId: LegacyTenant);

        Assert.Equal(3, capped.PairsAboveThreshold);
        Assert.Equal(15L, capped.PairsExamined);
        Assert.Equal(2, capped.EdgesCreated);
        Assert.Equal(capped.EdgesCreated, _graph.EdgeCount);
        Assert.True(capped.HitMaxEdgeCap);
        Assert.False(capped.PairScanIncomplete);
        AssertLinkedIn(LegacyTenant, "safe1-a", "safe1-b");
        AssertLinkedIn(LegacyTenant, "safe2-a", "safe2-b");

        var rest = _scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 10, tenantId: LegacyTenant);

        Assert.Equal(1, rest.EdgesCreated);
        Assert.Equal(2, rest.EdgesSkippedExisting);
        Assert.False(rest.HitMaxEdgeCap);
        Assert.False(rest.PairScanIncomplete);
        Assert.Equal(3, _graph.EdgeCount);
        AssertLinkedIn(LegacyTenant, "safe3-a", "safe3-b");
    }

    // -- 14. ALLOCATION: WALKING EVERY PAIR MUST NOT MEAN BUILDING AN OBJECT PER PAIR --

    /// <summary>
    /// The sibling of test 9, and the half a retention count could not express.
    ///
    /// Retention was fixed by keeping only a bounded top-K, but every admissible pair still
    /// constructed the GraphEdge - and its empty metadata dictionary - before the ranking decided
    /// whether to keep it, and every REFUSED pair constructed one just to ask the guard whether it
    /// was usable. Measured on a production-shaped 502-entry scan: 125,751 pairs examined,
    /// 18,382,728 bytes allocated above the no-yield baseline, about 146 bytes a pair. The first
    /// default work window can examine roughly eighteen million pairs, which projects to about
    /// 2.6 GB of churn in one background pass that nobody is watching.
    ///
    /// So the property is structural rather than a GC measurement: the number of edge objects a
    /// scan builds equals the number it OFFERS the graph, and is independent of how many pairs it
    /// walked. The seam counts constructions at the scan's single construction site - see
    /// <see cref="AutoLinkScanProbe.EdgesMaterialized"/>.
    /// </summary>
    [Fact]
    public void TensOfThousandsOfRefusedPairs_BuildNoEdgeObjectBetweenThem()
    {
        const int floodIds = 300;
        for (int i = 0; i < floodIds; i++)
            PlantAmbiguousOutsideTheScan($"flood-{i:D4}");

        PlantPair(slot: 0, "viable-a", "viable-b", skew: 0.30f);

        var probe = default(AutoLinkScanProbe);
        var scanner = ScannerOver(FloodThenOneViablePair(floodIds), p => probe = p);
        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 2, tenantId: Tenant);

        int floodPairs = floodIds * (floodIds - 1) / 2;
        Assert.Equal(floodPairs + 1, probe.PairsAboveThreshold);
        Assert.True(floodPairs > 40_000, "the flood must dwarf any per-cap bound for this to mean anything");

        // THE ASSERTION THE OLD CODE FAILED: it built 44,851 edges in order to write one. One
        // object per edge written, and the pairs walked do not enter into it.
        Assert.Equal(1, probe.EdgesMaterialized);
        Assert.Equal(result.EdgesCreated, probe.EdgesMaterialized);

        Assert.Equal(1, result.EdgesCreated);
        AssertLinked("viable-a", "viable-b");
    }

    /// <summary>
    /// The other source of throwaway objects, and the one a refusal-only fixture would miss: pairs
    /// that are perfectly admissible and simply lose the ranking. Twelve of them under a cap of one.
    /// </summary>
    [Fact]
    public void AdmissibleCandidatesThatLoseTheRanking_AreNeverBuiltAsEdges()
    {
        for (int slot = 0; slot < 12; slot++)
            PlantPair(slot, $"p{slot:D2}-a", $"p{slot:D2}-b", skew: 0.02f);

        var script = new List<(string IdA, string IdB, float Similarity)>();
        for (int slot = 0; slot < 12; slot++)
            script.Add(($"p{slot:D2}-a", $"p{slot:D2}-b", 0.86f + (slot * 0.01f)));

        var probe = default(AutoLinkScanProbe);
        var scanner = ScannerOver(script, p => probe = p);
        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant);

        Assert.Equal(12, probe.PairsAboveThreshold);
        Assert.Equal(1, probe.EdgesMaterialized);
        Assert.Equal(result.EdgesCreated, probe.EdgesMaterialized);

        // And the one that WAS built is still the best of the twelve, so the saving did not come
        // out of the ranking.
        Assert.Equal(1, result.EdgesCreated);
        AssertLinked("p11-a", "p11-b");
    }

    /// <summary>
    /// OVER-CORRECTION CONTROL for the allocation fix. Deferring construction must not defer any
    /// EDGE: with a cap wide enough for every candidate, every candidate is built and every one is
    /// written, so the count that dropped is only ever the count of objects thrown away.
    /// </summary>
    [Fact]
    public void WhenTheCapFitsEveryCandidate_EveryOneOfThemIsBuiltAndWritten()
    {
        for (int slot = 0; slot < 11; slot++)
            PlantPair(slot, $"r{slot:D2}-a", $"r{slot:D2}-b", skew: 0.02f);

        var script = new List<(string IdA, string IdB, float Similarity)>();
        for (int slot = 0; slot < 11; slot++)
            script.Add(($"r{slot:D2}-a", $"r{slot:D2}-b", 0.90f + (slot * 0.005f)));

        var probe = default(AutoLinkScanProbe);
        var scanner = ScannerOver(script, p => probe = p);
        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 11, tenantId: Tenant);

        Assert.Equal(11, probe.PairsAboveThreshold);
        Assert.Equal(11, probe.EdgesMaterialized);
        Assert.Equal(11, result.EdgesCreated);
        Assert.False(result.HitMaxEdgeCap);
        for (int slot = 0; slot < 11; slot++)
            AssertLinked($"r{slot:D2}-a", $"r{slot:D2}-b");
    }

    /// <summary>
    /// LEGACY MIRROR for the allocation fix, over the real detector rather than a scripted order:
    /// the pre-tenancy partition builds one object per edge written too.
    /// </summary>
    [Fact]
    public void InTheLegacyPartition_EdgeConstructionIsBoundedByWhatIsWritten()
    {
        PlantPairIn(LegacyTenant, slot: 0, "safe1-a", "safe1-b", skew: 0.01f);
        PlantPairIn(LegacyTenant, slot: 1, "safe2-a", "safe2-b", skew: 0.14f);
        PlantPairIn(LegacyTenant, slot: 2, "safe3-a", "safe3-b", skew: 0.33f);

        var probe = default(AutoLinkScanProbe);
        var scanner = ProbedScanner(p => probe = p);
        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: LegacyTenant);

        Assert.Equal(3, probe.PairsAboveThreshold);
        Assert.Equal(1, probe.EdgesMaterialized);
        Assert.Equal(1, result.EdgesCreated);
        AssertLinkedIn(LegacyTenant, "safe1-a", "safe1-b");
    }

    // -- 15. ATTRIBUTION CAN MOVE BETWEEN ADMISSION AND THE WRITE --

    /// <summary>
    /// The endpoint screen runs BEFORE the graph's write lock - deliberately, because it resolves
    /// through CognitiveIndex and index work under the graph lock is a lock-order inversion. That
    /// leaves a gap the width of the lock acquisition, and the gap is writable: inserting a same-id
    /// twin is an ordinary entry write that takes none of the graph's locks and moves none of its
    /// revisions. The admitted endpoint is therefore shared with an entry nobody showed this caller
    /// by the time the edge lands, and OnlyIfUnlinked does not notice, because it only asks about
    /// graph relations.
    ///
    /// Deterministic and forced: the batch is a lazy enumerable, so the twin is planted while
    /// AddEdges is genuinely suspended inside its own admission loop, with the first edge already
    /// admitted. No delay, no polling, no run in which the two miss each other.
    /// </summary>
    [Fact]
    public void ATwinPlantedBetweenAdmissionAndTheWrite_LeavesNoEdgeInTheGraph()
    {
        PlantPair(slot: 0, "toctou-a", "toctou-b", skew: 0.02f);

        int written = _graph.AddEdges(
            TwinPlantedMidAdmission("toctou-a", "toctou-b"), EdgeAddMode.OnlyIfUnlinked);

        // The reviewer's run wrote the edge and returned 1 while the endpoint named two namespaces.
        Assert.Equal(0, written);
        Assert.Equal(0, _graph.EdgeCount);
        Assert.Empty(_graph.GetStoredEdgesForEntry("toctou-a", tenantId: Tenant));
        Assert.Empty(_graph.GetStoredEdgesForEntry("toctou-b", tenantId: Tenant));

        // The fixture really did make the endpoint ambiguous - otherwise this would pass for the
        // wrong reason, against a batch that was never in danger.
        Assert.Equal(2, _index.CountNamespacesContaining("toctou-a", tenantId: Tenant));
    }

    /// <summary>
    /// OVER-CORRECTION CONTROL. The identical lazy batch with nothing planted behind it: laziness
    /// alone must not refuse anything, or the fix would have bought its safety by declining every
    /// deferred caller.
    /// </summary>
    [Fact]
    public void WithNoTwinPlanted_TheSameLazyBatchIsWrittenInFull()
    {
        PlantPair(slot: 0, "toctou-a", "toctou-b", skew: 0.02f);

        int written = _graph.AddEdges(
            TwinPlantedMidAdmission("toctou-a", "toctou-b", plant: false), EdgeAddMode.OnlyIfUnlinked);

        Assert.Equal(1, written);
        Assert.Equal(1, _graph.EdgeCount);
        AssertLinked("toctou-a", "toctou-b");
    }

    /// <summary>
    /// The same gap on the single-edge path, which the reviewer flagged separately and which has no
    /// enumerable to suspend. The seam is the graph's own first-write edge load: it runs INSIDE the
    /// write lock, after admission and before any mutation, which is precisely the window.
    ///
    /// The refusal must read as an ordinary miss. A caller that could tell "attribution moved under
    /// you" apart from "no such entry" would have the twin oracle the whole suppression mechanism
    /// exists to close.
    /// </summary>
    [Fact]
    public void TryAddEdge_WithATwinPlantedBetweenAdmissionAndTheWrite_RefusesAsAnOrdinaryMiss()
    {
        PlantPair(slot: 0, "single-a", "single-b", skew: 0.02f);

        var graph = new KnowledgeGraph(new EdgeLoadHook(_persistence, () => PlantTwin("single-a")), _index);

        bool written = graph.TryAddEdge(
            new GraphEdge("single-a", "single-b", "similar_to", 0.95f, null, Tenant), out var reply);

        Assert.False(written);
        Assert.Equal(TopologyGuard.Unattributable("single-a"), reply);
        Assert.Equal(0, graph.EdgeCount);
        Assert.Empty(graph.GetStoredEdgesForEntry("single-a", tenantId: Tenant));
        Assert.Equal(2, _index.CountNamespacesContaining("single-a", tenantId: Tenant));
    }

    /// <summary>
    /// OVER-CORRECTION CONTROL for the single-edge path: the identical seam planting nothing must
    /// still write the edge.
    /// </summary>
    [Fact]
    public void TryAddEdge_WithNothingRacingIt_StillWritesTheEdge()
    {
        PlantPair(slot: 0, "single-a", "single-b", skew: 0.02f);

        var graph = new KnowledgeGraph(new EdgeLoadHook(_persistence, onFirstLoad: null), _index);

        bool written = graph.TryAddEdge(
            new GraphEdge("single-a", "single-b", "similar_to", 0.95f, null, Tenant), out _);

        Assert.True(written);
        Assert.Equal(1, graph.EdgeCount);
    }

    /// <summary>
    /// LEGACY MIRROR for the attribution race. Two entries under one id in two namespaces of the
    /// pre-tenancy partition are ambiguous for exactly the same reason, and the gap closes there too.
    /// </summary>
    [Fact]
    public void InTheLegacyPartition_ATwinPlantedBetweenAdmissionAndTheWrite_IsStillRefused()
    {
        PlantPairIn(LegacyTenant, slot: 0, "legacy-a", "legacy-b", skew: 0.02f);

        int written = _graph.AddEdges(
            TwinPlantedMidAdmission("legacy-a", "legacy-b", tenantId: LegacyTenant),
            EdgeAddMode.OnlyIfUnlinked);

        Assert.Equal(0, written);
        Assert.Equal(0, _graph.EdgeCount);
        Assert.Empty(_graph.GetStoredEdgesForEntry("legacy-a", tenantId: LegacyTenant));
        Assert.Equal(2, _index.CountNamespacesContaining("legacy-a", tenantId: LegacyTenant));
    }

    // -- 16. THE RESUME CURSOR IS READ, ADVANCED AND WRITTEN - AS ONE STEP OR NOT AT ALL --

    /// <summary>
    /// The scanner is a singleton shared by the background sweep and the callable tool, and the
    /// cursor sequence is not atomic per key. Two scans could read the same start, and the slower -
    /// OLDER - one would write last, overwriting progress a newer scan had already made. The
    /// reviewer's interleaving observed starts 0, 0, 1, 1: the fourth scan began at anchor 1 after
    /// an earlier scan had already advanced past it to 2, so a whole quadratic window was repeated.
    ///
    /// Deterministic and forced, the same way test 11 forces its race: scan A is suspended INSIDE
    /// its own pair enumeration - start anchor read, cursor not yet written - while two more scans
    /// of the same (tenant, namespace) run to completion. That is the interleaving two threads
    /// produce, made to happen on every run.
    ///
    /// One scan per key at a time turns the sequence into one step, so B and C do not run at all,
    /// and the starts observed are 0 then 1 - never a repeat, never a step backwards.
    /// </summary>
    [Fact]
    public void ScansThatOverlapOnOneNamespace_CannotRollTheResumeCursorBackward()
    {
        // Eight candidates, so a budget of eight comparisons buys exactly one anchor per scan and
        // each scan's start anchor is the previous scan's plus one.
        for (int slot = 0; slot < 4; slot++)
            PlantPair(slot, $"q{slot}-a", $"q{slot}-b", skew: 0.02f);

        var starts = new List<int>();
        var nested = new List<AutoLinkResult>();
        AutoLinkScanner scanner = null!;
        bool interleaveOnce = true;

        IEnumerable<(string IdA, string IdB, float Similarity)> Source(PairScanWindow window)
        {
            starts.Add(window.StartAnchor);
            if (interleaveOnce)
            {
                interleaveOnce = false;
                nested.Add(scanner.Scan(ScanNs, 0.85f, 1, tenantId: Tenant, maxPairComparisons: 8));  // B
                nested.Add(scanner.Scan(ScanNs, 0.85f, 1, tenantId: Tenant, maxPairComparisons: 8));  // C
            }
            yield break;
        }

        scanner = new AutoLinkScanner(_index, _graph, new DuplicateDetector(),
            pairs: (_, _, window, _) => Source(window));

        var a = scanner.Scan(ScanNs, 0.85f, 1, tenantId: Tenant, maxPairComparisons: 8);  // A
        var d = scanner.Scan(ScanNs, 0.85f, 1, tenantId: Tenant, maxPairComparisons: 8);  // D

        // THE ASSERTION THE OLD CODE FAILED: it observed 0, 0, 1, 1.
        Assert.Equal(new[] { 0, 1 }, starts);

        Assert.False(a.ScanAlreadyInProgress);
        Assert.False(d.ScanAlreadyInProgress);

        // And the two that were turned away say so, rather than reporting an empty scan a caller
        // would read as "nothing left to link".
        Assert.Equal(2, nested.Count);
        Assert.All(nested, r =>
        {
            Assert.True(r.ScanAlreadyInProgress);
            Assert.True(r.PairScanIncomplete);
            Assert.Equal(0, r.PairsExamined);
            Assert.Equal(0, r.PairsAboveThreshold);
            Assert.Equal(0, r.EdgesCreated);
            Assert.Equal(0, r.ScannedEntries);
            Assert.False(r.HitMaxEdgeCap);
        });
    }

    /// <summary>
    /// OVER-CORRECTION CONTROL for the serialization. Scans that do NOT overlap must never be
    /// turned away, and the rotation must still advance one anchor per scan - a key left behind by
    /// a finished scan would wedge the namespace shut forever, silently.
    /// </summary>
    [Fact]
    public void BackToBackScansOfOneNamespace_AreNeverDeferredAndKeepAdvancing()
    {
        for (int slot = 0; slot < 4; slot++)
            PlantPair(slot, $"q{slot}-a", $"q{slot}-b", skew: 0.02f);

        var starts = new List<int>();
        var scanner = new AutoLinkScanner(_index, _graph, new DuplicateDetector(),
            pairs: (_, _, window, _) =>
            {
                starts.Add(window.StartAnchor);
                return Array.Empty<(string, string, float)>();
            });

        for (int i = 0; i < 3; i++)
        {
            var result = scanner.Scan(ScanNs, 0.85f, 1, tenantId: Tenant, maxPairComparisons: 8);
            Assert.False(result.ScanAlreadyInProgress);
        }

        Assert.Equal(new[] { 0, 1, 2 }, starts);
    }

    /// <summary>
    /// The key is (tenant, namespace) and not namespace, and this is the LEGACY MIRROR of the
    /// serialization: a scan of the same namespace in the pre-tenancy partition runs to completion
    /// while a tenant's scan of it is in flight. Keying on the namespace alone would let one
    /// tenant's background sweep silently suppress every other tenant's scans of a namespace name
    /// they merely happen to share.
    /// </summary>
    [Fact]
    public void AScanOfTheSameNamespaceInAnotherTenant_RunsWhileThisOneIsInFlight()
    {
        for (int slot = 0; slot < 4; slot++)
        {
            PlantPair(slot, $"q{slot}-a", $"q{slot}-b", skew: 0.02f);
            PlantPairIn(LegacyTenant, slot, $"L{slot}-a", $"L{slot}-b", skew: 0.02f);
        }

        AutoLinkScanner scanner = null!;
        AutoLinkResult? legacy = null;
        bool interleaveOnce = true;

        IEnumerable<(string IdA, string IdB, float Similarity)> Source(
            IReadOnlyList<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates)
        {
            if (candidates[0].Entry.TenantId.Length == 0)
            {
                yield return ("L0-a", "L0-b", 0.95f);
                yield break;
            }

            if (interleaveOnce)
            {
                interleaveOnce = false;
                legacy = scanner.Scan(ScanNs, 0.85f, 1, tenantId: LegacyTenant, maxPairComparisons: 8);
            }
        }

        scanner = new AutoLinkScanner(_index, _graph, new DuplicateDetector(),
            pairs: (candidates, _, _, _) => Source(candidates));

        var tenantScan = scanner.Scan(ScanNs, 0.85f, 1, tenantId: Tenant, maxPairComparisons: 8);

        Assert.False(tenantScan.ScanAlreadyInProgress);
        Assert.NotNull(legacy);
        Assert.False(legacy!.ScanAlreadyInProgress);
        Assert.Equal(1, legacy.EdgesCreated);
        AssertLinkedIn(LegacyTenant, "L0-a", "L0-b");
    }

    // -- 17. THE COST THE SCAN REPORTS MUST BE THE COST IT PAID --

    /// <summary>
    /// The pair stream yields only what CLEARS the threshold, so a counter in the consumer's loop
    /// body counts neighbours found, not pairs compared. That counter was named PairsExamined, was
    /// serialized to operators as <c>pairsExamined</c>, was the subject of a log sentence asserting
    /// it was "the work done over that anchor range", and was the justification quoted in the
    /// comments for the ranking-tuple design. It was the only cost number this subsystem published.
    ///
    /// Four independent pairs among eight entries: four matches out of twenty-eight comparisons.
    /// In a real steady-state namespace the ratio is three to five orders of magnitude, so an
    /// operator reading the small number concludes the quadratic stage is trivially cheap and tunes
    /// maxPairComparisons in the wrong direction.
    /// </summary>
    [Fact]
    public void TheReportedCost_IsThePairsCompared_NotThePairsThatMatched()
    {
        // Every planted pair lies in its own plane, so the four pairs clear 0.85 and all
        // twenty-four cross-pairs sit at cosine 0.
        for (int slot = 0; slot < 4; slot++)
            PlantPair(slot, $"s{slot}-a", $"s{slot}-b", skew: 0.02f);

        var result = _scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 10, tenantId: Tenant);

        // THE ASSERTION THE OLD CODE FAILED: it reported 4 here, under a name, a JSON property and
        // a log sentence that all three said this was the work.
        Assert.Equal(28L, result.PairsExamined);
        Assert.Equal(4, result.PairsAboveThreshold);
        Assert.Equal(4, result.EdgesCreated);
    }

    /// <summary>
    /// THE INVERTED CASE, and the reason the old number could not be repaired by reinterpreting it:
    /// it does not even move monotonically with the work. The identical walk — eight candidates,
    /// twenty-eight pair slots — reports 4 above when the namespace holds four independent pairs
    /// and 28 when it holds eight near-duplicates, and a memory store accumulates near-duplicates
    /// on its own. Same cost, seven times the number.
    /// </summary>
    [Fact]
    public void WhenEveryComparedPairMatches_TheCostIsUnchangedAndTheMatchCountIsNot()
    {
        PlantNearIdenticalCluster(8);

        var result = _scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 100, tenantId: Tenant);

        Assert.Equal(28L, result.PairsExamined);
        Assert.Equal(28, result.PairsAboveThreshold);
        Assert.Equal(28, result.EdgesCreated);
    }

    // -- 18. THE NEIGHBOUR MEMO IS THE SCAN'S LARGEST RETAINED STRUCTURE --

    /// <summary>
    /// The other structure the loop retains, and the one the probe could not see.
    ///
    /// AutoLinkScanProbe.Retained reports the ranking buffer, which is O(cap) — and the two tests
    /// that assert it drive their flood through ids the guard refuses one line BEFORE the neighbour
    /// memo is consulted, so the memo is empty in exactly the tests that measure retention. The
    /// memo was a Dictionary keyed by each pair's canonical (lex-smaller) endpoint, so it grew
    /// toward one HashSet per candidate, each sized by that node's degree: O(candidates x degree),
    /// held for the whole scan, and largest in precisely the densified steady state this scanner
    /// exists to produce.
    ///
    /// The anchor here sorts in the MIDDLE of its partners, which is the discriminator: half the
    /// row canonicalizes to the partner and half to the anchor, so the old shape kept 301 sets for
    /// this one anchor row. Keyed on the id the stream puts first, it keeps one — and an anchor
    /// sorting first or last would have let the old shape pass by accident.
    /// </summary>
    [Fact]
    public void AFloodOfAlreadyLinkedPairs_RetainsOneNodesNeighbours_NotOnePerCandidate()
    {
        const int partners = 600;

        PlantAttributableOutsideTheScan(MidSortingAnchor);
        PlantPair(slot: 0, "viable-a", "viable-b", skew: 0.30f);

        var edges = new List<GraphEdge>(partners);
        var script = new List<(string IdA, string IdB, float Similarity)> { ("viable-a", "viable-b", 0.95f) };
        for (int i = 0; i < partners; i++)
        {
            string partner = i < partners / 2 ? $"aaa-{i:D4}" : $"zzz-{i:D4}";
            PlantAttributableOutsideTheScan(partner);
            edges.Add(new GraphEdge(MidSortingAnchor, partner, "contradicts", 1f, null, Tenant));
            script.Add((MidSortingAnchor, partner, 0.99f));
        }
        Assert.Equal(partners, _graph.AddEdges(edges));

        var probe = default(AutoLinkScanProbe);
        var scanner = ScannerOver(script, p => probe = p);
        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 2, tenantId: Tenant);

        // THE ASSERTION THE OLD CODE FAILED, and the one no probe field could express: keyed by
        // lex-min endpoint it held 302 sets here — one for the anchor, one for each of the 300
        // partners sorting ahead of it, and one for the viable pair's own row — while probe.Retained
        // reported 1 and a test called that bounded.
        Assert.Equal(1, probe.NeighborNodesMemoized);

        // What it retains is one node's degree, which is a property of the GRAPH. The old shape was
        // candidates x degree, which is a property of the namespace squared.
        Assert.Equal(partners, probe.NeighborIdsRetained);
        Assert.Equal(partners, _graph.GetStoredEdgesForEntry(MidSortingAnchor, tenantId: Tenant).Count);

        // And the graph's upgradeable-read lock — which admits ONE holder at a time process-wide —
        // is taken once per anchor row instead of once per distinct lex-min id: two acquisitions
        // for 601 pairs, against 302 before.
        Assert.Equal(2, probe.AdjacencyReads);

        // The flood is still walked, still reported, and the viable pair ahead of it still lands.
        Assert.Equal(partners, result.EdgesSkippedExisting);
        Assert.Equal(1, result.EdgesCreated);
        AssertLinked("viable-a", "viable-b");
    }

    /// <summary>
    /// OVER-CORRECTION CONTROL for the memo. Holding one node makes correctness depend on nothing:
    /// a miss re-reads the graph, and the method is a cost filter rather than the authority, so a
    /// stream that never repeats an anchor must reach the same answers and pay a read per pair.
    ///
    /// That is the property that licenses the single slot, and it is the one a test seam scripting
    /// an arbitrary order would otherwise violate silently.
    /// </summary>
    [Fact]
    public void AnInterleavedStream_StillSkipsEveryLinkedPair_AndOnlyPaysForIt()
    {
        var edges = new List<GraphEdge>();
        for (int hub = 0; hub < 3; hub++)
        {
            PlantAttributableOutsideTheScan($"hub-{hub}");
            for (int spoke = 0; spoke < 2; spoke++)
            {
                PlantAttributableOutsideTheScan($"hub-{hub}-spoke-{spoke}");
                edges.Add(new GraphEdge($"hub-{hub}", $"hub-{hub}-spoke-{spoke}", "contradicts", 1f, null, Tenant));
            }
        }
        Assert.Equal(6, _graph.AddEdges(edges));
        PlantPair(slot: 0, "unrelated-a", "unrelated-b", skew: 0.30f);

        // Round-robin, so no two consecutive pairs share an anchor and every lookup is a miss.
        var script = new List<(string IdA, string IdB, float Similarity)>();
        for (int spoke = 0; spoke < 2; spoke++)
            for (int hub = 0; hub < 3; hub++)
                script.Add(($"hub-{hub}", $"hub-{hub}-spoke-{spoke}", 0.99f));

        var probe = default(AutoLinkScanProbe);
        var scanner = ScannerOver(script, p => probe = p);
        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 10, tenantId: Tenant);

        // CORRECTNESS IS ORDER-INDEPENDENT: all six are still recognised as already linked, so none
        // is joined by a derived similar_to.
        Assert.Equal(6, result.EdgesSkippedExisting);
        Assert.Equal(0, result.EdgesCreated);

        // Only the COST moves: one read per pair when consecutive pairs never share an anchor,
        // against one per row when they do. That is the whole of what anchor-major ordering buys.
        Assert.Equal(6, probe.AdjacencyReads);
        Assert.Equal(1, probe.NeighborNodesMemoized);
    }

    // -- 19. THE EDGE CAP IS A MEMORY BOUND, SO IT CANNOT BE LEFT TO THE CALLER --

    /// <summary>
    /// The ranking buffer is O(cap) only while the cap is bounded, and it was not: Scan is public,
    /// the auto-link tool passes maxNewEdges through unvalidated, and DecayConfig.
    /// AutoLinkMaxNewEdgesPerScan is a settable int the background sweep reads straight into it.
    ///
    /// At int.MaxValue the buffer's capacity is a capacity it can never reach, so compaction never
    /// runs and the buffer becomes what it had just stopped being — a scan-wide structure sized by
    /// pairs walked. That is the round-6 defect, reachable again through a parameter the round-7
    /// fix left open, and the saturating branch written for it made the degenerate case explicit
    /// rather than closing it.
    /// </summary>
    [Fact]
    public void ACapOfIntMaxValue_IsClampedToTheHardCeiling()
    {
        PlantPair(slot: 0, "safe-a", "safe-b", skew: 0.02f);

        var probe = default(AutoLinkScanProbe);
        var scanner = ProbedScanner(p => probe = p);
        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: int.MaxValue, tenantId: Tenant);

        // THE ASSERTION THE OLD CODE FAILED: it took int.MaxValue whole.
        Assert.Equal(AutoLinkScanner.MaxNewEdgesPerScanHardCap, probe.EdgeCapApplied);

        // And the clamp changed nothing else. A ceiling that altered what an ordinary namespace
        // links would be a worse cure than the disease.
        Assert.Equal(1, result.EdgesCreated);
        Assert.False(result.HitMaxEdgeCap);
        AssertLinked("safe-a", "safe-b");
    }

    /// <summary>
    /// OVER-CORRECTION CONTROL for the ceiling: an ordinary cap must reach the scan untouched, and
    /// a negative one must land at zero rather than making the ranking buffer's capacity negative —
    /// the same saturation hazard approached from the other end.
    /// </summary>
    [Fact]
    public void AnOrdinaryCap_ReachesTheScanUnclamped_AndANegativeOneBecomesZero()
    {
        for (int slot = 0; slot < 3; slot++)
            PlantPair(slot, $"s{slot}-a", $"s{slot}-b", skew: 0.02f);

        var probe = default(AutoLinkScanProbe);
        var scanner = ProbedScanner(p => probe = p);

        var capped = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 2, tenantId: Tenant);
        Assert.Equal(2, probe.EdgeCapApplied);
        Assert.Equal(2, capped.EdgesCreated);
        Assert.True(capped.HitMaxEdgeCap);

        var negative = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: -5, tenantId: Tenant);
        Assert.Equal(0, probe.EdgeCapApplied);
        Assert.Equal(0, negative.EdgesCreated);
    }

    // -- 20. THE RESUME CURSOR RETRACTS, OR IT IS A LEAK WITH A SECOND-ORDER CORRECTNESS BUG --

    /// <summary>
    /// _resumeAnchors had exactly one write site and no removal site anywhere, on a DI singleton in
    /// a process designed to run for weeks. Nothing tells it a namespace stopped existing: the
    /// candidate index, the BM25/HNSW indexes, the persisted snapshot and the diffusion kernel each
    /// retract their own per-partition state and none of them reaches the scanner.
    ///
    /// Debate namespaces are the workload that makes it concrete — one per session, none of them
    /// starting with '_' so the sweep scans every one, and purge_debates deletes them on a TTL. A
    /// deleted namespace is never scanned again, so it can never come back to drop its own cursor;
    /// the dictionary is monotonic in the (tenant, namespace) pairs EVER scanned.
    /// </summary>
    [Fact]
    public void ACursorForADeletedNamespace_IsDroppedByTheNextScanOfThatTenant()
    {
        const string doomed = "doomed";
        PlantPairInNamespace(doomed, slot: 0, "d-a", "d-b", skew: 0.02f);
        PlantPair(slot: 1, "keep-a", "keep-b", skew: 0.02f);

        // A budget of one comparison over two candidates buys one anchor, so the scan stops on the
        // budget and writes a cursor.
        _scanner.Scan(doomed, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant, maxPairComparisons: 1);
        Assert.Equal(1, _scanner.ResumeCursorCount);

        // The namespace goes the way purge_debates takes an expired debate.
        _index.DeleteAllInNamespace(doomed, Tenant);

        // THE ASSERTION THE OLD CODE FAILED: this left two cursors, one of them naming a namespace
        // that no longer exists, once per debate for the life of the process.
        _scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant, maxPairComparisons: 1);
        Assert.Equal(1, _scanner.ResumeCursorCount);
    }

    /// <summary>
    /// The second removal path, and the one the reconciliation cannot reach: a namespace that still
    /// EXISTS — so the tenant's listing still names it — but no longer holds two entries a pairwise
    /// scan can use. There is no pair space for a cursor to point into, so the cursor is dead state.
    /// Both of ScanExclusive's early returns carry the same removal, for the same reason.
    /// </summary>
    [Fact]
    public void ANamespaceThatStopsHavingAPairToScan_DropsItsCursor()
    {
        PlantPair(slot: 0, "one-a", "one-b", skew: 0.02f);

        _scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant, maxPairComparisons: 1);
        Assert.Equal(1, _scanner.ResumeCursorCount);

        Assert.True(_index.Delete("one-b", ScanNs, Tenant));

        var result = _scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant, maxPairComparisons: 1);

        Assert.Equal(1, result.ScannedEntries);
        Assert.Equal(0, _scanner.ResumeCursorCount);
    }

    /// <summary>
    /// OVER-CORRECTION CONTROL for both removals. A cleanup that dropped LIVE cursors would restart
    /// every budgeted namespace at anchor 0 on every scan — which is the starvation the cursor
    /// exists to prevent, reintroduced by its own cleanup, and invisible in every count a caller
    /// sees. So the third scan of a namespace must still resume where its first one stopped, across
    /// an intervening scan of a different namespace that ran the reconciliation.
    /// </summary>
    [Fact]
    public void CursorsForNamespacesThatStillExist_SurviveAndKeepAdvancing()
    {
        const string other = "other";
        PlantPairInNamespace(other, slot: 0, "o-a", "o-b", skew: 0.02f);
        PlantPair(slot: 1, "keep-a", "keep-b", skew: 0.02f);

        var starts = new List<(string Ns, int Start)>();
        string scanning = "";
        var scanner = new AutoLinkScanner(_index, _graph, new DuplicateDetector(),
            pairs: (_, _, window, _) =>
            {
                starts.Add((scanning, window.StartAnchor));
                return Array.Empty<(string, string, float)>();
            });

        scanning = other; scanner.Scan(other, 0.85f, 1, tenantId: Tenant, maxPairComparisons: 1);
        scanning = ScanNs; scanner.Scan(ScanNs, 0.85f, 1, tenantId: Tenant, maxPairComparisons: 1);
        scanning = other; scanner.Scan(other, 0.85f, 1, tenantId: Tenant, maxPairComparisons: 1);

        Assert.Equal(2, scanner.ResumeCursorCount);
        Assert.Equal(new[] { (other, 0), (ScanNs, 0), (other, 1) }, starts);
    }

    // ── fixtures ──

    /// <summary>
    /// Every ordered flood pair, generated lazily, then the one viable pair. Lazy because the point
    /// of the test is that nothing holds tens of thousands of tuples — and that has to include the
    /// fixture, or the test would be measuring its own list.
    /// </summary>
    private static IEnumerable<(string IdA, string IdB, float Similarity)> FloodThenOneViablePair(int floodIds)
    {
        for (int i = 0; i < floodIds; i++)
            for (int j = i + 1; j < floodIds; j++)
                yield return ($"flood-{i:D4}", $"flood-{j:D4}", 0.99f);

        yield return ("viable-a", "viable-b", 0.95f);
    }

    /// <summary>
    /// THE BARRIER. The scan is suspended inside this enumeration, having already read and memoized
    /// node-a's adjacency for the first pair, when the writer runs. That is the interleaving the
    /// production race produces between two threads, made deterministic: no delay, no polling, and
    /// no run in which the two miss each other.
    /// </summary>
    private IEnumerable<(string IdA, string IdB, float Similarity)> LinkTheSecondPairMidEnumeration()
    {
        // Canonically node-a is the source, so judging this pair is what caches node-a's neighbours
        // for the rest of the scan — the snapshot the write below invalidates.
        yield return ("node-a", "node-x", 0.99f);

        _graph.AddEdge(new GraphEdge("node-a", "node-b", "contradicts", 1f, null, Tenant));

        yield return ("node-a", "node-b", 0.95f);
    }

    /// <summary>
    /// A batch of one edge, handed over lazily, with the twin planted AFTER that edge has been
    /// admitted and BEFORE the write lock is taken.
    ///
    /// The whole interleaving is in the shape of the enumerable. AddEdges screens each edge as it
    /// pulls it - endpoints resolved through CognitiveIndex, outside the graph lock, because index
    /// work under that lock inverts this codebase's lock order - and only then takes the lock. So
    /// the consumer is genuinely suspended inside its own admission loop at the moment this method
    /// resumes, which is exactly where a concurrent entry write lands in production. Deterministic:
    /// no delay, no polling, no run in which the two miss each other.
    /// </summary>
    private IEnumerable<GraphEdge> TwinPlantedMidAdmission(
        string idA, string idB, bool plant = true, string? tenantId = null)
    {
        string tenant = tenantId ?? Tenant;
        yield return new GraphEdge(idA, idB, "similar_to", 0.95f, null, tenant);

        if (plant)
            PlantTwinIn(tenant, idA);
    }

    /// <summary>
    /// <paramref name="count"/> distinct unit-ish vectors in a fixed dimension — enough of them to
    /// push the detector over its spectral pivot when asked, and consistent enough that no candidate
    /// is dropped for a zero norm or a mismatched length.
    /// </summary>
    private static List<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> SyntheticCandidates(int count)
    {
        const int dim = 96;
        var candidates = new List<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)>(count);
        for (int i = 0; i < count; i++)
        {
            var v = new float[dim];
            for (int k = 0; k < dim; k++)
                v[k] = MathF.Sin((i + 1) * 0.37f * (k + 1)) + 0.05f;
            var entry = new CognitiveEntry($"syn-{i:D5}", v, ScanNs, $"synthetic {i}", tenantId: Tenant);
            candidates.Add((entry, McpEngramMemory.Core.Services.Retrieval.VectorMath.Norm(v), null));
        }
        return candidates;
    }

    /// <summary>
    /// Drains the stream at a threshold nothing can fail, so the yielded set must be the complete
    /// unordered pair set of the candidate list — once each, and no self-pairs.
    /// </summary>
    private static void AssertEveryUnorderedPairArrivesOnce(
        List<(CognitiveEntry Entry, float Norm, QuantizedVector? Quantized)> candidates)
    {
        var seen = new HashSet<(string, string)>();
        int yielded = 0;
        foreach (var (idA, idB, _) in new DuplicateDetector()
                     .StreamDuplicates(candidates, -1f, PairScanWindow.Full, CancellationToken.None))
        {
            Assert.NotEqual(idA, idB);
            var key = string.CompareOrdinal(idA, idB) < 0 ? (idA, idB) : (idB, idA);
            Assert.True(seen.Add(key), $"pair {key} was yielded more than once");
            yielded++;
        }

        int n = candidates.Count;
        Assert.Equal(n * (n - 1) / 2, yielded);
    }

    private IReadOnlyList<string> RelationsBetween(string idA, string idB)
        => _graph.GetStoredEdgesForEntry(idA, tenantId: Tenant)
            .Where(e => (e.SourceId == idA && e.TargetId == idB) || (e.SourceId == idB && e.TargetId == idA))
            .Select(e => e.Relation)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();


    /// <summary>
    /// A scanner fed a fixed candidate ORDER instead of the detector's.
    ///
    /// The starvation these tests are about is positional — a viable pair behind an ineligible run —
    /// and the detector emits pairs in candidate-index order, which is a hash-bucket walk over the
    /// namespace dictionary. Scripting the order states the arrangement under test instead of hoping
    /// the fixture produced it. The entries themselves are still really planted and the graph is
    /// still really consulted, so everything downstream of the ordering is the production path.
    /// </summary>
    private AutoLinkScanner ScannerOver(IEnumerable<(string IdA, string IdB, float Similarity)> script)
        => new(_index, _graph, new DuplicateDetector(), pairs: (_, _, _, _) => script);

    /// <summary>
    /// As <see cref="ScannerOver"/>, plus the cost probe: everything the pair loop still held when
    /// it ended — the ranking buffer AND the neighbour memo — how much of the graph it read to get
    /// there, how many GraphEdge objects it built, and the cap those bounds derive from. Together
    /// they are the whole memory claim: the scan walks every pair in its window, and what it may
    /// not do is remember them, allocate an object for each of them, or take the graph's lock once
    /// per one of them.
    /// </summary>
    private AutoLinkScanner ScannerOver(
        IEnumerable<(string IdA, string IdB, float Similarity)> script, Action<AutoLinkScanProbe> onProbe)
        => new(_index, _graph, new DuplicateDetector(), pairs: (_, _, _, _) => script,
            logger: null, onScanProbe: onProbe);

    /// <summary>
    /// The real detector plus the cost probe - for a case whose claim is about how many objects a
    /// scan builds rather than about which candidate arrives before which, so scripting the order
    /// would only weaken it.
    /// </summary>
    private AutoLinkScanner ProbedScanner(Action<AutoLinkScanProbe> onProbe)
        => new(_index, _graph, new DuplicateDetector(), pairs: null, logger: null, onScanProbe: onProbe);

    /// <summary>
    /// The real detector with a tally of how many times a pairwise pass was STARTED. It counts the
    /// call, not the pairs, because the quadratic cost belongs to the call.
    /// </summary>
    private AutoLinkScanner CountingScanner(Action onPass)
    {
        var detector = new DuplicateDetector();
        return new AutoLinkScanner(_index, _graph, detector, pairs: (candidates, threshold, window, token) =>
        {
            onPass();
            return detector.StreamDuplicates(candidates, threshold, window, token);
        });
    }

    /// <summary>
    /// A pair source that HONOURS the window it is handed, the way the detector does: anchor
    /// <paramref name="rowsByAnchor"/> keyed by candidate index, visited in the window's rotated
    /// order. Scripting the rows rather than planting them is what lets a test say "the viable pair
    /// lives five anchors past where this scan can reach" and mean it.
    /// </summary>
    private AutoLinkScanner WindowedScannerOver(
        IReadOnlyDictionary<int, (string IdA, string IdB, float Similarity)[]> rowsByAnchor)
        => new(_index, _graph, new DuplicateDetector(),
            pairs: (candidates, _, window, _) => EnumerateWindow(candidates.Count, window, rowsByAnchor));

    private static IEnumerable<(string IdA, string IdB, float Similarity)> EnumerateWindow(
        int count, PairScanWindow window,
        IReadOnlyDictionary<int, (string IdA, string IdB, float Similarity)[]> rowsByAnchor)
    {
        if (count <= 0) yield break;
        int rows = Math.Min(window.MaxAnchors, count);
        int start = ((window.StartAnchor % count) + count) % count;
        for (int r = 0; r < rows; r++)
        {
            int anchor = start + r;
            if (anchor >= count) anchor -= count;
            if (!rowsByAnchor.TryGetValue(anchor, out var pairs)) continue;
            foreach (var pair in pairs) yield return pair;
        }
    }

    /// <summary>
    /// Two entries in the scanned namespace at cosine 1/sqrt(1+skew^2), laid on the two dimensions
    /// belonging to <paramref name="slot"/>. Every other slot is exactly orthogonal to this one, so
    /// a pair's rank is a pure function of its skew and no cross-pair can drift above the threshold
    /// and reorder the candidates a test is reasoning about.
    /// </summary>
    private void PlantPair(int slot, string idA, string idB, float skew)
        => PlantPairIn(Tenant, slot, idA, idB, skew);

    /// <summary>
    /// A pair like <see cref="PlantPair"/>, in a namespace other than the scanned one. For the
    /// cursor tests, which need a second SCANNABLE namespace of the same tenant — one the scanner
    /// will write a resume cursor for, and one of which can then be deleted.
    /// </summary>
    private void PlantPairInNamespace(string ns, int slot, string idA, string idB, float skew)
    {
        var a = new float[Dim];
        var b = new float[Dim];
        a[slot * 2] = 1f;
        b[slot * 2] = 1f;
        b[(slot * 2) + 1] = skew;

        _index.Upsert(new CognitiveEntry(idA, a, ns, $"{idA} text", tenantId: Tenant));
        _index.Upsert(new CognitiveEntry(idB, b, ns, $"{idB} text", tenantId: Tenant));
    }

    /// <summary>
    /// <paramref name="count"/> near-identical entries in the scanned namespace: one shared
    /// dimension carrying nearly all the magnitude, and a per-entry skew on a second. Every
    /// unordered pair clears any threshold below ~0.999.
    ///
    /// This is the state a memory store reaches on its own — the same lesson stored a dozen times,
    /// which is why a 0.85 threshold finds anything at all — and the state in which "pairs above
    /// threshold" and "pairs compared" coincide, so the two can be shown to be different numbers
    /// that merely agree here.
    /// </summary>
    private void PlantNearIdenticalCluster(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var v = new float[Dim];
            v[0] = 1f;
            v[1] = 0.001f * i;
            _index.Upsert(new CognitiveEntry($"clone-{i:D2}", v, ScanNs, $"clone {i}", tenantId: Tenant));
        }
    }

    /// <inheritdoc cref="PlantPair"/>
    private void PlantPairIn(string tenantId, int slot, string idA, string idB, float skew)
    {
        var a = new float[Dim];
        var b = new float[Dim];
        a[slot * 2] = 1f;
        b[slot * 2] = 1f;
        b[(slot * 2) + 1] = skew;

        _index.Upsert(new CognitiveEntry(idA, a, ScanNs, $"{idA} text", tenantId: tenantId));
        _index.Upsert(new CognitiveEntry(idB, b, ScanNs, $"{idB} text", tenantId: tenantId));
    }

    /// <summary>
    /// An id the tenant answers to in two namespaces, NEITHER of them the scanned one.
    ///
    /// The flood fixture needs tens of thousands of permanently-refused pairs without putting tens
    /// of thousands of entries in front of the scan: the pairs are scripted, so the ids only have to
    /// be unattributable, and an id outside the scanned namespace is unattributable just as well as
    /// one inside it while leaving the candidate list at its planted size.
    /// </summary>
    private void PlantAmbiguousOutsideTheScan(string id)
    {
        var v = new float[Dim];
        v[Dim - 1] = 1f;
        _index.Upsert(new CognitiveEntry(id, v, ShadowNs, "flood id", tenantId: Tenant));
        _index.Upsert(new CognitiveEntry(id, v, SecondShadowNs, "flood id twin", tenantId: Tenant));
    }

    /// <summary>
    /// The sibling of <see cref="PlantAmbiguousOutsideTheScan"/> for the memo tests: an id in
    /// exactly ONE namespace of the tenant, so it is attributable and its pairs reach the neighbour
    /// memo instead of being refused by the guard one line earlier.
    ///
    /// Outside the scanned namespace for the same reason: hundreds of linked ids are needed to fill
    /// the memo, and putting them in front of the scan would change the candidate list the pairs
    /// are scripted around. Only the guard and the graph ever see them.
    /// </summary>
    private void PlantAttributableOutsideTheScan(string id)
    {
        var v = new float[Dim];
        v[Dim - 1] = 1f;
        _index.Upsert(new CognitiveEntry(id, v, ShadowNs, "linked id", tenantId: Tenant));
    }

    /// <summary>
    /// A second entry under the same bare id in another namespace of the SAME tenant — the one
    /// condition that makes an id unattributable. It is never scanned and never linked; it exists so
    /// that the (tenant, id) node the scanned entry would write on is shared with an entry the sweep
    /// was never shown.
    /// </summary>
    private void PlantTwin(string id) => PlantTwinIn(Tenant, id);

    /// <inheritdoc cref="PlantTwin"/>
    private void PlantTwinIn(string tenantId, string id)
    {
        var v = new float[Dim];
        v[Dim - 1] = 1f;
        _index.Upsert(new CognitiveEntry(id, v, ShadowNs, "another entry under the same id", tenantId: tenantId));
    }

    private void AssertLinked(string idA, string idB)
        => AssertLinkedIn(Tenant, idA, idB);

    private void AssertLinkedIn(string tenantId, string idA, string idB)
        => Assert.Contains(_graph.GetStoredEdgesForEntry(idA, tenantId: tenantId), e => IsSimilarTo(e, idA, idB));

    private void AssertNotLinked(string idA, string idB)
        => Assert.DoesNotContain(_graph.GetStoredEdgesForEntry(idA, tenantId: Tenant), e => IsSimilarTo(e, idA, idB));

    private static bool IsSimilarTo(GraphEdge edge, string idA, string idB)
        => edge.Relation == "similar_to"
           && ((edge.SourceId == idA && edge.TargetId == idB)
               || (edge.SourceId == idB && edge.TargetId == idA));
}

/// <summary>
/// A storage provider that delegates everything and runs one action the first time the graph loads
/// its persisted edges.
///
/// THE DETERMINISTIC SEAM FOR THE SINGLE-EDGE WRITE PATH. KnowledgeGraph.TryAddEdge takes no
/// enumerable from its caller, so there is nothing lazy to suspend it inside - but it does load its
/// persisted edges INSIDE its own write lock, after admission and before the first mutation, which
/// is exactly the window the attribution race lives in. Running the interfering write from there
/// makes the interleaving happen on every run, with no delay, no polling and no pair of threads
/// that could miss each other.
///
/// Fires once. The graph loads its edges once per instance, and a hook that could fire again would
/// make the test depend on how many times an implementation detail happens to call back.
/// </summary>
file sealed class EdgeLoadHook : IStorageProvider
{
    private readonly IStorageProvider _inner;
    private Action? _onFirstLoad;

    public EdgeLoadHook(IStorageProvider inner, Action? onFirstLoad)
    {
        _inner = inner;
        _onFirstLoad = onFirstLoad;
    }

    public List<GraphEdge> LoadGlobalEdges()
    {
        var edges = _inner.LoadGlobalEdges();
        var hook = _onFirstLoad;
        _onFirstLoad = null;
        hook?.Invoke();
        return edges;
    }

    public NamespaceData LoadNamespace(string ns) => _inner.LoadNamespace(ns);
    public IReadOnlyList<string> GetPersistedNamespaces() => _inner.GetPersistedNamespaces();
    public void ScheduleSave(string ns, Func<NamespaceData> dataProvider) => _inner.ScheduleSave(ns, dataProvider);
    public void SaveNamespaceSync(string ns, NamespaceData data) => _inner.SaveNamespaceSync(ns, data);
    public bool SupportsIncrementalWrites => _inner.SupportsIncrementalWrites;
    public void ScheduleUpsertEntry(string ns, CognitiveEntry entry) => _inner.ScheduleUpsertEntry(ns, entry);
    public void ScheduleDeleteEntry(string ns, string entryId) => _inner.ScheduleDeleteEntry(ns, entryId);
    public void ScheduleDeleteEntry(string ns, string entryId, string tenantId) => _inner.ScheduleDeleteEntry(ns, entryId, tenantId);
    public void ScheduleSaveGlobalEdges(Func<List<GraphEdge>> dataProvider) => _inner.ScheduleSaveGlobalEdges(dataProvider);
    public List<SemanticCluster> LoadClusters() => _inner.LoadClusters();
    public void ScheduleSaveClusters(Func<List<SemanticCluster>> dataProvider) => _inner.ScheduleSaveClusters(dataProvider);
    public List<CollapseRecord> LoadCollapseHistory() => _inner.LoadCollapseHistory();
    public void ScheduleSaveCollapseHistory(Func<List<CollapseRecord>> dataProvider) => _inner.ScheduleSaveCollapseHistory(dataProvider);
    public Dictionary<string, DecayConfig> LoadDecayConfigs() => _inner.LoadDecayConfigs();
    public void ScheduleSaveDecayConfigs(Func<Dictionary<string, DecayConfig>> dataProvider) => _inner.ScheduleSaveDecayConfigs(dataProvider);
    public HnswSnapshot? LoadHnswSnapshot(string ns) => _inner.LoadHnswSnapshot(ns);
    public void SaveHnswSnapshotSync(string ns, HnswSnapshot snapshot) => _inner.SaveHnswSnapshotSync(ns, snapshot);
    public void DeleteHnswSnapshot(string ns) => _inner.DeleteHnswSnapshot(ns);
    public Task DeleteNamespaceAsync(string ns) => _inner.DeleteNamespaceAsync(ns);
    public Task DeleteNamespaceAsync(string ns, string tenantId) => _inner.DeleteNamespaceAsync(ns, tenantId);
    public void Flush() => _inner.Flush();

    // The inner provider belongs to the fixture, which disposes it. Tearing it down here would take
    // the store out from under the rest of the test.
    public void Dispose() { }
}
