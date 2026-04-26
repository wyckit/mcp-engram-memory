using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class AutoLinkScannerTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly DuplicateDetector _duplicateDetector;
    private readonly AutoLinkScanner _scanner;

    public AutoLinkScannerTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"autolink_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
        _graph = new KnowledgeGraph(_persistence, _index);
        _duplicateDetector = new DuplicateDetector();
        _scanner = new AutoLinkScanner(_index, _graph, _duplicateDetector);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    /// <summary>
    /// Auto-link should find clearly-similar pairs (cosine &gt;= threshold) and create
    /// similar_to edges between them. Setup: 5 pairs of near-duplicate vectors
    /// plus filler. Each pair should get exactly one similar_to edge.
    /// </summary>
    [Fact]
    public void CreatesSimilarToEdgesForNearDuplicates()
    {
        const string ns = "alike";
        const int pairs = 5;
        const int filler = 30;
        const int dim = 64;

        var rng = new Random(11);
        for (int p = 0; p < pairs; p++)
        {
            var basis = RandomUnit(rng, dim);
            var noisy = (float[])basis.Clone();
            for (int i = 0; i < dim; i++) noisy[i] += (float)(rng.NextDouble() - 0.5) * 0.1f;
            _index.Upsert(new CognitiveEntry($"a_{p}", basis, ns, $"a {p}"));
            _index.Upsert(new CognitiveEntry($"b_{p}", noisy, ns, $"b {p}"));
        }
        for (int f = 0; f < filler; f++)
            _index.Upsert(new CognitiveEntry($"f_{f}", RandomUnit(rng, dim), ns, $"filler {f}"));

        var edgesBefore = _graph.EdgeCount;
        var result = _scanner.Scan(ns, threshold: 0.85f);
        var edgesAfter = _graph.EdgeCount;

        Assert.True(result.EdgesCreated >= pairs,
            $"Expected at least {pairs} edges (one per planted pair); got {result.EdgesCreated}.");
        Assert.Equal(result.EdgesCreated, edgesAfter - edgesBefore);

        // Each planted pair should now have an edge between them.
        for (int p = 0; p < pairs; p++)
        {
            var neighbors = _graph.GetNeighbors($"a_{p}", direction: "both");
            Assert.Contains(neighbors.Neighbors, n => n.Entry.Id == $"b_{p}");
        }
    }

    /// <summary>
    /// Auto-link must skip pairs that already have any edge between them, in
    /// either direction, regardless of relation type. Setup: a near-duplicate
    /// pair already linked by a manual contradicts edge — auto-link must NOT
    /// add a redundant similar_to edge.
    /// </summary>
    [Fact]
    public void SkipsPairsWithExistingEdges()
    {
        const string ns = "preexisting";
        const int dim = 32;

        var rng = new Random(22);
        var basis = RandomUnit(rng, dim);
        var noisy = (float[])basis.Clone();
        for (int i = 0; i < dim; i++) noisy[i] += (float)(rng.NextDouble() - 0.5) * 0.05f;
        _index.Upsert(new CognitiveEntry("x", basis, ns));
        _index.Upsert(new CognitiveEntry("y", noisy, ns));
        for (int f = 0; f < 30; f++)
            _index.Upsert(new CognitiveEntry($"f_{f}", RandomUnit(rng, dim), ns));

        // Manual contradicts edge between x and y.
        _graph.AddEdge(new GraphEdge("x", "y", "contradicts", 1.0f));

        var result = _scanner.Scan(ns, threshold: 0.85f);

        // Pair (x, y) should appear in pairsExamined but be skipped.
        Assert.True(result.PairsExamined >= 1);
        Assert.True(result.EdgesSkippedExisting >= 1);

        // No similar_to edge between x and y should exist.
        var xEdges = _graph.GetEdgesForEntry("x");
        Assert.DoesNotContain(xEdges, e => e.Relation == "similar_to" && (e.TargetId == "y" || e.SourceId == "y"));
    }

    /// <summary>
    /// Re-running the scan must not duplicate edges — first scan creates them,
    /// second scan finds them already present and skips. Idempotency is what
    /// makes the background service safe to run on every interval.
    /// </summary>
    [Fact]
    public void RescanIsIdempotent()
    {
        const string ns = "idempotent";
        const int dim = 32;

        var rng = new Random(33);
        for (int p = 0; p < 4; p++)
        {
            var basis = RandomUnit(rng, dim);
            var noisy = (float[])basis.Clone();
            for (int i = 0; i < dim; i++) noisy[i] += (float)(rng.NextDouble() - 0.5) * 0.05f;
            _index.Upsert(new CognitiveEntry($"a_{p}", basis, ns));
            _index.Upsert(new CognitiveEntry($"b_{p}", noisy, ns));
        }
        for (int f = 0; f < 30; f++)
            _index.Upsert(new CognitiveEntry($"f_{f}", RandomUnit(rng, dim), ns));

        var first = _scanner.Scan(ns, threshold: 0.85f);
        var edgesAfterFirst = _graph.EdgeCount;
        Assert.True(first.EdgesCreated >= 4);

        var second = _scanner.Scan(ns, threshold: 0.85f);
        var edgesAfterSecond = _graph.EdgeCount;

        Assert.Equal(0, second.EdgesCreated);
        Assert.Equal(edgesAfterFirst, edgesAfterSecond);
        Assert.True(second.EdgesSkippedExisting >= first.EdgesCreated);
    }

    /// <summary>
    /// The per-scan edge cap must be honored. Dense namespace with many
    /// above-threshold pairs; ask for at most 3 new edges; verify the cap and
    /// the HitMaxEdgeCap flag.
    /// </summary>
    [Fact]
    public void HonorsMaxEdgeCap()
    {
        const string ns = "capped";
        const int dim = 32;

        var rng = new Random(44);
        // 10 base vectors, each cloned with small noise → 10 pairs of near-twins.
        for (int p = 0; p < 10; p++)
        {
            var basis = RandomUnit(rng, dim);
            var noisy = (float[])basis.Clone();
            for (int i = 0; i < dim; i++) noisy[i] += (float)(rng.NextDouble() - 0.5) * 0.05f;
            _index.Upsert(new CognitiveEntry($"a_{p}", basis, ns));
            _index.Upsert(new CognitiveEntry($"b_{p}", noisy, ns));
        }

        var result = _scanner.Scan(ns, threshold: 0.85f, maxNewEdges: 3);

        Assert.True(result.EdgesCreated <= 3, $"Expected cap of 3, got {result.EdgesCreated}.");
        Assert.True(result.HitMaxEdgeCap);
    }

    /// <summary>
    /// Tiny namespace (&lt;2 entries) returns gracefully without scanning.
    /// </summary>
    [Fact]
    public void EmptyOrTinyNamespaceNoOps()
    {
        var result0 = _scanner.Scan("nonexistent", threshold: 0.85f);
        Assert.Equal(0, result0.EdgesCreated);

        _index.Upsert(new CognitiveEntry("solo", new[] { 1f, 0f, 0f }, "tiny", "lone"));
        var result1 = _scanner.Scan("tiny", threshold: 0.85f);
        Assert.Equal(0, result1.EdgesCreated);
    }

    private static float[] RandomUnit(Random rng, int d)
    {
        var v = new float[d];
        float ns = 0f;
        for (int i = 0; i < d; i++)
        {
            v[i] = (float)(rng.NextDouble() - 0.5);
            ns += v[i] * v[i];
        }
        float inv = 1f / MathF.Sqrt(ns);
        for (int i = 0; i < d; i++) v[i] *= inv;
        return v;
    }
}
