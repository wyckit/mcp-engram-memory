using McpEngramMemory.Core.Models.Constitution;

namespace McpEngramMemory.Core.Services.Constitution;

/// <summary>Validates immutable hashes and composes a monotone root-to-overlay policy chain.</summary>
public static class ConstitutionComposer
{
    public static ConstitutionBundle Compose(
        ConstitutionVersion root,
        IEnumerable<ConstitutionVersion>? overlays = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ValidatePublishedHash(root);
        if (root.Definition.LayerKind != ConstitutionLayerKind.Root)
            throw new ConstitutionCompositionException("The first Constitution version must be a root.");
        ValidateRootInvariants(root.Definition.Constraints);

        var overlayList = (overlays ?? []).ToArray();
        var allRules = root.Definition.Rules.ToList();
        var seenRuleIds = new HashSet<string>(allRules.Select(rule => rule.RuleId), StringComparer.Ordinal);
        var parent = root;
        var effectiveConstraints = root.Definition.Constraints;

        foreach (var overlay in overlayList)
        {
            ValidatePublishedHash(overlay);
            if (overlay.Definition.LayerKind != ConstitutionLayerKind.Overlay)
                throw new ConstitutionCompositionException("Only overlays may follow the root Constitution.");
            if (!string.Equals(
                    overlay.Definition.ParentVersionHash,
                    parent.ContentHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ConstitutionCompositionException(
                    $"Overlay '{overlay.Definition.ConstitutionId}' does not target the current parent hash.");
            }

            EnsureDoesNotWeaken(effectiveConstraints, overlay.Definition.Constraints);
            foreach (var rule in overlay.Definition.Rules)
            {
                if (!seenRuleIds.Add(rule.RuleId))
                    throw new ConstitutionCompositionException(
                        $"Overlay rule '{rule.RuleId}' attempts to replace an inherited rule.");
                allRules.Add(rule);
            }

            effectiveConstraints = overlay.Definition.Constraints;
            parent = overlay;
        }

        return new ConstitutionBundle(
            root,
            overlayList,
            allRules.OrderBy(rule => rule.Priority).ThenBy(rule => rule.RuleId, StringComparer.Ordinal),
            effectiveConstraints);
    }

    private static void ValidatePublishedHash(ConstitutionVersion version)
    {
        string actual = ConstitutionCanonicalizer.ComputeHash(version);
        if (!string.Equals(actual, version.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new ConstitutionCompositionException(
                $"Constitution version '{version.Version}' does not match its canonical content hash.");
    }

    private static void ValidateRootInvariants(ConstitutionConstraints constraints)
    {
        if (!constraints.PreserveProvenance ||
            !constraints.RequireEvidenceForKnowledge ||
            !constraints.PreserveContradictions ||
            !constraints.RequireDeterministicVerificationFirst ||
            !constraints.RequireExplainability ||
            !constraints.RequireAudit ||
            constraints.MinimumEvidenceCount < 1)
        {
            throw new ConstitutionCompositionException(
                "A root Constitution must retain provenance, evidence, contradictions, deterministic-first " +
                "verification, explainability, audit, and an evidence floor of at least one.");
        }
    }

    private static void EnsureDoesNotWeaken(
        ConstitutionConstraints parent,
        ConstitutionConstraints child)
    {
        RejectTrueToFalse(parent.PreserveProvenance, child.PreserveProvenance, nameof(child.PreserveProvenance));
        RejectTrueToFalse(parent.RequireEvidenceForKnowledge, child.RequireEvidenceForKnowledge, nameof(child.RequireEvidenceForKnowledge));
        RejectTrueToFalse(parent.PreserveContradictions, child.PreserveContradictions, nameof(child.PreserveContradictions));
        RejectTrueToFalse(parent.RequireDeterministicVerificationFirst, child.RequireDeterministicVerificationFirst, nameof(child.RequireDeterministicVerificationFirst));
        RejectTrueToFalse(parent.RequireExplainability, child.RequireExplainability, nameof(child.RequireExplainability));
        RejectTrueToFalse(parent.RequireAudit, child.RequireAudit, nameof(child.RequireAudit));

        if (child.MinimumEvidenceCount < parent.MinimumEvidenceCount)
            throw new ConstitutionCompositionException("An overlay cannot lower the minimum evidence count.");

        var parentAllowed = parent.AllowedOperations.ToHashSet();
        if (child.AllowedOperations.Any(operation => !parentAllowed.Contains(operation)))
            throw new ConstitutionCompositionException("An overlay cannot add an operation forbidden by its parent.");
    }

    private static void RejectTrueToFalse(bool parent, bool child, string constraintName)
    {
        if (parent && !child)
            throw new ConstitutionCompositionException(
                $"Overlay constraint '{constraintName}' weakens its parent.");
    }
}

public sealed class ConstitutionCompositionException : InvalidOperationException
{
    public ConstitutionCompositionException(string message) : base(message) { }
}
