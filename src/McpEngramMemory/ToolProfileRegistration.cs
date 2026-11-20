using McpEngramMemory.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace McpEngramMemory;

/// <summary>Shared by the stdio host and tool-surface regression tests.</summary>
internal static class ToolProfileRegistration
{
    /// <summary>
    /// Normalize <c>MEMORY_TOOL_PROFILE</c> to one of <c>minimal</c>, <c>standard</c>, or <c>full</c>.
    /// Null/empty silently defaults to <c>minimal</c>; unrecognized non-empty values emit a diagnostic
    /// warning and fall back safely to <c>minimal</c> so typos never silently confuse operators.
    /// </summary>
    internal static string ResolveProfile(string? rawProfile, out string? warning)
    {
        if (string.IsNullOrWhiteSpace(rawProfile))
        {
            warning = null;
            return "minimal";
        }

        var normalized = rawProfile.Trim().ToLowerInvariant();
        if (normalized is "minimal" or "standard" or "full")
        {
            warning = null;
            return normalized;
        }

        warning = $"Warning: Unrecognized MEMORY_TOOL_PROFILE '{rawProfile.Trim()}'. Valid values are 'minimal', 'standard', or 'full'. Falling back to 'minimal'.";
        return "minimal";
    }

    internal static IMcpServerBuilder WithMemoryToolProfile(this IMcpServerBuilder builder, string? profile, TextWriter? warningWriter = null)
    {
        var resolved = ResolveProfile(profile, out var warning);
        if (warning is not null)
            (warningWriter ?? Console.Error).WriteLine(warning);

        // Minimal: core memory operations + admin + composite tier-1 tools.
        builder
            .WithTools<CoreMemoryTools>()
            .WithTools<AdminTools>()
            .WithTools<CompositeTools>()
            .WithTools<MultiAgentTools>();

        if (resolved is "standard" or "full")
        {
            builder
                .WithTools<GraphTools>()
                .WithTools<ClusterTools>()
                .WithTools<LifecycleTools>()
                .WithTools<IntelligenceTools>()
                .WithTools<MemoryDiffusionTools>()
                .WithTools<SpectralRetrievalTools>();
        }

        if (resolved is "full")
        {
            builder
                .WithTools<AccretionTools>()
                .WithTools<BenchmarkTools>()
                .WithTools<MrcrBenchmarkTools>()
                .WithTools<DebateTools>()
                .WithTools<MaintenanceTools>()
                .WithTools<ExpertTools>()
                .WithTools<GovernedLearningTools>()
                .WithTools<SynthesisTools>()
                .WithTools<VisualizationTools>();
        }

        return builder;
    }
}
