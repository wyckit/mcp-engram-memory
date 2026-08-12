using System.Collections.ObjectModel;
using McpEngramMemory.Core.Models.Knowledge;

namespace McpEngramMemory.Core.Models.Planning;

/// <summary>
/// Immutable identity and upper bounds for one agent. A profile is an authorization ceiling,
/// not proof that any particular artifact is currently authorized.
/// </summary>
public sealed class AgentProfile
{
    public string ProfileId { get; }
    public string Version { get; }
    public string TenantId { get; }
    public string PrincipalId { get; }
    public string Purpose { get; }
    public IReadOnlyList<ArtifactCapability> Capabilities { get; }
    public PermissionEnvelope Permissions { get; }
    public IReadOnlyList<string> AvailableSourceIds { get; }
    public int MaximumRetrievalItems { get; }
    public ContextBudget MaximumContextBudget { get; }

    public AgentProfile(
        string profileId,
        string version,
        string tenantId,
        string principalId,
        string purpose,
        IEnumerable<ArtifactCapability> capabilities,
        PermissionEnvelope permissions,
        IEnumerable<string> availableSourceIds,
        int maximumRetrievalItems,
        ContextBudget maximumContextBudget)
    {
        ProfileId = Required(profileId, nameof(profileId));
        Version = Required(version, nameof(version));
        TenantId = Required(tenantId, nameof(tenantId));
        PrincipalId = Required(principalId, nameof(principalId));
        Purpose = Required(purpose, nameof(purpose));
        ArgumentNullException.ThrowIfNull(capabilities);
        Capabilities = new ReadOnlyCollection<ArtifactCapability>(capabilities
            .Distinct()
            .OrderBy(value => value)
            .ToArray());
        Permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        AvailableSourceIds = ReadOnlyIds(availableSourceIds, nameof(availableSourceIds));
        if (maximumRetrievalItems < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRetrievalItems));
        MaximumRetrievalItems = maximumRetrievalItems;
        MaximumContextBudget = maximumContextBudget
            ?? throw new ArgumentNullException(nameof(maximumContextBudget));
    }

    internal static IReadOnlyList<string> ReadOnlyIds(IEnumerable<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new ReadOnlyCollection<string>(values
            .Select(value => Required(value, parameterName))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray());
    }

    internal static string Required(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty.", parameterName)
            : value.Trim();
}

/// <summary>
/// A versioned selection from a profile's existing authority. Every field is interpreted as a
/// restriction; a loadout cannot introduce capabilities, grants, sources, or larger budgets.
/// </summary>
public sealed class AgentLoadout
{
    public string LoadoutId { get; }
    public string Version { get; }
    public IReadOnlyList<ArtifactCapability> Capabilities { get; }
    public PermissionEnvelope Permissions { get; }
    public IReadOnlyList<string> EnabledSourceIds { get; }
    public int MaximumRetrievalItems { get; }
    public ContextBudget MaximumContextBudget { get; }

    public AgentLoadout(
        string loadoutId,
        string version,
        IEnumerable<ArtifactCapability> capabilities,
        PermissionEnvelope permissions,
        IEnumerable<string> enabledSourceIds,
        int maximumRetrievalItems,
        ContextBudget maximumContextBudget)
    {
        LoadoutId = AgentProfile.Required(loadoutId, nameof(loadoutId));
        Version = AgentProfile.Required(version, nameof(version));
        ArgumentNullException.ThrowIfNull(capabilities);
        Capabilities = new ReadOnlyCollection<ArtifactCapability>(capabilities
            .Distinct()
            .OrderBy(value => value)
            .ToArray());
        Permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        EnabledSourceIds = AgentProfile.ReadOnlyIds(enabledSourceIds, nameof(enabledSourceIds));
        if (maximumRetrievalItems < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRetrievalItems));
        MaximumRetrievalItems = maximumRetrievalItems;
        MaximumContextBudget = maximumContextBudget
            ?? throw new ArgumentNullException(nameof(maximumContextBudget));
    }
}

/// <summary>The effective, narrowed authorization scope produced by binding a loadout.</summary>
public sealed class ScopedAgentProfile
{
    public AgentProfile Profile { get; }
    public AgentLoadout Loadout { get; }
    public string TenantId => Profile.TenantId;
    public string PrincipalId => Profile.PrincipalId;
    public string Purpose => Profile.Purpose;
    public IReadOnlyList<ArtifactCapability> Capabilities => Loadout.Capabilities;
    public PermissionEnvelope Permissions => Loadout.Permissions;
    public IReadOnlyList<string> EnabledSourceIds => Loadout.EnabledSourceIds;
    public int MaximumRetrievalItems => Loadout.MaximumRetrievalItems;
    public ContextBudget MaximumContextBudget => Loadout.MaximumContextBudget;

    internal ScopedAgentProfile(AgentProfile profile, AgentLoadout loadout)
    {
        Profile = profile;
        Loadout = loadout;
    }

    /// <summary>
    /// Checks the local loadout ceiling only. Artifact-specific authorization must still be
    /// evaluated at the point of retrieval or disclosure.
    /// </summary>
    public bool Allows(ArtifactCapability capability)
        => Capabilities.Contains(capability) && Permissions.Allows(capability, PrincipalId);
}
