using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// Structural-deflation witnesses for <see cref="MemoryDiffusionKernel"/>: isolated
/// (edge-less) entries must be excluded from the eigenproblem and handled as identity
/// by <see cref="MemoryDiffusionKernel.ApplySpectralFilter"/>'s out-of-basis
/// pass-through. Mathematically each isolated node is its own graph component with
/// Laplacian eigenvalue 0 and heat-kernel filter value exp(0) = 1 — identity.
/// </summary>
public class MemoryDiffusionKernelDeflationTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly KnowledgeGraph _graph;
    private readonly MemoryDiffusionKernel _kernel;

    public MemoryDiffusionKernelDeflationTests()
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
    /// Test 1 — isolated entries must pass through the spectral filter unchanged
    /// (identity), while linked entries are actually filtered. Against the pre-deflation
    /// code this FAILS: isolated entries are included in the basis with Laplacian
    /// eigenvalue 1 (lambda_M = 0), so their signal is wrongly attenuated — the latent
    /// debt-zeroing bug where isolated entries silently receive no (or wrong) spectral
    /// decay instead of identity pass-through.
    /// </summary>
    [Fact]
    public void IsolatedEntriesPassThroughSpectralFilterUnchanged()
    {
        const string ns = "deflate_identity";
        const int linked = 40;
        const int isolated = 20;
        SeedRing(ns, linked);
        for (int i = 0; i < isolated; i++)
            _index.Upsert(new CognitiveEntry($"iso_{i:D2}", new[] { 100f + i, 0f }, ns, $"isolated {i}"));

        var signal = new Dictionary<string, float>();
        for (int i = 0; i < linked; i++) signal[$"link_{i:D3}"] = 0.5f + 0.01f * i;
        for (int i = 0; i < isolated; i++) signal[$"iso_{i:D2}"] = 1.0f + 0.1f * i;

        var filtered = _kernel.ApplySpectralFilter(ns, signal, lambda => MathF.Exp(-lambda), tenantId: "");

        // Isolated entries: exact identity pass-through (lambda_L = 0 singleton
        // component, exp(-0) = 1).
        for (int i = 0; i < isolated; i++)
        {
            string id = $"iso_{i:D2}";
            Assert.True(signal[id] == filtered[id],
                $"Isolated entry {id}: expected exact identity pass-through {signal[id]}, got {filtered[id]}.");
        }

        // Linked entries: the filter must actually do something to a non-uniform signal.
        float diffSq = 0f;
        for (int i = 0; i < linked; i++)
        {
            string id = $"link_{i:D3}";
            float d = signal[id] - filtered[id];
            diffSq += d * d;
        }
        Assert.True(diffSq > 1e-4f,
            $"Linked entries should be measurably filtered; got squared diff {diffSq:E2}.");
    }

    /// <summary>
    /// Test 2 — a MagnetFun-shaped namespace: 108 entries of which only 22 are linked
    /// (with &gt;= 8 edges among them). After deflation the linked core (22) is below
    /// MinimumNodesForSpectral (32), so GetBasis must return null (spectral bypass)
    /// instead of throwing. Against the pre-deflation code this namespace shape either
    /// throws InvalidOperationException from the eigensolver's orthonormality assert or
    /// returns a corrupt basis, depending on n.
    /// </summary>
    [Fact]
    public void TinyLinkedCoreBypassesSpectral()
    {
        const string ns = "deflate_tiny_core";
        const int linked = 22;
        const int isolated = 86; // 108 total
        SeedRing(ns, linked);
        for (int i = 0; i < isolated; i++)
            _index.Upsert(new CognitiveEntry($"iso_{i:D2}", new[] { 100f + i, 0f }, ns, $"isolated {i}"));

        var basis = _kernel.GetBasis(ns, tenantId: ""); // must not throw
        Assert.Null(basis);
    }

    /// <summary>
    /// Test 3 — 200 entries, 120 linked: the basis must be built over the linked
    /// subgraph only. EntryIds contains exactly the 120 linked ids (all with degree
    /// &gt; 0), and the eigenvector columns are orthonormal.
    /// </summary>
    [Fact]
    public void DeflatedBasisContainsOnlyLinkedEntries()
    {
        const string ns = "deflate_partial";
        const int linked = 120;
        const int isolated = 80; // 200 total
        SeedRing(ns, linked, withChords: true);
        for (int i = 0; i < isolated; i++)
            _index.Upsert(new CognitiveEntry($"iso_{i:D2}", new[] { 100f + i, 0f }, ns, $"isolated {i}"));

        var basis = _kernel.GetBasis(ns, tenantId: "");
        Assert.NotNull(basis);
        Assert.Equal(linked, basis!.EntryIds.Count);
        foreach (var id in basis.EntryIds)
            Assert.StartsWith("link_", id); // every basis id has degree > 0 by construction

        int n = basis.NodeCount;
        int k = basis.TopK;
        for (int a = 0; a < k; a++)
        {
            float diag = 0f;
            for (int i = 0; i < n; i++) diag += basis.Eigenvectors[i, a] * basis.Eigenvectors[i, a];
            Assert.True(MathF.Abs(diag - 1f) <= 1e-3f,
                $"Basis column {a} has norm^2 {diag}, expected 1.");
            for (int b = a + 1; b < k; b++)
            {
                float off = 0f;
                for (int i = 0; i < n; i++) off += basis.Eigenvectors[i, a] * basis.Eigenvectors[i, b];
                Assert.True(MathF.Abs(off) <= 1e-3f,
                    $"<basis col {a}, col {b}> = {off}, expected 0.");
            }
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Seed <paramref name="count"/> entries named link_### connected in a similar_to
    /// ring (count edges, well above MinimumEdgesForSpectral). With
    /// <paramref name="withChords"/>, adds extra chords to de-symmetrize the spectrum.
    /// </summary>
    private void SeedRing(string ns, int count, bool withChords = false)
    {
        for (int i = 0; i < count; i++)
            _index.Upsert(new CognitiveEntry($"link_{i:D3}", new[] { (float)i, 1f }, ns, $"linked {i}"));

        for (int i = 0; i < count; i++)
            _graph.AddEdge(new GraphEdge($"link_{i:D3}", $"link_{(i + 1) % count:D3}", "similar_to", 1.0f));

        if (withChords)
        {
            for (int i = 0; i < count; i += 5)
                _graph.AddEdge(new GraphEdge($"link_{i:D3}", $"link_{(i + 13) % count:D3}", "cross_reference", 0.5f));
        }
    }
}
