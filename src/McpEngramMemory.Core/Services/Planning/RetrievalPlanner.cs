using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Models.Planning;

namespace McpEngramMemory.Core.Services.Planning;

/// <summary>
/// Deterministic, model-free retrieval orchestration. Source and artifact authorization always
/// precede source discovery disclosure and relevance scoring respectively.
/// </summary>
public sealed class RetrievalPlanner
{
    private readonly IReadOnlyDictionary<string, IRetrievalSourceAdapter> _sources;
    private readonly IArtifactAuthorizationAdapter _authorization;
    private readonly IRetrievalRelevanceAdapter _relevance;

    public RetrievalPlanner(
        IEnumerable<IRetrievalSourceAdapter> sources,
        IArtifactAuthorizationAdapter authorization,
        IRetrievalRelevanceAdapter relevance)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _relevance = relevance ?? throw new ArgumentNullException(nameof(relevance));

        var sourceMap = new Dictionary<string, IRetrievalSourceAdapter>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            var sourceId = AgentProfile.Required(source.Descriptor.SourceId, "SourceId");
            if (!sourceMap.TryAdd(sourceId, source))
                throw new ArgumentException($"Retrieval source '{sourceId}' occurs more than once.", nameof(sources));
        }
        _sources = sourceMap;
    }

    public async ValueTask<RetrievalPlan> PlanAsync(
        RetrievalPlanningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var trace = new List<PlanningTraceEvent>();
        var omissions = new List<PlanningOmission>();
        var scored = new List<ScoredCandidate>();
        var sequence = new SequenceCounter();

        void Trace(
            PlanningTraceStage stage,
            PlanningTraceOutcome outcome,
            string code,
            string? sourceId = null,
            ArtifactRef? artifact = null,
            string? detail = null)
            => trace.Add(new PlanningTraceEvent(sequence.Next(), stage, outcome, code, sourceId, artifact, detail));

        if (!request.Agent.Allows(ArtifactCapability.Search))
        {
            Trace(PlanningTraceStage.LoadoutCheck, PlanningTraceOutcome.Denied, "search-not-authorized-by-loadout");
            omissions.Add(new PlanningOmission(PlanningOmissionKind.Authorization, "search-not-authorized-by-loadout"));
            return BuildPlan(request, PlanningStatus.Abstained, Array.Empty<RetrievalPlanItem>(), omissions, trace);
        }

        Trace(PlanningTraceStage.LoadoutCheck, PlanningTraceOutcome.Allowed, "search-authorized-by-loadout");

        foreach (var sourceId in request.Agent.EnabledSourceIds.OrderBy(value => value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_sources.TryGetValue(sourceId, out var source))
            {
                Trace(PlanningTraceStage.SourceDiscovery, PlanningTraceOutcome.FailedClosed,
                    "source-adapter-missing", sourceId);
                omissions.Add(new PlanningOmission(PlanningOmissionKind.AdapterFailure,
                    "source-adapter-missing", sourceId));
                continue;
            }

            var sourceAuthorization = await AuthorizeAsync(
                source.Descriptor.SourceReference,
                request.Agent,
                ArtifactCapability.Search,
                trace,
                sequence,
                PlanningTraceStage.SourceAuthorization,
                sourceId,
                cancellationToken).ConfigureAwait(false);
            if (!sourceAuthorization)
            {
                omissions.Add(new PlanningOmission(PlanningOmissionKind.Authorization,
                    "source-not-authorized", sourceId));
                continue;
            }

            var sourceQuery = new RetrievalSourceQuery(
                request.Query,
                request.Agent.TenantId,
                request.Agent.PrincipalId,
                request.Agent.Purpose,
                request.AllowedKinds,
                request.Agent.MaximumRetrievalItems);

            IReadOnlyList<RetrievalCandidate> candidates;
            try
            {
                candidates = await source.DiscoverAsync(sourceQuery, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Source adapter returned null.");
                Trace(PlanningTraceStage.SourceDiscovery, PlanningTraceOutcome.Completed,
                    "source-discovery-complete", sourceId,
                    detail: $"candidate-count={candidates.Count}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Trace(PlanningTraceStage.SourceDiscovery, PlanningTraceOutcome.FailedClosed,
                    "source-adapter-failed", sourceId, detail: exception.GetType().Name);
                omissions.Add(new PlanningOmission(PlanningOmissionKind.AdapterFailure,
                    "source-adapter-failed", sourceId));
                continue;
            }

            foreach (var candidate in candidates
                         .Where(value => value is not null)
                         .OrderBy(value => value.Artifact.ToString(), StringComparer.Ordinal)
                         .ThenBy(value => value.CandidateKey, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.Artifact.TenantId != request.Agent.TenantId ||
                    !request.AllowedKinds.Contains(candidate.Artifact.Kind))
                {
                    Trace(PlanningTraceStage.ArtifactAuthorization, PlanningTraceOutcome.Excluded,
                        "candidate-outside-request-scope", sourceId);
                    omissions.Add(new PlanningOmission(PlanningOmissionKind.InvalidCandidate,
                        "candidate-outside-request-scope", sourceId));
                    continue;
                }

                var authorized = true;
                foreach (var reference in candidate.References.All)
                {
                    if (!await AuthorizeAsync(
                            reference,
                            request.Agent,
                            ArtifactCapability.Search,
                            trace,
                            sequence,
                            PlanningTraceStage.ArtifactAuthorization,
                            sourceId,
                            cancellationToken).ConfigureAwait(false))
                    {
                        authorized = false;
                        break;
                    }
                }

                if (!authorized)
                {
                    omissions.Add(new PlanningOmission(PlanningOmissionKind.Authorization,
                        "candidate-reference-not-authorized", sourceId));
                    continue;
                }

                double orderingScore;
                try
                {
                    orderingScore = await _relevance.ScoreAsync(
                        sourceQuery, source.Descriptor, candidate, cancellationToken).ConfigureAwait(false);
                    if (!double.IsFinite(orderingScore))
                        throw new InvalidOperationException("Relevance adapter returned a non-finite score.");
                    Trace(PlanningTraceStage.RelevanceOrdering, PlanningTraceOutcome.Completed,
                        "candidate-scored", sourceId, candidate.Artifact);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Trace(PlanningTraceStage.RelevanceOrdering, PlanningTraceOutcome.FailedClosed,
                        "relevance-adapter-failed", sourceId, candidate.Artifact, exception.GetType().Name);
                    omissions.Add(new PlanningOmission(PlanningOmissionKind.AdapterFailure,
                        "relevance-adapter-failed", sourceId, candidate.Artifact));
                    continue;
                }

                scored.Add(new ScoredCandidate(sourceId, candidate, orderingScore));
            }
        }

        var ordered = scored
            .OrderByDescending(value => value.OrderingScore)
            .ThenBy(value => value.Candidate.Artifact.ToString(), StringComparer.Ordinal)
            .ThenBy(value => value.SourceId, StringComparer.Ordinal)
            .ThenBy(value => value.Candidate.CandidateKey, StringComparer.Ordinal)
            .ToArray();
        var selected = ordered.Take(request.MaximumItems).Select((value, index) => new RetrievalPlanItem(
            index + 1,
            value.SourceId,
            value.Candidate.CandidateKey,
            value.Candidate.References,
            value.OrderingScore,
            value.Candidate.Warnings)).ToArray();

        foreach (var excluded in ordered.Skip(request.MaximumItems))
        {
            omissions.Add(new PlanningOmission(PlanningOmissionKind.Budget,
                "retrieval-item-limit", excluded.SourceId, excluded.Candidate.Artifact));
        }

        Trace(PlanningTraceStage.Selection, PlanningTraceOutcome.Completed, "selection-complete",
            detail: $"selected-count={selected.Length}");
        var status = selected.Length == 0
            ? PlanningStatus.Abstained
            : omissions.Count == 0 ? PlanningStatus.Complete : PlanningStatus.Incomplete;
        return BuildPlan(request, status, selected, omissions, trace);
    }

    private async ValueTask<bool> AuthorizeAsync(
        ArtifactRef artifact,
        ScopedAgentProfile agent,
        ArtifactCapability capability,
        ICollection<PlanningTraceEvent> trace,
        SequenceCounter sequence,
        PlanningTraceStage stage,
        string sourceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var decision = await _authorization.AuthorizeAsync(
                new ArtifactAuthorizationRequest(artifact, agent, capability, agent.Purpose),
                cancellationToken).ConfigureAwait(false);
            var code = string.IsNullOrWhiteSpace(decision.Code)
                ? decision.IsAuthorized ? "authorized" : "not-authorized"
                : decision.Code;
            trace.Add(new PlanningTraceEvent(
                sequence.Next(),
                stage,
                decision.IsAuthorized ? PlanningTraceOutcome.Allowed : PlanningTraceOutcome.Denied,
                code,
                sourceId,
                decision.IsAuthorized ? artifact : null));
            return decision.IsAuthorized;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            trace.Add(new PlanningTraceEvent(sequence.Next(), stage, PlanningTraceOutcome.FailedClosed,
                "authorization-adapter-failed", sourceId, null, exception.GetType().Name));
            return false;
        }
    }

    private static RetrievalPlan BuildPlan(
        RetrievalPlanningRequest request,
        PlanningStatus status,
        IEnumerable<RetrievalPlanItem> items,
        IEnumerable<PlanningOmission> omissions,
        IEnumerable<PlanningTraceEvent> trace)
        => new(request.PlanId, request.Query, request.Agent, status, items, omissions, trace);

    private sealed record ScoredCandidate(
        string SourceId,
        RetrievalCandidate Candidate,
        double OrderingScore);

    private sealed class SequenceCounter
    {
        private int _value;
        public int Next() => ++_value;
    }
}
