using McpEngramMemory.Core.Models.Assets;
using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Services.Assets;

namespace McpEngramMemory.Tests;

public class SemanticAssetFamilyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AssetFamilies_AreStronglyTypedAndCannotMasqueradeAsEachOther()
    {
        var skill = AssetPublisher.Publish(SkillDefinition());
        var documentation = AssetPublisher.Publish(DocumentationDefinition());
        var codeGraph = AssetPublisher.Publish(CodeGraphDefinition());
        var curriculum = AssetPublisher.Publish(CurriculumDefinition(Objectives()));

        Assert.IsType<SkillVersion>(skill);
        Assert.IsType<DocumentationVersion>(documentation);
        Assert.IsType<CodeGraphVersion>(codeGraph);
        Assert.IsType<CurriculumVersion>(curriculum);
        Assert.Equal(ArtifactKind.Skill, skill.Reference.Kind);
        Assert.Equal(ArtifactKind.Document, documentation.Reference.Kind);
        Assert.Equal(ArtifactKind.Code, codeGraph.Reference.Kind);
        Assert.Equal(ArtifactKind.Curriculum, curriculum.Reference.Kind);
        Assert.NotEqual(skill.GetType(), documentation.GetType());
    }

    [Fact]
    public void CanonicalHashes_AreStableAcrossUnorderedInputs()
    {
        var skillOne = AssetPublisher.Publish(SkillDefinition(reverse: false));
        var skillTwo = AssetPublisher.Publish(SkillDefinition(reverse: true));
        Assert.Equal(skillOne.ContentHash, skillTwo.ContentHash);

        var docsOne = AssetPublisher.Publish(DocumentationDefinition(reverse: false));
        var docsTwo = AssetPublisher.Publish(DocumentationDefinition(reverse: true));
        Assert.Equal(docsOne.ContentHash, docsTwo.ContentHash);

        var graphOne = AssetPublisher.Publish(CodeGraphDefinition(reverse: false));
        var graphTwo = AssetPublisher.Publish(CodeGraphDefinition(reverse: true));
        Assert.Equal(graphOne.ContentHash, graphTwo.ContentHash);

        var objectives = Objectives();
        var curriculumOne = AssetPublisher.Publish(CurriculumDefinition(objectives));
        var curriculumTwo = AssetPublisher.Publish(CurriculumDefinition(objectives.Reverse()));
        Assert.Equal(curriculumOne.ContentHash, curriculumTwo.ContentHash);
    }

    [Fact]
    public void CodeGraph_RejectsReferencesToMissingTypedNodes()
    {
        var definition = CodeGraphDefinition(references:
        [
            new CodeGraphReference(
                "method",
                "missing-file",
                CodeReferenceKind.Defines,
                Ref(ArtifactKind.Code, "origin", "commit-1"))
        ]);

        var error = Assert.Throws<InvalidOperationException>(() => AssetPublisher.Publish(definition));
        Assert.Contains("missing endpoint", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Curriculum_RejectsMissingPrerequisitesAndCycles()
    {
        var missing = Objective("a", ["absent"]);
        var missingError = Assert.Throws<InvalidOperationException>(() =>
            AssetPublisher.Publish(CurriculumDefinition([missing])));
        Assert.Contains("missing prerequisite", missingError.Message, StringComparison.OrdinalIgnoreCase);

        var cycle = new[] { Objective("a", ["b"]), Objective("b", ["a"]) };
        var cycleError = Assert.Throws<InvalidOperationException>(() =>
            AssetPublisher.Publish(CurriculumDefinition(cycle)));
        Assert.Contains("cycle", cycleError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Curriculum_TopologicalOrder_IsDeterministicWithLexicalTieBreaking()
    {
        var shuffled = Objectives().OrderByDescending(value => value.ObjectiveId).ToArray();

        var first = AssetPublisher.Publish(CurriculumDefinition(shuffled));
        var second = AssetPublisher.Publish(CurriculumDefinition(shuffled.Reverse()));

        Assert.Equal(["a", "b", "c", "d"], first.OrderedObjectives.Select(value => value.ObjectiveId));
        Assert.Equal(
            first.OrderedObjectives.Select(value => value.ObjectiveId),
            second.OrderedObjectives.Select(value => value.ObjectiveId));
        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    [Fact]
    public void Publication_RejectsPermissionBroadeningFromEvidence()
    {
        var evidencePermissions = Permissions("alice");
        var broadened = Permissions("alice", "bob");
        var definition = DocumentationDefinition(
            permissions: broadened,
            provenance: [Evidence(ArtifactKind.Document, "source-revision", evidencePermissions)]);

        var error = Assert.Throws<InvalidOperationException>(() => AssetPublisher.Publish(definition));
        Assert.Contains("cannot broaden", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Curriculum_RejectsRawMemoryAndRequiresTrainingPermission()
    {
        var rawMemory = Objective("a", [], ArtifactKind.Memory);
        Assert.Throws<InvalidOperationException>(() =>
            AssetPublisher.Publish(CurriculumDefinition([rawMemory])));

        var source = Ref(ArtifactKind.Knowledge, "source-a", "1");
        var readOnlyEvidence = new EvidenceReference(
            source,
            Hash('e'),
            Now,
            "source-a",
            new PermissionEnvelope([new CapabilityGrant(ArtifactCapability.Read, ["alice"])]));
        var noTraining = new LearningObjective(
            "a",
            "Objective a",
            source,
            [],
            readOnlyEvidence,
            [Criterion("a")]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            AssetPublisher.Publish(CurriculumDefinition([noTraining])));
        Assert.Contains("Train permission", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SkillVersionDefinition SkillDefinition(bool reverse = false)
    {
        var parameters = new[]
        {
            new SkillParameter("path", "string", true, "Input path"),
            new SkillParameter("mode", "string", false, "Execution mode")
        };
        var prerequisites = new[]
        {
            Ref(ArtifactKind.Knowledge, "prereq-b", "1"),
            Ref(ArtifactKind.Skill, "prereq-a", "2")
        };
        return new SkillVersionDefinition(
            Ref(ArtifactKind.Skill, "skill", "1.0"),
            "Safe compiler",
            "Compile a governed artifact",
            "A deterministic executable contract",
            reverse ? parameters.Reverse() : parameters,
            reverse ? prerequisites.Reverse() : prerequisites,
            ["Input exists"],
            reverse
                ? [new SkillStep(2, "verify", "Verify output", "Valid output"), new SkillStep(1, "compile", "Compile input", "Artifact")]
                : [new SkillStep(1, "compile", "Compile input", "Artifact"), new SkillStep(2, "verify", "Verify output", "Valid output")],
            ["Provenance retained"],
            ["Compilation failure"],
            "Discard the candidate output.",
            [Ref(ArtifactKind.Code, "compiler", "commit-1")],
            [Ref(ArtifactKind.Verification, "skill-verifier", "1")],
            [Evidence(ArtifactKind.Evidence, "skill-evidence", Permissions("alice"))],
            AssetLifecycleState.Published,
            AssetVersionStatus.Active,
            Temporal(),
            Permissions("alice"));
    }

    private static DocumentationVersionDefinition DocumentationDefinition(
        bool reverse = false,
        PermissionEnvelope? permissions = null,
        IEnumerable<EvidenceReference>? provenance = null)
    {
        var fragments = new[]
        {
            new DocumentationFragment("b", "Details", "Detailed content"),
            new DocumentationFragment("a", "Overview", "Overview content")
        };
        return new DocumentationVersionDefinition(
            Ref(ArtifactKind.Document, "guide", "rev-7"),
            "Governed guide",
            new DocumentationSource("https://example.test/guide", "rev-7", Hash('d'), "maintainer"),
            reverse ? fragments.Reverse() : fragments,
            provenance ?? [Evidence(ArtifactKind.Document, "source-revision", Permissions("alice"))],
            AssetLifecycleState.Published,
            AssetVersionStatus.Active,
            Temporal(),
            permissions ?? Permissions("alice"));
    }

    private static CodeGraphVersionDefinition CodeGraphDefinition(
        bool reverse = false,
        IEnumerable<CodeGraphReference>? references = null)
    {
        var nodes = new[]
        {
            new CodeGraphNode("module", CodeNodeKind.Module, "Core"),
            new CodeGraphNode("file", CodeNodeKind.File, "Worker.cs", "src/Worker.cs"),
            new CodeGraphNode("method", CodeNodeKind.Symbol, "Run", "src/Worker.cs", CodeSymbolKind.Method)
        };
        var defaultReferences = new[]
        {
            new CodeGraphReference("module", "file", CodeReferenceKind.Contains, Ref(ArtifactKind.Code, "origin", "commit-1")),
            new CodeGraphReference("file", "method", CodeReferenceKind.Defines, Ref(ArtifactKind.Code, "origin", "commit-1"))
        };
        return new CodeGraphVersionDefinition(
            Ref(ArtifactKind.Code, "code-graph", "commit-1"),
            "repo",
            "commit-1",
            "csharp",
            reverse ? nodes.Reverse() : nodes,
            references ?? (reverse ? defaultReferences.Reverse() : defaultReferences),
            [Evidence(ArtifactKind.Code, "repository-snapshot", Permissions("alice"))],
            AssetLifecycleState.Published,
            AssetVersionStatus.Active,
            Temporal(),
            Permissions("alice"));
    }

    private static CurriculumVersionDefinition CurriculumDefinition(IEnumerable<LearningObjective> objectives)
        => new(
            Ref(ArtifactKind.Curriculum, "curriculum", "1.0"),
            "Architecture curriculum",
            objectives,
            "compiler-1",
            AssetLifecycleState.Published,
            AssetVersionStatus.Active,
            Temporal(),
            Permissions("alice"));

    private static IReadOnlyList<LearningObjective> Objectives()
        =>
        [
            Objective("d", ["c", "b"]),
            Objective("c", ["a"]),
            Objective("b", ["a"]),
            Objective("a", [])
        ];

    private static LearningObjective Objective(
        string id,
        IEnumerable<string> prerequisites,
        ArtifactKind sourceKind = ArtifactKind.Knowledge)
    {
        var source = Ref(sourceKind, $"source-{id}", "1");
        return new LearningObjective(
            id,
            $"Objective {id}",
            source,
            prerequisites,
            new EvidenceReference(source, Hash(id[0]), Now, $"source-{id}", Permissions("alice")),
            [Criterion(id)]);
    }

    private static PromotionCriterion Criterion(string id)
        => new($"criterion-{id}", Ref(ArtifactKind.Verification, $"verifier-{id}", "1"), 0.8m);

    private static EvidenceReference Evidence(
        ArtifactKind kind,
        string id,
        PermissionEnvelope permissions)
        => new(Ref(kind, id, "1"), Hash('a'), Now, $"source-{id}", permissions);

    private static ArtifactRef Ref(ArtifactKind kind, string id, string version)
        => new("tenant", "project", kind, id, version);

    private static PermissionEnvelope Permissions(params string[] subjects)
        => new(
        [
            new CapabilityGrant(ArtifactCapability.Read, subjects),
            new CapabilityGrant(ArtifactCapability.Train, subjects)
        ]);

    private static BitemporalValidity Temporal()
        => new(Now, Now, Now.AddDays(-1), Now.AddYears(1), Now, null);

    private static string Hash(char value) => new(value, 64);
}
