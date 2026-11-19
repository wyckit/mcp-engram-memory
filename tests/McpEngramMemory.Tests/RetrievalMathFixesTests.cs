using System.Net;
using System.Text;
using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Core.Services.Synthesis;

namespace McpEngramMemory.Tests;

/// <summary>
/// Regression tests for the 2.1.x retrieval-math hardening pass covering:
/// HybridSearchEngine, VectorQuantizer, PorterStemmer, PhysicsEngine,
/// MemoryDiffusionKernel / RandomizedEigensolver, SpreadingActivationService,
/// ExpertDispatcher, OllamaClient, and SynthesisEngine.
/// </summary>
public sealed class RetrievalMathFixesTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"retrieval_math_fixes_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private string SubDir(string name)
    {
        var path = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 1. HybridSearchEngine — BM25 pre-truncation filter & overload
    // ══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(10)]   // RRF fusion path (entryCount < 100)
    [InlineData(150)]  // Cascade path (entryCount >= 100)
    public void HybridSearch_CategoryFilter_DoesNotLoseEligibleBm25MatchesToPreTruncation(int entryCount)
    {
        const string ns = "cat_filter";
        const string query = "quantum calibration";
        var bm25 = new BM25Index();
        var entries = new Dictionary<string, CognitiveEntry>();

        // Seed 35 high-BM25 "reference" entries that will fill any unfiltered top-20 BM25 window.
        for (int i = 0; i < 35; i++)
        {
            var refEntry = new CognitiveEntry(
                $"ref-{i:D2}", [0.6f, 0.8f], ns,
                "quantum calibration quantum calibration quantum calibration reference note",
                category: "reference", lifecycleState: "ltm");
            entries[refEntry.Id] = refEntry;
            bm25.Index(refEntry);
        }

        // Seed 2 eligible "decision" entries with slightly lower BM25 score (so they rank #36-#37 unfiltered).
        var dec1 = new CognitiveEntry("dec-1", [0.85f, 0.5268f], ns,
            "Architecture note describing quantum calibration policy in detail for production services",
            category: "decision", lifecycleState: "ltm");
        var dec2 = new CognitiveEntry("dec-2", [0.80f, 0.6f], ns,
            "Secondary quantum calibration threshold decision recorded after review",
            category: "decision", lifecycleState: "ltm");
        entries[dec1.Id] = dec1;
        entries[dec2.Id] = dec2;
        bm25.Index(dec1);
        bm25.Index(dec2);

        // Supply only one unrelated vector hit so dec-1 and dec-2 MUST be rescued by BM25.
        var vectorHit = new CognitiveSearchResult("vec-only", "background context", 0.65f, "ltm", 0f,
            "decision", null, false, null, 1);

        var engine = new HybridSearchEngine();
        var results = engine.HybridSearch(
            [vectorHit], query, ns, k: 5,
            includeStates: null, category: "decision",
            rerank: false, rrfK: 60, bm25: bm25, reranker: new TokenReranker(),
            getEntry: (id, _) => entries.GetValueOrDefault(id),
            queryVector: [1f, 0f], entryCount: entryCount);

        Assert.Contains(results, r => r.Id == "dec-1");
        Assert.Contains(results, r => r.Id == "dec-2");
        Assert.DoesNotContain(results, r => r.Id.StartsWith("ref-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(10)]   // RRF fusion path
    [InlineData(150)]  // Cascade path
    public void HybridSearch_StateFilter_DoesNotLoseLiveBm25MatchesBehindArchivedEntries(int entryCount)
    {
        const string ns = "state_filter";
        const string query = "idempotency token";
        var bm25 = new BM25Index();
        var entries = new Dictionary<string, CognitiveEntry>();

        for (int i = 0; i < 35; i++)
        {
            var archived = new CognitiveEntry(
                $"arch-{i:D2}", [0.7f, 0.714f], ns,
                "idempotency token idempotency token idempotency token archived log",
                lifecycleState: "archived");
            entries[archived.Id] = archived;
            bm25.Index(archived);
        }

        var live = new CognitiveEntry(
            "live-hit", [0.85f, 0.5268f], ns,
            "Current payment handler requires an idempotency token on every retry request",
            lifecycleState: "ltm");
        entries[live.Id] = live;
        bm25.Index(live);

        var vectorHit = new CognitiveSearchResult("vec-live", "payment handler overview", 0.60f, "ltm", 0f,
            null, null, false, null, 1);

        var engine = new HybridSearchEngine();
        var results = engine.HybridSearch(
            [vectorHit], query, ns, k: 5,
            includeStates: new HashSet<string> { "stm", "ltm" }, category: null,
            rerank: false, rrfK: 60, bm25: bm25, reranker: new TokenReranker(),
            getEntry: (id, _) => entries.GetValueOrDefault(id),
            queryVector: [1f, 0f], entryCount: entryCount);

        Assert.Contains(results, r => r.Id == "live-hit");
        Assert.DoesNotContain(results, r => r.Id.StartsWith("arch-", StringComparison.Ordinal));
    }

    [Fact]
    public void HybridSearch_ExplicitBm25IncludeIdsOverload_ScopesKeywordSearch()
    {
        const string ns = "include_ids";
        const string query = "telemetry pipeline";
        var bm25 = new BM25Index();
        var allowed = new CognitiveEntry("allowed", [0.8f, 0.6f], ns, "telemetry pipeline exporter", lifecycleState: "ltm");
        var excluded = new CognitiveEntry("excluded", [0.9f, 0.436f], ns, "telemetry pipeline telemetry pipeline", lifecycleState: "ltm");
        var entries = new Dictionary<string, CognitiveEntry>
        {
            [allowed.Id] = allowed,
            [excluded.Id] = excluded
        };
        bm25.Index(allowed);
        bm25.Index(excluded);

        var vectorHit = new CognitiveSearchResult("vec", "general metrics", 0.60f, "ltm", 0f,
            null, null, false, null, 1);

        var engine = new HybridSearchEngine();
        var results = engine.HybridSearch(
            [vectorHit], query, ns, k: 5,
            includeStates: null, category: null,
            rerank: false, rrfK: 60, bm25: bm25, reranker: new TokenReranker(),
            getEntry: (id, _) => entries.GetValueOrDefault(id),
            queryVector: [1f, 0f], entryCount: 2,
            bm25IncludeIds: new HashSet<string> { "allowed" });

        Assert.Contains(results, r => r.Id == "allowed");
        Assert.DoesNotContain(results, r => r.Id == "excluded");
    }

    [Fact]
    public void DiversityReranker_NormalizesRrfScaleRelevanceWithoutMutatingReturnedScores()
    {
        // RRF scores (~0.016-0.033) are ~30x smaller than pairwise cosine redundancy [0, 1].
        // Candidate 'b' has strong RRF relevance (0.032) and moderate redundancy (cos=0.5 with 'a');
        // candidate 'c' has half the RRF relevance (0.016) and slightly lower redundancy (cos=0.2 with 'a').
        // With lambda=0.6:
        //   Normalized:  b = 0.6*(0.032/0.033) - 0.4*0.5 = 0.382; c = 0.6*(0.016/0.033) - 0.4*0.2 = 0.211 -> 'b' wins.
        //   Unnormalized: b = 0.6*0.032 - 0.4*0.5 = -0.181;        c = 0.6*0.016 - 0.4*0.2 = -0.070 -> 'c' wrongly won!
        var vectors = new Dictionary<string, float[]>
        {
            ["a"] = [1f, 0f],
            ["b"] = [0.5f, 0.8660254f],
            ["c"] = [0.2f, 0.9797959f]
        };
        var input = new[]
        {
            new CognitiveSearchResult("a", "top", 0.033f, "ltm", 0f, null, null, false, null, 1),
            new CognitiveSearchResult("b", "strong-relevant", 0.032f, "ltm", 0f, null, null, false, null, 1),
            new CognitiveSearchResult("c", "weak-diverse", 0.016f, "ltm", 0f, null, null, false, null, 1)
        };

        var reranked = DiversityReranker.Rerank(input, [1f, 0f], id => vectors.GetValueOrDefault(id), k: 2, lambda: 0.6f);

        Assert.Equal(["a", "b"], reranked.Select(r => r.Id).ToArray());
        // Original scores preserved untouched.
        Assert.Equal(0.033f, reranked[0].Score);
        Assert.Equal(0.032f, reranked[1].Score);
    }

    [Fact]
    public void DiversityReranker_MismatchedVectorDimensions_DoesNotThrow()
    {
        var vectors = new Dictionary<string, float[]>
        {
            ["a"] = [1f, 0f, 0f],
            ["b"] = [0.9f, 0.1f] // different dimensionality
        };
        var input = new[]
        {
            new CognitiveSearchResult("a", "a", 0.9f, "ltm", 0f, null, null, false, null, 1),
            new CognitiveSearchResult("b", "b", 0.8f, "ltm", 0f, null, null, false, null, 1)
        };

        var reranked = DiversityReranker.Rerank(input, [1f, 0f, 0f], id => vectors.GetValueOrDefault(id), k: 2);

        Assert.Equal(2, reranked.Count);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2. VectorQuantizer — De-offsetted ApproximateCosine
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void VectorQuantizer_ApproximateCosine_AccurateOnPositivelySkewedVectors()
    {
        // Previously, asymmetric quantization mapped a=[10,10,10,11] to [-128,-128,-128,127]
        // and b=[10,10,10,9] to [127,127,127,-128], causing raw Int8 dot product to return -1.0
        // when true cosine similarity is +0.995.
        float[] a = [10f, 10f, 10f, 11f];
        float[] b = [10f, 10f, 10f, 9f];
        float exact = ExactCosine(a, b);
        float approx = VectorQuantizer.ApproximateCosine(VectorQuantizer.Quantize(a), VectorQuantizer.Quantize(b));

        Assert.True(exact > 0.99f);
        Assert.True(MathF.Abs(approx - exact) < 0.005f,
            $"Skewed 4D cosine mismatch: exact={exact:F5}, approx={approx:F5}");

        // Also verify 384D positively shifted embeddings (exercises SIMD path).
        var rng = new Random(77);
        var highA = new float[384];
        var highB = new float[384];
        for (int i = 0; i < 384; i++)
        {
            highA[i] = 5f + (float)rng.NextDouble();
            highB[i] = 5f + (float)rng.NextDouble();
        }
        float exact384 = ExactCosine(highA, highB);
        float approx384 = VectorQuantizer.ApproximateCosine(VectorQuantizer.Quantize(highA), VectorQuantizer.Quantize(highB));
        Assert.True(MathF.Abs(approx384 - exact384) < 0.005f,
            $"Skewed 384D cosine mismatch: exact={exact384:F5}, approx={approx384:F5}");
    }

    [Fact]
    public void VectorQuantizer_ApproximateCosine_ConstantAndZeroAndEmptyVectors()
    {
        var pos = VectorQuantizer.Quantize([0.5f, 0.5f, 0.5f, 0.5f]);
        var neg = VectorQuantizer.Quantize([-0.5f, -0.5f, -0.5f, -0.5f]);
        var zero = VectorQuantizer.Quantize(new float[384]);
        var unit = VectorQuantizer.Quantize(Enumerable.Range(0, 384).Select(i => i == 0 ? 1f : 0f).ToArray());
        var empty = VectorQuantizer.Quantize(Array.Empty<float>());

        Assert.Equal(1f, VectorQuantizer.ApproximateCosine(pos, pos), precision: 4);
        Assert.Equal(-1f, VectorQuantizer.ApproximateCosine(pos, neg), precision: 4);
        Assert.Equal(0f, VectorQuantizer.ApproximateCosine(zero, unit));
        Assert.Equal(0f, VectorQuantizer.ApproximateCosine(zero, zero));
        Assert.Equal(0f, VectorQuantizer.ApproximateCosine(empty, empty));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3. PorterStemmer — Step 1c enabling Step 2/3 rules
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void PorterStemmer_Step1c_EnablesStep2AndStep3SuffixRules()
    {
        Assert.Equal("reliable", PorterStemmer.Stem("reliability"));
        Assert.Equal(PorterStemmer.Stem("capability"), PorterStemmer.Stem("capabilities"));
        Assert.Equal("capable", PorterStemmer.Stem("capability"));
        Assert.Equal(PorterStemmer.Stem("dependency"), PorterStemmer.Stem("dependencies"));
        Assert.Equal("dependence", PorterStemmer.Stem("dependency"));
        Assert.Equal(PorterStemmer.Stem("latency"), PorterStemmer.Stem("latencies"));
        Assert.Equal("sensitive", PorterStemmer.Stem("sensitivity"));
        Assert.Equal("formal", PorterStemmer.Stem("formality"));
        Assert.Equal("probable", PorterStemmer.Stem("probably"));
        Assert.Equal(PorterStemmer.Stem("security"), PorterStemmer.Stem("securities"));
        Assert.Equal(PorterStemmer.Stem("happy"), PorterStemmer.Stem("happiness"));

        // Stem without vowel before 'y' must remain unchanged.
        Assert.Equal("sky", PorterStemmer.Stem("sky"));

        // Existing IR-critical custom rule preserved.
        Assert.Equal("encrypt", PorterStemmer.Stem("encryption"));
        Assert.Equal("encrypt", PorterStemmer.Stem("encrypting"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 4. PhysicsEngine — Slingshot T=0 semantics & ComputeGravity clamping
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void PhysicsEngine_Slingshot_TemperatureZeroSortsPurelySemantic_TemperatureOneSortsPurelyPhysics()
    {
        var engine = new PhysicsEngine();
        // 'a' has highest cosine (0.85, low mass -> gravity ~30.8);
        // 'b' has lower cosine (0.75, huge LTM mass -> gravity ~221.1, highest gravity).
        var results = new[]
        {
            new CognitiveSearchResult("a", "semantic winner", 0.85f, "stm", 0f, null, null, false, null, 1),
            new CognitiveSearchResult("b", "gravity winner", 0.75f, "ltm", 0f, null, null, false, null, 1000),
            new CognitiveSearchResult("c", "middle", 0.80f, "stm", 0f, null, null, false, null, 1),
        };

        var semanticDefault = engine.Slingshot(results);
        Assert.Equal("a", semanticDefault.Asteroid.Id);
        Assert.Equal("b", semanticDefault.Sun.Id);
        Assert.Equal(["a", "c", "b"], semanticDefault.AllResults.Select(r => r.Id).ToArray());

        // Continuous near T=0: T=0.001 should not invert T=0.
        var nearZero = engine.Slingshot(results, temperature: 0.001f);
        Assert.Equal("a", nearZero.AllResults[0].Id);

        // T=1: pure physics (gravity descending).
        var purePhysics = engine.Slingshot(results, temperature: 1f);
        Assert.Equal("b", purePhysics.AllResults[0].Id);
        for (int i = 0; i < purePhysics.AllResults.Count - 1; i++)
            Assert.True(purePhysics.AllResults[i].GravityForce >= purePhysics.AllResults[i + 1].GravityForce);
    }

    [Fact]
    public void PhysicsEngine_ComputeGravity_ClampsBoostedScoresAboveOneAndHandlesUnsortedAsteroid()
    {
        float atOne = PhysicsEngine.ComputeGravity(2.0f, 1.0f);
        float boosted = PhysicsEngine.ComputeGravity(2.0f, 1.15f);
        Assert.Equal(atOne, boosted);

        float nanGravity = PhysicsEngine.ComputeGravity(2.0f, float.NaN);
        Assert.True(float.IsFinite(nanGravity));

        // Unsorted input still identifies highest cosine as Asteroid.
        var engine = new PhysicsEngine();
        var unsorted = new[]
        {
            new CognitiveSearchResult("low", "low", 0.50f, "ltm", 0f, null, null, false, null, 10),
            new CognitiveSearchResult("high", "high", 0.92f, "stm", 0f, null, null, false, null, 1),
        };
        var slingshot = engine.Slingshot(unsorted, temperature: 0f);
        Assert.Equal("high", slingshot.Asteroid.Id);
        Assert.Equal("high", slingshot.AllResults[0].Id);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 5. MemoryDiffusionKernel & RandomizedEigensolver
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void MemoryDiffusionKernel_BipartiteGraph_TopKNotHijackedByNegativeEigenvalues()
    {
        using var persistence = new PersistenceManager(SubDir("bipartite"), debounceMs: 50);
        using var index = new CognitiveIndex(persistence);
        var graph = new KnowledgeGraph(persistence, index);
        var kernel = new MemoryDiffusionKernel(index, graph);

        // Two disconnected complete bipartite components K_{20,20} (80 linked nodes total).
        // Under unshifted M = D^-1/2 W D^-1/2, each component has eigenvalue -1 (lambda_L = 2),
        // which has magnitude 1 and hijacks power iteration. Under shifted S = (I + M)/2 >= 0,
        // lambda_S(-1) = 0, so the top-K sketch captures ONLY the smallest Laplacian modes.
        const string ns = "bipartite";
        for (int comp = 0; comp < 2; comp++)
        {
            for (int side = 0; side < 2; side++)
            {
                for (int i = 0; i < 20; i++)
                {
                    index.Upsert(new CognitiveEntry(
                        $"c{comp}_s{side}_{i:D2}", [comp + 1f, side + i + 1f], ns, $"node {comp}-{side}-{i}"));
                }
            }

            var edges = new List<GraphEdge>();
            for (int i = 0; i < 20; i++)
                for (int j = 0; j < 20; j++)
                    edges.Add(new GraphEdge($"c{comp}_s0_{i:D2}", $"c{comp}_s1_{j:D2}", "similar_to", 1f));
            graph.AddEdges(edges);
        }

        // Request topK=8 (< 80 - oversample=10, forcing the randomized subspace path).
        var basis = kernel.GetBasis(ns, tenantId: "", topK: 8);
        Assert.NotNull(basis);
        Assert.Equal(80, basis!.NodeCount);
        Assert.Equal(8, basis.TopK);

        // Two disconnected components -> two zero eigenvalues, and NO lambda_L ~= 2 hijacker in top-8.
        Assert.True(basis.Eigenvalues[0] < 0.05f, $"Expected lambda_0 ~ 0, got {basis.Eigenvalues[0]}");
        Assert.True(basis.Eigenvalues[1] < 0.05f, $"Expected lambda_1 ~ 0, got {basis.Eigenvalues[1]}");
        Assert.All(basis.Eigenvalues, lambda =>
        {
            Assert.InRange(lambda, 0f, 2f);
            Assert.True(lambda < 1.5f, $"High-frequency bipartite mode lambda_L={lambda} hijacked top-K.");
        });

        // Ascending order check.
        for (int i = 0; i < basis.Eigenvalues.Count - 1; i++)
            Assert.True(basis.Eigenvalues[i] <= basis.Eigenvalues[i + 1] + 1e-5f);
    }

    [Fact]
    public async Task MemoryDiffusionKernel_DoubleCheckedLock_DoesNotRecomputeWhenTopKClampedToNodeCount()
    {
        using var persistence = new PersistenceManager(SubDir("dcl"), debounceMs: 50);
        using var index = new CognitiveIndex(persistence);
        var graph = new KnowledgeGraph(persistence, index);
        var kernel = new CountingDiffusionKernel(index, graph);

        const string ns = "clamped";
        const int nodes = 36; // < DefaultTopK (96), so basis.TopK == 36 == basis.NodeCount
        for (int i = 0; i < nodes; i++)
            index.Upsert(new CognitiveEntry($"n{i:D2}", [i + 1f, 1f], ns, $"node {i}"));
        for (int i = 0; i < nodes; i++)
            graph.AddEdge(new GraphEdge($"n{i:D2}", $"n{(i + 1) % nodes:D2}", "similar_to", 1f));

        // Synchronize 6 threads to contend on the same per-namespace lock requesting topK=96.
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return kernel.GetBasis(ns, tenantId: "", topK: 96);
        })).ToArray();

        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, Assert.NotNull);
        Assert.Equal(1, kernel.ComputeCount);
        Assert.All(results, b => Assert.Same(results[0], b));
    }

    [Fact]
    public void MemoryDiffusionKernel_DeterministicSeed_IsStableAcrossCalls()
    {
        int seed1 = MemoryDiffusionKernel.ComputeDeterministicSeed(42L, "alpha");
        int seed2 = MemoryDiffusionKernel.ComputeDeterministicSeed(42L, "alpha");
        int seedDiffNs = MemoryDiffusionKernel.ComputeDeterministicSeed(42L, "beta");
        int seedDiffRev = MemoryDiffusionKernel.ComputeDeterministicSeed(43L, "alpha");

        Assert.Equal(seed1, seed2);
        Assert.NotEqual(seed1, seedDiffNs);
        Assert.NotEqual(seed1, seedDiffRev);
    }

    [Fact]
    public void MemoryDiffusionKernel_NamespaceRemoved_RetractsImmediately()
    {
        using var persistence = new PersistenceManager(SubDir("ns_removed"), debounceMs: 50);
        using var index = new CognitiveIndex(persistence);
        var graph = new KnowledgeGraph(persistence, index);
        var kernel = new MemoryDiffusionKernel(index, graph);

        const string tenant = "t-ephemeral";
        const string ns = "debate-session";
        for (int i = 0; i < 36; i++)
            index.Upsert(new CognitiveEntry($"d{i:D2}", [MathF.Cos(i), MathF.Sin(i)], ns, $"item {i}", tenantId: tenant));
        for (int i = 0; i < 36; i++)
            graph.AddEdge(new GraphEdge($"d{i:D2}", $"d{(i + 1) % 36:D2}", "similar_to", 1f, null, tenant));

        Assert.NotNull(kernel.GetBasis(ns, tenant));
        Assert.Equal(1, kernel.CachedPartitionCount);

        Assert.True(index.DeleteAllInNamespace(ns, tenant) > 0);

        // Retracted immediately on NamespaceRemoved without waiting for round-robin polling.
        Assert.Equal(0, kernel.CachedPartitionCount);
        Assert.Equal(0, kernel.ComputeLockPartitionCount);
        Assert.Equal(0, kernel.RetractablePartitionCount);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 6. SpreadingActivationService — Cycle guard
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void SpreadingActivation_BidirectionalEdge_DoesNotBounceBackOrMultiplyEnergy()
    {
        using var persistence = new PersistenceManager(SubDir("spread_bidir"), debounceMs: 50);
        using var index = new CognitiveIndex(persistence);
        var graph = new KnowledgeGraph(persistence, index);
        var clusters = new ClusterManager(index, persistence);
        var spreading = new SpreadingActivationService(index, graph, clusters);

        index.Upsert(new CognitiveEntry("a", [1f, 0f], "ns", "node a"));
        index.Upsert(new CognitiveEntry("b", [0f, 1f], "ns", "node b"));
        graph.AddEdge(new GraphEdge("a", "b", "depends_on", 1f));

        // Exact 1-hop energy: baseEnergy (1.0) * depends_on (0.9) / sqrt(1) = 0.9.
        // Without cycle guard, A -> B -> A -> B accumulated 0.9 + 0.18225 = 1.08225 on B and phantom energy on A.
        var result = spreading.PropagateAccess("a", "ns", tenantId: "", baseEnergy: 1.0f);

        Assert.Equal(1, result.NodesReached);
        Assert.Equal(1, result.NodesUpdated);
        Assert.Equal(0.9f, result.TotalEnergySpread, precision: 4);
        Assert.Equal(0f, index.Get("a", "ns", "")!.ActivationEnergy);
        Assert.Equal(0.9f, index.Get("b", "ns", "")!.ActivationEnergy, precision: 4);
    }

    [Fact]
    public void SpreadingActivation_CycleGraph_ExpandsEachNodeAtMostOnce()
    {
        using var persistence = new PersistenceManager(SubDir("spread_cycle"), debounceMs: 50);
        using var index = new CognitiveIndex(persistence);
        var graph = new KnowledgeGraph(persistence, index);
        var clusters = new ClusterManager(index, persistence);
        var spreading = new SpreadingActivationService(index, graph, clusters);

        string[] ids = ["n0", "n1", "n2", "n3"];
        foreach (var id in ids)
            index.Upsert(new CognitiveEntry(id, [1f, 0f], "ns", id));
        for (int i = 0; i < ids.Length; i++)
            graph.AddEdge(new GraphEdge(ids[i], ids[(i + 1) % ids.Length], "depends_on", 1f));

        int edgeReads = 0;
        graph.OnAttributableEdgeRead = () => Interlocked.Increment(ref edgeReads);

        var result = spreading.PropagateAccess("n0", "ns", tenantId: "", baseEnergy: 1.0f);

        Assert.Equal(3, result.NodesReached);
        Assert.Equal(3, result.NodesUpdated);
        Assert.Equal(0f, index.Get("n0", "ns", "")!.ActivationEnergy);
        // Each of the 4 degree-2 nodes is expanded at most once -> exactly 8 edge reads (no cycle re-expansion).
        Assert.Equal(8, edgeReads);
        // 1-hop neighbors n1 and n3 receive exact 1-hop boost (1.0 * 0.9 / sqrt(2)) with no depth-2 bounce-back.
        float expectedOneHop = 0.9f / MathF.Sqrt(2f);
        Assert.Equal(expectedOneHop, index.Get("n1", "ns", "")!.ActivationEnergy, precision: 4);
        Assert.Equal(expectedOneHop, index.Get("n3", "ns", "")!.ActivationEnergy, precision: 4);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 7. ExpertDispatcher — Cycle/StackOverflow, Route leaf filter, RouteHierarchical
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ExpertDispatcher_LinkToParent_RejectsSelfCyclesRootsAndLeafParents_AndPreservesBranchLevel()
    {
        using var persistence = new PersistenceManager(SubDir("expert_cycles"), debounceMs: 50);
        using var index = new CognitiveIndex(persistence);
        var embedding = new HashEmbeddingService(dimensions: 64);
        var dispatcher = new ExpertDispatcher(index, embedding);

        dispatcher.CreateDomainNode("root1", "Engineering root", "root");
        dispatcher.CreateDomainNode("branch1", "Backend branch", "branch", "root1");
        dispatcher.CreateDomainNode("branch2", "Database sub-branch", "branch", "branch1");
        dispatcher.CreateExpert("leaf1", "PostgreSQL specialist");
        dispatcher.CreateExpert("leaf2", "Redis specialist");

        // Self-parenting rejected.
        Assert.False(dispatcher.LinkToParent("branch1", "branch1"));
        Assert.False(dispatcher.LinkToParent("leaf1", "leaf1"));

        // Ancestor/descendant cycle rejected.
        Assert.False(dispatcher.LinkToParent("branch1", "branch2"));

        // Re-parenting a root rejected.
        Assert.False(dispatcher.LinkToParent("root1", "branch1"));

        // Linking under a leaf rejected.
        Assert.False(dispatcher.LinkToParent("leaf2", "leaf1"));

        // Snapshot live metadata dictionary reference before valid LinkToParent to verify no in-place mutation.
        var leafBefore = index.Get("leaf1", ExpertDispatcher.SystemNamespace, "")!;
        var originalLeafMeta = leafBefore.Metadata;
        var rootBefore = index.Get("root1", ExpertDispatcher.SystemNamespace, "")!;
        var originalRootMeta = rootBefore.Metadata;

        // Valid re-parenting of branch2 under root1 preserves level="branch".
        Assert.True(dispatcher.LinkToParent("branch2", "root1"));
        var branch2After = index.Get("branch2", ExpertDispatcher.SystemNamespace, "")!;
        Assert.Equal("branch", branch2After.Metadata["level"]);
        Assert.Equal("root1", branch2After.Metadata["parentNodeId"]);

        // Valid leaf link clones metadata rather than mutating prior dictionary references.
        Assert.True(dispatcher.LinkToParent("leaf1", "root1"));
        Assert.False(originalLeafMeta.ContainsKey("parentNodeId"));
        Assert.DoesNotContain("leaf1", originalRootMeta["childNodeIds"]);
        Assert.Equal("root1", index.Get("leaf1", ExpertDispatcher.SystemNamespace, "")!.Metadata["parentNodeId"]);

        // Even if corrupted cyclic metadata exists in storage, GetDomainTree terminates without StackOverflowException.
        var corruptA = index.Get("branch1", ExpertDispatcher.SystemNamespace, "")!;
        var corruptMetaA = new Dictionary<string, string>(corruptA.Metadata) { ["childNodeIds"] = "branch2" };
        var corruptB = index.Get("branch2", ExpertDispatcher.SystemNamespace, "")!;
        var corruptMetaB = new Dictionary<string, string>(corruptB.Metadata) { ["childNodeIds"] = "branch1" };
        index.Upsert(new CognitiveEntry(corruptA.Id, corruptA.Vector, corruptA.Ns, corruptA.Text, corruptA.Category, corruptMetaA, "ltm"));
        index.Upsert(new CognitiveEntry(corruptB.Id, corruptB.Vector, corruptB.Ns, corruptB.Text, corruptB.Category, corruptMetaB, "ltm"));

        var tree = dispatcher.GetDomainTree();
        Assert.True(tree.MaxDepth >= 1);
    }

    [Fact]
    public void ExpertDispatcher_Route_ExcludesRootAndBranchDomainNodes()
    {
        using var persistence = new PersistenceManager(SubDir("expert_route_leaf"), debounceMs: 50);
        using var index = new CognitiveIndex(persistence);
        var embedding = new HashEmbeddingService(dimensions: 64);
        var dispatcher = new ExpertDispatcher(index, embedding);

        const string text = "Distributed systems consensus protocols";
        dispatcher.CreateDomainNode("systems_root", text, "root");
        dispatcher.CreateDomainNode("consensus_branch", text, "branch", "systems_root");

        // With ONLY root and branch nodes, flat Route must NOT return empty domain_* namespaces.
        var (missStatus, missExperts) = dispatcher.Route(embedding.Embed(text), topK: 3, threshold: 0.3f);
        Assert.Equal("needs_expert", missStatus);
        Assert.Empty(missExperts);

        // Add a leaf expert -> Route returns ONLY the leaf expert.
        dispatcher.CreateExpert("raft_specialist", text);
        var (hitStatus, hitExperts) = dispatcher.Route(embedding.Embed(text), topK: 3, threshold: 0.3f);
        Assert.Equal("routed", hitStatus);
        Assert.Single(hitExperts);
        Assert.Equal("raft_specialist", hitExperts[0].ExpertId);
        Assert.Equal("expert_raft_specialist", hitExperts[0].TargetNamespace);
    }

    [Fact]
    public void ExpertDispatcher_RouteHierarchical_NeverPromotesBranchesAsLeaves_AndSortsPathDescending()
    {
        using var persistence = new PersistenceManager(SubDir("expert_hier"), debounceMs: 50);
        using var index = new CognitiveIndex(persistence);
        var embedding = new ControlledEmbeddingService();
        var dispatcher = new ExpertDispatcher(index, embedding);

        // Case 1: Root matches query [1,0], branch does NOT match threshold (0.2), no direct leaves.
        // RouteHierarchical must return needs_expert, NOT return the unmatched branch as an expert.
        embedding.Vectors["root"] = [1f, 0f];
        embedding.Vectors["cold_branch"] = [0.2f, 0.98f];
        dispatcher.CreateDomainNode("root_a", "root", "root");
        dispatcher.CreateDomainNode("branch_cold", "cold_branch", "branch", "root_a");

        var miss = dispatcher.RouteHierarchical([1f, 0f], topK: 3, threshold: 0.5f);
        Assert.Equal("needs_expert", miss.Status);
        Assert.Empty(miss.Experts);

        // Case 2: Hot branch with 4 leaves in unsorted score order; topK=2 must select top-2 descending
        // in BOTH Experts and Path.
        embedding.Vectors["hot_branch"] = [0.99f, 0.141f];
        embedding.Vectors["leaf_50"] = [0.50f, 0.866f];
        embedding.Vectors["leaf_95"] = [0.95f, 0.312f];
        embedding.Vectors["leaf_70"] = [0.70f, 0.714f];
        embedding.Vectors["leaf_85"] = [0.85f, 0.527f];

        dispatcher.CreateDomainNode("branch_hot", "hot_branch", "branch", "root_a");
        dispatcher.CreateExpert("l50", "leaf_50");
        dispatcher.CreateExpert("l95", "leaf_95");
        dispatcher.CreateExpert("l70", "leaf_70");
        dispatcher.CreateExpert("l85", "leaf_85");
        Assert.True(dispatcher.LinkToParent("l50", "branch_hot"));
        Assert.True(dispatcher.LinkToParent("l95", "branch_hot"));
        Assert.True(dispatcher.LinkToParent("l70", "branch_hot"));
        Assert.True(dispatcher.LinkToParent("l85", "branch_hot"));

        var routed = dispatcher.RouteHierarchical([1f, 0f], topK: 2, threshold: 0.4f);
        Assert.Equal("routed", routed.Status);
        Assert.Equal(["l95", "l85"], routed.Experts.Select(e => e.ExpertId).ToArray());

        var pathLeaves = routed.Path.Where(n => n.Level == "leaf").Select(n => n.NodeId).ToArray();
        Assert.Equal(["l95", "l85"], pathLeaves);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 8. OllamaClient & SynthesisEngine
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task OllamaClient_IsAvailableAsync_MatchesExactOrLatestTagOnly()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"models":[{"name":"qwen2.5:14b","size":100},{"name":"llama3.2:latest","size":100},{"name":"phi3.5:3.8b","size":100}]}""",
                Encoding.UTF8, "application/json")
        });
        using var client = new OllamaClient(handler, "http://localhost:11434");

        // Prefix collisions must NOT match.
        Assert.False(await client.IsAvailableAsync("qwen2.5:1"));
        Assert.False(await client.IsAvailableAsync("qwen2.5:14"));
        Assert.False(await client.IsAvailableAsync("qwen2.5"));
        Assert.False(await client.IsAvailableAsync("llama3"));
        Assert.False(await client.IsAvailableAsync("phi3.5"));

        // Exact matches and untagged base name matching :latest MUST match.
        Assert.True(await client.IsAvailableAsync("qwen2.5:14b"));
        Assert.True(await client.IsAvailableAsync("QWEN2.5:14B"));
        Assert.True(await client.IsAvailableAsync("llama3.2"));
        Assert.True(await client.IsAvailableAsync("llama3.2:latest"));
        Assert.True(await client.IsAvailableAsync("phi3.5:3.8b"));
    }

    [Fact]
    public async Task SynthesisEngine_VerifiesDistinctReduceModelAvailability()
    {
        using var persistence = new PersistenceManager(SubDir("synth_reduce_avail"), debounceMs: 50);
        using var index = new CognitiveIndex(persistence);
        var clusters = new ClusterManager(index, persistence);
        index.Upsert(new CognitiveEntry("e1", [1f, 0f], "work", "memory one", lifecycleState: "ltm"));

        var generator = new FakeTextGenerator(
            isAvailable: model => model == "qwen2.5:0.5b",
            generate: (_, _, _) => Task.FromResult<string?>("summary"));
        using var engine = new SynthesisEngine(index, clusters, generator,
            mapModel: "qwen2.5:0.5b", reduceModel: "qwen2.5:7b");

        var result = await engine.SynthesizeNamespaceAsync("work", query: null, tenantId: "");

        Assert.Equal("error", result.Status);
        Assert.Contains("qwen2.5:7b", result.Error);
        Assert.Equal(0, generator.GenerateCalls);
    }

    [Fact]
    public async Task SynthesisEngine_ReduceHttpRequestException_ReturnsReduceFailedGracefully()
    {
        using var persistence = new PersistenceManager(SubDir("synth_reduce_http"), debounceMs: 50);
        using var index = new CognitiveIndex(persistence);
        var clusters = new ClusterManager(index, persistence);
        index.Upsert(new CognitiveEntry("e1", [1f, 0f], "work", "memory one", lifecycleState: "ltm"));

        var generator = new FakeTextGenerator(
            isAvailable: _ => true,
            generate: (model, prompt, _) =>
            {
                if (prompt.Contains("Synthesize these chunk summaries", StringComparison.Ordinal))
                    throw new HttpRequestException("Connection refused during reduce");
                return Task.FromResult<string?>("chunk summary");
            });
        using var engine = new SynthesisEngine(index, clusters, generator);

        var result = await engine.SynthesizeNamespaceAsync("work", query: null, tenantId: "");

        Assert.Equal("reduce_failed", result.Status);
        Assert.Null(result.Synthesis);
        Assert.NotNull(result.Error);
        Assert.Contains("HttpRequestException", result.Error);
        Assert.Equal(1, result.ChunksProcessed);
        Assert.Equal(1, result.EntriesProcessed);
        Assert.Single(result.ChunkSummaries!);
    }

    [Fact]
    public async Task SynthesisEngine_PartialMapFailures_ReportsChunksFailedWarningsAndMappedEntriesOnly()
    {
        using var persistence = new PersistenceManager(SubDir("synth_partial"), debounceMs: 50);
        using var index = new CognitiveIndex(persistence);
        var clusters = new ClusterManager(index, persistence);

        // Seed 30 entries -> 2 chunks of 15.
        for (int i = 0; i < 30; i++)
            index.Upsert(new CognitiveEntry($"m{i:D2}", [1f, i], "work", $"memory item {i}", lifecycleState: "ltm"));

        int mapCall = 0;
        var generator = new FakeTextGenerator(
            isAvailable: _ => true,
            generate: (_, prompt, _) =>
            {
                if (prompt.Contains("Synthesize these chunk summaries", StringComparison.Ordinal))
                    return Task.FromResult<string?>("final synthesis");

                if (Interlocked.Increment(ref mapCall) == 1)
                    throw new HttpRequestException("simulated chunk 1 outage");

                return Task.FromResult<string?>("chunk 2 summary");
            });
        using var engine = new SynthesisEngine(index, clusters, generator);

        var result = await engine.SynthesizeNamespaceAsync("work", query: null, tenantId: "");

        Assert.Equal("synthesized", result.Status);
        Assert.Equal("final synthesis", result.Synthesis);
        Assert.Equal(1, result.ChunksProcessed);
        Assert.Equal(1, result.ChunksFailed);
        Assert.Equal(15, result.EntriesProcessed);
        Assert.NotNull(result.Warnings);
        Assert.Single(result.Warnings!);
        Assert.Contains("simulated chunk 1 outage", result.Warnings![0]);
    }

    [Fact]
    public void SynthesisEngine_Dispose_DoesNotDisposeInjectedGenerator_AndIsIdempotentWhenOwned()
    {
        using var persistence = new PersistenceManager(SubDir("synth_dispose"), debounceMs: 50);
        using var index = new CognitiveIndex(persistence);
        var clusters = new ClusterManager(index, persistence);

        var injected = new FakeTextGenerator(_ => true, (_, _, _) => Task.FromResult<string?>("ok"));
        var engineWithInjected = new SynthesisEngine(index, clusters, injected);
        engineWithInjected.Dispose();
        Assert.False(injected.Disposed);

        var engineWithOwned = new SynthesisEngine(index, clusters, ollamaUrl: "http://127.0.0.1:11434");
        engineWithOwned.Dispose();
        engineWithOwned.Dispose(); // idempotent
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static float ExactCosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }

    private sealed class CountingDiffusionKernel : MemoryDiffusionKernel
    {
        private int _computeCount;
        public int ComputeCount => Volatile.Read(ref _computeCount);

        public CountingDiffusionKernel(CognitiveIndex index, KnowledgeGraph graph)
            : base(index, graph) { }

        protected override DiffusionBasis? ComputeBasis(string ns, int topK, long graphRevision, string tenantId)
        {
            Interlocked.Increment(ref _computeCount);
            Thread.Sleep(50);
            return base.ComputeBasis(ns, topK, graphRevision, tenantId);
        }
    }

    private sealed class ControlledEmbeddingService : IEmbeddingService
    {
        public Dictionary<string, float[]> Vectors { get; } = new(StringComparer.Ordinal);
        public int Dimensions => 2;
        public float[] Embed(string text) => Vectors.TryGetValue(text, out var v) ? v : [1f, 0f];
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class FakeTextGenerator(
        Func<string, bool> isAvailable,
        Func<string, string, CancellationToken, Task<string?>> generate) : ITextGenerator, IDisposable
    {
        private int _generateCalls;
        public int GenerateCalls => Volatile.Read(ref _generateCalls);
        public bool Disposed { get; private set; }

        public Task<bool> IsAvailableAsync(string model, CancellationToken ct = default)
            => Task.FromResult(isAvailable(model));

        public Task<string?> GenerateAsync(string model, string prompt, int maxTokens = 512,
            float temperature = 0.1f, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _generateCalls);
            return generate(model, prompt, ct);
        }

        public void Dispose() => Disposed = true;
    }
}
