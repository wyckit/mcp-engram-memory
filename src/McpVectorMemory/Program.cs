using McpVectorMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Persistence: defaults to ~/.local/share/mcp-vector-memory/index.json.
// Set VECTOR_MEMORY_DATA_PATH to override, or "none" to disable persistence.
string? dataPath = Environment.GetEnvironmentVariable("VECTOR_MEMORY_DATA_PATH");
if (dataPath is null)
{
    dataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "mcp-vector-memory",
        "index.json");
}
else if (dataPath.Equals("none", StringComparison.OrdinalIgnoreCase) || dataPath == "")
{
    dataPath = null;
}

// Use a factory so the DI container owns the lifetime and calls Dispose on shutdown.
string? capturedDataPath = dataPath;
builder.Services.AddSingleton(_ => new VectorIndex(capturedDataPath));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<VectorMemoryTools>();

await builder.Build().RunAsync();
