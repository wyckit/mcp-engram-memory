using System.Collections.ObjectModel;
using McpEngramMemory.Core.Models.Knowledge;

namespace McpEngramMemory.Core.Models.Planning;

public enum PlanningStatus
{
    Complete,
    Incomplete,
    Abstained
}

public enum PlanningTraceStage
{
    LoadoutCheck,
    SourceAuthorization,
    SourceDiscovery,
    ArtifactAuthorization,
    RelevanceOrdering,
    Selection,
    Materialization,
    BudgetEnforcement
}

public enum PlanningTraceOutcome
{
    Allowed,
    Denied,
    Completed,
    Excluded,
    FailedClosed
}

/// <summary>One ordered, audit-friendly event from planning or context compilation.</summary>
public sealed record PlanningTraceEvent(
    int Sequence,
    PlanningTraceStage Stage,
    PlanningTraceOutcome Outcome,
    string Code,
    string? SourceId = null,
    ArtifactRef? Artifact = null,
    string? Detail = null);

public enum PlanningOmissionKind
{
    Authorization,
    AdapterFailure,
    InvalidCandidate,
    Budget,
    Policy
}

public sealed record PlanningOmission(
    PlanningOmissionKind Kind,
    string Code,
    string? SourceId = null,
    ArtifactRef? Artifact = null);

/// <summary>
/// Typed references which must travel with a candidate and with every compiled fragment.
/// </summary>
public sealed class ArtifactReferenceSet
{
    public ArtifactRef Primary { get; }
    public IReadOnlyList<ArtifactRef> Citations { get; }
    public IReadOnlyList<ArtifactRef> Provenance { get; }
    public IReadOnlyList<ArtifactRef> AuditRecords { get; }
    public IReadOnlyList<ArtifactRef> All { get; }

    public ArtifactReferenceSet(
        ArtifactRef primary,
        IEnumerable<ArtifactRef>? citations = null,
        IEnumerable<ArtifactRef>? provenance = null,
        IEnumerable<ArtifactRef>? auditRecords = null)
    {
        Primary = primary ?? throw new ArgumentNullException(nameof(primary));
        Citations = Normalize(citations);
        Provenance = Normalize(provenance);
        AuditRecords = Normalize(auditRecords);
        All = new ReadOnlyCollection<ArtifactRef>(new[] { Primary }
            .Concat(Citations)
            .Concat(Provenance)
            .Concat(AuditRecords)
            .Distinct()
            .OrderBy(value => value == Primary ? 0 : 1)
            .ThenBy(value => value.ToString(), StringComparer.Ordinal)
            .ToArray());
    }

    public ArtifactReferenceSet Merge(ArtifactReferenceSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Primary != other.Primary)
            throw new InvalidOperationException("Reference sets for different primary artifacts cannot be merged.");
        return new ArtifactReferenceSet(
            Primary,
            Citations.Concat(other.Citations),
            Provenance.Concat(other.Provenance),
            AuditRecords.Concat(other.AuditRecords));
    }

    private static IReadOnlyList<ArtifactRef> Normalize(IEnumerable<ArtifactRef>? values)
        => new ReadOnlyCollection<ArtifactRef>((values ?? Array.Empty<ArtifactRef>())
            .Where(value => value is not null)
            .Distinct()
            .OrderBy(value => value.ToString(), StringComparer.Ordinal)
            .ToArray());
}

/// <summary>Stable identity and authorization root for a retrieval source adapter.</summary>
public sealed record RetrievalSourceDescriptor(
    string SourceId,
    string AdapterVersion,
    ArtifactRef SourceReference);

/// <summary>
/// An unranked source candidate. RankingText is visible only after the candidate and all of its
/// references pass authorization; it is intentionally absent from the resulting plan.
/// </summary>
public sealed class RetrievalCandidate
{
    public string CandidateKey { get; }
    public string RankingText { get; }
    public ArtifactReferenceSet References { get; }
    public IReadOnlyList<string> Warnings { get; }

    public ArtifactRef Artifact => References.Primary;

    public RetrievalCandidate(
        string candidateKey,
        string rankingText,
        ArtifactReferenceSet references,
        IEnumerable<string>? warnings = null)
    {
        CandidateKey = AgentProfile.Required(candidateKey, nameof(candidateKey));
        RankingText = rankingText ?? throw new ArgumentNullException(nameof(rankingText));
        References = references ?? throw new ArgumentNullException(nameof(references));
        Warnings = new ReadOnlyCollection<string>((warnings ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray());
    }
}

public sealed record RetrievalSourceQuery(
    string Query,
    string TenantId,
    string PrincipalId,
    string Purpose,
    IReadOnlyList<ArtifactKind> AllowedKinds,
    int CandidateLimit);

public sealed class RetrievalPlanningRequest
{
    public string PlanId { get; }
    public string Query { get; }
    public ScopedAgentProfile Agent { get; }
    public IReadOnlyList<ArtifactKind> AllowedKinds { get; }
    public int MaximumItems { get; }

    public RetrievalPlanningRequest(
        string planId,
        string query,
        ScopedAgentProfile agent,
        IEnumerable<ArtifactKind> allowedKinds,
        int maximumItems)
    {
        PlanId = AgentProfile.Required(planId, nameof(planId));
        Query = AgentProfile.Required(query, nameof(query));
        Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        ArgumentNullException.ThrowIfNull(allowedKinds);
        AllowedKinds = new ReadOnlyCollection<ArtifactKind>(allowedKinds.Distinct().OrderBy(value => value).ToArray());
        if (AllowedKinds.Count == 0)
            throw new ArgumentException("At least one artifact kind must be allowed.", nameof(allowedKinds));
        if (maximumItems < 0 || maximumItems > agent.MaximumRetrievalItems)
            throw new ArgumentOutOfRangeException(nameof(maximumItems),
                "The request cannot exceed the effective loadout retrieval limit.");
        MaximumItems = maximumItems;
    }
}

/// <summary>
/// A selected artifact and its transparent ordering score. RelevanceOrderingScore is only an
/// information-retrieval ordering signal; it is never confidence, authority, truth, or evidence.
/// </summary>
public sealed record RetrievalPlanItem(
    int Rank,
    string SourceId,
    string CandidateKey,
    ArtifactReferenceSet References,
    double RelevanceOrderingScore,
    IReadOnlyList<string> Warnings)
{
    public ArtifactRef Artifact => References.Primary;
}

public sealed class RetrievalPlan
{
    public string PlanId { get; }
    public string Query { get; }
    public ScopedAgentProfile Agent { get; }
    public PlanningStatus Status { get; }
    public IReadOnlyList<RetrievalPlanItem> Items { get; }
    public IReadOnlyList<PlanningOmission> Omissions { get; }
    public IReadOnlyList<PlanningTraceEvent> Trace { get; }

    public RetrievalPlan(
        string planId,
        string query,
        ScopedAgentProfile agent,
        PlanningStatus status,
        IEnumerable<RetrievalPlanItem> items,
        IEnumerable<PlanningOmission> omissions,
        IEnumerable<PlanningTraceEvent> trace)
    {
        PlanId = AgentProfile.Required(planId, nameof(planId));
        Query = AgentProfile.Required(query, nameof(query));
        Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        Status = status;
        Items = new ReadOnlyCollection<RetrievalPlanItem>(items.OrderBy(value => value.Rank).ToArray());
        Omissions = new ReadOnlyCollection<PlanningOmission>(omissions.ToArray());
        Trace = new ReadOnlyCollection<PlanningTraceEvent>(trace.OrderBy(value => value.Sequence).ToArray());
    }
}

public sealed record ArtifactAuthorizationRequest(
    ArtifactRef Artifact,
    ScopedAgentProfile Agent,
    ArtifactCapability Capability,
    string Purpose);

public sealed record ArtifactAuthorizationDecision(bool IsAuthorized, string Code);

public interface IArtifactAuthorizationAdapter
{
    ValueTask<ArtifactAuthorizationDecision> AuthorizeAsync(
        ArtifactAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IRetrievalSourceAdapter
{
    RetrievalSourceDescriptor Descriptor { get; }

    ValueTask<IReadOnlyList<RetrievalCandidate>> DiscoverAsync(
        RetrievalSourceQuery query,
        CancellationToken cancellationToken = default);
}

public interface IRetrievalRelevanceAdapter
{
    string AdapterId { get; }
    string Version { get; }

    ValueTask<double> ScoreAsync(
        RetrievalSourceQuery query,
        RetrievalSourceDescriptor source,
        RetrievalCandidate authorizedCandidate,
        CancellationToken cancellationToken = default);
}
