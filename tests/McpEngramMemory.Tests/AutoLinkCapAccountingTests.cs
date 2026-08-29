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
/// so the remaining tests pin both halves of the resulting contract: rescans reach every viable pair
/// however many ineligible ones stand in front of it, and one scan still costs ONE pairwise pass.
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
    /// The window the detector was asked for used to be a fixed <c>2*cap + PairPoolSlack</c>, and the
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
    /// buffer the scan settles its ranking in holds <c>2*cap + PairPoolSlack</c> = ten, so a viable
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

    // ── fixtures ──

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
        => new(_index, _graph, new DuplicateDetector(), pairs: (_, _) => script);

    /// <summary>
    /// The real detector with a tally of how many times a pairwise pass was STARTED. It counts the
    /// call, not the pairs, because the quadratic cost belongs to the call.
    /// </summary>
    private AutoLinkScanner CountingScanner(Action onPass)
    {
        var detector = new DuplicateDetector();
        return new AutoLinkScanner(_index, _graph, detector, pairs: (candidates, threshold) =>
        {
            onPass();
            return detector.StreamDuplicates(candidates, threshold);
        });
    }

    /// <summary>
    /// Two entries in the scanned namespace at cosine 1/sqrt(1+skew^2), laid on the two dimensions
    /// belonging to <paramref name="slot"/>. Every other slot is exactly orthogonal to this one, so
    /// a pair's rank is a pure function of its skew and no cross-pair can drift above the threshold
    /// and reorder the candidates a test is reasoning about.
    /// </summary>
    private void PlantPair(int slot, string idA, string idB, float skew)
    {
        var a = new float[Dim];
        var b = new float[Dim];
        a[slot * 2] = 1f;
        b[slot * 2] = 1f;
        b[(slot * 2) + 1] = skew;

        _index.Upsert(new CognitiveEntry(idA, a, ScanNs, $"{idA} text", tenantId: Tenant));
        _index.Upsert(new CognitiveEntry(idB, b, ScanNs, $"{idB} text", tenantId: Tenant));
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
        => Assert.Contains(_graph.GetStoredEdgesForEntry(idA, tenantId: Tenant), e => IsSimilarTo(e, idA, idB));

    private void AssertNotLinked(string idA, string idB)
        => Assert.DoesNotContain(_graph.GetStoredEdgesForEntry(idA, tenantId: Tenant), e => IsSimilarTo(e, idA, idB));

    private static bool IsSimilarTo(GraphEdge edge, string idA, string idB)
        => edge.Relation == "similar_to"
           && ((edge.SourceId == idA && edge.TargetId == idB)
               || (edge.SourceId == idB && edge.TargetId == idA));
}
