using McpEngramMemory.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace McpEngramMemory;

/// <summary>Shared by the stdio host and tool-surface regression tests.</summary>
internal static class ToolProfileRegistration
{
    internal static IMcpServerBuilder WithMemoryToolProfile(this IMcpServerBuilder builder, string profile)
    {
        // Minimal: core memory operations + admin + composite tier-1 tools.
        builder
            .WithTools<CoreMemoryTools>()
            .WithTools<AdminTools>()
            .WithTools<CompositeTools>()
            .WithTools<MultiAgentTools>();

        if (profile is "standard" or "full")
        {
            builder
                .WithTools<GraphTools>()
                .WithTools<ClusterTools>()
                .WithTools<LifecycleTools>()
                .WithTools<IntelligenceTools>()
                .WithTools<MemoryDiffusionTools>()
                .WithTools<SpectralRetrievalTools>();
        }

        if (profile is "full")
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
