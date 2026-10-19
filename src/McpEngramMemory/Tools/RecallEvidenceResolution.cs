using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Models.Governance;
using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Models.Provenance;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Governance;
using McpEngramMemory.Core.Services.Knowledge;
using McpEngramMemory.Core.Services.Provenance;

namespace McpEngramMemory.Tools;

/// <summary>
/// A bounded, read-only preference based on explicitly cited governed support. This is
/// neither a truth verifier nor an authority score. Ordinary memory metadata only locates
/// a committed version; it cannot supply its permissions, provenance, or evidence.
/// </summary>
internal sealed class RecallEvidenceResolution(CognitiveIndex index, NamespaceAccess access, IGovernedKnowledgeStore store)
{
    private readonly Dictionary<(string Ns, string Id), GovernedKnowledgeSnapshot> _snapshots = new();
    private readonly HashSet<(string Ns, string Id)> _linked = new();

    public IReadOnlyList<RecallConflict> Resolve(IReadOnlyList<RecallConflict> conflicts)
    {
        // Governed promotion does not admit legacy/default identities. Never manufacture
        // authority for that compatibility mode from ordinary memory metadata.
        if (access.TenantId.Length == 0 || access.AgentId == AgentIdentity.DefaultAgentId) return conflicts;
        // Allow cold storage and busy hosts to complete legitimate reads. One second
        // proved too short under concurrent framework/ONNX workloads in CI.
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var output = conflicts.ToArray();
        for (int i = 0; i < conflicts.Count; i++)
        {
            try
            {
                var resolution = ResolvePair(conflicts[i], budget.Token);
                if (resolution is not null)
                    output[i] = conflicts[i] with { Status = "resolved", Resolution = resolution };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // An incomplete read pass cannot establish that later citations do not
                // oppose an earlier choice. Roll back optional preferences for this call.
                return conflicts;
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                // Missing, inaccessible, invalid and unavailable evidence are deliberately
                // indistinguishable. Optional resolution must not break ordinary recall.
            }
        }

        // An unresolved cited disagreement vetoes a local preference: e.g. B has
        // support against A, but C has contrary support against B. Do not resolve A/B
        // while ignoring B/C. The caller disables resolution when the pair cap is reached.
        // Uncited repetitions (including the source text itself appearing in search)
        // neither add authority nor veto the supported choice by sheer repetition.
        var blocked = output.Where(c => c.Resolution is null && c.Evidence.Any(e => _linked.Contains((e.Namespace, e.Id))))
            .SelectMany(c => c.Evidence)
            .Select(e => (e.Namespace, e.Id)).ToHashSet();
        bool changed;
        do
        {
            changed = false;
            foreach (var conflict in output.Where(c => c.Evidence.Any(e => blocked.Contains((e.Namespace, e.Id)))))
                foreach (var evidence in conflict.Evidence) changed |= blocked.Add((evidence.Namespace, evidence.Id));
        } while (changed);
        for (int i = 0; i < output.Length; i++)
            if (output[i].Evidence.Any(e => blocked.Contains((e.Namespace, e.Id)))) output[i] = conflicts[i];
        return output;
    }

    private RecallConflictResolution? ResolvePair(RecallConflict conflict, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var entries = conflict.Evidence.Select(e => access.CanRead(e.Namespace)
            ? index.Get(e.Id, e.Namespace, access.TenantId) : null).ToArray();
        if (entries.Length != 2 || entries.Where((e, i) => e is null || e.Text != conflict.Evidence[i].Text).Any()) return null;
        // Two citations, including an invalid or inaccessible opposing citation, are not
        // a license to pick whichever can currently be read. No source-count voting.
        var linked = entries.Where(e => e!.Metadata.ContainsKey("recallKnowledge")).ToArray();
        foreach (var entry in linked) _linked.Add((entry!.Ns, entry.Id));
        if (linked.Length != 1) return null;
        var preferred = linked[0]!;
        var other = entries.Single(e => e != preferred)!;
        if (!TryReference(preferred.Metadata["recallKnowledge"], out var id, out var hash)) return null;
        var key = (preferred.Ns, id);
        if (!_snapshots.TryGetValue(key, out var snapshot))
        {
            // Recall's public synchronous API is retained. Both built-in stores use
            // ConfigureAwait(false); the total optional lookup budget is shared per call.
            snapshot = store.ReadAsync(access.TenantId, preferred.Ns, id, cancellation).AsTask()
                .WaitAsync(cancellation).GetAwaiter().GetResult();
            _snapshots[key] = snapshot;
        }
        var version = snapshot.ActiveVersion;
        if (version is null || snapshot.Asset is not { } asset ||
            version.Reference != new ArtifactRef(access.TenantId, preferred.Ns, ArtifactKind.Knowledge, id, version.Reference.Version) ||
            asset.TenantId != access.TenantId || asset.Namespace != preferred.Ns || asset.ArtifactId != id ||
            asset.ActiveVersionHash != hash || version.ContentHash != hash || KnowledgeCanonicalizer.ComputeHash(version) != hash ||
            version.Status != KnowledgeStatus.Active ||
            version.Maturity is not (KnowledgeMaturity.Supported or KnowledgeMaturity.Verified or KnowledgeMaturity.Established) ||
            !Readable(version.Permissions) || version.ContradictingEvidence.Count != 0 ||
            version.SupportingEvidence.Count is < 1 or > 16) return null;
        var now = DateTimeOffset.UtcNow;
        var temporal = version.Definition.Temporal;
        if (temporal.ValidFrom > now || temporal.ValidUntil <= now || temporal.SupersededAt is not null) return null;

        var lineage = snapshot.Provenance.FirstOrDefault(p => p.Target == version.Reference &&
            p.Relation == ProvenanceRelation.DerivedFrom && Readable(p.EffectivePermissions) &&
            p.ConstitutionVersionHash == version.Definition.ConstitutionVersionHash &&
            ProvenanceCanonicalizer.ComputeHash(p) == p.ContentHash &&
            version.SupportingEvidence.All(s => p.Sources.Contains(s.Artifact)) &&
            p.Verifiers.Any(v => v.TenantId == access.TenantId && v.Namespace == preferred.Ns &&
                v.Kind == ArtifactKind.Verification && v.ArtifactId == "engram.current-evidence" && v.Version == "1") &&
            snapshot.Audit.Any(a => a.EventId == p.AuditEventId && a.TenantId == access.TenantId &&
                a.PrincipalId == p.ActorId && a.Phase == ConstitutionPhase.Commit && a.Outcome == ConstitutionOutcome.Allow &&
                a.ConstitutionVersionHashes.Contains(p.ConstitutionVersionHash)));
        if (lineage is null) return null;

        var preferredClaims = RecallConflictDetection.ParseAll(preferred.Text);
        if (preferredClaims.Any(c => preferredClaims.Any(c.ConflictsWith))) return null;
        var otherClaims = RecallConflictDetection.ParseAll(other.Text);
        var disputed = preferredClaims.Where(c => otherClaims.Any(c.ConflictsWith)).ToArray();
        var governedClaims = RecallConflictDetection.ParseAll(version.Definition.Claim);
        if (disputed.Length == 0 || !disputed.All(governedClaims.Contains) ||
            governedClaims.Any(c => disputed.Any(c.ConflictsWith))) return null;

        var citations = new List<RecallResolutionSource>();
        var covered = new HashSet<RecallConflictDetection.Claim>();
        foreach (var evidence in version.SupportingEvidence)
        {
            cancellation.ThrowIfCancellationRequested();
            var reference = evidence.Artifact;
            if (reference.TenantId != access.TenantId || reference.Kind != ArtifactKind.Evidence ||
                reference.Version != evidence.ContentHash || !evidence.IsStable || !Readable(evidence.Permissions) ||
                !access.CanRead(reference.Namespace) ||
                entries.Any(e => e!.Ns == reference.Namespace && e.Id == reference.ArtifactId)) return null;
            var source = index.Get(reference.ArtifactId, reference.Namespace, access.TenantId);
            if (source is null || source.IsSummaryNode || source.LifecycleState is not ("stm" or "ltm") ||
                GovernedLearningTools.EntryHash(source) != evidence.ContentHash) return null;
            var claims = RecallConflictDetection.ParseAll(source.Text);
            if (claims.Any(c => disputed.Any(c.ConflictsWith))) return null;
            var supporting = disputed.Where(claims.Contains).ToArray();
            // Every cited source must actually address this disagreement, not just exist.
            if (supporting.Length == 0) return null;
            covered.UnionWith(supporting);
            citations.Add(new(source.Id, source.Ns, source.Text!, evidence.ContentHash));
        }
        if (!disputed.All(covered.Contains)) return null;
        return new("governed_support", preferred.Id, preferred.Ns, id, version.Reference.Version,
            version.ContentHash, lineage.AssertionId, citations);
    }

    private bool Readable(PermissionEnvelope permissions) => permissions.Allows(ArtifactCapability.Read, access.AgentId);

    private static bool TryReference(string json, out string id, out string hash)
    {
        id = hash = "";
        if (json.Length > 1024) return false;
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
            !root.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("contentHash", out var hashValue) || hashValue.ValueKind != JsonValueKind.String) return false;
        id = idValue.GetString()!;
        hash = hashValue.GetString()!;
        return id.Length is > 0 and <= 256 && !string.IsNullOrWhiteSpace(id) &&
            hash.Length == 64 && hash.All(Uri.IsHexDigit);
    }
}
