using System.Security.Cryptography;
using System.Text.Json;
using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Models.Provenance;

namespace McpEngramMemory.Core.Services.Provenance;

public static class ProvenanceCanonicalizer
{
    public static ProvenanceAssertion Publish(
        string assertionId,
        ArtifactRef target,
        IEnumerable<ArtifactRef> sources,
        ProvenanceRelation relation,
        string actorId,
        string runtimeId,
        string runtimeVersion,
        IEnumerable<ArtifactRef>? verifiers,
        string constitutionVersionHash,
        string auditEventId,
        PermissionEnvelope effectivePermissions,
        DateTimeOffset recordedAt)
    {
        var orderedSources = sources.OrderBy(value => value.ToString(), StringComparer.Ordinal).ToArray();
        var orderedVerifiers = (verifiers ?? []).OrderBy(value => value.ToString(), StringComparer.Ordinal).ToArray();
        var hash = ComputeHash(assertionId, target, orderedSources, relation, actorId, runtimeId,
            runtimeVersion, orderedVerifiers, constitutionVersionHash, auditEventId,
            effectivePermissions, recordedAt);
        return new ProvenanceAssertion(assertionId, target, orderedSources, relation, actorId, runtimeId,
            runtimeVersion, orderedVerifiers, constitutionVersionHash, auditEventId,
            effectivePermissions, recordedAt, hash);
    }

    public static string ComputeHash(ProvenanceAssertion assertion)
        => ComputeHash(assertion.AssertionId, assertion.Target, assertion.Sources, assertion.Relation,
            assertion.ActorId, assertion.RuntimeId, assertion.RuntimeVersion, assertion.Verifiers,
            assertion.ConstitutionVersionHash, assertion.AuditEventId, assertion.EffectivePermissions,
            assertion.RecordedAt);

    private static string ComputeHash(
        string assertionId,
        ArtifactRef target,
        IEnumerable<ArtifactRef> sources,
        ProvenanceRelation relation,
        string actorId,
        string runtimeId,
        string runtimeVersion,
        IEnumerable<ArtifactRef> verifiers,
        string constitutionVersionHash,
        string auditEventId,
        PermissionEnvelope permissions,
        DateTimeOffset recordedAt)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("assertionId", assertionId.Trim());
            WriteRef(writer, "target", target);
            writer.WriteStartArray("sources");
            foreach (var source in sources.OrderBy(value => value.ToString(), StringComparer.Ordinal))
                WriteRefValue(writer, source);
            writer.WriteEndArray();
            writer.WriteString("relation", relation.ToString());
            writer.WriteString("actorId", actorId.Trim());
            writer.WriteString("runtimeId", runtimeId.Trim());
            writer.WriteString("runtimeVersion", runtimeVersion.Trim());
            writer.WriteStartArray("verifiers");
            foreach (var verifier in verifiers.OrderBy(value => value.ToString(), StringComparer.Ordinal))
                WriteRefValue(writer, verifier);
            writer.WriteEndArray();
            writer.WriteString("constitutionVersionHash", constitutionVersionHash.Trim().ToLowerInvariant());
            writer.WriteString("auditEventId", auditEventId.Trim());
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
            writer.WriteString("recordedAt", recordedAt.ToUniversalTime().ToString("O"));
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteRef(Utf8JsonWriter writer, string name, ArtifactRef reference)
    {
        writer.WriteStartObject(name);
        WriteRefProperties(writer, reference);
        writer.WriteEndObject();
    }

    private static void WriteRefValue(Utf8JsonWriter writer, ArtifactRef reference)
    {
        writer.WriteStartObject();
        WriteRefProperties(writer, reference);
        writer.WriteEndObject();
    }

    private static void WriteRefProperties(Utf8JsonWriter writer, ArtifactRef reference)
    {
        writer.WriteString("tenantId", reference.TenantId);
        writer.WriteString("namespace", reference.Namespace);
        writer.WriteString("kind", reference.Kind.ToString());
        writer.WriteString("artifactId", reference.ArtifactId);
        writer.WriteString("version", reference.Version);
    }
}
