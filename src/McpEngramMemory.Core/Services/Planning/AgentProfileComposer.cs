using McpEngramMemory.Core.Models.Planning;
using McpEngramMemory.Core.Services.Knowledge;

namespace McpEngramMemory.Core.Services.Planning;

/// <summary>Validates and binds loadouts without permitting authority amplification.</summary>
public static class AgentProfileComposer
{
    public static ScopedAgentProfile Compose(AgentProfile profile, AgentLoadout loadout)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(loadout);

        var profileCapabilities = profile.Capabilities.ToHashSet();
        if (loadout.Capabilities.Any(capability => !profileCapabilities.Contains(capability)))
            throw new InvalidOperationException("A loadout cannot add capabilities absent from its profile.");

        if (!PermissionEnvelopeService.IsNarrowerThanOrEqual(loadout.Permissions, profile.Permissions))
            throw new InvalidOperationException("A loadout cannot broaden its profile's permission envelope.");

        var availableSources = profile.AvailableSourceIds.ToHashSet(StringComparer.Ordinal);
        if (loadout.EnabledSourceIds.Any(source => !availableSources.Contains(source)))
            throw new InvalidOperationException("A loadout cannot enable sources absent from its profile.");

        if (loadout.MaximumRetrievalItems > profile.MaximumRetrievalItems)
            throw new InvalidOperationException("A loadout cannot increase its profile's retrieval limit.");

        if (!loadout.MaximumContextBudget.IsWithin(profile.MaximumContextBudget))
            throw new InvalidOperationException("A loadout cannot increase its profile's context budget.");

        return new ScopedAgentProfile(profile, loadout);
    }
}
