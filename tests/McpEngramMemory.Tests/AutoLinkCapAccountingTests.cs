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

    // Four planted pairs, two dimensions each.
    private const int Dim = 8;

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

    // ── fixtures ──

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
