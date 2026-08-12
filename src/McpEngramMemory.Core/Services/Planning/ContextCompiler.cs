using System.Text;
using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Models.Planning;

namespace McpEngramMemory.Core.Services.Planning;

/// <summary>
/// Deterministically compiles selected artifacts into governed context. Every reference is
/// re-authorized after selection and no adapter failure can produce a disclosed fragment.
/// </summary>
public sealed class ContextCompiler
{
    private readonly IReadOnlyDictionary<string, IContextArtifactAdapter> _adapters;
    private readonly IArtifactAuthorizationAdapter _authorization;
    private readonly IContextTokenCounter _tokenCounter;

    public ContextCompiler(
        IEnumerable<IContextArtifactAdapter> adapters,
        IArtifactAuthorizationAdapter authorization,
        IContextTokenCounter? tokenCounter = null)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _tokenCounter = tokenCounter ?? new DeterministicContextTokenCounter();

        var adapterMap = new Dictionary<string, IContextArtifactAdapter>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            var sourceId = AgentProfile.Required(adapter.SourceId, "SourceId");
            if (!adapterMap.TryAdd(sourceId, adapter))
                throw new ArgumentException($"Context adapter '{sourceId}' occurs more than once.", nameof(adapters));
        }
        _adapters = adapterMap;
    }

    public async ValueTask<ContextManifest> CompileAsync(
        ContextCompilationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var trace = new List<PlanningTraceEvent>();
        var omissions = new List<PlanningOmission>(request.Plan.Omissions);
        var items = new List<ContextManifestItem>();
        var sequence = new SequenceCounter();
        var usedTokens = 0;
        var usedBytes = 0;

        void Trace(
            PlanningTraceStage stage,
            PlanningTraceOutcome outcome,
            string code,
            string? sourceId = null,
            ArtifactRef? artifact = null,
            string? detail = null)
            => trace.Add(new PlanningTraceEvent(sequence.Next(), stage, outcome, code, sourceId, artifact, detail));

        if (!request.Agent.Allows(ArtifactCapability.Read))
        {
            Trace(PlanningTraceStage.LoadoutCheck, PlanningTraceOutcome.Denied,
                "read-not-authorized-by-loadout");
            omissions.Add(new PlanningOmission(PlanningOmissionKind.Authorization,
                "read-not-authorized-by-loadout"));
            return BuildManifest(request, PlanningStatus.Abstained, items, usedTokens, usedBytes, omissions, trace);
        }

        Trace(PlanningTraceStage.LoadoutCheck, PlanningTraceOutcome.Allowed,
            "read-authorized-by-loadout");

        foreach (var planItem in request.Plan.Items
                     .OrderBy(value => value.Rank)
                     .ThenBy(value => value.Artifact.ToString(), StringComparer.Ordinal)
                     .ThenBy(value => value.SourceId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await AuthorizeAllAsync(planItem.References.All, request.Agent, planItem.SourceId,
                    trace, sequence, cancellationToken).ConfigureAwait(false))
            {
                omissions.Add(new PlanningOmission(PlanningOmissionKind.Authorization,
                    "selected-reference-no-longer-authorized", planItem.SourceId));
                continue;
            }

            if (!_adapters.TryGetValue(planItem.SourceId, out var adapter))
            {
                Trace(PlanningTraceStage.Materialization, PlanningTraceOutcome.FailedClosed,
                    "context-adapter-missing", planItem.SourceId, planItem.Artifact);
                omissions.Add(new PlanningOmission(PlanningOmissionKind.AdapterFailure,
                    "context-adapter-missing", planItem.SourceId, planItem.Artifact));
                continue;
            }

            ContextArtifact materialized;
            try
            {
                materialized = await adapter.MaterializeAsync(planItem, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Context adapter returned null.");
                if (materialized.References.Primary != planItem.Artifact)
                    throw new InvalidOperationException("Context adapter changed the selected artifact identity.");
                Trace(PlanningTraceStage.Materialization, PlanningTraceOutcome.Completed,
                    "artifact-materialized", planItem.SourceId, planItem.Artifact);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Trace(PlanningTraceStage.Materialization, PlanningTraceOutcome.FailedClosed,
                    "context-adapter-failed", planItem.SourceId, planItem.Artifact, exception.GetType().Name);
                omissions.Add(new PlanningOmission(PlanningOmissionKind.AdapterFailure,
                    "context-adapter-failed", planItem.SourceId, planItem.Artifact));
                continue;
            }

            var references = planItem.References.Merge(materialized.References);
            var additionalReferences = references.All.Except(planItem.References.All).ToArray();
            if (!await AuthorizeAllAsync(additionalReferences, request.Agent, planItem.SourceId,
                    trace, sequence, cancellationToken).ConfigureAwait(false))
            {
                omissions.Add(new PlanningOmission(PlanningOmissionKind.Authorization,
                    "materialized-reference-not-authorized", planItem.SourceId));
                continue;
            }

            int tokenCount;
            int byteCount;
            try
            {
                tokenCount = _tokenCounter.CountTokens(materialized.Content);
                byteCount = Encoding.UTF8.GetByteCount(materialized.Content);
                if (tokenCount < 0)
                    throw new InvalidOperationException("Token counter returned a negative count.");
            }
            catch (Exception exception)
            {
                Trace(PlanningTraceStage.BudgetEnforcement, PlanningTraceOutcome.FailedClosed,
                    "budget-measurement-failed", planItem.SourceId, planItem.Artifact,
                    exception.GetType().Name);
                omissions.Add(new PlanningOmission(PlanningOmissionKind.AdapterFailure,
                    "budget-measurement-failed", planItem.SourceId, planItem.Artifact));
                continue;
            }

            if (items.Count >= request.Budget.MaximumItems ||
                (long)usedTokens + tokenCount > request.Budget.MaximumTokens ||
                (long)usedBytes + byteCount > request.Budget.MaximumBytes)
            {
                Trace(PlanningTraceStage.BudgetEnforcement, PlanningTraceOutcome.Excluded,
                    "context-budget-exceeded", planItem.SourceId, planItem.Artifact,
                    $"tokens={tokenCount};bytes={byteCount}");
                omissions.Add(new PlanningOmission(PlanningOmissionKind.Budget,
                    "context-budget-exceeded", planItem.SourceId, planItem.Artifact));
                continue;
            }

            usedTokens += tokenCount;
            usedBytes += byteCount;
            var warnings = planItem.Warnings.Concat(materialized.Warnings)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            items.Add(new ContextManifestItem(
                items.Count + 1,
                planItem.SourceId,
                references,
                materialized.ContentHash,
                materialized.Content,
                tokenCount,
                byteCount,
                planItem.RelevanceOrderingScore,
                warnings));
            Trace(PlanningTraceStage.BudgetEnforcement, PlanningTraceOutcome.Completed,
                "context-item-included", planItem.SourceId, planItem.Artifact);
        }

        var status = items.Count == 0
            ? PlanningStatus.Abstained
            : request.Plan.Status == PlanningStatus.Complete && omissions.Count == 0
                ? PlanningStatus.Complete
                : PlanningStatus.Incomplete;
        return BuildManifest(request, status, items, usedTokens, usedBytes, omissions, trace);
    }

    private async ValueTask<bool> AuthorizeAllAsync(
        IEnumerable<ArtifactRef> references,
        ScopedAgentProfile agent,
        string sourceId,
        ICollection<PlanningTraceEvent> trace,
        SequenceCounter sequence,
        CancellationToken cancellationToken)
    {
        foreach (var artifact in references.OrderBy(value => value.ToString(), StringComparer.Ordinal))
        {
            try
            {
                var decision = await _authorization.AuthorizeAsync(
                    new ArtifactAuthorizationRequest(artifact, agent, ArtifactCapability.Read, agent.Purpose),
                    cancellationToken).ConfigureAwait(false);
                var code = string.IsNullOrWhiteSpace(decision.Code)
                    ? decision.IsAuthorized ? "authorized" : "not-authorized"
                    : decision.Code;
                trace.Add(new PlanningTraceEvent(
                    sequence.Next(),
                    PlanningTraceStage.ArtifactAuthorization,
                    decision.IsAuthorized ? PlanningTraceOutcome.Allowed : PlanningTraceOutcome.Denied,
                    code,
                    sourceId,
                    decision.IsAuthorized ? artifact : null));
                if (!decision.IsAuthorized)
                    return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                trace.Add(new PlanningTraceEvent(
                    sequence.Next(),
                    PlanningTraceStage.ArtifactAuthorization,
                    PlanningTraceOutcome.FailedClosed,
                    "authorization-adapter-failed",
                    sourceId,
                    null,
                    exception.GetType().Name));
                return false;
            }
        }

        return true;
    }

    private static ContextManifest BuildManifest(
        ContextCompilationRequest request,
        PlanningStatus status,
        IEnumerable<ContextManifestItem> items,
        int usedTokens,
        int usedBytes,
        IEnumerable<PlanningOmission> omissions,
        IEnumerable<PlanningTraceEvent> trace)
        => new(
            request.CompilationId,
            request.Plan.PlanId,
            status,
            items,
            usedTokens,
            usedBytes,
            omissions,
            trace);

    private sealed class SequenceCounter
    {
        private int _value;
        public int Next() => ++_value;
    }
}
