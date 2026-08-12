using System.Collections.ObjectModel;

namespace McpEngramMemory.Core.Models.Constitution;

/// <summary>A serialization-friendly, version-pinned artifact reference used during evaluation.</summary>
public sealed record OperationArtifactReference(
    string TenantId,
    string Namespace,
    string ArtifactKind,
    string ArtifactId,
    string Version);

/// <summary>
/// Immutable description of a cognitive action. Payloads are represented by a content hash so
/// evaluation and audit do not retain arbitrary mutable objects or sensitive request bodies.
/// </summary>
public sealed class OperationEnvelope
{
    public string OperationId { get; }
    public CognitiveOperationKind Kind { get; }
    public string TenantId { get; }
    public string PrincipalId { get; }
    public string Purpose { get; }
    public IReadOnlyList<OperationArtifactReference> Inputs { get; }
    public OperationArtifactReference? Target { get; }
    public string PayloadHash { get; }
    public DateTimeOffset RequestedAt { get; }
    public IReadOnlyDictionary<string, string> Attributes { get; }

    public OperationEnvelope(
        string operationId,
        CognitiveOperationKind kind,
        string tenantId,
        string principalId,
        string purpose,
        IEnumerable<OperationArtifactReference>? inputs,
        OperationArtifactReference? target,
        string payloadHash,
        DateTimeOffset requestedAt,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        OperationId = Required(operationId, nameof(operationId));
        Kind = kind;
        // Empty is the explicitly supported legacy-unisolated tenant partition.
        TenantId = tenantId?.Trim() ?? string.Empty;
        PrincipalId = Required(principalId, nameof(principalId));
        Purpose = Required(purpose, nameof(purpose));
        PayloadHash = Required(payloadHash, nameof(payloadHash)).ToLowerInvariant();
        RequestedAt = requestedAt;
        Inputs = new ReadOnlyCollection<OperationArtifactReference>((inputs ?? [])
            .OrderBy(value => value.TenantId, StringComparer.Ordinal)
            .ThenBy(value => value.Namespace, StringComparer.Ordinal)
            .ThenBy(value => value.ArtifactKind, StringComparer.Ordinal)
            .ThenBy(value => value.ArtifactId, StringComparer.Ordinal)
            .ThenBy(value => value.Version, StringComparer.Ordinal)
            .ToArray());
        Target = target;
        var sortedAttributes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (attributes is not null)
        {
            foreach (var (key, value) in attributes)
                sortedAttributes[key] = value;
        }
        Attributes = new ReadOnlyDictionary<string, string>(sortedAttributes);
    }

    private static string Required(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty.", parameterName)
            : value.Trim();
}
