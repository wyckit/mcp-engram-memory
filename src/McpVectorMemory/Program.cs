using McpVectorMemory.Core.Services;
using McpVectorMemory.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Services
builder.Services.AddSingleton(sp => new PersistenceManager(
    logger: sp.GetService<ILogger<PersistenceManager>>()));
builder.Services.AddSingleton<IStorageProvider>(sp => sp.GetRequiredService<PersistenceManager>());
builder.Services.AddSingleton<CognitiveIndex>();
builder.Services.AddSingleton<KnowledgeGraph>();
builder.Services.AddSingleton<ClusterManager>();
builder.Services.AddSingleton<LifecycleEngine>();
builder.Services.AddSingleton<PhysicsEngine>();
builder.Services.AddSingleton<AccretionScanner>();
builder.Services.AddSingleton<MetricsCollector>();
builder.Services.AddSingleton<BenchmarkRunner>();
builder.Services.AddSingleton<DebateSessionManager>();
builder.Services.AddSingleton<ExpertDispatcher>();
builder.Services.AddSingleton<OnnxEmbeddingService>();
builder.Services.AddSingleton<IEmbeddingService>(sp => sp.GetRequiredService<OnnxEmbeddingService>());
builder.Services.AddHostedService<EmbeddingWarmupService>();
builder.Services.AddHostedService<DecayBackgroundService>();
builder.Services.AddHostedService<AccretionBackgroundService>();

// MCP Server with all tool groups
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<CoreMemoryTools>()
    .WithTools<GraphTools>()
    .WithTools<ClusterTools>()
    .WithTools<LifecycleTools>()
    .WithTools<AdminTools>()
    .WithTools<AccretionTools>()
    .WithTools<BenchmarkTools>()
    .WithTools<IntelligenceTools>()
    .WithTools<DebateTools>()
    .WithTools<MaintenanceTools>()
    .WithTools<ExpertTools>();

await builder.Build().RunAsync();
