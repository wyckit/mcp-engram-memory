using System.Reflection;
using McpEngramMemory.Tools;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tests;

/// <summary>
/// Guards the documented tool surface (README, docs/mcp-tools-reference.md,
/// SECURITY.md, banner) against silent drift: counts the actual
/// <see cref="McpServerToolAttribute"/>-decorated methods per profile grouping
/// (mirroring the <c>MEMORY_TOOL_PROFILE</c> wiring in Program.cs) and asserts
/// the totals the docs claim. If this test fails, a tool was added or removed —
/// update the documented counts (62 total; minimal 17 / standard 39 / full 62)
/// in the same change.
/// </summary>
public class ToolSurfaceCountTests
{
    private static readonly Type[] MinimalToolClasses =
    {
        typeof(CoreMemoryTools),
        typeof(AdminTools),
        typeof(CompositeTools),
        typeof(MultiAgentTools),
    };

    private static readonly Type[] StandardAdditions =
    {
        typeof(GraphTools),
        typeof(ClusterTools),
        typeof(LifecycleTools),
        typeof(IntelligenceTools),
        typeof(MemoryDiffusionTools),
        typeof(SpectralRetrievalTools),
    };

    private static readonly Type[] FullAdditions =
    {
        typeof(AccretionTools),
        typeof(BenchmarkTools),
        typeof(MrcrBenchmarkTools),
        typeof(DebateTools),
        typeof(MaintenanceTools),
        typeof(ExpertTools),
        typeof(SynthesisTools),
        typeof(VisualizationTools),
    };

    private static int CountTools(IEnumerable<Type> toolClasses) =>
        toolClasses
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Count(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    [Theory]
    [InlineData("minimal", 17)]
    [InlineData("standard", 39)]
    [InlineData("full", 62)]
    public void Profile_exposes_documented_tool_count(string profile, int expectedCount)
    {
        var classes = new List<Type>(MinimalToolClasses);
        if (profile is "standard" or "full")
            classes.AddRange(StandardAdditions);
        if (profile is "full")
            classes.AddRange(FullAdditions);

        Assert.Equal(expectedCount, CountTools(classes));
    }

    [Fact]
    public void Every_tool_class_in_assembly_is_assigned_to_a_profile()
    {
        var assigned = MinimalToolClasses.Concat(StandardAdditions).Concat(FullAdditions).ToHashSet();

        var allToolClasses = typeof(CoreMemoryTools).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .ToList();

        Assert.NotEmpty(allToolClasses);
        var unassigned = allToolClasses.Where(t => !assigned.Contains(t)).ToList();
        Assert.True(unassigned.Count == 0,
            $"Tool classes not wired to any MEMORY_TOOL_PROFILE grouping (update Program.cs, this test, and the documented counts): {string.Join(", ", unassigned.Select(t => t.Name))}");
    }
}
