using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Models.Learning;
using McpEngramMemory.Core.Services.Learning;

namespace McpEngramMemory.Tests;

public sealed class LearningPipelineTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly OperationArtifactReference Evidence =
        new("tenant-a", "sources", "Document", "source-1", "v1");

    [Fact]
    public async Task Teacher_EmitsImmutableQuarantinedProposal()
    {
        var generator = new StubGenerator(new KnowledgeProposalDraft(
            "The claim",
            KnowledgeHypothesisType.Generalization,
            new[] { Evidence },
            Array.Empty<OperationArtifactReference>(),
            0.2,
            new[] { "Needs broader fixture coverage" },
            new KnowledgeValidityInterval(Now, Now.AddDays(30)),
            new HashSet<KnowledgeCapability> { KnowledgeCapability.Read }));
        var teacher = new TeacherRuntime(generator);
        var request = new TeacherRequest(
            "proposal-1",
            "tenant-a",
            "learn",
            new[] { Evidence },
            Generator(),
            "constitution-v1",
            new HashSet<KnowledgeCapability> { KnowledgeCapability.Read },
            Now);

        var proposal = await teacher.ProposeAsync(request, Budget());

        Assert.Equal(LearningProposalStatus.Quarantined, proposal.Status);
        Assert.Equal("The claim", proposal.Claim);
        Assert.Equal("constitution-v1", proposal.ConstitutionVersionHash);
        Assert.Single(proposal.SupportingEvidence);
    }

    [Fact]
    public async Task Planner_OrdersDeterministicBeforeModelBeforeHuman()
    {
        var calls = new List<string>();
        var planner = new VerifierPlanner();
        var verifiers = new ILearningVerifier[]
        {
            PassingVerifier("human", VerifierKind.HumanApproval, calls),
            PassingVerifier("model", VerifierKind.Model, calls, "critic-model", "critic-prompt", "view-2"),
            PassingVerifier("det-b", VerifierKind.Deterministic, calls),
            PassingVerifier("det-a", VerifierKind.Deterministic, calls)
        };

        var trace = await planner.VerifyAsync(Proposal(), verifiers, Budget(maxVerifierRuns: 4));

        Assert.Equal(new[] { "det-a", "det-b", "model", "human" }, calls);
        Assert.Equal(new[] { 1, 2, 3, 4 }, trace.Runs.Select(run => run.Sequence));
        Assert.True(trace.DeterministicChecksPassed);
        Assert.True(trace.HumanApproved);
    }

    [Fact]
    public async Task DeterministicVeto_PreventsModelOverride()
    {
        var calls = new List<string>();
        var deterministic = new StubVerifier(
            new VerifierIdentity("schema", "1", VerifierKind.Deterministic),
            (_, _) =>
            {
                calls.Add("schema");
                return Result(VerificationStatus.Failed, "schema-invalid");
            });
        var model = PassingVerifier("model", VerifierKind.Model, calls, "teacher-model", "teacher-prompt", "view-1");

        var trace = await new VerifierPlanner().VerifyAsync(
            Proposal(), new ILearningVerifier[] { model, deterministic }, Budget(maxVerifierRuns: 2));

        Assert.Equal(new[] { "schema" }, calls);
        Assert.Equal(VerificationStatus.Failed, trace.Status);
        Assert.False(trace.DeterministicChecksPassed);
        Assert.DoesNotContain(trace.Runs, run => run.Verifier.Kind == VerifierKind.Model);
    }

    [Fact]
    public async Task SameModelPromptAndEvidenceView_IsRecordedAsNonIndependent()
    {
        var planner = new VerifierPlanner();
        var deterministic = PassingVerifier("schema", VerifierKind.Deterministic, new List<string>());
        var sameModel = PassingVerifier(
            "same-model",
            VerifierKind.Model,
            new List<string>(),
            "teacher-model",
            "teacher-prompt",
            "view-1");

        var trace = await planner.VerifyAsync(
            Proposal(), new[] { deterministic, sameModel }, Budget(maxVerifierRuns: 2));

        var modelRun = Assert.Single(trace.Runs.Where(run => run.Verifier.Kind == VerifierKind.Model));
        Assert.False(modelRun.IsIndependentFromTeacher);
        Assert.False(trace.HasIndependentModelPass);
    }

    [Fact]
    public void Promotion_DeniesProposalWithoutSupportingEvidence()
    {
        var proposal = Proposal(supportingEvidence: Array.Empty<OperationArtifactReference>());
        var result = Promote(proposal, PassingTrace(proposal), ConstitutionOutcome.Allow,
            new Dictionary<string, string>());

        Assert.Equal(PromotionOutcome.Denied, result.Outcome);
        Assert.Contains(result.Findings, finding => finding.Code == "supporting-evidence-required");
    }

    [Fact]
    public void Promotion_DeniesPermissionBroadening()
    {
        var proposal = Proposal(
            inherited: new[] { KnowledgeCapability.Read },
            requested: new[] { KnowledgeCapability.Read, KnowledgeCapability.Train });
        var result = Promote(proposal, PassingTrace(proposal), ConstitutionOutcome.Allow,
            CurrentVersions(Evidence));

        Assert.Equal(PromotionOutcome.Denied, result.Outcome);
        Assert.Contains(result.Findings, finding => finding.Code == "permission-broadening-forbidden");
    }

    [Fact]
    public void Promotion_RequiresThenAcceptsHumanApproval()
    {
        var proposal = Proposal();
        var withoutApproval = Promote(
            proposal,
            PassingTrace(proposal),
            ConstitutionOutcome.RequireApproval,
            CurrentVersions(Evidence));
        var withApproval = Promote(
            proposal,
            PassingTrace(proposal, humanApproved: true),
            ConstitutionOutcome.RequireApproval,
            CurrentVersions(Evidence));

        Assert.Equal(PromotionOutcome.RequireApproval, withoutApproval.Outcome);
        Assert.Equal(PromotionOutcome.Promoted, withApproval.Outcome);
    }

    [Fact]
    public async Task VerifierException_ProducesAuditReadyFailClosedTrace()
    {
        var verifier = new StubVerifier(
            new VerifierIdentity("deterministic-check", "2.3", VerifierKind.Deterministic),
            (_, _) => throw new InvalidOperationException("fixture failure"));

        var trace = await new VerifierPlanner().VerifyAsync(
            Proposal(), new[] { verifier }, Budget(maxVerifierRuns: 1));

        var run = Assert.Single(trace.Runs);
        Assert.Equal(1, run.Sequence);
        Assert.Equal("deterministic-check", run.Verifier.VerifierId);
        Assert.Equal("2.3", run.Verifier.Version);
        Assert.Equal(VerificationStatus.Error, run.Status);
        Assert.Equal("verifier-failed-closed", Assert.Single(run.Findings).Code);
        Assert.True(run.CompletedAt >= run.StartedAt);
        Assert.Equal(VerificationStatus.Failed, trace.Status);
    }

    private static KnowledgeProposal Proposal(
        IReadOnlyList<OperationArtifactReference>? supportingEvidence = null,
        IEnumerable<KnowledgeCapability>? inherited = null,
        IEnumerable<KnowledgeCapability>? requested = null)
        => new(
            "proposal-1",
            "tenant-a",
            "A governed claim",
            KnowledgeHypothesisType.Generalization,
            supportingEvidence ?? new[] { Evidence },
            Array.Empty<OperationArtifactReference>(),
            Generator(),
            0.1,
            Array.Empty<string>(),
            new KnowledgeValidityInterval(Now, null),
            "constitution-v1",
            inherited ?? new[] { KnowledgeCapability.Read, KnowledgeCapability.Use },
            requested,
            Now);

    private static GenerationIdentity Generator()
        => new("teacher-model", "runtime-1", "teacher-prompt", "prompt-v1", "view-1");

    private static LearningExecutionBudget Budget(int maxVerifierRuns = 8)
        => new(1, maxVerifierRuns, DateTimeOffset.UtcNow.AddMinutes(5));

    private static StubVerifier PassingVerifier(
        string id,
        VerifierKind kind,
        IList<string> calls,
        string? modelId = null,
        string? promptFamily = null,
        string? evidenceViewId = null)
        => new(
            new VerifierIdentity(id, "1", kind, modelId, promptFamily, evidenceViewId),
            (_, _) =>
            {
                calls.Add(id);
                return Result(VerificationStatus.Passed, "passed");
            });

    private static ValueTask<(VerificationStatus, IReadOnlyList<VerificationFinding>)> Result(
        VerificationStatus status,
        string code)
        => ValueTask.FromResult<(VerificationStatus, IReadOnlyList<VerificationFinding>)>((
            status,
            new[] { new VerificationFinding(code, code) }));

    private static VerificationTrace PassingTrace(KnowledgeProposal proposal, bool humanApproved = false)
    {
        var runs = new List<VerificationRun>
        {
            Run(1, "deterministic", VerifierKind.Deterministic, VerificationStatus.Passed)
        };
        if (humanApproved)
            runs.Add(Run(2, "human", VerifierKind.HumanApproval, VerificationStatus.Passed));
        return new VerificationTrace(proposal.ProposalId, runs);
    }

    private static VerificationRun Run(
        int sequence,
        string id,
        VerifierKind kind,
        VerificationStatus status)
        => new(
            sequence,
            new VerifierIdentity(id, "1", kind),
            status,
            true,
            Array.Empty<VerificationFinding>(),
            Now,
            Now);

    private static PromotionResult Promote(
        KnowledgeProposal proposal,
        VerificationTrace trace,
        ConstitutionOutcome outcome,
        IReadOnlyDictionary<string, string> versions)
    {
        var decision = new ConstitutionDecision(
            "promotion-op",
            ConstitutionPhase.Postcondition,
            outcome,
            Array.Empty<ConstitutionFinding>(),
            new[] { proposal.ConstitutionVersionHash });
        return new KnowledgePromotionEvaluator().Evaluate(
            new PromotionRequest(proposal, trace, decision, versions));
    }

    private static IReadOnlyDictionary<string, string> CurrentVersions(
        params OperationArtifactReference[] evidence)
        => evidence.ToDictionary(KnowledgeProposal.EvidenceKey, value => value.Version, StringComparer.Ordinal);

    private sealed class StubGenerator : IKnowledgeProposalGenerator
    {
        private readonly KnowledgeProposalDraft _draft;
        public StubGenerator(KnowledgeProposalDraft draft) => _draft = draft;

        public ValueTask<KnowledgeProposalDraft> GenerateAsync(
            TeacherRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_draft);
    }

    private sealed class StubVerifier : ILearningVerifier
    {
        private readonly Func<KnowledgeProposal, CancellationToken,
            ValueTask<(VerificationStatus, IReadOnlyList<VerificationFinding>)>> _run;

        public StubVerifier(
            VerifierIdentity identity,
            Func<KnowledgeProposal, CancellationToken,
                ValueTask<(VerificationStatus, IReadOnlyList<VerificationFinding>)>> run)
        {
            Identity = identity;
            _run = run;
        }

        public VerifierIdentity Identity { get; }

        public ValueTask<(VerificationStatus Status, IReadOnlyList<VerificationFinding> Findings)> VerifyAsync(
            KnowledgeProposal proposal,
            CancellationToken cancellationToken = default)
            => _run(proposal, cancellationToken);
    }
}
