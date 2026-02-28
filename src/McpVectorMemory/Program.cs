using McpVectorMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<VectorIndex>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<VectorMemoryTools>();

await builder.Build().RunAsync();
