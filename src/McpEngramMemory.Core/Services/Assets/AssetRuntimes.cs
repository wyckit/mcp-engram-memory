using System.Collections.ObjectModel;
using McpEngramMemory.Core.Models.Assets;
using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Services.Knowledge;

namespace McpEngramMemory.Core.Services.Assets;

public enum SkillExecutionStatus
{
    Succeeded,
    Failed,
    Denied,
    Quarantined
}

public sealed record SkillExecutionBudget(int MaxSteps, DateTimeOffset Deadline);

public sealed record SkillSandboxRequest(
    ArtifactRef Skill,
    string SkillContentHash,
    string Subject,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<ArtifactRef> AuthorizedResources,
    SkillExecutionBudget Budget,
    string ConstitutionVersionHash);

public sealed record SkillSandboxResult(
    bool Succeeded,
    string OutputContentHash,
    IReadOnlyList<string> AuditReferences,
    string? ErrorCode = null);

/// <summary>Host-owned isolation boundary. Core never executes skill instructions directly.</summary>
public interface ISkillSandbox
{
    string IsolationProfile { get; }
    ValueTask<SkillSandboxResult> ExecuteAsync(
        SkillSandboxRequest request,
        CancellationToken cancellationToken = default);
}

public interface ISkillDeterministicVerifier
{
    ArtifactRef Reference { get; }
    ValueTask<bool> VerifyAsync(
        SkillVersion skill,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default);
}

public sealed record SkillExecutionResult(
    SkillExecutionStatus Status,
    string Code,
    SkillSandboxResult? SandboxResult,
    IReadOnlyList<ArtifactRef> VerifiersRun);

/// <summary>Permission-checked, deterministic-first coordinator around a host sandbox.</summary>
public sealed class SkillExecutionCoordinator
{
    private readonly ISkillSandbox _sandbox;
    private readonly TimeProvider _timeProvider;

    public SkillExecutionCoordinator(ISkillSandbox sandbox, TimeProvider? timeProvider = null)
    {
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<SkillExecutionResult> ExecuteAsync(
        SkillVersion skill,
        string subject,
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyDictionary<ArtifactRef, bool> authorizedResources,
        IEnumerable<ISkillDeterministicVerifier> verifiers,
        SkillExecutionBudget budget,
        string constitutionVersionHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(authorizedResources);
        ArgumentNullException.ThrowIfNull(verifiers);
        if (string.IsNullOrWhiteSpace(_sandbox.IsolationProfile))
            return Result(SkillExecutionStatus.Denied, "sandbox-isolation-unspecified");
        if (skill.Definition.Lifecycle != AssetLifecycleState.Published ||
            skill.Definition.Status != AssetVersionStatus.Active)
            return Result(SkillExecutionStatus.Denied, "skill-not-active");
        if (!skill.Definition.Permissions.Allows(ArtifactCapability.Use, subject))
            return Result(SkillExecutionStatus.Denied, "skill-use-not-authorized");
        if (budget.MaxSteps < skill.Definition.Steps.Count || _timeProvider.GetUtcNow() > budget.Deadline)
            return Result(SkillExecutionStatus.Denied, "skill-budget-exhausted");
        if (skill.Definition.Parameters.Any(parameter => parameter.Required && !parameters.ContainsKey(parameter.Name)))
            return Result(SkillExecutionStatus.Denied, "required-parameter-missing");
        if (parameters.Keys.Any(key => skill.Definition.Parameters.All(parameter => parameter.Name != key)))
            return Result(SkillExecutionStatus.Denied, "unknown-parameter");
        if (skill.Definition.Resources.Any(resource =>
                !authorizedResources.TryGetValue(resource, out var allowed) || !allowed))
            return Result(SkillExecutionStatus.Denied, "resource-not-authorized");

        var byReference = verifiers.ToDictionary(value => value.Reference);
        var ran = new List<ArtifactRef>();
        foreach (var reference in skill.Definition.DeterministicVerifiers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byReference.TryGetValue(reference, out var verifier))
                return Result(SkillExecutionStatus.Denied, "deterministic-verifier-missing", ran);
            bool passed;
            try
            {
                passed = await verifier.VerifyAsync(skill, parameters, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Result(SkillExecutionStatus.Denied, "deterministic-verifier-error", ran);
            }
            ran.Add(reference);
            if (!passed)
                return Result(SkillExecutionStatus.Denied, "deterministic-verifier-veto", ran);
        }

        var sortedParameters = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
            sortedParameters[key] = value;
        var request = new SkillSandboxRequest(skill.Reference, skill.ContentHash, subject,
            new ReadOnlyDictionary<string, string>(sortedParameters),
            skill.Definition.Resources, budget, constitutionVersionHash);
        var sandboxResult = await _sandbox.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        return new SkillExecutionResult(
            sandboxResult.Succeeded ? SkillExecutionStatus.Succeeded : SkillExecutionStatus.Quarantined,
            sandboxResult.Succeeded ? "skill-executed" : sandboxResult.ErrorCode ?? "sandbox-failed",
            sandboxResult,
            ran);

        SkillExecutionResult Result(
            SkillExecutionStatus status,
            string code,
            IEnumerable<ArtifactRef>? completed = null)
            => new(status, code, null, (completed ?? []).ToArray());
    }
}

public sealed record CurriculumSourceAttestation(
    ArtifactRef Source,
    string ContentHash,
    KnowledgeMaturity? KnowledgeMaturity,
    AssetLifecycleState? SkillLifecycle,
    AssetVersionStatus Status,
    PermissionEnvelope Permissions,
    IReadOnlyList<ArtifactRef> VerificationRecords)
{
    public bool IsVerified => Source.Kind switch
    {
        ArtifactKind.Knowledge => KnowledgeMaturity >=
            McpEngramMemory.Core.Models.Knowledge.KnowledgeMaturity.Verified,
        ArtifactKind.Skill => SkillLifecycle == AssetLifecycleState.Published,
        _ => false
    };
}

public static class CurriculumCompiler
{
    public static CurriculumVersion Compile(
        CurriculumVersionDefinition definition,
        string subject,
        IReadOnlyDictionary<ArtifactRef, CurriculumSourceAttestation> attestations)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(attestations);
        var sourcePermissions = new List<PermissionEnvelope>();
        foreach (var objective in definition.Objectives)
        {
            if (!attestations.TryGetValue(objective.Source, out var attestation) ||
                attestation.Source != objective.Source ||
                !attestation.IsVerified ||
                attestation.Status != AssetVersionStatus.Active ||
                attestation.ContentHash != objective.Evidence.ContentHash ||
                attestation.VerificationRecords.Count == 0 ||
                !attestation.Permissions.Allows(ArtifactCapability.Train, subject))
            {
                throw new InvalidOperationException(
                    $"Curriculum source '{objective.Source}' is not verified and permitted for training.");
            }
            sourcePermissions.Add(attestation.Permissions);
        }

        var inherited = PermissionEnvelopeService.Intersect(sourcePermissions);
        if (!PermissionEnvelopeService.IsNarrowerThanOrEqual(definition.Permissions, inherited))
            throw new UnauthorizedAccessException("Curriculum permissions cannot exceed its verified sources.");
        return AssetPublisher.Publish(definition);
    }
}

public enum CodeExtractorFamily
{
    Roslyn,
    TreeSitter,
    Custom
}

public sealed record IncrementalCodeGraphRequest(
    ArtifactRef Target,
    string Repository,
    string Commit,
    string Language,
    IReadOnlyList<string> ChangedPaths,
    CodeGraphVersion? PreviousVersion);

public interface ICodeGraphExtractor
{
    string ExtractorId { get; }
    CodeExtractorFamily Family { get; }
    IReadOnlySet<string> Languages { get; }
    ValueTask<CodeGraphVersionDefinition> ExtractAsync(
        IncrementalCodeGraphRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Roslyn-first for C#, tree-sitter-first elsewhere, while retaining a pluggable host boundary.</summary>
public sealed class IncrementalCodeGraphIndexer
{
    private readonly IReadOnlyList<ICodeGraphExtractor> _extractors;

    public IncrementalCodeGraphIndexer(IEnumerable<ICodeGraphExtractor> extractors)
        => _extractors = extractors?.ToArray() ?? throw new ArgumentNullException(nameof(extractors));

    public ICodeGraphExtractor SelectExtractor(string language)
    {
        var candidates = _extractors.Where(value => value.Languages.Contains(language)).ToArray();
        if (candidates.Length == 0)
            throw new InvalidOperationException($"No code graph extractor supports '{language}'.");
        bool csharp = language.Equals("c#", StringComparison.OrdinalIgnoreCase) ||
                      language.Equals("csharp", StringComparison.OrdinalIgnoreCase);
        return candidates.OrderBy(value => csharp
                ? value.Family switch { CodeExtractorFamily.Roslyn => 0, CodeExtractorFamily.TreeSitter => 1, _ => 2 }
                : value.Family switch { CodeExtractorFamily.TreeSitter => 0, CodeExtractorFamily.Roslyn => 1, _ => 2 })
            .ThenBy(value => value.ExtractorId, StringComparer.Ordinal)
            .First();
    }

    public async ValueTask<CodeGraphVersion> IndexAsync(
        IncrementalCodeGraphRequest request,
        CancellationToken cancellationToken = default)
    {
        var extractor = SelectExtractor(request.Language);
        var definition = await extractor.ExtractAsync(request, cancellationToken).ConfigureAwait(false);
        if (definition.Reference != request.Target || definition.Repository != request.Repository ||
            definition.Commit != request.Commit || definition.Language != request.Language)
            throw new InvalidOperationException("Extractor output identity does not match the pinned request.");
        return AssetPublisher.Publish(definition);
    }
}
