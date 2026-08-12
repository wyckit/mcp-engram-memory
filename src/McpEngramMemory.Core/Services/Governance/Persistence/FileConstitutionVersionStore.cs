using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Services.Constitution;

namespace McpEngramMemory.Core.Services.Governance.Persistence;

public sealed record PersistedConstitutionSet(
    string TenantId,
    IReadOnlyList<ConstitutionVersion> Versions,
    string ActiveVersionHash);

/// <summary>Atomic, tenant-partitioned snapshots of immutable Constitution versions.</summary>
public sealed class FileConstitutionVersionStore
{
    private const string StoreName = "constitution-versions";
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileConstitutionVersionStore(string root)
        => _root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));

    public async ValueTask SaveAsync(
        string tenantId,
        IEnumerable<ConstitutionVersion> versions,
        string activeVersionHash,
        CancellationToken cancellationToken = default)
    {
        var ordered = versions?.OrderBy(value => value.PublishedAt).ThenBy(value => value.Version, StringComparer.Ordinal).ToArray()
                      ?? throw new ArgumentNullException(nameof(versions));
        if (ordered.Length == 0)
            throw new ArgumentException("At least one Constitution version is required.", nameof(versions));
        if (ordered.Any(value => !string.Equals(
                ConstitutionCanonicalizer.ComputeHash(value), value.ContentHash, StringComparison.Ordinal)))
            throw new InvalidDataException("A Constitution version has an invalid immutable hash.");
        string active = RequiredHash(activeVersionHash);
        if (!ordered.Any(value => value.ContentHash == active))
            throw new InvalidDataException("The active Constitution pointer does not reference a stored version.");

        var payload = new PersistedConstitutionSet(tenantId ?? string.Empty, ordered, active);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await CrashSafeJsonPersistence.WriteSnapshotAsync(
                PathFor(tenantId), StoreName, tenantId ?? string.Empty,
                PersistedConstitutionSetDto.From(payload), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<PersistenceLoadResult<PersistedConstitutionSet>> LoadAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var persisted = await CrashSafeJsonPersistence.ReadSnapshotAsync<PersistedConstitutionSetDto>(
            PathFor(tenantId), StoreName, tenantId ?? string.Empty, cancellationToken);
        var result = new PersistenceLoadResult<PersistedConstitutionSet>(
            persisted.Value?.ToDomain(), persisted.Diagnostics);
        if (result.Value is { } value)
        {
            if (value.Versions.Any(version => !string.Equals(
                    ConstitutionCanonicalizer.ComputeHash(version), version.ContentHash, StringComparison.Ordinal)))
                throw new InvalidDataException("A persisted Constitution immutable hash is invalid.");
            if (!value.Versions.Any(version => version.ContentHash == value.ActiveVersionHash))
                throw new InvalidDataException("The persisted active Constitution pointer is inconsistent.");
        }
        return result;
    }

    private string PathFor(string? tenantId)
        => Path.Combine(CrashSafeJsonPersistence.TenantDirectory(_root, tenantId ?? string.Empty), "constitution.json");

    private static string RequiredHash(string value)
    {
        string normalized = value?.Trim().ToLowerInvariant()
                            ?? throw new ArgumentNullException(nameof(value));
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Value must be a SHA-256 hash.", nameof(value));
        return normalized;
    }
}
