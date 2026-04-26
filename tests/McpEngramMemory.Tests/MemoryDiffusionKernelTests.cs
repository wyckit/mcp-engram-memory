using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

public class MemoryDiffusionKernelTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly MemoryDiffusionKernel _kernel;

    public MemoryDiffusionKernelTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"spine_test_{Guid.NewGuid():N}");
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
    /// Test 1 — a 4-cluster synthetic graph (32 nodes, 8 per cluster) with dense
    /// within-cluster edges and a single between-cluster bridge per pair should
    /// produce a Laplacian whose smallest eigenvalue is ~0 (single connected
    /// component) and whose top-4 eigenvectors approximately span the 4 cluster
    /// indicator functions. We verify the latter by projecting each indicator
    /// into the K=4 subspace and checking the residual is small.
    /// </summary>
    [Fact]
    public void BasisRecoversClusterStructure()
    {
        const string ns = "clusters";
        const int clusters = 4;
        const int perCluster = 8;
        SeedClusteredGraph(ns, clusters, perCluster, withinDensity: 0.6f);

        var basis = _kernel.GetBasis(ns, topK: 8);
        Assert.NotNull(basis);
        Assert.Equal(clusters * perCluster, basis!.NodeCount);

        // Smallest eigenvalue should be approximately 0 (one connected component).
        Assert.True(basis.Eigenvalues[0] < 0.05f,
            $"Smallest eigenvalue should be ~0, got {basis.Eigenvalues[0]}.");

        // Project each cluster's indicator vector into the 4-dim subspace U[:, 0:4]
        // and confirm the projection captures most of its energy.
        for (int c = 0; c < clusters; c++)
        {
            var indicator = new float[basis.NodeCount];
            for (int i = 0; i < basis.NodeCount; i++)
                indicator[i] = basis.EntryIds[i].StartsWith($"c{c}_") ? 1f : 0f;

            float origNormSq = 0f;
            for (int i = 0; i < indicator.Length; i++) origNormSq += indicator[i] * indicator[i];

            // Reconstruction in the top-4 eigenbasis: u_recon = U U^T indicator
            var projCoeffs = new float[4];
            for (int j = 0; j < 4; j++)
                for (int i = 0; i < basis.NodeCount; i++)
                    projCoeffs[j] += basis.Eigenvectors[i, j] * indicator[i];

            var recon = new float[basis.NodeCount];
            for (int i = 0; i < basis.NodeCount; i++)
                for (int j = 0; j < 4; j++)
                    recon[i] += basis.Eigenvectors[i, j] * projCoeffs[j];

            float residSq = 0f;
            for (int i = 0; i < indicator.Length; i++)
            {
                float r = indicator[i] - recon[i];
                residSq += r * r;
            }

            float ratio = residSq / origNormSq;
            Assert.True(ratio < 0.25f,
                $"Cluster {c} indicator residual ratio {ratio:F3} exceeds tolerance; basis did not recover cluster structure.");
        }
    }

    /// <summary>
    /// Test 2 — the heat-kernel filter <c>lambda -&gt; exp(-lambda * dt)</c> should
    /// be a contraction (output L2 norm never exceeds input) and should be
    /// monotonically more dissipative as dt increases. Verified on a one-hot
    /// signal in a small connected namespace.
    /// </summary>
    [Fact]
    public void HeatKernelIsContractionAndMonotonic()
    {
        const string ns = "heat";
        SeedClusteredGraph(ns, clusters: 4, perCluster: 8, withinDensity: 0.5f);

        var basis = _kernel.GetBasis(ns);
        Assert.NotNull(basis);

        var signal = new Dictionary<string, float>();
        foreach (var id in basis!.EntryIds) signal[id] = 0f;
        signal[basis.EntryIds[0]] = 1f;

        float inputNormSq = 1f; // one-hot

        float[] outNormSq = new float[5];
        float[] dts = { 0.0f, 0.1f, 0.5f, 2.0f, 10.0f };
        for (int k = 0; k < dts.Length; k++)
        {
            float dt = dts[k];
            var filtered = _kernel.ApplySpectralFilter(ns, signal, lambda => MathF.Exp(-lambda * dt));
            float ns2 = 0f;
            foreach (var id in basis.EntryIds) ns2 += filtered[id] * filtered[id];
            outNormSq[k] = ns2;
        }

        // dt=0 should be a near-identity (norm ~ 1).
        Assert.True(outNormSq[0] > 0.95f && outNormSq[0] < 1.05f,
            $"At dt=0 expected near-identity, got norm^2 {outNormSq[0]:F3}.");

        // Each subsequent dt should be a contraction.
        for (int k = 0; k < dts.Length; k++)
            Assert.True(outNormSq[k] <= inputNormSq + 1e-3f,
                $"Heat kernel at dt={dts[k]} expanded the signal: norm^2 {outNormSq[k]:F3} > {inputNormSq:F3}.");

        // Strict monotone decrease as dt grows.
        for (int k = 1; k < dts.Length; k++)
            Assert.True(outNormSq[k] < outNormSq[k - 1] + 1e-4f,
                $"Heat kernel should be monotonically dissipative; norm^2 went {outNormSq[k - 1]:F3} -> {outNormSq[k]:F3} between dt={dts[k - 1]} and dt={dts[k]}.");
    }

    /// <summary>
    /// Test 3 — the subdiffusive (alpha=0.7) and standard (alpha=1) filters
    /// disagree on a non-trivial signal, in the direction predicted by their
    /// algebra: at lambda&lt;1 the alpha=0.7 filter damps more (since
    /// lambda^0.7 &gt; lambda there), at lambda&gt;1 it damps less. We verify
    /// only that the two outputs differ measurably — the precise spectral
    /// profile is the math test, this is the plumbing test.
    /// </summary>
    [Fact]
    public void StandardAndSubdiffusiveFiltersDiffer()
    {
        const string ns = "frac";
        SeedClusteredGraph(ns, clusters: 4, perCluster: 8, withinDensity: 0.5f);

        var basis = _kernel.GetBasis(ns);
        Assert.NotNull(basis);

        var signal = new Dictionary<string, float>();
        foreach (var id in basis!.EntryIds) signal[id] = 0f;
        signal[basis.EntryIds[0]] = 1f;

        const float dt = 1.0f;
        var standard = _kernel.ApplySpectralFilter(ns, signal, lambda => MathF.Exp(-lambda * dt));
        var subdiff = _kernel.ApplySpectralFilter(ns, signal, lambda => MathF.Exp(-MathF.Pow(lambda, 0.7f) * dt));

        float diffSq = 0f;
        foreach (var id in basis.EntryIds)
        {
            float d = standard[id] - subdiff[id];
            diffSq += d * d;
        }
        Assert.True(diffSq > 1e-4f,
            $"Subdiffusive filter should produce a measurably different output; got squared diff {diffSq:E2}.");
    }

    /// <summary>
    /// Test 6 — adding an edge increments KnowledgeGraph.Revision; the next
    /// GetBasis call must detect the divergence and return a freshly computed
    /// basis (newer ComputedAt, newer GraphRevision).
    /// </summary>
    [Fact]
    public void AddingEdgeInvalidatesCachedBasis()
    {
        const string ns = "invalidate";
        SeedClusteredGraph(ns, clusters: 4, perCluster: 8, withinDensity: 0.5f);

        var first = _kernel.GetBasis(ns);
        Assert.NotNull(first);
        long firstRev = first!.GraphRevision;
        var firstComputedAt = first.ComputedAt;

        // Cache hit: same revision, same instance.
        var cached = _kernel.GetBasis(ns);
        Assert.Same(first, cached);

        // Sleep ensures ComputedAt strictly advances past resolution noise.
        Thread.Sleep(20);
        _graph.AddEdge(new GraphEdge("c0_0", "c1_0", "similar_to", 0.5f));

        var second = _kernel.GetBasis(ns);
        Assert.NotNull(second);
        Assert.True(second!.GraphRevision > firstRev,
            $"Basis should be recomputed at higher revision; was {firstRev}, now {second.GraphRevision}.");
        Assert.True(second.ComputedAt > firstComputedAt);
        Assert.NotSame(first, second);
    }

    /// <summary>
    /// Test 7 — many parallel GetBasis calls across different namespaces must
    /// not deadlock or throw. A single-namespace contention test would
    /// serialize on the per-ns lock; multi-namespace is the parallel case.
    /// </summary>
    [Fact]
    public void ParallelGetBasisAcrossNamespacesSucceeds()
    {
        const int nsCount = 10;
        for (int n = 0; n < nsCount; n++)
            SeedClusteredGraph($"par_{n}", clusters: 4, perCluster: 8, withinDensity: 0.5f);

        var bases = new DiffusionBasis?[nsCount];
        Parallel.For(0, nsCount, n =>
        {
            bases[n] = _kernel.GetBasis($"par_{n}");
        });

        for (int n = 0; n < nsCount; n++)
        {
            Assert.NotNull(bases[n]);
            Assert.Equal($"par_{n}", bases[n]!.Namespace);
        }
    }

    /// <summary>
    /// Test 8 — namespaces below the spectral-method threshold must return
    /// null from GetBasis, signalling callers to fall back to non-spectral
    /// behavior. Two regimes: too few nodes, and too few positive-relation edges.
    /// </summary>
    [Fact]
    public void ColdStartReturnsNullForTinyNamespace()
    {
        const string ns = "tiny";
        for (int i = 0; i < 10; i++)
            _index.Upsert(new CognitiveEntry($"t_{i}", new[] { 1f, 0f }, ns, $"entry {i}"));
        _graph.AddEdge(new GraphEdge("t_0", "t_1", "similar_to"));

        var basis = _kernel.GetBasis(ns);
        Assert.Null(basis);
    }

    /// <summary>
    /// Diagnostic — verify U columns are mutually orthonormal. The downstream
    /// projection identities (heat-kernel contraction, etc.) all depend on this.
    /// </summary>
    [Fact]
    public void EigenvectorColumnsAreOrthonormal()
    {
        const string ns = "ortho";
        SeedClusteredGraph(ns, clusters: 4, perCluster: 8, withinDensity: 0.5f);
        var basis = _kernel.GetBasis(ns, topK: 8);
        Assert.NotNull(basis);

        int n = basis!.NodeCount;
        int k = basis.TopK;
        float maxDiagDev = 0f;
        float maxOffDiag = 0f;
        for (int a = 0; a < k; a++)
        {
            for (int b = 0; b < k; b++)
            {
                float dot = 0f;
                for (int i = 0; i < n; i++) dot += basis.Eigenvectors[i, a] * basis.Eigenvectors[i, b];
                if (a == b) maxDiagDev = MathF.Max(maxDiagDev, MathF.Abs(dot - 1f));
                else maxOffDiag = MathF.Max(maxOffDiag, MathF.Abs(dot));
            }
        }
        // Float-precision tolerance: 1e-3 is generous; 1e-5 would be strict.
        Assert.True(maxDiagDev < 1e-3f, $"||u_j||^2 - 1 deviation: {maxDiagDev}");
        Assert.True(maxOffDiag < 1e-3f, $"max |<u_a, u_b>| for a!=b: {maxOffDiag}");
    }

    /// <summary>
    /// Bonus — contradicts edges must NOT contribute to the Laplacian. A graph
    /// with two clusters connected only by 'contradicts' edges should still be
    /// treated as two disconnected components, which surfaces as a second
    /// near-zero eigenvalue (multiplicity = component count).
    /// </summary>
    [Fact]
    public void ContradictsEdgesAreExcluded()
    {
        const string ns = "contradict";
        SeedClusteredGraph(ns, clusters: 2, perCluster: 16, withinDensity: 0.5f, addBridges: false);

        // Add many contradicts edges between the clusters; they must not connect them.
        for (int i = 0; i < 8; i++)
            _graph.AddEdge(new GraphEdge($"c0_{i}", $"c1_{i}", "contradicts", 1.0f));

        var basis = _kernel.GetBasis(ns);
        Assert.NotNull(basis);

        // Two disconnected components -> multiplicity 2 at eigenvalue 0.
        Assert.True(basis!.Eigenvalues[0] < 0.05f);
        Assert.True(basis.Eigenvalues[1] < 0.05f,
            $"Two components should give two near-zero eigenvalues; got [{basis.Eigenvalues[0]}, {basis.Eigenvalues[1]}].");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a synthetic clustered graph in <paramref name="ns"/>: <paramref name="clusters"/>
    /// groups of <paramref name="perCluster"/> nodes each. Within each cluster, every pair
    /// is linked with probability <paramref name="withinDensity"/>. If
    /// <paramref name="addBridges"/> is true, one weak edge is added between consecutive clusters
    /// to keep the graph connected.
    /// </summary>
    private void SeedClusteredGraph(string ns, int clusters, int perCluster, float withinDensity, bool addBridges = true)
    {
        var rng = new Random(12345);
        for (int c = 0; c < clusters; c++)
        {
            for (int i = 0; i < perCluster; i++)
                _index.Upsert(new CognitiveEntry($"c{c}_{i}", new[] { (float)c, (float)i }, ns, $"cluster {c} member {i}"));
        }

        for (int c = 0; c < clusters; c++)
        {
            for (int i = 0; i < perCluster; i++)
                for (int j = i + 1; j < perCluster; j++)
                    if (rng.NextDouble() < withinDensity)
                        _graph.AddEdge(new GraphEdge($"c{c}_{i}", $"c{c}_{j}", "similar_to", 1.0f));
        }

        if (addBridges)
        {
            for (int c = 0; c + 1 < clusters; c++)
                _graph.AddEdge(new GraphEdge($"c{c}_0", $"c{c + 1}_0", "cross_reference", 0.1f));
        }
    }
}
