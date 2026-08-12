using McpEngramMemory.Core.Models.Assets;
using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Services.Assets;

namespace McpEngramMemory.Tests;

public sealed class AssetRuntimeTests
{
    private static readonly string HashA = new('a', 64);
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task SkillExecutionRunsDeterministicVerifierBeforeHostSandbox()
    {
        var skill = Skill();
        var sandbox = new FakeSandbox();
        var coordinator = new SkillExecutionCoordinator(sandbox);
        var verifier = new FakeVerifier(skill.Definition.DeterministicVerifiers[0], false);

        var result = await coordinator.ExecuteAsync(skill, "agent",
            new Dictionary<string, string> { ["input"] = "value" },
            skill.Definition.Resources.ToDictionary(value => value, _ => true),
            new[] { verifier }, new SkillExecutionBudget(10, DateTimeOffset.MaxValue), HashA);

        Assert.Equal(SkillExecutionStatus.Denied, result.Status);
        Assert.Equal("deterministic-verifier-veto", result.Code);
        Assert.Equal(0, sandbox.CallCount);
    }

    [Fact]
    public async Task SkillExecutionDelegatesOnlyPinnedAuthorizedContractToSandbox()
    {
        var skill = Skill();
        var sandbox = new FakeSandbox();
        var coordinator = new SkillExecutionCoordinator(sandbox);
        var verifier = new FakeVerifier(skill.Definition.DeterministicVerifiers[0], true);

        var result = await coordinator.ExecuteAsync(skill, "agent",
            new Dictionary<string, string> { ["input"] = "value" },
            skill.Definition.Resources.ToDictionary(value => value, _ => true),
            new[] { verifier }, new SkillExecutionBudget(10, DateTimeOffset.MaxValue), HashA);

        Assert.Equal(SkillExecutionStatus.Succeeded, result.Status);
        Assert.Equal(1, sandbox.CallCount);
        Assert.Equal(skill.ContentHash, sandbox.LastRequest!.SkillContentHash);
        Assert.Equal(skill.Definition.Resources, sandbox.LastRequest.AuthorizedResources);
    }

    [Fact]
    public void CurriculumCompilerRequiresVerifiedActiveTrainPermittedSources()
    {
        var definition = CurriculumDefinition();
        var source = definition.Objectives[0].Source;
        var evidence = definition.Objectives[0].Evidence;
        var supported = new CurriculumSourceAttestation(source, evidence.ContentHash,
            KnowledgeMaturity.Supported, null, AssetVersionStatus.Active, evidence.Permissions,
            new[] { Ref(ArtifactKind.Verification, "verify", "v1") });
        Assert.Throws<InvalidOperationException>(() => CurriculumCompiler.Compile(definition, "agent",
            new Dictionary<ArtifactRef, CurriculumSourceAttestation> { [source] = supported }));

        var verified = supported with { KnowledgeMaturity = KnowledgeMaturity.Verified };
        var curriculum = CurriculumCompiler.Compile(definition, "agent",
            new Dictionary<ArtifactRef, CurriculumSourceAttestation> { [source] = verified });
        Assert.Equal(ArtifactKind.Curriculum, curriculum.Reference.Kind);
    }

    [Fact]
    public void CodeGraphIndexerSelectsRoslynFirstForCSharpAndTreeSitterForOtherLanguages()
    {
        var roslyn = new FakeExtractor("roslyn", CodeExtractorFamily.Roslyn, new[] { "csharp", "typescript" });
        var tree = new FakeExtractor("tree-sitter", CodeExtractorFamily.TreeSitter, new[] { "csharp", "typescript" });
        var indexer = new IncrementalCodeGraphIndexer(new ICodeGraphExtractor[] { tree, roslyn });

        Assert.Same(roslyn, indexer.SelectExtractor("csharp"));
        Assert.Same(tree, indexer.SelectExtractor("typescript"));
    }

    private static SkillVersion Skill()
    {
        var permissions = Permissions();
        var evidenceRef = Ref(ArtifactKind.Evidence, "skill-source", "v1");
        var definition = new SkillVersionDefinition(
            Ref(ArtifactKind.Skill, "skill", "v1"), "Skill", "Test", "Test skill",
            new[] { new SkillParameter("input", "string", true, "Input") },
            Array.Empty<ArtifactRef>(), new[] { "authorized" },
            new[] { new SkillStep(1, "step", "Do work", "Done") },
            new[] { "preserve audit" }, new[] { "sandbox failure" }, "Rollback",
            new[] { Ref(ArtifactKind.Document, "resource", "v1") },
            new[] { Ref(ArtifactKind.Verification, "verify", "v1") },
            new[] { new EvidenceReference(evidenceRef, HashA, Now, "source", permissions) },
            AssetLifecycleState.Published, AssetVersionStatus.Active, Temporal(), permissions);
        return AssetPublisher.Publish(definition);
    }

    private static CurriculumVersionDefinition CurriculumDefinition()
    {
        var permissions = Permissions();
        var source = Ref(ArtifactKind.Knowledge, "knowledge", "v1");
        var evidence = new EvidenceReference(source, HashA, Now, "knowledge-source", permissions);
        var objective = new LearningObjective("objective", "Objective", source, Array.Empty<string>(), evidence,
            new[] { new PromotionCriterion("criterion", Ref(ArtifactKind.Verification, "verify", "v1"), .8m) });
        return new CurriculumVersionDefinition(Ref(ArtifactKind.Curriculum, "curriculum", "v1"), "Curriculum",
            new[] { objective }, "compiler-v1", AssetLifecycleState.Published, AssetVersionStatus.Active,
            Temporal(), permissions);
    }

    private static PermissionEnvelope Permissions()
        => new(new[]
        {
            new CapabilityGrant(ArtifactCapability.Use, new[] { "agent" }),
            new CapabilityGrant(ArtifactCapability.Train, new[] { "agent" }),
            new CapabilityGrant(ArtifactCapability.Read, new[] { "agent" })
        });

    private static BitemporalValidity Temporal() => new(Now, Now, Now);

    private static ArtifactRef Ref(ArtifactKind kind, string id, string version)
        => new("tenant", "assets", kind, id, version);

    private sealed class FakeSandbox : ISkillSandbox
    {
        public string IsolationProfile => "test-process";
        public int CallCount { get; private set; }
        public SkillSandboxRequest? LastRequest { get; private set; }
        public ValueTask<SkillSandboxResult> ExecuteAsync(SkillSandboxRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return ValueTask.FromResult(new SkillSandboxResult(true, HashA, new[] { "audit" }));
        }
    }

    private sealed class FakeVerifier(ArtifactRef reference, bool result) : ISkillDeterministicVerifier
    {
        public ArtifactRef Reference { get; } = reference;
        public ValueTask<bool> VerifyAsync(SkillVersion skill, IReadOnlyDictionary<string, string> parameters,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(result);
    }

    private sealed class FakeExtractor(
        string id,
        CodeExtractorFamily family,
        IEnumerable<string> languages) : ICodeGraphExtractor
    {
        public string ExtractorId { get; } = id;
        public CodeExtractorFamily Family { get; } = family;
        public IReadOnlySet<string> Languages { get; } = languages.ToHashSet(StringComparer.OrdinalIgnoreCase);
        public ValueTask<CodeGraphVersionDefinition> ExtractAsync(IncrementalCodeGraphRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
