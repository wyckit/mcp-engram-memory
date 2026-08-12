using McpEngramMemory.Core.Models.Constitution;

namespace McpEngramMemory.Core.Services.Constitution;

/// <summary>The non-negotiable constitutional baseline shipped with Core.</summary>
public static class RootConstitution
{
    public const string AuditEnvelopeRuleId = "root.audit-envelope";

    public static readonly IReadOnlyList<string> Principles = new[]
    {
        "Never destroy provenance.",
        "Knowledge requires evidence.",
        "Memory is not truth.",
        "Contradictions are preserved until explicitly resolved.",
        "Derived knowledge cannot inherit broader permissions than its supporting evidence.",
        "Deterministic verification precedes model verification whenever possible.",
        "Every promoted knowledge object remains explainable.",
        "Every learning action is auditable."
    };

    /// <summary>A deterministic, content-addressed root whose publication identity never changes.</summary>
    public static ConstitutionVersion Version { get; } = ConstitutionCanonicalizer.Publish(
        new ConstitutionDefinition(
            "engram.root",
            "Engram Root Constitution",
            ConstitutionLayerKind.Root,
            ConstitutionConstraints.RootDefaults,
            Principles,
            new[]
            {
                new ConstitutionRuleDefinition(
                    AuditEnvelopeRuleId,
                    "1.0.0",
                    typeof(AuditEnvelopeConstitutionRule).FullName!,
                    "Every governed operation has a valid content digest and tenant-consistent references.",
                    -10_000,
                    Enum.GetValues<CognitiveOperationKind>())
            }),
        "1.0.0",
        DateTimeOffset.UnixEpoch);

    public static ConstitutionBundle Bundle { get; } = ConstitutionComposer.Compose(Version);
}
