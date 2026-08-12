using System.Collections.ObjectModel;
using System.Text;
using McpEngramMemory.Core.Models.Knowledge;

namespace McpEngramMemory.Core.Models.Planning;

public sealed record ContextBudget
{
    public int MaximumTokens { get; }
    public int MaximumBytes { get; }
    public int MaximumItems { get; }

    public ContextBudget(int maximumTokens, int maximumBytes, int maximumItems)
    {
        if (maximumTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumTokens));
        if (maximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (maximumItems < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumItems));
        MaximumTokens = maximumTokens;
        MaximumBytes = maximumBytes;
        MaximumItems = maximumItems;
    }

    public bool IsWithin(ContextBudget ceiling)
    {
        ArgumentNullException.ThrowIfNull(ceiling);
        return MaximumTokens <= ceiling.MaximumTokens &&
               MaximumBytes <= ceiling.MaximumBytes &&
               MaximumItems <= ceiling.MaximumItems;
    }
}

/// <summary>Materialized source content before its final disclosure authorization pass.</summary>
public sealed class ContextArtifact
{
    public string Content { get; }
    public string ContentHash { get; }
    public ArtifactReferenceSet References { get; }
    public IReadOnlyList<string> Warnings { get; }

    public ContextArtifact(
        string content,
        string contentHash,
        ArtifactReferenceSet references,
        IEnumerable<string>? warnings = null)
    {
        Content = string.IsNullOrEmpty(content)
            ? throw new ArgumentException("Context content must not be empty.", nameof(content))
            : content;
        ContentHash = AgentProfile.Required(contentHash, nameof(contentHash)).ToLowerInvariant();
        References = references ?? throw new ArgumentNullException(nameof(references));
        Warnings = new ReadOnlyCollection<string>((warnings ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray());
    }
}

public interface IContextArtifactAdapter
{
    string SourceId { get; }

    ValueTask<ContextArtifact> MaterializeAsync(
        RetrievalPlanItem item,
        CancellationToken cancellationToken = default);
}

/// <summary>Pluggable deterministic token estimator; no model invocation is permitted.</summary>
public interface IContextTokenCounter
{
    int CountTokens(string content);
}

/// <summary>
/// Deterministic, tokenizer-independent estimate. Alphanumeric runs cost at least one token per
/// four UTF-8 bytes and non-whitespace symbols cost one token each.
/// </summary>
public sealed class DeterministicContextTokenCounter : IContextTokenCounter
{
    public int CountTokens(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var tokens = 0;
        var runBytes = 0;

        void FlushRun()
        {
            if (runBytes == 0)
                return;
            tokens = checked(tokens + Math.Max(1, (runBytes + 3) / 4));
            runBytes = 0;
        }

        foreach (var rune in content.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                runBytes = checked(runBytes + rune.Utf8SequenceLength);
                continue;
            }

            FlushRun();
            if (!Rune.IsWhiteSpace(rune))
                tokens = checked(tokens + 1);
        }

        FlushRun();
        return tokens;
    }
}

public sealed class ContextCompilationRequest
{
    public string CompilationId { get; }
    public RetrievalPlan Plan { get; }
    public ScopedAgentProfile Agent { get; }
    public ContextBudget Budget { get; }

    public ContextCompilationRequest(
        string compilationId,
        RetrievalPlan plan,
        ScopedAgentProfile agent,
        ContextBudget budget)
    {
        CompilationId = AgentProfile.Required(compilationId, nameof(compilationId));
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        Budget = budget ?? throw new ArgumentNullException(nameof(budget));
        if (!budget.IsWithin(agent.MaximumContextBudget))
            throw new ArgumentOutOfRangeException(nameof(budget),
                "The compilation budget cannot exceed the effective loadout budget.");
        if (!ReferenceEquals(plan.Agent, agent))
            throw new ArgumentException("A plan can only be compiled under the scope that created it.", nameof(agent));
    }
}

/// <summary>
/// One emitted context fragment. RelevanceOrderingScore remains an ordering signal and the
/// compiler deliberately exposes no aggregate truth or confidence score.
/// </summary>
public sealed record ContextManifestItem(
    int Position,
    string SourceId,
    ArtifactReferenceSet References,
    string ContentHash,
    string Content,
    int TokenCount,
    int ByteCount,
    double RelevanceOrderingScore,
    IReadOnlyList<string> Warnings)
{
    public ArtifactRef Artifact => References.Primary;
}

public sealed class ContextManifest
{
    public string CompilationId { get; }
    public string PlanId { get; }
    public PlanningStatus Status { get; }
    public IReadOnlyList<ContextManifestItem> Items { get; }
    public int UsedTokens { get; }
    public int UsedBytes { get; }
    public IReadOnlyList<PlanningOmission> Omissions { get; }
    public IReadOnlyList<PlanningTraceEvent> Trace { get; }
    public bool HasEpistemicAssessment => false;

    public ContextManifest(
        string compilationId,
        string planId,
        PlanningStatus status,
        IEnumerable<ContextManifestItem> items,
        int usedTokens,
        int usedBytes,
        IEnumerable<PlanningOmission> omissions,
        IEnumerable<PlanningTraceEvent> trace)
    {
        CompilationId = compilationId;
        PlanId = planId;
        Status = status;
        Items = new ReadOnlyCollection<ContextManifestItem>(items.OrderBy(value => value.Position).ToArray());
        UsedTokens = usedTokens;
        UsedBytes = usedBytes;
        Omissions = new ReadOnlyCollection<PlanningOmission>(omissions.ToArray());
        Trace = new ReadOnlyCollection<PlanningTraceEvent>(trace.OrderBy(value => value.Sequence).ToArray());
    }
}
