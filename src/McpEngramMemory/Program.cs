using McpEngramMemory;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Services;
using McpEngramMemory.Core.Services.Evaluation;
using McpEngramMemory.Core.Services.Experts;
using McpEngramMemory.Core.Services.Graph;
using McpEngramMemory.Core.Services.Intelligence;
using McpEngramMemory.Core.Services.Lifecycle;
using McpEngramMemory.Core.Services.Retrieval;
using McpEngramMemory.Core.Services.Sharing;
using McpEngramMemory.Core.Services.Storage;
using McpEngramMemory.Core.Services.Synthesis;
using McpEngramMemory.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Subcommand: `dotnet run -- mrcr-pilot ...` runs the MRCR benchmark without starting the MCP server.
if (args.Length > 0 && args[0] == "mrcr-pilot")
{
    return await MrcrPilotCommand.RunAsync(args.Skip(1).ToArray());
}

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Storage provider — set MEMORY_STORAGE=sqlite to use SQLite backend
var storageMode = Environment.GetEnvironmentVariable("MEMORY_STORAGE");
if (string.Equals(storageMode, "sqlite", StringComparison.OrdinalIgnoreCase))
{
    var dbPath = Environment.GetEnvironmentVariable("MEMORY_SQLITE_PATH");
    builder.Services.AddSingleton(sp => new SqliteStorageProvider(
        dbPath: dbPath, logger: sp.GetService<ILogger<SqliteStorageProvider>>()));
    builder.Services.AddSingleton<IStorageProvider>(sp => sp.GetRequiredService<SqliteStorageProvider>());
}
else
{
    builder.Services.AddSingleton(sp => new PersistenceManager(
        logger: sp.GetService<ILogger<PersistenceManager>>()));
    builder.Services.AddSingleton<IStorageProvider>(sp => sp.GetRequiredService<PersistenceManager>());
}
// Memory limits — configurable via environment variables
var limits = new MemoryLimitsConfig(
    MaxNamespaceSize: int.TryParse(Environment.GetEnvironmentVariable("MEMORY_MAX_NAMESPACE_SIZE"), out var maxNs) ? maxNs : int.MaxValue,
    MaxTotalCount: int.TryParse(Environment.GetEnvironmentVariable("MEMORY_MAX_TOTAL_COUNT"), out var maxTotal) ? maxTotal : int.MaxValue);
builder.Services.AddSingleton(limits);
builder.Services.AddSingleton<CognitiveIndex>();
builder.Services.AddSingleton<KnowledgeGraph>();
builder.Services.AddSingleton<MemoryDiffusionKernel>();
builder.Services.AddSingleton<ClusterManager>();
builder.Services.AddSingleton<LifecycleEngine>();
builder.Services.AddSingleton<PhysicsEngine>();
builder.Services.AddSingleton<AccretionScanner>();
builder.Services.AddSingleton<MetricsCollector>();
builder.Services.AddSingleton<QueryExpander>();
builder.Services.AddSingleton<BenchmarkRunner>();
builder.Services.AddSingleton<AgentOutcomeBenchmarkRunner>();
builder.Services.AddSingleton<LiveAgentOutcomeBenchmarkRunner>();
builder.Services.AddSingleton<IAgentOutcomeModelClientFactory, AgentOutcomeModelClientFactory>();
builder.Services.AddSingleton<MrcrScorer>();
builder.Services.AddSingleton<MrcrBenchmarkRunner>();
builder.Services.AddSingleton<DebateSessionManager>();
builder.Services.AddSingleton<ExpertDispatcher>();
builder.Services.AddSingleton<SpreadingActivationService>();

// SLM synthesis engine (Ollama-powered map-reduce)
var ollamaUrl = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434";
var synthesisMapModel = Environment.GetEnvironmentVariable("SYNTHESIS_MAP_MODEL") ?? "qwen2.5:0.5b";
var synthesisReduceModel = Environment.GetEnvironmentVariable("SYNTHESIS_REDUCE_MODEL") ?? "qwen2.5:0.5b";
builder.Services.AddSingleton(sp => new SynthesisEngine(
    sp.GetRequiredService<CognitiveIndex>(),
    sp.GetRequiredService<ClusterManager>(),
    synthesisMapModel, synthesisReduceModel, ollamaUrl));

// Agent identity for multi-agent memory sharing (set AGENT_ID env var per agent)
var agentId = Environment.GetEnvironmentVariable("AGENT_ID") ?? AgentIdentity.DefaultAgentId;
builder.Services.AddSingleton(new AgentIdentity(agentId));
builder.Services.AddSingleton<NamespaceRegistry>();
builder.Services.AddSingleton<OnnxEmbeddingService>();
builder.Services.AddSingleton<IEmbeddingService>(sp => sp.GetRequiredService<OnnxEmbeddingService>());
builder.Services.AddHostedService<EmbeddingWarmupService>();
builder.Services.AddHostedService<DecayBackgroundService>();
builder.Services.AddHostedService<AccretionBackgroundService>();
builder.Services.AddHostedService<DiffusionKernelWarmupService>();
builder.Services.AddHostedService<ConsolidationBackgroundService>();

// Tool profiles — control how many tools are exposed via MEMORY_TOOL_PROFILE env var:
//   "minimal"  → 16 tools: core CRUD + admin + composite + multi-agent
//   "standard" → 35 tools: minimal + graph, lifecycle, clusters, intelligence
//   "full"     → 55 tools: everything (default for backward compatibility)
var toolProfile = Environment.GetEnvironmentVariable("MEMORY_TOOL_PROFILE")?.ToLowerInvariant() ?? "full";

var mcpBuilder = builder.Services
    .AddMcpServer()
    .WithStdioServerTransport();

// Minimal: core memory operations + admin + composite tier-1 tools
mcpBuilder
    .WithTools<CoreMemoryTools>()
    .WithTools<AdminTools>()
    .WithTools<CompositeTools>()
    .WithTools<MultiAgentTools>();

if (toolProfile is "standard" or "full")
{
    mcpBuilder
        .WithTools<GraphTools>()
        .WithTools<ClusterTools>()
        .WithTools<LifecycleTools>()
        .WithTools<IntelligenceTools>()
        .WithTools<MemoryDiffusionTools>();
}

if (toolProfile is "full")
{
    mcpBuilder
        .WithTools<AccretionTools>()
        .WithTools<BenchmarkTools>()
        .WithTools<MrcrBenchmarkTools>()
        .WithTools<DebateTools>()
        .WithTools<MaintenanceTools>()
        .WithTools<ExpertTools>()
        .WithTools<SynthesisTools>()
        .WithTools<VisualizationTools>();
}

await builder.Build().RunAsync();
return 0;

