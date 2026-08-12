using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Models.Governance;
using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Models.Learning;
using McpEngramMemory.Core.Models.Provenance;
using McpEngramMemory.Core.Services.Constitution;
using McpEngramMemory.Core.Services.Knowledge;
using McpEngramMemory.Core.Services.Provenance;

namespace McpEngramMemory.Core.Services.Governance.Persistence;

/// <summary>
/// Crash-safe atomic promotion store. A knowledge aggregate, active pointer, provenance assertion,
/// and audit event share one checksum-protected snapshot and therefore become visible together.
/// </summary>
public sealed class FileGovernedKnowledgeStore : IGovernedKnowledgeStore
{
    private const string StoreName = "governed-promotion";

    /// <summary>
    /// Bounded because promotion runs on a request-serving path while holding <c>_gate</c>: a peer
    /// that crashed with the lock handle leaked would otherwise wedge this store indefinitely,
    /// taking every other promotion and read on it down too. Surfacing the holder's IOException
    /// leaves the caller a retryable failure instead of a hang.
    /// </summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    private readonly string _root;
    private readonly ConstitutionCommitGuard _commitGuard;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<PersistenceDiagnostic> _diagnostics = new();

    public FileGovernedKnowledgeStore(string root, ConstitutionCommitGuard? commitGuard = null)
    {
        _root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));
        _commitGuard = commitGuard ?? new ConstitutionCommitGuard();
    }

    public IReadOnlyList<PersistenceDiagnostic> Diagnostics
    {
        get { lock (_diagnostics) return _diagnostics.ToArray(); }
    }

    public async ValueTask<GovernedCommitResult> CommitPromotionAsync(
        GovernedPromotionCommit commit,
        Func<CommitAuthorityState> resolveCurrentAuthority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(resolveCurrentAuthority);
        if (commit.Promotion.Outcome != PromotionOutcome.Promoted)
            return Denied("promotion-not-authorized");
        var invalid = ValidateCommit(commit);
        if (invalid is not null)
            return Denied(invalid);

        var reference = commit.Version.Reference;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(reference.TenantId, reference.Namespace, reference.ArtifactId);
            await using var processLock = await CrashSafeJsonPersistence.AcquireExclusiveLockAsync(
                path, LockTimeout, cancellationToken).ConfigureAwait(false);
            var authority = resolveCurrentAuthority();
            var recheck = _commitGuard.Recheck(commit.AuthorizationSnapshot,
                authority.ConstitutionVersionHash, authority.ResourceVersions);
            if (!recheck.CanCommit)
                return Denied(recheck.Code);

            var loaded = await CrashSafeJsonPersistence.ReadSnapshotAsync<GovernedSnapshotDto>(
                path, StoreName, reference.TenantId, cancellationToken).ConfigureAwait(false);
            lock (_diagnostics) _diagnostics.AddRange(loaded.Diagnostics);
            var current = loaded.Value?.ToDomain();
            if (current is not null)
                ValidateSnapshot(current, reference.TenantId, reference.Namespace, reference.ArtifactId);

            string? activeHash = current?.Asset.ActiveVersionHash;
            if (!string.Equals(activeHash, commit.ExpectedActiveVersionHash, StringComparison.Ordinal))
                return new GovernedCommitResult(
                    GovernedCommitOutcome.VersionConflict, "active-version-changed", null, activeHash);

            var existingVersion = current?.Asset.Versions.FirstOrDefault(value =>
                value.Reference.Version == reference.Version);
            if (existingVersion is not null)
            {
                if (existingVersion.ContentHash != commit.Version.ContentHash)
                    return Denied("knowledge-version-conflict");
                return new GovernedCommitResult(GovernedCommitOutcome.AlreadyCommitted,
                    "already-committed", existingVersion.Reference, existingVersion.ContentHash);
            }

            var existingAssertion = current?.Provenance.FirstOrDefault(value =>
                value.AssertionId == commit.Provenance.AssertionId);
            if (existingAssertion is not null && existingAssertion.ContentHash != commit.Provenance.ContentHash)
                return Denied("provenance-id-conflict");

            var versions = (current?.Asset.Versions ?? Array.Empty<KnowledgeVersion>())
                .Append(commit.Version).ToArray();
            var asset = KnowledgeCanonicalizer.PublishAsset(versions, commit.Version.ContentHash);
            var provenance = (current?.Provenance ?? Array.Empty<ProvenanceAssertion>())
                .Append(commit.Provenance)
                .GroupBy(value => value.AssertionId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(value => value.RecordedAt)
                .ThenBy(value => value.AssertionId, StringComparer.Ordinal)
                .ToArray();
            var audit = (current?.Audit ?? Array.Empty<ConstitutionAuditRecord>()).ToList();
            audit.Add(commit.Receipt.AuditRecord.WithSequence(audit.Count + 1L));
            var next = new GovernedSnapshot(asset, provenance, audit);

            await CrashSafeJsonPersistence.WriteSnapshotAsync(
                path, StoreName, reference.TenantId, GovernedSnapshotDto.From(next), cancellationToken)
                .ConfigureAwait(false);
            return new GovernedCommitResult(GovernedCommitOutcome.Committed,
                "committed", reference, commit.Version.ContentHash);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<GovernedKnowledgeSnapshot> ReadAsync(
        string tenantId,
        string @namespace,
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await CrashSafeJsonPersistence.ReadSnapshotAsync<GovernedSnapshotDto>(
                PathFor(tenantId, @namespace, artifactId), StoreName, tenantId ?? string.Empty,
                cancellationToken).ConfigureAwait(false);
            lock (_diagnostics) _diagnostics.AddRange(loaded.Diagnostics);
            if (loaded.Value is null)
                return new GovernedKnowledgeSnapshot(null, null,
                    Array.Empty<ProvenanceAssertion>(), Array.Empty<ConstitutionAuditRecord>());
            var value = loaded.Value.ToDomain();
            ValidateSnapshot(value, tenantId ?? string.Empty, @namespace, artifactId);
            var active = value.Asset.Versions.Single(version =>
                version.ContentHash == value.Asset.ActiveVersionHash);
            return new GovernedKnowledgeSnapshot(value.Asset, active, value.Provenance, value.Audit);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? ValidateCommit(GovernedPromotionCommit commit)
    {
        var version = commit.Version;
        if (KnowledgeCanonicalizer.ComputeHash(version) != version.ContentHash)
            return "knowledge-hash-invalid";
        if (ProvenanceCanonicalizer.ComputeHash(commit.Provenance) != commit.Provenance.ContentHash)
            return "provenance-hash-invalid";
        if (commit.Provenance.Target != version.Reference)
            return "provenance-target-mismatch";
        if (commit.Provenance.AuditEventId != commit.Receipt.AuditRecord.EventId)
            return "provenance-audit-receipt-mismatch";
        var receipt = commit.Receipt;
        var audit = receipt.AuditRecord;
        if (!ReferenceEquals(commit.Promotion.ConstitutionDecision, receipt.Decision) ||
            audit.OperationId != receipt.Decision.OperationId ||
            audit.Outcome != ConstitutionOutcome.Allow)
            return "audit-decision-mismatch";
        var decision = receipt.Decision;
        if (decision.Outcome != ConstitutionOutcome.Allow ||
            decision.Phase != ConstitutionPhase.Commit ||
            audit.Phase != ConstitutionPhase.Commit ||
            audit.TenantId != version.Reference.TenantId ||
            !decision.ConstitutionVersionHashes.SequenceEqual(audit.ConstitutionVersionHashes) ||
            !decision.ConstitutionVersionHashes.Contains(
                commit.AuthorizationSnapshot.ConstitutionVersionHash, StringComparer.Ordinal))
        {
            return "constitution-commit-receipt-invalid";
        }
        if (!ReceiptTargetsVersion(receipt, version))
            return "constitution-commit-target-mismatch";
        if (commit.Provenance.Sources.Any(source => !commit.SourcePermissionSnapshots.ContainsKey(source)))
            return "source-authorization-missing";
        var evidence = version.SupportingEvidence.Concat(version.ContradictingEvidence).ToArray();
        if (evidence.Any(item => !commit.Provenance.Sources.Contains(item.Artifact)))
            return "evidence-provenance-mismatch";
        if (evidence.Any(item => !PermissionEquivalent(
                item.Permissions, commit.SourcePermissionSnapshots[item.Artifact])))
            return "evidence-authorization-snapshot-mismatch";
        var inherited = PermissionEnvelopeService.Intersect(
            commit.Provenance.Sources.Select(source => commit.SourcePermissionSnapshots[source]));
        if (!PermissionEnvelopeService.IsNarrowerThanOrEqual(version.Permissions, inherited) ||
            !PermissionEnvelopeService.IsNarrowerThanOrEqual(commit.Provenance.EffectivePermissions, inherited))
            return "permission-broadening-forbidden";
        return null;
    }

    private static bool PermissionEquivalent(PermissionEnvelope left, PermissionEnvelope right)
        => PermissionEnvelopeService.IsNarrowerThanOrEqual(left, right) &&
           PermissionEnvelopeService.IsNarrowerThanOrEqual(right, left);

    private static bool ReceiptTargetsVersion(ConstitutionCommitReceipt receipt, KnowledgeVersion version)
    {
        var operation = receipt.Operation;
        var target = operation.Target;
        var reference = version.Reference;
        return operation.Kind == CognitiveOperationKind.PromoteKnowledge &&
               operation.TenantId == reference.TenantId &&
               operation.PayloadHash == version.ContentHash &&
               target is not null &&
               target.TenantId == reference.TenantId &&
               target.Namespace == reference.Namespace &&
               target.ArtifactKind == reference.Kind.ToString() &&
               target.ArtifactId == reference.ArtifactId &&
               target.Version == reference.Version;
    }

    private static void ValidateSnapshot(
        GovernedSnapshot snapshot,
        string tenantId,
        string @namespace,
        string artifactId)
    {
        var asset = snapshot.Asset;
        if (asset.TenantId != tenantId || asset.Namespace != @namespace || asset.ArtifactId != artifactId)
            throw new InvalidDataException("Governed snapshot identity does not match its partition.");
        if (KnowledgeCanonicalizer.ComputeHash(asset) != asset.ContentHash ||
            asset.Versions.Any(version => KnowledgeCanonicalizer.ComputeHash(version) != version.ContentHash))
            throw new InvalidDataException("Governed snapshot has an invalid knowledge hash.");
        if (snapshot.Provenance.Any(assertion => assertion.TenantId != tenantId ||
                ProvenanceCanonicalizer.ComputeHash(assertion) != assertion.ContentHash))
            throw new InvalidDataException("Governed snapshot has invalid provenance.");
        if (snapshot.Audit.Select((record, index) => (record, index))
            .Any(item => item.record.TenantId != tenantId || item.record.Sequence != item.index + 1L))
            throw new InvalidDataException("Governed snapshot has invalid audit sequence or tenancy.");
    }

    private string PathFor(string? tenantId, string @namespace, string artifactId)
        => Path.Combine(CrashSafeJsonPersistence.TenantDirectory(_root, tenantId ?? string.Empty),
            "governed", CrashSafeJsonPersistence.ArtifactFileName(@namespace, artifactId));

    private static GovernedCommitResult Denied(string code)
        => new(GovernedCommitOutcome.Denied, code, null, null);

    private sealed record GovernedSnapshot(
        KnowledgeAsset Asset,
        IReadOnlyList<ProvenanceAssertion> Provenance,
        IReadOnlyList<ConstitutionAuditRecord> Audit);

    private sealed record GovernedAuditDto(
        long Sequence,
        string EventId,
        string OperationId,
        string TenantId,
        string PrincipalId,
        ConstitutionPhase Phase,
        ConstitutionOutcome Outcome,
        string[] ConstitutionVersionHashes,
        string[] FindingCodes,
        DateTimeOffset OccurredAt)
    {
        public static GovernedAuditDto From(ConstitutionAuditRecord value)
            => new(value.Sequence, value.EventId, value.OperationId, value.TenantId, value.PrincipalId,
                value.Phase, value.Outcome, value.ConstitutionVersionHashes.ToArray(),
                value.FindingCodes.ToArray(), value.OccurredAt);

        public ConstitutionAuditRecord ToDomain()
            => new(Sequence, EventId, OperationId, TenantId, PrincipalId, Phase, Outcome,
                ConstitutionVersionHashes, FindingCodes, OccurredAt);
    }

    private sealed record GovernedSnapshotDto(
        KnowledgeAssetDto Asset,
        ProvenanceAssertionDto[] Provenance,
        GovernedAuditDto[] Audit)
    {
        public static GovernedSnapshotDto From(GovernedSnapshot value)
            => new(KnowledgeAssetDto.From(value.Asset),
                value.Provenance.Select(ProvenanceAssertionDto.From).ToArray(),
                value.Audit.Select(GovernedAuditDto.From).ToArray());

        public GovernedSnapshot ToDomain()
            => new(Asset.ToDomain(), Provenance.Select(value => value.ToDomain()).ToArray(),
                Audit.Select(value => value.ToDomain()).ToArray());
    }
}
