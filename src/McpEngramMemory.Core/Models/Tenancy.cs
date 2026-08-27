namespace McpEngramMemory.Core.Models;

/// <summary>
/// Shared tenant-id normalization used by every tenant-scoped model (entries, graph edges,
/// clusters, pending/committed collapses, decay configs). The legacy single-tenant partition is
/// the empty string, so every pre-tenant value and every no-tenant caller resolves to <c>""</c>.
///
/// This type also owns the partition-key alphabet. Partition keys are composed by concatenating
/// a tenant id and a namespace around <see cref="PartitionSeparator"/>, so the composition is
/// injective only while neither component can contain the separator. That is an invariant of the
/// key format, not of any one caller, which is why the separator and its validator live together
/// here rather than beside a single composition site.
/// </summary>
public static class Tenancy
{
    /// <summary>Maximum tenant-id length, matching the storage column width.</summary>
    public const int MaxTenantIdLength = 64;

    /// <summary>
    /// Separator used to compose a tenant-scoped partition key string for the BM25 and HNSW
    /// sub-indexes (which are keyed by string). ASCII Unit Separator. For the legacy tenant the
    /// composed key is just the namespace, so a namespace containing this character would compose
    /// to the same key as some other tenant's namespace — see
    /// <see cref="ValidatePartitionComponent"/>, which is what makes that unreachable.
    /// </summary>
    public const char PartitionSeparator = (char)0x1F;

    /// <summary>
    /// Normalizes a tenant identifier: null/whitespace collapses to the legacy empty-string tenant,
    /// otherwise the value is trimmed. Trimming is deliberately case-preserving — tenant ids are
    /// case-sensitive, so folding here would silently merge two distinct tenants. Throws when the
    /// value exceeds <see cref="MaxTenantIdLength"/> so a tenant key can never silently truncate,
    /// and when it contains a partition-key control character so it can never forge another
    /// tenant's partition.
    /// </summary>
    public static string Normalize(string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return string.Empty;

        var trimmed = tenantId.Trim();
        if (trimmed.Length > MaxTenantIdLength)
            throw new ArgumentException(
                $"TenantId must be at most {MaxTenantIdLength} characters.", nameof(tenantId));
        ValidatePartitionComponent(trimmed, nameof(tenantId));
        return trimmed;
    }

    /// <summary>
    /// Rejects any value that cannot safely become one half of a composed partition key.
    /// The whole control-character class is refused rather than just
    /// <see cref="PartitionSeparator"/>: rejecting exactly one character would make the next
    /// change of separator silently reopen the hole, and nothing below U+0020 has ever survived a
    /// storage round-trip anyway (PersistenceManager.GetNamespacePath mangles them through
    /// <c>Path.GetInvalidFileNameChars</c>), so no existing data depends on them.
    /// Null/empty is accepted — the legacy tenant is the empty string, and an empty namespace is a
    /// separate concern from key forgery.
    /// </summary>
    public static void ValidatePartitionComponent(string value, string paramName)
    {
        if (string.IsNullOrEmpty(value))
            return;

        for (int i = 0; i < value.Length; i++)
        {
            if (!char.IsControl(value[i]))
                continue;

            // The offending value is deliberately not echoed: it is attacker-controlled and would
            // put raw control characters into the log stream that reads this exception.
            throw new ArgumentException(
                $"'{paramName}' must not contain control characters " +
                $"(found U+{(int)value[i]:X4} at index {i}); they are reserved for partition keys.",
                paramName);
        }
    }
}
