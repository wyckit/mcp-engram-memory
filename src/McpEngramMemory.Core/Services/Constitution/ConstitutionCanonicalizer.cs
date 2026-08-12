using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpEngramMemory.Core.Models.Constitution;

namespace McpEngramMemory.Core.Services.Constitution;

/// <summary>Publishes immutable versions using a stable, property-ordered SHA-256 representation.</summary>
public static class ConstitutionCanonicalizer
{
    public static ConstitutionVersion Publish(
        ConstitutionDefinition definition,
        string version,
        DateTimeOffset publishedAt,
        string? supersedesVersionHash = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Version must not be empty.", nameof(version));

        string normalizedSupersedes = string.IsNullOrWhiteSpace(supersedesVersionHash)
            ? string.Empty
            : supersedesVersionHash.Trim().ToLowerInvariant();
        string hash = ComputeHash(definition, version.Trim(), publishedAt, normalizedSupersedes);
        return new ConstitutionVersion(
            definition,
            version.Trim(),
            publishedAt,
            normalizedSupersedes.Length == 0 ? null : normalizedSupersedes,
            hash);
    }

    public static string ComputeHash(ConstitutionVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return ComputeHash(
            version.Definition,
            version.Version,
            version.PublishedAt,
            version.SupersedesVersionHash ?? string.Empty);
    }

    private static string ComputeHash(
        ConstitutionDefinition definition,
        string version,
        DateTimeOffset publishedAt,
        string supersedesVersionHash)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("constitutionId", definition.ConstitutionId);
            writer.WriteString("name", definition.Name);
            writer.WriteString("layerKind", definition.LayerKind.ToString());
            WriteNullableString(writer, "parentVersionHash", definition.ParentVersionHash);
            writer.WriteString("version", version);
            writer.WriteString("publishedAt", publishedAt.ToUniversalTime().ToString("O"));
            WriteNullableString(writer, "supersedesVersionHash",
                supersedesVersionHash.Length == 0 ? null : supersedesVersionHash);

            writer.WritePropertyName("constraints");
            WriteConstraints(writer, definition.Constraints);

            writer.WriteStartArray("principles");
            foreach (var principle in definition.Principles.OrderBy(value => value, StringComparer.Ordinal))
                writer.WriteStringValue(principle);
            writer.WriteEndArray();

            writer.WriteStartArray("rules");
            foreach (var rule in definition.Rules
                         .OrderBy(value => value.Priority)
                         .ThenBy(value => value.RuleId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("ruleId", rule.RuleId);
                writer.WriteString("ruleVersion", rule.RuleVersion);
                writer.WriteString("implementationId", rule.ImplementationId);
                writer.WriteString("description", rule.Description);
                writer.WriteNumber("priority", rule.Priority);
                writer.WriteStartArray("appliesTo");
                foreach (var operation in rule.AppliesTo.OrderBy(value => value))
                    writer.WriteStringValue(operation.ToString());
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteConstraints(Utf8JsonWriter writer, ConstitutionConstraints constraints)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("preserveProvenance", constraints.PreserveProvenance);
        writer.WriteBoolean("requireEvidenceForKnowledge", constraints.RequireEvidenceForKnowledge);
        writer.WriteBoolean("preserveContradictions", constraints.PreserveContradictions);
        writer.WriteBoolean("requireDeterministicVerificationFirst", constraints.RequireDeterministicVerificationFirst);
        writer.WriteBoolean("requireExplainability", constraints.RequireExplainability);
        writer.WriteBoolean("requireAudit", constraints.RequireAudit);
        writer.WriteNumber("minimumEvidenceCount", constraints.MinimumEvidenceCount);
        writer.WriteStartArray("allowedOperations");
        foreach (var operation in constraints.AllowedOperations.OrderBy(value => value))
            writer.WriteStringValue(operation.ToString());
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
            writer.WriteNull(propertyName);
        else
            writer.WriteString(propertyName, value);
    }
}
