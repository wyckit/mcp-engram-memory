using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Services.Constitution;

namespace McpEngramMemory.Tests;

public sealed class ConstitutionCoreTests
{
    private static readonly DateTimeOffset PublishedAt =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CanonicalHash_IsStableAcrossInputOrdering()
    {
        var ruleA = RuleDefinition("rule-a", priority: 20, CognitiveOperationKind.Retrieve);
        var ruleB = RuleDefinition("rule-b", priority: 10, CognitiveOperationKind.WriteMemory);

        var first = RootDefinition(
            principles: new[] { "Memory is not truth", "Never destroy provenance" },
            rules: new[] { ruleA, ruleB });
        var second = RootDefinition(
            principles: new[] { "Never destroy provenance", "Memory is not truth" },
            rules: new[] { ruleB, ruleA });

        var firstVersion = ConstitutionCanonicalizer.Publish(first, "1.0.0", PublishedAt);
        var secondVersion = ConstitutionCanonicalizer.Publish(second, "1.0.0", PublishedAt);

        Assert.Equal(firstVersion.ContentHash, secondVersion.ContentHash);
        Assert.Equal(64, firstVersion.ContentHash.Length);
        Assert.All(firstVersion.ContentHash, character =>
            Assert.True(char.IsAsciiHexDigit(character) && !char.IsUpper(character)));
    }

    [Fact]
    public void Compose_RejectsOverlayThatWeakensRootInvariant()
    {
        var root = ConstitutionCanonicalizer.Publish(RootDefinition(), "1.0.0", PublishedAt);
        var weakened = new ConstitutionConstraints(
            preserveProvenance: false,
            requireEvidenceForKnowledge: true,
            preserveContradictions: true,
            requireDeterministicVerificationFirst: true,
            requireExplainability: true,
            requireAudit: true,
            minimumEvidenceCount: 1);
        var overlayDefinition = new ConstitutionDefinition(
            "org-overlay",
            "Organization overlay",
            ConstitutionLayerKind.Overlay,
            weakened,
            new[] { "Organization policy" },
            Array.Empty<ConstitutionRuleDefinition>(),
            root.ContentHash);
        var overlay = ConstitutionCanonicalizer.Publish(overlayDefinition, "1.0.0", PublishedAt);

        var error = Assert.Throws<ConstitutionCompositionException>(
            () => ConstitutionComposer.Compose(root, new[] { overlay }));

        Assert.Contains("weakens", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compose_RejectsOverlayRuleReplacement()
    {
        var rootRule = RuleDefinition("immutable-root-rule", 10, CognitiveOperationKind.WriteMemory);
        var root = ConstitutionCanonicalizer.Publish(
            RootDefinition(rules: new[] { rootRule }), "1.0.0", PublishedAt);
        var replacement = RuleDefinition("immutable-root-rule", 99, CognitiveOperationKind.WriteMemory);
        var overlayDefinition = new ConstitutionDefinition(
            "org-overlay",
            "Organization overlay",
            ConstitutionLayerKind.Overlay,
            ConstitutionConstraints.RootDefaults,
            new[] { "Tighter organization policy" },
            new[] { replacement },
            root.ContentHash);
        var overlay = ConstitutionCanonicalizer.Publish(overlayDefinition, "1.0.0", PublishedAt);

        Assert.Throws<ConstitutionCompositionException>(
            () => ConstitutionComposer.Compose(root, new[] { overlay }));
    }

    [Fact]
    public async Task Evaluator_UsesDeterministicPriorityThenRuleIdOrdering()
    {
        var definitions = new[]
        {
            RuleDefinition("z-last", 20, CognitiveOperationKind.Retrieve),
            RuleDefinition("b-second", 10, CognitiveOperationKind.Retrieve),
            RuleDefinition("a-first", 10, CognitiveOperationKind.Retrieve)
        };
        var version = ConstitutionCanonicalizer.Publish(
            RootDefinition(rules: definitions), "1.0.0", PublishedAt);
        var bundle = ConstitutionComposer.Compose(version);
        var calls = new List<string>();
        var evaluator = new DeterministicConstitutionEvaluator(new IConstitutionRule[]
        {
            new RecordingRule("z-last", 20, calls, ConstitutionOutcome.RequireApproval),
            new RecordingRule("b-second", 10, calls, ConstitutionOutcome.Quarantine),
            new RecordingRule("a-first", 10, calls, ConstitutionOutcome.Allow)
        });

        var decision = await evaluator.EvaluateAsync(
            Operation(CognitiveOperationKind.Retrieve),
            bundle,
            ConstitutionPhase.Precondition);

        Assert.Equal(new[] { "a-first", "b-second", "z-last" }, calls);
        Assert.Equal(new[] { "a-first", "b-second", "z-last" },
            decision.Findings.Select(finding => finding.RuleId));
        Assert.Equal(ConstitutionOutcome.Quarantine, decision.Outcome);
        Assert.Equal(new[] { version.ContentHash }, decision.ConstitutionVersionHashes);
    }

    [Fact]
    public async Task Evaluator_FailsClosedWhenPublishedRuleImplementationIsMissing()
    {
        var version = ConstitutionCanonicalizer.Publish(
            RootDefinition(rules: new[]
            {
                RuleDefinition("required-rule", 1, CognitiveOperationKind.PromoteKnowledge)
            }),
            "1.0.0",
            PublishedAt);
        var evaluator = new DeterministicConstitutionEvaluator(Array.Empty<IConstitutionRule>());

        var decision = await evaluator.EvaluateAsync(
            Operation(CognitiveOperationKind.PromoteKnowledge),
            ConstitutionComposer.Compose(version),
            ConstitutionPhase.Precondition);

        Assert.Equal(ConstitutionOutcome.Deny, decision.Outcome);
        Assert.Equal("rule-implementation-missing", Assert.Single(decision.Findings).Code);
    }

    [Fact]
    public async Task AuditStore_AppendsImmutableMonotoneSequence()
    {
        var store = new InMemoryConstitutionAuditStore();
        var first = await store.AppendAsync(AuditRecord("event-1", "op-1"));
        var second = await store.AppendAsync(AuditRecord("event-2", "op-2"));
        var snapshot = await store.ReadAllAsync();

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(new long[] { 1, 2 }, snapshot.Select(record => record.Sequence));
        Assert.Equal(new[] { "event-1", "event-2" }, snapshot.Select(record => record.EventId));
        Assert.DoesNotContain(
            typeof(IConstitutionAuditStore).GetMethods(),
            method => method.Name.Contains("Delete", StringComparison.Ordinal) ||
                      method.Name.Contains("Update", StringComparison.Ordinal));
    }

    [Fact]
    public void CommitGuard_DeniesWhenConstitutionOrResourceVersionChanged()
    {
        var snapshot = new CommitAuthorizationSnapshot(
            "aaaaaaaa",
            new Dictionary<string, string>
            {
                ["acl:work"] = "7",
                ["artifact:claim-1"] = "3"
            });
        var guard = new ConstitutionCommitGuard();

        var unchanged = guard.Recheck(snapshot, "AAAAAAAA", new Dictionary<string, string>
        {
            ["acl:work"] = "7",
            ["artifact:claim-1"] = "3"
        });
        var changed = guard.Recheck(snapshot, "bbbbbbbb", new Dictionary<string, string>
        {
            ["acl:work"] = "8",
            ["artifact:claim-1"] = "3"
        });

        Assert.True(unchanged.CanCommit);
        Assert.False(changed.CanCommit);
        Assert.Equal("versions-changed", changed.Code);
        Assert.Equal(new[] { "$constitution", "acl:work" }, changed.ChangedResources);
    }

    private static ConstitutionDefinition RootDefinition(
        IEnumerable<string>? principles = null,
        IEnumerable<ConstitutionRuleDefinition>? rules = null)
        => new(
            "engram-root",
            "Engram Root Constitution",
            ConstitutionLayerKind.Root,
            ConstitutionConstraints.RootDefaults,
            principles ?? new[] { "Never destroy provenance" },
            rules ?? Array.Empty<ConstitutionRuleDefinition>());

    private static ConstitutionRuleDefinition RuleDefinition(
        string id,
        int priority,
        params CognitiveOperationKind[] appliesTo)
        => new(id, "1.0.0", $"test:{id}", $"Rule {id}", priority, appliesTo);

    private static OperationEnvelope Operation(CognitiveOperationKind kind)
        => new(
            "operation-1",
            kind,
            "tenant-a",
            "agent-a",
            "test",
            Array.Empty<OperationArtifactReference>(),
            target: null,
            payloadHash: "abcd",
            requestedAt: PublishedAt);

    private static ConstitutionAuditRecord AuditRecord(string eventId, string operationId)
        => new(
            sequence: 999,
            eventId,
            operationId,
            "tenant-a",
            "agent-a",
            ConstitutionPhase.Precondition,
            ConstitutionOutcome.Allow,
            new[] { "constitution-hash" },
            Array.Empty<string>(),
            PublishedAt);

    private sealed class RecordingRule : IConstitutionRule
    {
        private readonly IList<string> _calls;
        private readonly ConstitutionOutcome _outcome;

        public RecordingRule(
            string ruleId,
            int priority,
            IList<string> calls,
            ConstitutionOutcome outcome)
        {
            RuleId = ruleId;
            Priority = priority;
            _calls = calls;
            _outcome = outcome;
        }

        public string RuleId { get; }
        public int Priority { get; }
        public IReadOnlySet<CognitiveOperationKind> AppliesTo { get; } =
            new HashSet<CognitiveOperationKind> { CognitiveOperationKind.Retrieve };

        public ValueTask<IReadOnlyList<ConstitutionFinding>> EvaluateAsync(
            ConstitutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            _calls.Add(RuleId);
            IReadOnlyList<ConstitutionFinding> findings = new[]
            {
                new ConstitutionFinding(RuleId, $"finding-{RuleId}", _outcome, RuleId)
            };
            return ValueTask.FromResult(findings);
        }
    }
}
