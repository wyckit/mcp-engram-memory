using System.Collections.ObjectModel;

namespace McpEngramMemory.Core.Models.Constitution;

/// <summary>Identifies whether a published Constitution is the invariant root or a narrowing overlay.</summary>
public enum ConstitutionLayerKind
{
    Root,
    Overlay
}

/// <summary>Operations understood by the deterministic Constitution kernel.</summary>
public enum CognitiveOperationKind
{
    ReadMemory,
    WriteMemory,
    DeleteMemory,
    Retrieve,
    CompileContext,
    ProposeKnowledge,
    VerifyKnowledge,
    PromoteKnowledge,
    Declassify,
    ExportCurriculum,
    AdministerGovernance
}

/// <summary>
/// Machine-checkable monotone constraints. Boolean invariants may move only from false to true,
/// the evidence floor may only increase, and allowed operations may only be removed by overlays.
/// </summary>
public sealed class ConstitutionConstraints
{
    public bool PreserveProvenance { get; }
    public bool RequireEvidenceForKnowledge { get; }
    public bool PreserveContradictions { get; }
    public bool RequireDeterministicVerificationFirst { get; }
    public bool RequireExplainability { get; }
    public bool RequireAudit { get; }
    public int MinimumEvidenceCount { get; }
    public IReadOnlyList<CognitiveOperationKind> AllowedOperations { get; }

    public ConstitutionConstraints(
        bool preserveProvenance,
        bool requireEvidenceForKnowledge,
        bool preserveContradictions,
        bool requireDeterministicVerificationFirst,
        bool requireExplainability,
        bool requireAudit,
        int minimumEvidenceCount,
        IEnumerable<CognitiveOperationKind>? allowedOperations = null)
    {
        if (minimumEvidenceCount < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumEvidenceCount));

        PreserveProvenance = preserveProvenance;
        RequireEvidenceForKnowledge = requireEvidenceForKnowledge;
        PreserveContradictions = preserveContradictions;
        RequireDeterministicVerificationFirst = requireDeterministicVerificationFirst;
        RequireExplainability = requireExplainability;
        RequireAudit = requireAudit;
        MinimumEvidenceCount = minimumEvidenceCount;
        AllowedOperations = ReadOnly(allowedOperations ?? Enum.GetValues<CognitiveOperationKind>());
    }

    /// <summary>The non-negotiable invariants required of every root Constitution.</summary>
    public static ConstitutionConstraints RootDefaults { get; } = new(
        preserveProvenance: true,
        requireEvidenceForKnowledge: true,
        preserveContradictions: true,
        requireDeterministicVerificationFirst: true,
        requireExplainability: true,
        requireAudit: true,
        minimumEvidenceCount: 1);

    internal static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values)
        => new ReadOnlyCollection<T>(values.Distinct().ToArray());
}

/// <summary>Stable identity and ordering metadata for one deterministic rule implementation.</summary>
public sealed record ConstitutionRuleDefinition
{
    public string RuleId { get; }
    public string RuleVersion { get; }
    public string ImplementationId { get; }
    public string Description { get; }
    public int Priority { get; }
    public IReadOnlyList<CognitiveOperationKind> AppliesTo { get; }

    public ConstitutionRuleDefinition(
        string ruleId,
        string ruleVersion,
        string implementationId,
        string description,
        int priority,
        IEnumerable<CognitiveOperationKind> appliesTo)
    {
        RuleId = Required(ruleId, nameof(ruleId));
        RuleVersion = Required(ruleVersion, nameof(ruleVersion));
        ImplementationId = Required(implementationId, nameof(implementationId));
        Description = Required(description, nameof(description));
        Priority = priority;
        AppliesTo = ConstitutionConstraints.ReadOnly(appliesTo)
            .OrderBy(value => value)
            .ToArray();
        if (AppliesTo.Count == 0)
            throw new ArgumentException("A rule must apply to at least one operation.", nameof(appliesTo));
    }

    private static string Required(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty.", parameterName)
            : value.Trim();
}

/// <summary>
/// Immutable semantic definition of one Constitution layer. Published roots have no parent;
/// overlays name the exact content hash of the parent version they narrow.
/// </summary>
public sealed class ConstitutionDefinition
{
    public string ConstitutionId { get; }
    public string Name { get; }
    public ConstitutionLayerKind LayerKind { get; }
    public string? ParentVersionHash { get; }
    public ConstitutionConstraints Constraints { get; }
    public IReadOnlyList<string> Principles { get; }
    public IReadOnlyList<ConstitutionRuleDefinition> Rules { get; }

    public ConstitutionDefinition(
        string constitutionId,
        string name,
        ConstitutionLayerKind layerKind,
        ConstitutionConstraints constraints,
        IEnumerable<string> principles,
        IEnumerable<ConstitutionRuleDefinition> rules,
        string? parentVersionHash = null)
    {
        ConstitutionId = Required(constitutionId, nameof(constitutionId));
        Name = Required(name, nameof(name));
        Constraints = constraints ?? throw new ArgumentNullException(nameof(constraints));
        LayerKind = layerKind;
        ParentVersionHash = string.IsNullOrWhiteSpace(parentVersionHash)
            ? null
            : parentVersionHash.Trim().ToLowerInvariant();

        if (layerKind == ConstitutionLayerKind.Root && ParentVersionHash is not null)
            throw new ArgumentException("A root Constitution cannot have a parent hash.", nameof(parentVersionHash));
        if (layerKind == ConstitutionLayerKind.Overlay && ParentVersionHash is null)
            throw new ArgumentException("An overlay must identify its exact parent version hash.", nameof(parentVersionHash));

        Principles = new ReadOnlyCollection<string>(principles
            .Select(value => Required(value, nameof(principles)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray());
        Rules = new ReadOnlyCollection<ConstitutionRuleDefinition>(rules
            .OrderBy(rule => rule.Priority)
            .ThenBy(rule => rule.RuleId, StringComparer.Ordinal)
            .ToArray());

        var duplicate = Rules.GroupBy(rule => rule.RuleId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Rule id '{duplicate.Key}' occurs more than once.", nameof(rules));
    }

    private static string Required(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty.", parameterName)
            : value.Trim();
}

/// <summary>An immutable published Constitution definition addressed by its canonical SHA-256 hash.</summary>
public sealed record ConstitutionVersion(
    ConstitutionDefinition Definition,
    string Version,
    DateTimeOffset PublishedAt,
    string? SupersedesVersionHash,
    string ContentHash);

/// <summary>A validated root and zero or more monotone overlays.</summary>
public sealed class ConstitutionBundle
{
    public ConstitutionVersion Root { get; }
    public IReadOnlyList<ConstitutionVersion> Overlays { get; }
    public IReadOnlyList<ConstitutionRuleDefinition> Rules { get; }
    public ConstitutionConstraints EffectiveConstraints { get; }
    public string EffectiveVersionHash => Overlays.Count == 0 ? Root.ContentHash : Overlays[^1].ContentHash;
    public IReadOnlyList<string> VersionHashes { get; }

    internal ConstitutionBundle(
        ConstitutionVersion root,
        IEnumerable<ConstitutionVersion> overlays,
        IEnumerable<ConstitutionRuleDefinition> rules,
        ConstitutionConstraints effectiveConstraints)
    {
        Root = root;
        Overlays = new ReadOnlyCollection<ConstitutionVersion>(overlays.ToArray());
        Rules = new ReadOnlyCollection<ConstitutionRuleDefinition>(rules.ToArray());
        EffectiveConstraints = effectiveConstraints;
        VersionHashes = new ReadOnlyCollection<string>(
            new[] { root.ContentHash }.Concat(Overlays.Select(value => value.ContentHash)).ToArray());
    }
}
