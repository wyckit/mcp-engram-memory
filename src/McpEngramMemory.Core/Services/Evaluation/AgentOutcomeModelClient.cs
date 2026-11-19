using System.Net;
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
/// Default provider factory. Supports Ollama (local) and three subscription-driven CLIs —
/// Claude Code (`claude -p`), OpenAI Codex (`codex exec`), and Google Gemini (`gemini -p`).
/// Each CLI variant consumes the user's vendor subscription instead of an API key.
/// </summary>
public sealed class AgentOutcomeModelClientFactory : IAgentOutcomeModelClientFactory
{
    internal const string AllowCustomEndpointEnvVar = "ENGRAM_ALLOW_CUSTOM_BENCHMARK_ENDPOINT";

    public IAgentOutcomeModelClient Create(string provider, string? endpoint = null)
    {
        var normalized = provider?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "ollama" => new OllamaAgentOutcomeModelClient(
                ValidateOllamaEndpoint(endpoint)),
            "claude-cli" or "claude_cli" or "claudecli" => new ClaudeCliModelClient(
                executable: ValidateCliEndpoint(provider!, endpoint, ClaudeCliModelClient.DefaultExecutable)),
            "codex-cli" or "codex_cli" or "codexcli" => new CodexCliModelClient(
                executable: ValidateCliEndpoint(provider!, endpoint, CodexCliModelClient.DefaultExecutable)),
            "gemini-cli" or "gemini_cli" or "geminicli" => new GeminiCliModelClient(
                executable: ValidateCliEndpoint(provider!, endpoint, GeminiCliModelClient.DefaultExecutable)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported live benchmark provider. Supported providers: ollama, claude-cli, codex-cli, gemini-cli.")
        };
    }

    internal static bool IsCustomEndpointAllowed()
    {
        var raw = Environment.GetEnvironmentVariable(AllowCustomEndpointEnvVar);
        return string.Equals(raw?.Trim(), "1", StringComparison.Ordinal)
            || string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ValidateOllamaEndpoint(string? endpoint)
    {
        var configuredOllamaUrl = Environment.GetEnvironmentVariable("OLLAMA_URL");
        string effective = string.IsNullOrWhiteSpace(endpoint)
            ? (string.IsNullOrWhiteSpace(configuredOllamaUrl) ? "http://localhost:11434" : configuredOllamaUrl.Trim())
            : endpoint.Trim();

        if (!Uri.TryCreate(effective, UriKind.Absolute, out var uri))
            throw new ArgumentException("Invalid Ollama endpoint URL.", nameof(endpoint));

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Ollama endpoint must use http or https.", nameof(endpoint));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Ollama endpoint must not contain userinfo credentials.", nameof(endpoint));

        if (!IsCustomEndpointAllowed() &&
            !IsLoopbackHost(uri) &&
            !MatchesConfiguredOllamaHost(uri, configuredOllamaUrl))
        {
            throw new ArgumentException(
                $"Ollama endpoint host '{uri.IdnHost}' is not permitted. Only loopback hosts (localhost, 127.0.0.1, ::1) or the operator-configured OLLAMA_URL host are allowed unless {AllowCustomEndpointEnvVar}=1 is set.",
                nameof(endpoint));
        }

        return effective;
    }

    internal static string? ValidateCliEndpoint(string provider, string? endpoint, string canonicalExecutable)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        var trimmed = endpoint.Trim();
        if (trimmed.Any(char.IsControl))
            throw new ArgumentException("Endpoint must not contain control characters.", nameof(endpoint));

        if (IsCustomEndpointAllowed())
            return trimmed;

        if (string.Equals(trimmed, canonicalExecutable, StringComparison.OrdinalIgnoreCase))
            return canonicalExecutable;

        throw new ArgumentException(
            $"Custom CLI executable endpoint is not permitted for provider '{provider}'. Only the canonical '{canonicalExecutable}' CLI is allowed unless {AllowCustomEndpointEnvVar}=1 is set.",
            nameof(endpoint));
    }

    private static bool IsLoopbackHost(Uri uri)
    {
        string host = uri.IdnHost;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();
            return IPAddress.IsLoopback(ip);
        }

        return false;
    }

    private static bool MatchesConfiguredOllamaHost(Uri candidate, string? configuredOllamaUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredOllamaUrl))
            return false;

        if (!Uri.TryCreate(configuredOllamaUrl.Trim(), UriKind.Absolute, out var configuredUri))
            return false;

        if (!string.Equals(configuredUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(configuredUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(candidate.IdnHost, configuredUri.IdnHost, StringComparison.OrdinalIgnoreCase);
    }
}
