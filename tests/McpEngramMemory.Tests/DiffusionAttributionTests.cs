using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// The diffusion basis must be built from the ATTRIBUTABLE edge view, and it must notice when
/// attribution changes underneath it.
///
/// A <see cref="GraphEdge"/> carries no namespace, so a stored edge is a claim about two BARE ids.
/// Restricting the endpoints to one namespace — which is all the kernel's <c>indexOf</c> filter
/// does — proves only that entries bearing those ids exist there; it never proves the edge was
/// created between THOSE entries, because an edge written between another principal's private twins
/// is byte-identical. Reading the stored view therefore let one namespace inherit a whole basis
/// sourced from another's private topology, and the entries it silently co-boosted were the
/// inheriting namespace's own — so nothing ever surfaced an id to give the import away.
///
/// The second half is that switching the accessor is not enough on its own. Inserting a twin writes
/// no edge, so the graph revision the cache watched did not move, and a basis computed while the
/// ids were unique was served unchanged after the attributable view had emptied.
/// </summary>
public sealed class DiffusionAttributionTests : IDisposable
{
    private const string Tenant = "acme";

    /// <summary>The namespace whose topology is built, and whose ids get twinned.</summary>
    private const string PrivateNs = "alice-private";

    /// <summary>The namespace that gets the twins. It never creates an edge of its own.</summary>
    private const string SharedNs = "bob-shared";

    /// <summary>Exactly <see cref="MemoryDiffusionKernel.MinimumNodesForSpectral"/>, so the fixture qualifies.</summary>
    private const int NodeCount = 32;

    private static readonly string[] AllIds =
        Enumerable.Range(0, NodeCount).Select(Id).ToArray();

    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly MemoryDiffusionKernel _kernel;

    public DiffusionAttributionTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"diffusion_attribution_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _kernel = new MemoryDiffusionKernel(_index, _graph);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    /// <summary>
    /// THE REVIEWER'S REPRODUCTION. Alice builds a qualifying namespace while every id is unique.
    /// Bob then acquires a twin of each of those ids and creates no edge at all. Bob's basis must
    /// not exist: every edge that could have supplied one was written inside Alice's namespace, and
    /// the bare ids on it name Bob's entries exactly as well as they name Alice's.
    ///
    /// Bob's basis has never been requested before this point, so no cache entry exists for it —
    /// the outcome here is decided purely by which edge view the kernel reads.
    /// </summary>
    [Fact]
    public void TwinnedNamespaceDoesNotInheritTheOriginalsTopology()
    {
        SeedQualifyingNamespace(PrivateNs);

        // The fixture genuinely qualifies: without this, "Bob has no basis" would prove nothing.
        var aliceBasis = _kernel.GetBasis(PrivateNs, tenantId: Tenant);
        Assert.NotNull(aliceBasis);
        Assert.Equal(NodeCount, aliceBasis!.NodeCount);
        Assert.True(aliceBasis.EdgeCount >= MemoryDiffusionKernel.MinimumEdgesForSpectral,
            $"Fixture must clear the edge threshold; got {aliceBasis.EdgeCount}.");

        SeedTwins(SharedNs);

        // Every edge is still stored — the twins removed no topology, they removed the ability to
        // say whose it is. That gap is exactly what the stored view handed to Bob.
        Assert.NotEmpty(_graph.GetStoredEdges(tenantId: Tenant));
        Assert.Empty(_graph.GetAllEdges(tenantId: Tenant));

        var bobBasis = _kernel.GetBasis(SharedNs, tenantId: Tenant);
        Assert.Null(bobBasis);
        Assert.Null(_kernel.GetStats(SharedNs, tenantId: Tenant));

        // The kernel's real output surface, and the strongest available form of "no id from the
        // private namespace appears in anything it returns": every id Alice linked is in this
        // signal, and the annihilating filter would drive every in-basis id to zero — the seed
        // included — the moment a basis existed to project through. Identity out means no value
        // moved between Bob's entries on the strength of Alice's links, and the key count means the
        // kernel introduced no id of its own.
        var signal = AllIds.ToDictionary(id => id, _ => 0f);
        signal[Id(0)] = 1f;
        var filtered = _kernel.ApplySpectralFilter(SharedNs, signal, _ => 0f, tenantId: Tenant);

        Assert.Equal(signal.Count, filtered.Count);
        foreach (var (id, expected) in signal)
            Assert.Equal(expected, filtered[id]);
    }

    /// <summary>
    /// FINDING 2's REPRODUCTION. A basis computed while the ids were unique must not survive a twin
    /// insert. Nothing about the graph changes here — no edge is added, removed or reweighted — so
    /// the graph revision the cache used to watch stays exactly where it was, and a freshness test
    /// built on it alone keeps serving the basis it should have thrown away.
    ///
    /// This fails if only the edge accessor is fixed: the recomputation that would consult the now
    /// empty attributable view never happens.
    /// </summary>
    [Fact]
    public void TwinInsertInvalidatesABasisComputedWhileIdsWereUnique()
    {
        SeedQualifyingNamespace(PrivateNs);

        var first = _kernel.GetBasis(PrivateNs, tenantId: Tenant);
        Assert.NotNull(first);
        Assert.Same(first, _kernel.GetBasis(PrivateNs, tenantId: Tenant));

        long graphRevisionBefore = _graph.RevisionFor(Tenant);
        int storedEdgesBefore = _graph.GetStoredEdges(tenantId: Tenant).Count;

        SeedTwins(SharedNs);

        // The premise of the finding, asserted rather than assumed: attribution moved and the graph
        // did not, so nothing the old freshness test could see has changed.
        Assert.Equal(graphRevisionBefore, _graph.RevisionFor(Tenant));
        Assert.Equal(storedEdgesBefore, _graph.GetStoredEdges(tenantId: Tenant).Count);
        Assert.Empty(_graph.GetAllEdges(tenantId: Tenant));

        // The accepted consequence, in the open: the namespace that owns the topology loses its
        // basis too, because its own edges are no longer attributable to it either. Fail-closed is
        // the point — the alternative was serving Bob a copy of it.
        Assert.Null(_kernel.GetBasis(PrivateNs, tenantId: Tenant));
    }

    /// <summary>
    /// The reverse transition. Retiring the twin makes the id name one entry again, so the edges
    /// become attributable and the basis comes back — recomputed, not resurrected from the copy
    /// that was cached before the twin landed.
    ///
    /// The graph revision is identical at both ends, which is what makes this a statement about
    /// attribution alone: nothing else in the freshness test moved in either direction.
    /// </summary>
    [Fact]
    public void RetiringTheTwinRestoresTheBasis()
    {
        SeedQualifyingNamespace(PrivateNs);

        var first = _kernel.GetBasis(PrivateNs, tenantId: Tenant);
        Assert.NotNull(first);

        SeedTwins(SharedNs);
        Assert.Null(_kernel.GetBasis(PrivateNs, tenantId: Tenant));

        foreach (var id in AllIds)
            Assert.True(_index.Delete(id, SharedNs, tenantId: Tenant), $"Twin '{id}' should have been removed.");

        var restored = _kernel.GetBasis(PrivateNs, tenantId: Tenant);
        Assert.NotNull(restored);
        Assert.NotSame(first, restored);
        Assert.Equal(first!.EdgeCount, restored!.EdgeCount);
        Assert.Equal(first.NodeCount, restored.NodeCount);
        Assert.Equal(first.GraphRevision, restored.GraphRevision);
    }

    /// <summary>
    /// OVER-CORRECTION CONTROL. With no duplicate anywhere, the basis is built exactly as before and
    /// is still served from cache across calls — including across ordinary entry writes, which are
    /// the writes a cache keyed on "something was tracked" would have thrown it away for.
    ///
    /// Without this, a fix that simply stopped caching would pass the invalidation test above while
    /// paying a full eigendecomposition on every decay cycle.
    /// </summary>
    [Fact]
    public void OrdinaryEntryWritesDoNotInvalidateTheBasis()
    {
        SeedQualifyingNamespace(PrivateNs);

        var first = _kernel.GetBasis(PrivateNs, tenantId: Tenant);
        Assert.NotNull(first);
        Assert.Same(first, _kernel.GetBasis(PrivateNs, tenantId: Tenant));

        long attributionBefore = _index.AttributionRevisionFor(Tenant);

        // Three writes, none of which can make any id ambiguous: a brand-new id in this namespace,
        // a re-upsert of an id that already lives here, and a brand-new id in another namespace of
        // the same tenant.
        _index.Upsert(new CognitiveEntry("fresh-id", [0.5f, 0.5f], PrivateNs, "fresh", tenantId: Tenant));
        _index.Upsert(new CognitiveEntry(Id(0), [1f, 0f], PrivateNs, "rewritten", tenantId: Tenant));
        _index.Upsert(new CognitiveEntry("other-ns-id", [0f, 1f], SharedNs, "elsewhere", tenantId: Tenant));

        // No bucket crossed the ambiguity boundary, so nothing derived from attribution is stale.
        Assert.Equal(attributionBefore, _index.AttributionRevisionFor(Tenant));

        // Same instance: still the cached basis, not a silent recomputation.
        Assert.Same(first, _kernel.GetBasis(PrivateNs, tenantId: Tenant));

        var stats = _kernel.GetStats(PrivateNs, tenantId: Tenant);
        Assert.NotNull(stats);
        Assert.False(stats!.Stale);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static string Id(int index) => $"n{index:D2}";

    /// <summary>
    /// Seed a qualifying namespace: 32 nodes (the spectral minimum) wired into a <c>similar_to</c>
    /// ring with <c>cross_reference</c> chords, the same fixture shape the deflation suite uses.
    ///
    /// Fully deterministic and structurally connected on purpose. The ring guarantees no node is
    /// deflated as isolated, so "32 linked nodes" is a property of the construction rather than of
    /// a random draw, and the chords break the ring's symmetry so the eigenproblem is not
    /// degenerate. Every edge is written while its endpoint ids are still unique in the tenant —
    /// the only state in which <see cref="KnowledgeGraph.AddEdge"/> accepts them at all, and the
    /// state a real deployment is in before anyone mints a twin.
    /// </summary>
    private void SeedQualifyingNamespace(string ns)
    {
        for (int i = 0; i < NodeCount; i++)
            _index.Upsert(new CognitiveEntry(Id(i), [(float)i, 1f], ns, $"node {i}", tenantId: Tenant));

        for (int i = 0; i < NodeCount; i++)
        {
            _graph.AddEdge(new GraphEdge(
                Id(i), Id((i + 1) % NodeCount), "similar_to", weight: 1.0f, tenantId: Tenant));
        }

        for (int i = 0; i < NodeCount; i += 5)
        {
            _graph.AddEdge(new GraphEdge(
                Id(i), Id((i + 13) % NodeCount), "cross_reference", weight: 0.5f, tenantId: Tenant));
        }
    }

    /// <summary>
    /// Give every seeded id a twin in <paramref name="ns"/>, in the SAME tenant. Deliberately only
    /// entry writes: no edge is created, so a consumer watching graph topology alone sees nothing
    /// happen while every one of those ids stops naming a single entry.
    /// </summary>
    private void SeedTwins(string ns)
    {
        for (int i = 0; i < NodeCount; i++)
            _index.Upsert(new CognitiveEntry(Id(i), [1f, 0f], ns, $"twin {i}", tenantId: Tenant));
    }
}
