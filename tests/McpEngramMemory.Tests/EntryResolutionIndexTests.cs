using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

/// <summary>
/// Pins the tenant + id candidate index that replaced the full namespace walk behind every bare-id
/// path (<see cref="CognitiveIndex.GetNamespacesContaining"/>, GetForTenant, DeleteForTenant,
/// CountNamespacesContaining and <see cref="EntryAccessResolver"/>).
///
/// The risk this file exists to cover is not slowness, it is disagreement. A scan is complete by
/// construction; an index is complete only while every path that can move an id between partitions
/// maintains it, and a path that forgets to is a silently wrong answer — an id resolving to nothing,
/// or an ambiguous id looking unique and then being acted on through an arbitrary twin. So the
/// central test is equivalence against a brute-force scan rather than any hand-written expectation,
/// and the maintenance tests walk each mutation path (upsert, delete, partition removal, reload)
/// one at a time.
/// </summary>
public class EntryResolutionIndexTests : IDisposable
{
    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 2;
        // Everything embeds identically: resolution here is by id and namespace, never by
        // similarity, so a uniform vector keeps ranking out of the result entirely.
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly StubEmbedding _embedding = new();

    public EntryResolutionIndexTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"resolution_index_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 10);
        _index = new CognitiveIndex(_persistence);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    private void Seed(string id, string ns, string tenantId) =>
        _index.Upsert(new CognitiveEntry(id, [1f, 0f], ns, $"{tenantId}/{ns}/{id}", tenantId: tenantId));

    /// <summary>
    /// The definition the index has to match: every namespace of this tenant in which a
    /// namespace-qualified Get actually finds the id. Deliberately expressed through the same
    /// public API a caller would use, so it stays an independent oracle rather than a second copy
    /// of the implementation under test.
    /// </summary>
    private IReadOnlyList<string> BruteForceScan(string id, string tenantId) =>
        _index.GetNamespaces(tenantId)
            .Where(ns => _index.Get(id, ns, tenantId: tenantId) is not null)
            .ToList();

    private static void AssertSameSet(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(
            expected.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            actual.OrderBy(s => s, StringComparer.Ordinal).ToList());

    // ── Equivalence with the scan it replaces ──

    [Fact]
    public void GetNamespacesContaining_AgreesWithABruteForceScan_ForEveryIdAndTenant()
    {
        // Legacy tenant plus two identified tenants, with ids duplicated within a tenant, across
        // tenants, and both at once — the shapes that separate "(tenant, ns, id) is the identity"
        // from the tenant-blind locator's "one namespace per id".
        Seed("alpha", "work", tenantId: "");
        Seed("dup", "work", tenantId: "");
        Seed("dup", "personal", tenantId: "");
        Seed("beta", "personal", tenantId: "");

        Seed("dup", "work", tenantId: "t1");
        Seed("dup", "t1-only", tenantId: "t1");
        Seed("gamma", "t1-only", tenantId: "t1");

        Seed("alpha", "work", tenantId: "t2");

        string[] tenants = ["", "t1", "t2"];
        string[] ids = ["alpha", "beta", "gamma", "dup", "never-stored"];

        foreach (var tenant in tenants)
        {
            foreach (var id in ids)
            {
                AssertSameSet(
                    BruteForceScan(id, tenant),
                    _index.GetNamespacesContaining(id, tenantId: tenant));
            }
        }

        // Guard the guard: were the scan oracle to find nothing anywhere, the loop above would
        // agree with an index that is permanently empty and prove nothing at all.
        AssertSameSet(["work", "personal"], BruteForceScan("dup", ""));
        AssertSameSet(["work", "t1-only"], BruteForceScan("dup", "t1"));
        Assert.Empty(BruteForceScan("dup", "t2"));
    }

    [Fact]
    public void GetNamespacesContaining_DoesNotCrossTenants()
    {
        // Being tenant-qualified is what lets this index safely cover the tenants the legacy
        // locator deliberately excludes: naming another tenant's namespace here would hand a
        // bare-id caller a partition it can never legitimately reach.
        Seed("secret", "vault", tenantId: "t1");

        Assert.Empty(_index.GetNamespacesContaining("secret", tenantId: ""));
        Assert.Empty(_index.GetNamespacesContaining("secret", tenantId: "t2"));
        Assert.Equal(new[] { "vault" }, _index.GetNamespacesContaining("secret", tenantId: "t1"));
    }

    // ── Maintenance, one mutation path at a time ──

    [Fact]
    public void Upsert_IntoASecondNamespace_AddsRatherThanMovesTheCandidate()
    {
        // The legacy locator moves on re-upsert; the candidate set must grow, because BOTH entries
        // still exist and a scan would have found both. A move here would report an ambiguous id
        // as unique and let the next write land on whichever twin the locator happened to hold.
        Seed("x", "n1", tenantId: "");
        Assert.Equal(new[] { "n1" }, _index.GetNamespacesContaining("x", tenantId: ""));

        Seed("x", "n2", tenantId: "");

        AssertSameSet(["n1", "n2"], _index.GetNamespacesContaining("x", tenantId: ""));
        AssertSameSet(BruteForceScan("x", ""), _index.GetNamespacesContaining("x", tenantId: ""));
    }

    [Fact]
    public void Upsert_OverAnExistingEntry_DoesNotDuplicateTheCandidate()
    {
        Seed("x", "n1", tenantId: "");
        Seed("x", "n1", tenantId: "");

        Assert.Equal(new[] { "n1" }, _index.GetNamespacesContaining("x", tenantId: ""));
        Assert.Equal(1, _index.CountNamespacesContaining("x", tenantId: ""));
    }

    [Fact]
    public void Delete_RetractsOnlyTheDeletedPlacement()
    {
        Seed("x", "n1", tenantId: "");
        Seed("x", "n2", tenantId: "");

        Assert.True(_index.Delete("x", "n1", tenantId: ""));

        Assert.Equal(new[] { "n2" }, _index.GetNamespacesContaining("x", tenantId: ""));
        AssertSameSet(BruteForceScan("x", ""), _index.GetNamespacesContaining("x", tenantId: ""));
        // The surviving twin is unambiguous again, so the tenant-scoped get resolves once more.
        Assert.Equal("n2", _index.GetForTenant("x", tenantId: "")!.Ns);

        Assert.True(_index.Delete("x", "n2", tenantId: ""));
        Assert.Empty(_index.GetNamespacesContaining("x", tenantId: ""));
        Assert.Null(_index.GetForTenant("x", tenantId: ""));
    }

    [Fact]
    public void Delete_InATenantPartition_RetractsTheCandidate()
    {
        // The legacy locator is legacy-only by design, so before the candidate index nothing at
        // all tracked a tenant entry's placement. This is the path that would keep offering a
        // deleted tenant entry's namespace if the retraction were left behind the legacy guard.
        Seed("x", "n1", tenantId: "t1");
        Seed("x", "n1", tenantId: "");

        Assert.True(_index.Delete("x", "n1", tenantId: "t1"));

        Assert.Empty(_index.GetNamespacesContaining("x", tenantId: "t1"));
        Assert.Equal(new[] { "n1" }, _index.GetNamespacesContaining("x", tenantId: ""));
    }

    [Fact]
    public void DeleteAllInNamespace_RetractsEveryCandidateInThatPartitionAndNoOther()
    {
        Seed("x", "gone", tenantId: "t1");
        Seed("y", "gone", tenantId: "t1");
        Seed("x", "kept", tenantId: "t1");
        Seed("x", "gone", tenantId: "");

        Assert.Equal(2, _index.DeleteAllInNamespace("gone", tenantId: "t1"));

        Assert.Equal(new[] { "kept" }, _index.GetNamespacesContaining("x", tenantId: "t1"));
        Assert.Empty(_index.GetNamespacesContaining("y", tenantId: "t1"));
        // Same namespace name, different tenant: a partition removal must not reach across.
        Assert.Equal(new[] { "gone" }, _index.GetNamespacesContaining("x", tenantId: ""));
    }

    [Fact]
    public void ReloadedIndex_ResolvesIdsInNamespacesNeverTouchedThisProcess()
    {
        // The completeness property. A scan was complete because listing the tenant's namespaces
        // loads every persisted one first; an index is only as complete as what has been
        // materialized. A fresh CognitiveIndex over the same store has loaded nothing, so if the
        // load path failed to populate the index — or if the lookup dropped its LoadAll — every id
        // below would come back as a miss.
        Seed("alpha", "work", tenantId: "");
        Seed("dup", "work", tenantId: "");
        Seed("dup", "personal", tenantId: "");
        Seed("dup", "vault", tenantId: "t1");
        _persistence.Flush();

        using var reloaded = new CognitiveIndex(_persistence);

        AssertSameSet(["work"], reloaded.GetNamespacesContaining("alpha", tenantId: ""));
        AssertSameSet(["work", "personal"], reloaded.GetNamespacesContaining("dup", tenantId: ""));
        AssertSameSet(["vault"], reloaded.GetNamespacesContaining("dup", tenantId: "t1"));
        Assert.Empty(reloaded.GetNamespacesContaining("alpha", tenantId: "t1"));

        // And the verbs derived from it agree on the cold store too.
        Assert.Equal(2, reloaded.CountNamespacesContaining("dup", tenantId: ""));
        Assert.Null(reloaded.GetForTenant("dup", tenantId: ""));
        Assert.Equal("vault", reloaded.GetForTenant("dup", tenantId: "t1")!.Ns);
    }

    // ── Invariants the three shared-scan callers must keep ──

    [Fact]
    public void CountNamespacesContaining_SaturatesAtTwo_AndNeverReportsThree()
    {
        Seed("x", "n1", tenantId: "");
        Assert.Equal(1, _index.CountNamespacesContaining("x", tenantId: ""));

        Seed("x", "n2", tenantId: "");
        Seed("x", "n3", tenantId: "");

        // Three namespaces hold it, but the contract is none / exactly-one / ambiguous, so the
        // count stops at the second hit rather than reporting a number nobody can act on.
        Assert.Equal(3, BruteForceScan("x", "").Count);
        Assert.Equal(2, _index.CountNamespacesContaining("x", tenantId: ""));
        Assert.Equal(0, _index.CountNamespacesContaining("never-stored", tenantId: ""));
    }

    [Fact]
    public void CountNamespacesContaining_WithANamespaceSnapshot_StillRestrictsTheWalk()
    {
        Seed("x", "n1", tenantId: "");
        Seed("x", "n2", tenantId: "");

        // The snapshot overload exists so a sweep does not reload the store once per id. It has to
        // agree with the unsnapshotted call for the full listing, and still honour a narrowed one.
        var full = _index.GetNamespaces("");
        Assert.Equal(2, _index.CountNamespacesContaining("x", tenantId: "", namespaceSnapshot: full));
        Assert.Equal(1, _index.CountNamespacesContaining("x", tenantId: "", namespaceSnapshot: ["n1"]));
        Assert.Equal(0, _index.CountNamespacesContaining("x", tenantId: "", namespaceSnapshot: ["n3"]));
    }

    [Fact]
    public void GetForTenant_AndDeleteForTenant_StillRefuseAnAmbiguousId()
    {
        Seed("x", "n1", tenantId: "");
        Seed("x", "n2", tenantId: "");

        Assert.Null(_index.GetForTenant("x", tenantId: ""));
        Assert.False(_index.DeleteForTenant("x", tenantId: ""));

        // The refusal is a refusal: neither twin was deleted by an arbitrary choice between them.
        Assert.NotNull(_index.Get("x", "n1", tenantId: ""));
        Assert.NotNull(_index.Get("x", "n2", tenantId: ""));
    }

    // ── The access pattern the reviewer asked to see optimized ──

    [Fact]
    public void Resolve_ProbesOnlyTheCandidateNamespaces_NotEveryNamespaceInTheTenant()
    {
        // The measurement, pinned as an access pattern rather than as wall-clock. Resolve applies
        // canAccess to each namespace it considers, so the predicate IS an honest counting seam:
        // one invocation per namespace probed, supplied by the caller, with no production hook
        // added for the test's benefit. Before the candidate index this loop ran over
        // GetNamespaces(tenant) — twelve invocations here, and a locked partition read for each.
        for (int i = 0; i < 12; i++)
            Seed($"filler-{i}", $"ns-{i:D2}", tenantId: "");
        Seed("needle", "ns-07", tenantId: "");

        Assert.True(_index.GetNamespaces("").Count >= 12);

        var probed = new List<string>();
        var entry = EntryAccessResolver.Resolve(
            _index, "needle", tenantId: "", canAccess: ns => { probed.Add(ns); return true; });

        Assert.Equal("ns-07", entry!.Ns);
        // Exactly the namespaces that hold the id. The eleven that do not are never consulted,
        // never predicate-checked and never read.
        Assert.Equal(new[] { "ns-07" }, probed);
    }

    [Fact]
    public void Resolve_ProbeCountScalesWithCandidates_NotWithNamespaceCount()
    {
        for (int i = 0; i < 12; i++)
            Seed($"filler-{i}", $"ns-{i:D2}", tenantId: "");
        Seed("twin", "ns-02", tenantId: "");
        Seed("twin", "ns-09", tenantId: "");

        var probed = new List<string>();
        var entry = EntryAccessResolver.Resolve(
            _index, "twin", tenantId: "", canAccess: ns => { probed.Add(ns); return true; });

        // Ambiguous among the visible namespaces, so it refuses — and it took two probes to say
        // so, not twelve. The work is a function of how many namespaces hold the id.
        Assert.Null(entry);
        AssertSameSet(["ns-02", "ns-09"], probed);
    }

    [Fact]
    public void Resolve_PreferredNamespace_StillShortCircuitsAheadOfTheCandidateWalk()
    {
        Seed("x", "n1", tenantId: "");
        Seed("x", "n2", tenantId: "");

        var probed = new List<string>();
        var entry = EntryAccessResolver.Resolve(
            _index, "x", tenantId: "", canAccess: ns => { probed.Add(ns); return true; },
            preferredNs: "n2");

        // Property (2) unchanged: the call site's own namespace wins outright, so an otherwise
        // ambiguous id still resolves — and only that namespace is checked.
        Assert.Equal("n2", entry!.Ns);
        Assert.Equal(new[] { "n2" }, probed);
    }

    [Fact]
    public void Resolve_PreferredNamespace_IsStillPredicateCheckedAndNeverABypass()
    {
        Seed("x", "forbidden", tenantId: "");

        var entry = EntryAccessResolver.Resolve(
            _index, "x", tenantId: "", canAccess: ns => ns != "forbidden", preferredNs: "forbidden");

        // Denied as the preferred namespace, then denied again as the only candidate. Null, and
        // indistinguishable from an id that was never stored — property (3).
        Assert.Null(entry);
        Assert.Null(EntryAccessResolver.Resolve(
            _index, "never-stored", tenantId: "", canAccess: _ => true));
    }

    // ── Filter-before-match under a genuinely identified principal ──

    [Fact]
    public void Resolve_IdentifiedPrincipal_InvisibleTwinNeitherWinsNorBlanksTheVisibleOne()
    {
        // A default-agent principal would prove nothing here: NamespaceRegistry.HasAccess
        // short-circuits AgentIdentity.Default to unrestricted, so the predicate would admit
        // everything and this test would pass with no filtering happening at all. Both agents are
        // therefore honestly identified, and each owns the namespace it writes to.
        var registry = new NamespaceRegistry(_index, _embedding);
        registry.ClaimOwnershipOnWrite("alice-private", "alice", tenantId: "");
        registry.ClaimOwnershipOnWrite("bob-work", "bob", tenantId: "");
        Seed("shared-id", "alice-private", tenantId: "");
        Seed("shared-id", "bob-work", tenantId: "");

        Assert.False(registry.HasAccess("bob", "alice-private", requiredLevel: "read", tenantId: ""));

        var bob = new NamespaceAccess(registry, new PrincipalContext(string.Empty, "bob"));
        var resolved = bob.ResolveReadableEntry(_index, "shared-id");

        // Both namespaces are candidates, so the index handed the resolver a pair that looks
        // ambiguous. Filtering before matching is what makes it unambiguous for bob: alice's twin
        // contributes neither a match nor an ambiguity signal — property (1).
        Assert.Equal("bob-work", resolved!.Ns);

        // The symmetric case: an id living ONLY where bob cannot look comes back null, identical
        // to an id that does not exist. Nothing distinguishes not-permitted from not-found.
        Seed("alice-only", "alice-private", tenantId: "");
        Assert.Null(bob.ResolveReadableEntry(_index, "alice-only"));
        Assert.Null(bob.ResolveReadableEntry(_index, "never-stored"));
    }
}
