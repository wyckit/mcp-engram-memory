using System.Collections.ObjectModel;
using McpEngramMemory.Core.Models.Knowledge;

namespace McpEngramMemory.Core.Models.Assets;

public enum AssetLifecycleState
{
    Draft,
    Published,
    Retired
}

public enum AssetVersionStatus
{
    Active,
    Disputed,
    Superseded,
    Withdrawn
}

public sealed record SkillParameter(string Name, string Type, bool Required, string Description);

public sealed record SkillStep(
    int Order,
    string StepId,
    string Instruction,
    string ExpectedOutcome);

/// <summary>Immutable executable contract; execution is deliberately left to a host sandbox.</summary>
public sealed class SkillVersionDefinition
{
    public ArtifactRef Reference { get; }
    public string Name { get; }
    public string Purpose { get; }
    public string Description { get; }
    public IReadOnlyList<SkillParameter> Parameters { get; }
    public IReadOnlyList<ArtifactRef> Prerequisites { get; }
    public IReadOnlyList<string> Preconditions { get; }
    public IReadOnlyList<SkillStep> Steps { get; }
    public IReadOnlyList<string> Invariants { get; }
    public IReadOnlyList<string> FailureConditions { get; }
    public string RollbackGuidance { get; }
    public IReadOnlyList<ArtifactRef> Resources { get; }
    public IReadOnlyList<ArtifactRef> DeterministicVerifiers { get; }
    public IReadOnlyList<EvidenceReference> Evidence { get; }
    public AssetLifecycleState Lifecycle { get; }
    public AssetVersionStatus Status { get; }
    public BitemporalValidity Temporal { get; }
    public PermissionEnvelope Permissions { get; }

    public SkillVersionDefinition(
        ArtifactRef reference,
        string name,
        string purpose,
        string description,
        IEnumerable<SkillParameter> parameters,
        IEnumerable<ArtifactRef> prerequisites,
        IEnumerable<string> preconditions,
        IEnumerable<SkillStep> steps,
        IEnumerable<string> invariants,
        IEnumerable<string> failureConditions,
        string rollbackGuidance,
        IEnumerable<ArtifactRef> resources,
        IEnumerable<ArtifactRef> deterministicVerifiers,
        IEnumerable<EvidenceReference> evidence,
        AssetLifecycleState lifecycle,
        AssetVersionStatus status,
        BitemporalValidity temporal,
        PermissionEnvelope permissions)
    {
        Reference = RequireKind(reference, ArtifactKind.Skill, nameof(reference));
        Name = Required(name, nameof(name));
        Purpose = Required(purpose, nameof(purpose));
        Description = Required(description, nameof(description));
        Parameters = ReadOnly(parameters.OrderBy(value => value.Name, StringComparer.Ordinal));
        Prerequisites = ReadOnly(prerequisites.OrderBy(value => value.ToString(), StringComparer.Ordinal));
        Preconditions = Strings(preconditions);
        Steps = ReadOnly(steps.OrderBy(value => value.Order));
        Invariants = Strings(invariants);
        FailureConditions = Strings(failureConditions);
        RollbackGuidance = Required(rollbackGuidance, nameof(rollbackGuidance));
        Resources = ReadOnly(resources.OrderBy(value => value.ToString(), StringComparer.Ordinal));
        DeterministicVerifiers = ReadOnly(deterministicVerifiers.OrderBy(value => value.ToString(), StringComparer.Ordinal));
        Evidence = EvidenceList(evidence);
        Lifecycle = lifecycle;
        Status = status;
        Temporal = temporal ?? throw new ArgumentNullException(nameof(temporal));
        Permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
    }

    internal static ArtifactRef RequireKind(ArtifactRef reference, ArtifactKind kind, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.Kind != kind
            ? throw new ArgumentException($"Reference must use ArtifactKind.{kind}.", parameterName)
            : reference;
    }

    internal static string Required(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty.", parameterName)
            : value.Trim();

    internal static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new ReadOnlyCollection<T>(values.ToArray());
    }

    internal static IReadOnlyList<string> Strings(IEnumerable<string> values)
        => ReadOnly(values.Select(value => Required(value, nameof(values))).Distinct(StringComparer.Ordinal));

    internal static IReadOnlyList<EvidenceReference> EvidenceList(IEnumerable<EvidenceReference> evidence)
        => ReadOnly(evidence.OrderBy(value => value.Artifact.ToString(), StringComparer.Ordinal)
            .ThenBy(value => value.ContentHash, StringComparer.Ordinal));
}

public sealed record SkillVersion(SkillVersionDefinition Definition, string ContentHash)
{
    public ArtifactRef Reference => Definition.Reference;
}

public sealed record DocumentationSource(
    string SourceUri,
    string SourceRevision,
    string SourceHash,
    string Authority);

public sealed class DocumentationFragment
{
    public string FragmentId { get; }
    public string Heading { get; }
    public string Text { get; }
    public IReadOnlyList<ArtifactRef> Citations { get; }

    public DocumentationFragment(
        string fragmentId,
        string heading,
        string text,
        IEnumerable<ArtifactRef>? citations = null)
    {
        FragmentId = SkillVersionDefinition.Required(fragmentId, nameof(fragmentId));
        Heading = SkillVersionDefinition.Required(heading, nameof(heading));
        Text = SkillVersionDefinition.Required(text, nameof(text));
        Citations = SkillVersionDefinition.ReadOnly((citations ?? Array.Empty<ArtifactRef>())
            .OrderBy(value => value.ToString(), StringComparer.Ordinal));
    }
}

/// <summary>One immutable source revision with explicit validity and provenance.</summary>
public sealed class DocumentationVersionDefinition
{
    public ArtifactRef Reference { get; }
    public string Title { get; }
    public DocumentationSource Source { get; }
    public IReadOnlyList<DocumentationFragment> Fragments { get; }
    public IReadOnlyList<EvidenceReference> Provenance { get; }
    public AssetLifecycleState Lifecycle { get; }
    public AssetVersionStatus Status { get; }
    public BitemporalValidity Temporal { get; }
    public PermissionEnvelope Permissions { get; }

    public DocumentationVersionDefinition(
        ArtifactRef reference,
        string title,
        DocumentationSource source,
        IEnumerable<DocumentationFragment> fragments,
        IEnumerable<EvidenceReference> provenance,
        AssetLifecycleState lifecycle,
        AssetVersionStatus status,
        BitemporalValidity temporal,
        PermissionEnvelope permissions)
    {
        Reference = SkillVersionDefinition.RequireKind(reference, ArtifactKind.Document, nameof(reference));
        Title = SkillVersionDefinition.Required(title, nameof(title));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Fragments = SkillVersionDefinition.ReadOnly(fragments.OrderBy(value => value.FragmentId, StringComparer.Ordinal));
        Provenance = SkillVersionDefinition.EvidenceList(provenance);
        Lifecycle = lifecycle;
        Status = status;
        Temporal = temporal ?? throw new ArgumentNullException(nameof(temporal));
        Permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
    }
}

public sealed record DocumentationVersion(DocumentationVersionDefinition Definition, string ContentHash)
{
    public ArtifactRef Reference => Definition.Reference;
}

public enum CodeNodeKind
{
    Module,
    File,
    Symbol
}

public enum CodeSymbolKind
{
    Namespace,
    Type,
    Method,
    Property,
    Field,
    Event,
    Test
}

public sealed record CodeGraphNode(
    string NodeId,
    CodeNodeKind Kind,
    string DisplayName,
    string? FilePath = null,
    CodeSymbolKind? SymbolKind = null);

public enum CodeReferenceKind
{
    Contains,
    Defines,
    Calls,
    Uses,
    Inherits,
    Implements,
    References,
    Tests
}

public sealed record CodeGraphReference(
    string SourceNodeId,
    string TargetNodeId,
    CodeReferenceKind Kind,
    ArtifactRef Origin);

/// <summary>Structural code graph, intentionally separate from cognitive and provenance graphs.</summary>
public sealed class CodeGraphVersionDefinition
{
    public ArtifactRef Reference { get; }
    public string Repository { get; }
    public string Commit { get; }
    public string Language { get; }
    public IReadOnlyList<CodeGraphNode> Nodes { get; }
    public IReadOnlyList<CodeGraphReference> References { get; }
    public IReadOnlyList<EvidenceReference> Provenance { get; }
    public AssetLifecycleState Lifecycle { get; }
    public AssetVersionStatus Status { get; }
    public BitemporalValidity Temporal { get; }
    public PermissionEnvelope Permissions { get; }

    public CodeGraphVersionDefinition(
        ArtifactRef reference,
        string repository,
        string commit,
        string language,
        IEnumerable<CodeGraphNode> nodes,
        IEnumerable<CodeGraphReference> references,
        IEnumerable<EvidenceReference> provenance,
        AssetLifecycleState lifecycle,
        AssetVersionStatus status,
        BitemporalValidity temporal,
        PermissionEnvelope permissions)
    {
        Reference = SkillVersionDefinition.RequireKind(reference, ArtifactKind.Code, nameof(reference));
        Repository = SkillVersionDefinition.Required(repository, nameof(repository));
        Commit = SkillVersionDefinition.Required(commit, nameof(commit));
        Language = SkillVersionDefinition.Required(language, nameof(language));
        Nodes = SkillVersionDefinition.ReadOnly(nodes.OrderBy(value => value.NodeId, StringComparer.Ordinal));
        References = SkillVersionDefinition.ReadOnly(references
            .OrderBy(value => value.SourceNodeId, StringComparer.Ordinal)
            .ThenBy(value => value.TargetNodeId, StringComparer.Ordinal)
            .ThenBy(value => value.Kind));
        Provenance = SkillVersionDefinition.EvidenceList(provenance);
        Lifecycle = lifecycle;
        Status = status;
        Temporal = temporal ?? throw new ArgumentNullException(nameof(temporal));
        Permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
    }
}

public sealed record CodeGraphVersion(CodeGraphVersionDefinition Definition, string ContentHash)
{
    public ArtifactRef Reference => Definition.Reference;
}

public sealed record PromotionCriterion(
    string CriterionId,
    ArtifactRef DeterministicVerifier,
    decimal RequiredScore);

public sealed class LearningObjective
{
    public string ObjectiveId { get; }
    public string Title { get; }
    public ArtifactRef Source { get; }
    public IReadOnlyList<string> PrerequisiteObjectiveIds { get; }
    public EvidenceReference Evidence { get; }
    public IReadOnlyList<PromotionCriterion> PromotionCriteria { get; }

    public LearningObjective(
        string objectiveId,
        string title,
        ArtifactRef source,
        IEnumerable<string> prerequisiteObjectiveIds,
        EvidenceReference evidence,
        IEnumerable<PromotionCriterion> promotionCriteria)
    {
        ObjectiveId = SkillVersionDefinition.Required(objectiveId, nameof(objectiveId));
        Title = SkillVersionDefinition.Required(title, nameof(title));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        PrerequisiteObjectiveIds = SkillVersionDefinition.Strings(prerequisiteObjectiveIds)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        PromotionCriteria = SkillVersionDefinition.ReadOnly(promotionCriteria
            .OrderBy(value => value.CriterionId, StringComparer.Ordinal));
    }
}

/// <summary>Governed, dependency-ordered learning export; raw memory is not an allowed source.</summary>
public sealed class CurriculumVersionDefinition
{
    public ArtifactRef Reference { get; }
    public string Name { get; }
    public IReadOnlyList<LearningObjective> Objectives { get; }
    public string CompilerVersion { get; }
    public AssetLifecycleState Lifecycle { get; }
    public AssetVersionStatus Status { get; }
    public BitemporalValidity Temporal { get; }
    public PermissionEnvelope Permissions { get; }

    public CurriculumVersionDefinition(
        ArtifactRef reference,
        string name,
        IEnumerable<LearningObjective> objectives,
        string compilerVersion,
        AssetLifecycleState lifecycle,
        AssetVersionStatus status,
        BitemporalValidity temporal,
        PermissionEnvelope permissions)
    {
        Reference = SkillVersionDefinition.RequireKind(reference, ArtifactKind.Curriculum, nameof(reference));
        Name = SkillVersionDefinition.Required(name, nameof(name));
        Objectives = SkillVersionDefinition.ReadOnly(objectives);
        CompilerVersion = SkillVersionDefinition.Required(compilerVersion, nameof(compilerVersion));
        Lifecycle = lifecycle;
        Status = status;
        Temporal = temporal ?? throw new ArgumentNullException(nameof(temporal));
        Permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
    }
}

public sealed record CurriculumVersion(
    CurriculumVersionDefinition Definition,
    IReadOnlyList<LearningObjective> OrderedObjectives,
    string ContentHash)
{
    public ArtifactRef Reference => Definition.Reference;
}
