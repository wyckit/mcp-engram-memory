using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Models.Planning;
using McpEngramMemory.Core.Services.Planning;

namespace McpEngramMemory.Tests;

public sealed class PlanningPipelineTests
{
    [Fact]
    public async Task Planner_AuthorizesSourceAndCandidateBeforeRelevance()
    {
        var sourceReference = Ref("adapter", ArtifactKind.Document);
        var allowed = Ref("allowed");
        var denied = Ref("denied");
        var authorization = new RecordingAuthorizer(request =>
            Decision(request.Artifact != denied));
        var source = new StubSource(
            "memory",
            sourceReference,
            _ =>
            {
                Assert.Contains(authorization.Calls,
                    call => call.Artifact == sourceReference && call.Capability == ArtifactCapability.Search);
                return new[] { Candidate(denied), Candidate(allowed) };
            });
        var relevanceCalls = new List<ArtifactRef>();
        var relevance = new StubRelevance(candidate =>
        {
            Assert.Contains(authorization.Calls,
                call => call.Artifact == candidate.Artifact && call.Capability == ArtifactCapability.Search);
            relevanceCalls.Add(candidate.Artifact);
            return 0.5;
        });

        var plan = await new RetrievalPlanner(new[] { source }, authorization, relevance)
            .PlanAsync(Request(Scope("memory"), maximumItems: 5));

        Assert.Equal(new[] { allowed }, relevanceCalls);
        Assert.Equal(allowed, Assert.Single(plan.Items).Artifact);
        Assert.DoesNotContain(plan.Trace, item => item.Artifact == denied);
        var authorizationSequence = Assert.Single(plan.Trace.Where(item =>
            item.Stage == PlanningTraceStage.ArtifactAuthorization && item.Artifact == allowed)).Sequence;
        var relevanceSequence = Assert.Single(plan.Trace.Where(item =>
            item.Stage == PlanningTraceStage.RelevanceOrdering && item.Artifact == allowed)).Sequence;
        Assert.True(authorizationSequence < relevanceSequence);
    }

    [Fact]
    public void LoadoutComposition_RejectsCapabilityPermissionSourceAndBudgetBroadening()
    {
        var profile = Profile("memory");

        Assert.Throws<InvalidOperationException>(() => AgentProfileComposer.Compose(
            profile,
            Loadout(new[] { ArtifactCapability.Read, ArtifactCapability.Search, ArtifactCapability.Train },
                Envelope(
                    (ArtifactCapability.Read, new[] { "alice" }),
                    (ArtifactCapability.Search, new[] { "alice" })),
                new[] { "memory" })));

        Assert.Throws<InvalidOperationException>(() => AgentProfileComposer.Compose(
            profile,
            Loadout(new[] { ArtifactCapability.Read, ArtifactCapability.Search },
                Envelope(
                    (ArtifactCapability.Read, new[] { "alice", "bob" }),
                    (ArtifactCapability.Search, new[] { "alice" })),
                new[] { "memory" })));

        Assert.Throws<InvalidOperationException>(() => AgentProfileComposer.Compose(
            profile,
            Loadout(new[] { ArtifactCapability.Read },
                Envelope((ArtifactCapability.Read, new[] { "alice" })),
                new[] { "external" })));

        Assert.Throws<InvalidOperationException>(() => AgentProfileComposer.Compose(
            profile,
            new AgentLoadout(
                "loadout", "v1", new[] { ArtifactCapability.Read },
                Envelope((ArtifactCapability.Read, new[] { "alice" })),
                new[] { "memory" },
                11,
                new ContextBudget(100, 100, 10))));

        var narrowed = AgentProfileComposer.Compose(
            profile,
            Loadout(new[] { ArtifactCapability.Read },
                Envelope((ArtifactCapability.Read, new[] { "alice" })),
                new[] { "memory" }));
        Assert.Equal(new[] { ArtifactCapability.Read }, narrowed.Capabilities);
        Assert.False(narrowed.Allows(ArtifactCapability.Search));
    }

    [Fact]
    public async Task ContextCompiler_UsesStablePlanOrderAndDeterministicBudgets()
    {
        var scope = Scope("memory");
        var first = Ref("first");
        var second = Ref("second");
        var third = Ref("third");
        var plan = Plan(scope,
            Item(3, third),
            Item(1, first),
            Item(2, second));
        var adapter = new StubContextAdapter("memory", item => new ContextArtifact(
            item.Artifact == third ? "cc" : "aaaa",
            Hash(item.Artifact.ArtifactId[0]),
            item.References));
        var compiler = new ContextCompiler(
            new[] { adapter },
            new RecordingAuthorizer(_ => Decision(true)),
            new CharacterTokenCounter());
        var request = new ContextCompilationRequest(
            "compile", plan, scope, new ContextBudget(6, 6, 2));

        var firstRun = await compiler.CompileAsync(request);
        var secondRun = await compiler.CompileAsync(request);

        Assert.Equal(new[] { first, third }, firstRun.Items.Select(item => item.Artifact));
        Assert.Equal(firstRun.Items.Select(item => item.Artifact),
            secondRun.Items.Select(item => item.Artifact));
        Assert.Equal(6, firstRun.UsedTokens);
        Assert.Equal(6, firstRun.UsedBytes);
        Assert.Equal(PlanningStatus.Incomplete, firstRun.Status);
        Assert.Contains(firstRun.Omissions,
            omission => omission.Artifact == second && omission.Code == "context-budget-exceeded");
    }

    [Fact]
    public async Task ContextCompiler_RemovesSelectionWhoseAuthorizationBecameStale()
    {
        var scope = Scope("memory");
        var artifact = Ref("revoked");
        var authorization = new RecordingAuthorizer(request =>
            Decision(request.Capability == ArtifactCapability.Search));
        var planner = new RetrievalPlanner(
            new[] { new StubSource("memory", Ref("adapter", ArtifactKind.Document),
                _ => new[] { Candidate(artifact) }) },
            authorization,
            new StubRelevance(_ => 1));
        var plan = await planner.PlanAsync(Request(scope, maximumItems: 1));
        Assert.Single(plan.Items);
        var materializationCalls = 0;
        var compiler = new ContextCompiler(
            new[]
            {
                new StubContextAdapter("memory", item =>
                {
                    materializationCalls++;
                    return new ContextArtifact("secret", Hash('a'), item.References);
                })
            },
            authorization,
            new CharacterTokenCounter());

        var manifest = await compiler.CompileAsync(new ContextCompilationRequest(
            "compile", plan, scope, new ContextBudget(100, 100, 10)));

        Assert.Equal(PlanningStatus.Abstained, manifest.Status);
        Assert.Empty(manifest.Items);
        Assert.Equal(0, materializationCalls);
        Assert.Contains(manifest.Omissions,
            omission => omission.Code == "selected-reference-no-longer-authorized");
    }

    [Fact]
    public async Task ContextCompiler_PreservesAndReauthorizesCitationProvenanceAndAuditReferences()
    {
        var scope = Scope("memory");
        var primary = Ref("claim", ArtifactKind.Knowledge);
        var citationOne = Ref("citation-1", ArtifactKind.Document);
        var citationTwo = Ref("citation-2", ArtifactKind.Document);
        var provenanceOne = Ref("provenance-1", ArtifactKind.Evidence);
        var provenanceTwo = Ref("provenance-2", ArtifactKind.Evidence);
        var auditOne = Ref("audit-1", ArtifactKind.Verification);
        var auditTwo = Ref("audit-2", ArtifactKind.Verification);
        var planReferences = new ArtifactReferenceSet(
            primary, new[] { citationOne }, new[] { provenanceOne }, new[] { auditOne });
        var plan = Plan(scope, Item(1, planReferences));
        var materializedReferences = new ArtifactReferenceSet(
            primary, new[] { citationTwo }, new[] { provenanceTwo }, new[] { auditTwo });
        var authorization = new RecordingAuthorizer(_ => Decision(true));
        var compiler = new ContextCompiler(
            new[]
            {
                new StubContextAdapter("memory", _ => new ContextArtifact(
                    "governed claim", Hash('b'), materializedReferences))
            },
            authorization,
            new CharacterTokenCounter());

        var manifest = await compiler.CompileAsync(new ContextCompilationRequest(
            "compile", plan, scope, new ContextBudget(100, 100, 10)));

        var item = Assert.Single(manifest.Items);
        Assert.Equal(new[] { citationOne, citationTwo }, item.References.Citations);
        Assert.Equal(new[] { provenanceOne, provenanceTwo }, item.References.Provenance);
        Assert.Equal(new[] { auditOne, auditTwo }, item.References.AuditRecords);
        Assert.All(item.References.All, reference => Assert.Contains(authorization.Calls,
            call => call.Artifact == reference && call.Capability == ArtifactCapability.Read));
        Assert.False(manifest.HasEpistemicAssessment);
    }

    [Fact]
    public async Task SourceAndContextAdapterErrors_FailClosedWithAuditableAbstention()
    {
        var scope = Scope("memory");
        var source = new StubSource(
            "memory",
            Ref("adapter", ArtifactKind.Document),
            _ => throw new InvalidOperationException("source failure"));
        var authorization = new RecordingAuthorizer(_ => Decision(true));
        var planner = new RetrievalPlanner(
            new[] { source }, authorization, new StubRelevance(_ => 1));

        var failedPlan = await planner.PlanAsync(Request(scope, maximumItems: 1));

        Assert.Equal(PlanningStatus.Abstained, failedPlan.Status);
        Assert.Empty(failedPlan.Items);
        Assert.Contains(failedPlan.Trace, item =>
            item.Code == "source-adapter-failed" && item.Outcome == PlanningTraceOutcome.FailedClosed);

        var artifact = Ref("selected");
        var selectedPlan = Plan(scope, Item(1, artifact));
        var compiler = new ContextCompiler(
            new[]
            {
                new StubContextAdapter("memory", _ =>
                    throw new InvalidOperationException("materialization failure"))
            },
            authorization,
            new CharacterTokenCounter());

        var manifest = await compiler.CompileAsync(new ContextCompilationRequest(
            "compile", selectedPlan, scope, new ContextBudget(100, 100, 10)));

        Assert.Equal(PlanningStatus.Abstained, manifest.Status);
        Assert.Empty(manifest.Items);
        Assert.Contains(manifest.Trace, item =>
            item.Code == "context-adapter-failed" && item.Outcome == PlanningTraceOutcome.FailedClosed);
    }

    [Fact]
    public async Task AuthorizationAndRelevanceAdapterErrors_NeverSelectACandidate()
    {
        var scope = Scope("memory");
        var sourceReference = Ref("adapter", ArtifactKind.Document);
        var candidate = Ref("candidate");
        var source = new StubSource("memory", sourceReference, _ => new[] { Candidate(candidate) });
        var relevanceCalls = 0;
        var failedAuthorization = new RecordingAuthorizer(request =>
        {
            if (request.Artifact == candidate)
                throw new InvalidOperationException("authorization failure");
            return Decision(true);
        });
        var relevance = new StubRelevance(_ =>
        {
            relevanceCalls++;
            return 1;
        });

        var authorizationPlan = await new RetrievalPlanner(
                new[] { source }, failedAuthorization, relevance)
            .PlanAsync(Request(scope, maximumItems: 1));

        Assert.Equal(PlanningStatus.Abstained, authorizationPlan.Status);
        Assert.Empty(authorizationPlan.Items);
        Assert.Equal(0, relevanceCalls);
        Assert.Contains(authorizationPlan.Trace, item =>
            item.Code == "authorization-adapter-failed" &&
            item.Outcome == PlanningTraceOutcome.FailedClosed);

        var relevancePlan = await new RetrievalPlanner(
                new[] { source },
                new RecordingAuthorizer(_ => Decision(true)),
                new StubRelevance(_ => throw new InvalidOperationException("relevance failure")))
            .PlanAsync(Request(scope, maximumItems: 1));

        Assert.Equal(PlanningStatus.Abstained, relevancePlan.Status);
        Assert.Empty(relevancePlan.Items);
        Assert.Contains(relevancePlan.Trace, item =>
            item.Code == "relevance-adapter-failed" &&
            item.Outcome == PlanningTraceOutcome.FailedClosed);
    }

    private static RetrievalPlanningRequest Request(ScopedAgentProfile scope, int maximumItems)
        => new("plan", "query", scope, new[] { ArtifactKind.Memory, ArtifactKind.Evidence }, maximumItems);

    private static RetrievalPlan Plan(ScopedAgentProfile scope, params RetrievalPlanItem[] items)
        => new("plan", "query", scope, PlanningStatus.Complete, items,
            Array.Empty<PlanningOmission>(), Array.Empty<PlanningTraceEvent>());

    private static RetrievalPlanItem Item(int rank, ArtifactRef artifact)
        => Item(rank, new ArtifactReferenceSet(artifact));

    private static RetrievalPlanItem Item(int rank, ArtifactReferenceSet references)
        => new(rank, "memory", $"candidate-{rank}", references, 1d,
            Array.Empty<string>());

    private static RetrievalCandidate Candidate(ArtifactRef artifact)
        => new(artifact.ArtifactId, artifact.ArtifactId, new ArtifactReferenceSet(artifact));

    private static ArtifactAuthorizationDecision Decision(bool authorized)
        => new(authorized, authorized ? "allowed" : "denied");

    private static ArtifactRef Ref(string id, ArtifactKind kind = ArtifactKind.Memory)
        => new("tenant", "project", kind, id, "v1");

    private static string Hash(char value) => new(value, 64);

    private static ScopedAgentProfile Scope(params string[] sources)
        => AgentProfileComposer.Compose(Profile(sources), Loadout(
            new[] { ArtifactCapability.Read, ArtifactCapability.Search },
            Envelope(
                (ArtifactCapability.Read, new[] { "alice" }),
                (ArtifactCapability.Search, new[] { "alice" })),
            sources));

    private static AgentProfile Profile(params string[] sources)
        => new(
            "profile", "v1", "tenant", "alice", "answer-user",
            new[] { ArtifactCapability.Read, ArtifactCapability.Search },
            Envelope(
                (ArtifactCapability.Read, new[] { "alice" }),
                (ArtifactCapability.Search, new[] { "alice" })),
            sources,
            10,
            new ContextBudget(100, 100, 10));

    private static AgentLoadout Loadout(
        IEnumerable<ArtifactCapability> capabilities,
        PermissionEnvelope permissions,
        IEnumerable<string> sources)
        => new("loadout", "v1", capabilities, permissions, sources, 10,
            new ContextBudget(100, 100, 10));

    private static PermissionEnvelope Envelope(
        params (ArtifactCapability Capability, string[] Subjects)[] grants)
        => new(grants.Select(value => new CapabilityGrant(value.Capability, value.Subjects)));

    private sealed class RecordingAuthorizer : IArtifactAuthorizationAdapter
    {
        private readonly Func<ArtifactAuthorizationRequest, ArtifactAuthorizationDecision> _authorize;
        public List<ArtifactAuthorizationRequest> Calls { get; } = new();

        public RecordingAuthorizer(
            Func<ArtifactAuthorizationRequest, ArtifactAuthorizationDecision> authorize)
            => _authorize = authorize;

        public ValueTask<ArtifactAuthorizationDecision> AuthorizeAsync(
            ArtifactAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            return ValueTask.FromResult(_authorize(request));
        }
    }

    private sealed class StubSource : IRetrievalSourceAdapter
    {
        private readonly Func<RetrievalSourceQuery, IReadOnlyList<RetrievalCandidate>> _discover;

        public StubSource(
            string sourceId,
            ArtifactRef sourceReference,
            Func<RetrievalSourceQuery, IReadOnlyList<RetrievalCandidate>> discover)
        {
            Descriptor = new RetrievalSourceDescriptor(sourceId, "v1", sourceReference);
            _discover = discover;
        }

        public RetrievalSourceDescriptor Descriptor { get; }

        public ValueTask<IReadOnlyList<RetrievalCandidate>> DiscoverAsync(
            RetrievalSourceQuery query,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_discover(query));
    }

    private sealed class StubRelevance : IRetrievalRelevanceAdapter
    {
        private readonly Func<RetrievalCandidate, double> _score;
        public StubRelevance(Func<RetrievalCandidate, double> score) => _score = score;
        public string AdapterId => "test-relevance";
        public string Version => "v1";

        public ValueTask<double> ScoreAsync(
            RetrievalSourceQuery query,
            RetrievalSourceDescriptor source,
            RetrievalCandidate authorizedCandidate,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_score(authorizedCandidate));
    }

    private sealed class StubContextAdapter : IContextArtifactAdapter
    {
        private readonly Func<RetrievalPlanItem, ContextArtifact> _materialize;
        public StubContextAdapter(string sourceId, Func<RetrievalPlanItem, ContextArtifact> materialize)
        {
            SourceId = sourceId;
            _materialize = materialize;
        }

        public string SourceId { get; }

        public ValueTask<ContextArtifact> MaterializeAsync(
            RetrievalPlanItem item,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_materialize(item));
    }

    private sealed class CharacterTokenCounter : IContextTokenCounter
    {
        public int CountTokens(string content) => content.Length;
    }
}
