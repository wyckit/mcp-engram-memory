using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Services.Knowledge;

namespace McpEngramMemory.Core.Services.Governance.Persistence;

/// <summary>
/// Focused atomic store for one governed knowledge aggregate. Versions and the active pointer
/// share a single checksum-protected snapshot so a crash cannot publish half a pointer update.
/// </summary>
public sealed class FileKnowledgeAssetStore
{
    private const string StoreName = "knowledge-assets";
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileKnowledgeAssetStore(string root)
        => _root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));

    public async ValueTask SaveAsync(
        KnowledgeAsset asset,
        CancellationToken cancellationToken = default)
    {
        Validate(asset);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await CrashSafeJsonPersistence.WriteSnapshotAsync(
                PathFor(asset.TenantId, asset.Namespace, asset.ArtifactId),
                StoreName,
                asset.TenantId,
                KnowledgeAssetDto.From(asset),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<PersistenceLoadResult<KnowledgeAsset>> LoadAsync(
        string tenantId,
        string @namespace,
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        var persisted = await CrashSafeJsonPersistence.ReadSnapshotAsync<KnowledgeAssetDto>(
            PathFor(tenantId, @namespace, artifactId),
            StoreName,
            tenantId ?? string.Empty,
            cancellationToken);
        var result = new PersistenceLoadResult<KnowledgeAsset>(
            persisted.Value?.ToDomain(), persisted.Diagnostics);
        if (result.Value is { } asset)
        {
            if (asset.Namespace != @namespace || asset.ArtifactId != artifactId)
                throw new InvalidDataException("Knowledge snapshot identity does not match its partition.");
            Validate(asset);
        }
        return result;
    }

    private static void Validate(KnowledgeAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Versions.Any(version => !string.Equals(
                KnowledgeCanonicalizer.ComputeHash(version), version.ContentHash, StringComparison.Ordinal)))
            throw new InvalidDataException("A knowledge version has an invalid immutable hash.");
        if (!asset.Versions.Any(version => version.ContentHash == asset.ActiveVersionHash))
            throw new InvalidDataException("The active knowledge pointer does not reference a stored version.");
        if (!string.Equals(KnowledgeCanonicalizer.ComputeHash(asset), asset.ContentHash, StringComparison.Ordinal))
            throw new InvalidDataException("The knowledge aggregate hash is invalid.");
    }

    private string PathFor(string? tenantId, string @namespace, string artifactId)
        => Path.Combine(
            CrashSafeJsonPersistence.TenantDirectory(_root, tenantId ?? string.Empty),
            "knowledge",
            CrashSafeJsonPersistence.ArtifactFileName(@namespace, artifactId));
}
