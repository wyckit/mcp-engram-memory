using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

/// <summary>
/// memory_feedback must apply the mutation to the entry it authorized, and to no other.
///
/// The bug this fixture pins: <c>MemoryFeedback</c> resolved the caller's own visible twin —
/// correctly — and then, for any caller whose tenant id was empty, discarded that resolved
/// namespace and forwarded the caller-supplied <c>ns</c> to
/// <see cref="LifecycleEngine.ApplyFeedback"/>, which performs no second ACL check. Authorize A,
/// mutate B. The mistaken premise was that an empty tenant means unrestricted; "" is the legacy
/// PARTITION, and identified ACL principals live in it. Only the DEFAULT AGENT is unisolated
/// (<see cref="NamespaceRegistry.HasAccess"/> short-circuits it), which is why every test below
/// that exercises the ACL drives the tools as Bob, an honestly-identified second agent — a
/// default-agent test cannot observe an ACL failure at all and would pass with the fix reverted.
/// </summary>
public class LifecycleFeedbackAuthorizationTests : IDisposable
{
    private sealed class StubEmbedding : IEmbeddingService
    {
        public int Dimensions => 2;
        // Uniform embedding: nothing here depends on similarity, so any reachability is a
        // permission outcome and never a scoring artifact.
        public float[] Embed(string text) => [0.5f, 0.5f];
    }

    private readonly string _path;
    private readonly PersistenceManager _persistence;
    private readonly CognitiveIndex _index;
    private readonly LifecycleEngine _lifecycle;
    private readonly NamespaceRegistry _registry;
    private readonly StubEmbedding _embedding = new();

    private const string AliceNs = "alice-private";
    private const string BobNs = "bob-work";
    private const string SharedId = "shared-id";

    public LifecycleFeedbackAuthorizationTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"lcfeedback_acl_{Guid.NewGuid():N}");
        _persistence = new PersistenceManager(_path, debounceMs: 10);
        _index = new CognitiveIndex(_persistence);
        _lifecycle = new LifecycleEngine(_index, _persistence);
        _registry = new NamespaceRegistry(_index, _embedding);
    }

    public void Dispose()
    {
        _index.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }

    private LifecycleTools Tools(string agentId, string tenantId = "") => new(
        _lifecycle, _embedding, _index,
        new NamespaceAccess(_registry, new PrincipalContext(tenantId, agentId)));

    /// <summary>Write an entry and register the namespace to an identified owner, as a real write would.</summary>
    private void Seed(string agentId, string ns, string id, string text)
    {
        _index.Upsert(new CognitiveEntry(id, [1f, 0f], ns, text, lifecycleState: "stm", tenantId: ""));
        _registry.ClaimOwnershipOnWrite(ns, agentId, tenantId: "");
    }

    /// <summary>
    /// The same id in two namespaces of the legacy tenant, one Bob's and one Alice's. Ids are
    /// unique only per (tenant, namespace), so this is a legal store state, not a corruption.
    /// </summary>
    private void SeedConflictingTwins()
    {
        Seed("alice", AliceNs, SharedId, "alice's private note");
        Seed("bob", BobNs, SharedId, "bob's own note");

        // The ACL preconditions are the whole experiment; assert them so a fixture regression
        // cannot silently turn the exploit test into a tautology.
        Assert.False(_registry.HasAccess("bob", AliceNs, requiredLevel: "write", tenantId: ""));
        Assert.False(_registry.HasAccess("bob", AliceNs, requiredLevel: "read", tenantId: ""));
        Assert.True(_registry.HasAccess("bob", BobNs, requiredLevel: "write", tenantId: ""));
    }

    private CognitiveEntry Entry(string ns, string id) =>
        _index.Get(id, ns, tenantId: "") ?? throw new InvalidOperationException($"missing {ns}/{id}");

    // ── The exploit ──

    [Fact]
    public void MemoryFeedback_CallerNamedNamespace_CannotRedirectTheMutationOntoAnotherAgentsTwin()
    {
        SeedConflictingTwins();

        // Bob authorizes through the twin he legitimately owns and names Alice's namespace.
        // Before the fix his empty tenant id selected the caller-controlled `ns`, so this call
        // resolved Bob's entry and then boosted Alice's.
        var result = Tools("bob").MemoryFeedback(SharedId, 3.0f, ns: AliceNs);

        var feedback = Assert.IsType<FeedbackResult>(result);
        Assert.Equal(3f, feedback.NewActivationEnergy);

        // Alice's entry is untouched on every field ApplyFeedback writes: energy, state, and the
        // access count that a positive delta records. A freshly stored entry starts at 1, so
        // "still 1" is the untouched value and 2 would be the recorded access.
        var alice = Entry(AliceNs, SharedId);
        Assert.Equal(0f, alice.ActivationEnergy);
        Assert.Equal("stm", alice.LifecycleState);
        Assert.Equal(1, alice.AccessCount);

        // The delta landed on the entry that was actually authorized.
        var bob = Entry(BobNs, SharedId);
        Assert.Equal(3f, bob.ActivationEnergy);
        Assert.Equal(2, bob.AccessCount);
    }

    [Fact]
    public void MemoryFeedback_CallerNamedNamespace_CannotSuppressAnotherAgentsTwinIntoArchival()
    {
        // The mirror-image damage: the exploit is not only a boost. A negative delta large enough
        // to cross the archive threshold would have evicted Alice's entry from live retrieval,
        // which is a denial-of-service on data Bob cannot even read.
        SeedConflictingTwins();
        _index.SetActivationEnergyAndState(SharedId, -4f, "ltm", AliceNs, tenantId: "");

        var result = Tools("bob").MemoryFeedback(SharedId, -3.0f, ns: AliceNs);

        Assert.IsType<FeedbackResult>(result);
        var alice = Entry(AliceNs, SharedId);
        Assert.Equal(-4f, alice.ActivationEnergy);
        Assert.Equal("ltm", alice.LifecycleState);
        Assert.Equal(-3f, Entry(BobNs, SharedId).ActivationEnergy);
    }

    [Fact]
    public void MemoryFeedback_UnresolvableId_IsIndistinguishableFromAGenuineMiss()
    {
        // Naming a namespace Bob cannot write must not become an oracle either. An id that
        // exists ONLY in Alice's namespace has to answer exactly like an id that exists nowhere:
        // not-found, not-permitted and ambiguous are one reply.
        Seed("alice", AliceNs, SharedId, "alice's private note");

        var denied = Assert.IsType<string>(Tools("bob").MemoryFeedback(SharedId, 3.0f, ns: AliceNs));
        var genuineMiss = Assert.IsType<string>(
            Tools("bob").MemoryFeedback("no-such-entry-anywhere", 3.0f, ns: AliceNs));

        // Byte-equal once the id this test itself varied is normalized away. THIS EQUALITY IS THE
        // PROPERTY — a distinct denial would confirm that a private twin of the id exists.
        Assert.Equal(genuineMiss.Replace("no-such-entry-anywhere", SharedId, StringComparison.Ordinal), denied);
        Assert.Equal(0f, Entry(AliceNs, SharedId).ActivationEnergy);
    }

    // ── Over-correction controls: the fix authorizes the target, it does not disable feedback ──

    [Fact]
    public void MemoryFeedback_NullNamespace_StillAppliesToTheCallersOwnTwin()
    {
        SeedConflictingTwins();

        var feedback = Assert.IsType<FeedbackResult>(Tools("bob").MemoryFeedback(SharedId, 2.0f));

        Assert.Equal(SharedId, feedback.Id);
        Assert.Equal(2f, feedback.NewActivationEnergy);
        Assert.False(feedback.StateChanged);
        Assert.Equal(2f, Entry(BobNs, SharedId).ActivationEnergy);
        // Alice's invisible twin neither received the delta nor blanked the resolution.
        Assert.Equal(0f, Entry(AliceNs, SharedId).ActivationEnergy);
    }

    [Fact]
    public void MemoryFeedback_CallersOwnNamespace_BehavesIdenticallyToNullNamespace()
    {
        SeedConflictingTwins();
        var tools = Tools("bob");

        var withoutNs = Assert.IsType<FeedbackResult>(tools.MemoryFeedback(SharedId, 2.0f));
        var withOwnNs = Assert.IsType<FeedbackResult>(tools.MemoryFeedback(SharedId, 2.0f, ns: BobNs));

        // The second call picked up exactly where the first left off, which is only possible if
        // both landed on the same entry — naming your own namespace is neither a redirect nor a
        // refusal, it is simply ignored.
        Assert.Equal(withoutNs.Id, withOwnNs.Id);
        Assert.Equal(withoutNs.NewActivationEnergy, withOwnNs.PreviousActivationEnergy);
        Assert.Equal(withoutNs.PreviousState, withOwnNs.PreviousState);
        Assert.Equal(withoutNs.StateChanged, withOwnNs.StateChanged);
        Assert.Equal(4f, Entry(BobNs, SharedId).ActivationEnergy);
        Assert.Equal(0f, Entry(AliceNs, SharedId).ActivationEnergy);
    }

    // ── Legacy mirror: the default agent, the single-user deployment ──

    [Fact]
    public void MemoryFeedback_DefaultAgentUniqueId_IsUnchanged()
    {
        const string ns = "default-ns";
        _index.Upsert(new CognitiveEntry("solo", [1f, 0f], ns, "ordinary note", lifecycleState: "stm", tenantId: ""));

        var feedback = Assert.IsType<FeedbackResult>(
            Tools(AgentIdentity.DefaultAgentId).MemoryFeedback("solo", 2.0f));

        Assert.Equal(0f, feedback.PreviousActivationEnergy);
        Assert.Equal(2f, feedback.NewActivationEnergy);
        Assert.Equal("stm", feedback.PreviousState);
        Assert.Equal("stm", feedback.NewState);
        Assert.False(feedback.StateChanged);
        Assert.Equal(2f, Entry(ns, "solo").ActivationEnergy);
        // Stored at 1, incremented once by the positive delta's recorded access.
        Assert.Equal(2, Entry(ns, "solo").AccessCount);
    }

    [Fact]
    public void MemoryFeedback_NullNamespace_NowUsesTheEntrysOwnDecayConfigInsteadOfEngineDefaults()
    {
        // The one accepted behaviour delta, pinned rather than left implicit. Passing ns == null
        // used to send ApplyFeedback down its threshold-defaults path (stm 2.0 / archive -5.0)
        // even when the namespace holding the entry had a stored config. Now that the resolved
        // namespace is always forwarded, that config is honoured — the thresholds finally come
        // from the same namespace as the entry being scored, which is what configure_decay
        // always claimed to do.
        const string ns = "configured-ns";
        _index.Upsert(new CognitiveEntry("solo", [1f, 0f], ns, "ordinary note", lifecycleState: "stm", tenantId: ""));
        _index.SetActivationEnergyAndState("solo", 5f, null, ns, tenantId: "");

        var tools = Tools(AgentIdentity.DefaultAgentId);
        Assert.IsType<DecayConfig>(tools.ConfigureDecay(ns, stmThreshold: 10f));

        // -1 lands on 4.0: above the 2.0 default (no transition) and below the configured 10.0
        // (demote to ltm). The two behaviours are therefore distinguishable in one call.
        var feedback = Assert.IsType<FeedbackResult>(tools.MemoryFeedback("solo", -1.0f));

        Assert.Equal(4f, feedback.NewActivationEnergy);
        Assert.True(feedback.StateChanged);
        Assert.Equal("ltm", feedback.NewState);
        Assert.Equal("ltm", Entry(ns, "solo").LifecycleState);
    }
}
