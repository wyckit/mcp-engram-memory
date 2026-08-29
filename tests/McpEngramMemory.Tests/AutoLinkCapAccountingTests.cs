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

        // Nothing here is ambiguous, so pre-screening must be invisible: three pairs offered, the
        // top two written, the cap flagged, and the count equal to the graph.
        Assert.Equal(3, capped.PairsExamined);
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
        Assert.Equal(12, result.PairsExamined);
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

        Assert.Equal(3, capped.PairsExamined);
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

        int retained = -1;
        var scanner = ScannerOver(FloodThenOneViablePair(floodIds), r => retained = r);
        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 2, tenantId: Tenant);

        int floodPairs = floodIds * (floodIds - 1) / 2;
        Assert.Equal(floodPairs + 1, result.PairsExamined);

        // O(cap), not O(pairs). The ranking buffer is 2*cap + slack = 12 at this cap, and it is the
        // only thing the loop accumulates; the flood contributes nothing to it because a refused
        // candidate never enters it. A scan-wide set keyed by pair would report 44,851 here.
        Assert.InRange(retained, 0, 12);
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

        int retained = -1;
        var scanner = ScannerOver(script, r => retained = r);
        var result = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant);

        Assert.Equal(1, result.EdgesCreated);
        Assert.True(result.HitMaxEdgeCap);
        AssertLinked("p11-a", "p11-b");
        Assert.InRange(retained, 0, 10);
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

            Assert.Equal(0, early.PairsExamined);
            Assert.Equal(0, early.EdgesCreated);
            Assert.False(early.HitMaxEdgeCap);

            // The third outcome, stated rather than folded into one of the other two: nothing was
            // written and the cap was not binding, but this is NOT "nothing left to link".
            Assert.True(early.PairScanIncomplete);
        }

        var reached = scanner.Scan(ScanNs, threshold: 0.85f, maxNewEdges: 1, tenantId: Tenant,
            maxPairComparisons: 8);

        Assert.Equal(1, reached.PairsExamined);
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

        Assert.Equal(11, result.PairsExamined);
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

        Assert.Equal(3, capped.PairsExamined);
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
    /// As <see cref="ScannerOver"/>, plus the number of candidate tuples the pair loop still held
    /// when it ended. That number is the whole memory claim: the scan walks every pair in its
    /// window, and what it may not do is remember them.
    /// </summary>
    private AutoLinkScanner ScannerOver(
        IEnumerable<(string IdA, string IdB, float Similarity)> script, Action<int> onRetention)
        => new(_index, _graph, new DuplicateDetector(), pairs: (_, _, _, _) => script,
            logger: null, onLoopRetention: onRetention);

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
    /// A second entry under the same bare id in another namespace of the SAME tenant — the one
    /// condition that makes an id unattributable. It is never scanned and never linked; it exists so
    /// that the (tenant, id) node the scanned entry would write on is shared with an entry the sweep
    /// was never shown.
    /// </summary>
    private void PlantTwin(string id)
    {
        var v = new float[Dim];
        v[Dim - 1] = 1f;
        _index.Upsert(new CognitiveEntry(id, v, ShadowNs, "another entry under the same id", tenantId: Tenant));
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
