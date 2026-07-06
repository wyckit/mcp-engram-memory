using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Storage;

namespace McpEngramMemory.Tests;

/// <summary>
/// Tenant-isolation tests for the index/store/search layer (T2-05). Verifies that the same
/// (ns, id) under two tenants stays disjoint across every operation, that an id-probe from the
/// wrong tenant reveals nothing, that a tenant-scoped delete requires an exact (tenant, ns) match,
/// and that the global tenant-less APIs resolve strictly within the legacy tenant. Backward-compat
/// (no-tenant callers behave exactly as before) is covered by the unmodified existing suite.
///
/// Datasets are intentionally tiny (well under the HNSW threshold) so the retrieval path is the
/// deterministic exact/vector path with no snapshot I/O.
/// </summary>
public class CognitiveIndexTenantTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;

    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    public CognitiveIndexTenantTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"cognitive_tenant_test_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_testDataPath, debounceMs: 50);
        _index = new CognitiveIndex(_persistence);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_testDataPath))
            Directory.Delete(_testDataPath, true);
    }

    private static CognitiveEntry Entry(string id, float[] vector, string ns, string tenantId,
        string? text = null)
        => new(id, vector, ns, text: text, tenantId: tenantId);

    // ── Same (ns, id) under two tenants is disjoint ──

    [Fact]
    public void Get_SameNsAndId_TwoTenants_AreDisjoint()
    {
        _index.Upsert(Entry("shared", new[] { 1f, 0f }, "work", TenantA, text: "alpha"));
        _index.Upsert(Entry("shared", new[] { 0f, 1f }, "work", TenantB, text: "bravo"));

        var a = _index.Get("shared", "work", TenantA);
        var b = _index.Get("shared", "work", TenantB);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal("alpha", a!.Text);
        Assert.Equal("bravo", b!.Text);
        Assert.Equal(TenantA, a.TenantId);
        Assert.Equal(TenantB, b.TenantId);
        // Distinct object identity — one tenant's row never aliases the other's.
        Assert.NotSame(a, b);
    }

    [Fact]
    public void Upsert_OneTenant_DoesNotAffectAnotherTenantsEntry()
    {
        _index.Upsert(Entry("shared", new[] { 1f, 0f }, "work", TenantA, text: "original-a"));
        _index.Upsert(Entry("shared", new[] { 0f, 1f }, "work", TenantB, text: "original-b"));

        // Overwrite only tenant A's entry.
        _index.Upsert(Entry("shared", new[] { 1f, 0f }, "work", TenantA, text: "updated-a"));

        Assert.Equal("updated-a", _index.Get("shared", "work", TenantA)!.Text);
        Assert.Equal("original-b", _index.Get("shared", "work", TenantB)!.Text);
    }

    [Fact]
    public void Search_IsScopedToTenant()
    {
        _index.Upsert(Entry("shared", new[] { 1f, 0f }, "work", TenantA, text: "alpha"));
        _index.Upsert(Entry("shared", new[] { 1f, 0f }, "work", TenantB, text: "bravo"));

        var aResults = _index.Search(new SearchRequest
        {
            Query = new[] { 1f, 0f }, Namespace = "work", K = 10, TenantId = TenantA
        });
        var bResults = _index.Search(new SearchRequest
        {
            Query = new[] { 1f, 0f }, Namespace = "work", K = 10, TenantId = TenantB
        });

        Assert.Single(aResults);
        Assert.Single(bResults);
        Assert.Equal("alpha", aResults[0].Text);
        Assert.Equal("bravo", bResults[0].Text);
    }

    [Fact]
    public void HybridSearch_IsScopedToTenant()
    {
        _index.Upsert(Entry("a1", new[] { 1f, 0f }, "work", TenantA, text: "quarterly revenue report"));
        _index.Upsert(Entry("b1", new[] { 1f, 0f }, "work", TenantB, text: "quarterly revenue report"));

        var aResults = _index.HybridSearch(
            new[] { 1f, 0f }, "quarterly revenue", "work", k: 10, tenantId: TenantA);
        var bResults = _index.HybridSearch(
            new[] { 1f, 0f }, "quarterly revenue", "work", k: 10, tenantId: TenantB);

        Assert.Single(aResults);
        Assert.Single(bResults);
        Assert.Equal("a1", aResults[0].Id);
        Assert.Equal("b1", bResults[0].Id);
    }

    [Fact]
    public void SearchMultiple_IsScopedToTenant()
    {
        _index.Upsert(Entry("a1", new[] { 1f, 0f }, "work", TenantA, text: "alpha"));
        _index.Upsert(Entry("a2", new[] { 1f, 0f }, "personal", TenantA, text: "alpha2"));
        _index.Upsert(Entry("b1", new[] { 1f, 0f }, "work", TenantB, text: "bravo"));

        var results = _index.SearchMultiple(
            new[] { 1f, 0f }, new[] { "work", "personal" }, queryText: null, k: 10, tenantId: TenantA);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.StartsWith("a", r.Id));
    }

    // ── Wrong-tenant id-probe reveals nothing ──

    [Fact]
    public void Get_WrongTenant_ReturnsNull()
    {
        _index.Upsert(Entry("only-a", new[] { 1f, 0f }, "work", TenantA, text: "alpha"));

        Assert.Null(_index.Get("only-a", "work", TenantB));   // wrong tenant
        Assert.Null(_index.Get("only-a", "work"));            // legacy tenant
        Assert.Null(_index.Get("only-a"));                    // global (legacy-only) resolver
    }

    [Fact]
    public void Search_WrongTenant_ReturnsEmpty()
    {
        _index.Upsert(Entry("only-a", new[] { 1f, 0f }, "work", TenantA, text: "alpha"));

        var results = _index.Search(new SearchRequest
        {
            Query = new[] { 1f, 0f }, Namespace = "work", K = 10, TenantId = TenantB
        });

        Assert.Empty(results);
    }

    // ── Delete(id, ns[, tenant]) requires an exact partition match ──

    [Fact]
    public void Delete_NamespaceMismatch_ReturnsFalse()
    {
        _index.Upsert(Entry("x", new[] { 1f, 0f }, "work", TenantA, text: "alpha"));

        Assert.False(_index.Delete("x", "other", TenantA)); // right tenant, wrong ns
        Assert.NotNull(_index.Get("x", "work", TenantA));   // still present
    }

    [Fact]
    public void Delete_TenantMismatch_ReturnsFalse()
    {
        _index.Upsert(Entry("x", new[] { 1f, 0f }, "work", TenantA, text: "alpha"));

        Assert.False(_index.Delete("x", "work", TenantB)); // right ns, wrong tenant
        Assert.NotNull(_index.Get("x", "work", TenantA));  // still present
    }

    [Fact]
    public void Delete_ExactMatch_ReturnsTrueAndRemovesOnlyThatTenant()
    {
        _index.Upsert(Entry("shared", new[] { 1f, 0f }, "work", TenantA, text: "alpha"));
        _index.Upsert(Entry("shared", new[] { 0f, 1f }, "work", TenantB, text: "bravo"));

        Assert.True(_index.Delete("shared", "work", TenantA));

        Assert.Null(_index.Get("shared", "work", TenantA));      // gone
        Assert.NotNull(_index.Get("shared", "work", TenantB));   // untouched
        Assert.Equal("bravo", _index.Get("shared", "work", TenantB)!.Text);
    }

    [Fact]
    public void GlobalDelete_DoesNotRemoveTenantEntries()
    {
        // Same id in the legacy tenant and in tenant A.
        _index.Upsert(Entry("shared", new[] { 1f, 0f }, "work", "", text: "legacy"));
        _index.Upsert(Entry("shared", new[] { 0f, 1f }, "work", TenantA, text: "alpha"));

        // Global (tenant-less) delete only reaches the legacy entry.
        Assert.True(_index.Delete("shared"));

        Assert.Null(_index.Get("shared"));                     // legacy gone
        Assert.Null(_index.Get("shared", "work"));             // legacy gone
        Assert.NotNull(_index.Get("shared", "work", TenantA)); // tenant A untouched
    }

    // ── Global tenant-less APIs resolve strictly within the legacy tenant ──

    [Fact]
    public void GlobalGet_ResolvesLegacyTenantOnly()
    {
        _index.Upsert(Entry("shared", new[] { 1f, 0f }, "work", "", text: "legacy"));
        _index.Upsert(Entry("shared", new[] { 0f, 1f }, "work", TenantA, text: "alpha"));

        var global = _index.Get("shared");
        Assert.NotNull(global);
        Assert.Equal("legacy", global!.Text);
        Assert.Equal(string.Empty, global.TenantId);
    }

    [Fact]
    public void GlobalGet_TenantOnlyId_ReturnsNull()
    {
        // Id exists only in a non-legacy tenant → the global resolver must not find it,
        // even after a full load. This is what makes cross-tenant id-probing impossible.
        _index.Upsert(Entry("only-a", new[] { 1f, 0f }, "work", TenantA, text: "alpha"));

        Assert.Null(_index.Get("only-a"));
        Assert.False(_index.Delete("only-a"));
        Assert.NotNull(_index.Get("only-a", "work", TenantA)); // reachable only via explicit tenant
    }

    [Fact]
    public void LegacySearch_DoesNotLeakTenantEntries()
    {
        _index.Upsert(Entry("legacy", new[] { 1f, 0f }, "work", "", text: "legacy"));
        _index.Upsert(Entry("tenant", new[] { 1f, 0f }, "work", TenantA, text: "alpha"));

        // No-tenant search sees only the legacy entry.
        var legacy = _index.Search(new SearchRequest
        {
            Query = new[] { 1f, 0f }, Namespace = "work", K = 10
        });

        Assert.Single(legacy);
        Assert.Equal("legacy", legacy[0].Text);
    }

    // ── Isolation survives persistence round-trip (LoadNamespace tenant bucketing) ──

    [Fact]
    public void TenantIsolation_SurvivesReload()
    {
        _index.Upsert(Entry("shared", new[] { 1f, 0f }, "work", TenantA, text: "alpha"));
        _index.Upsert(Entry("shared", new[] { 0f, 1f }, "work", TenantB, text: "bravo"));
        _index.Upsert(Entry("shared", new[] { 1f, 1f }, "work", "", text: "legacy"));
        _persistence.Flush();

        // Re-open a fresh index over the same persisted data — forces a LoadNamespace that must
        // re-bucket the three co-keyed rows into their (tenant, ns) partitions.
        using var persistence2 = new PersistenceManager(_testDataPath, debounceMs: 50);
        using var index2 = new CognitiveIndex(persistence2);

        Assert.Equal("alpha", index2.Get("shared", "work", TenantA)!.Text);
        Assert.Equal("bravo", index2.Get("shared", "work", TenantB)!.Text);
        Assert.Equal("legacy", index2.Get("shared", "work")!.Text);
        Assert.Equal("legacy", index2.Get("shared")!.Text); // global resolver → legacy only
    }

    // ── Batch upsert honors per-entry tenant ──

    [Fact]
    public void UpsertBatch_PartitionsByTenant()
    {
        _index.UpsertBatch(new[]
        {
            Entry("shared", new[] { 1f, 0f }, "work", TenantA, text: "alpha"),
            Entry("shared", new[] { 0f, 1f }, "work", TenantB, text: "bravo"),
        });

        Assert.Equal("alpha", _index.Get("shared", "work", TenantA)!.Text);
        Assert.Equal("bravo", _index.Get("shared", "work", TenantB)!.Text);
    }
}
