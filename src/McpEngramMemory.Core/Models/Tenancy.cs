namespace McpEngramMemory.Core.Models;

/// <summary>
/// Shared tenant-id normalization used by every tenant-scoped model (entries, graph edges,
/// clusters, pending/committed collapses, decay configs). The legacy single-tenant partition is
/// the empty string, so every pre-tenant value and every no-tenant caller resolves to <c>""</c>.
/// </summary>
public static class Tenancy
{
    /// <summary>Maximum tenant-id length, matching the storage column width.</summary>
    public const int MaxTenantIdLength = 64;

    /// <summary>
    /// Normalizes a tenant identifier: null/whitespace collapses to the legacy empty-string tenant,
    /// otherwise the value is trimmed. Throws when it exceeds <see cref="MaxTenantIdLength"/> so a
    /// tenant key can never silently truncate.
    /// </summary>
    public static string Normalize(string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return string.Empty;

        var trimmed = tenantId.Trim();
        if (trimmed.Length > MaxTenantIdLength)
            throw new ArgumentException(
                $"TenantId must be at most {MaxTenantIdLength} characters.", nameof(tenantId));
        return trimmed;
    }
}
