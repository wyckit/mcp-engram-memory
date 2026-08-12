using System.Security.Cryptography;
using System.Text.Json;
using McpEngramMemory.Core.Models.Knowledge;

namespace McpEngramMemory.Core.Services.Knowledge;

/// <summary>Canonical content addressing for immutable knowledge versions and aggregates.</summary>
public static class KnowledgeCanonicalizer
{
    public static KnowledgeVersion PublishVersion(KnowledgeVersionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new KnowledgeVersion(definition, ComputeHash(definition));
    }

    public static string ComputeHash(KnowledgeVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return ComputeHash(version.Definition);
    }

    public static KnowledgeAsset PublishAsset(
        IEnumerable<KnowledgeVersion> versions,
        string activeVersionHash)
    {
        ArgumentNullException.ThrowIfNull(versions);
        var ordered = versions
            .OrderBy(version => version.Reference.Version, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0)
            throw new ArgumentException("A knowledge asset requires at least one version.", nameof(versions));
        if (ordered.Any(version => !string.Equals(
                version.ContentHash, ComputeHash(version), StringComparison.Ordinal)))
            throw new ArgumentException("Every supplied knowledge version must have a valid canonical hash.", nameof(versions));
        if (ordered.Select(version => version.Reference.Version).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Knowledge version labels must be unique within an asset.", nameof(versions));

        var first = ordered[0].Reference;
        if (ordered.Any(version =>
                version.Reference.TenantId != first.TenantId ||
                version.Reference.Namespace != first.Namespace ||
                version.Reference.ArtifactId != first.ArtifactId ||
                version.Reference.Kind != ArtifactKind.Knowledge))
            throw new ArgumentException("Every version must belong to the same knowledge asset.", nameof(versions));

        if (string.IsNullOrWhiteSpace(activeVersionHash))
            throw new ArgumentException("The active version hash must not be empty.", nameof(activeVersionHash));
        var active = activeVersionHash.Trim().ToLowerInvariant();
        if (!ordered.Any(version => version.ContentHash == active))
            throw new ArgumentException("The active version hash must identify a supplied version.", nameof(activeVersionHash));

        string hash = ComputeAssetHash(first, ordered, active);
        return new KnowledgeAsset(first.TenantId, first.Namespace, first.ArtifactId, ordered, active, hash);
    }

    public static string ComputeHash(KnowledgeAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var reference = new ArtifactRef(
            asset.TenantId, asset.Namespace, ArtifactKind.Knowledge, asset.ArtifactId, "aggregate");
        return ComputeAssetHash(reference, asset.Versions, asset.ActiveVersionHash);
    }

    private static string ComputeHash(KnowledgeVersionDefinition definition)
        => Hash(writer => WriteDefinition(writer, definition));

    private static string ComputeAssetHash(
        ArtifactRef identity,
        IEnumerable<KnowledgeVersion> versions,
        string activeVersionHash)
        => Hash(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("tenantId", identity.TenantId);
            writer.WriteString("namespace", identity.Namespace);
            writer.WriteString("artifactId", identity.ArtifactId);
            writer.WriteString("activeVersionHash", activeVersionHash);
            writer.WriteStartArray("versions");
            foreach (var version in versions.OrderBy(value => value.Reference.Version, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("version", version.Reference.Version);
                writer.WriteString("contentHash", version.ContentHash);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static string Hash(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            write(writer);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteDefinition(Utf8JsonWriter writer, KnowledgeVersionDefinition definition)
    {
        writer.WriteStartObject();
        WriteArtifact(writer, "reference", definition.Reference);
        writer.WriteString("claim", definition.Claim);
        writer.WriteString("maturity", definition.Maturity.ToString());
        writer.WriteString("status", definition.Status.ToString());
        writer.WriteString("constitutionVersionHash", definition.ConstitutionVersionHash);
        writer.WriteString("derivationBranchId", definition.DerivationBranchId);
        WriteTemporal(writer, definition.Temporal);
        WriteEpistemic(writer, definition.Epistemic);
        WriteEvidence(writer, "supportingEvidence", definition.SupportingEvidence);
        WriteEvidence(writer, "contradictingEvidence", definition.ContradictingEvidence);
        WritePermissions(writer, definition.Permissions);
        writer.WriteEndObject();
    }

    private static void WriteArtifact(Utf8JsonWriter writer, string propertyName, ArtifactRef artifact)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteString("tenantId", artifact.TenantId);
        writer.WriteString("namespace", artifact.Namespace);
        writer.WriteString("kind", artifact.Kind.ToString());
        writer.WriteString("artifactId", artifact.ArtifactId);
        writer.WriteString("version", artifact.Version);
        writer.WriteEndObject();
    }

    private static void WriteTemporal(Utf8JsonWriter writer, BitemporalValidity temporal)
    {
        writer.WriteStartObject("temporal");
        WriteDate(writer, "createdAt", temporal.CreatedAt);
        WriteDate(writer, "recordedAt", temporal.RecordedAt);
        WriteDate(writer, "validFrom", temporal.ValidFrom);
        WriteNullableDate(writer, "validUntil", temporal.ValidUntil);
        WriteNullableDate(writer, "verifiedAt", temporal.VerifiedAt);
        WriteNullableDate(writer, "supersededAt", temporal.SupersededAt);
        writer.WriteEndObject();
    }

    private static void WriteEpistemic(Utf8JsonWriter writer, EpistemicProfile epistemic)
    {
        writer.WriteStartObject("epistemic");
        WriteComponent(writer, "confidence", epistemic.Confidence);
        WriteComponent(writer, "authority", epistemic.Authority);
        WriteComponent(writer, "trust", epistemic.Trust);
        WriteComponent(writer, "evidenceStrength", epistemic.EvidenceStrength);
        WriteComponent(writer, "freshness", epistemic.Freshness);
        WriteComponent(writer, "consensus", epistemic.Consensus);
        writer.WriteEndObject();
    }

    private static void WriteComponent(Utf8JsonWriter writer, string name, CalibratedComponent component)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("value", component.Value);
        writer.WriteString("basis", component.Basis);
        writer.WriteString("calibrationVersion", component.CalibrationVersion);
        WriteDate(writer, "evaluatedAt", component.EvaluatedAt);
        writer.WriteEndObject();
    }

    private static void WriteEvidence(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<EvidenceReference> evidence)
    {
        writer.WriteStartArray(propertyName);
        foreach (var item in evidence
                     .OrderBy(value => value.Artifact.ToString(), StringComparer.Ordinal)
                     .ThenBy(value => value.ContentHash, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            WriteArtifact(writer, "artifact", item.Artifact);
            writer.WriteString("contentHash", item.ContentHash);
            WriteDate(writer, "observedAt", item.ObservedAt);
            writer.WriteString("independentSourceKey", item.IndependentSourceKey);
            WritePermissions(writer, item.Permissions);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WritePermissions(Utf8JsonWriter writer, PermissionEnvelope permissions)
    {
        writer.WriteStartArray("permissions");
        foreach (var grant in permissions.Grants.OrderBy(value => value.Capability))
        {
            writer.WriteStartObject();
            writer.WriteString("capability", grant.Capability.ToString());
            writer.WriteStartArray("subjects");
            foreach (var subject in grant.Subjects.OrderBy(value => value, StringComparer.Ordinal))
                writer.WriteStringValue(subject);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteDate(Utf8JsonWriter writer, string propertyName, DateTimeOffset value)
        => writer.WriteString(propertyName, value.ToUniversalTime().ToString("O"));

    private static void WriteNullableDate(Utf8JsonWriter writer, string propertyName, DateTimeOffset? value)
    {
        if (value is null)
            writer.WriteNull(propertyName);
        else
            WriteDate(writer, propertyName, value.Value);
    }
}
