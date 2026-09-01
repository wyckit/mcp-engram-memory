using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Models.Governance;
using McpEngramMemory.Core.Models.Knowledge;
using McpEngramMemory.Core.Models.Learning;
using McpEngramMemory.Core.Models.Provenance;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Constitution;
using McpEngramMemory.Core.Services.Governance;
using McpEngramMemory.Core.Services.Knowledge;
using McpEngramMemory.Core.Services.Learning;
using McpEngramMemory.Core.Services.Provenance;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tools;

/// <summary>Hosted adapter for the governed Teacher/Verifier/publication pipeline.</summary>
[McpServerToolType]
public sealed class GovernedLearningTools
{
    private readonly CognitiveIndex _index;
    private readonly NamespaceAccess _access;
    private readonly IPrincipalContext _principal;
    private readonly IConstitutionProvider _constitution;
    private readonly ConstitutionKernel _kernel;
    private readonly IGovernedKnowledgeStore _store;

    public GovernedLearningTools(
        CognitiveIndex index,
        NamespaceAccess access,
        IPrincipalContext principal,
        IConstitutionProvider constitution,
        ConstitutionKernel kernel,
        IGovernedKnowledgeStore store)
    {
        _index = index;
        _access = access;
        _principal = principal;
        _constitution = constitution;
        _kernel = kernel;
        _store = store;
    }

    [McpServerTool(Name = "promote_knowledge", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Promote an evidence-backed claim through the governed Teacher, deterministic Verifier, Constitution receipt, provenance, and atomic knowledge store. This is distinct from promote_memory, which only changes STM/LTM lifecycle state.")]
    public async Task<object> PromoteKnowledge(
        [Description("Stable governed knowledge artifact ID.")] string id,
        [Description("Owned namespace containing the source memories and target knowledge asset.")] string ns,
        [Description("Claim proposed for governed knowledge.")] string claim,
        [Description("One or more exact source memory IDs in the namespace.")] string[] supportingSourceIds,
        [Description("Optional source memory IDs that contradict the claim; contradictions remain attached.")] string[]? contradictingSourceIds = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_principal.TenantId) ||
            _principal.AgentId == AgentIdentity.DefaultAgentId)
        {
            return Error("governed knowledge requires an authenticated, non-default principal and non-legacy tenant context");
        }
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(claim))
            return Error("id, ns, and claim are required");
        if (supportingSourceIds is null || supportingSourceIds.Length == 0)
            return Error("at least one supporting source is required");
        if (!_access.CanRead(ns) || !_access.CanWrite(ns))
            return Error("namespace is not accessible for governed publication");

        var support = ResolveSources(ns, supportingSourceIds);
        var contradictions = ResolveSources(ns, contradictingSourceIds ?? []);
        if (support is null || contradictions is null)
            return Error("one or more source memories are missing or inaccessible");

        var all = support.Concat(contradictions).DistinctBy(item => item.Reference).ToArray();
        var permissions = PrincipalPermissions(_principal.AgentId);
        var constitutionHash = _constitution.Current.EffectiveVersionHash;
        var proposalId = $"mcp-{Guid.NewGuid():N}";
        var generator = new DirectProposalGenerator(claim, support.Select(SourceOperation).ToArray(),
            contradictions.Select(SourceOperation).ToArray());
        var teacherRequest = new TeacherRequest(
            proposalId, _principal.TenantId, $"Promote governed knowledge '{id}'",
            all.Select(SourceOperation).ToArray(),
            new GenerationIdentity("engram.mcp.direct-teacher", "1", "governed-promotion", "1",
                $"{_principal.TenantId}/{ns}"),
            constitutionHash,
            new[] { KnowledgeCapability.Read }.ToHashSet(),
            DateTimeOffset.UtcNow);
        var existing = await _store.ReadAsync(_principal.TenantId, ns, id, cancellationToken)
            .ConfigureAwait(false);
        var coordinator = new GovernedLearningCoordinator(
            new TeacherRuntime(generator),
            new VerifierPlanner(),
            new KnowledgePromotionEvaluator(),
            _kernel,
            _store);
        var verifier = new CurrentEvidenceVerifier(_index, _principal.TenantId, ns, all);

        var result = await coordinator.ExecuteAsync(new GovernedLearningRequest(
            teacherRequest,
            _principal.AgentId,
            new LearningExecutionBudget(1, 1, DateTimeOffset.UtcNow.AddMinutes(1), false),
            [verifier],
            proposal => Materialize(proposal, id, ns, support, contradictions, permissions,
                existing.ActiveVersion?.ContentHash),
            () => CurrentAuthority(all)), cancellationToken).ConfigureAwait(false);

        return new
        {
            status = result.Commit?.Outcome.ToString().ToLowerInvariant()
                     ?? result.Promotion.Outcome.ToString().ToLowerInvariant(),
            code = result.Commit?.Code ?? string.Join(",", result.Promotion.Findings.Select(value => value.Code)),
            artifactId = id,
            version = result.Commit?.ActiveVersion?.Version,
            contentHash = result.Commit?.ActiveVersionHash,
            proposalStatus = result.Proposal.Status.ToString().ToLowerInvariant(),
            deterministicVerified = result.Verification.DeterministicChecksPassed,
            constitutionHashes = result.Promotion.ConstitutionDecision.ConstitutionVersionHashes
        };
    }

    private GovernedLearningMaterialization Materialize(
        KnowledgeProposal proposal,
        string id,
        string ns,
        IReadOnlyList<SourceSnapshot> support,
        IReadOnlyList<SourceSnapshot> contradictions,
        PermissionEnvelope permissions,
        string? expectedActiveHash)
    {
        var now = DateTimeOffset.UtcNow;
        var versionId = Hash(string.Join("\n", proposal.Claim, proposal.ConstitutionVersionHash,
            string.Join("|", proposal.AllEvidence.Select(value => $"{KnowledgeProposal.EvidenceKey(value)}@{value.Version}"))))[..24];
        var target = new ArtifactRef(_principal.TenantId, ns, ArtifactKind.Knowledge, id, versionId);
        EvidenceReference Evidence(SourceSnapshot source) => new(
            source.Reference, source.ContentHash, source.Entry.CreatedAt, source.Reference.ArtifactId, permissions);
        CalibratedComponent Component(decimal value, string basis) => new(value, basis, "1", now);
        var profile = new EpistemicProfile(
            Component(.5m, "deterministic-verification"),
            Component(.5m, "source-memory-authority-unassessed"),
            Component(.5m, "source-memory-trust-unassessed"),
            Component(Math.Min(.9m, .4m + support.Count * .1m), $"{support.Count}-source-evidence"),
            Component(.5m, "source-revision-pinned"),
            Component(.5m, "consensus-not-inferred"));
        var definition = new KnowledgeVersionDefinition(
            target, proposal.Claim, KnowledgeMaturity.Supported,
            contradictions.Count == 0 ? KnowledgeStatus.Active : KnowledgeStatus.Disputed,
            new BitemporalValidity(now, now, proposal.Validity.ValidFrom ?? now, proposal.Validity.ValidUntil,
                verifiedAt: now),
            profile, support.Select(Evidence), contradictions.Select(Evidence), permissions,
            proposal.ConstitutionVersionHash);
        var version = KnowledgeCanonicalizer.PublishVersion(definition);
        var sourcePermissions = support.Concat(contradictions)
            .Select(item => item.Reference)
            .Distinct()
            .ToDictionary(item => item, _ => permissions);

        return new GovernedLearningMaterialization(
            version,
            receipt => ProvenanceCanonicalizer.Publish(
                $"promotion-{proposal.ProposalId}", target, sourcePermissions.Keys,
                ProvenanceRelation.DerivedFrom, _principal.AgentId, "mcp-engram-memory", "2.1",
                resultVerifierRefs(proposal, ns), receipt.Decision.ConstitutionVersionHashes[^1],
                receipt.AuditRecord.EventId, permissions, now),
            sourcePermissions,
            expectedActiveHash);

        IEnumerable<ArtifactRef> resultVerifierRefs(KnowledgeProposal value, string targetNamespace)
            => [new ArtifactRef(value.TenantId, targetNamespace, ArtifactKind.Verification,
                "engram.current-evidence", "1")];
    }

    private CommitAuthorityState CurrentAuthority(IEnumerable<SourceSnapshot> sources)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var current = _index.Get(source.Entry.Id, source.Entry.Ns, tenantId: _principal.TenantId);
            versions[KnowledgeProposal.EvidenceKey(SourceOperation(source))] =
                current is null ? "missing" : EntryHash(current);
        }
        return new CommitAuthorityState(_constitution.Current.EffectiveVersionHash, versions);
    }

    private IReadOnlyList<SourceSnapshot>? ResolveSources(string ns, IEnumerable<string> ids)
    {
        var result = new List<SourceSnapshot>();
        foreach (var id in ids.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
        {
            var entry = _index.Get(id, ns, tenantId: _principal.TenantId);
            if (entry is null || !_access.CanRead(entry.Ns))
                return null;
            var hash = EntryHash(entry);
            result.Add(new SourceSnapshot(entry,
                new ArtifactRef(_principal.TenantId, ns, ArtifactKind.Evidence, entry.Id, hash), hash));
        }
        return result;
    }

    private static OperationArtifactReference SourceOperation(SourceSnapshot source)
        => new(source.Reference.TenantId, source.Reference.Namespace, source.Reference.Kind.ToString(),
            source.Reference.ArtifactId, source.Reference.Version);

    private static PermissionEnvelope PrincipalPermissions(string principal)
        // Legacy CognitiveEntry ACLs prove namespace readability only. They do not establish
        // USE or TRAIN rights, so governed publication must remain read-only until an explicit,
        // audited declassification/capability grant supplies broader source permissions.
        => new([new CapabilityGrant(ArtifactCapability.Read, [principal])]);

    private static string EntryHash(CognitiveEntry entry)
        => Hash(string.Join("\n", entry.TenantId, entry.Ns, entry.Id, entry.Text, entry.Category,
            string.Join("\n", entry.Metadata.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"{item.Key}={item.Value}"))));

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static object Error(string message) => new { status = "error", message };

    private sealed record SourceSnapshot(CognitiveEntry Entry, ArtifactRef Reference, string ContentHash);

    private sealed class DirectProposalGenerator : IKnowledgeProposalGenerator
    {
        private readonly KnowledgeProposalDraft _draft;

        public DirectProposalGenerator(string claim,
            IReadOnlyList<OperationArtifactReference> support,
            IReadOnlyList<OperationArtifactReference> contradictions)
            => _draft = new KnowledgeProposalDraft(claim, KnowledgeHypothesisType.Generalization,
                support, contradictions, .5, ["Model authority and consensus are not inferred."],
                new KnowledgeValidityInterval(DateTimeOffset.UtcNow, null),
                new[] { KnowledgeCapability.Read }.ToHashSet());

        public ValueTask<KnowledgeProposalDraft> GenerateAsync(
            TeacherRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_draft);
    }

    private sealed class CurrentEvidenceVerifier : ILearningVerifier
    {
        private readonly CognitiveIndex _index;
        private readonly string _tenant;
        private readonly string _namespace;
        private readonly IReadOnlyList<SourceSnapshot> _sources;

        public CurrentEvidenceVerifier(CognitiveIndex index, string tenant, string @namespace,
            IReadOnlyList<SourceSnapshot> sources)
            => (_index, _tenant, _namespace, _sources) = (index, tenant, @namespace, sources);

        public VerifierIdentity Identity { get; } =
            new("engram.current-evidence", "1", VerifierKind.Deterministic);

        public ValueTask<(VerificationStatus Status, IReadOnlyList<VerificationFinding> Findings)> VerifyAsync(
            KnowledgeProposal proposal, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool stable = _sources.Count > 0 && _sources.All(source =>
            {
                var current = _index.Get(source.Entry.Id, _namespace, tenantId: _tenant);
                return current is not null && EntryHash(current) == source.ContentHash;
            });
            IReadOnlyList<VerificationFinding> findings = stable
                ? Array.Empty<VerificationFinding>()
                : [new VerificationFinding("evidence-changed", "Source evidence changed before verification.")];
            return ValueTask.FromResult((stable ? VerificationStatus.Passed : VerificationStatus.Failed, findings));
        }
    }
}
