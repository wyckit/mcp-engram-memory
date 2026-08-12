using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Models.Provenance;
using McpEngramMemory.Core.Services.Knowledge;
using McpEngramMemory.Core.Services.Provenance;

namespace McpEngramMemory.Core.Services.Governance.Persistence;

/// <summary>Tenant-partitioned append-only provenance journal implementing the focused store contract.</summary>
public sealed class FileProvenanceStore : IProvenanceStore
{
    private const string StoreName = "provenance-assertions";
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Dictionary<string, ProvenanceAssertion>> _byTenant = new(StringComparer.Ordinal);
    private readonly List<PersistenceDiagnostic> _diagnostics = new();

    public FileProvenanceStore(string root)
        => _root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));

    public IReadOnlyList<PersistenceDiagnostic> Diagnostics
    {
        get { lock (_diagnostics) return _diagnostics.ToArray(); }
    }

    public async ValueTask<ProvenanceAppendResult> AppendAsync(
        ProvenanceAssertion assertion,
        IReadOnlyDictionary<ArtifactRef, PermissionEnvelope> sourcePermissions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        ArgumentNullException.ThrowIfNull(sourcePermissions);
        ValidateForAppend(assertion, sourcePermissions);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await GetTenantAsync(assertion.TenantId, cancellationToken);
            if (records.TryGetValue(assertion.AssertionId, out var existing))
            {
                if (existing.ContentHash != assertion.ContentHash)
                    throw new ProvenanceConflictException("An assertion id is immutable once published.");
                return new ProvenanceAppendResult(ProvenanceAppendOutcome.AlreadyPresent, existing);
            }

            await CrashSafeJsonPersistence.AppendAsync(
                PathFor(assertion.TenantId), StoreName, assertion.TenantId,
                records.Count + 1L, ProvenanceAssertionDto.From(assertion), cancellationToken);
            records.Add(assertion.AssertionId, assertion);
            return new ProvenanceAppendResult(ProvenanceAppendOutcome.Appended, assertion);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ProvenanceLineage> ReadLineageAsync(
        ProvenanceQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Root.TenantId != query.TenantId)
            throw new UnauthorizedAccessException("The root is outside the requested tenant.");
        if (query.MaxDepth <= 0 || query.MaxAssertions <= 0)
            throw new ArgumentOutOfRangeException(nameof(query), "Lineage limits must be positive.");

        ProvenanceAssertion[] snapshot;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            snapshot = (await GetTenantAsync(query.TenantId, cancellationToken)).Values.ToArray();
        }
        finally
        {
            _gate.Release();
        }

        var selected = new List<ProvenanceAssertion>();
        var visitedTargets = new HashSet<ArtifactRef> { query.Root };
        var frontier = new Queue<(ArtifactRef Target, int Depth)>();
        frontier.Enqueue((query.Root, 0));
        bool complete = true;
        while (frontier.Count > 0)
        {
            var (target, depth) = frontier.Dequeue();
            if (depth >= query.MaxDepth) { complete = false; continue; }
            foreach (var assertion in snapshot.Where(value => value.Target == target)
                         .OrderBy(value => value.RecordedAt)
                         .ThenBy(value => value.AssertionId, StringComparer.Ordinal))
            {
                if (!assertion.EffectivePermissions.Allows(query.Capability, query.Subject)) continue;
                if (selected.Count == query.MaxAssertions)
                {
                    complete = false;
                    frontier.Clear();
                    break;
                }
                selected.Add(assertion);
                foreach (var source in assertion.Sources)
                    if (visitedTargets.Add(source)) frontier.Enqueue((source, depth + 1));
            }
        }
        return new ProvenanceLineage(query.Root, selected, complete);
    }

    private async ValueTask<Dictionary<string, ProvenanceAssertion>> GetTenantAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        if (_byTenant.TryGetValue(tenantId, out var existing)) return existing;
        var replay = await CrashSafeJsonPersistence.ReplayAsync<ProvenanceAssertionDto>(
            PathFor(tenantId), StoreName, tenantId, cancellationToken);
        var loaded = new Dictionary<string, ProvenanceAssertion>(StringComparer.Ordinal);
        foreach (var item in replay.Records)
        {
            var assertion = item.Value.ToDomain();
            if (assertion.ContentHash != ProvenanceCanonicalizer.ComputeHash(assertion))
                throw new InvalidDataException("Persisted provenance immutable hash is invalid.");
            if (!loaded.TryAdd(assertion.AssertionId, assertion) &&
                loaded[assertion.AssertionId].ContentHash != assertion.ContentHash)
                throw new InvalidDataException("Persisted provenance assertion id is conflicted.");
        }
        _byTenant.Add(tenantId, loaded);
        lock (_diagnostics) _diagnostics.AddRange(replay.Diagnostics);
        return loaded;
    }

    private static void ValidateForAppend(
        ProvenanceAssertion assertion,
        IReadOnlyDictionary<ArtifactRef, PermissionEnvelope> sourcePermissions)
    {
        if (assertion.ContentHash != ProvenanceCanonicalizer.ComputeHash(assertion))
            throw new ProvenanceConflictException("The assertion content hash is invalid.");
        if (assertion.Sources.Any(source => !sourcePermissions.ContainsKey(source)))
            throw new UnauthorizedAccessException("Every exact source requires an authorization snapshot.");
        var inherited = PermissionEnvelopeService.Intersect(
            assertion.Sources.Select(source => sourcePermissions[source]));
        if (!PermissionEnvelopeService.IsNarrowerThanOrEqual(assertion.EffectivePermissions, inherited))
            throw new UnauthorizedAccessException("Derived provenance permissions cannot exceed source permissions.");
    }

    private string PathFor(string tenantId)
        => Path.Combine(CrashSafeJsonPersistence.TenantDirectory(_root, tenantId), "provenance.journal");
}
