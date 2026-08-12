using McpEngramMemory.Core.Models.Constitution;

namespace McpEngramMemory.Core.Services.Constitution;

/// <summary>
/// Rechecks the exact Constitution and resource versions captured by precondition evaluation.
/// Call inside, or immediately before, the storage compare-and-commit transaction.
/// </summary>
public sealed class ConstitutionCommitGuard
{
    public CommitRecheckResult Recheck(
        CommitAuthorizationSnapshot authorized,
        string currentConstitutionVersionHash,
        IReadOnlyDictionary<string, string> currentResourceVersions)
    {
        ArgumentNullException.ThrowIfNull(authorized);
        ArgumentNullException.ThrowIfNull(currentResourceVersions);

        var changed = new List<string>();
        if (!string.Equals(
                authorized.ConstitutionVersionHash,
                currentConstitutionVersionHash?.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            changed.Add("$constitution");
        }

        foreach (var (resource, expectedVersion) in authorized.ResourceVersions)
        {
            if (!currentResourceVersions.TryGetValue(resource, out var actualVersion) ||
                !string.Equals(expectedVersion, actualVersion, StringComparison.Ordinal))
            {
                changed.Add(resource);
            }
        }

        if (changed.Count == 0)
            return new CommitRecheckResult(true, "versions-current", Array.Empty<string>());

        changed.Sort(StringComparer.Ordinal);
        return new CommitRecheckResult(false, "versions-changed", changed.ToArray());
    }
}
