using System.Reflection;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpEngramMemory.Tests;

/// <summary>
/// Guards the MCP SDK boundary independently of a network transport. These tests exercise
/// the same registration and reflection-based schema generation used by Program.cs while
/// remaining deterministic on every supported test target framework.
/// </summary>
public class McpSdkConformanceTests
{
    [Fact]
    public void Runtime_uses_the_pinned_2_1_sdk()
    {
        var informationalVersion = typeof(McpServerTool).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        Assert.StartsWith("2.1.0", informationalVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void Sdk_negotiates_the_current_protocol_and_retains_legacy_versions()
    {
        var options = new McpServerOptions();

        // An unpinned server negotiates both the discovery-based 2026 revision and
        // initialize-handshake clients supported by the 2.1 SDK.
        Assert.Null(options.ProtocolVersion);

        options.ProtocolVersion = "2026-07-28";
        Assert.Equal("2026-07-28", options.ProtocolVersion);

        options.ProtocolVersion = "2024-11-05";
        Assert.Equal("2024-11-05", options.ProtocolVersion);
    }

    [Fact]
    public void Stdio_registration_discovers_the_existing_composite_tool_contract()
    {
        var services = new ServiceCollection();
        services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<CompositeTools>();

        using var provider = services.BuildServiceProvider();
        var tools = provider.GetServices<McpServerTool>().ToArray();

        Assert.Equal(4, tools.Length);
        Assert.Equal(
            ["get_context_block", "recall", "reflect", "remember"],
            tools.Select(tool => tool.ProtocolTool.Name).Order().ToArray());
    }

    [Fact]
    public void Full_profile_host_registration_activates_namespace_guarded_tools()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mcp-full-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var persistence = new PersistenceManager(root, debounceMs: 50);
            using var index = new CognitiveIndex(persistence);
            var embedding = new HashEmbeddingService();
            var services = new ServiceCollection();
            services.AddSingleton(index);
            services.AddSingleton<IEmbeddingService>(embedding);
            services.AddSingleton<ExpertDispatcher>();
            services.AddSingleton<MetricsCollector>();
            services.AddSingleton<NamespaceRegistry>();
            services.AddSingleton<IPrincipalContext>(PrincipalContext.LegacyUnisolated);
            services.AddSingleton(AgentIdentity.Default);

            // Mirror Program.cs: both principal representations are registered, so the guard must
            // be constructed explicitly instead of leaving the container to choose an overload.
            services.AddSingleton(sp => new NamespaceAccess(
                sp.GetRequiredService<NamespaceRegistry>(),
                sp.GetRequiredService<IPrincipalContext>()));
            services.AddMcpServer().WithTools<ExpertTools>();

            using var provider = services.BuildServiceProvider();
            var guardedTool = ActivatorUtilities.CreateInstance<ExpertTools>(provider);
            var domainTree = Assert.IsType<DomainTreeResult>(guardedTool.GetDomainTree());

            Assert.Empty(domainTree.Roots);
            Assert.Equal(0, domainTree.TotalNodes);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Remember_schema_preserves_required_and_optional_wire_arguments()
    {
        var remember = CreateTool(nameof(CompositeTools.Remember));
        var schema = remember.ProtocolTool.InputSchema;

        Assert.Equal("remember", remember.ProtocolTool.Name);
        Assert.Equal("object", schema.GetProperty("type").GetString());

        var properties = schema.GetProperty("properties");
        Assert.Equal(
            ["category", "id", "lifecycleState", "metadata", "ns", "text"],
            properties.EnumerateObject().Select(property => property.Name).Order().ToArray());

        var required = schema.GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(new HashSet<string>(["id", "ns", "text"], StringComparer.Ordinal), required);
        Assert.True(SchemaAllowsType(properties.GetProperty("metadata"), "object"));

        // Existing tools intentionally retain the legacy content response contract until
        // individual result DTOs opt into structured content in a separate compatibility change.
        Assert.Null(remember.ProtocolTool.OutputSchema);
    }

    [Fact]
    public void Every_tool_explicitly_advertises_all_safety_annotations()
    {
        var methods = GetToolMethods();
        Assert.Equal(63, methods.Count);

        foreach (var method in methods)
        {
            var attributeData = method.CustomAttributes.Single(data =>
                data.AttributeType == typeof(McpServerToolAttribute));
            var explicitNames = attributeData.NamedArguments
                .Select(argument => argument.MemberName)
                .ToHashSet(StringComparer.Ordinal);

            Assert.True(
                new[] { "ReadOnly", "Destructive", "Idempotent", "OpenWorld" }.All(explicitNames.Contains),
                $"Tool method {method.DeclaringType!.Name}.{method.Name} must explicitly declare every MCP safety annotation.");

            var attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;
            var protocolTool = CreateTool(method).ProtocolTool;
            var annotations = protocolTool.Annotations;

            Assert.NotNull(annotations);
            Assert.Equal<bool?>(attribute.ReadOnly, annotations.ReadOnlyHint);
            Assert.Equal<bool?>(attribute.Destructive, annotations.DestructiveHint);
            Assert.Equal<bool?>(attribute.Idempotent, annotations.IdempotentHint);
            Assert.Equal<bool?>(attribute.OpenWorld, annotations.OpenWorldHint);
        }
    }

    [Fact]
    public void Representative_tools_advertise_conservative_safety_classifications()
    {
        var tools = GetToolMethods()
            .Select(CreateTool)
            .ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.Annotations!);

        AssertAnnotations(tools["get_memory"], readOnly: true, destructive: false, idempotent: true, openWorld: false);
        AssertAnnotations(tools["remember"], readOnly: false, destructive: true, idempotent: false, openWorld: false);
        AssertAnnotations(tools["delete_memory"], readOnly: false, destructive: true, idempotent: true, openWorld: false);
        AssertAnnotations(tools["recall"], readOnly: false, destructive: false, idempotent: false, openWorld: false);
        AssertAnnotations(tools["compare_live_agent_outcome_artifacts"], readOnly: true, destructive: false, idempotent: true, openWorld: false);
        AssertAnnotations(tools["run_live_agent_outcome_benchmark"], readOnly: false, destructive: true, idempotent: false, openWorld: true);
        AssertAnnotations(tools["synthesize_memories"], readOnly: true, destructive: false, idempotent: true, openWorld: true);
        AssertAnnotations(tools["share_namespace"], readOnly: false, destructive: true, idempotent: true, openWorld: false);
    }

    private static McpServerTool CreateTool(string methodName)
    {
        var method = typeof(CompositeTools).GetMethod(methodName,
            BindingFlags.Public | BindingFlags.Instance)!;

        return McpServerTool.Create(
            method,
            _ => throw new InvalidOperationException("Schema-only tool should not be invoked."),
            new McpServerToolCreateOptions());
    }

    private static McpServerTool CreateTool(MethodInfo method) =>
        McpServerTool.Create(
            method,
            _ => throw new InvalidOperationException("Metadata-only tool should not be invoked."),
            new McpServerToolCreateOptions());

    private static IReadOnlyList<MethodInfo> GetToolMethods() =>
        typeof(CoreMemoryTools).Assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .OrderBy(method => method.DeclaringType!.Name)
            .ThenBy(method => method.Name)
            .ToArray();

    private static void AssertAnnotations(
        ToolAnnotations actual,
        bool readOnly,
        bool destructive,
        bool idempotent,
        bool openWorld)
    {
        Assert.Equal<bool?>(readOnly, actual.ReadOnlyHint);
        Assert.Equal<bool?>(destructive, actual.DestructiveHint);
        Assert.Equal<bool?>(idempotent, actual.IdempotentHint);
        Assert.Equal<bool?>(openWorld, actual.OpenWorldHint);
    }

    private static bool SchemaAllowsType(System.Text.Json.JsonElement schema, string expectedType)
    {
        var type = schema.GetProperty("type");
        return type.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => type.GetString() == expectedType,
            System.Text.Json.JsonValueKind.Array => type.EnumerateArray()
                .Any(item => item.GetString() == expectedType),
            _ => false,
        };
    }
}
