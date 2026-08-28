using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

/// <summary>
/// A count that describes a payload it does not match is a disclosure oracle, and the cluster tools
/// are the second place it was found: <c>find_contradictions</c> reported a total taken before the
/// ACL filter, and <c>get_cluster</c>/<c>list_clusters</c> reported a <c>MemberCount</c> taken
/// before it. The arithmetic states exactly what the filter withheld — a reader of a shared cluster
/// saw zero members beside a count of one, which is "there is a member here you may not see",
/// delivered as a number instead of as an entry.
///
/// The two filters in the path are different and both are needed. <see cref="ClusterManager"/> is
/// ACL-BLIND — it has no principal — and screens for topology attribution only; its count is correct
/// at its own layer and stays there. <see cref="ClusterTools"/> is the only layer that has a
/// principal, so the member list AND every field describing it are narrowed there, together.
///
/// Every principal here is genuinely IDENTIFIED. <c>NamespaceRegistry.HasAccess</c> short-circuits
/// <c>AgentIdentity.Default</c> to unrestricted, so a default-agent version of these tests would
/// pass against the unfixed tool and prove nothing. The one deliberate default-agent test is the
/// legacy mirror at the bottom, whose entire job is to show that nothing changed there.
///
/// The clusters deliberately live in the namespace Bob MAY read. If they lived in Alice's private
/// namespace the tool's existing read gate on the cluster's own namespace would deny first, and the
/// tests would pass without the member projection ever running.
/// </summary>
public class ClusterCountDisclosureTests : IDisposable
{
    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 2;
        // Uniform embedding: nothing here depends on similarity, so a leak can only ever be a
        // permission failure and never a ranking artifact.
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    private const string AlicePrivateNs = "ccd-alice-private";
    private const string AliceSharedNs = "ccd-alice-shared";

    // Bob genuinely owns this. An identified principal inherits nothing from an unregistered
    // namespace, so Bob has to be a real owner somewhere or the gates would deny for the wrong
    // reason and every assertion below would be vacuous.
    private const string BobNs = "ccd-bob-work";

    // No apostrophes in any text asserted against serialized JSON: System.Text.Json's default
    // encoder escapes U+0027, so a quoted phrase would never appear literally and the leak
    // assertions would pass vacuously.
    private const string PrivateMemberId = "ccd-alice-private-member";
    private const string PrivateMemberText = "unshared incident write-up CCD-SECRET-4K2X";
    private const string SharedMemberId = "ccd-alice-shared-member";
    private const string SharedMemberText = "shared roadmap note for the quarter";

    private const string PrivateOnlyCluster = "ccd-private-only";
    private const string MixedCluster = "ccd-mixed";

    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly ClusterManager _clusters;
    private readonly NamespaceRegistry _registry;
    private readonly StubEmbedding _embedding = new();

    public ClusterCountDisclosureTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"cluster_count_disclosure_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 10);
        _index = new CognitiveIndex(_persistence);
        _clusters = new ClusterManager(_index, _persistence);
        _registry = new NamespaceRegistry(_index, _embedding);

        // Timestamps are pinned rather than left to wall-clock ordering: staleness is a strict
        // CreatedAt comparison, and entries written microseconds apart can share a tick.
        // The private member is NEWER than any summary stored below, so it alone makes a summary
        // stale; the shared member is OLDER, so it never does. That split is what lets the staleness
        // bit be attributed to one member or the other.
        Seed(PrivateMemberId, AlicePrivateNs, PrivateMemberText, DateTimeOffset.UtcNow.AddHours(1));
        Seed(SharedMemberId, AliceSharedNs, SharedMemberText, DateTimeOffset.UtcNow.AddHours(-1));

        _registry.EnsureOwnership(AlicePrivateNs, "alice", tenantId: "");
        _registry.EnsureOwnership(AliceSharedNs, "alice", tenantId: "");
        _registry.EnsureOwnership(BobNs, "bob", tenantId: "");
        _registry.Share(AliceSharedNs, "alice", "bob", "read", tenantId: "");

        // Both clusters live in the namespace Bob may read; only their MEMBERS differ in reach.
        _clusters.CreateCluster(PrivateOnlyCluster, AliceSharedNs, [PrivateMemberId],
            "alice private grouping", tenantId: "");
        _clusters.CreateCluster(MixedCluster, AliceSharedNs, [SharedMemberId, PrivateMemberId],
            "alice mixed grouping", tenantId: "");

        // The summaries live in the cluster's own namespace, which Bob may read — so HasSummary and
        // SummaryEntry must survive the projection. Only what describes the MEMBERS is narrowed.
        _clusters.StoreSummary(PrivateOnlyCluster, "summary of the private grouping",
            _embedding.Embed("summary"), tenantId: "");
        _clusters.StoreSummary(MixedCluster, "summary of the mixed grouping",
            _embedding.Embed("summary"), tenantId: "");
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    // ── fixtures ──

    private void Seed(string id, string ns, string text, DateTimeOffset createdAt) =>
        _index.Upsert(new CognitiveEntry(id, _embedding.Embed(text), ns, text) { CreatedAt = createdAt });

    private static string Json(object? o) => System.Text.Json.JsonSerializer.Serialize(o);

    private ClusterTools Tools(string agentId) =>
        new(_clusters, _embedding, new NamespaceAccess(_registry, new AgentIdentity(agentId)));

    private GetClusterResult View(string agentId, string clusterId) =>
        Assert.IsType<GetClusterResult>(Tools(agentId).GetCluster(clusterId));

    private ClusterSummaryInfo Listed(string agentId, string clusterId) =>
        Assert.Single(Tools(agentId).ListClusters(AliceSharedNs), c => c.ClusterId == clusterId);

    /// <summary>
    /// The one thing the reply must never carry: the withheld member's id, its text, or the
    /// namespace it lives in. Asserted on the SERIALIZED reply, because emptiness of a collection
    /// would also pass against an implementation that leaked the same facts through another field.
    /// </summary>
    private static void AssertNothingPrivateLeaked(object? reply)
    {
        var json = Json(reply);
        Assert.DoesNotContain(PrivateMemberId, json);
        Assert.DoesNotContain(PrivateMemberText, json);
        Assert.DoesNotContain(AlicePrivateNs, json);
    }

    // ── 1. THE REPRODUCTION: an identified reader of a shared cluster, zero visible members ──

    [Fact]
    public void GetCluster_WithdrawsThePrivateMemberFromTheCountAsWellAsFromTheList()
    {
        // The ACL is real in both directions, so neither result below is an accident of setup.
        Assert.True(_registry.HasAccess("bob", AliceSharedNs, "read", tenantId: ""));
        Assert.False(_registry.HasAccess("bob", AlicePrivateNs, "read", tenantId: ""));

        var bob = View("bob", PrivateOnlyCluster);

        Assert.Empty(bob.Members);
        // THE FINDING: this was 1 beside an empty list, which states that a member exists and is
        // being withheld — the same disclosure the entry itself would have been, minus the text.
        Assert.Equal(0, bob.MemberCount);
        AssertNothingPrivateLeaked(bob);

        // The cluster itself is legitimately Bob's to read, so suppression must not degrade into
        // suppressing the reply — he would then read his own permission level off its shape.
        Assert.Equal(PrivateOnlyCluster, bob.ClusterId);
        Assert.Equal(AliceSharedNs, bob.Namespace);
        Assert.Equal("alice private grouping", bob.Label);
        Assert.NotNull(bob.SummaryEntry);

        // Control on the same fixture: Core really does hold that membership and really does
        // consider the summary stale, so the zero above is suppression and not an empty cluster.
        var core = _clusters.GetCluster(PrivateOnlyCluster, tenantId: "");
        Assert.Equal(1, core!.MemberCount);
        Assert.Equal(PrivateMemberId, Assert.Single(core.Members).Id);
        Assert.True(core.IsStale);
    }

    [Fact]
    public void ListClusters_CountsOnlyTheMembersThePrincipalMayRead()
    {
        var listing = Tools("bob").ListClusters(AliceSharedNs);

        // Both clusters are still listed — they live in a namespace Bob may read, and hiding them
        // would be the over-correction. It is their member tallies that narrow.
        Assert.Equal(2, listing.Count);
        Assert.Equal(0, Listed("bob", PrivateOnlyCluster).MemberCount);
        Assert.Equal(1, Listed("bob", MixedCluster).MemberCount);
        AssertNothingPrivateLeaked(listing);

        // HasSummary is not blanket-suppressed: the summary lives in the cluster's own namespace,
        // Bob may read it, and get_cluster hands it to him. The flag has to agree with that.
        Assert.All(listing, c => Assert.True(c.HasSummary));

        // Control: the ACL-blind layer tallies both members, which is correct where it sits.
        Assert.Equal(1, Assert.Single(_clusters.ListClusters(AliceSharedNs, tenantId: ""),
            c => c.ClusterId == PrivateOnlyCluster).MemberCount);
        Assert.Equal(2, Assert.Single(_clusters.ListClusters(AliceSharedNs, tenantId: ""),
            c => c.ClusterId == MixedCluster).MemberCount);
    }

    // ── 2. DERIVED FIELDS: staleness is a claim about the members, so it follows them ──

    /// <summary>
    /// Staleness compares each member's CreatedAt against the summary's. Computed over the
    /// unfiltered set it answers "something you may not read is newer than this summary" — one bit
    /// about an entry the caller was just refused. Here Bob's only visible member is OLDER than the
    /// summary, so <c>false</c> is both the withheld answer and the honest answer over what he can
    /// see; Alice, who can see the newer private member, still gets <c>true</c>.
    /// </summary>
    [Fact]
    public void Staleness_AgreesWithTheVisibleProjectionAndNotWithTheHiddenOne()
    {
        var bob = View("bob", MixedCluster);
        Assert.Equal(SharedMemberId, Assert.Single(bob.Members).Id);
        Assert.False(bob.IsStale);
        AssertNothingPrivateLeaked(bob);

        // The bit really was true before the filter, so the false above is the projection and not a
        // fixture that was never stale.
        Assert.True(_clusters.GetCluster(MixedCluster, tenantId: "")!.IsStale);
        Assert.True(View("alice", MixedCluster).IsStale);

        // ...and it is genuinely correct over Bob's visible member, not merely withheld: the one
        // member he can see predates the summary.
        var summaryCreatedAt = _index.Get($"summary:{MixedCluster}")!.CreatedAt;
        Assert.True(_index.Get(SharedMemberId)!.CreatedAt < summaryCreatedAt);
        Assert.True(_index.Get(PrivateMemberId)!.CreatedAt > summaryCreatedAt);
    }

    /// <summary>
    /// The invariant behind every assertion in this class, stated once over every principal and both
    /// tools: the number a caller is given equals the number of members that caller received, and
    /// the two tools never disagree about it. Any gap between them is the oracle.
    /// </summary>
    [Fact]
    public void EveryPrincipalsCountEqualsTheListItActuallyReceived()
    {
        foreach (var agentId in new[] { "alice", "bob", AgentIdentity.DefaultAgentId })
        {
            foreach (var clusterId in new[] { PrivateOnlyCluster, MixedCluster })
            {
                var view = View(agentId, clusterId);
                Assert.Equal(view.Members.Count, view.MemberCount);
                Assert.Equal(view.MemberCount, Listed(agentId, clusterId).MemberCount);
            }
        }
    }

    // ── 3. OVER-CORRECTION CONTROLS: the cheap wrong fix is to zero the count for everyone ──

    [Fact]
    public void TheOwner_WhoMayReadEverything_StillSeesEveryMemberAndTheFullCount()
    {
        var privateOnly = View("alice", PrivateOnlyCluster);
        Assert.Equal(PrivateMemberId, Assert.Single(privateOnly.Members).Id);
        Assert.Equal(1, privateOnly.MemberCount);
        Assert.True(privateOnly.IsStale);

        var mixed = View("alice", MixedCluster);
        Assert.Equal(2, mixed.MemberCount);
        Assert.Contains(mixed.Members, m => m.Id == PrivateMemberId);
        Assert.Contains(mixed.Members, m => m.Id == SharedMemberId);

        // The listing agrees with the detail view for the owner too.
        Assert.Equal(1, Listed("alice", PrivateOnlyCluster).MemberCount);
        Assert.Equal(2, Listed("alice", MixedCluster).MemberCount);
    }

    [Fact]
    public void AReaderWithPartialVisibility_SeesExactlyTheMembersItMayRead()
    {
        var mixed = View("bob", MixedCluster);

        // Not zero, and not two: exactly the one member whose namespace Bob was granted.
        var member = Assert.Single(mixed.Members);
        Assert.Equal(SharedMemberId, member.Id);
        Assert.Equal(SharedMemberText, member.Text);
        Assert.Equal(AliceSharedNs, member.Namespace);
        Assert.Equal(1, mixed.MemberCount);
        Assert.Equal(1, Listed("bob", MixedCluster).MemberCount);
        AssertNothingPrivateLeaked(mixed);
    }

    [Fact]
    public void ANamespaceThePrincipalCannotRead_IsStillAnEmptyListing()
    {
        // Unchanged, and asserted so the narrowing above cannot be mistaken for the whole gate:
        // a namespace Bob has no grant on discloses nothing at all, not even cluster ids.
        Assert.Empty(Tools("bob").ListClusters(AlicePrivateNs));

        // Carol has no grant on the cluster's own namespace, so the reply is the plain miss — the
        // same string an id that does not exist produces. Not-found and not-permitted stay
        // indistinguishable; narrowing the member count must not introduce a third shape.
        var denied = Assert.IsType<string>(Tools("carol").GetCluster(PrivateOnlyCluster));
        Assert.Equal($"Cluster '{PrivateOnlyCluster}' not found.", denied);
        Assert.Equal(Assert.IsType<string>(Tools("carol").GetCluster("ccd-no-such-cluster"))
            .Replace("ccd-no-such-cluster", PrivateOnlyCluster, StringComparison.Ordinal), denied);
    }

    // ── 4. LEGACY MIRROR: the unrestricted default agent, byte-for-byte as before ──

    /// <summary>
    /// The default agent is unisolated by design — <c>HasAccess</c> short-circuits it — so nothing
    /// is ever withheld from it and the tool's figures must equal the ACL-blind ones Core computes.
    /// This is the test that would fail if the fix had over-corrected into suppressing counts
    /// generally rather than narrowing them to a principal.
    /// </summary>
    [Fact]
    public void TheLegacyDefaultAgent_SeesTheSameCountsAndMembersAsBefore()
    {
        var privateOnly = View(AgentIdentity.DefaultAgentId, PrivateOnlyCluster);
        Assert.Equal(PrivateMemberId, Assert.Single(privateOnly.Members).Id);
        Assert.Equal(PrivateMemberText, privateOnly.Members[0].Text);
        Assert.Equal(1, privateOnly.MemberCount);
        Assert.True(privateOnly.IsStale);

        var mixed = View(AgentIdentity.DefaultAgentId, MixedCluster);
        Assert.Equal(2, mixed.MemberCount);
        Assert.Equal(2, mixed.Members.Count);
        Assert.True(mixed.IsStale);

        // The whole listing, compared against Core's ACL-blind one rather than against hand-written
        // expectations: for this principal the projection must be the identity function.
        Assert.Equal(
            Json(_clusters.ListClusters(AliceSharedNs, tenantId: "")
                .OrderBy(c => c.ClusterId, StringComparer.Ordinal).ToList()),
            Json(Tools(AgentIdentity.DefaultAgentId).ListClusters(AliceSharedNs)
                .OrderBy(c => c.ClusterId, StringComparer.Ordinal).ToList()));
    }
}
