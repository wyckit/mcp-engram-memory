using McpEngramMemory.Core.Services.Synthesis;

namespace McpEngramMemory.Core.Services.Evaluation;

/// <summary>
/// Minimal text-generation client contract for live agent-outcome benchmarks.
/// </summary>
public interface IAgentOutcomeModelClient : IDisposable
{
    Task<bool> IsAvailableAsync(string model, CancellationToken ct = default);

    Task<string?> GenerateAsync(
        string model,
        string prompt,
        int maxTokens = 320,
        float temperature = 0.1f,
        CancellationToken ct = default);
}

/// <summary>
/// Factory for benchmark generation clients.
/// </summary>
public interface IAgentOutcomeModelClientFactory
{
    IAgentOutcomeModelClient Create(string provider, string? endpoint = null);
}

/// <summary>
/// Ollama-backed client adapter for live benchmark generation.
/// </summary>
public sealed class OllamaAgentOutcomeModelClient : IAgentOutcomeModelClient
{
    private readonly OllamaClient _client;

    public OllamaAgentOutcomeModelClient(string? baseUrl = null)
    {
        _client = new OllamaClient(baseUrl ?? "http://localhost:11434");
    }

    public Task<bool> IsAvailableAsync(string model, CancellationToken ct = default)
        => _client.IsAvailableAsync(model, ct);

    public Task<string?> GenerateAsync(
        string model,
        string prompt,
        int maxTokens = 320,
        float temperature = 0.1f,
        CancellationToken ct = default)
        => _client.GenerateAsync(model, prompt, maxTokens, temperature, ct);

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// Default provider factory. Supports Ollama (local) and the Claude Code CLI
/// (`claude -p`) so benchmarks can run against the user's Claude subscription.
/// </summary>
public sealed class AgentOutcomeModelClientFactory : IAgentOutcomeModelClientFactory
{
    public IAgentOutcomeModelClient Create(string provider, string? endpoint = null)
    {
        return provider.Trim().ToLowerInvariant() switch
        {
            "ollama" => new OllamaAgentOutcomeModelClient(
                endpoint ?? Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434"),
            "claude-cli" or "claude_cli" or "claudecli" => new ClaudeCliModelClient(
                executable: endpoint),
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported live benchmark provider. Supported providers: ollama, claude-cli.")
        };
    }
}
