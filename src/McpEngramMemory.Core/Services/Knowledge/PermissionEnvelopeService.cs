using McpEngramMemory.Core.Models.Knowledge;

namespace McpEngramMemory.Core.Services.Knowledge;

/// <summary>Deterministic permission-lattice operations used by derivation and promotion.</summary>
public static class PermissionEnvelopeService
{
    /// <summary>
    /// Computes the capability-by-capability intersection of every supporting source. An empty
    /// intersection is valid and remains represented as an envelope with no effective grants.
    /// </summary>
    public static PermissionEnvelope Intersect(IEnumerable<PermissionEnvelope> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var sourceList = sources.ToArray();
        if (sourceList.Length == 0)
            throw new ArgumentException("At least one source envelope is required.", nameof(sources));

        var grants = new List<CapabilityGrant>();
        foreach (var capability in Enum.GetValues<ArtifactCapability>())
        {
            var intersection = new HashSet<string>(
                sourceList[0].SubjectsFor(capability), StringComparer.Ordinal);
            foreach (var source in sourceList.Skip(1))
                intersection.IntersectWith(source.SubjectsFor(capability));
            if (intersection.Count > 0)
                grants.Add(new CapabilityGrant(capability, intersection));
        }

        return new PermissionEnvelope(grants);
    }

    /// <summary>Returns true only when candidate grants no capability/subject absent in baseline.</summary>
    public static bool IsNarrowerThanOrEqual(PermissionEnvelope candidate, PermissionEnvelope baseline)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(baseline);
        foreach (var capability in Enum.GetValues<ArtifactCapability>())
        {
            var allowed = new HashSet<string>(baseline.SubjectsFor(capability), StringComparer.Ordinal);
            if (candidate.SubjectsFor(capability).Any(subject => !allowed.Contains(subject)))
                return false;
        }

        return true;
    }
}
