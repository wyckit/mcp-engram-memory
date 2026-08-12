using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Models.Provenance;
using McpEngramMemory.Core.Services.Knowledge;

namespace McpEngramMemory.Core.Services.Provenance;

/// <summary>Append-only contract. Deliberately has no update or delete capability.</summary>
public interface IProvenanceStore
{
    ValueTask<ProvenanceAppendResult> AppendAsync(
        ProvenanceAssertion assertion,
        IReadOnlyDictionary<ArtifactRef, PermissionEnvelope> sourcePermissions,
        CancellationToken cancellationToken = default);

    ValueTask<ProvenanceLineage> ReadLineageAsync(
        ProvenanceQuery query,
        CancellationToken cancellationToken = default);
}
/// <summary>
/// Deterministic append-only projection suitable for embedding and tests. Persistence adapters
/// can implement the same contract without exposing destructive graph operations.
/// </summary>
public sealed class InMemoryProvenanceStore : IProvenanceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string TenantId, string AssertionId), ProvenanceAssertion> _assertions = new();

    public ValueTask<ProvenanceAppendResult> AppendAsync(
        ProvenanceAssertion assertion,
        IReadOnlyDictionary<ArtifactRef, PermissionEnvelope> sourcePermissions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        ArgumentNullException.ThrowIfNull(sourcePermissions);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(assertion.ContentHash, ProvenanceCanonicalizer.ComputeHash(assertion), StringComparison.Ordinal))
            throw new ProvenanceConflictException("The assertion content hash is invalid.");
        if (assertion.Sources.Any(source => !sourcePermissions.ContainsKey(source)))
            throw new UnauthorizedAccessException("Every exact source requires an authorization snapshot.");

        var inherited = PermissionEnvelopeService.Intersect(
            assertion.Sources.Select(source => sourcePermissions[source]));
        if (!PermissionEnvelopeService.IsNarrowerThanOrEqual(assertion.EffectivePermissions, inherited))
            throw new UnauthorizedAccessException("Derived provenance permissions cannot exceed source permissions.");

        lock (_gate)
        {
            var key = (assertion.TenantId, assertion.AssertionId);
            if (_assertions.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing.ContentHash, assertion.ContentHash, StringComparison.Ordinal))
                    throw new ProvenanceConflictException("An assertion id is immutable once published.");
                return ValueTask.FromResult(new ProvenanceAppendResult(
                    ProvenanceAppendOutcome.AlreadyPresent, existing));
            }

            _assertions.Add(key, assertion);
            return ValueTask.FromResult(new ProvenanceAppendResult(ProvenanceAppendOutcome.Appended, assertion));
        }
    }

    public ValueTask<ProvenanceLineage> ReadLineageAsync(
        ProvenanceQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Root.TenantId != query.TenantId)
            throw new UnauthorizedAccessException("The root is outside the requested tenant.");
        if (query.MaxDepth <= 0 || query.MaxAssertions <= 0)
            throw new ArgumentOutOfRangeException(nameof(query), "Lineage limits must be positive.");

        ProvenanceAssertion[] snapshot;
        lock (_gate)
            snapshot = _assertions.Values.Where(value => value.TenantId == query.TenantId).ToArray();

        var selected = new List<ProvenanceAssertion>();
        var visitedTargets = new HashSet<ArtifactRef> { query.Root };
        var frontier = new Queue<(ArtifactRef Target, int Depth)>();
        frontier.Enqueue((query.Root, 0));
        var complete = true;

        while (frontier.Count > 0)
        {
            var (target, depth) = frontier.Dequeue();
            if (depth >= query.MaxDepth)
            {
                complete = false;
                continue;
            }

            foreach (var assertion in snapshot
                         .Where(value => value.Target == target)
                         .OrderBy(value => value.RecordedAt)
                         .ThenBy(value => value.AssertionId, StringComparer.Ordinal))
            {
                if (!assertion.EffectivePermissions.Allows(query.Capability, query.Subject))
                    continue;
                if (selected.Count == query.MaxAssertions)
                {
                    complete = false;
                    frontier.Clear();
                    break;
                }
                selected.Add(assertion);
                foreach (var source in assertion.Sources)
                    if (visitedTargets.Add(source))
                        frontier.Enqueue((source, depth + 1));
            }
        }

        return ValueTask.FromResult(new ProvenanceLineage(query.Root, selected, complete));
    }
}
