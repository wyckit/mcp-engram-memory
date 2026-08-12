using System.Security.Cryptography;
using System.Text.Json;
using McpEngramMemory.Core.Models.Assets;
using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Services.Knowledge;

namespace McpEngramMemory.Core.Services.Assets;

/// <summary>Deterministic validation and canonical publication for semantic asset families.</summary>
public static class AssetPublisher
{
    public static SkillVersion Publish(SkillVersionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Steps.Count == 0)
            throw new InvalidOperationException("A skill requires at least one executable step.");
        if (definition.Steps.Any(value =>
                string.IsNullOrWhiteSpace(value.StepId) ||
                string.IsNullOrWhiteSpace(value.Instruction) ||
                string.IsNullOrWhiteSpace(value.ExpectedOutcome)))
            throw new InvalidOperationException("Skill steps require ids, instructions, and expected outcomes.");
        if (definition.Steps.Select(value => value.StepId).Distinct(StringComparer.Ordinal).Count() != definition.Steps.Count)
            throw new InvalidOperationException("Skill step ids must be unique.");
        if (!definition.Steps.Select(value => value.Order).SequenceEqual(Enumerable.Range(1, definition.Steps.Count)))
            throw new InvalidOperationException("Skill steps must have contiguous one-based ordering.");
        if (definition.Parameters.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != definition.Parameters.Count)
            throw new InvalidOperationException("Skill parameter names must be unique.");
        if (definition.Parameters.Any(value =>
                string.IsNullOrWhiteSpace(value.Name) ||
                string.IsNullOrWhiteSpace(value.Type) ||
                string.IsNullOrWhiteSpace(value.Description)))
            throw new InvalidOperationException("Skill parameters require names, types, and descriptions.");
        if (definition.DeterministicVerifiers.Count == 0 ||
            definition.DeterministicVerifiers.Any(value => value.Kind != ArtifactKind.Verification))
            throw new InvalidOperationException("A skill requires deterministic verifier references.");
        ValidateTenant(definition.Reference, definition.Prerequisites
            .Concat(definition.Resources)
            .Concat(definition.DeterministicVerifiers));
        ValidateEvidence(definition.Reference, definition.Evidence, definition.Permissions);

        return new SkillVersion(definition, Hash(writer => WriteSkill(writer, definition)));
    }

    public static DocumentationVersion Publish(DocumentationVersionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!IsStableHash(definition.Source.SourceHash))
            throw new InvalidOperationException("Documentation source revisions require a stable SHA-256 hash.");
        if (string.IsNullOrWhiteSpace(definition.Source.SourceUri) ||
            string.IsNullOrWhiteSpace(definition.Source.SourceRevision) ||
            string.IsNullOrWhiteSpace(definition.Source.Authority))
            throw new InvalidOperationException("Documentation source, revision, and authority are required.");
        if (definition.Fragments.Count == 0 ||
            definition.Fragments.Select(value => value.FragmentId).Distinct(StringComparer.Ordinal).Count() != definition.Fragments.Count)
            throw new InvalidOperationException("Documentation requires uniquely identified fragments.");
        ValidateTenant(definition.Reference, definition.Fragments.SelectMany(value => value.Citations));
        ValidateEvidence(definition.Reference, definition.Provenance, definition.Permissions);

        return new DocumentationVersion(definition, Hash(writer => WriteDocumentation(writer, definition)));
    }

    public static CodeGraphVersion Publish(CodeGraphVersionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Nodes.Count == 0)
            throw new InvalidOperationException("A code graph requires nodes.");
        if (definition.Nodes.Any(value => string.IsNullOrWhiteSpace(value.NodeId) || string.IsNullOrWhiteSpace(value.DisplayName)) ||
            definition.Nodes.Select(value => value.NodeId).Distinct(StringComparer.Ordinal).Count() != definition.Nodes.Count)
            throw new InvalidOperationException("Code graph node ids must be non-empty and unique.");
        if (definition.Nodes.Any(value => value.Kind == CodeNodeKind.Symbol && value.SymbolKind is null) ||
            definition.Nodes.Any(value => value.Kind != CodeNodeKind.Symbol && value.SymbolKind is not null) ||
            definition.Nodes.Any(value => value.Kind == CodeNodeKind.File && string.IsNullOrWhiteSpace(value.FilePath)))
            throw new InvalidOperationException("Code node shape does not match its module/file/symbol type.");

        var nodeIds = definition.Nodes.Select(value => value.NodeId).ToHashSet(StringComparer.Ordinal);
        var dangling = definition.References.FirstOrDefault(value =>
            !nodeIds.Contains(value.SourceNodeId) || !nodeIds.Contains(value.TargetNodeId));
        if (dangling is not null)
            throw new InvalidOperationException(
                $"Code reference '{dangling.SourceNodeId}' -> '{dangling.TargetNodeId}' has a missing endpoint.");
        if (definition.References.Any(value => value.Origin.Kind != ArtifactKind.Code))
            throw new InvalidOperationException("Code reference origins must be exact Code artifacts.");
        ValidateTenant(definition.Reference, definition.References.Select(value => value.Origin));
        ValidateEvidence(definition.Reference, definition.Provenance, definition.Permissions);

        return new CodeGraphVersion(definition, Hash(writer => WriteCodeGraph(writer, definition)));
    }

    public static CurriculumVersion Publish(CurriculumVersionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Objectives.Count == 0)
            throw new InvalidOperationException("A curriculum requires learning objectives.");
        if (definition.Objectives.Select(value => value.ObjectiveId).Distinct(StringComparer.Ordinal).Count() != definition.Objectives.Count)
            throw new InvalidOperationException("Learning objective ids must be unique.");

        foreach (var objective in definition.Objectives)
        {
            if (objective.Source.Kind is not (ArtifactKind.Knowledge or ArtifactKind.Skill))
                throw new InvalidOperationException("Curriculum sources must be governed Knowledge or Skill versions; raw memory is forbidden.");
            if (objective.Evidence.Artifact != objective.Source || !objective.Evidence.IsStable)
                throw new InvalidOperationException("Every objective must cite stable evidence for its exact source version.");
            if (objective.Evidence.Permissions.SubjectsFor(ArtifactCapability.Train).Count == 0)
                throw new InvalidOperationException("Every curriculum source requires explicit Train permission.");
            if (objective.PromotionCriteria.Count == 0 || objective.PromotionCriteria.Any(value =>
                    value.DeterministicVerifier.Kind != ArtifactKind.Verification ||
                    value.RequiredScore is < 0m or > 1m ||
                    string.IsNullOrWhiteSpace(value.CriterionId)))
                throw new InvalidOperationException("Objectives require valid deterministic promotion criteria.");
            ValidateTenant(definition.Reference,
                new[] { objective.Source, objective.Evidence.Artifact }
                    .Concat(objective.PromotionCriteria.Select(value => value.DeterministicVerifier)));
        }
        ValidatePermissions(definition.Permissions, definition.Objectives.Select(value => value.Evidence));

        var ordered = CurriculumPlanner.TopologicalOrder(definition.Objectives);
        return new CurriculumVersion(
            definition,
            ordered,
            Hash(writer => WriteCurriculum(writer, definition, ordered)));
    }

    private static void ValidateEvidence(
        ArtifactRef owner,
        IReadOnlyList<EvidenceReference> evidence,
        PermissionEnvelope permissions)
    {
        if (evidence.Count == 0 || evidence.Any(value => !value.IsStable))
            throw new InvalidOperationException("Published assets require stable provenance evidence.");
        ValidateTenant(owner, evidence.Select(value => value.Artifact));
        ValidatePermissions(permissions, evidence);
    }

    private static void ValidatePermissions(
        PermissionEnvelope permissions,
        IEnumerable<EvidenceReference> evidence)
    {
        if (evidence.Any(value =>
                !PermissionEnvelopeService.IsNarrowerThanOrEqual(permissions, value.Permissions)))
            throw new InvalidOperationException("Derived asset permissions cannot broaden supporting evidence permissions.");
    }

    private static void ValidateTenant(ArtifactRef owner, IEnumerable<ArtifactRef> references)
    {
        if (references.Any(value => !string.Equals(value.TenantId, owner.TenantId, StringComparison.Ordinal)))
            throw new InvalidOperationException("Cross-tenant asset references are not permitted.");
    }

    private static bool IsStableHash(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string Hash(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            write(writer);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteSkill(Utf8JsonWriter writer, SkillVersionDefinition value)
    {
        Begin(writer, "skill", value.Reference, value.Lifecycle, value.Status, value.Temporal, value.Permissions);
        writer.WriteString("name", value.Name);
        writer.WriteString("purpose", value.Purpose);
        writer.WriteString("description", value.Description);
        writer.WriteStartArray("parameters");
        foreach (var parameter in value.Parameters)
        {
            writer.WriteStartObject();
            writer.WriteString("name", parameter.Name);
            writer.WriteString("type", parameter.Type);
            writer.WriteBoolean("required", parameter.Required);
            writer.WriteString("description", parameter.Description);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        WriteArtifacts(writer, "prerequisites", value.Prerequisites);
        WriteStringArray(writer, "preconditions", value.Preconditions);
        writer.WriteStartArray("steps");
        foreach (var step in value.Steps)
        {
            writer.WriteStartObject();
            writer.WriteNumber("order", step.Order);
            writer.WriteString("stepId", step.StepId);
            writer.WriteString("instruction", step.Instruction);
            writer.WriteString("expectedOutcome", step.ExpectedOutcome);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        WriteStringArray(writer, "invariants", value.Invariants);
        WriteStringArray(writer, "failureConditions", value.FailureConditions);
        writer.WriteString("rollbackGuidance", value.RollbackGuidance);
        WriteArtifacts(writer, "resources", value.Resources);
        WriteArtifacts(writer, "deterministicVerifiers", value.DeterministicVerifiers);
        WriteEvidence(writer, value.Evidence);
        writer.WriteEndObject();
    }

    private static void WriteDocumentation(Utf8JsonWriter writer, DocumentationVersionDefinition value)
    {
        Begin(writer, "documentation", value.Reference, value.Lifecycle, value.Status, value.Temporal, value.Permissions);
        writer.WriteString("title", value.Title);
        writer.WriteString("sourceUri", value.Source.SourceUri);
        writer.WriteString("sourceRevision", value.Source.SourceRevision);
        writer.WriteString("sourceHash", value.Source.SourceHash.ToLowerInvariant());
        writer.WriteString("authority", value.Source.Authority);
        writer.WriteStartArray("fragments");
        foreach (var fragment in value.Fragments)
        {
            writer.WriteStartObject();
            writer.WriteString("fragmentId", fragment.FragmentId);
            writer.WriteString("heading", fragment.Heading);
            writer.WriteString("text", fragment.Text);
            WriteArtifacts(writer, "citations", fragment.Citations);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        WriteEvidence(writer, value.Provenance);
        writer.WriteEndObject();
    }

    private static void WriteCodeGraph(Utf8JsonWriter writer, CodeGraphVersionDefinition value)
    {
        Begin(writer, "codeGraph", value.Reference, value.Lifecycle, value.Status, value.Temporal, value.Permissions);
        writer.WriteString("repository", value.Repository);
        writer.WriteString("commit", value.Commit);
        writer.WriteString("language", value.Language);
        writer.WriteStartArray("nodes");
        foreach (var node in value.Nodes)
        {
            writer.WriteStartObject();
            writer.WriteString("nodeId", node.NodeId);
            writer.WriteString("kind", node.Kind.ToString());
            writer.WriteString("displayName", node.DisplayName);
            if (node.FilePath is null) writer.WriteNull("filePath"); else writer.WriteString("filePath", node.FilePath);
            if (node.SymbolKind is null) writer.WriteNull("symbolKind"); else writer.WriteString("symbolKind", node.SymbolKind.ToString());
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("references");
        foreach (var reference in value.References)
        {
            writer.WriteStartObject();
            writer.WriteString("source", reference.SourceNodeId);
            writer.WriteString("target", reference.TargetNodeId);
            writer.WriteString("kind", reference.Kind.ToString());
            WriteArtifact(writer, "origin", reference.Origin);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        WriteEvidence(writer, value.Provenance);
        writer.WriteEndObject();
    }

    private static void WriteCurriculum(
        Utf8JsonWriter writer,
        CurriculumVersionDefinition value,
        IEnumerable<LearningObjective> ordered)
    {
        Begin(writer, "curriculum", value.Reference, value.Lifecycle, value.Status, value.Temporal, value.Permissions);
        writer.WriteString("name", value.Name);
        writer.WriteString("compilerVersion", value.CompilerVersion);
        writer.WriteStartArray("objectives");
        foreach (var objective in ordered)
        {
            writer.WriteStartObject();
            writer.WriteString("objectiveId", objective.ObjectiveId);
            writer.WriteString("title", objective.Title);
            WriteArtifact(writer, "source", objective.Source);
            WriteStringArray(writer, "prerequisites", objective.PrerequisiteObjectiveIds);
            WriteEvidence(writer, [objective.Evidence]);
            writer.WriteStartArray("promotionCriteria");
            foreach (var criterion in objective.PromotionCriteria)
            {
                writer.WriteStartObject();
                writer.WriteString("criterionId", criterion.CriterionId);
                WriteArtifact(writer, "deterministicVerifier", criterion.DeterministicVerifier);
                writer.WriteNumber("requiredScore", criterion.RequiredScore);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void Begin(
        Utf8JsonWriter writer,
        string family,
        ArtifactRef reference,
        AssetLifecycleState lifecycle,
        AssetVersionStatus status,
        BitemporalValidity temporal,
        PermissionEnvelope permissions)
    {
        writer.WriteStartObject();
        writer.WriteString("family", family);
        WriteArtifact(writer, "reference", reference);
        writer.WriteString("lifecycle", lifecycle.ToString());
        writer.WriteString("status", status.ToString());
        writer.WriteStartObject("temporal");
        WriteDate(writer, "createdAt", temporal.CreatedAt);
        WriteDate(writer, "recordedAt", temporal.RecordedAt);
        WriteDate(writer, "validFrom", temporal.ValidFrom);
        WriteNullableDate(writer, "validUntil", temporal.ValidUntil);
        WriteNullableDate(writer, "verifiedAt", temporal.VerifiedAt);
        WriteNullableDate(writer, "supersededAt", temporal.SupersededAt);
        writer.WriteEndObject();
        WritePermissions(writer, permissions);
    }

    private static void WriteArtifact(Utf8JsonWriter writer, string name, ArtifactRef value)
    {
        writer.WriteStartObject(name);
        writer.WriteString("tenantId", value.TenantId);
        writer.WriteString("namespace", value.Namespace);
        writer.WriteString("kind", value.Kind.ToString());
        writer.WriteString("artifactId", value.ArtifactId);
        writer.WriteString("version", value.Version);
        writer.WriteEndObject();
    }

    private static void WriteArtifacts(Utf8JsonWriter writer, string name, IEnumerable<ArtifactRef> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in OrderArtifacts(values))
        {
            writer.WriteStartObject();
            writer.WriteString("tenantId", value.TenantId);
            writer.WriteString("namespace", value.Namespace);
            writer.WriteString("kind", value.Kind.ToString());
            writer.WriteString("artifactId", value.ArtifactId);
            writer.WriteString("version", value.Version);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteEvidence(Utf8JsonWriter writer, IEnumerable<EvidenceReference> values)
    {
        writer.WriteStartArray("evidence");
        foreach (var value in values
                     .OrderBy(item => item.Artifact.TenantId, StringComparer.Ordinal)
                     .ThenBy(item => item.Artifact.Namespace, StringComparer.Ordinal)
                     .ThenBy(item => item.Artifact.Kind)
                     .ThenBy(item => item.Artifact.ArtifactId, StringComparer.Ordinal)
                     .ThenBy(item => item.Artifact.Version, StringComparer.Ordinal)
                     .ThenBy(item => item.ContentHash, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            WriteArtifact(writer, "artifact", value.Artifact);
            writer.WriteString("hash", value.ContentHash);
            WriteDate(writer, "observedAt", value.ObservedAt);
            writer.WriteString("sourceKey", value.IndependentSourceKey);
            WritePermissions(writer, value.Permissions);
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
            WriteStringArray(writer, "subjects", grant.Subjects);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
            writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static IOrderedEnumerable<ArtifactRef> OrderArtifacts(IEnumerable<ArtifactRef> values)
        => values
            .OrderBy(value => value.TenantId, StringComparer.Ordinal)
            .ThenBy(value => value.Namespace, StringComparer.Ordinal)
            .ThenBy(value => value.Kind)
            .ThenBy(value => value.ArtifactId, StringComparer.Ordinal)
            .ThenBy(value => value.Version, StringComparer.Ordinal);

    private static void WriteDate(Utf8JsonWriter writer, string name, DateTimeOffset value)
        => writer.WriteString(name, value.ToUniversalTime().ToString("O"));

    private static void WriteNullableDate(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value.HasValue)
            WriteDate(writer, name, value.Value);
        else
            writer.WriteNull(name);
    }
}
