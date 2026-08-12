using System.Collections.ObjectModel;

namespace McpEngramMemory.Core.Models.Knowledge;

/// <summary>Independent capabilities that may be granted over a governed artifact.</summary>
public enum ArtifactCapability
{
    Read,
    Search,
    Use,
    Train,
    Modify,
    Promote,
    Verify,
    Declassify,
    Administer
}

/// <summary>A canonical set of subjects granted one artifact capability.</summary>
public sealed class CapabilityGrant
{
    public ArtifactCapability Capability { get; }
    public IReadOnlyList<string> Subjects { get; }

    public CapabilityGrant(ArtifactCapability capability, IEnumerable<string> subjects)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        Capability = capability;
        Subjects = new ReadOnlyCollection<string>(subjects
            .Select(RequiredSubject)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray());
    }

    private static string RequiredSubject(string value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Permission subjects must not be empty.", nameof(value))
            : value.Trim();
}

/// <summary>
/// Capability-oriented authorization label. Omitted capabilities and empty subject sets grant
/// nobody; there is deliberately no implicit public or owner grant.
/// </summary>
public sealed class PermissionEnvelope
{
    public IReadOnlyList<CapabilityGrant> Grants { get; }

    public PermissionEnvelope(IEnumerable<CapabilityGrant>? grants = null)
    {
        var normalized = (grants ?? Array.Empty<CapabilityGrant>())
            .GroupBy(grant => grant.Capability)
            .Select(group => new CapabilityGrant(group.Key, group.SelectMany(grant => grant.Subjects)))
            .Where(grant => grant.Subjects.Count > 0)
            .OrderBy(grant => grant.Capability)
            .ToArray();
        Grants = new ReadOnlyCollection<CapabilityGrant>(normalized);
    }

    public bool Allows(ArtifactCapability capability, string subject)
        => SubjectsFor(capability).Contains(subject, StringComparer.Ordinal);

    public IReadOnlyList<string> SubjectsFor(ArtifactCapability capability)
        => Grants.FirstOrDefault(grant => grant.Capability == capability)?.Subjects
           ?? Array.Empty<string>();
}
